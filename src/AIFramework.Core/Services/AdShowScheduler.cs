#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 广告展示调度器：<b>冷却 + 每日配额 + 完播记账</b>的纯逻辑，不依赖任何广告 SDK 与引擎。
    /// <para>
    /// <b>它解决的问题不是"放广告"，而是"什么时候不该放、这次展示算不算一次机会"</b>：
    /// 广告平台只要起播就计一次曝光（无论玩家是否看完），因此扣减必须发生在"展示"这一层；
    /// 而奖励只能在"完播"这一层发放。这条边界被无头单测逐条锁死后，
    /// Console 假填充与未来真实 SDK 适配器就共享同一套结算语义。
    /// </para>
    /// <para>
    /// <b>可用性不在这里判断</b>：本类不知道 SDK 是否接线、有没有填充（那是适配器的职责，
    /// 见 <see cref="IAdService.IsAvailable"/>）。适配器的判定顺序固定为
    /// "可用性（<see cref="IAdService.IsAvailable"/>）→ <see cref="CanShow"/>（配额 → 冷却）"，
    /// 与本类文档里"可用性 → 配额 → 冷却"的整体口径一致。
    /// </para>
    /// <para>
    /// <b>两个时钟，各管一段</b>：
    /// <list type="bullet">
    /// <item>冷却读 <see cref="ITimeProvider.ElapsedSeconds"/>（单调钟）：玩家改系统时间/时区不能绕过冷却，
    /// 也不会因为对时把冷却意外清零。</item>
    /// <item>每日配额读 <see cref="ITimeProvider.UtcNow"/> 的<b>UTC 自然日</b>：日序号 = <c>(UtcNow.Date - 1970-01-01 UTC).Days</c>，
    /// 跨日（UTC 00:00）自动归零。固定用 UTC 而不是本地时间，是为了"同一账号换时区不会多刷一组配额"，
    /// 与 <c>MailHandler</c> 的天序号口径保持同一套约定。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>不做拦截式记账</b>：<see cref="RecordShown"/> 只负责"记一笔真实展示"，不检查配额（调用方应先
    /// <see cref="CanShow"/>）。这样即使外部状态漂移也不会把一次已经发生的曝光记丢；超额时
    /// <see cref="RemainingRewardedToday"/> 读数为 0 而不是负数，调用方不需要处理负剩余。
    /// </para>
    /// <para>
    /// <b>非法输入</b>：<c>placementId &lt; 0</c> 已在 <see cref="AdShowRequest"/> 构造期归一化为 0；
    /// 本类在内部再做一次兜底（保证分组键恒非负）。未知 <see cref="AdPlacement"/> 枚举值一律按
    /// <see cref="AdShowState.Unavailable"/> 处理且不记账——宁可不出广告，也不猜一个脏配置的语义。
    /// </para>
    /// <para>
    /// <b>冷却分组键</b>是 <c>(广告位类型, 广告位 id)</c>：首页激励视频与结算页激励视频互不干扰，
    /// 激励视频与插屏即使共用 id 也各自按自己的冷却秒数计算（30s / 120s）。
    /// 业务广告位是有限集合（通常 &lt; 16 个），因此不为字典做容量淘汰；调用方不得传入动态生成的 id。
    /// </para>
    /// <para>线程模型：非线程安全。展示判定与记账都在主线程串行调用。</para>
    /// </summary>
    public sealed class AdShowScheduler
    {
        /// <summary>默认激励视频冷却（秒）。</summary>
        public const int DefaultRewardedCooldownSeconds = 30;

        /// <summary>默认插屏冷却（秒）。</summary>
        public const int DefaultInterstitialCooldownSeconds = 120;

        /// <summary>默认激励视频每日上限（自然日，UTC 日序号口径）。</summary>
        public const int DefaultDailyRewardedCap = 20;

        /// <summary>默认插屏每日上限（自然日，UTC 日序号口径）。</summary>
        public const int DefaultDailyInterstitialCap = 10;

        /// <summary>天序号纪元：与 <c>MailHandler</c> 不同，这里用 Unix 纪元（UTC），口径写死在文档里。</summary>
        private static readonly DateTime DayEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>时间源（冷却读单调钟，配额读 UTC 自然日）。</summary>
        private readonly ITimeProvider _time;

        /// <summary>激励视频冷却秒数（&lt; 0 已夹到 0）。</summary>
        private readonly int _rewardedCooldownSeconds;

        /// <summary>插屏冷却秒数（&lt; 0 已夹到 0）。</summary>
        private readonly int _interstitialCooldownSeconds;

        /// <summary>激励视频每日上限（&lt; 0 已夹到 0，即当天全禁）。</summary>
        private readonly int _dailyRewardedCap;

        /// <summary>插屏每日上限（&lt; 0 已夹到 0，即当天全禁）。</summary>
        private readonly int _dailyInterstitialCap;

        /// <summary>
        /// 最近一次展示的单调时刻：键 = <c>(广告位类型 &lt;&lt; 32) | 广告位 id</c>，值 = ElapsedSeconds。
        /// <para>用 <c>long</c> 打包键而不是自定义结构体：单字典、无装箱、无额外相等比较器，
        /// 且键的构成在 <see cref="CooldownKey"/> 一处可见。</para>
        /// </summary>
        private readonly Dictionary<long, double> _lastShowSeconds = new Dictionary<long, double>();

        /// <summary>当前使用量所属的 UTC 天序号；<see cref="int.MinValue"/> 表示尚无记录。</summary>
        private int _usageDayIndex = int.MinValue;

        /// <summary>当天已消耗的激励视频次数。</summary>
        private int _rewardedUsedToday;

        /// <summary>当天已消耗的插屏次数。</summary>
        private int _interstitialUsedToday;

        /// <summary>累计完播次数（跨日不清零，诊断/对账用）。</summary>
        private int _completedCount;

        /// <summary>累计跳过次数（跨日不清零，诊断/对账用）。</summary>
        private int _skippedCount;

        /// <summary>创建广告展示调度器。</summary>
        /// <param name="time">时间源（非 <c>null</c>；冷却与跨日都必须经由此端口，逻辑层禁止直接读系统时钟）。</param>
        /// <param name="rewardedCooldownSeconds">激励视频冷却秒数（负值按 0 处理，即无冷却）。</param>
        /// <param name="interstitialCooldownSeconds">插屏冷却秒数（负值按 0 处理）。</param>
        /// <param name="dailyRewardedCap">激励视频每日上限（负值按 0 处理，即当天全禁）。</param>
        /// <param name="dailyInterstitialCap">插屏每日上限（负值按 0 处理）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="time"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public AdShowScheduler(
            ITimeProvider time,
            int rewardedCooldownSeconds = DefaultRewardedCooldownSeconds,
            int interstitialCooldownSeconds = DefaultInterstitialCooldownSeconds,
            int dailyRewardedCap = DefaultDailyRewardedCap,
            int dailyInterstitialCap = DefaultDailyInterstitialCap)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _rewardedCooldownSeconds = ClampToZero(rewardedCooldownSeconds);
            _interstitialCooldownSeconds = ClampToZero(interstitialCooldownSeconds);
            _dailyRewardedCap = ClampToZero(dailyRewardedCap);
            _dailyInterstitialCap = ClampToZero(dailyInterstitialCap);
        }

        /// <summary>今天还能看几次激励视频（自然日重置；不返回负数）。</summary>
        public int RemainingRewardedToday
        {
            get
            {
                int remaining = _dailyRewardedCap - RewardedUsedToday;
                return remaining > 0 ? remaining : 0;
            }
        }

        /// <summary>今天还能看几次插屏（自然日重置；不返回负数）。</summary>
        public int RemainingInterstitialToday
        {
            get
            {
                int remaining = _dailyInterstitialCap - InterstitialUsedToday;
                return remaining > 0 ? remaining : 0;
            }
        }

        /// <summary>累计完播次数（跨日不清零；横幅没有完播概念，不计入）。</summary>
        public int CompletedCount
        {
            get { return _completedCount; }
        }

        /// <summary>累计跳过次数（跨日不清零；只有激励视频/插屏可能产生跳过）。</summary>
        public int SkippedCount
        {
            get { return _skippedCount; }
        }

        /// <summary>
        /// 展示前判定：可用性（未知枚举）→ 配额 → 冷却。<b>纯查询，不改变任何状态</b>，可重复调用。
        /// <para>
        /// 顺序固定：同一时刻既冷却又超额时必须先报 <see cref="AdShowState.QuotaExceeded"/>，
        /// 否则 UI 会让玩家倒计时 30 秒后发现次数早就用完了。
        /// </para>
        /// </summary>
        /// <param name="request">展示请求。</param>
        /// <returns>
        /// <see cref="AdShowState.Shown"/> 表示"可展示"（未真正展示）；
        /// <see cref="AdShowState.QuotaExceeded"/> / <see cref="AdShowState.CoolingDown"/> 表示被拦截；
        /// 未知广告位类型返回 <see cref="AdShowState.Unavailable"/>。
        /// </returns>
        public AdShowState CanShow(in AdShowRequest request)
        {
            switch (request.Placement)
            {
                case AdPlacement.RewardedVideo:
                    if (RewardedUsedToday >= _dailyRewardedCap)
                    {
                        return AdShowState.QuotaExceeded;
                    }

                    return IsCoolingDown(AdPlacement.RewardedVideo, request.PlacementId, _rewardedCooldownSeconds)
                        ? AdShowState.CoolingDown
                        : AdShowState.Shown;

                case AdPlacement.Interstitial:
                    if (InterstitialUsedToday >= _dailyInterstitialCap)
                    {
                        return AdShowState.QuotaExceeded;
                    }

                    return IsCoolingDown(AdPlacement.Interstitial, request.PlacementId, _interstitialCooldownSeconds)
                        ? AdShowState.CoolingDown
                        : AdShowState.Shown;

                case AdPlacement.Banner:
                    // 横幅无冷却、无配额：Phase 1 只记账不改布局，恒可展示（真正的展示由调用方按需驱动）。
                    return AdShowState.Shown;

                default:
                    // 未知枚举值（配置脏数据/版本不一致）：不猜语义，按不可用处理。
                    return AdShowState.Unavailable;
            }
        }

        /// <summary>
        /// 记录一次<b>真实展示</b>（完播或跳过都算一次消耗；插屏同理）。
        /// <para>
        /// 消耗规则：激励视频/插屏各扣 1 次当日配额，并按 <c>(广告位类型, 广告位 id)</c> 刷新冷却起点；
        /// 横幅与未知枚举不记账。调用方必须先通过 <see cref="CanShow"/>，本方法不做拦截。
        /// </para>
        /// <para>
        /// 为什么不返回状态：状态由调用方按 <paramref name="completed"/> 决定
        /// （完播 → <see cref="AdShowState.Shown"/>，跳过 → <see cref="AdShowState.Skipped"/>），
        /// 服务端记录与本地记账分离，避免"记账成功但发奖失败"时把两件事混成一个返回值。
        /// </para>
        /// </summary>
        /// <param name="request">展示请求。</param>
        /// <param name="completed">是否完播；<c>false</c> 表示用户中途关闭（照样消耗配额/冷却，但不得发奖）。</param>
        public void RecordShown(in AdShowRequest request, bool completed)
        {
            int placementId = request.PlacementId < 0 ? 0 : request.PlacementId;

            switch (request.Placement)
            {
                case AdPlacement.RewardedVideo:
                    EnsureUsageDay();
                    _rewardedUsedToday++;
                    RecordCooldown(AdPlacement.RewardedVideo, placementId);
                    break;

                case AdPlacement.Interstitial:
                    EnsureUsageDay();
                    _interstitialUsedToday++;
                    RecordCooldown(AdPlacement.Interstitial, placementId);
                    break;

                case AdPlacement.Banner:
                    // 横幅没有"完播"概念，也不消耗任何配额/冷却：Phase 1 只记账不改布局。
                    return;

                default:
                    // 未知枚举不记账：脏配置不应该污染真实广告位的配额。
                    return;
            }

            if (completed)
            {
                _completedCount++;
            }
            else
            {
                _skippedCount++;
            }
        }

        /// <summary>当天已用的激励视频次数（跨日自动视为 0，<b>不修改状态</b>）。</summary>
        private int RewardedUsedToday
        {
            get { return _usageDayIndex == TodayIndex ? _rewardedUsedToday : 0; }
        }

        /// <summary>当天已用的插屏次数（跨日自动视为 0，<b>不修改状态</b>）。</summary>
        private int InterstitialUsedToday
        {
            get { return _usageDayIndex == TodayIndex ? _interstitialUsedToday : 0; }
        }

        /// <summary>
        /// 今天的 UTC 自然日序号（自 1970-01-01 UTC 起的整天数）。
        /// <para>取 <c>.Date</c> 而不是 <c>Ticks / TimeSpan.TicksPerDay</c>：前者对任意 DateTimeKind 都按
        /// "日期"截断，后者会把 1970 年前的负时刻算错一天。</para>
        /// </summary>
        private int TodayIndex
        {
            get { return (int)(_time.UtcNow.Date - DayEpoch).TotalDays; }
        }

        /// <summary>若已跨日则把当日消耗计数归零（只由 <see cref="RecordShown"/> 调用，保证 CanShow 无副作用）。</summary>
        private void EnsureUsageDay()
        {
            int today = TodayIndex;
            if (_usageDayIndex == today)
            {
                return;
            }

            _usageDayIndex = today;
            _rewardedUsedToday = 0;
            _interstitialUsedToday = 0;
        }

        /// <summary>
        /// 该广告位是否还在冷却。口径：<c>当前单调时刻 - 上次展示时刻 &lt; 冷却秒数</c>；
        /// 恰好满冷却秒数即放行（边界取"冷却结束的那一刻可以看"）。
        /// 单调钟倒退（异常时间源）时差值为负，同样按冷却中处理，绝不放行。
        /// </summary>
        private bool IsCoolingDown(AdPlacement placement, int placementId, int cooldownSeconds)
        {
            if (cooldownSeconds <= 0)
            {
                return false;
            }

            double lastSeconds;
            if (!_lastShowSeconds.TryGetValue(CooldownKey(placement, placementId), out lastSeconds))
            {
                return false;
            }

            return _time.ElapsedSeconds - lastSeconds < cooldownSeconds;
        }

        /// <summary>刷新某广告位的冷却起点（以单调钟为基准）。</summary>
        private void RecordCooldown(AdPlacement placement, int placementId)
        {
            _lastShowSeconds[CooldownKey(placement, placementId)] = _time.ElapsedSeconds;
        }

        /// <summary>冷却分组键：高 32 位广告位类型 + 低 32 位广告位 id（id 已在构造期归一化，这里再兜底）。</summary>
        private static long CooldownKey(AdPlacement placement, int placementId)
        {
            int safePlacementId = placementId < 0 ? 0 : placementId;
            return ((long)(byte)placement << 32) | (uint)safePlacementId;
        }

        /// <summary>负配置按 0 处理：配置错误退化为"关闭该限制"，而不是让广告功能整体崩溃。</summary>
        private static int ClampToZero(int value)
        {
            return value < 0 ? 0 : value;
        }
    }
}
