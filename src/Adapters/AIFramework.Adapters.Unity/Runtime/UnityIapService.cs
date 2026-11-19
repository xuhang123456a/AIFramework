#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 侧 IAP 服务：<b>不接任何真实商店</b>，只提供两条明确路径——
    /// "明确不可用"（<see cref="IsAvailable"/> 为 <c>false</c> 时所有调用返回
    /// <see cref="IapPurchaseState.StoreUnavailable"/>）与"用 <see cref="IStoragePort"/> 做本地假商店"。
    /// <para>
    /// <b>为什么默认不可用</b>：IAP 最容易变成"看起来能买、其实没扣款/没发货"的功能。
    /// 在 App Store / Google Play 真正接线（初始化、商品拉取、验签、补单回调）之前，
    /// 宁可让上层如实看到"商店不可用"，也不要在演示里制造"已经支付成功"的假象。
    /// </para>
    /// <para>
    /// <b>本地假商店的落盘格式</b>：购买记录写端口键 <c>&lt;storeId&gt;.iap</c>（UTF-8 文本，每行
    /// <c>productId|receipt</c>），构造时读回并据此回答"已拥有"，<see cref="RestorePurchases"/>
    /// 直接复用文件里的收据。它模拟的是"商店侧的已购清单"，因此消耗品（一次性到账）不写入文件、也不参与恢复。
    /// </para>
    /// <para>
    /// <b>绝不抛异常（除构造参数错误）</b>：读写失败、商品未知、收据被拒一律降级成状态结果；
    /// 一次存储抖动或一张坏收据不允许把购买流程打断。文件读失败等价于"本地没有购买记录"。
    /// </para>
    /// <para>
    /// <b>真实接入要补什么</b>：商店 SDK 初始化与商品目录拉取、异步购买结果回调、服务端收据验签、
    /// 订阅续费/到期、退款与掉单对账。这些都属于适配层后续工作，<see cref="IIapService"/> 的调用点语义不变。
    /// </para>
    /// <para>线程模型：非线程安全。Unity 主线程串行调用（与 <c>UnityStoragePort</c> 一致）。</para>
    /// </summary>
    public sealed class UnityIapService : IIapService
    {
        /// <summary>默认假商店 ID（端口键即 <c>unitymock.iap</c>，符合本地存档端口 <c>[A-Za-z0-9._-]</c> 白名单）。</summary>
        public const string DefaultStoreId = "unitymock";

        /// <summary>本地假商店文件后缀（端口键 = <c>storeId + 该后缀</c>）。</summary>
        public const string StoreFileSuffix = ".iap";

        /// <summary>本地假商店文件里的字段分隔符（商品 ID 与收据都不允许包含它）。</summary>
        public const char RecordSeparator = '|';

        /// <summary>默认目录：金币商品到账的道具 ID（背包 itemId；与 Console 假商店保持一致）。</summary>
        public const int GoldItemId = 1001;

        /// <summary>默认目录：去广告商品到账的道具 ID（背包 itemId；与 Console 假商店保持一致）。</summary>
        public const int NoAdsItemId = 2001;

        /// <summary>默认目录商品：60 金币。</summary>
        public const string ProductGold60 = "com.demo.gold60";

        /// <summary>默认目录商品：300 金币。</summary>
        public const string ProductGold300 = "com.demo.gold300";

        /// <summary>默认目录商品：去广告（非消耗品）。</summary>
        public const string ProductNoAds = "com.demo.no_ads";

        /// <summary>不可用原因：未注入存储/被配置为离线/真实商店未接线。</summary>
        public const string ReasonUnavailable = "store unavailable";

        /// <summary>不可用原因：商品 ID 不在目录里。</summary>
        public const string ReasonUnknownProduct = "unknown_product";

        /// <summary>成功原因：购买成功（本地假商店已记录收据）。</summary>
        public const string ReasonPurchased = "purchased";

        /// <summary>成功原因：补单找回的历史购买。</summary>
        public const string ReasonRestored = "restored";

        /// <summary>本地假商店端口；<c>null</c> 表示"没有商店后端"（只能回答不可用）。</summary>
        private readonly IStoragePort? _storage;

        /// <summary>购买台账（收据去重 + 拥有型一次性发放）。</summary>
        private readonly IapPurchaseLedger _ledger;

        /// <summary>商品目录副本。</summary>
        private readonly IapProduct[] _catalog;

        /// <summary>本地假商店端口键（<c>&lt;storeId&gt;.iap</c>）。</summary>
        private readonly string _storeKey;

        /// <summary>是否可用（构造后不可变；没有存储后端时恒为 <c>false</c>）。</summary>
        private readonly bool _available;

        /// <summary>已拥有（拥有型商品）的收据镜像：商品 ID → 收据。</summary>
        private readonly Dictionary<string, string> _owned =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>收据序号（同一会话内单调递增，保证生成的收据唯一）。</summary>
        private int _receiptSequence;

        /// <summary>
        /// 创建 Unity 侧 IAP 服务。
        /// <para>不传 <paramref name="storage"/> 时<see cref="IsAvailable"/> 恒为 <c>false</c>：
        /// 没有商店后端就不假装能买。</para>
        /// </summary>
        /// <param name="storage">本地假商店端口（建议 <c>new UnityStoragePort("iap")</c>）；<c>null</c> 表示不接线。</param>
        /// <param name="available">是否启用；<c>false</c> 时所有操作返回 <see cref="IapPurchaseState.StoreUnavailable"/>。</param>
        /// <param name="storeId">假商店 ID（构成端口键 <c>&lt;storeId&gt;.iap</c>）；只允许 <c>[A-Za-z0-9._-]</c>。</param>
        /// <param name="catalog">商品目录；<c>null</c> 时使用默认 3 件演示商品。</param>
        /// <param name="validator">收据校验器；<c>null</c> 时使用"非空且不超长即通过"的本地 mock。</param>
        /// <exception cref="ArgumentException"><paramref name="storeId"/> 为空或含非法字符（编程错误，装配期暴露）。</exception>
        public UnityIapService(
            IStoragePort? storage = null,
            bool available = true,
            string storeId = DefaultStoreId,
            IapProduct[]? catalog = null,
            IReceiptValidator? validator = null)
        {
            ValidateStoreId(storeId);

            _storage = storage;
            _available = available && storage != null;
            _storeKey = storeId + StoreFileSuffix;
            _catalog = CopyCatalog(catalog ?? CreateDefaultCatalog());
            _ledger = new IapPurchaseLedger(validator ?? new LocalMockReceiptValidator());

            LoadOwnedFromStore();
        }

        /// <summary>本地假商店端口键（<c>&lt;storeId&gt;.iap</c>；诊断用）。</summary>
        public string StoreKey
        {
            get { return _storeKey; }
        }

        /// <inheritdoc />
        /// <remarks>只有"被启用"且"存在存储后端"时才为 <c>true</c>：没有后端必须如实报告不可用。</remarks>
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <summary>默认商品目录（与 Console 假商店同一份；每次调用返回新数组）。</summary>
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
        public IapProduct[] GetCatalog()
        {
            return CopyCatalog(_catalog);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 未接线/离线 → <see cref="IapPurchaseState.StoreUnavailable"/>；
        /// 未知商品 → <see cref="IapPurchaseState.StoreUnavailable"/>（原因 <see cref="ReasonUnknownProduct"/>）；
        /// 拥有型已拥有 → <see cref="IapPurchaseState.AlreadyOwned"/>；
        /// 收据被台账拒绝 → <see cref="IapPurchaseState.InvalidReceipt"/>。
        /// 成功后收据形如 <c>unity-com.demo.gold60#3</c>，拥有型商品额外写入本地假商店文件。
        /// </remarks>
        public IapPurchaseResult Purchase(string productId)
        {
            if (!_available)
            {
                return new IapPurchaseResult(
                    IapPurchaseState.StoreUnavailable, productId, string.Empty, ReasonUnavailable);
            }

            IapProduct product;
            if (!TryFindProduct(productId, out product))
            {
                return new IapPurchaseResult(
                    IapPurchaseState.StoreUnavailable, productId, string.Empty, ReasonUnknownProduct);
            }

            if (product.Kind != IapProductKind.Consumable && _owned.ContainsKey(product.ProductId))
            {
                return new IapPurchaseResult(
                    IapPurchaseState.AlreadyOwned, product.ProductId, string.Empty, IapPurchaseLedger.ReasonAlreadyOwned);
            }

            _receiptSequence++;
            string receipt = "unity-" + product.ProductId + "#"
                + _receiptSequence.ToString(CultureInfo.InvariantCulture);

            string reason;
            if (!_ledger.TryRecord(in product, receipt, out reason))
            {
                // 没有真实支付渠道，失败只可能来自收据格式/校验或重复（本地记录与服务断档）。
                IapPurchaseState state = reason == IapPurchaseLedger.ReasonAlreadyOwned
                    ? IapPurchaseState.AlreadyOwned
                    : IapPurchaseState.InvalidReceipt;

                return new IapPurchaseResult(state, product.ProductId, receipt, reason);
            }

            if (product.Kind != IapProductKind.Consumable)
            {
                _owned[product.ProductId] = receipt;
                PersistOwned();
            }

            return new IapPurchaseResult(
                IapPurchaseState.Purchased, product.ProductId, receipt, ReasonPurchased);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 从本地假商店文件读回的已购拥有型商品逐条返回（收据复用文件里的原始值），
        /// 消耗品不参与恢复。文件读不到/为空时返回空数组（不是错误）。
        /// </remarks>
        public IapPurchaseResult[] RestorePurchases()
        {
            if (!_available)
            {
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
                if (_owned.TryGetValue(product.ProductId, out receipt))
                {
                    restored.Add(new IapPurchaseResult(
                        IapPurchaseState.Purchased, product.ProductId, receipt, ReasonRestored));
                }
            }

            return restored.ToArray();
        }

        /// <summary>
        /// 构造期读回本地假商店文件，重建"已拥有"表。
        /// <para>读失败/文件损坏/行格式非法都按"没有该条记录"跳过：恢复购买少一条是可诊断的，
        /// 抛异常打断启动不可接受。</para>
        /// </summary>
        private void LoadOwnedFromStore()
        {
            IStoragePort? storage = _storage;
            if (!_available || storage == null)
            {
                return;
            }

            byte[]? bytes;
            try
            {
                bytes = storage.Read(_storeKey);
            }
            catch (Exception)
            {
                return;
            }

            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            string text = Encoding.UTF8.GetString(bytes);
            int start = 0;
            while (start < text.Length)
            {
                int lineEnd = text.IndexOf('\n', start);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }

                string line = text.Substring(start, lineEnd - start).Trim();
                start = lineEnd + 1;

                if (line.Length == 0)
                {
                    continue;
                }

                int separator = line.IndexOf(RecordSeparator);
                if (separator <= 0 || separator >= line.Length - 1)
                {
                    continue;
                }

                string productId = line.Substring(0, separator);
                string receipt = line.Substring(separator + 1);

                // 只认目录里的拥有型商品：未知 ID / 消耗品的脏行一律忽略（避免脏数据变成免费商品）。
                if (IsOneTimeProduct(productId))
                {
                    _owned[productId] = receipt;
                }
            }
        }

        /// <summary>把"已拥有"表整体写回本地假商店文件（先负载后落盘，失败只影响恢复，不回滚本次购买）。</summary>
        private void PersistOwned()
        {
            IStoragePort? storage = _storage;
            if (!_available || storage == null)
            {
                return;
            }

            // 按目录顺序输出，保证同一状态落盘内容稳定（字典枚举顺序不保证稳定，会给 diff 制造噪声）。
            var builder = new StringBuilder(_owned.Count * 64);
            for (int i = 0; i < _catalog.Length; i++)
            {
                string? receipt;
                if (_owned.TryGetValue(_catalog[i].ProductId, out receipt))
                {
                    builder.Append(_catalog[i].ProductId);
                    builder.Append(RecordSeparator);
                    builder.Append(receipt);
                    builder.Append('\n');
                }
            }

            try
            {
                storage.Write(_storeKey, Encoding.UTF8.GetBytes(builder.ToString()));
            }
            catch (Exception)
            {
                // 写失败：本次购买仍然算成功（商店侧已扣款），只是重启后恢复不到这条记录。
                // 本地假商店没有重试队列，真实实现必须在这里补"待重试"语义。
            }
        }

        /// <summary>该商品是否是目录里的拥有型商品（非消耗品/订阅；未知 ID 返回 <c>false</c>）。</summary>
        private bool IsOneTimeProduct(string productId)
        {
            IapProduct product;
            return TryFindProduct(productId, out product) && product.Kind != IapProductKind.Consumable;
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

        /// <summary>
        /// 校验假商店 ID：它会直接拼成 <see cref="IStoragePort"/> 的键，必须满足本地存储的白名单，
        /// 否则构造一个永远写不进去的商店比直接报错更难排障。
        /// </summary>
        private static void ValidateStoreId(string? storeId)
        {
            if (string.IsNullOrEmpty(storeId))
            {
                throw new ArgumentException("假商店 ID 不能为空。", nameof(storeId));
            }

            for (int i = 0; i < storeId.Length; i++)
            {
                char c = storeId[i];
                bool allowed = (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '.'
                    || c == '_'
                    || c == '-';
                if (!allowed)
                {
                    throw new ArgumentException(
                        "假商店 ID 只允许 [A-Za-z0-9._-]，非法字符 '" + c + "'。", nameof(storeId));
                }
            }

            if (storeId == "." || storeId.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException("假商店 ID 不允许是 \".\" 或包含 \"..\"（目录穿越防护）。", nameof(storeId));
            }

            if (storeId.EndsWith(".tmp", StringComparison.Ordinal))
            {
                throw new ArgumentException("假商店 ID 不允许以 \".tmp\" 结尾（与临时文件同名空间冲突）。", nameof(storeId));
            }
        }

        /// <summary>
        /// 本地 mock 收据校验器：**不是验签**，只拦明显非法的收据形状（空/超长）。
        /// 真实实现必须换成服务端验签（App Store / Google Play 的收据校验接口），
        /// 否则客户端改内存就能"买"任何东西。
        /// </summary>
        private sealed class LocalMockReceiptValidator : IReceiptValidator
        {
            /// <inheritdoc />
            public bool Validate(string productId, string receipt)
            {
                if (string.IsNullOrEmpty(productId) || string.IsNullOrEmpty(receipt))
                {
                    return false;
                }

                return receipt.Length <= IapPurchaseLedger.MaxReceiptLength;
            }
        }
    }
}
