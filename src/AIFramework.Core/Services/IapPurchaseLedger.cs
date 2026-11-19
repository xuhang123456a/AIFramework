#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// IAP 购买台账：收据去重 + 拥有型商品一次性发货 + "最近一次成功购买要发什么"的记录。
    /// <para>
    /// <b>它解决的是"重复到账"而不是"付钱"</b>：支付永远由商店完成，客户端只负责回答三件事——
    /// 这张收据是不是新的（<see cref="IsReceiptConsumed"/>）、这个拥有型商品是不是已经发过（<see cref="IsOwned"/>）、
    /// 这次成功购买该发什么（<see cref="GrantedItemId"/> / <see cref="GrantedCount"/>）。
    /// 这三条与引擎、平台、网络全部无关，因此放在 Core 里被无头单测逐条锁死；
    /// Console 假商店与 Unity 本地假商店共用同一份规则，换适配器不会改变结算语义。
    /// </para>
    /// <para>
    /// <b>不抛异常</b>：除了构造时 <c>null</c> 校验器属于编程错误会抛 <see cref="ArgumentNullException"/>，
    /// <see cref="TryRecord"/> 的任何失败都用 <c>false</c> + <see cref="TryRecord"/> 的 <c>reason</c> 表达。
    /// 购买/发货链路不允许因为一张坏收据把游戏打断。
    /// </para>
    /// <para>
    /// <b>reason 全部是常量</b>：拒绝原因只做诊断与日志，不参与业务判定（业务判定看 <c>TryRecord</c> 的 bool）。
    /// 常量而非字符串拼接既避免了给热路径增加分配，也保证同一种失败在任何适配器上报出的文本一致。
    /// </para>
    /// <para>
    /// <b>处理顺序（确定性原因）</b>：商品 ID 非法 → 空收据 → 超长收据 → 校验器判定 → 收据重复 → 已拥有 →
    /// 登记成功。顺序固定后，同一输入在任何时候都得到同一个 <c>reason</c>，排障时不必猜"是哪条规则先命中"。
    /// </para>
    /// <para>线程模型：非线程安全。由购买/发货主线程串行调用（IAP 本身不是高频路径）。</para>
    /// </summary>
    public sealed class IapPurchaseLedger
    {
        /// <summary>
        /// 收据长度上限（字符数）。真实平台收据远长于普通标识，但也不是无限长：
        /// 超过上限的输入一律按伪造/损坏处理，避免被超长字符串拖慢哈希与日志。
        /// </summary>
        public const int MaxReceiptLength = 256;

        /// <summary>成功原因：已登记（<see cref="TryRecord"/> 返回 <c>true</c> 时的 reason）。</summary>
        public const string ReasonRecorded = "recorded";

        /// <summary>拒绝原因：商品 ID 为空（实现方的目录配置错误）。</summary>
        public const string ReasonInvalidProduct = "invalid product";

        /// <summary>拒绝原因：收据为空/缺失。</summary>
        public const string ReasonReceiptMissing = "receipt missing";

        /// <summary>拒绝原因：收据超过 <see cref="MaxReceiptLength"/> 字符。</summary>
        public const string ReasonReceiptTooLong = "receipt too long";

        /// <summary>拒绝原因：校验器判定收据不可信（伪造/过期/被拒）。</summary>
        public const string ReasonReceiptRejected = "receipt rejected";

        /// <summary>拒绝原因：校验器自身失败（抛异常）——按"不可信"处理，绝不放行。</summary>
        public const string ReasonValidationFailed = "receipt validation failed";

        /// <summary>拒绝原因：同一收据已经成功登记过（幂等去重，防重复到账）。</summary>
        public const string ReasonReceiptConsumed = "receipt already consumed";

        /// <summary>拒绝原因：拥有型商品（非消耗品/订阅）已经发放过，不允许二次发货。</summary>
        public const string ReasonAlreadyOwned = "already owned";

        /// <summary>收据校验端口；构造后不可变。</summary>
        private readonly IReceiptValidator _validator;

        /// <summary>已发放过的拥有型商品 ID 集合（消耗品永不进入此集合）。</summary>
        private readonly HashSet<string> _ownedProductIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>已成功登记过的收据集合（只收成功登记过的，校验失败不消耗收据）。</summary>
        private readonly HashSet<string> _consumedReceipts = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>最近一次成功登记要发放的道具 ID（0 = 没有/不发道具）。</summary>
        private int _grantedItemId;

        /// <summary>最近一次成功登记要发放的数量（0 = 没有/不发道具）。</summary>
        private int _grantedCount;

        /// <summary>成功登记次数（消耗品可重复累加；被拒绝的尝试不计数）。</summary>
        private int _recordCount;

        /// <summary>创建购买台账。</summary>
        /// <param name="validator">收据校验器（非 <c>null</c>；本地 mock 或服务端判定均可）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="validator"/> 为 <c>null</c>（唯一的抛异常入口：编程错误）。</exception>
        public IapPurchaseLedger(IReceiptValidator validator)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        }

        /// <summary>最近一次成功登记要发放的道具 ID（0 = 没有成功登记 / 该商品不发道具）。</summary>
        public int GrantedItemId
        {
            get { return _grantedItemId; }
        }

        /// <summary>最近一次成功登记要发放的数量（与 <see cref="GrantedItemId"/> 同一口径）。</summary>
        public int GrantedCount
        {
            get { return _grantedCount; }
        }

        /// <summary>成功登记次数（用于验收"消耗品可重复到账、拥有型只发一次"）。</summary>
        public int RecordCount
        {
            get { return _recordCount; }
        }

        /// <summary>
        /// 登记一次购买：去重（同 <paramref name="receipt"/> 只认一次）、拥有型商品只发一次。
        /// <para>
        /// <b>成功后</b>：<see cref="GrantedItemId"/> / <see cref="GrantedCount"/> 被改写为本次商品值，
        /// <see cref="RecordCount"/> 自增 1，收据进入"已消耗"集合，拥有型商品进入"已拥有"集合。
        /// </para>
        /// <para>
        /// <b>失败时</b>：台账完全不变（不消耗收据、不改 <see cref="GrantedItemId"/>、不加计数），
        /// 调用方可以放心重试或按原因展示错误。
        /// </para>
        /// <para>
        /// <b>类型语义</b>：只有 <see cref="IapProductKind.Consumable"/> 允许重复登记；
        /// <see cref="IapProductKind.NonConsumable"/>、<see cref="IapProductKind.Subscription"/>
        /// 以及任何未知枚举值都按"拥有型"处理（未知类型宁可少发，也不能成为无限刷道具的入口）。
        /// </para>
        /// </summary>
        /// <param name="product">本次购买的商品（<c>in</c> 传参，不复制结构体）。</param>
        /// <param name="receipt">商店收据（空/超长/校验不通过都会拒绝；成功后被记为已消耗）。</param>
        /// <param name="reason">成功或拒绝的原因（永远非 <c>null</c>，内容为 <see cref="IapPurchaseLedger"/> 上的常量）。</param>
        /// <returns>是否成功登记（<c>true</c> 表示调用方可以按商品值发货）。</returns>
        public bool TryRecord(in IapProduct product, string receipt, out string reason)
        {
            if (string.IsNullOrEmpty(product.ProductId))
            {
                reason = ReasonInvalidProduct;
                return false;
            }

            if (string.IsNullOrEmpty(receipt))
            {
                reason = ReasonReceiptMissing;
                return false;
            }

            if (receipt.Length > MaxReceiptLength)
            {
                reason = ReasonReceiptTooLong;
                return false;
            }

            bool validated;
            try
            {
                validated = _validator.Validate(product.ProductId, receipt);
            }
            catch (Exception)
            {
                // 校验器（未来可能是网络/服务端代理）抛异常按"不可信"处理：异常绝不穿透发货链路，
                // 但也不能因此放行——放行等于免费发货。
                reason = ReasonValidationFailed;
                return false;
            }

            if (!validated)
            {
                reason = ReasonReceiptRejected;
                return false;
            }

            // 去重放在校验之后、发放之前：同一张收据第二次出现时，任何状态都不允许再改。
            if (_consumedReceipts.Contains(receipt))
            {
                reason = ReasonReceiptConsumed;
                return false;
            }

            // 只有"拥有型"才检查已拥有；消耗品每次购买都是新的一笔，必须放行。
            if (product.Kind != IapProductKind.Consumable && _ownedProductIds.Contains(product.ProductId))
            {
                reason = ReasonAlreadyOwned;
                return false;
            }

            _consumedReceipts.Add(receipt);
            if (product.Kind != IapProductKind.Consumable)
            {
                _ownedProductIds.Add(product.ProductId);
            }

            _grantedItemId = product.GrantItemId;
            _grantedCount = product.GrantCount;
            _recordCount++;

            reason = ReasonRecorded;
            return true;
        }

        /// <summary>
        /// 该商品是否已发放过。<b>只对拥有型商品有意义</b>：消耗品恒为 <c>false</c>（可以反复购买）。
        /// </summary>
        /// <param name="productId">商品 ID（空串返回 <c>false</c>）。</param>
        /// <returns>拥有型商品是否已发放过。</returns>
        public bool IsOwned(string productId)
        {
            if (string.IsNullOrEmpty(productId))
            {
                return false;
            }

            return _ownedProductIds.Contains(productId);
        }

        /// <summary>该收据是否已经成功登记过（幂等验收/排障用；空串恒为 <c>false</c>）。</summary>
        /// <param name="receipt">商店收据。</param>
        /// <returns>是否已被消耗。</returns>
        public bool IsReceiptConsumed(string receipt)
        {
            if (string.IsNullOrEmpty(receipt))
            {
                return false;
            }

            return _consumedReceipts.Contains(receipt);
        }
    }
}
