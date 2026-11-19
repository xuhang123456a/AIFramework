#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台 IAP 服务：**内存假商店**，用来在无头环境验收 <see cref="IIapService"/> 的全部语义
    /// （目录 / 购买 / 拥有型去重 / 收据校验失败 / 恢复购买 / 不可用降级）。
    /// <para>
    /// <b>它不是空实现</b>：收据由本服务真实生成（<c>"console-" + productId + "#" + 序号</c>），
    /// 并交给 <see cref="IapPurchaseLedger"/> 做去重与发放登记；重复购买拥有型商品返回
    /// <see cref="IapPurchaseState.AlreadyOwned"/> 而不是再发一次。真实商店（App Store / Google Play）
    /// 只是把这些步骤换成 SDK 调用与收据验签，结算语义必须与这里完全一致——否则无头测试绿、真机扣了钱没到账。
    /// </para>
    /// <para>
    /// <b>为什么没有网络、没有平台 SDK</b>：支付渠道属于适配层后续工作；本类只提供
    /// "离线可跑、结果可断言"的参考实现，让切片侧（商店 UI / 背包发货）先完整落地。
    /// </para>
    /// <para>
    /// <b>默认目录</b>：<c>com.demo.gold60</c>（消耗品，发 1001 × 60）、<c>com.demo.gold300</c>
    /// （消耗品，发 1001 × 300）、<c>com.demo.no_ads</c>（非消耗品，发 2001 × 1）。
    /// </para>
    /// <para>
    /// <b>日志是排空的环形缓冲</b>：只服务冒烟与排障（<see cref="DrainLog"/> 取走即清空），
    /// 不参与业务判定，因此允许在这里拼字符串（正式逻辑路径禁止动态拼接）。
    /// </para>
    /// <para>线程模型：非线程安全。购买与发货同线程串行调用（IAP 不是高频路径）。</para>
    /// </summary>
    public sealed class ConsoleIapService : IIapService
    {
        /// <summary>购买日志环形容量（最近 32 条）。</summary>
        public const int LogCapacity = 32;

        /// <summary>默认目录：金币商品到账的道具 ID（背包 itemId）。</summary>
        public const int GoldItemId = 1001;

        /// <summary>默认目录：去广告商品到账的道具 ID（背包 itemId）。</summary>
        public const int NoAdsItemId = 2001;

        /// <summary>默认目录商品：60 金币。</summary>
        public const string ProductGold60 = "com.demo.gold60";

        /// <summary>默认目录商品：300 金币。</summary>
        public const string ProductGold300 = "com.demo.gold300";

        /// <summary>默认目录商品：去广告（非消耗品）。</summary>
        public const string ProductNoAds = "com.demo.no_ads";

        /// <summary>不可用原因：构造时配置为离线，或商店后端未接线。</summary>
        public const string ReasonUnavailable = "store unavailable";

        /// <summary>不可用原因：商品 ID 不在目录里（配置错误或客户端过旧）。</summary>
        public const string ReasonUnknownProduct = "unknown_product";

        /// <summary>成功原因：收据已登记，调用方可以发货。</summary>
        public const string ReasonPurchased = "purchased";

        /// <summary>成功原因：补单找回的历史购买（调用方按拥有型商品处理，不要再发货）。</summary>
        public const string ReasonRestored = "restored";

        /// <summary>购买台账（收据去重 + 拥有型一次性发放）；构造后不可变。</summary>
        private readonly IapPurchaseLedger _ledger;

        /// <summary>商品目录副本（构造时深拷贝，外部改写传入数组不得影响本服务）。</summary>
        private readonly IapProduct[] _catalog;

        /// <summary>是否配置为可用（构造后不可变，避免运行中"假装掉线/上线"造成不确定行为）。</summary>
        private readonly bool _available;

        /// <summary>已购买拥有型商品的收据镜像（恢复购买要复用同一张收据）。</summary>
        private readonly Dictionary<string, string> _ownedReceipts =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>收据序号（单调递增，保证同一会话内生成的收据不会互相碰撞）。</summary>
        private int _receiptSequence;

        /// <summary>购买日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置；满员时指向最旧的一条。</summary>
        private int _logNext;

        /// <summary>用默认目录创建可用假商店。</summary>
        /// <param name="ledger">购买台账（非 <c>null</c>；收据校验器由台账持有）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="ledger"/> 为 <c>null</c>。</exception>
        public ConsoleIapService(IapPurchaseLedger ledger)
            : this(ledger, CreateDefaultCatalog(), available: true)
        {
        }

        /// <summary>用默认目录创建假商店，并指定可用性（验收离线降级路径）。</summary>
        /// <param name="ledger">购买台账（非 <c>null</c>）。</param>
        /// <param name="available">是否可用；<c>false</c> 时购买/恢复一律返回 <see cref="IapPurchaseState.StoreUnavailable"/>。</param>
        /// <exception cref="ArgumentNullException"><paramref name="ledger"/> 为 <c>null</c>。</exception>
        public ConsoleIapService(IapPurchaseLedger ledger, bool available)
            : this(ledger, CreateDefaultCatalog(), available)
        {
        }

        /// <summary>用自定义目录创建假商店。</summary>
        /// <param name="ledger">购买台账（非 <c>null</c>）。</param>
        /// <param name="catalog">商品目录（非 <c>null</c>；本服务会拷贝一份，外部改数组不影响内部）。</param>
        /// <param name="available">是否可用。</param>
        /// <exception cref="ArgumentNullException"><paramref name="ledger"/> 或 <paramref name="catalog"/> 为 <c>null</c>。</exception>
        public ConsoleIapService(IapPurchaseLedger ledger, IapProduct[] catalog, bool available = true)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _catalog = CopyCatalog(catalog ?? throw new ArgumentNullException(nameof(catalog)));
            _available = available;
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <summary>默认商品目录（每次调用返回新数组，调用方可安全持有/改写）。</summary>
        /// <returns>三件默认商品：60 金币、300 金币、去广告。</returns>
        public static IapProduct[] CreateDefaultCatalog()
        {
            return new[]
            {
                new IapProduct(ProductGold60, IapProductKind.Consumable, "$0.99", GoldItemId, 60),
                new IapProduct(ProductGold300, IapProductKind.Consumable, "$4.99", GoldItemId, 300),
                new IapProduct(ProductNoAds, IapProductKind.NonConsumable, "$2.99", NoAdsItemId, 1)
            };
        }

        /// <inheritdoc />
        /// <remarks>返回目录副本：调用方改写返回数组不会影响本服务后续的购买与恢复。</remarks>
        public IapProduct[] GetCatalog()
        {
            return CopyCatalog(_catalog);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 状态映射：
        /// <list type="bullet">
        /// <item>未知商品 → <see cref="IapPurchaseState.StoreUnavailable"/>（原因 <see cref="ReasonUnknownProduct"/>）。</item>
        /// <item>拥有型商品已购买 → <see cref="IapPurchaseState.AlreadyOwned"/>（不再生成收据、不发货）。</item>
        /// <item>台账拒绝（收据校验失败/收据重复/已拥有）→ <see cref="IapPurchaseState.InvalidReceipt"/>
        /// 或 <see cref="IapPurchaseState.AlreadyOwned"/>，原因直接透传台账常量。</item>
        /// <item>其余 → <see cref="IapPurchaseState.Purchased"/>，收据形如 <c>console-com.demo.gold60#7</c>。</item>
        /// </list>
        /// </remarks>
        public IapPurchaseResult Purchase(string productId)
        {
            if (!_available)
            {
                AppendLog("purchase " + Describe(productId) + " -> StoreUnavailable");
                return new IapPurchaseResult(
                    IapPurchaseState.StoreUnavailable, productId, string.Empty, ReasonUnavailable);
            }

            IapProduct product;
            if (!TryFindProduct(productId, out product))
            {
                AppendLog("purchase " + Describe(productId) + " -> StoreUnavailable (unknown_product)");
                return new IapPurchaseResult(
                    IapPurchaseState.StoreUnavailable, productId, string.Empty, ReasonUnknownProduct);
            }

            // 只有拥有型商品才预检"已拥有"；消耗品每次都是新的一笔交易。
            if (product.Kind != IapProductKind.Consumable && _ledger.IsOwned(product.ProductId))
            {
                AppendLog("purchase " + product.ProductId + " -> AlreadyOwned");
                return new IapPurchaseResult(
                    IapPurchaseState.AlreadyOwned, product.ProductId, string.Empty, IapPurchaseLedger.ReasonAlreadyOwned);
            }

            _receiptSequence++;
            string receipt = "console-" + product.ProductId + "#"
                + _receiptSequence.ToString(CultureInfo.InvariantCulture);

            string reason;
            if (!_ledger.TryRecord(in product, receipt, out reason))
            {
                // 台账已经判过"已拥有"的情况理论上不可达（上面预检过），但并发/外部状态变化仍要如实映射。
                IapPurchaseState state = reason == IapPurchaseLedger.ReasonAlreadyOwned
                    ? IapPurchaseState.AlreadyOwned
                    : IapPurchaseState.InvalidReceipt;

                AppendLog("purchase " + product.ProductId + " #"
                    + _receiptSequence.ToString(CultureInfo.InvariantCulture) + " -> " + state + " (" + reason + ")");
                return new IapPurchaseResult(state, product.ProductId, receipt, reason);
            }

            if (product.Kind != IapProductKind.Consumable)
            {
                _ownedReceipts[product.ProductId] = receipt;
            }

            AppendLog("purchase " + product.ProductId + " #"
                + _receiptSequence.ToString(CultureInfo.InvariantCulture) + " -> Purchased");
            return new IapPurchaseResult(
                IapPurchaseState.Purchased, product.ProductId, receipt, ReasonPurchased);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <b>恢复口径</b>：只返回"拥有型且本实例确实开过收据"的商品（消耗品不参与恢复，商店本身也不提供消耗品补单）。
        /// 收据复用购买时生成的那一张，因此可以再次交给 <see cref="IReceiptValidator"/> 校验。
        /// 已拥有但收据不可知（例如台账由外部单独填充）的商品会被跳过——没有收据的"恢复"对发货方没有意义。
        /// </remarks>
        public IapPurchaseResult[] RestorePurchases()
        {
            if (!_available)
            {
                AppendLog("restore -> StoreUnavailable");
                return new[]
                {
                    new IapPurchaseResult(
                        IapPurchaseState.StoreUnavailable, string.Empty, string.Empty, ReasonUnavailable)
                };
            }

            var restored = new List<IapPurchaseResult>();
            for (int i = 0; i < _catalog.Length; i++)
            {
                IapProduct product = _catalog[i];
                if (product.Kind == IapProductKind.Consumable)
                {
                    continue;
                }

                // TryGetValue 的 out 参数带 MaybeNullWhen(false)：声明为可空，成功分支内可空流分析会收窄。
                string? receipt;
                if (_ledger.IsOwned(product.ProductId) && _ownedReceipts.TryGetValue(product.ProductId, out receipt))
                {
                    restored.Add(new IapPurchaseResult(
                        IapPurchaseState.Purchased, product.ProductId, receipt, ReasonRestored));
                }
            }

            AppendLog("restore -> " + restored.Count.ToString(CultureInfo.InvariantCulture) + " item(s)");
            return restored.ToArray();
        }

        /// <summary>取走并清空购买日志（冒烟/排障用；取走即清空，避免同一批事件被重复打印）。</summary>
        /// <returns>按发生顺序排列的日志条目。</returns>
        public IReadOnlyList<string> DrainLog()
        {
            var entries = new List<string>(_logCount);
            int start = _logCount < LogCapacity ? 0 : _logNext;
            for (int i = 0; i < _logCount; i++)
            {
                int index = start + i;
                if (index >= LogCapacity)
                {
                    index -= LogCapacity;
                }

                entries.Add(_log[index]);
            }

            Array.Clear(_log, 0, _log.Length);
            _logCount = 0;
            _logNext = 0;
            return entries;
        }

        /// <summary>按 ID 查商品（目录通常只有个位数条目，线性扫描即可，不建字典也不分配）。</summary>
        private bool TryFindProduct(string? productId, out IapProduct product)
        {
            for (int i = 0; i < _catalog.Length; i++)
            {
                if (string.Equals(_catalog[i].ProductId, productId, StringComparison.Ordinal))
                {
                    product = _catalog[i];
                    return true;
                }
            }

            product = default;
            return false;
        }

        /// <summary>复制目录数组（结构体是值类型，只需复制数组本身）。</summary>
        private static IapProduct[] CopyCatalog(IapProduct[] catalog)
        {
            var copy = new IapProduct[catalog.Length];
            Array.Copy(catalog, copy, catalog.Length);
            return copy;
        }

        /// <summary>日志里的商品描述：非法/空 ID 也要可读，不能因为拼日志而抛异常。</summary>
        private static string Describe(string? productId)
        {
            if (string.IsNullOrEmpty(productId))
            {
                return "product=(empty)";
            }

            return productId;
        }

        /// <summary>写入一条日志；满员时覆盖最旧的一条（仅诊断路径，允许分配）。</summary>
        private void AppendLog(string entry)
        {
            _log[_logNext] = entry;

            _logNext++;
            if (_logNext == LogCapacity)
            {
                _logNext = 0;
            }

            if (_logCount < LogCapacity)
            {
                _logCount++;
            }
        }
    }
}
