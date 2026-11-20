using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleAdService"/> 的行为契约验收：可用性降级、完播/跳过结果映射、冷却与配额拦截、
    /// 脚本化"跳过"的一次性消费语义，以及排空环形日志。
    /// <para>
    /// 这些用例同时是"未来真实广告 SDK 实现"的验收清单：真机实现负责拉起 SDK 与拿到关闭回调，
    /// 但"什么算一次展示消耗、什么时候能发奖、被拦截时不得记账"必须与这里完全一致——
    /// 否则无头测试全绿、真机上要么发不出奖励，要么被刷。
    /// </para>
    /// </summary>
    public sealed class ConsoleAdServiceTests
    {
        [Fact]
        public void Constructor_NullScheduler_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new ConsoleAdService(null!, new FakeTimeProvider()));
        }

        [Fact]
        public void Constructor_NullTime_Throws()
        {
            // 时间源只做装配期校验：调度器已持有同一口径的时钟，本服务不再自带第二套时间源。
            var time = new FakeTimeProvider();
            Assert.Throws<ArgumentNullException>(
                () => new ConsoleAdService(new AdShowScheduler(time), null!));
        }

        [Fact]
        public void IsAvailable_ReflectsConstructorConfiguration()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);

            Assert.True(new ConsoleAdService(scheduler, time).IsAvailable);
            Assert.False(new ConsoleAdService(scheduler, time, available: false).IsAvailable);
        }

        [Fact]
        public void Unavailable_Show_ReturnsUnavailableWithoutConsumingSchedulerState()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            var service = new ConsoleAdService(scheduler, time, available: false);
            AdShowRequest request = Rewarded(1);

            AdShowResult result = service.Show(in request);

            Assert.Equal(AdShowState.Unavailable, result.State);
            Assert.Equal(ConsoleAdService.ReasonUnavailable, result.Reason);
            Assert.False(result.IsRewarded);

            // 未接线不得消耗任何记账状态：否则恢复接线后玩家"莫名少了一次机会"。
            Assert.Equal(20, scheduler.RemainingRewardedToday);
            Assert.Equal(0, service.TotalShown);
            Assert.Equal(0, service.TotalSkipped);

            Assert.Equal("ad#1 rewarded unavailable", Assert.Single(service.DrainLog()));
        }

        [Fact]
        public void Unavailable_CanShow_ReturnsUnavailable()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            var service = new ConsoleAdService(scheduler, time, available: false);
            AdShowRequest request = Rewarded(1);

            AdShowResult result = service.CanShow(in request);

            Assert.Equal(AdShowState.Unavailable, result.State);
            Assert.False(service.IsAvailable);

            // 纯查询不写日志（UI 可能每秒轮询一次，不能被诊断日志刷爆）。
            Assert.Empty(service.DrainLog());
        }

        [Fact]
        public void Show_Rewarded_ReturnsShownAndIsRewarded_AndConsumesQuota()
        {
            CreateService(out ConsoleAdService service, out AdShowScheduler scheduler);
            AdShowRequest request = Rewarded(1);

            AdShowResult result = service.Show(in request);

            Assert.Equal(AdShowState.Shown, result.State);
            Assert.Equal(AdPlacement.RewardedVideo, result.Placement);
            Assert.Equal(1, result.PlacementId);
            Assert.Equal(ConsoleAdService.ReasonShown, result.Reason);
            Assert.True(result.IsRewarded);
            Assert.Equal(1, service.TotalShown);
            Assert.Equal(0, service.TotalSkipped);
            Assert.Equal(19, scheduler.RemainingRewardedToday);
        }

        [Fact]
        public void Show_Interstitial_ReturnsShownButNeverRewarded()
        {
            CreateService(out ConsoleAdService service, out _);
            AdShowRequest request = Interstitial(2);

            AdShowResult result = service.Show(in request);

            Assert.Equal(AdShowState.Shown, result.State);
            Assert.False(result.IsRewarded);
            Assert.Equal(1, service.TotalShown);
        }

        [Fact]
        public void Show_Banner_ReturnsShownAndNeverRewarded()
        {
            CreateService(out ConsoleAdService service, out AdShowScheduler scheduler);
            AdShowRequest request = Banner(3);

            AdShowResult result = service.Show(in request);

            Assert.Equal(AdShowState.Shown, result.State);
            Assert.False(result.IsRewarded);

            // 横幅不消耗激励/插屏配额。
            Assert.Equal(20, scheduler.RemainingRewardedToday);
            Assert.Equal(10, scheduler.RemainingInterstitialToday);
            Assert.Equal(1, service.TotalShown);
        }

        [Fact]
        public void Show_WithinCooldown_ReturnsCoolingDownWithoutExtraConsumption()
        {
            CreateService(out ConsoleAdService service, out AdShowScheduler scheduler);
            AdShowRequest request = Rewarded(1);

            Assert.Equal(AdShowState.Shown, service.Show(in request).State);
            AdShowResult second = service.Show(in request);

            Assert.Equal(AdShowState.CoolingDown, second.State);
            Assert.Equal(ConsoleAdService.ReasonCoolingDown, second.Reason);

            // 被冷却拦下的调用不得二次消耗配额，也不计入展示/跳过统计。
            Assert.Equal(19, scheduler.RemainingRewardedToday);
            Assert.Equal(1, service.TotalShown);
            Assert.Equal(0, service.TotalSkipped);
        }

        [Fact]
        public void Show_QuotaExceeded_ReturnsQuotaExceededAndDoesNotCountAsShown()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 1, dailyInterstitialCap: 1);
            var service = new ConsoleAdService(scheduler, time);

            AdShowRequest first = Rewarded(1);
            AdShowRequest second = Rewarded(2);
            Assert.Equal(AdShowState.Shown, service.Show(in first).State);

            AdShowResult blocked = service.Show(in second);

            Assert.Equal(AdShowState.QuotaExceeded, blocked.State);
            Assert.Equal(ConsoleAdService.ReasonQuotaExceeded, blocked.Reason);
            Assert.Equal(1, service.TotalShown);
            Assert.Equal(0, service.TotalSkipped);
        }

        [Fact]
        public void Show_ScriptedSkip_ReturnsSkippedAndConsumesQuotaButNoReward()
        {
            CreateService(out ConsoleAdService service, out AdShowScheduler scheduler);
            service.SetNextCompletion(false);
            AdShowRequest request = Rewarded(1);

            AdShowResult result = service.Show(in request);

            Assert.Equal(AdShowState.Skipped, result.State);
            Assert.Equal(ConsoleAdService.ReasonSkipped, result.Reason);
            Assert.False(result.IsRewarded);

            // 跳过也消耗一次（平台侧已曝光），但不发奖。
            Assert.Equal(19, scheduler.RemainingRewardedToday);
            Assert.Equal(0, service.TotalShown);
            Assert.Equal(1, service.TotalSkipped);
        }

        [Fact]
        public void SetNextCompletion_IsConsumedByOneRealShow()
        {
            CreateService(out ConsoleAdService service, out _);
            service.SetNextCompletion(false);
            AdShowRequest request = Rewarded(1);

            Assert.Equal(AdShowState.Skipped, service.Show(in request).State);

            // 一次性脚本：默认回到"假填充总是完播"，下一次真实展示不会再跳过。
            Assert.Equal(AdShowState.Shown, service.Show(new AdShowRequest(AdPlacement.RewardedVideo, 2)).State);
            Assert.Equal(1, service.TotalSkipped);
            Assert.Equal(1, service.TotalShown);
        }

        [Fact]
        public void SetNextCompletion_IsNotConsumedByBlockedCall()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            var service = new ConsoleAdService(scheduler, time);
            AdShowRequest request = Rewarded(1);

            Assert.Equal(AdShowState.Shown, service.Show(in request).State);
            service.SetNextCompletion(false);

            // 冷却拦下的调用没有真正展示，脚本必须留到下一次真实展示。
            Assert.Equal(AdShowState.CoolingDown, service.Show(in request).State);

            time.Advance(30);
            Assert.Equal(AdShowState.Skipped, service.Show(in request).State);
            Assert.Equal(1, service.TotalSkipped);
        }

        [Fact]
        public void CanShow_WhenReady_ReturnsShownWithReadyReasonAndNoSideEffects()
        {
            CreateService(out ConsoleAdService service, out AdShowScheduler scheduler);
            service.SetNextCompletion(false);
            AdShowRequest request = Rewarded(1);

            for (int i = 0; i < 5; i++)
            {
                AdShowResult canShow = service.CanShow(in request);
                Assert.Equal(AdShowState.Shown, canShow.State);
                Assert.Equal(ConsoleAdService.ReasonReady, canShow.Reason);
            }

            // 查询不消耗配额、不消费脚本、不写日志。
            Assert.Equal(20, scheduler.RemainingRewardedToday);
            Assert.Empty(service.DrainLog());

            Assert.Equal(AdShowState.Skipped, service.Show(in request).State);
        }

        [Fact]
        public void Show_UnknownPlacement_ReturnsUnavailableWithoutAccounting()
        {
            CreateService(out ConsoleAdService service, out AdShowScheduler scheduler);
            var request = new AdShowRequest((AdPlacement)9, 1);

            AdShowResult result = service.Show(in request);

            Assert.Equal(AdShowState.Unavailable, result.State);
            Assert.False(result.IsRewarded);
            Assert.Equal(0, service.TotalShown);
            Assert.Equal(0, service.TotalSkipped);
            Assert.Equal(20, scheduler.RemainingRewardedToday);

            IReadOnlyList<string> log = service.DrainLog();
            Assert.Single(log);
            Assert.Equal("ad#1 " + ConsoleAdService.NameOf((AdPlacement)9) + " unavailable", log[0]);
        }

        [Fact]
        public void Show_InterstitialScriptedSkip_IsSkippedAndNotRewarded()
        {
            CreateService(out ConsoleAdService service, out _);
            service.SetNextCompletion(false);
            AdShowRequest request = Interstitial(1);

            AdShowResult result = service.Show(in request);

            Assert.Equal(AdShowState.Skipped, result.State);
            Assert.False(result.IsRewarded);
            Assert.Equal(1, service.TotalSkipped);
        }

        [Fact]
        public void Show_Banner_IgnoresScriptedSkipAndDoesNotConsumeIt()
        {
            CreateService(out ConsoleAdService service, out _);
            service.SetNextCompletion(false);

            // 横幅没有"完播"概念：脚本化的跳过只作用于激励视频/插屏。
            Assert.Equal(AdShowState.Shown, service.Show(new AdShowRequest(AdPlacement.Banner, 1)).State);

            AdShowRequest rewarded = Rewarded(1);
            Assert.Equal(AdShowState.Skipped, service.Show(in rewarded).State);

            Assert.Equal(1, service.TotalShown);
            Assert.Equal(1, service.TotalSkipped);
        }

        [Fact]
        public void Show_SharesSchedulerStateAcrossInstances()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            var first = new ConsoleAdService(scheduler, time);
            var second = new ConsoleAdService(scheduler, time);
            AdShowRequest request = Rewarded(1);

            Assert.Equal(AdShowState.Shown, first.Show(in request).State);

            // 同一调度器被两个 UI 入口共享：冷却与配额必须全局一致，不能"换界面重置"。
            Assert.Equal(AdShowState.CoolingDown, second.CanShow(in request).State);
            Assert.Equal(19, scheduler.RemainingRewardedToday);
        }

        [Fact]
        public void DrainLog_RecordsOneLinePerShowAttempt_AndClearsAfterDrain()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(time);
            var service = new ConsoleAdService(scheduler, time);

            AdShowRequest rewarded = Rewarded(1);
            Assert.Equal(AdShowState.Shown, service.Show(in rewarded).State);
            Assert.Equal(AdShowState.CoolingDown, service.Show(in rewarded).State);

            IReadOnlyList<string> log = service.DrainLog();

            Assert.Equal(2, log.Count);
            Assert.Equal("ad#1 rewarded shown", log[0]);
            Assert.Equal("ad#1 rewarded cooling_down", log[1]);

            // 排空语义：第二次取出必须为空，避免同一批事件被重复打印。
            Assert.Empty(service.DrainLog());
        }

        [Fact]
        public void DrainLog_KeepsOnlyLatestEntries()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 100, dailyInterstitialCap: 100);
            var service = new ConsoleAdService(scheduler, time);

            for (int id = 1; id <= 40; id++)
            {
                AdShowRequest request = Rewarded(id);
                Assert.Equal(AdShowState.Shown, service.Show(in request).State);
            }

            IReadOnlyList<string> log = service.DrainLog();

            // 环形容量 32：前 8 条被覆盖，保留 id 9..40。
            Assert.Equal(ConsoleAdService.LogCapacity, log.Count);
            Assert.Equal("ad#9 rewarded shown", log[0]);
            Assert.Equal("ad#40 rewarded shown", log[log.Count - 1]);
        }

        [Fact]
        public void TotalShownAndSkipped_OnlyCountRealShowings()
        {
            var time = new FakeTimeProvider();
            var scheduler = new AdShowScheduler(
                time, rewardedCooldownSeconds: 0, interstitialCooldownSeconds: 0,
                dailyRewardedCap: 2, dailyInterstitialCap: 2);
            var service = new ConsoleAdService(scheduler, time);

            AdShowRequest done = Rewarded(1);
            AdShowRequest skipped = Rewarded(2);
            AdShowRequest overQuota = Rewarded(3);
            service.SetNextCompletion(false);

            Assert.Equal(AdShowState.Skipped, service.Show(in skipped).State);
            service.SetNextCompletion(true);
            Assert.Equal(AdShowState.Shown, service.Show(in done).State);
            Assert.Equal(AdShowState.QuotaExceeded, service.Show(in overQuota).State);

            Assert.Equal(1, service.TotalShown);
            Assert.Equal(1, service.TotalSkipped);
        }

        [Fact]
        public void Show_NullReasonNormalization_KeepsContractNonEmpty()
        {
            // 契约层归一化：即使实现方给 null 原因，调用方也不必判空。
            var result = new AdShowResult(AdShowState.Unavailable, AdPlacement.Banner, 1, null);

            Assert.Equal(string.Empty, result.Reason);
            Assert.False(result.IsRewarded);
        }

        /// <summary>默认配置的 Console 服务 + 其共享调度器（默认冷却 30/120 秒、配额 20/10）。</summary>
        private static void CreateService(out ConsoleAdService service, out AdShowScheduler scheduler)
        {
            var time = new FakeTimeProvider();
            scheduler = new AdShowScheduler(time);
            service = new ConsoleAdService(scheduler, time);
        }

        /// <summary>激励视频请求。</summary>
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

        /// <summary>可拨动的假时间源（无头测试唯一时间入口）。</summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            public void Advance(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }
        }
    }
}
