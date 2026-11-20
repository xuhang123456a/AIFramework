using System;
using AIFramework.Contracts.Meta;
using AIFramework.Features.LimitedOffer;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.LimitedOffer
{
    /// <summary>
    /// 限时礼包行为验收：左闭右开窗口、三态展示、实付价向下取整边界（1% / 100% / 奇数）、
    /// 每日限购与 UTC 跨日重置、总限购、成交时"发货指令 + 成交事件"各一次、失败零副作用、
    /// 空表退化、快照时间来自注入的时间源、null 依赖与重复注册。
    /// <para>所有用例都用可快进假时间源，不碰真实时钟（AIF003）。</para>
    /// </summary>
    public sealed class LimitedOfferHandlerTests
    {
        /// <summary>默认锚点折算成 int32 Unix 秒（表内时间列是 int32）。</summary>
        private static readonly int AnchorUnix =
            LimitedOfferHarness.ToUnixSecondsInt(LimitedOfferHarness.DefaultNow);

        private const int DaySeconds = 86400;

        /// <summary>发货原因标签（与 CONTRACT.md §2 的 6901 对齐）。</summary>
        private const int ExpectedGrantReasonTagId = 6901;

        private static LimitedOfferRowSpec Row(
            int offerId,
            int startOffsetSeconds,
            int endOffsetSeconds,
            int discountPercent = 50,
            int dailyLimit = 1,
            int totalLimit = 10,
            int originalPrice = 1000,
            int itemId = 1001,
            int grantCount = 1,
            int priceItemId = 1,
            string name = "测试礼包")
        {
            return new LimitedOfferRowSpec(
                offerId,
                name,
                itemId,
                grantCount,
                priceItemId,
                originalPrice,
                discountPercent,
                AnchorUnix + startOffsetSeconds,
                AnchorUnix + endOffsetSeconds,
                dailyLimit,
                totalLimit);
        }

        private static LimitedOfferHarness Create(params LimitedOfferRowSpec[] rows)
        {
            return LimitedOfferHarness.Create(LimitedOfferTableStub.WithRows(rows));
        }

        [Fact]
        public void Query_ThreeWindowStates_MarksOnlyOngoingOpen()
        {
            LimitedOfferHarness harness = Create(
                Row(3001, -7200, -3600, name: "已结束"),
                Row(3002, -3600, 3600, name: "进行中"),
                Row(3003, 3600, 7200, name: "未开始"));

            LimitedOfferSnapshot snapshot = harness.Query();

            Assert.Equal(1, snapshot.OpenCount);
            Assert.False(snapshot.Offers[0].IsOpen);
            Assert.True(snapshot.Offers[1].IsOpen);
            Assert.False(snapshot.Offers[2].IsOpen);

            // 剩余秒只在开启时有值：未开始与已结束恒为 0。
            Assert.Equal(0L, snapshot.Offers[0].SecondsRemaining);
            Assert.Equal(3600L, snapshot.Offers[1].SecondsRemaining);
            Assert.Equal(0L, snapshot.Offers[2].SecondsRemaining);
        }

        [Fact]
        public void Window_StartBoundary_IsInclusive()
        {
            // start == now：左闭，开始那一秒已经可以买。
            LimitedOfferHarness harness = Create(Row(3001, 0, 60));

            Assert.True(harness.Query().Offers[0].IsOpen);
            Assert.True(harness.Purchase(3001).IsSuccess);
        }

        [Fact]
        public void Window_EndBoundary_IsExclusive()
        {
            // end == now：右开，结束那一秒已经不可买（相邻两期礼包不会在同一秒同时可买）。
            LimitedOfferHarness harness = Create(Row(3001, -60, 0));

            Assert.False(harness.Query().Offers[0].IsOpen);

            LimitedOfferPurchaseOutcome outcome = harness.Purchase(3001);
            Assert.Equal(LimitedOfferPurchaseResult.NotOpen, outcome.Result);
            Assert.False(outcome.IsSuccess);
        }

        [Fact]
        public void Window_LastSecondBeforeEnd_StaysOpenThenCloses()
        {
            LimitedOfferHarness harness = Create(Row(3001, 0, 60));

            harness.AdvanceSeconds(59);
            Assert.True(harness.Query().Offers[0].IsOpen);
            Assert.Equal(1L, harness.Query().Offers[0].SecondsRemaining);

            harness.AdvanceSeconds(1);
            Assert.False(harness.Query().Offers[0].IsOpen);
            Assert.Equal(0L, harness.Query().Offers[0].SecondsRemaining);
        }

        [Fact]
        public void Snapshot_ExposesAllFieldsOfRow()
        {
            LimitedOfferHarness harness = Create(Row(
                3101,
                -60,
                3600,
                discountPercent: 40,
                dailyLimit: 3,
                totalLimit: 9,
                originalPrice: 1280,
                itemId: 5001,
                grantCount: 5,
                priceItemId: 2,
                name: "周卡·折扣包"));

            LimitedOfferSnapshot snapshot = harness.Query();

            Assert.Single(snapshot.Offers);
            LimitedOfferState state = snapshot.Offers[0];
            Assert.Equal(3101, state.OfferId);
            Assert.Equal("周卡·折扣包", state.Name);
            Assert.True(state.IsOpen);
            Assert.Equal(512, state.FinalPrice);
            Assert.Equal(1280, state.OriginalPrice);
            Assert.Equal(40, state.DiscountPercent);
            Assert.Equal(3, state.DailyRemaining);
            Assert.Equal(9, state.TotalRemaining);
            Assert.Equal(3600L, state.SecondsRemaining);
            Assert.False(state.IsDailyUnlimited);
            Assert.Equal(1, snapshot.OpenCount);
        }

        [Fact]
        public void Snapshot_NowUnixSeconds_ComesFromInjectedTimeProvider()
        {
            LimitedOfferHarness harness = Create(Row(3001, -60, 3600));

            Assert.Equal(LimitedOfferHarness.ToUnixSeconds(LimitedOfferHarness.DefaultNow), harness.Query().NowUnixSeconds);

            harness.AdvanceSeconds(30);

            Assert.Equal(
                LimitedOfferHarness.ToUnixSeconds(LimitedOfferHarness.DefaultNow) + 30,
                harness.Query().NowUnixSeconds);
        }

        [Fact]
        public void Snapshot_OpenCount_CountsOnlyOpenOffers()
        {
            LimitedOfferHarness harness = Create(
                Row(3001, -600, 600),
                Row(3002, -60, 60),
                Row(3003, 60, 120),
                Row(3004, -3600, 3600));

            LimitedOfferSnapshot snapshot = harness.Query();

            Assert.Equal(4, snapshot.Offers.Length);
            Assert.Equal(3, snapshot.OpenCount);
        }

        [Fact]
        public void Snapshot_EmptyTable_ReturnsEmptySnapshot()
        {
            LimitedOfferHarness harness = Create();

            LimitedOfferSnapshot snapshot = harness.Query();

            Assert.Empty(snapshot.Offers);
            Assert.Equal(0, snapshot.OpenCount);
            Assert.Equal(LimitedOfferHarness.ToUnixSeconds(LimitedOfferHarness.DefaultNow), snapshot.NowUnixSeconds);
            Assert.Equal(0, harness.Handler.Count);
        }

        [Fact]
        public void FinalPrice_FullPercent_EqualsOriginalPrice()
        {
            LimitedOfferHarness harness = Create(Row(3001, -60, 3600, discountPercent: 100, originalPrice: 1280));

            Assert.Equal(1280, harness.Query().Offers[0].FinalPrice);
            Assert.Equal(1280, LimitedOfferHandler.ComputeFinalPrice(1280, 100));
        }

        [Fact]
        public void FinalPrice_TenPercentOf1280_Is128()
        {
            LimitedOfferHarness harness = Create(Row(3001, -60, 3600, discountPercent: 10, originalPrice: 1280));

            Assert.Equal(128, harness.Query().Offers[0].FinalPrice);
        }

        [Fact]
        public void FinalPrice_OnePercent_ClampsToOne()
        {
            // 99 × 1% = 0.99 → 向下取整 0 → 必须夹到 1，否则就是 0 元购。
            Assert.Equal(1, LimitedOfferHandler.ComputeFinalPrice(99, 1));

            LimitedOfferHarness harness = Create(Row(3001, -60, 3600, discountPercent: 1, originalPrice: 99));
            LimitedOfferPurchaseOutcome outcome = harness.Purchase(3001);

            Assert.True(outcome.IsSuccess);
            Assert.Equal(1, outcome.FinalPrice);
            Assert.Equal(1, harness.PurchasedEvents()[0].FinalPrice);
        }

        [Fact]
        public void FinalPrice_OddPercent_TruncatesFraction()
        {
            // 999 × 33% = 329.67 → 向下取整 329（不是四舍五入的 330）。
            Assert.Equal(329, LimitedOfferHandler.ComputeFinalPrice(999, 33));

            LimitedOfferHarness harness = Create(Row(3001, -60, 3600, discountPercent: 33, originalPrice: 999));
            Assert.Equal(329, harness.Query().Offers[0].FinalPrice);
        }

        [Fact]
        public void ComputeFinalPrice_InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LimitedOfferHandler.ComputeFinalPrice(0, 50));
            Assert.Throws<ArgumentOutOfRangeException>(() => LimitedOfferHandler.ComputeFinalPrice(-1, 50));
            Assert.Throws<ArgumentOutOfRangeException>(() => LimitedOfferHandler.ComputeFinalPrice(100, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => LimitedOfferHandler.ComputeFinalPrice(100, 101));
        }

        [Fact]
        public void Purchase_Success_SendsGrantCommandAndPublishesEventOnce()
        {
            LimitedOfferHarness harness = Create(Row(
                3002,
                -3600,
                3600,
                discountPercent: 50,
                originalPrice: 500,
                itemId: 1002,
                grantCount: 5,
                priceItemId: 1));

            // 走真实命令链路（验证 Register 注册的命令处理器与逐条副作用）。
            harness.SendPurchase(3002);

            GrantItemCommand grant = Assert.Single(harness.GrantCommands());
            Assert.Equal(1002, grant.ItemId);
            Assert.Equal(5, grant.Count);
            Assert.Equal(ExpectedGrantReasonTagId, grant.ReasonTagId);

            LimitedOfferPurchasedEvent purchased = Assert.Single(harness.PurchasedEvents());
            Assert.Equal(3002, purchased.OfferId);
            Assert.Equal(1002, purchased.ItemId);
            Assert.Equal(5, purchased.GrantCount);
            Assert.Equal(250, purchased.FinalPrice);
        }

        [Fact]
        public void Purchase_Success_OutcomeCarriesDealTerms()
        {
            LimitedOfferHarness harness = Create(Row(
                3002,
                -3600,
                3600,
                discountPercent: 50,
                originalPrice: 500,
                itemId: 1002,
                grantCount: 5));

            LimitedOfferPurchaseOutcome outcome = harness.Purchase(3002);

            Assert.Equal(LimitedOfferPurchaseResult.Success, outcome.Result);
            Assert.True(outcome.IsSuccess);
            Assert.Equal(3002, outcome.OfferId);
            Assert.Equal(1002, outcome.ItemId);
            Assert.Equal(5, outcome.GrantCount);
            Assert.Equal(250, outcome.FinalPrice);
            Assert.Equal(string.Empty, outcome.Reason);
        }

        [Fact]
        public void Purchase_Twice_PublishesOneEventPerPurchase()
        {
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3600, dailyLimit: 3, totalLimit: 10));

            Assert.True(harness.Purchase(3001).IsSuccess);
            Assert.True(harness.Purchase(3001).IsSuccess);

            Assert.Equal(2, harness.PurchasedEvents().Count);
            Assert.Equal(2, harness.GrantCommands().Count);

            // 查询不会额外发事件：事件只由成交产生。
            harness.Query();
            Assert.Equal(2, harness.PurchasedEvents().Count);
        }

        [Fact]
        public void Purchase_NotStarted_ReturnsNotOpenWithoutSideEffects()
        {
            LimitedOfferHarness harness = Create(Row(3001, 3600, 7200, discountPercent: 50, originalPrice: 500));

            LimitedOfferPurchaseOutcome outcome = harness.Purchase(3001);

            Assert.Equal(LimitedOfferPurchaseResult.NotOpen, outcome.Result);
            Assert.Equal(3001, outcome.OfferId);
            Assert.Equal(250, outcome.FinalPrice); // 失败也回显报价，方便 UI 说明"过期就这个价"            Assert.Contains("窗口", outcome.Reason, StringComparison.Ordinal);

            Assert.Empty(harness.PurchasedEvents());
            Assert.Empty(harness.GrantCommands());
            Assert.Equal(1, harness.Query().Offers[0].DailyRemaining);
        }

        [Fact]
        public void Purchase_Ended_ReturnsNotOpenWithoutSideEffects()
        {
            LimitedOfferHarness harness = Create(Row(3001, -7200, -3600));

            LimitedOfferPurchaseOutcome outcome = harness.Purchase(3001);

            Assert.Equal(LimitedOfferPurchaseResult.NotOpen, outcome.Result);
            Assert.Empty(harness.PurchasedEvents());
            Assert.Empty(harness.GrantCommands());
        }

        [Fact]
        public void Purchase_UnknownOffer_ReturnsUnknownOfferWithoutSideEffects()
        {
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3600));

            LimitedOfferPurchaseOutcome outcome = harness.Purchase(9999);

            Assert.Equal(LimitedOfferPurchaseResult.UnknownOffer, outcome.Result);
            Assert.Equal(9999, outcome.OfferId);
            Assert.Equal(0, outcome.ItemId);
            Assert.Equal(0, outcome.GrantCount);
            Assert.Equal(0, outcome.FinalPrice);
            Assert.Contains("TbLimitedOffer", outcome.Reason, StringComparison.Ordinal);

            Assert.Empty(harness.PurchasedEvents());
            Assert.Empty(harness.GrantCommands());
        }

        [Fact]
        public void Purchase_EmptyTable_ReturnsUnavailable()
        {
            LimitedOfferHarness harness = Create();

            LimitedOfferPurchaseOutcome outcome = harness.Purchase(3001);

            Assert.Equal(LimitedOfferPurchaseResult.Unavailable, outcome.Result);
            Assert.Equal(0, outcome.ItemId);
            Assert.Contains("为空", outcome.Reason, StringComparison.Ordinal);

            Assert.Empty(harness.PurchasedEvents());
            Assert.Empty(harness.GrantCommands());
        }

        [Fact]
        public void Purchase_DailyLimit_RejectsThirdAttempt()
        {
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3600, dailyLimit: 2, totalLimit: 10));

            Assert.True(harness.Purchase(3001).IsSuccess);
            Assert.True(harness.Purchase(3001).IsSuccess);

            LimitedOfferPurchaseOutcome rejected = harness.Purchase(3001);

            Assert.Equal(LimitedOfferPurchaseResult.LimitReached, rejected.Result);
            Assert.Contains("今日限购", rejected.Reason, StringComparison.Ordinal);
            Assert.Equal(2, harness.PurchasedEvents().Count);
            Assert.Equal(2, harness.GrantCommands().Count);

            LimitedOfferState state = harness.Query().Offers[0];
            Assert.Equal(0, state.DailyRemaining);
            Assert.Equal(8, state.TotalRemaining);
        }

        [Fact]
        public void Purchase_DailyLimit_ResetsOnNextUtcDay()
        {
            // 窗口跨两天（-1h .. +2d），否则推进一天后会先被判成 NotOpen，测不到跨日重置。
            LimitedOfferHarness harness = Create(Row(3001, -3600, 2 * DaySeconds, dailyLimit: 1, totalLimit: 10));

            Assert.True(harness.Purchase(3001).IsSuccess);
            Assert.Equal(LimitedOfferPurchaseResult.LimitReached, harness.Purchase(3001).Result);

            // 从 08:00 推进到次日 00:00（UTC 自然日切换，只有 16 小时）。
            harness.AdvanceSeconds(16 * 3600);

            // 查询本身只读，但跨日后按"今天已购 0"折算：每日限购自动回满。
            Assert.Equal(1, harness.Query().Offers[0].DailyRemaining);

            Assert.True(harness.Purchase(3001).IsSuccess);
            Assert.Equal(2, harness.PurchasedEvents().Count);
            Assert.Equal(8, harness.Query().Offers[0].TotalRemaining);
        }

        [Fact]
        public void Purchase_DailyLimitZero_MeansUnlimited()
        {
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3600, dailyLimit: 0, totalLimit: 10));

            Assert.True(harness.Purchase(3001).IsSuccess);
            Assert.True(harness.Purchase(3001).IsSuccess);
            Assert.True(harness.Purchase(3001).IsSuccess);

            LimitedOfferState state = harness.Query().Offers[0];
            Assert.Equal(LimitedOfferState.UnlimitedRemaining, state.DailyRemaining);
            Assert.True(state.IsDailyUnlimited);
            Assert.Equal(7, state.TotalRemaining);
            Assert.Equal(3, harness.PurchasedEvents().Count);
        }

        [Fact]
        public void Purchase_TotalLimit_EnforcedAcrossDays()
        {
            // dailyLimit = 0（不限当日）+ totalLimit = 2：第二天也不会把总额度还回来。
            // 窗口要给够两天（-1h .. +3d），否则推进一天后会先被判成 NotOpen。
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3 * DaySeconds, dailyLimit: 0, totalLimit: 2));

            Assert.True(harness.Purchase(3001).IsSuccess);
            Assert.True(harness.Purchase(3001).IsSuccess);

            LimitedOfferPurchaseOutcome sameDay = harness.Purchase(3001);
            Assert.Equal(LimitedOfferPurchaseResult.LimitReached, sameDay.Result);
            Assert.Contains("总限购", sameDay.Reason, StringComparison.Ordinal);

            harness.AdvanceDays(1);

            LimitedOfferPurchaseOutcome nextDay = harness.Purchase(3001);
            Assert.Equal(LimitedOfferPurchaseResult.LimitReached, nextDay.Result);
            Assert.Contains("总限购", nextDay.Reason, StringComparison.Ordinal);

            Assert.Equal(2, harness.PurchasedEvents().Count);
            Assert.Equal(0, harness.Query().Offers[0].TotalRemaining);
        }

        [Fact]
        public void Purchase_TotalLimitZero_RejectsImmediately()
        {
            // totalLimit = 0 语义是"一次都不能买"（与 dailyLimit = 0 的"不限"刻意不对称）。
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3600, dailyLimit: 1, totalLimit: 0));

            LimitedOfferPurchaseOutcome outcome = harness.Purchase(3001);

            Assert.Equal(LimitedOfferPurchaseResult.LimitReached, outcome.Result);
            Assert.Contains("总限购", outcome.Reason, StringComparison.Ordinal);
            Assert.Equal(0, harness.Query().Offers[0].TotalRemaining);
            Assert.Empty(harness.PurchasedEvents());
        }

        [Fact]
        public void Snapshot_RemainingDecrementsAfterPurchases()
        {
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3600, dailyLimit: 3, totalLimit: 10));

            Assert.Equal(3, harness.Query().Offers[0].DailyRemaining);
            Assert.Equal(10, harness.Query().Offers[0].TotalRemaining);

            Assert.True(harness.Purchase(3001).IsSuccess);

            Assert.Equal(2, harness.Query().Offers[0].DailyRemaining);
            Assert.Equal(9, harness.Query().Offers[0].TotalRemaining);
        }

        [Fact]
        public void Purchase_DoesNotConsumeCurrency()
        {
            // 边界：本切片不查余额、不发 ConsumeItemCommand（扣款由装配层/经济系统桥接）。
            LimitedOfferHarness harness = Create(Row(3001, -3600, 3600));

            Assert.True(harness.Purchase(3001).IsSuccess);

            Assert.Equal(0, harness.Bus.CountSent<ConsumeItemCommand>());
            Assert.Equal(1, harness.Bus.CountSent<GrantItemCommand>());
            Assert.Equal(0, harness.Bus.CountQueried<InventoryQueryRequest>());
        }

        [Fact]
        public void Constructor_NullBus_Throws()
        {
            LimitedOfferTableStub loader = LimitedOfferTableStub.WithRows(Row(3001, -60, 60));
            var time = new FakeTimeProvider(LimitedOfferHarness.DefaultNow);

            Assert.Throws<ArgumentNullException>(() => new LimitedOfferHandler(null!, time, loader));
        }

        [Fact]
        public void Constructor_NullTime_Throws()
        {
            LimitedOfferTableStub loader = LimitedOfferTableStub.WithRows(Row(3001, -60, 60));
            var bus = new RecordingBus();

            Assert.Throws<ArgumentNullException>(() => new LimitedOfferHandler(bus, null!, loader));
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            var bus = new RecordingBus();
            var time = new FakeTimeProvider(LimitedOfferHarness.DefaultNow);

            Assert.Throws<ArgumentNullException>(() => new LimitedOfferHandler(bus, time, null!));
        }

        [Fact]
        public void Register_NullBus_Throws()
        {
            LimitedOfferHarness harness = Create(Row(3001, -60, 60));

            Assert.Throws<ArgumentNullException>(() => harness.Handler.Register(null!));
        }

        [Fact]
        public void Register_Twice_Throws()
        {
            LimitedOfferHarness harness = Create(Row(3001, -60, 60));

            // 同一消息类型只允许一个处理器：重复注册必须当场炸，而不是静默覆盖。
            Assert.Throws<InvalidOperationException>(() => harness.Handler.Register(harness.Bus));
        }
    }
}
