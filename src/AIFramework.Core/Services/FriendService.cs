#nullable enable
using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 社交大脑：把 <see cref="ISocialService"/>（好友/赠送/领取后端）包成"带每日额度、可跨日、
    /// 可降级"的业务端口，让切片与 UI 只需要问三件事——今天还能送几次、还能领几个、这次操作的结果是什么。
    /// <para>
    /// <b>为什么这段逻辑必须在 Core 而不是某个适配器里</b>：每日赠送总量上限、跨 UTC 自然日重置、
    /// 不可用降级这三条规则在 Console 与 Unity、以及未来的真实社交 SDK 上必须<b>逐字一致</b>。
    /// 写进任一适配器，另一端就会漂移出"只在真机上多送一次/少领一次"的差异；而这类差异直接对应经济产出。
    /// Core 是唯一能被两个装配入口共用、又能被无头单测逐条钉死的位置
    /// （与 <see cref="AccountSessionManager"/>、<see cref="StorePurchaseCoordinator"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>两层账，各自权威</b>：
    /// <list type="bullet">
    /// <item><b>本地额度</b>（本类）：<see cref="_maxGiftPerDay"/> 次/UTC 自然日。它是防连点与体验保护，
    /// 用 <see cref="ITimeProvider"/> 的 UTC 日序号结算，<b>不读系统时钟</b>（逻辑层铁律）。</item>
    /// <item><b>服务端事实</b>（<see cref="ISocialService"/>）：每人每天能否被送/被领。它才是权威，
    /// 本类只如实透传 <see cref="FriendGiftResult.AlreadyGifted"/> / <see cref="FriendGiftResult.NotFriend"/> /
    /// <see cref="FriendGiftResult.Cooldown"/>，绝不把服务端拒绝"改写"成成功。</item>
    /// </list>
    /// 客户端额度可以被改本地时间绕过，所以它<b>只用于省网络调用与保护 UI</b>；
    /// 真正的"每人每天一次"必须由服务端裁决（见 <see cref="ISocialService"/> 文档）。
    /// </para>
    /// <para>
    /// <b>跨日口径</b>：日序号 = <c>(UtcNow.Date - 1970-01-01 UTC).Days</c>，与
    /// <see cref="AdShowScheduler"/> / 邮件天序号同一套约定。用 UTC 而不是本地时区，
    /// 是为了"同一账号换时区不会多刷一组额度"。<b>同一天内多次 <see cref="Refresh"/> 不会重复重置</b>：
    /// 只有日序号真的变了才归零（见 <see cref="SettleDay"/>）。
    /// </para>
    /// <para>
    /// <b>跨日结算的触发点</b>：<see cref="Refresh"/> 与三个操作入口都会先结算一次，
    /// 因此玩家跨日后直接点"赠送"也能拿到新一天的额度，不需要先手动刷新页面。
    /// 反过来，<see cref="DailyGiftRemaining"/> / <see cref="DailyClaimRemaining"/> 两个读数
    /// <b>没有副作用</b>：它们按"日序号是否变了"直接算，属性读取永远不会改写状态
    /// （与 <see cref="AdShowScheduler"/> 的"不做拦截式记账"同一原则）。
    /// </para>
    /// <para>
    /// <b><see cref="DailyClaimRemaining"/> 的口径是"今天还能从几个好友那里领"</b>，不是本地额度：
    /// 领取的上限天然由服务端"每人每天 1 次"决定，本类不额外发明一个领取配额
    /// （构造函数里也没有 <c>maxClaimPerDay</c>，见契约）。它读的是最近一次
    /// <see cref="Refresh"/> / 领取成功后重建的好友快照；跨日后先按"全员可领"乐观填充，
    /// 再由下一次 <see cref="Refresh"/> 用服务端快照纠正。这个取舍的理由：午夜后把读数停在 0
    /// 会让整个领取入口看起来坏掉，而乐观读数的代价只是"点一下发现已领过"（结果码如实透传）。
    /// </para>
    /// <para>
    /// <b>不可用降级</b>：<see cref="ISocialService.IsAvailable"/> 为 <c>false</c>（或读取时抛异常）时，
    /// 所有操作返回 <see cref="FriendGiftResult.Unavailable"/>、<see cref="ClaimAllStamina"/> 返回 0、
    /// <see cref="DailyClaimRemaining"/> 为 0，<b>不抛异常</b>。社交是外围系统，断网不该拦住玩家进游戏。
    /// </para>
    /// <para>
    /// <b>参数错误优先于可用性判定</b>：好友 id 为 <c>null</c>/空/空白一律抛
    /// <see cref="ArgumentException"/>，<b>即使后端当时不可用</b>。理由是这两类错误的性质不同——
    /// 空 id 是调用方的编程错误（必须立刻响，否则会被静默"降级"成一次看不出问题的失败），
    /// 而"后端不可用"是对一次合法调用的业务回答。把编程错误混进 <see cref="FriendGiftResult.NotFriend"/>
    /// 会让真实事故（UI 传了空 id）在日志里跟"对方删了我"一模一样。
    /// </para>
    /// <para>
    /// <b>限定与不做的部分</b>：本类不缓存好友列表（列表由 UI 直接读 <see cref="Social"/> 的
    /// <see cref="ISocialService.GetFriends"/>，避免两份列表漂移），只管每日额度与"可领数"这一个派生读数；
    /// 也不做请求排队/重试——真实 SDK 的异步与重试属于适配层。
    /// </para>
    /// <para>线程模型：<b>非线程安全</b>。所有调用（刷新、赠送、领取）必须串行在主线程；本类不加锁。</para>
    /// </summary>
    public sealed class FriendService
    {
        /// <summary>默认每日赠送上限（20 次，与多数二游的"好友体力"设计对齐）。</summary>
        public const int DefaultMaxGiftPerDay = 20;

        /// <summary>失败原因：后端不可用（未接线 / 断网 / 实现违约）。</summary>
        public const string ReasonUnavailable = "social service unavailable";

        /// <summary>失败原因：本地每日赠送额度已用尽（跨 UTC 日恢复）。</summary>
        public const string ReasonDailyGiftLimit = "daily gift limit reached";

        /// <summary>参数错误原因：好友 id 为空（<see cref="ArgumentException.Message"/> 用）。</summary>
        public const string ReasonEmptyFriendId = "empty friend id";

        /// <summary>失败原因前缀：后端实现违约抛异常（后接异常类型名，不含堆栈）。</summary>
        public const string ReasonServiceThrew = "social service threw";

        /// <summary>天序号纪元（UTC），与 <see cref="AdShowScheduler"/> 同一口径。</summary>
        private static readonly DateTime DayEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>社交后端；构造后不可变。</summary>
        private readonly ISocialService _social;

        /// <summary>时间源（只用于 UTC 自然日判定；构造后不可变）。</summary>
        private readonly ITimeProvider _time;

        /// <summary>每日赠送上限（负值已在构造期夹到 0，即当天全禁）。</summary>
        private readonly int _maxGiftPerDay;

        /// <summary>最近一次结算所属的 UTC 天序号（构造期先取当天，避免首次刷新被误判为跨日）。</summary>
        private int _dayIndex;

        /// <summary>当天已成功赠送的次数（只记<b>成功</b>：服务端拒绝不消耗本地额度）。</summary>
        private int _giftsToday;

        /// <summary>当天快照里还能领取的好友数（跨日后按 <see cref="_friendCount"/> 乐观填充）。</summary>
        private int _claimableCount;

        /// <summary>最近一次快照里的好友总数（跨日后"全员可领"的乐观口径依据）。</summary>
        private int _friendCount;

        /// <summary>创建社交大脑。</summary>
        /// <param name="social">社交后端（非 <c>null</c>）。</param>
        /// <param name="time">时间源（非 <c>null</c>；跨日必须经由此端口，逻辑层禁止直接读系统时钟）。</param>
        /// <param name="maxGiftPerDay">
        /// 每日赠送上限（默认 <see cref="DefaultMaxGiftPerDay"/>）。
        /// <para>
        /// <b>负值按 0 处理（fail-closed）</b>：配额为负是配置事故，按 0 处理意味着"当天不能送"，
        /// 而不是"无限送"。宁可少一个功能，也不让配置错误变成一个可以无限刷体力的漏洞
        /// （与 <see cref="AdShowScheduler"/> 的负配额口径一致）。
        /// </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="social"/> 或 <paramref name="time"/> 为 <c>null</c>（装配期编程错误，必须立刻响）。
        /// </exception>
        public FriendService(ISocialService social, ITimeProvider time, int maxGiftPerDay = DefaultMaxGiftPerDay)
        {
            _social = social ?? throw new ArgumentNullException(nameof(social));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _maxGiftPerDay = maxGiftPerDay < 0 ? 0 : maxGiftPerDay;
            _dayIndex = SafeTodayIndex();
        }

        /// <summary>
        /// 社交后端（只读暴露）：列表页直接读 <see cref="ISocialService.GetFriends"/>，
        /// 本类<b>不复制</b>一份好友列表，避免"UI 看到的"和"服务端认的"两份状态漂移。
        /// </summary>
        public ISocialService Social
        {
            get { return _social; }
        }

        /// <summary>今天还能赠送几次（UTC 自然日重置；<b>不返回负数</b>；读取无副作用）。</summary>
        public int DailyGiftRemaining
        {
            get
            {
                int used = _dayIndex == SafeTodayIndex() ? _giftsToday : 0;
                int remaining = _maxGiftPerDay - used;
                return remaining > 0 ? remaining : 0;
            }
        }

        /// <summary>
        /// 今天还能从几个好友那里领体力（快照口径，见类型注释）。
        /// <para>后端不可用时为 0（与 <c>GetFriends</c> 返回空数组一致），<b>不抛异常</b>。</para>
        /// </summary>
        public int DailyClaimRemaining
        {
            get
            {
                if (!SafeIsAvailable())
                {
                    return 0;
                }

                // 跨日且尚未重新拉列表：按"全员可领"乐观填充（服务端跨日重置），
                // 下一次 Refresh 会用真实快照纠正。理由见类型注释。
                if (_dayIndex != SafeTodayIndex())
                {
                    return _friendCount;
                }

                return _claimableCount;
            }
        }

        /// <summary>
        /// 刷新列表并结算跨日重置（UTC 日序号变化时重置每日计数）。
        /// <para>
        /// <b>顺序固定为"先结算本地、再拉后端、最后重建快照"</b>：后端不可用或抛异常时，
        /// 本地额度依然要跨日恢复（否则断网过夜后玩家第二天也送不了）、且不能把刷新失败抛给调用方。
        /// 不可用时不触达后端，直接把好友快照清成空（与 <see cref="ISocialService.GetFriends"/> 的空数组口径一致）。
        /// </para>
        /// <para>同一天内重复调用是幂等的：不会重复重置额度，只是再拉一次列表。</para>
        /// </summary>
        public void Refresh()
        {
            SettleDay();

            if (!SafeIsAvailable())
            {
                // 离线：列表不可用，口径统一为 0（与"GetFriends 返回空数组"一致），
                // 也不去触达一个已经声明不可用的后端。
                _friendCount = 0;
                _claimableCount = 0;
                return;
            }

            try
            {
                _social.Refresh();
            }
            catch (Exception)
            {
                // 契约要求不抛；实现违约时按"这次没刷到"处理，本地快照与额度照常结算，不打断调用方。
            }

            RebuildSnapshot();
        }

        /// <summary>
        /// 给好友送体力（本地每日总量 ≤ <see cref="_maxGiftPerDay"/>，且每人每天 1 次由服务端裁决）。
        /// <para>
        /// <b>额度在调后端之前判</b>：超额直接返回 <see cref="FriendGiftResult.Cooldown"/> 且<b>不触达后端</b>
        /// ——连点十次不该换来十次网络请求。
        /// </para>
        /// <para>
        /// <b>只有成功才扣额度</b>：服务端回 <see cref="FriendGiftResult.AlreadyGifted"/> /
        /// <see cref="FriendGiftResult.NotFriend"/> / <see cref="FriendGiftResult.Cooldown"/> 一律如实透传，
        /// 且<b>不消耗</b>本地次数（否则被拒绝一次就永久少一次，玩家会看到"明明没送出去却少了额度"）。
        /// </para>
        /// </summary>
        /// <param name="friendId">好友的玩家 id（<c>null</c>/空/空白 → <see cref="ArgumentException"/>）。</param>
        /// <returns>操作结果（<b>永不抛异常</b>，参数错误除外）。</returns>
        /// <exception cref="ArgumentException"><paramref name="friendId"/> 为 <c>null</c>、空或空白。</exception>
        public FriendOperationOutcome GiftStamina(string friendId)
        {
            RequireFriendId(friendId);
            SettleDay();

            if (!SafeIsAvailable())
            {
                return Failure(FriendGiftResult.Unavailable, ReasonUnavailable);
            }

            if (DailyGiftRemaining <= 0)
            {
                return Failure(FriendGiftResult.Cooldown, ReasonDailyGiftLimit);
            }

            FriendOperationOutcome outcome;
            try
            {
                outcome = _social.GiftStamina(friendId);
            }
            catch (Exception ex)
            {
                // 规则：后端抛异常 → Unavailable，原因带异常类型名、不带堆栈（与账号端口同一口径）。
                return Failure(FriendGiftResult.Unavailable, ReasonServiceThrew + ": " + ex.GetType().Name);
            }

            if (outcome.Success)
            {
                _giftsToday++;
            }

            // 赠送不改变"还能领几个"，因此不重建快照（省一次列表拉取）。
            return outcome;
        }

        /// <summary>
        /// 一键领取全部可领体力。
        /// <para>
        /// <b>返回 0 是合法结果</b>（今天已经领完 / 没有好友 / 后端不可用），调用方不得把 0 当错误弹窗；
        /// 真实失败在社交系统里没有"扣钱"风险，静默为 0 比抛异常更合适。
        /// </para>
        /// <para>
        /// <b>后端抛异常与返回负数都收敛成 0</b>：本方法签名只能回 <c>int</c>，没有地方放失败原因，
        /// 因此绝不能把异常漏出去（那会打断一次普通的"一键领取"点击）。
        /// </para>
        /// </summary>
        /// <returns>本次领取总量（≥ 0；<b>永不抛异常</b>）。</returns>
        public int ClaimAllStamina()
        {
            SettleDay();

            if (!SafeIsAvailable())
            {
                return 0;
            }

            int total;
            try
            {
                total = _social.ClaimAllStamina();
            }
            catch (Exception)
            {
                return 0;
            }

            if (total < 0)
            {
                total = 0;
            }

            // 领取会改变"还能领几个"：立刻按服务端快照纠正读数（否则一键领取后按钮还亮着）。
            RebuildSnapshot();
            return total;
        }

        /// <summary>
        /// 领取单个好友的体力（每人每天 1 次由服务端裁决；本地不设领取额度）。
        /// <para>
        /// 服务端的 <see cref="FriendGiftResult.AlreadyGifted"/>（= 今天已领过）/
        /// <see cref="FriendGiftResult.NotFriend"/> / <see cref="FriendGiftResult.Cooldown"/> 均如实透传；
        /// 只有成功后才重建"可领数"读数。
        /// </para>
        /// </summary>
        /// <param name="friendId">好友的玩家 id（<c>null</c>/空/空白 → <see cref="ArgumentException"/>）。</param>
        /// <returns>操作结果（<b>永不抛异常</b>，参数错误除外）。</returns>
        /// <exception cref="ArgumentException"><paramref name="friendId"/> 为 <c>null</c>、空或空白。</exception>
        public FriendOperationOutcome ClaimStamina(string friendId)
        {
            RequireFriendId(friendId);
            SettleDay();

            if (!SafeIsAvailable())
            {
                return Failure(FriendGiftResult.Unavailable, ReasonUnavailable);
            }

            FriendOperationOutcome outcome;
            try
            {
                outcome = _social.ClaimStamina(friendId);
            }
            catch (Exception ex)
            {
                return Failure(FriendGiftResult.Unavailable, ReasonServiceThrew + ": " + ex.GetType().Name);
            }

            if (outcome.Success)
            {
                RebuildSnapshot();
            }

            return outcome;
        }

        /// <summary>
        /// UTC 时间 → UTC 自然日序号（自 1970-01-01 UTC 起的整天数）。
        /// <para>
        /// 这是"跨日"的<b>唯一口径</b>，Console/Unity 假后端与真实适配层都应复用它，避免三处各写一份
        /// 天序号导致"客户端说已跨日、服务端说没有"。
        /// </para>
        /// <para>
        /// 取 <c>.Date</c> 而不是 <c>Ticks / TicksPerDay</c>：前者对任意 <see cref="DateTimeKind"/>
        /// 都按"日期"截断，后者会把 1970 年前的负时刻算错一天。
        /// <b>本方法不做时区换算</b>：调用方必须传 <see cref="ITimeProvider.UtcNow"/>
        /// （传本地时间会得到本地的"日"，与 UTC 口径不符）。
        /// </para>
        /// </summary>
        /// <param name="utcNow">UTC 当前时间。</param>
        /// <returns>UTC 自然日序号。</returns>
        public static int ToUtcDayIndex(DateTime utcNow)
        {
            return (int)(utcNow.Date - DayEpoch).TotalDays;
        }

        /// <summary>
        /// 若已跨日则把当日赠送计数归零、把可领数按"全员可领"乐观填充。
        /// <para>只由 <see cref="Refresh"/> 与三个操作入口调用：读取类属性不需要它（它们自己按日序号算）。</para>
        /// </summary>
        private void SettleDay()
        {
            int today = SafeTodayIndex();
            if (_dayIndex == today)
            {
                return;
            }

            _dayIndex = today;
            _giftsToday = 0;
            _claimableCount = _friendCount;
        }

        /// <summary>
        /// 按后端当前列表重建"可领数"读数。
        /// <para>
        /// 不可用时清成 0（与"GetFriends 返回空数组"一致）；实现违约抛异常时<b>保留上一次快照</b>
        /// ——把一次异常当成"没有好友"会让 UI 误报空列表，比留旧值更糟。
        /// </para>
        /// </summary>
        private void RebuildSnapshot()
        {
            if (!SafeIsAvailable())
            {
                _friendCount = 0;
                _claimableCount = 0;
                return;
            }

            FriendInfo[]? friends;
            try
            {
                friends = _social.GetFriends();
            }
            catch (Exception)
            {
                return;
            }

            if (friends == null)
            {
                // 违约实现（契约要求永不返回 null）：按空列表处理，但绝不让 null 流到调用方。
                friends = Array.Empty<FriendInfo>();
            }

            int claimable = 0;
            for (int i = 0; i < friends.Length; i++)
            {
                if (friends[i].CanClaim)
                {
                    claimable++;
                }
            }

            _friendCount = friends.Length;
            _claimableCount = claimable;
        }

        /// <summary>读后端可用性；实现违约抛异常时按"不可用"处理（降级总比崩溃好）。</summary>
        /// <returns>后端是否可用。</returns>
        private bool SafeIsAvailable()
        {
            try
            {
                return _social.IsAvailable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 今天的 UTC 日序号；时间源违约（抛异常）时返回<b>上一次结算的日序号</b>。
        /// <para>
        /// 这个兜底方向是刻意的：日序号不变 ⇒ 当日额度不会被重置 ⇒ 时间源坏掉时宁可少送，
        /// 也不因为一次异常把配额刷满（fail-closed）。
        /// </para>
        /// </summary>
        /// <returns>UTC 日序号。</returns>
        private int SafeTodayIndex()
        {
            try
            {
                return ToUtcDayIndex(_time.UtcNow);
            }
            catch (Exception)
            {
                return _dayIndex;
            }
        }

        /// <summary>好友 id 校验：空/空白属于调用方编程错误，必须立刻响（见类型注释的参数错误优先级）。</summary>
        /// <param name="friendId">待校验的好友 id。</param>
        /// <exception cref="ArgumentException">为 <c>null</c>、空或空白。</exception>
        private static void RequireFriendId(string friendId)
        {
            if (string.IsNullOrWhiteSpace(friendId))
            {
                throw new ArgumentException(ReasonEmptyFriendId, nameof(friendId));
            }
        }

        /// <summary>构造一个失败结果（数量为 0）。</summary>
        /// <param name="result">失败结果码。</param>
        /// <param name="reason">诊断原因。</param>
        /// <returns>失败结果。</returns>
        private static FriendOperationOutcome Failure(FriendGiftResult result, string reason)
        {
            return new FriendOperationOutcome(false, result, 0, reason);
        }
    }
}
