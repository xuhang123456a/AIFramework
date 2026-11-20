using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Services;
using AIFramework.Core.Cqe;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="StorePurchaseCoordinator"/> 的行为契约验收：商店 → 台账 → 发奖指令 → 埋点这条链路上，
    /// 每一步的"做/不做"都被逐条钉死。
    /// <para>
    /// <b>这些用例守的是钱</b>：绿一次不代表逻辑漂亮，只代表"重复收据不会再发一份""取消不会被当成功"
    /// "恢复购买不会把已经到账的东西再发一遍"这三条在改动后依然成立。任何一条放松，线上就是资损或玩家投诉。
    /// </para>
    /// <para>
    /// 替身刻意只用真实现（<see cref="CqeBus"/> + <see cref="IapPurchaseLedger"/>）加最小假商店/假埋点：
    /// 总线在这里必须是真的，因为"未注册处理器会抛异常"正是要被验收的行为之一；
    /// 台账也必须是真的，因为防双发的闸门就在台账里，用假台账等于没测。
    /// </para>
    /// </summary>
    public sealed class StorePurchaseCoordinatorTests
    {
        /// <summary>测试用合法收据。</summary>
        private const string Receipt = "receipt-1";

        private static readonly IapProduct Gold60 =
            new IapProduct("com.demo.gold60", IapProductKind.Consumable, "$0.99", 1001, 60);

        private static readonly IapProduct Gold300 =
            new IapProduct("com.demo.gold300", IapProductKind.Consumable, "$4.99", 1001, 300);

        private static readonly IapProduct NoAds =
            new IapProduct("com.demo.no_ads", IapProductKind.NonConsumable, "$2.99", 2001, 1);

        private static readonly IapProduct Vip =
            new IapProduct("com.demo.vip", IapProductKind.Subscription, "$9.99", 0, 0);

        [Fact]
        public void Constructor_NullDependencies_Throw()
        {
            // 三个端口都是硬依赖：缺任何一个都无法完成"扣款→去重→发货"的闭环，属于装配期编程错误。
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            var bus = new CqeBus();
            var store = new FakeStore();

            Assert.Throws<ArgumentNullException>(() => new StorePurchaseCoordinator(null!, ledger, bus));
            Assert.Throws<ArgumentNullException>(() => new StorePurchaseCoordinator(store, null!, bus));
            Assert.Throws<ArgumentNullException>(() => new StorePurchaseCoordinator(store, ledger, null!));
        }

        [Fact]
        public void Purchase_Consumable_ClosesLoopWithOneCommandAndOneEvent()
        {
            var analytics = new RecordingAnalytics();
            var harness = new Harness(analytics: analytics);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            // 闭环结果：道具与数量就是目录配置的那一份。
            Assert.True(outcome.Granted);
            Assert.Equal(1001, outcome.ItemId);
            Assert.Equal(60, outcome.Count);
            Assert.Equal(StorePurchaseCoordinator.ReasonGranted, outcome.Reason);

            // 商店只被问一次，且问的是这个 SKU。
            Assert.Equal(1, harness.Store.PurchaseCallCount);
            Assert.Equal(Gold60.ProductId, harness.Store.LastProductId);

            // 发货指令确实发出去了，且带的是商品配置值 + 商店发货原因标签。
            GrantItemCommand command = Assert.Single(harness.Handler.Commands);
            Assert.Equal(1001, command.ItemId);
            Assert.Equal(60, command.Count);
            Assert.Equal(MetaReasonTags.ShopGrant, command.ReasonTagId);

            // 台账登记一次，收据被消耗（防双发的唯一依据）。
            Assert.Equal(1, harness.Ledger.RecordCount);
            Assert.True(harness.Ledger.IsReceiptConsumed(Receipt));

            // 埋点恰好 1 条。
            AnalyticsEvent tracked = Assert.Single(analytics.Events);
            Assert.Equal(AnalyticsEventIds.IapPurchased, tracked.EventId);
            Assert.Equal(1001f, tracked.Value0);
            Assert.Equal(60f, tracked.Value1);
            Assert.Equal(Gold60.ProductId, tracked.Label);
        }

        [Fact]
        public void Purchase_GrantCommand_UsesShopGrantReasonTag()
        {
            var harness = new Harness();

            harness.Coordinator.Purchase(Gold60.ProductId);

            // 契约层没有 IAP 专用原因标签，本任务沿用 6102（商店：发货）。这条用例把口径钉死：
            // 将来若在 Contracts 追加 6103，改这里一处即可，测试会立刻告诉接线方"标签变了"。
            Assert.Equal(6102, StorePurchaseCoordinator.GrantReasonTagId);
            Assert.Equal(StorePurchaseCoordinator.GrantReasonTagId, MetaReasonTags.ShopGrant);
            Assert.Equal(MetaReasonTags.ShopGrant, Assert.Single(harness.Handler.Commands).ReasonTagId);
        }

        [Theory]
        [InlineData(IapPurchaseState.AlreadyOwned)]
        [InlineData(IapPurchaseState.Cancelled)]
        [InlineData(IapPurchaseState.StoreUnavailable)]
        [InlineData(IapPurchaseState.InvalidReceipt)]
        [InlineData(IapPurchaseState.Pending)]
        public void Purchase_NonPurchasedState_DoesNotGrantTrackOrRecord(IapPurchaseState state)
        {
            // 五种"不是成功"的状态：已拥有（不重复发）、用户取消（不是异常）、商店不可用（稍后重试）、
            // 收据无效（安全事件）、待处理（家长审批/延迟确认，结果未定）。它们唯一的共同点是：都不能发货。
            var analytics = new RecordingAnalytics();
            var harness = new Harness(analytics: analytics);
            harness.Store.State = state;

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.False(outcome.Granted);
            Assert.Equal(0, outcome.ItemId);
            Assert.Equal(0, outcome.Count);

            // 原因必须是原始 state 名：UI 靠它分流（已拥有=提示、取消=静默、待处理=稍后恢复）。
            Assert.Equal(state.ToString(), outcome.Reason);

            Assert.Empty(harness.Handler.Commands);
            Assert.Empty(analytics.Events);
            Assert.Equal(0, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Purchase_SameReceiptTwice_SecondCallIsRejectedAndNotGrantedAgain()
        {
            // 真实商店偶发重复回调、玩家也会重放收据：第二张"同号收据"必须被台账拦下。
            var analytics = new RecordingAnalytics();
            var harness = new Harness(analytics: analytics);

            StorePurchaseOutcome first = harness.Coordinator.Purchase(Gold60.ProductId);
            StorePurchaseOutcome second = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.True(first.Granted);
            Assert.False(second.Granted);
            Assert.Equal(IapPurchaseLedger.ReasonReceiptConsumed, second.Reason);
            Assert.Equal(0, second.ItemId);

            // 全程只发一次货、只记一条收入。
            Assert.Single(harness.Handler.Commands);
            Assert.Single(analytics.Events);
            Assert.Equal(1, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Purchase_NonConsumableTwice_SecondCallIsRejectedAsAlreadyOwned()
        {
            // 非消耗品换一张新收据再买（不同交易号）也不允许二次发货：拥有型商品只发一次。
            var harness = new Harness();
            Assert.True(harness.Coordinator.Purchase(NoAds.ProductId).Granted);

            harness.Store.Receipt = "receipt-2";
            StorePurchaseOutcome second = harness.Coordinator.Purchase(NoAds.ProductId);

            Assert.False(second.Granted);
            Assert.Equal(IapPurchaseLedger.ReasonAlreadyOwned, second.Reason);
            Assert.Single(harness.Handler.Commands);
            Assert.Equal(1, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Purchase_ReceiptRejectedByValidator_DoesNotGrant()
        {
            // 商店说 Purchased，但收据校验不过（伪造/过期）：台账拒收，绝不放行发货。
            var validator = new ToggleValidator { Accept = false };
            var harness = new Harness(validator: validator);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.False(outcome.Granted);
            Assert.Equal(IapPurchaseLedger.ReasonReceiptRejected, outcome.Reason);
            Assert.Empty(harness.Handler.Commands);
            Assert.Equal(0, harness.Ledger.RecordCount);
            Assert.False(harness.Ledger.IsReceiptConsumed(Receipt));
        }

        [Fact]
        public void Purchase_StoreThrows_ReturnsFailedOutcomeWithoutThrowing()
        {
            // 契约要求商店实现永不抛异常；实现违约时协调器必须自己兜住，绝不能把异常穿透到 UI。
            var harness = new Harness();
            harness.Store.ThrowOnPurchase = true;

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.False(outcome.Granted);
            Assert.Equal(StorePurchaseCoordinator.ReasonStoreFailed, outcome.Reason);
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Purchase_EmptyProductId_DoesNotReachStore()
        {
            var harness = new Harness();

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(string.Empty);

            // 空 SKU 不可能是合法商品：连平台调用都不该发生（有些 SDK 会为无效 ID 记一次失败埋点）。
            Assert.False(outcome.Granted);
            Assert.Equal(StorePurchaseCoordinator.ReasonInvalidProduct, outcome.Reason);
            Assert.Equal(0, harness.Store.PurchaseCallCount);
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Purchase_UnknownProduct_DoesNotConsumeReceipt()
        {
            // 商店扣款成功、但目录里没有这件商品（客户端过旧/配置漏了）：不知道发什么。
            // 此时**不能登记**——收据一旦被台账消耗就永远发不出货；不登记至少还能补单。
            var harness = new Harness();

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase("com.demo.ghost");

            Assert.False(outcome.Granted);
            Assert.Equal(StorePurchaseCoordinator.ReasonUnknownProduct, outcome.Reason);
            Assert.Equal(0, harness.Ledger.RecordCount);
            Assert.False(harness.Ledger.IsReceiptConsumed(Receipt));
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Purchase_ProductWithoutGrant_RecordsWithoutSendingCommand()
        {
            // 订阅/功能解锁这类"商业上成功、但没有道具可发"的商品：台账要登记（否则每次恢复都在补发），
            // 指令不能发（0 数量指令只会制造噪音），结果仍是成功。
            var analytics = new RecordingAnalytics();
            var harness = new Harness(analytics: analytics);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Vip.ProductId);

            Assert.True(outcome.Granted);
            Assert.Equal(0, outcome.ItemId);
            Assert.Equal(0, outcome.Count);
            Assert.Equal(StorePurchaseCoordinator.ReasonGrantedNoItem, outcome.Reason);

            Assert.Empty(harness.Handler.Commands);
            Assert.Equal(1, harness.Ledger.RecordCount);
            Assert.True(harness.Ledger.IsOwned(Vip.ProductId));

            // 收入埋点照记（钱确实收了），只是道具数值为 0。
            AnalyticsEvent tracked = Assert.Single(analytics.Events);
            Assert.Equal(0f, tracked.Value0);
            Assert.Equal(0f, tracked.Value1);
        }

        [Fact]
        public void Purchase_Override_FillsMissingGrantFromCatalog()
        {
            // 目录只列了 SKU 与价格、没配到账内容（GrantItemId/GrantCount 都是 0）：
            // 重载参数补空缺，这是"产品 ID 直接映射道具"的主用法。
            var harness = new Harness(new[]
            {
                new IapProduct("com.demo.gold_pack", IapProductKind.Consumable, "$4.99", 0, 0)
            });

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase("com.demo.gold_pack", 5001, 5);

            Assert.True(outcome.Granted);
            Assert.Equal(5001, outcome.ItemId);
            Assert.Equal(5, outcome.Count);
            GrantItemCommand command = Assert.Single(harness.Handler.Commands);
            Assert.Equal(5001, command.ItemId);
            Assert.Equal(5, command.Count);
        }

        [Fact]
        public void Purchase_Override_IsIgnoredWhenCatalogAlreadyConfiguresGrant()
        {
            // 目录已经把到账内容配全了，调用方再传一套值也不生效：到账内容只能有一个事实源。
            // 反过来（调用方覆盖目录）时，一次误传就能把"首充双倍"变成永久双倍。
            var harness = new Harness();

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId, 5001, 5);

            Assert.True(outcome.Granted);
            Assert.Equal(1001, outcome.ItemId);
            Assert.Equal(60, outcome.Count);
            Assert.Equal(1001, Assert.Single(harness.Handler.Commands).ItemId);
        }

        [Fact]
        public void Purchase_OverrideCountOnly_FallsBackToConfiguredGoldItemId()
        {
            // 只给数量（"这个 SKU 就是给 25 金币"）：道具 ID 退回构造参数 goldItemId。
            // 这里刻意传 7 而不是默认的 1，验收的是"构造参数真的被用上了"，而不是硬编码常量。
            var harness = new Harness(
                new[] { new IapProduct("com.demo.gold_pack", IapProductKind.Consumable, "$4.99", 0, 0) },
                goldItemId: 7);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase("com.demo.gold_pack", 0, 25);

            Assert.True(outcome.Granted);
            Assert.Equal(7, outcome.ItemId);
            Assert.Equal(25, outcome.Count);
            Assert.Equal(7, Assert.Single(harness.Handler.Commands).ItemId);
        }

        [Fact]
        public void Purchase_OverrideForUnknownProduct_SynthesizesRepeatableConsumable()
        {
            // 目录里完全查不到这件商品，但调用方明确给了到账内容：按消耗品合成（可反复购买）。
            // 若按拥有型合成，玩家第二次购买就会因为"台账只发一次"而拿不到货。
            var harness = new Harness(new[] { Gold60 });

            StorePurchaseOutcome first = harness.Coordinator.Purchase("com.demo.ghost", 5001, 5);
            harness.Store.Receipt = "receipt-2";
            StorePurchaseOutcome second = harness.Coordinator.Purchase("com.demo.ghost", 5001, 5);

            Assert.True(first.Granted);
            Assert.True(second.Granted);
            Assert.Equal(2, harness.Handler.Commands.Count);
            Assert.Equal(2, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Purchase_BusWithoutHandler_ReturnsGrantFailedWithoutThrowing()
        {
            // CQE 语义已确认：未注册处理器时 Send 抛 InvalidOperationException。
            // 协调器必须捕获并视为失败——但要说清代价：台账已登记（收据已消耗），道具没发出去，
            // 这是**不可回滚**状态，因此单独用 ReasonGrantFailed 标出来给上层补偿。
            var harness = new Harness(registerHandler: false);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.False(outcome.Granted);
            Assert.Equal(StorePurchaseCoordinator.ReasonGrantFailed, outcome.Reason);
            Assert.Equal(1, harness.Ledger.RecordCount); // 如实反映：登记已发生，重放同一收据不会再发
            Assert.True(harness.Ledger.IsReceiptConsumed(Receipt));
        }

        [Fact]
        public void Purchase_HandlerThrows_ReturnsGrantFailedWithoutThrowing()
        {
            // 背包处理器自己抛异常（代码缺陷）对协调器而言同样是"指令没有确认落地"。
            var harness = new Harness(handlerThrows: true);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.False(outcome.Granted);
            Assert.Equal(StorePurchaseCoordinator.ReasonGrantFailed, outcome.Reason);
        }

        [Fact]
        public void Purchase_WithoutAnalytics_StillClosesLoop()
        {
            // 埋点是可选项：没接线时链路行为完全不变，只是没有事件。
            var harness = new Harness(analytics: null);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.True(outcome.Granted);
            Assert.Single(harness.Handler.Commands);
        }

        [Fact]
        public void Purchase_AnalyticsThrows_DoesNotFailCompletedPurchase()
        {
            // 埋点实现违约抛异常时，道具已经进了背包：再把异常抛给 UI 会让"已到账"被当成"购买失败"。
            var analytics = new RecordingAnalytics { ThrowOnTrack = true };
            var harness = new Harness(analytics: analytics);

            StorePurchaseOutcome outcome = harness.Coordinator.Purchase(Gold60.ProductId);

            Assert.True(outcome.Granted);
            Assert.Single(harness.Handler.Commands);
        }

        [Fact]
        public void Purchase_Analytics_PriceTierIsStableHashOfPriceLabel()
        {
            // 口径锁死：FNV-1a 32 位（偏移基 2166136261 / 质数 16777619）对 priceLabel 逐字符混淆后 %1000。
            // 为什么不能用 string.GetHashCode()：.NET Core 起字符串哈希按进程随机化，
            // 同一份数据每次启动算出的档位都不同，看板上"同一个价格"会散成几十个档。
            var analytics = new RecordingAnalytics();
            var harness = new Harness(analytics: analytics);

            harness.Coordinator.Purchase(Gold60.ProductId);   // "$0.99"
            harness.Store.Receipt = "receipt-2";
            harness.Coordinator.Purchase(Gold300.ProductId);  // "$4.99"

            Assert.Equal(2, analytics.Events.Count);
            Assert.Equal(847f, analytics.Events[0].Value2);
            Assert.Equal(275f, analytics.Events[1].Value2);

            // 档位必须落在 0..999（看板分组的域），且空价格标签记为 0（未知价格）。
            var emptyPrice = new IapProduct("com.demo.free", IapProductKind.Consumable, string.Empty, 1001, 1);
            var second = new Harness(new[] { emptyPrice }, analytics);
            second.Coordinator.Purchase(emptyPrice.ProductId);

            Assert.Equal(0f, analytics.Events[2].Value2);
        }

        [Fact]
        public void Restore_NotYetGranted_GrantsOnceAndCountsAsRestored()
        {
            // 重装/换机：本地台账是空的，商店回报"你买过去广告" → 补发一次。
            var analytics = new RecordingAnalytics();
            var harness = new Harness(analytics: analytics);
            harness.Store.RestoreResults = new[]
            {
                new IapPurchaseResult(IapPurchaseState.Purchased, NoAds.ProductId, "restore-1", "restored")
            };

            int restored = harness.Coordinator.RestorePurchases();

            Assert.Equal(1, restored);
            GrantItemCommand command = Assert.Single(harness.Handler.Commands);
            Assert.Equal(2001, command.ItemId);
            Assert.Equal(1, command.Count);
            Assert.Equal(1, harness.Ledger.RecordCount);
            Assert.True(harness.Ledger.IsOwned(NoAds.ProductId));
            Assert.Single(analytics.Events);
            Assert.Equal(1, harness.Store.RestoreCallCount);
        }

        [Fact]
        public void Restore_AlreadyGranted_CountsWithoutGrantingOrTracking()
        {
            // 同一台设备再点一次"恢复购买"：权益本地已有，不重复发、不重复记收入，
            // 但仍计入返回件数（否则 UI 会把"全都恢复过了"显示成"没有可恢复的商品"）。
            var analytics = new RecordingAnalytics();
            var harness = new Harness(analytics: analytics);
            Assert.True(harness.Coordinator.Purchase(NoAds.ProductId).Granted);
            harness.Store.RestoreResults = new[]
            {
                new IapPurchaseResult(IapPurchaseState.Purchased, NoAds.ProductId, Receipt, "restored")
            };

            int restored = harness.Coordinator.RestorePurchases();

            Assert.Equal(1, restored);
            Assert.Single(harness.Handler.Commands); // 仍然只有购买时那一条
            Assert.Single(analytics.Events);
            Assert.Equal(1, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Restore_Twice_SecondCallDoesNotGrantAgain()
        {
            // 连点两次恢复：第二次必须只做确认。这条是"恢复购买不发双份"最直接的一刀。
            var harness = new Harness();
            harness.Store.RestoreResults = new[]
            {
                new IapPurchaseResult(IapPurchaseState.Purchased, NoAds.ProductId, "restore-1", "restored")
            };

            Assert.Equal(1, harness.Coordinator.RestorePurchases());
            Assert.Equal(1, harness.Coordinator.RestorePurchases());

            Assert.Single(harness.Handler.Commands);
            Assert.Equal(1, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Restore_SkipsConsumables()
        {
            // 商店侧不提供消耗品补单；客户端若照单发货，等于凭空造道具（刷金币漏洞）。
            var harness = new Harness();
            harness.Store.RestoreResults = new[]
            {
                new IapPurchaseResult(IapPurchaseState.Purchased, Gold60.ProductId, "gold-restore", "restored")
            };

            Assert.Equal(0, harness.Coordinator.RestorePurchases());
            Assert.Empty(harness.Handler.Commands);
            Assert.Equal(0, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Restore_SkipsProductsMissingFromCatalog()
        {
            // 目录里查不到 → 不知道发什么。此时登记只会把收据吃掉，因此跳过且不计数。
            var harness = new Harness();
            harness.Store.RestoreResults = new[]
            {
                new IapPurchaseResult(IapPurchaseState.Purchased, "com.demo.ghost", "ghost-1", "restored")
            };

            Assert.Equal(0, harness.Coordinator.RestorePurchases());
            Assert.Empty(harness.Handler.Commands);
            Assert.Equal(0, harness.Ledger.RecordCount);
        }

        [Fact]
        public void Restore_SkipsNonPurchasedStates()
        {
            // 商店不可用时恢复会返回单条 StoreUnavailable：那是"问不到"，不是"没有已购项"，更不是可发货项。
            var harness = new Harness();
            harness.Store.RestoreResults = new[]
            {
                new IapPurchaseResult(IapPurchaseState.StoreUnavailable, string.Empty, string.Empty, "store unavailable")
            };

            Assert.Equal(0, harness.Coordinator.RestorePurchases());
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Restore_StoreThrows_ReturnsZeroWithoutThrowing()
        {
            var harness = new Harness();
            harness.Store.ThrowOnRestore = true;

            Assert.Equal(0, harness.Coordinator.RestorePurchases());
        }

        [Fact]
        public void Restore_EmptyResults_ReturnsZero()
        {
            var harness = new Harness();

            Assert.Equal(0, harness.Coordinator.RestorePurchases());
            Assert.Empty(harness.Handler.Commands);
        }

        [Fact]
        public void Catalog_ReturnsDefensiveCopyOfLatestStoreCatalog()
        {
            var harness = new Harness();

            IapProduct[] first = harness.Coordinator.Catalog;
            Assert.Equal(4, first.Length);

            // 外部改写返回的数组不得影响协调器，也不得影响商店内部目录。
            first[0] = new IapProduct("com.demo.hacked", IapProductKind.Consumable, "$0", 0, 0);
            Assert.Equal(Gold60.ProductId, harness.Coordinator.Catalog[0].ProductId);
            Assert.Equal(Gold60.ProductId, harness.Store.GetCatalog()[0].ProductId);

            // 目录是"每次向商店要最新"的，不是构造期快照：真实商店的商品是异步拉回来的。
            harness.Store.AddProduct(new IapProduct("com.demo.new", IapProductKind.Consumable, "$9.99", 3001, 1));
            Assert.Equal(5, harness.Coordinator.Catalog.Length);
        }

        [Fact]
        public void Catalog_BrokenStore_ReturnsEmptyWithoutThrowing()
        {
            // 商店违约（抛异常 / 返回 null）时目录退化成空数组：UI 可以据此禁用商店，
            // 而不是在打开商店页那一刻崩溃。
            var throwing = new Harness();
            throwing.Store.ThrowOnCatalog = true;
            Assert.Empty(throwing.Coordinator.Catalog);

            var nullCatalog = new Harness();
            nullCatalog.Store.NullCatalog = true;
            Assert.Empty(nullCatalog.Coordinator.Catalog);

            // 连带的后果如实反映：目录空 → 商品无法解析 → 不登记、不发货（而不是乱发）。
            nullCatalog.Store.State = IapPurchaseState.Purchased;
            StorePurchaseOutcome outcome = nullCatalog.Coordinator.Purchase(Gold60.ProductId);
            Assert.False(outcome.Granted);
            Assert.Equal(StorePurchaseCoordinator.ReasonUnknownProduct, outcome.Reason);
        }

        /// <summary>
        /// 一次购买/恢复的测试装配：真 <see cref="CqeBus"/> + 真 <see cref="IapPurchaseLedger"/> +
        /// 假商店 + 假埋点。所有字段都可读写，便于逐条构造失败路径。
        /// </summary>
        private sealed class Harness
        {
            internal Harness(
                IapProduct[]? catalog = null,
                IAnalyticsService? analytics = null,
                int goldItemId = MetaItemIds.Gold,
                bool registerHandler = true,
                bool handlerThrows = false,
                IReceiptValidator? validator = null)
            {
                Store = new FakeStore(catalog ?? DefaultCatalog());
                Ledger = new IapPurchaseLedger(validator ?? new ToggleValidator());
                Bus = new CqeBus();
                Handler = new RecordingCommandHandler { ThrowOnHandle = handlerThrows };
                if (registerHandler)
                {
                    Bus.RegisterCommandHandler<GrantItemCommand>(Handler);
                }

                Coordinator = new StorePurchaseCoordinator(Store, Ledger, Bus, analytics, goldItemId);
            }

            internal FakeStore Store { get; }

            internal IapPurchaseLedger Ledger { get; }

            internal CqeBus Bus { get; }

            internal RecordingCommandHandler Handler { get; }

            internal StorePurchaseCoordinator Coordinator { get; }

            /// <summary>与 <c>ConsoleIapService</c> 默认目录同构的 4 件商品，覆盖消耗品/非消耗品/订阅。</summary>
            private static IapProduct[] DefaultCatalog()
            {
                return new[] { Gold60, Gold300, NoAds, Vip };
            }
        }

        /// <summary>
        /// 假商店：把"买什么状态、给什么收据、恢复返回什么"全部做成可写属性，
        /// 让每条失败路径都能精确构造。它<b>不</b>持有台账（真实商店也不持有），
        /// 登记收据的责任在协调器，这正是本任务要验收的接线形态。
        /// </summary>
        private sealed class FakeStore : IIapService
        {
            private readonly List<IapProduct> _catalog;

            internal FakeStore(params IapProduct[] catalog)
            {
                _catalog = new List<IapProduct>(catalog);
            }

            /// <summary>购买返回的状态（默认成功）。</summary>
            internal IapPurchaseState State { get; set; } = IapPurchaseState.Purchased;

            /// <summary>购买返回的收据（默认 <see cref="Receipt"/>）。</summary>
            internal string Receipt { get; set; } = StorePurchaseCoordinatorTests.Receipt;

            /// <summary>购买返回的诊断原因。</summary>
            internal string PurchaseReason { get; set; } = "purchased";

            /// <summary>恢复购买返回的结果集。</summary>
            internal IapPurchaseResult[] RestoreResults { get; set; } = Array.Empty<IapPurchaseResult>();

            /// <summary>购买时抛异常（模拟违约实现）。</summary>
            internal bool ThrowOnPurchase { get; set; }

            /// <summary>恢复时抛异常。</summary>
            internal bool ThrowOnRestore { get; set; }

            /// <summary>取目录时抛异常。</summary>
            internal bool ThrowOnCatalog { get; set; }

            /// <summary>取目录时返回 <c>null</c>。</summary>
            internal bool NullCatalog { get; set; }

            /// <summary>购买调用次数。</summary>
            internal int PurchaseCallCount { get; private set; }

            /// <summary>最近一次购买请求的商品 ID。</summary>
            internal string? LastProductId { get; private set; }

            /// <summary>恢复调用次数。</summary>
            internal int RestoreCallCount { get; private set; }

            /// <inheritdoc />
            public bool IsAvailable
            {
                get { return true; }
            }

            /// <summary>追加一件商品（验收"目录是每次取最新"）。</summary>
            internal void AddProduct(IapProduct product)
            {
                _catalog.Add(product);
            }

            /// <inheritdoc />
            public IapProduct[] GetCatalog()
            {
                if (ThrowOnCatalog)
                {
                    throw new InvalidOperationException("catalog unavailable");
                }

                if (NullCatalog)
                {
                    return null!;
                }

                return _catalog.ToArray();
            }

            /// <inheritdoc />
            public IapPurchaseResult Purchase(string productId)
            {
                PurchaseCallCount++;
                LastProductId = productId;
                if (ThrowOnPurchase)
                {
                    throw new InvalidOperationException("store exploded");
                }

                return new IapPurchaseResult(State, productId, Receipt, PurchaseReason);
            }

            /// <inheritdoc />
            public IapPurchaseResult[] RestorePurchases()
            {
                RestoreCallCount++;
                if (ThrowOnRestore)
                {
                    throw new InvalidOperationException("restore exploded");
                }

                return RestoreResults;
            }
        }

        /// <summary>记录型发奖处理器：留下一份 <see cref="GrantItemCommand"/> 清单供断言。</summary>
        private sealed class RecordingCommandHandler : ICommandHandler<GrantItemCommand>
        {
            /// <summary>收到的指令（按顺序）。</summary>
            internal List<GrantItemCommand> Commands { get; } = new List<GrantItemCommand>();

            /// <summary>处理时抛异常（模拟背包切片的代码缺陷）。</summary>
            internal bool ThrowOnHandle { get; set; }

            /// <inheritdoc />
            public void Handle(in GrantItemCommand command)
            {
                if (ThrowOnHandle)
                {
                    throw new InvalidOperationException("inventory exploded");
                }

                Commands.Add(command);
            }
        }

        /// <summary>记录型埋点：只收事件，不落盘。</summary>
        private sealed class RecordingAnalytics : IAnalyticsService
        {
            /// <summary>收到的事件（按顺序）。</summary>
            internal List<AnalyticsEvent> Events { get; } = new List<AnalyticsEvent>();

            /// <summary>记录时抛异常（模拟违约实现）。</summary>
            internal bool ThrowOnTrack { get; set; }

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
                if (ThrowOnTrack)
                {
                    throw new InvalidOperationException("analytics exploded");
                }

                Events.Add(analyticsEvent);
            }

            /// <inheritdoc />
            public void Flush()
            {
            }
        }

        /// <summary>可切换行为的收据校验器（验收"校验失败 → 不发物品"）。</summary>
        private sealed class ToggleValidator : IReceiptValidator
        {
            /// <summary>是否通过校验。</summary>
            internal bool Accept { get; set; } = true;

            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                return Accept;
            }
        }
    }
}
