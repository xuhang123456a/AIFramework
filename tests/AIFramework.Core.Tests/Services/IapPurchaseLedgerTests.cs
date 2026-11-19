using System;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="IapPurchaseLedger"/> 的行为契约验收：收据去重、拥有型一次性发放、消耗品可重复、
    /// 坏收据/校验失败/校验器异常一律拒绝，以及"被拒绝的尝试绝不污染台账"。
    /// <para>
    /// 这些用例是"重复到账"红线的唯一守卫：真实商店偶尔会重复回调、玩家会重放收据、
    /// 客户端也可能重复提交。台账在这里判错一次，线上就是一次免费发货或一次玩家投诉。
    /// </para>
    /// </summary>
    public sealed class IapPurchaseLedgerTests
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
        public void Constructor_NullValidator_Throws()
        {
            // 唯一允许抛异常的入口：校验器是硬依赖，没有它就无法判断收据真伪。
            Assert.Throws<ArgumentNullException>(() => new IapPurchaseLedger(null!));
        }

        [Fact]
        public void TryRecord_ValidConsumable_RecordsReceiptAndGrant()
        {
            var validator = new ToggleValidator();
            var ledger = new IapPurchaseLedger(validator);

            bool recorded = ledger.TryRecord(in Gold60, Receipt, out string reason);

            Assert.True(recorded);
            Assert.Equal(IapPurchaseLedger.ReasonRecorded, reason);
            Assert.Equal(1, ledger.RecordCount);
            Assert.Equal(Gold60.GrantItemId, ledger.GrantedItemId);
            Assert.Equal(Gold60.GrantCount, ledger.GrantedCount);
            Assert.True(ledger.IsReceiptConsumed(Receipt));
            Assert.Equal(1, validator.CallCount);
        }

        [Fact]
        public void TryRecord_DuplicateReceipt_RejectedAndGrantUnchanged()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            Assert.True(ledger.TryRecord(in Gold60, Receipt, out _));

            // 同一张收据换个商品再提交（伪造/重放）：必须拒绝，且不能覆盖上一次的发放值。
            bool second = ledger.TryRecord(in Gold300, Receipt, out string reason);

            Assert.False(second);
            Assert.Equal(IapPurchaseLedger.ReasonReceiptConsumed, reason);
            Assert.Equal(1, ledger.RecordCount);
            Assert.Equal(Gold60.GrantItemId, ledger.GrantedItemId);
            Assert.Equal(Gold60.GrantCount, ledger.GrantedCount);
        }

        [Fact]
        public void TryRecord_NonConsumableSecondPurchase_RejectedAsAlreadyOwned()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            Assert.True(ledger.TryRecord(in NoAds, "no-ads-1", out _));

            // 新的收据 + 同一件拥有型商品：商店侧可能是重复购买，但道具只能发一次。
            bool second = ledger.TryRecord(in NoAds, "no-ads-2", out string reason);

            Assert.False(second);
            Assert.Equal(IapPurchaseLedger.ReasonAlreadyOwned, reason);
            Assert.Equal(1, ledger.RecordCount);
            Assert.True(ledger.IsOwned(NoAds.ProductId));
        }

        [Fact]
        public void TryRecord_SubscriptionSecondPurchase_RejectedAsAlreadyOwned()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            Assert.True(ledger.TryRecord(in Vip, "vip-1", out _));

            Assert.False(ledger.TryRecord(in Vip, "vip-2", out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonAlreadyOwned, reason);
            Assert.True(ledger.IsOwned(Vip.ProductId));
            Assert.Equal(1, ledger.RecordCount);
        }

        [Fact]
        public void TryRecord_ConsumableCanBeRecordedRepeatedly()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());

            Assert.True(ledger.TryRecord(in Gold60, "gold-1", out _));
            Assert.True(ledger.TryRecord(in Gold60, "gold-2", out _));

            Assert.Equal(2, ledger.RecordCount);
            Assert.Equal(1001, ledger.GrantedItemId);
            Assert.Equal(60, ledger.GrantedCount);

            // 消耗品永远不是"已拥有"：它可以再买。
            Assert.False(ledger.IsOwned(Gold60.ProductId));
        }

        [Fact]
        public void TryRecord_EmptyReceipt_RejectedBeforeValidation()
        {
            var validator = new ToggleValidator();
            var ledger = new IapPurchaseLedger(validator);

            Assert.False(ledger.TryRecord(in Gold60, string.Empty, out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonReceiptMissing, reason);
            Assert.Equal(0, validator.CallCount); // 形状非法连校验器都不该碰
            Assert.Equal(0, ledger.RecordCount);
        }

        [Fact]
        public void TryRecord_NullReceipt_RejectedWithoutThrowing()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());

            Assert.False(ledger.TryRecord(in Gold60, null!, out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonReceiptMissing, reason);
        }

        [Fact]
        public void TryRecord_OverlongReceipt_Rejected()
        {
            var validator = new ToggleValidator();
            var ledger = new IapPurchaseLedger(validator);
            string receipt = new string('r', IapPurchaseLedger.MaxReceiptLength + 1);

            Assert.False(ledger.TryRecord(in Gold60, receipt, out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonReceiptTooLong, reason);
            Assert.Equal(0, validator.CallCount);
            Assert.Equal(0, ledger.RecordCount);
            Assert.False(ledger.IsReceiptConsumed(receipt));
        }

        [Fact]
        public void TryRecord_MaxLengthReceipt_IsAccepted()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            string receipt = new string('r', IapPurchaseLedger.MaxReceiptLength);

            Assert.True(ledger.TryRecord(in Gold60, receipt, out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonRecorded, reason);
        }

        [Fact]
        public void TryRecord_ValidatorRejects_RejectedAndReceiptNotConsumed()
        {
            var validator = new ToggleValidator { Accept = false };
            var ledger = new IapPurchaseLedger(validator);

            Assert.False(ledger.TryRecord(in Gold60, Receipt, out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonReceiptRejected, reason);
            Assert.Equal(0, ledger.RecordCount);
            Assert.False(ledger.IsReceiptConsumed(Receipt));
        }

        [Fact]
        public void TryRecord_ValidatorThrows_TreatedAsRejectedWithoutThrowing()
        {
            var validator = new ToggleValidator { Throw = true };
            var ledger = new IapPurchaseLedger(validator);

            // 校验器可能就是未来的网络代理：它抛异常也绝不能穿透购买链路。
            Assert.False(ledger.TryRecord(in Gold60, Receipt, out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonValidationFailed, reason);
            Assert.Equal(0, ledger.RecordCount);
        }

        [Fact]
        public void TryRecord_ReceiptRejectedEarlierCanBeAcceptedLater()
        {
            var validator = new ToggleValidator { Accept = false };
            var ledger = new IapPurchaseLedger(validator);

            Assert.False(ledger.TryRecord(in Gold60, Receipt, out _));

            // 校验失败不消耗收据：服务端恢复后，同一张真实收据仍应能补单成功（否则玩家白付钱）。
            validator.Accept = true;
            Assert.True(ledger.TryRecord(in Gold60, Receipt, out _));
            Assert.Equal(1, ledger.RecordCount);
        }

        [Fact]
        public void GrantedItemId_ReflectsLastSuccessfulRecord()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());

            Assert.True(ledger.TryRecord(in Gold60, "gold-1", out _));
            Assert.Equal(1001, ledger.GrantedItemId);
            Assert.Equal(60, ledger.GrantedCount);

            Assert.True(ledger.TryRecord(in Gold300, "gold-2", out _));
            Assert.Equal(1001, ledger.GrantedItemId);
            Assert.Equal(300, ledger.GrantedCount);
        }

        [Fact]
        public void TryRecord_RejectedAttempt_DoesNotChangeGrant()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            Assert.True(ledger.TryRecord(in NoAds, "no-ads-1", out _));

            // 被拒的重复购买不允许改写"最近一次要发什么"（否则发货方可能按这份脏值再发一次）。
            Assert.False(ledger.TryRecord(in NoAds, "no-ads-2", out _));

            Assert.Equal(2001, ledger.GrantedItemId);
            Assert.Equal(1, ledger.GrantedCount);
        }

        [Fact]
        public void IsOwned_TracksOnlyOneTimeProducts()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());

            Assert.True(ledger.TryRecord(in Gold60, "gold-1", out _));
            Assert.True(ledger.TryRecord(in NoAds, "no-ads-1", out _));
            Assert.True(ledger.TryRecord(in Vip, "vip-1", out _));

            Assert.False(ledger.IsOwned(Gold60.ProductId));
            Assert.True(ledger.IsOwned(NoAds.ProductId));
            Assert.True(ledger.IsOwned(Vip.ProductId));

            Assert.False(ledger.IsOwned(string.Empty));
            Assert.False(ledger.IsOwned("com.demo.never-bought"));
        }

        [Fact]
        public void IsReceiptConsumed_OnlyTrueAfterSuccessfulRecord()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());

            Assert.False(ledger.IsReceiptConsumed(Receipt));
            Assert.False(ledger.IsReceiptConsumed(string.Empty));

            Assert.True(ledger.TryRecord(in Gold60, Receipt, out _));

            Assert.True(ledger.IsReceiptConsumed(Receipt));
            Assert.False(ledger.IsReceiptConsumed("receipt-unknown"));
        }

        [Fact]
        public void TryRecord_EmptyProductId_RejectedBeforeValidation()
        {
            var validator = new ToggleValidator();
            var ledger = new IapPurchaseLedger(validator);
            var invalid = new IapProduct(string.Empty, IapProductKind.Consumable, string.Empty, 0, 0);

            Assert.False(ledger.TryRecord(in invalid, Receipt, out string reason));

            Assert.Equal(IapPurchaseLedger.ReasonInvalidProduct, reason);
            Assert.Equal(0, validator.CallCount);
            Assert.Equal(0, ledger.RecordCount);
        }

        [Fact]
        public void TryRecord_UnknownProductKind_TreatedAsOneTimeProduct()
        {
            var ledger = new IapPurchaseLedger(new ToggleValidator());
            var unknownKind = new IapProduct("com.demo.future", (IapProductKind)200, string.Empty, 3001, 1);

            Assert.True(ledger.TryRecord(in unknownKind, "future-1", out _));
            Assert.False(ledger.TryRecord(in unknownKind, "future-2", out string reason));

            // 未知类型按拥有型处理：宁可少发一次，也不能成为无限刷道具的入口。
            Assert.Equal(IapPurchaseLedger.ReasonAlreadyOwned, reason);
            Assert.True(ledger.IsOwned(unknownKind.ProductId));
        }

        /// <summary>
        /// 可切换行为的收据校验器：同时记录调用次数，用来验收"坏形状不触达校验器"与异常降级路径。
        /// </summary>
        private sealed class ToggleValidator : IReceiptValidator
        {
            /// <summary>是否通过校验。</summary>
            public bool Accept { get; set; } = true;

            /// <summary>是否模拟校验器故障（抛异常）。</summary>
            public bool Throw { get; set; }

            /// <summary>被调用次数。</summary>
            public int CallCount { get; private set; }

            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                CallCount++;
                if (Throw)
                {
                    throw new InvalidOperationException("validator unavailable");
                }

                return Accept;
            }
        }
    }
}
