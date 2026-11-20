using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Services;
using AIFramework.Core.Cqe;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleStorePurchaseFlow"/> 的端到端验收：真 <see cref="ConsoleIapService"/> +
    /// 真 <see cref="IapPurchaseLedger"/>（两份）+ 真 <see cref="CqeBus"/>，只有埋点是记录型替身。
    /// <para>
    /// <b>为什么这组用例必须存在</b>：Core 的协调器用"纯假商店"验的是规则，验不到**接线**。
    /// 假商店自己也会写台账，如果装配时把同一个台账实例同时交给商店和协调器，每笔购买都会被判成
    /// <c>receipt already consumed</c>——Core 的用例全绿，真机上却一件货都发不出去。这里正是那个坑的回归位。
    /// </para>
    /// </summary>
    public sealed class StorePurchaseFlowTests
    {
        /// <summary>去广告商品的道具 ID（与 <see cref="ConsoleIapService.NoAdsItemId"/> 同口径）。</summary>
        private const int NoAdsItemId = 2001;

        [Fact]
        public void Buy_Consumable_EndToEndGrantsTracksAndKeepsTwoSeparateLedgers()
        {
            var analytics = new RecordingAnalytics();
            var handler = new RecordingCommandHandler();
            var flow = CreateFlow(handler, analytics);

            StorePurchaseOutcome outcome = flow.Buy(ConsoleIapService.ProductGold60);

            Assert.True(outcome.Granted);
            Assert.Equal(ConsoleIapService.GoldItemId, outcome.ItemId);
            Assert.Equal(60, outcome.Count);

            // 发货指令确实进到了总线（这里就是背包切片将来接的位置）。
            GrantItemCommand command = Assert.Single(handler.Commands);
            Assert.Equal(ConsoleIapService.GoldItemId, command.ItemId);
            Assert.Equal(60, command.Count);
            Assert.Equal(MetaReasonTags.ShopGrant, command.ReasonTagId);

            // 游戏侧台账登记了，收据被消耗。
            Assert.Equal(1, flow.Ledger.RecordCount);

            // 商店侧也记了一笔（它自己那份台账），收据与游戏侧看到的是同一张：
            // 两份台账各自独立工作，正是这个用例要守住的东西。
            Assert.Contains("Purchased", string.Join(" | ", flow.DrainStoreLog()));

            AnalyticsEvent tracked = Assert.Single(analytics.Events);
            Assert.Equal(AnalyticsEventIds.IapPurchased, tracked.EventId);
            Assert.Equal(ConsoleIapService.ProductGold60, tracked.Label);
        }

        [Fact]
        public void Buy_NonConsumableTwice_SecondIsAlreadyOwnedAndNeverGrantsAgain()
        {
            // 这条同时钉住两件事：商店侧的"已拥有"预检，以及游戏侧的台账去重。
            var handler = new RecordingCommandHandler();
            var flow = CreateFlow(handler, new RecordingAnalytics());

            StorePurchaseOutcome first = flow.Buy(ConsoleIapService.ProductNoAds);
            StorePurchaseOutcome second = flow.Buy(ConsoleIapService.ProductNoAds);

            Assert.True(first.Granted);
            Assert.False(second.Granted);
            Assert.Equal(IapPurchaseState.AlreadyOwned.ToString(), second.Reason);
            Assert.Single(handler.Commands);
            Assert.Equal(NoAdsItemId, Assert.Single(handler.Commands).ItemId);
            Assert.Equal(1, flow.Ledger.RecordCount);
        }

        [Fact]
        public void Buy_UnknownProduct_ReturnsStoreUnavailableWithoutGranting()
        {
            var handler = new RecordingCommandHandler();
            var flow = CreateFlow(handler, new RecordingAnalytics());

            StorePurchaseOutcome outcome = flow.Buy("com.demo.unknown");

            Assert.False(outcome.Granted);
            Assert.Equal(IapPurchaseState.StoreUnavailable.ToString(), outcome.Reason);
            Assert.Empty(handler.Commands);
            Assert.Equal(0, flow.Ledger.RecordCount);
        }

        [Fact]
        public void Buy_OfflineStore_ReturnsStoreUnavailableWithoutGranting()
        {
            // 离线/未接线时商店如实回答不可用：链路必须原样透出，不能"假装成功"。
            var handler = new RecordingCommandHandler();
            var analytics = new RecordingAnalytics();
            var flow = CreateFlow(handler, analytics, storeAvailable: false);

            StorePurchaseOutcome outcome = flow.Buy(ConsoleIapService.ProductGold60);

            Assert.False(outcome.Granted);
            Assert.Equal(IapPurchaseState.StoreUnavailable.ToString(), outcome.Reason);
            Assert.Empty(handler.Commands);
            Assert.Empty(analytics.Events);
            Assert.Equal(0, flow.Ledger.RecordCount);
        }

        [Fact]
        public void Buy_ReceiptRejected_ReturnsInvalidReceiptWithoutGranting()
        {
            var handler = new RecordingCommandHandler();
            var flow = CreateFlow(handler, new RecordingAnalytics(), validator: new RejectAllValidator());

            StorePurchaseOutcome outcome = flow.Buy(ConsoleIapService.ProductGold60);

            Assert.False(outcome.Granted);
            Assert.Equal(IapPurchaseState.InvalidReceipt.ToString(), outcome.Reason);
            Assert.Empty(handler.Commands);
            Assert.Equal(0, flow.Ledger.RecordCount);
        }

        [Fact]
        public void Restore_AfterPurchaseMadeOutsideGame_GrantsExactlyOnce()
        {
            // 模拟"在别的设备买过 / 上次发到一半崩了"：商店侧有记录，游戏侧台账是空的。
            var handler = new RecordingCommandHandler();
            var analytics = new RecordingAnalytics();
            var flow = CreateFlow(handler, analytics);
            Assert.Equal(
                IapPurchaseState.Purchased,
                flow.Store.Purchase(ConsoleIapService.ProductNoAds).State);

            int restored = flow.Restore();

            Assert.Equal(1, restored);
            Assert.Single(handler.Commands);
            Assert.Equal(NoAdsItemId, Assert.Single(handler.Commands).ItemId);
            Assert.Equal(1, flow.Ledger.RecordCount);
            Assert.Single(analytics.Events);

            // 再点一次恢复：权益已在本地，不重复发、不重复记收入，但仍确认 1 件。
            Assert.Equal(1, flow.Restore());
            Assert.Single(handler.Commands);
            Assert.Single(analytics.Events);
        }

        [Fact]
        public void Restore_AlreadyGrantedByBuy_DoesNotGrantAgain()
        {
            var handler = new RecordingCommandHandler();
            var flow = CreateFlow(handler, new RecordingAnalytics());
            Assert.True(flow.Buy(ConsoleIapService.ProductNoAds).Granted);

            Assert.Equal(1, flow.Restore());
            Assert.Single(handler.Commands);
        }

        [Fact]
        public void Restore_WithoutAnyPurchase_ReturnsZero()
        {
            var flow = CreateFlow(new RecordingCommandHandler(), new RecordingAnalytics());

            Assert.Equal(0, flow.Restore());
        }

        [Fact]
        public void Buy_Override_FillsGrantMissingFromStoreCatalog()
        {
            // 目录只列 SKU 与价格（到账由调用点决定）：这是"产品 ID 直接映射道具"的真实用法。
            var handler = new RecordingCommandHandler();
            var flow = CreateFlow(
                handler,
                new RecordingAnalytics(),
                catalog: new[]
                {
                    new IapProduct("com.demo.custom", IapProductKind.Consumable, "¥6.00", 0, 0)
                });

            StorePurchaseOutcome outcome = flow.Buy("com.demo.custom", 5001, 5);

            Assert.True(outcome.Granted);
            Assert.Equal(5001, outcome.ItemId);
            Assert.Equal(5, outcome.Count);
            Assert.Equal(5001, Assert.Single(handler.Commands).ItemId);
        }

        [Fact]
        public void Buy_NoAnalyticsWired_StillClosesLoop()
        {
            var handler = new RecordingCommandHandler();
            var flow = CreateFlow(handler, analytics: null);

            Assert.True(flow.Buy(ConsoleIapService.ProductGold300).Granted);
            Assert.Single(handler.Commands);
        }

        [Fact]
        public void Catalog_ReturnsDefensiveCopyAndMirrorsStoreCatalog()
        {
            var flow = CreateFlow(new RecordingCommandHandler(), new RecordingAnalytics());

            IapProduct[] catalog = flow.Catalog;
            Assert.Equal(3, catalog.Length);
            Assert.Equal(ConsoleIapService.ProductGold60, catalog[0].ProductId);

            // 外部改写返回数组不得影响协调器，也不得影响商店的购买行为。
            catalog[0] = new IapProduct("com.demo.hacked", IapProductKind.Consumable, "$0", 0, 0);
            Assert.Equal(ConsoleIapService.ProductGold60, flow.Catalog[0].ProductId);
            Assert.Equal(
                IapPurchaseState.Purchased,
                flow.Store.Purchase(ConsoleIapService.ProductGold60).State);
        }

        [Fact]
        public void Constructor_NullBus_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ConsoleStorePurchaseFlow(null!));
        }

        /// <summary>装配一次真实接线：真总线 + 真商店 + 两份真台账，只有埋点是替身。</summary>
        private static ConsoleStorePurchaseFlow CreateFlow(
            RecordingCommandHandler handler,
            IAnalyticsService? analytics,
            bool storeAvailable = true,
            IapProduct[]? catalog = null,
            IReceiptValidator? validator = null)
        {
            var bus = new CqeBus();
            bus.RegisterCommandHandler<GrantItemCommand>(handler);
            return new ConsoleStorePurchaseFlow(bus, analytics, storeAvailable, catalog, validator);
        }

        /// <summary>记录型发奖处理器。</summary>
        private sealed class RecordingCommandHandler : ICommandHandler<GrantItemCommand>
        {
            /// <summary>收到的指令（按顺序）。</summary>
            internal List<GrantItemCommand> Commands { get; } = new List<GrantItemCommand>();

            /// <inheritdoc />
            public void Handle(in GrantItemCommand command)
            {
                Commands.Add(command);
            }
        }

        /// <summary>记录型埋点。</summary>
        private sealed class RecordingAnalytics : IAnalyticsService
        {
            /// <summary>收到的事件（按顺序）。</summary>
            internal List<AnalyticsEvent> Events { get; } = new List<AnalyticsEvent>();

            /// <inheritdoc />
            public bool IsAvailable
            {
                get { return true; }
            }

            /// <inheritdoc />
            public long AcceptedCount
            {
                get { return Events.Count; }
            }

            /// <inheritdoc />
            public long DroppedCount
            {
                get { return 0; }
            }

            /// <inheritdoc />
            public void Track(in AnalyticsEvent analyticsEvent)
            {
                Events.Add(analyticsEvent);
            }

            /// <inheritdoc />
            public void Flush()
            {
            }
        }

        /// <summary>拒绝一切收据的校验器（验收 InvalidReceipt 映射）。</summary>
        private sealed class RejectAllValidator : IReceiptValidator
        {
            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                return false;
            }
        }
    }
}
