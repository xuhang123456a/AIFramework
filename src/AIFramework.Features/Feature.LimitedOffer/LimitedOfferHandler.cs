#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.LimitedOffer
{
    /// <summary>
    /// 表驱动限时礼包机：唯一允许裁定"这个礼包此刻能不能买、实付多少、今天还剩几次"的地方。
    /// <para>
    /// <b>只做四件事</b>：按 <c>[start, end)</c> 判定窗口 → 按 <c>discountPercent</c> 折算实付价 →
    /// 按 <c>dailyLimit</c> / <c>totalLimit</c> 裁决限购 → 成交时发 <see cref="GrantItemCommand"/>
    /// 并广播 <see cref="LimitedOfferPurchasedEvent"/>。它不查余额、不扣货币、不画 UI、不落存档。
    /// </para>
    /// <para>
    /// <b>为什么不做余额判定</b>：货币属于专属经济系统（另一个切片/服务端），
    /// 本切片若自己查背包就会形成跨切片强耦合（AIF001 的变体）。因此成交判定只覆盖
    /// "窗口 / 限购 / 参数"三类，扣款由装配层在 <see cref="Purchase"/> 返回
    /// <see cref="LimitedOfferPurchaseResult.Success"/> 之后、按 <see cref="LimitedOfferPurchaseOutcome.FinalPrice"/>
    /// 桥接执行（详见 <c>CONTRACT.md</c> §4.6）。
    /// </para>
    /// <para>
    /// <b>时间策略</b>：所有时间只经构造注入的 <see cref="ITimeProvider"/>（AIF003），
    /// 秒数为 <c>UtcNow</c> 折算的 UTC Unix 秒；"今天"用 <c>now / 86400</c> 向下取整的 UTC 日序号，
    /// 与玩家本地时区、夏令时无关。
    /// </para>
    /// <para>
    /// <b>状态归属</b>：限购计数只存在于本对象内存（不落盘）。生产上必须由存档/服务端持有权威计数，
    /// 本切片只做客户端侧防误触：进程重启后计数归零，真正的限购以服务端为准。
    /// </para>
    /// </summary>
    public sealed class LimitedOfferHandler
        : ICommandHandler<LimitedOfferPurchaseCommand>,
          IQueryHandler<LimitedOfferQueryRequest, LimitedOfferSnapshot>
    {
        /// <summary>Unix 纪元（1970-01-01T00:00:00Z）：只用于把 <see cref="ITimeProvider.UtcNow"/> 折成秒。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>一天的秒数：UTC 自然日序号与每日限购重置都按它切。</summary>
        private const long SecondsPerDay = 86400L;

        /// <summary>百分数刻度：折扣按 <c>percent / 100</c> 折算。</summary>
        private const int PercentScale = 100;

        /// <summary>实付价下界：再怎么打折也不会出现 0 元购（0 元礼包会变成无限刷）。</summary>
        private const int MinFinalPrice = 1;

        /// <summary>发货原因标签（因果日志用，取值与既有切片不冲突）。</summary>
        private const int GrantReasonTagId = 6901;

        /// <summary>"今天还没买过"的日序号水位：与任何真实日序号（≥ 0）都不相等。</summary>
        private const long NoPurchasedDay = long.MinValue;

        private readonly ICqeBus _bus;
        private readonly ITimeProvider _time;
        private readonly LimitedOfferTables _tables;

        /// <summary>与子表行对齐的"当日已购次数"（跨日后按需清零，见 <see cref="Purchase"/>）。</summary>
        private readonly int[] _dailyPurchased;

        /// <summary>与子表行对齐的"<see cref="_dailyPurchased"/> 属于哪个 UTC 日序号"。</summary>
        private readonly long[] _dailyPurchasedDay;

        /// <summary>与子表行对齐的"累计已购次数"（本对象生命周期内，不落盘）。</summary>
        private readonly int[] _totalPurchased;

        /// <summary>加载子表并初始化限购计数；构造里不注册总线、不发事件、不读时钟。</summary>
        /// <param name="bus">切片消息总线（只用于发货指令与成交事实）。</param>
        /// <param name="time">可快进时间源：窗口判定与跨日判定都读它。</param>
        /// <param name="loader">子表加载端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/>、<paramref name="time"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">限时礼包子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">限时礼包子表数据非法。</exception>
        public LimitedOfferHandler(ICqeBus bus, ITimeProvider time, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _time = time;
            _tables = new LimitedOfferTables(loader);

            int count = _tables.Count;
            _dailyPurchased = new int[count];
            _dailyPurchasedDay = new long[count];
            _totalPurchased = new int[count];
            for (int i = 0; i < count; i++)
            {
                _dailyPurchasedDay[i] = NoPurchasedDay;
            }
        }

        /// <summary>子表行数（礼包条数）；0 表示本拍没有可售礼包（购买统一返回 <c>Unavailable</c>）。</summary>
        public int Count
        {
            get { return _tables.Count; }
        }

        /// <summary>
        /// 注册购买命令与全量查询处理器。
        /// 装配入口必须传入构造本对象时的同一个总线实例（发货与广播用的都是构造注入的总线）。
        /// </summary>
        /// <param name="bus">切片消息总线。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<LimitedOfferPurchaseCommand>(this);
            bus.RegisterQueryHandler<LimitedOfferQueryRequest, LimitedOfferSnapshot>(this);
        }

        /// <summary>
        /// CQE 入口：购买限时礼包。返回 <c>void</c>（命令契约），逐次结果请直接用 <see cref="Purchase"/>，
        /// 或从快照的剩余次数反推。
        /// </summary>
        /// <param name="command">购买命令。</param>
        public void Handle(in LimitedOfferPurchaseCommand command)
        {
            Purchase(command.OfferId);
        }

        /// <summary>
        /// 同步购买入口：返回逐次结果，供适配层/Presenter 直接展示失败原因。
        /// <para>
        /// 判定顺序固定为：空表 → 未知礼包 → 时间窗口 → 每日限购 → 总限购；任一失败即返回，
        /// **不产生任何副作用**（不发指令、不发事件、计数不变）。
        /// </para>
        /// <para>
        /// 成功时顺序固定为：先落定限购计数 → 再发 <see cref="GrantItemCommand"/>（发货）→
        /// 最后广播 <see cref="LimitedOfferPurchasedEvent"/>：订阅方在事件回调里查询快照拿到的是成交后口径，
        /// 且"计数已加、货没发"这类中间态不会被外部观察到。
        /// </para>
        /// </summary>
        /// <param name="offerId">要购买的礼包 id。</param>
        /// <returns>本次尝试的结果；失败时带人读原因。</returns>
        public LimitedOfferPurchaseOutcome Purchase(int offerId)
        {
            if (_tables.Count == 0)
            {
                // 整表 0 行 = 运营侧没有任何可售礼包，与"这个 id 不存在"是两类问题，结果码分开。
                return new LimitedOfferPurchaseOutcome(
                    LimitedOfferPurchaseResult.Unavailable,
                    offerId,
                    0,
                    0,
                    0,
                    "限时礼包子表为空，本拍没有任何可售礼包。");
            }

            int index;
            LimitedOfferDef def;
            if (!TryFindOffer(offerId, out index, out def))
            {
                return new LimitedOfferPurchaseOutcome(
                    LimitedOfferPurchaseResult.UnknownOffer,
                    offerId,
                    0,
                    0,
                    0,
                    "礼包 id " + offerId + " 不在 TbLimitedOffer 中。");
            }

            long now = CurrentUnixSeconds();
            int finalPrice = ComputeFinalPrice(def.OriginalPrice, def.DiscountPercent);

            if (!IsWithinWindow(def, now))
            {
                return new LimitedOfferPurchaseOutcome(
                    LimitedOfferPurchaseResult.NotOpen,
                    offerId,
                    def.ItemId,
                    def.GrantCount,
                    finalPrice,
                    "礼包 " + offerId + " 不在可购买窗口内（左闭右开 [start, end)）：now=" + now
                    + "，start=" + def.StartUnixSeconds + "，end=" + def.EndUnixSeconds + "。");
            }

            long dayIndex = FloorDivide(now, SecondsPerDay);
            if (_dailyPurchasedDay[index] != dayIndex)
            {
                // 惰性跨日重置：只在该行被真正访问时清零，避免每拍扫全表。
                _dailyPurchasedDay[index] = dayIndex;
                _dailyPurchased[index] = 0;
            }

            if (def.DailyLimit > 0 && _dailyPurchased[index] >= def.DailyLimit)
            {
                return new LimitedOfferPurchaseOutcome(
                    LimitedOfferPurchaseResult.LimitReached,
                    offerId,
                    def.ItemId,
                    def.GrantCount,
                    finalPrice,
                    "礼包 " + offerId + " 今日限购已用尽（dailyLimit=" + def.DailyLimit
                    + "，已购 " + _dailyPurchased[index] + "）。");
            }

            if (_totalPurchased[index] >= def.TotalLimit)
            {
                // totalLimit = 0 是"一次都不能买"：这里天然命中，不需要额外特判。
                return new LimitedOfferPurchaseOutcome(
                    LimitedOfferPurchaseResult.LimitReached,
                    offerId,
                    def.ItemId,
                    def.GrantCount,
                    finalPrice,
                    "礼包 " + offerId + " 总限购已用尽（totalLimit=" + def.TotalLimit
                    + "，已购 " + _totalPurchased[index] + "）。");
            }

            _dailyPurchased[index]++;
            _totalPurchased[index]++;

            // 顺序契约：先记账，再发货，最后广播事实。
            // 反序会让"事件已发、货还在路上"的撕裂态出现在因果日志里，无法回放定位。
            _bus.Send(new GrantItemCommand(def.ItemId, def.GrantCount, GrantReasonTagId));
            _bus.Publish(new LimitedOfferPurchasedEvent(def.OfferId, def.ItemId, def.GrantCount, finalPrice));

            return new LimitedOfferPurchaseOutcome(
                LimitedOfferPurchaseResult.Success,
                def.OfferId,
                def.ItemId,
                def.GrantCount,
                finalPrice,
                string.Empty);
        }

        /// <summary>
        /// 返回限时礼包全量只读快照：窗口、报价、剩余次数全部按**查询时刻**折算，
        /// <see cref="LimitedOfferSnapshot.NowUnixSeconds"/> 取注入的时间源。
        /// <para>
        /// 查询**只读**：不会推进任何跨日水位，也不会改写限购计数；
        /// 跨日后未发生购买时，快照里的 <c>DailyRemaining</c> 直接按 0 已购折算回满。
        /// </para>
        /// <para>空表返回空数组、<c>OpenCount = 0</c>，不崩也不抛。</para>
        /// </summary>
        /// <param name="query">全量查询请求（无参数）。</param>
        /// <returns>按配置表行顺序排列的快照。</returns>
        public LimitedOfferSnapshot Handle(in LimitedOfferQueryRequest query)
        {
            long now = CurrentUnixSeconds();
            long dayIndex = FloorDivide(now, SecondsPerDay);
            int count = _tables.Count;
            LimitedOfferState[] offers = new LimitedOfferState[count];
            int openCount = 0;

            for (int i = 0; i < count; i++)
            {
                LimitedOfferDef def = _tables.Get(i);
                bool isOpen = IsWithinWindow(def, now);
                if (isOpen)
                {
                    openCount++;
                }

                // 查询不改写 _dailyPurchased：只要水位不是今天，就按"今天已购 0"折算。
                int purchasedToday = _dailyPurchasedDay[i] == dayIndex ? _dailyPurchased[i] : 0;
                int dailyRemaining = def.DailyLimit <= 0
                    ? LimitedOfferState.UnlimitedRemaining
                    : ClampToZero(def.DailyLimit - purchasedToday);

                offers[i] = new LimitedOfferState(
                    def.OfferId,
                    def.Name,
                    isOpen,
                    ComputeFinalPrice(def.OriginalPrice, def.DiscountPercent),
                    def.OriginalPrice,
                    def.DiscountPercent,
                    dailyRemaining,
                    ClampToZero(def.TotalLimit - _totalPurchased[i]),
                    isOpen ? RemainingSeconds(def, now) : 0L);
            }

            return new LimitedOfferSnapshot(offers, openCount, now);
        }

        /// <summary>
        /// 实付价：<c>floor(originalPrice * discountPercent / 100)</c>，并夹到至少 <see cref="MinFinalPrice"/>。
        /// <para>
        /// 用 <see cref="long"/> 做中间量，避免 <c>originalPrice * discountPercent</c> 在 int 上溢出；
        /// 正数除法天然向下取整，所以不需要额外的 floor 处理。
        /// </para>
        /// </summary>
        /// <param name="originalPrice">原价（必须为正）。</param>
        /// <param name="discountPercent">实付百分比（1..100）。</param>
        /// <returns>实付价（≥ 1，且 ≤ 原价）。</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="originalPrice"/> 非正，或 <paramref name="discountPercent"/> 不在 1..100。</exception>
        public static int ComputeFinalPrice(int originalPrice, int discountPercent)
        {
            if (originalPrice <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(originalPrice), originalPrice, "原价必须为正。");
            }

            if (discountPercent < 1 || discountPercent > PercentScale)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(discountPercent),
                    discountPercent,
                    "实付百分比必须在 1.." + PercentScale + "。");
            }

            long price = ((long)originalPrice * discountPercent) / PercentScale;
            if (price < MinFinalPrice)
            {
                return MinFinalPrice;
            }

            return price > int.MaxValue ? int.MaxValue : (int)price;
        }

        /// <summary>左闭右开窗口判定：<c>start &lt;= now &lt; end</c>（用 long 比较，避免 int 减法溢出）。</summary>
        private static bool IsWithinWindow(LimitedOfferDef def, long now)
        {
            return now >= def.StartUnixSeconds && now < def.EndUnixSeconds;
        }

        /// <summary>按 <c>offerId</c> 线性查找（子表已保证唯一）；找不到返回 <c>false</c>。</summary>
        private bool TryFindOffer(int offerId, out int index, out LimitedOfferDef def)
        {
            for (int i = 0; i < _tables.Count; i++)
            {
                LimitedOfferDef candidate = _tables.Get(i);
                if (candidate.OfferId == offerId)
                {
                    index = i;
                    def = candidate;
                    return true;
                }
            }

            index = -1;
            def = default;
            return false;
        }

        /// <summary>负数夹到 0：剩余次数不允许为负值（"已超额"只可能来自存档回滚，不该展示给玩家）。</summary>
        private static int ClampToZero(int value)
        {
            return value < 0 ? 0 : value;
        }

        /// <summary>距关闭剩余秒：<c>end - now</c> 夹到 ≥ 0（调用方已保证窗口内，这里只做防御）。</summary>
        private static long RemainingSeconds(LimitedOfferDef def, long now)
        {
            long remaining = def.EndUnixSeconds - now;
            return remaining < 0L ? 0L : remaining;
        }

        /// <summary>当前 UTC 时刻折成 Unix 秒；<see cref="ITimeProvider.UtcNow"/> 按 UTC 语义解释（Local 先转 UTC）。</summary>
        private long CurrentUnixSeconds()
        {
            DateTime utcNow = _time.UtcNow;
            if (utcNow.Kind == DateTimeKind.Local)
            {
                utcNow = utcNow.ToUniversalTime();
            }
            else if (utcNow.Kind != DateTimeKind.Utc)
            {
                // Unspecified 一律按 UTC 解释：逻辑层不猜测本机时区（AIF003）。
                utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            }

            return (long)Math.Floor((utcNow - UnixEpoch).TotalSeconds);
        }

        /// <summary>向下取整的整数除法：Unix 秒在 1970 年之前为负时也能得到正确的日序号。</summary>
        private static long FloorDivide(long value, long divisor)
        {
            long quotient = value / divisor;
            if ((value % divisor != 0) && ((value < 0) != (divisor < 0)))
            {
                quotient--;
            }

            return quotient;
        }
    }
}
