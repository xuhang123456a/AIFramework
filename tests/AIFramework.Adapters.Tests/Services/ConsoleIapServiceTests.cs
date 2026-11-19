using System;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleIapService"/> 的行为契约验收：默认目录、未知商品降级、消耗品/拥有型发货语义、
    /// 恢复购买、不可用状态与排空环形日志。
    /// <para>
    /// 这些用例同时是"未来真实商店实现"的验收清单——假商店在这里被允许的行为就是真商店必须做到的最小语义；
    /// 任何一条在这里放松（例如重复购买又发一次道具），真机上线就会变成真实的资损。
    /// </para>
    /// </summary>
    public sealed class ConsoleIapServiceTests
    {
        [Fact]
        public void IsAvailable_ReflectsConstructorConfiguration()
        {
            Assert.True(CreateService(out _).IsAvailable);

            var offline = new ConsoleIapService(new IapPurchaseLedger(new AcceptAllValidator()), available: false);
            Assert.False(offline.IsAvailable);
        }

        [Fact]
        public void GetCatalog_ReturnsThreeDefaultProducts()
        {
            IapProduct[] catalog = CreateService(out _).GetCatalog();

            Assert.Equal(3, catalog.Length);

            Assert.Equal(ConsoleIapService.ProductGold60, catalog[0].ProductId);
            Assert.Equal(IapProductKind.Consumable, catalog[0].Kind);
            Assert.Equal(ConsoleIapService.GoldItemId, catalog[0].GrantItemId);
            Assert.Equal(60, catalog[0].GrantCount);
            Assert.False(string.IsNullOrEmpty(catalog[0].PriceLabel));

            Assert.Equal(ConsoleIapService.ProductGold300, catalog[1].ProductId);
            Assert.Equal(IapProductKind.Consumable, catalog[1].Kind);
            Assert.Equal(300, catalog[1].GrantCount);

            Assert.Equal(ConsoleIapService.ProductNoAds, catalog[2].ProductId);
            Assert.Equal(IapProductKind.NonConsumable, catalog[2].Kind);
        }

        [Fact]
        public void GetCatalog_ReturnsDefensiveCopy()
        {
            ConsoleIapService service = CreateService(out _);
            IapProduct[] first = service.GetCatalog();

            // 试图越过契约改内部目录（真实实现里目录来自商店，绝不能被调用方改写）。
            first[0] = new IapProduct("com.demo.hacked", IapProductKind.Consumable, "$0", 0, 0);

            Assert.Equal(ConsoleIapService.ProductGold60, service.GetCatalog()[0].ProductId);
            Assert.Equal(IapPurchaseState.Purchased, service.Purchase(ConsoleIapService.ProductGold60).State);
        }

        [Fact]
        public void Purchase_UnknownProduct_ReturnsStoreUnavailableWithUnknownProductReason()
        {
            ConsoleIapService service = CreateService(out IapPurchaseLedger ledger);

            IapPurchaseResult result = service.Purchase("com.demo.unknown");

            Assert.Equal(IapPurchaseState.StoreUnavailable, result.State);
            Assert.Equal(ConsoleIapService.ReasonUnknownProduct, result.Reason);
            Assert.False(result.IsSuccess);
            Assert.Equal(0, ledger.RecordCount);
        }

        [Fact]
        public void Purchase_EmptyProductId_ReturnsStoreUnavailable()
        {
            IapPurchaseResult result = CreateService(out _).Purchase(string.Empty);

            Assert.Equal(IapPurchaseState.StoreUnavailable, result.State);
            Assert.Equal(ConsoleIapService.ReasonUnknownProduct, result.Reason);
        }

        [Fact]
        public void Purchase_Consumable_ReturnsPurchasedReceiptAndGrant()
        {
            ConsoleIapService service = CreateService(out IapPurchaseLedger ledger);

            IapPurchaseResult result = service.Purchase(ConsoleIapService.ProductGold60);

            Assert.Equal(IapPurchaseState.Purchased, result.State);
            Assert.True(result.IsSuccess);
            Assert.Equal(ConsoleIapService.ProductGold60, result.ProductId);
            Assert.Equal("console-com.demo.gold60#1", result.Receipt);
            Assert.Equal(ConsoleIapService.ReasonPurchased, result.Reason);

            // 发货口径来自台账：60 金币。
            Assert.Equal(1, ledger.RecordCount);
            Assert.Equal(1001, ledger.GrantedItemId);
            Assert.Equal(60, ledger.GrantedCount);
            Assert.True(ledger.IsReceiptConsumed(result.Receipt));
        }

        [Fact]
        public void Purchase_NonConsumableTwice_SecondReturnsAlreadyOwned()
        {
            ConsoleIapService service = CreateService(out IapPurchaseLedger ledger);

            IapPurchaseResult first = service.Purchase(ConsoleIapService.ProductNoAds);
            IapPurchaseResult second = service.Purchase(ConsoleIapService.ProductNoAds);

            Assert.Equal(IapPurchaseState.Purchased, first.State);
            Assert.Equal(IapPurchaseState.AlreadyOwned, second.State);
            Assert.Equal(IapPurchaseLedger.ReasonAlreadyOwned, second.Reason);
            Assert.False(second.IsSuccess);

            // 拥有型只发一次：台账只登记了一笔。
            Assert.Equal(1, ledger.RecordCount);
            Assert.True(ledger.IsOwned(ConsoleIapService.ProductNoAds));
        }

        [Fact]
        public void Purchase_ConsumableTwice_BothPurchasedWithDistinctReceipts()
        {
            ConsoleIapService service = CreateService(out IapPurchaseLedger ledger);

            IapPurchaseResult first = service.Purchase(ConsoleIapService.ProductGold60);
            IapPurchaseResult second = service.Purchase(ConsoleIapService.ProductGold60);

            Assert.Equal(IapPurchaseState.Purchased, first.State);
            Assert.Equal(IapPurchaseState.Purchased, second.State);
            Assert.NotEqual(first.Receipt, second.Receipt);
            Assert.Equal(2, ledger.RecordCount);
            Assert.False(ledger.IsOwned(ConsoleIapService.ProductGold60));
        }

        [Fact]
        public void RestorePurchases_AfterNonConsumablePurchase_ReturnsSameReceipt()
        {
            ConsoleIapService service = CreateService(out _);
            IapPurchaseResult purchase = service.Purchase(ConsoleIapService.ProductNoAds);

            IapPurchaseResult[] restored = service.RestorePurchases();

            IapPurchaseResult restoredItem = Assert.Single(restored);
            Assert.Equal(IapPurchaseState.Purchased, restoredItem.State);
            Assert.Equal(ConsoleIapService.ProductNoAds, restoredItem.ProductId);
            Assert.Equal(purchase.Receipt, restoredItem.Receipt);
            Assert.Equal(ConsoleIapService.ReasonRestored, restoredItem.Reason);
            Assert.True(restoredItem.IsSuccess);
        }

        [Fact]
        public void RestorePurchases_DoesNotIncludeConsumables()
        {
            ConsoleIapService service = CreateService(out _);
            service.Purchase(ConsoleIapService.ProductGold60);

            // 消耗品补单就是刷道具漏洞，商店本身也不提供；恢复必须为空。
            Assert.Empty(service.RestorePurchases());
        }

        [Fact]
        public void UnavailableStore_PurchaseAndRestore_ReturnStoreUnavailable()
        {
            var service = new ConsoleIapService(new IapPurchaseLedger(new AcceptAllValidator()), available: false);

            IapPurchaseResult purchase = service.Purchase(ConsoleIapService.ProductGold60);
            Assert.Equal(IapPurchaseState.StoreUnavailable, purchase.State);
            Assert.Equal(ConsoleIapService.ReasonUnavailable, purchase.Reason);
            Assert.False(purchase.IsSuccess);

            IapPurchaseResult[] restored = service.RestorePurchases();
            IapPurchaseResult restoredItem = Assert.Single(restored);
            Assert.Equal(IapPurchaseState.StoreUnavailable, restoredItem.State);
            Assert.Equal(ConsoleIapService.ReasonUnavailable, restoredItem.Reason);
        }

        [Fact]
        public void Purchase_ValidatorRejects_ReturnsInvalidReceipt()
        {
            var validator = new ToggleValidator { Accept = false };
            var ledger = new IapPurchaseLedger(validator);
            var service = new ConsoleIapService(ledger);

            IapPurchaseResult result = service.Purchase(ConsoleIapService.ProductGold60);

            Assert.Equal(IapPurchaseState.InvalidReceipt, result.State);
            Assert.Equal(IapPurchaseLedger.ReasonReceiptRejected, result.Reason);
            Assert.False(result.IsSuccess);
            Assert.Equal(0, ledger.RecordCount);
        }

        [Fact]
        public void Constructor_CustomCatalog_IsUsedForPurchase()
        {
            var custom = new[]
            {
                new IapProduct("com.demo.custom", IapProductKind.Consumable, "¥1.00", 5001, 5)
            };
            var service = new ConsoleIapService(new IapPurchaseLedger(new AcceptAllValidator()), custom);

            Assert.Single(service.GetCatalog());
            Assert.Equal(IapPurchaseState.Purchased, service.Purchase("com.demo.custom").State);
        }

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new ConsoleIapService(null!));
            Assert.Throws<ArgumentNullException>(
                () => new ConsoleIapService(new IapPurchaseLedger(new AcceptAllValidator()), (IapProduct[])null!));
        }

        [Fact]
        public void DrainLog_ReturnsEventsInOrderAndClears()
        {
            ConsoleIapService service = CreateService(out _);
            service.Purchase(ConsoleIapService.ProductNoAds);
            service.RestorePurchases();

            var log = service.DrainLog();

            Assert.Equal(2, log.Count);
            Assert.Contains("purchase", log[0]);
            Assert.Contains("Purchased", log[0]);
            Assert.Contains("restore", log[1]);

            // 排空语义：同一批事件不允许被重复打印。
            Assert.Empty(service.DrainLog());
        }

        [Fact]
        public void DrainLog_KeepsOnlyLatestEntries()
        {
            ConsoleIapService service = CreateService(out _);
            for (int i = 0; i < 40; i++)
            {
                Assert.Equal(IapPurchaseState.Purchased, service.Purchase(ConsoleIapService.ProductGold60).State);
            }

            var log = service.DrainLog();

            // 环形缓冲只保留最近 32 条：40 次购买后第一条应是第 9 次，最后一条是第 40 次。
            Assert.Equal(ConsoleIapService.LogCapacity, log.Count);
            Assert.Contains("#9 ", log[0]);
            Assert.Contains("#40", log[log.Count - 1]);
        }

        /// <summary>创建"默认目录 + 通过一切合法收据"的假商店。</summary>
        private static ConsoleIapService CreateService(out IapPurchaseLedger ledger)
        {
            ledger = new IapPurchaseLedger(new AcceptAllValidator());
            return new ConsoleIapService(ledger);
        }

        /// <summary>验收用收据校验器：空商品/空收据拒绝，其余通过。</summary>
        private sealed class AcceptAllValidator : IReceiptValidator
        {
            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                return !string.IsNullOrEmpty(productId) && !string.IsNullOrEmpty(receipt);
            }
        }

        /// <summary>可切换行为的收据校验器（验收"校验失败 → InvalidReceipt"映射）。</summary>
        private sealed class ToggleValidator : IReceiptValidator
        {
            /// <summary>是否通过校验。</summary>
            public bool Accept { get; set; } = true;

            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                return Accept;
            }
        }
    }
}
