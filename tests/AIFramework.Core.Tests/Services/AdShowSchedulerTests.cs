using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="AdShowScheduler"/> 的行为契约验收：冷却分组、每日配额、跨日重置、跳过也消耗、
    /// 横幅不参与判定、非法输入归一化，以及"<c>CanShow</c> 绝不改状态"。
    /// <para>
    /// 这些用例是"看广告发奖"红线的唯一守卫：广告平台侧只要起播就算一次曝光（无论玩家是否看完），
    /// 因此配额/冷却必须在"展示"这一层消耗，而奖励只能在"完播"这一层发放。
    /// 任何一条被放松（例如跳过不扣次数），线上就直接变成可无限刷的奖励漏洞。
    /// </para>
    /// <para>
    /// 时间口径是这套用例的核心：冷却走 <see cref="ITimeProvider.ElapsedSeconds"/>（单调钟，
    /// 改系统时间不能绕过冷却），每日配额走 <see cref="ITimeProvider.UtcNow"/> 的自然日（跨日归零）。
    /// 假时间源把两者拆开推进，专门验证"用错了时钟"这类回归。
    /// </para>
    /// </summary>
    public sealed class AdShowSchedulerTests
    {
        [Fact]
        public void Constructor_NullTime_Throws()
        {
            // 唯一允许抛异常的入口：没有时间源就无法判断冷却与跨日，属于装配期编程错误。
            Assert.Throws<ArgumentNullException>(() => new AdShowScheduler(null!));
        }

        [Fact]
        public void CanShow_InitialState_AllPlacementsAreShown()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);

            AdShowRequest rewarded = Rewarded(1);
            AdShowRequest interstitial = Interstitial(1);
            AdShowRequest banner = Banner(1);

            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in rewarded));
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in interstitial));
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in banner));
        }

        [Fact]
        public void CanShow_DoesNotConsumeQuotaOrStartCooldown()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest request = Rewarded(1);

            // 纯查询：重复调用必须一直得到同一个答案，且不改变任何记账状态。
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(AdShowState.Shown, scheduler.CanShow(in request));
            }

            Assert.Equal(20, scheduler.RemainingRewardedToday);

            scheduler.RecordShown(in request, completed: true);

            // 若 CanShow 偷偷消耗过状态，这里就不可能恰好剩 19 且进入冷却。
            Assert.Equal(19, scheduler.RemainingRewardedToday);
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in request));
        }

        [Fact]
        public void RecordShown_ThenCanShow_IsCoolingDownAndConsumesOneQuota()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest request = Rewarded(1);

            scheduler.RecordShown(in request, completed: true);

            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in request));
            Assert.Equal(19, scheduler.RemainingRewardedToday);
        }

        [Fact]
        public void CanShow_AfterCooldownExpires_IsShownAgain()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest request = Rewarded(1);

            scheduler.RecordShown(in request, completed: true);

            // 差一秒结束冷却：仍然阻断。
            time.Advance(29);
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in request));

            // 满 30 秒即放行（边界取"冷却结束的那一刻可看"，用整秒推进避免浮点抖动）。
            time.Advance(1);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in request));
        }

        [Fact]
        public void RewardedAndInterstitial_UseTheirOwnCooldownSeconds()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest rewarded = Rewarded(1);
            AdShowRequest interstitial = Interstitial(1);

            scheduler.RecordShown(in rewarded, completed: true);
            scheduler.RecordShown(in interstitial, completed: true);

            // 30 秒：激励视频解冻，插屏（120 秒）仍在冷却。
            time.Advance(30);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in rewarded));
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in interstitial));

            // 再 90 秒（合计 120）：插屏解冻。
            time.Advance(90);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in interstitial));
        }

        [Fact]
        public void Cooldown_IsTrackedPerPlacementId()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest first = Rewarded(1);
            AdShowRequest second = Rewarded(2);

            scheduler.RecordShown(in first, completed: true);

            // 广告位 1 冷却，广告位 2 是另一个位，立即可看。
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in first));
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in second));
        }

        [Fact]
        public void RewardedQuota_Exhausted_ReturnsQuotaExceeded_ButInterstitialStillShown()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 3, dailyInterstitialCap: 2);

            // 用不同 placementId 绕开冷却，只考察配额。
            for (int id = 1; id <= 3; id++)
            {
                AdShowRequest request = Rewarded(id);
                Assert.Equal(AdShowState.Shown, scheduler.CanShow(in request));
                scheduler.RecordShown(in request, completed: true);
            }

            AdShowRequest fourth = Rewarded(4);
            Assert.Equal(AdShowState.QuotaExceeded, scheduler.CanShow(in fourth));
            Assert.Equal(0, scheduler.RemainingRewardedToday);

            // 激励视频配额用尽不影响插屏的独立配额。
            AdShowRequest interstitial = Interstitial(1);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in interstitial));
            Assert.Equal(2, scheduler.RemainingInterstitialToday);
        }

        [Fact]
        public void InterstitialQuota_Exhausted_ReturnsQuotaExceeded()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 1, dailyInterstitialCap: 2);

            for (int id = 1; id <= 2; id++)
            {
                AdShowRequest request = Interstitial(id);
                scheduler.RecordShown(in request, completed: true);
            }

            AdShowRequest third = Interstitial(3);
            Assert.Equal(AdShowState.QuotaExceeded, scheduler.CanShow(in third));
            Assert.Equal(0, scheduler.RemainingInterstitialToday);
        }

        [Fact]
        public void Quota_ResetsOnNextUtcNaturalDay()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 2, dailyInterstitialCap: 1);

            AdShowRequest used1 = Rewarded(1);
            AdShowRequest used2 = Rewarded(2);
            scheduler.RecordShown(in used1, completed: true);
            scheduler.RecordShown(in used2, completed: true);
            Assert.Equal(0, scheduler.RemainingRewardedToday);

            // 整 24 小时（同时推进单调钟与 UTC 墙钟）→ 次自然日，配额自动归零。
            time.Advance(86400);

            AdShowRequest next = Rewarded(3);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in next));
            Assert.Equal(2, scheduler.RemainingRewardedToday);
        }

        [Fact]
        public void Quota_DoesNotResetWithinSameUtcDay()
        {
            var time = new FakeTimeProvider(startUtc: new DateTime(2026, 11, 20, 0, 30, 0, DateTimeKind.Utc));
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 1, dailyInterstitialCap: 1);

            AdShowRequest request = Rewarded(1);
            scheduler.RecordShown(in request, completed: true);

            // 23 小时后仍是同一天（00:30 → 23:30）：配额不得重置。
            time.Advance(23 * 3600);
            AdShowRequest sameDay = Rewarded(2);
            Assert.Equal(AdShowState.QuotaExceeded, scheduler.CanShow(in sameDay));
            Assert.Equal(0, scheduler.RemainingRewardedToday);
        }

        [Fact]
        public void Quota_UsesUtcDay_NotMonotonicClock()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 1, dailyInterstitialCap: 1);

            AdShowRequest request = Rewarded(1);
            scheduler.RecordShown(in request, completed: true);

            // 只推进单调钟（相当于应用连续运行了两天，但 UTC 日期没动）：配额不得重置。
            // 若实现误用 ElapsedSeconds 算天数，这里会变成 Shown —— 线上就是"挂机可刷广告"。
            time.AdvanceMonotonicOnly(2 * 86400);

            AdShowRequest next = Rewarded(2);
            Assert.Equal(AdShowState.QuotaExceeded, scheduler.CanShow(in next));
        }

        [Fact]
        public void Cooldown_UsesMonotonicClock_NotUtcWallClock()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest request = Rewarded(1);

            scheduler.RecordShown(in request, completed: true);

            // 只改 UTC 墙钟（玩家手动改系统时间/时区）：不得绕过冷却。
            // 若实现误用 UtcNow 算冷却，这里会变成 Shown —— 线上就是"改表看无限广告"。
            time.AdvanceWallClockOnly(TimeSpan.FromHours(1));

            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in request));
        }

        [Fact]
        public void SkippedShow_StillConsumesQuotaAndCooldown()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest request = Rewarded(1);

            // 用户中途关闭：平台侧已经产生一次曝光，配额与冷却照样消耗。
            scheduler.RecordShown(in request, completed: false);

            Assert.Equal(19, scheduler.RemainingRewardedToday);
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in request));

            // 完播/跳过分开记账，供完播奖励口径核对。
            Assert.Equal(0, scheduler.CompletedCount);
            Assert.Equal(1, scheduler.SkippedCount);
        }

        [Fact]
        public void Banner_HasNoCooldownAndConsumesNoQuota()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest banner = Banner(1);

            scheduler.RecordShown(in banner, completed: true);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in banner));

            // 横幅不进入任何配额，也不影响激励/插屏的剩余次数。
            Assert.Equal(20, scheduler.RemainingRewardedToday);
            Assert.Equal(10, scheduler.RemainingInterstitialToday);
            Assert.Equal(0, scheduler.CompletedCount);
        }

        [Fact]
        public void Banner_UnaffectedByExhaustedRewardedQuota()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 1, dailyInterstitialCap: 1);

            AdShowRequest rewarded = Rewarded(1);
            scheduler.RecordShown(in rewarded, completed: true);
            Assert.Equal(0, scheduler.RemainingRewardedToday);

            // 配额耗尽的只有激励视频，横幅照常展示（Phase 1 只记账不改布局）。
            AdShowRequest banner = Banner(7);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in banner));
        }

        [Fact]
        public void InvalidPlacementId_IsNormalizedToZero()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);

            // 契约层归一化：负 id 视为 0，不会制造"幽灵广告位"。
            var invalid = new AdShowRequest(AdPlacement.RewardedVideo, -5);
            AdShowRequest normalized = Rewarded(0);
            Assert.Equal(0, invalid.PlacementId);

            scheduler.RecordShown(in invalid, completed: true);

            // 负 id 与 0 号位共享同一个冷却/配额分组。
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in normalized));
            Assert.Equal(19, scheduler.RemainingRewardedToday);
        }

        [Fact]
        public void UnknownPlacement_IsUnavailable_AndRecordShownDoesNothing()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);

            // 未知枚举值（配置表脏数据/版本不一致）：宁可不出广告，也不猜语义。
            var unknown = new AdShowRequest((AdPlacement)99, 1);
            Assert.Equal(AdShowState.Unavailable, scheduler.CanShow(in unknown));

            scheduler.RecordShown(in unknown, completed: true);

            // 不消耗配额、不启动任何冷却，正常广告位不受影响。
            Assert.Equal(20, scheduler.RemainingRewardedToday);
            Assert.Equal(10, scheduler.RemainingInterstitialToday);
            AdShowRequest valid = Rewarded(1);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in valid));
        }

        [Fact]
        public void RemainingCounters_StartAtConfiguredCaps()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, dailyRewardedCap: 5, dailyInterstitialCap: 2);

            Assert.Equal(5, scheduler.RemainingRewardedToday);
            Assert.Equal(2, scheduler.RemainingInterstitialToday);
        }

        [Fact]
        public void NegativeConfiguration_IsClampedToZero()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time,
                rewardedCooldownSeconds: -10,
                interstitialCooldownSeconds: -1,
                dailyRewardedCap: -3,
                dailyInterstitialCap: 0);

            // 负冷却按 0 处理：配置错误退化为"无冷却"，而不是让广告永远看不了。
            AdShowRequest rewarded = Rewarded(1);
            scheduler.RecordShown(in rewarded, completed: true);
            Assert.Equal(AdShowState.QuotaExceeded, scheduler.CanShow(in rewarded));

            // 负配额按 0 处理：直接视为"当天全部用尽"，读数不出现负数。
            Assert.Equal(0, scheduler.RemainingRewardedToday);
            Assert.Equal(0, scheduler.RemainingInterstitialToday);
        }

        [Fact]
        public void RecordShown_BeyondCap_RemainingNeverGoesNegative()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 2, dailyInterstitialCap: 2);

            // 即使调用方绕过 CanShow 直接记账（外部改内存/并发），读数也不允许变成负数。
            for (int id = 1; id <= 5; id++)
            {
                AdShowRequest request = Rewarded(id);
                scheduler.RecordShown(in request, completed: true);
            }

            Assert.Equal(0, scheduler.RemainingRewardedToday);
        }

        [Fact]
        public void CompletedAndSkippedCounters_AreLifetimeDiagnostics()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 9, dailyInterstitialCap: 9);

            AdShowRequest first = Rewarded(1);
            AdShowRequest second = Rewarded(2);
            AdShowRequest skipped = Rewarded(3);
            scheduler.RecordShown(in first, completed: true);
            scheduler.RecordShown(in second, completed: true);
            scheduler.RecordShown(in skipped, completed: false);

            Assert.Equal(2, scheduler.CompletedCount);
            Assert.Equal(1, scheduler.SkippedCount);

            // 累计口径跨日不清零（配额清的是当日消耗，不是完播统计）。
            time.Advance(86400);
            Assert.Equal(2, scheduler.CompletedCount);
            Assert.Equal(1, scheduler.SkippedCount);
        }

        [Fact]
        public void Quota_IsCheckedBeforeCooldown()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 30, interstitialCooldownSeconds: 30,
                dailyRewardedCap: 1, dailyInterstitialCap: 1);

            AdShowRequest request = Rewarded(1);
            scheduler.RecordShown(in request, completed: true);

            // 同一时刻既"冷却中"又"配额用尽"：必须先报配额，UI 才能提示"明日再来"而不是倒计时 30 秒。
            Assert.Equal(AdShowState.QuotaExceeded, scheduler.CanShow(in request));
        }

        [Fact]
        public void DefaultConstructor_UsesDocumentedDefaults()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);

            Assert.Equal(20, scheduler.RemainingRewardedToday);
            Assert.Equal(10, scheduler.RemainingInterstitialToday);

            AdShowRequest rewarded = Rewarded(1);
            scheduler.RecordShown(in rewarded, completed: true);
            time.Advance(29);
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in rewarded));
            time.Advance(1);
            Assert.Equal(AdShowState.Shown, scheduler.CanShow(in rewarded));
        }

        [Fact]
        public void InterstitialSkipped_IsAlsoRecordedAsSkipped()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            AdShowRequest request = Interstitial(2);

            scheduler.RecordShown(in request, completed: false);

            Assert.Equal(1, scheduler.SkippedCount);
            Assert.Equal(9, scheduler.RemainingInterstitialToday);
            Assert.Equal(AdShowState.CoolingDown, scheduler.CanShow(in request));
        }

        /// <summary>激励视频请求（默认广告位 id 1）。</summary>
        private static AdShowRequest Rewarded(int placementId)
        {
            return new AdShowRequest(AdPlacement.RewardedVideo, placementId);
        }

        /// <summary>插屏请求。</summary>
        private static AdShowRequest Interstitial(int placementId)
        {
            return new AdShowRequest(AdPlacement.Interstitial, placementId);
        }

        /// <summary>横幅请求。</summary>
        private static AdShowRequest Banner(int placementId)
        {
            return new AdShowRequest(AdPlacement.Banner, placementId);
        }

        /// <summary>
        /// 可分别拨动"单调钟"与"UTC 墙钟"的假时间源。
        /// <para>
        /// 两个时钟必须能独立推进，才能验收"冷却只看单调钟、配额只看自然日"这两条口径；
        /// 只提供一个 <c>Advance</c> 的假时间源会让这两个错误互相掩盖。
        /// </para>
        /// </summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow;

            public FakeTimeProvider(DateTime? startUtc = null)
            {
                _utcNow = startUtc ?? new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
            }

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            /// <summary>同时推进单调钟与墙钟（正常流逝的时间）。</summary>
            public void Advance(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }

            /// <summary>只推进单调钟（模拟应用连续运行但 UTC 日期不变）。</summary>
            public void AdvanceMonotonicOnly(double seconds)
            {
                _elapsedSeconds += seconds;
            }

            /// <summary>只改墙钟（模拟玩家手动改系统时间/时区）。</summary>
            public void AdvanceWallClockOnly(TimeSpan delta)
            {
                _utcNow = _utcNow.Add(delta);
            }
        }
    }
}
