using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="PlayerMetrics"/> 的行为契约验收：事件映射、日切与 DAU/连续天数、会话时长、
    /// 当日窗口、CSV 导出，以及"时间源违约也不炸业务"的降级路径。
    /// <para>
    /// <b>这些用例是看板数字的唯一守卫</b>：看板一旦算错，运营会按错的 DAU 排活动、按错的付费额做定价，
    /// 而且错了不会有人报错——数字看起来永远是"合理"的。因此每条口径都用假时钟逐条钉死，
    /// 特别是"同一关重开才算 Retry""战败不计胜利但也不计入未知事件""重复 Begin 不刷会话数"这三条。
    /// </para>
    /// <para>
    /// <b>时间口径</b>：日序号走 <see cref="ITimeProvider.UtcNow"/> 的 UTC 日，会话时长走
    /// <see cref="ITimeProvider.ElapsedSeconds"/> 单调钟。假时钟把两者拆开推进，
    /// 专门验证"用错了时钟"这类回归（例如跨天把时长清零、或读墙钟算时长）。
    /// </para>
    /// </summary>
    public sealed class PlayerMetricsTests
    {
        /// <summary>默认起始时刻：2026-11-20 13:00 UTC（北京时间 21:00，与 UTC 不同日序的日子已过）。</summary>
        private static readonly DateTime StartUtc = new DateTime(2026, 11, 20, 13, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------- 构造与降级

        [Fact]
        public void Constructor_NullTime_Throws()
        {
            // 没有时间源就算不出日序号与会话时长，属于装配期编程错误，必须装配即炸。
            Assert.Throws<ArgumentNullException>(() => new PlayerMetrics(null!));
        }

        [Fact]
        public void Snapshot_FreshAggregator_IsAllZeros()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            PlayerMetricsSnapshot snapshot = metrics.Snapshot();

            Assert.Equal(0, snapshot.Sessions);
            Assert.Equal(0L, snapshot.PlaySeconds);
            Assert.Equal(0, snapshot.LevelsStarted);
            Assert.Equal(0, snapshot.LevelsWon);
            Assert.Equal(0, snapshot.Purchases);
            Assert.Equal(0, snapshot.AdsWatched);
            Assert.Equal(0L, snapshot.SoftSpent);
            Assert.Equal(0L, snapshot.SoftEarned);
            Assert.Equal(0, snapshot.Retries);
            Assert.Equal(0, snapshot.DailyActiveDays);
            Assert.Equal(0, snapshot.CurrentStreakDays);
            Assert.Equal(0L, metrics.UnknownEvents);
        }

        // ---------------------------------------------------------------- 事件映射

        [Fact]
        public void Track_InvalidEventId_CountsUnknownAndTouchesNoMetric()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.None, 1f, 2f, 3f, null));

            Assert.Equal(1L, metrics.UnknownEvents);
            Assert.Equal(0, metrics.Snapshot().Purchases);
            Assert.Equal(0, metrics.Snapshot().LevelsStarted);
        }

        [Fact]
        public void Track_NonFiniteValue_CountsUnknown()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            // id 合法但数值是 NaN：结构上就不是一条可信事件，只能丢进未知，不能污染任何指标。
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.BattleStart, float.NaN, 0f, 0f, null));

            Assert.Equal(1L, metrics.UnknownEvents);
            Assert.Equal(0, metrics.Snapshot().LevelsStarted);
        }

        [Fact]
        public void Track_BattleStart_IncrementsLevelsStarted()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(BattleStart(cartridge: 1, levelId: 10));

            Assert.Equal(1, metrics.Snapshot().LevelsStarted);
            Assert.Equal(0, metrics.Snapshot().Retries);
            Assert.Equal(0L, metrics.UnknownEvents);
            Assert.Equal(1, metrics.DailyLevelsStarted);
        }

        [Fact]
        public void Track_BattleStart_SameLevelAgain_CountsOneRetry()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(BattleStart(1, 10));
            metrics.Track(BattleStart(1, 10));
            metrics.Track(BattleStart(1, 10));

            // 三次开始 = 1 次首次 + 2 次重开；LevelsStarted 仍然按"开始了几次"计 3。
            Assert.Equal(3, metrics.Snapshot().LevelsStarted);
            Assert.Equal(2, metrics.Snapshot().Retries);
            Assert.Equal(2, metrics.DailyRetries);
        }

        [Fact]
        public void Track_BattleStart_DifferentLevels_CountNoRetry()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(BattleStart(1, 10));
            metrics.Track(BattleStart(1, 11));
            metrics.Track(BattleStart(2, 10));

            // 关卡 10 的第二次出现算重开（第三行），因此恰好 1 次。
            Assert.Equal(3, metrics.Snapshot().LevelsStarted);
            Assert.Equal(1, metrics.Snapshot().Retries);
        }

        [Fact]
        public void Track_BattleStart_WithoutUsableLevelId_CountsStartButNoRetry()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            // 关卡 id 缺失（0 / 负数）时无法判定"是不是同一关重开"，只能记开始。
            metrics.Track(BattleStart(1, 0));
            metrics.Track(BattleStart(1, -5));

            Assert.Equal(2, metrics.Snapshot().LevelsStarted);
            Assert.Equal(0, metrics.Snapshot().Retries);
        }

        [Fact]
        public void Track_BattleEnd_Win_IncrementsLevelsWon()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(BattleEnd(won: true, durationSeconds: 42f));

            Assert.Equal(1, metrics.Snapshot().LevelsWon);
            Assert.Equal(1, metrics.DailyLevelsWon);
        }

        [Fact]
        public void Track_BattleEnd_Loss_CountsNothingAndIsNotUnknown()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(BattleEnd(won: false, durationSeconds: 42f));

            Assert.Equal(0, metrics.Snapshot().LevelsWon);
            // 战败是"已知但没有指标"，绝不能算未知：否则正常玩家的未知事件告警会被败场刷爆。
            Assert.Equal(0L, metrics.UnknownEvents);
        }

        [Fact]
        public void Track_BattleEnd_Duration_DoesNotAddPlaySeconds()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.Track(BattleEnd(won: true, durationSeconds: 300f));

            // 时长只认会话口径（Begin/End），否则一段战斗会被会话与战斗事件记两次。
            Assert.Equal(0L, metrics.Snapshot().PlaySeconds);
        }

        [Fact]
        public void Track_Purchase_AddsPurchaseAndSoftSpent()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(Purchase(itemId: 900, spent: 120f));

            Assert.Equal(1, metrics.Snapshot().Purchases);
            Assert.Equal(120L, metrics.Snapshot().SoftSpent);
            Assert.Equal(120L, metrics.DailySoftSpent);
            Assert.Equal(1, metrics.DailyPurchases);
        }

        [Fact]
        public void Track_Purchase_NegativeAmount_CountsPurchaseButNoSpend()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(Purchase(itemId: 900, spent: -80f));

            // 看板口径只增不减：负数（退款/脏数据）不做冲抵，但购买行为本身确实发生了。
            Assert.Equal(1, metrics.Snapshot().Purchases);
            Assert.Equal(0L, metrics.Snapshot().SoftSpent);
        }

        [Fact]
        public void Track_IapPurchased_CountsPurchaseButNotSoftSpent()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            // value1 是价格档（现实货币），不是软通货数量，不能进 SoftSpent。
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.IapPurchased, 77f, 6f, 0f, "com.demo.pack6"));

            Assert.Equal(1, metrics.Snapshot().Purchases);
            Assert.Equal(0L, metrics.Snapshot().SoftSpent);
            Assert.Equal(0L, metrics.UnknownEvents);
        }

        [Fact]
        public void Track_AdCompleted_IncrementsAdsWatched()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.AdCompleted, 1f, 0f, 0f, null));

            Assert.Equal(1, metrics.Snapshot().AdsWatched);
            Assert.Equal(1, metrics.DailyAdsWatched);
        }

        [Fact]
        public void Track_ItemGranted_SoftCurrency_IncrementsSoftEarned()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(ItemGranted(itemId: PlayerMetrics.DefaultSoftCurrencyItemId, quantity: 250f));

            Assert.Equal(250L, metrics.Snapshot().SoftEarned);
            Assert.Equal(250L, metrics.DailySoftEarned);
        }

        [Fact]
        public void Track_ItemGranted_OtherItem_IsNotCurrency()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            // 皮肤/材料被授予不是"发钱"：折算成货币会让通胀读数完全失真。
            metrics.Track(ItemGranted(itemId: 200, quantity: 5f));

            Assert.Equal(0L, metrics.Snapshot().SoftEarned);
            Assert.Equal(0L, metrics.UnknownEvents);
        }

        [Fact]
        public void Track_ItemGranted_CustomSoftCurrencyId_IsHonoured()
        {
            // 项目里金币 id 与默认值不同时，构造函数参数是唯一的接线点（不需要改 Core）。
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc), softCurrencyItemId: 5000);

            metrics.Track(ItemGranted(itemId: 5000, quantity: 40f));
            metrics.Track(ItemGranted(itemId: PlayerMetrics.DefaultSoftCurrencyItemId, quantity: 999f));

            // 默认 id（1001）在自定义口径下既不算收入、也不算未知事件。
            Assert.Equal(40L, metrics.Snapshot().SoftEarned);
            Assert.Equal(0L, metrics.UnknownEvents);
        }

        [Fact]
        public void Track_ItemGranted_WhenSoftCurrencyDisabled_CountsNothing()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc), softCurrencyItemId: 0);

            metrics.Track(ItemGranted(itemId: PlayerMetrics.DefaultSoftCurrencyItemId, quantity: 40f));

            Assert.Equal(0L, metrics.Snapshot().SoftEarned);
        }

        [Fact]
        public void Track_Gacha_IsRecognizedButContributesNothing()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.Gacha, 10f, 1f, 0f, null));

            // 已知事件、看板无对应列：识别后丢弃。若计入未知，未知事件计数就失去诊断价值。
            Assert.Equal(0L, metrics.UnknownEvents);
            Assert.Equal(0, metrics.Snapshot().Purchases);
            Assert.Equal(0L, metrics.Snapshot().SoftSpent);
        }

        [Fact]
        public void Track_UnknownEventId_CountsUnknownOnly()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(new AnalyticsEvent(9999, 1f, 1f, 1f, "future.event"));

            Assert.Equal(1L, metrics.UnknownEvents);
            Assert.Equal(0, metrics.Snapshot().Purchases);
            Assert.Equal(0, metrics.DailyPurchases);
        }

        [Fact]
        public void Track_AppLaunch_MarksActiveImplicitly()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, 1200f, 0f, 0f, null));

            Assert.Equal(1, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(1, metrics.Snapshot().CurrentStreakDays);
            Assert.Equal(0L, metrics.UnknownEvents);
        }

        [Fact]
        public void Track_SceneEnter_MarksActiveImplicitly()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.SceneEnter, 3f, 0f, 0f, "BattleScene"));

            Assert.Equal(1, metrics.Snapshot().DailyActiveDays);
        }

        [Fact]
        public void Track_PlayEvents_DoNotMarkActive()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            // 活跃只由 AppLaunch / SceneEnter / MarkActive 驱动；否则每个战斗事件都在改日窗口，
            // 出错时很难定位是哪条触发的。
            metrics.Track(BattleStart(1, 10));
            metrics.Track(Purchase(900, 10f));
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.AdCompleted, 1f, 0f, 0f, null));

            Assert.Equal(0, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(0, metrics.Snapshot().CurrentStreakDays);
        }

        // ---------------------------------------------------------------- 会话

        [Fact]
        public void BeginSession_IncrementsSessionsOnce()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.BeginSession();

            Assert.Equal(1, metrics.Snapshot().Sessions);
            Assert.True(metrics.InSession);
        }

        [Fact]
        public void BeginSession_Twice_DoesNotCountTwoSessions()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            // 断线重连 / 切场景会重复触发 Begin：必须按"已在会话中"幂等，否则会话数直接翻倍。
            metrics.BeginSession();
            metrics.BeginSession();
            metrics.BeginSession();

            Assert.Equal(1, metrics.Snapshot().Sessions);
        }

        [Fact]
        public void EndSession_WithoutBegin_IsIgnored()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.EndSession();

            Assert.Equal(0, metrics.Snapshot().Sessions);
            Assert.Equal(0L, metrics.Snapshot().PlaySeconds);
            Assert.False(metrics.InSession);
        }

        [Fact]
        public void BeginThenEnd_AddsElapsedSeconds()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.BeginSession();
            time.AdvanceSeconds(90);
            metrics.EndSession();

            Assert.Equal(90L, metrics.Snapshot().PlaySeconds);
            Assert.False(metrics.InSession);
        }

        [Fact]
        public void TwoSessions_AccumulatePlaySecondsAcrossGaps()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.BeginSession();
            time.AdvanceSeconds(30);
            metrics.EndSession();

            // 会话之间的空档不计入游玩时长（这正是"会话口径"与"进程存活时长"的区别）。
            time.AdvanceSeconds(3600);

            metrics.BeginSession();
            time.AdvanceSeconds(15);
            metrics.EndSession();

            Assert.Equal(2, metrics.Snapshot().Sessions);
            Assert.Equal(45L, metrics.Snapshot().PlaySeconds);
        }

        [Fact]
        public void EndSession_Twice_DoesNotDoubleCount()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.BeginSession();
            time.AdvanceSeconds(60);
            metrics.EndSession();
            time.AdvanceSeconds(60);
            metrics.EndSession();

            Assert.Equal(60L, metrics.Snapshot().PlaySeconds);
        }

        [Fact]
        public void EndSession_ClockGoesBackwards_AddsNoNegativePlaytime()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.BeginSession();
            time.AdvanceMonotonicOnly(-5);
            metrics.EndSession();

            // 单调钟回拨（异常时间源）：不累加负数，但会话必须正常结束，否则玩家会被永久卡在会话中。
            Assert.Equal(0L, metrics.Snapshot().PlaySeconds);
            Assert.False(metrics.InSession);

            time.AdvanceSeconds(10);
            metrics.BeginSession();
            Assert.Equal(2, metrics.Snapshot().Sessions);
        }

        [Fact]
        public void BeginSession_WhenClockThrows_StillCountsSession()
        {
            var time = new FakeTimeProvider(StartUtc) { ThrowOnElapsedSeconds = true };
            var metrics = new PlayerMetrics(time);

            metrics.BeginSession();
            time.ThrowOnElapsedSeconds = false;
            time.AdvanceSeconds(120);
            metrics.EndSession();

            // 时间源读不到开始秒：会话照记（会话确实发生了），但时长无法计算，宁可为 0 也不猜。
            Assert.Equal(1, metrics.Snapshot().Sessions);
            Assert.Equal(0L, metrics.Snapshot().PlaySeconds);
        }

        [Fact]
        public void PlaySeconds_RoundsHalfSecondsUp()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.BeginSession();
            time.AdvanceSeconds(90.5);
            metrics.EndSession();

            // 内部按 double 累加、快照时四舍五入：0.5 秒进位，避免长时间运行后系统性漏计。
            Assert.Equal(91L, metrics.Snapshot().PlaySeconds);
        }

        // ---------------------------------------------------------------- DAU / 连续天数

        [Fact]
        public void MarkActive_FirstCall_StartsDailyActiveAndStreak()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.MarkActive();

            Assert.Equal(1, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(1, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void MarkActive_SameDay_DoesNotGrowEitherCounter()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.MarkActive();
            time.AdvanceSeconds(60);
            metrics.MarkActive();
            time.AdvanceSeconds(60);
            metrics.MarkActive();

            Assert.Equal(1, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(1, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void MarkActive_NextUtcDay_IncrementsBothCounters()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.MarkActive();
            time.AdvanceDays(1);
            metrics.MarkActive();

            Assert.Equal(2, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(2, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void MarkActive_AfterSkippedDay_ResetsStreakButKeepsActiveDays()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.MarkActive();
            time.AdvanceDays(1);
            metrics.MarkActive();
            time.AdvanceDays(3);
            metrics.MarkActive();

            // 累计活跃天数只增（3 天里活跃了 3 次）；连续天数断档后从 1 重新开始。
            Assert.Equal(3, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(1, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void MarkActive_AcrossMonthBoundary_KeepsStreak()
        {
            var time = new FakeTimeProvider(new DateTime(2026, 11, 30, 10, 0, 0, DateTimeKind.Utc));
            var metrics = new PlayerMetrics(time);

            metrics.MarkActive();
            time.AdvanceDays(1);
            metrics.MarkActive();

            // 跨月不是"断档"：相邻两天必须继续连击（按日序号算，而不是按月份字符串比）。
            Assert.Equal(2, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void MarkActive_ClockGoesBackwards_ResetsStreakWithoutDoubleCountingDay()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.MarkActive();
            time.AdvanceDays(1);
            metrics.MarkActive();
            Assert.Equal(2, metrics.Snapshot().DailyActiveDays);

            // 玩家把系统时间往回调两天：连续天数断开，但"活跃天数"绝不重复累加
            // （同一天被数两次，看板的 DAU 会凭空变多，比少一天更糟）。
            time.AdvanceWallClockOnly(TimeSpan.FromDays(-2));
            metrics.MarkActive();

            Assert.Equal(2, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(1, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void MarkActive_WhenClockThrows_IsANoOp()
        {
            var time = new FakeTimeProvider(StartUtc) { ThrowOnUtcNow = true };
            var metrics = new PlayerMetrics(time);

            metrics.MarkActive();

            // 算不出"今天"就不该动 DAU：宁可少记一天，也不能拿错时间算出错误日序号。
            Assert.Equal(0, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(0, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void MarkActive_BeforeNineteenSeventy_IsTreatedAsUnavailable()
        {
            var time = new FakeTimeProvider(new DateTime(1969, 12, 31, 23, 0, 0, DateTimeKind.Utc));
            var metrics = new PlayerMetrics(time);

            metrics.MarkActive();

            // 1970 前的时刻是"时钟坏了的典型症状"，按不可用处理，避免算出负日序号污染 streak。
            Assert.Equal(0, metrics.Snapshot().DailyActiveDays);
        }

        // ---------------------------------------------------------------- 当日窗口

        [Fact]
        public void ResetDaily_ClearsWindowButKeepsCumulative()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(Purchase(900, 50f));
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.AdCompleted, 1f, 0f, 0f, null));
            metrics.Track(BattleStart(1, 10));

            metrics.ResetDaily();

            // 当日窗口归零。
            Assert.Equal(0, metrics.DailyPurchases);
            Assert.Equal(0L, metrics.DailySoftSpent);
            Assert.Equal(0, metrics.DailyAdsWatched);
            Assert.Equal(0, metrics.DailyLevelsStarted);

            // 累计字段一律不动。
            PlayerMetricsSnapshot snapshot = metrics.Snapshot();
            Assert.Equal(1, snapshot.Purchases);
            Assert.Equal(50L, snapshot.SoftSpent);
            Assert.Equal(1, snapshot.AdsWatched);
            Assert.Equal(1, snapshot.LevelsStarted);
        }

        [Fact]
        public void ResetDaily_KeepsDailyActiveDaysAndStreak()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.MarkActive();
            metrics.ResetDaily();

            // DAU/连续天数是"跨日累计"，不是当日窗口：ResetDaily 若把它们清零，看板会天天显示 1。
            Assert.Equal(1, metrics.Snapshot().DailyActiveDays);
            Assert.Equal(1, metrics.Snapshot().CurrentStreakDays);
        }

        [Fact]
        public void NextUtcDay_ResetsWindowAutomaticallyOnWrite()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.Track(Purchase(900, 50f));
            Assert.Equal(1, metrics.DailyPurchases);

            time.AdvanceDays(1);
            metrics.Track(Purchase(900, 30f));

            // 即使适配层忘了在零点调 ResetDaily，跨天后的写入也会先切窗口：当窗口从 1 重新开始。
            Assert.Equal(1, metrics.DailyPurchases);
            Assert.Equal(2, metrics.Snapshot().Purchases);
            Assert.Equal(30L, metrics.DailySoftSpent);
            Assert.Equal(80L, metrics.Snapshot().SoftSpent);
        }

        [Fact]
        public void MarkActive_OnNewDay_ClearsDailyWindow()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.AdCompleted, 1f, 0f, 0f, null));
            time.AdvanceDays(1);
            metrics.MarkActive();

            Assert.Equal(0, metrics.DailyAdsWatched);
            Assert.Equal(1, metrics.Snapshot().AdsWatched);
        }

        // ---------------------------------------------------------------- 快照与 CSV

        [Fact]
        public void Snapshot_IsAValueCopyAndDoesNotFollowLaterEvents()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            metrics.Track(Purchase(900, 10f));
            PlayerMetricsSnapshot snapshot = metrics.Snapshot();
            metrics.Track(Purchase(900, 90f));

            // 快照是值类型：拿到手的数字不该被后续事件改掉（看板 UI 与 CSV 导出都依赖这一点）。
            Assert.Equal(1, snapshot.Purchases);
            Assert.Equal(10L, snapshot.SoftSpent);
            Assert.Equal(2, metrics.Snapshot().Purchases);
            Assert.Equal(100L, metrics.Snapshot().SoftSpent);
        }

        [Fact]
        public void ToCsv_HasHeaderPlusExactlyOneDataRow()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            string[] lines = SplitCsv(metrics.ToCsv());

            Assert.Equal(2, lines.Length);
            Assert.Equal(PlayerMetrics.CsvHeader, lines[0]);
            Assert.Equal(PlayerMetricsSnapshot.CsvColumnCount, lines[1].Split(',').Length);
        }

        [Fact]
        public void ToCsv_DateColumnComesFromInjectedClock()
        {
            var metrics = new PlayerMetrics(new FakeTimeProvider(StartUtc));

            string[] lines = SplitCsv(metrics.ToCsv());

            // 日期列必须来自 ITimeProvider（AIF003），且是 UTC 日期：2026-11-20 13:00Z 就是 11-20。
            Assert.StartsWith("2026-11-20,", lines[1], StringComparison.Ordinal);
            Assert.Equal("2026-11-20", metrics.CurrentDateUtc);
        }

        [Fact]
        public void ToCsv_ReflectsAccumulatedMetricsInFixedColumnOrder()
        {
            var time = new FakeTimeProvider(StartUtc);
            var metrics = new PlayerMetrics(time);

            metrics.BeginSession();
            time.AdvanceSeconds(125);
            metrics.EndSession();
            metrics.MarkActive();
            metrics.Track(BattleStart(1, 10));
            metrics.Track(BattleStart(1, 10));
            metrics.Track(BattleEnd(won: true, durationSeconds: 30f));
            metrics.Track(Purchase(900, 300f));
            metrics.Track(ItemGranted(PlayerMetrics.DefaultSoftCurrencyItemId, 1000f));
            metrics.Track(new AnalyticsEvent(AnalyticsEventIds.AdCompleted, 1f, 0f, 0f, null));

            string row = SplitCsv(metrics.ToCsv())[1];
            string[] fields = row.Split(',');

            Assert.Equal(PlayerMetricsSnapshot.CsvColumnCount, fields.Length);
            Assert.Equal("2026-11-20", fields[0]);
            Assert.Equal("1", fields[1]);    // sessions
            Assert.Equal("125", fields[2]);  // playSeconds
            Assert.Equal("2", fields[3]);    // levelsStarted
            Assert.Equal("1", fields[4]);    // levelsWon
            Assert.Equal("1", fields[5]);    // purchases
            Assert.Equal("1", fields[6]);    // adsWatched
            Assert.Equal("300", fields[7]);  // softSpent
            Assert.Equal("1000", fields[8]); // softEarned
            Assert.Equal("1", fields[9]);    // retries
            Assert.Equal("1", fields[10]);   // dailyActiveDays
            Assert.Equal("1", fields[11]);   // currentStreakDays
        }

        [Fact]
        public void ToCsv_WhenClockThrows_WritesUnknownDateInsteadOfThrowing()
        {
            var time = new FakeTimeProvider(StartUtc) { ThrowOnUtcNow = true };
            var metrics = new PlayerMetrics(time);

            // 退出/上报路径上的导出绝不能因为时钟异常而抛：丢一整份指标比日期不可用严重得多。
            string csv = metrics.ToCsv();
            string[] lines = SplitCsv(csv);

            Assert.Equal(PlayerMetrics.UnknownDateText, metrics.CurrentDateUtc);
            Assert.StartsWith(PlayerMetrics.UnknownDateText + ",", lines[1], StringComparison.Ordinal);
            Assert.Equal(2, lines.Length);
        }

        [Fact]
        public void ToCsvRow_UsesInjectedDateAndFixedFieldOrder()
        {
            var snapshot = new PlayerMetricsSnapshot(
                sessions: 3,
                playSeconds: 125L,
                levelsStarted: 4,
                levelsWon: 2,
                purchases: 1,
                adsWatched: 7,
                softSpent: 300L,
                softEarned: 1000L,
                retries: 2,
                dailyActiveDays: 5,
                currentStreakDays: 2);

            // dateUtc 参数是"固定日期导出"的唯一入口：测试与补跑历史数据都不依赖真实时钟。
            Assert.Equal("2026-01-02,3,125,4,2,1,7,300,1000,2,5,2", snapshot.ToCsvRow("2026-01-02"));
        }

        [Fact]
        public void ToCsvRow_DefaultSnapshot_IsAllZeros()
        {
            var snapshot = default(PlayerMetricsSnapshot);

            Assert.Equal(
                "2026-01-02,0,0,0,0,0,0,0,0,0,0,0",
                snapshot.ToCsvRow("2026-01-02"));
        }

        [Fact]
        public void ToCsvRow_NullDate_Throws()
        {
            var snapshot = default(PlayerMetricsSnapshot);

            Assert.Throws<ArgumentNullException>(() => snapshot.ToCsvRow(null!));
        }

        [Fact]
        public void ToCsvRow_BlankDate_Throws()
        {
            var snapshot = default(PlayerMetricsSnapshot);

            Assert.Throws<ArgumentException>(() => snapshot.ToCsvRow("   "));
        }

        [Fact]
        public void ToCsvRow_DateWithComma_Throws()
        {
            var snapshot = default(PlayerMetricsSnapshot);

            // 本实现不做字段转义：宁可抛，也不要生成一行会被导入脚本拆错列的数据。
            Assert.Throws<ArgumentException>(() => snapshot.ToCsvRow("2026-01-02,extra"));
        }

        [Fact]
        public void ToCsvRow_DateWithNewline_Throws()
        {
            var snapshot = default(PlayerMetricsSnapshot);

            Assert.Throws<ArgumentException>(() => snapshot.ToCsvRow("2026-01-02\n2026-01-03"));
        }

        [Fact]
        public void CsvHeader_ColumnCountMatchesDeclaredConstant()
        {
            // 表头加列却不改 CsvColumnCount，会让 ConsoleMetricsExport 的读回校验静默放过错行。
            Assert.Equal(PlayerMetricsSnapshot.CsvColumnCount, PlayerMetricsSnapshot.CsvHeader.Split(',').Length);
            Assert.Equal(PlayerMetricsSnapshot.CsvHeader, PlayerMetrics.CsvHeader);
        }

        // ---------------------------------------------------------------- 辅助

        /// <summary>拆 CSV 行并去掉末尾空行（导出文本以 '\n' 结尾）。</summary>
        private static string[] SplitCsv(string csv)
        {
            return csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary><c>BattleStart</c>：value0 = 卡带类型，value1 = 关卡 id。</summary>
        private static AnalyticsEvent BattleStart(int cartridge, int levelId)
        {
            return new AnalyticsEvent(AnalyticsEventIds.BattleStart, cartridge, levelId, 0f, null);
        }

        /// <summary><c>BattleEnd</c>：value0 = 是否胜利，value1 = 时长秒。</summary>
        private static AnalyticsEvent BattleEnd(bool won, float durationSeconds)
        {
            return new AnalyticsEvent(AnalyticsEventIds.BattleEnd, won ? 1f : 0f, durationSeconds, 0f, null);
        }

        /// <summary><c>Purchase</c>：value0 = itemId，value1 = 花费。</summary>
        private static AnalyticsEvent Purchase(int itemId, float spent)
        {
            return new AnalyticsEvent(AnalyticsEventIds.Purchase, itemId, spent, 0f, null);
        }

        /// <summary><c>ItemGranted</c>：value0 = itemId，value1 = 数量。</summary>
        private static AnalyticsEvent ItemGranted(int itemId, float quantity)
        {
            return new AnalyticsEvent(AnalyticsEventIds.ItemGranted, itemId, quantity, 0f, null);
        }

        /// <summary>
        /// 可分别拨动"单调钟"与"UTC 墙钟"的假时间源，并可让任一路径抛异常（模拟时间源违约）。
        /// <para>
        /// 两个时钟必须能独立推进，才能验收"日切只看墙钟、时长只看单调钟"；
        /// 只提供一个 <c>Advance</c> 的假时间源会让这两个错误互相掩盖。
        /// </para>
        /// </summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow;

            public FakeTimeProvider(DateTime startUtc)
            {
                _utcNow = startUtc;
            }

            /// <summary>读单调钟是否抛异常（模拟时间源违约）。</summary>
            public bool ThrowOnElapsedSeconds { get; set; }

            /// <summary>读墙钟是否抛异常（模拟时间源违约）。</summary>
            public bool ThrowOnUtcNow { get; set; }

            public double ElapsedSeconds
            {
                get
                {
                    if (ThrowOnElapsedSeconds)
                    {
                        throw new InvalidOperationException("fake monotonic clock failure");
                    }

                    return _elapsedSeconds;
                }
            }

            public DateTime UtcNow
            {
                get
                {
                    if (ThrowOnUtcNow)
                    {
                        throw new InvalidOperationException("fake wall clock failure");
                    }

                    return _utcNow;
                }
            }

            /// <summary>同时推进单调钟与墙钟（= 正常流逝的时间）。</summary>
            public void AdvanceSeconds(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }

            /// <summary>推进整天：跨天用例的唯一时间入口。</summary>
            public void AdvanceDays(int days)
            {
                AdvanceSeconds(days * 86400d);
            }

            /// <summary>只推进单调钟（模拟"应用连续运行但 UTC 日期没变"或时钟回拨）。</summary>
            public void AdvanceMonotonicOnly(double seconds)
            {
                _elapsedSeconds += seconds;
            }

            /// <summary>只改墙钟（模拟玩家手动改系统时间）。</summary>
            public void AdvanceWallClockOnly(TimeSpan delta)
            {
                _utcNow = _utcNow.Add(delta);
            }
        }
    }
}
