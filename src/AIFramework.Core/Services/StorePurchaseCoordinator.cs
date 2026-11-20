#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 一次 IAP 购买到"发奖 + 埋点"的完整闭环结果，<b>只读</b>。
    /// <para>
    /// <b><see cref="Granted"/> 的口径是"这次购物车已经结算完"</b>，不是"商店扣款成功"：
    /// 台账登记通过、该发的道具发出去（或该商品本来就不发道具）、埋点已记，才算 <c>true</c>。
    /// 商店说 <c>Purchased</c> 但台账拒收/指令没送达时必须是 <c>false</c>——否则 UI 会对玩家说
    /// "购买成功"，而背包里什么都没有。
    /// </para>
    /// <para>
    /// <b><see cref="ItemId"/> / <see cref="Count"/> 只在 <see cref="Granted"/> 为 <c>true</c> 时有意义</b>，
    /// 失败时恒为 <c>0</c>：失败路径没有"发了什么"可言，留半截值只会让调用方误以为可以照着补发。
    /// 无道具商品（订阅、功能解锁）成功时同样是 <c>0/0</c>，此时 <see cref="Reason"/> 为
    /// <c>granted no item</c>，与失败靠 <see cref="Granted"/> 区分，不靠数值区分。
    /// </para>
    /// <para>
    /// <b><see cref="Reason"/> 是诊断文本，不是业务判定依据</b>：业务分支只看 <see cref="Granted"/>。
    /// 但它的取值是稳定的——商店侧失败直接用 <see cref="IapPurchaseState"/> 的枚举名，
    /// 台账拒收直接用 <see cref="IapPurchaseLedger"/> 的常量，协调器自己的原因用
    /// <see cref="StorePurchaseCoordinator"/> 上的 <c>Reason*</c> 常量——因此日志/看板/测试可以安全地按它聚合。
    /// </para>
    /// </summary>
    public readonly struct StorePurchaseOutcome
    {
        /// <summary>构造一次闭环结果。</summary>
        /// <param name="granted">本次是否已完整闭环（台账登记 + 发货 + 埋点）。</param>
        /// <param name="itemId">实际发放的道具 ID（未发放时为 0）。</param>
        /// <param name="count">实际发放的数量（未发放时为 0）。</param>
        /// <param name="reason">诊断原因（<c>null</c> 归一化为空串；禁止作为业务判定依据）。</param>
        public StorePurchaseOutcome(bool granted, int itemId, int count, string reason)
        {
            Granted = granted;
            ItemId = itemId;
            Count = count;
            Reason = reason ?? string.Empty;
        }

        /// <summary>本次是否已完整闭环（<c>true</c> 才允许 UI 展示"购买成功"）。</summary>
        public bool Granted { get; }

        /// <summary>实际发放的道具 ID（<c>0</c> = 本次没有发放道具）。</summary>
        public int ItemId { get; }

        /// <summary>实际发放的数量（<c>0</c> = 本次没有发放道具）。</summary>
        public int Count { get; }

        /// <summary>诊断原因（可能为空串；取值见类型注释的稳定性约定）。</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// IAP 购买协调器：把三个**互相不知道对方存在**的端口串成一条真实商业链路——
    /// <see cref="IIapService"/>（商店扣款）→ <see cref="IapPurchaseLedger"/>（收据去重/拥有型一次性发放）
    /// → <see cref="ICqeBus"/> 上的 <see cref="GrantItemCommand"/>（背包发货）→ <see cref="IAnalyticsService"/>（收入埋点）。
    /// <para>
    /// <b>为什么在 Core 而不是 Feature.Shop</b>：<see cref="IapPurchaseLedger"/> 本身就在 Core，
    /// 而 Features 只允许引用 Contracts（AIF004）。把它放进任何切片都会逼切片去引用 Core；
    /// 放进 Contracts 又会让契约层带上"结算流程"这种实现细节。Core 是唯一既不越界又能被
    /// Console/Unity 两个装配入口共用、还能被无头单测逐条钉死的位置。
    /// </para>
    /// <para>
    /// <b>接线必须给协调器一个独立的台账实例</b>：Console/Unity 的假商店实现
    /// （<c>ConsoleIapService</c> / <c>UnityIapService</c>）自己持有一份台账，并在 <c>Purchase</c> 内部就调用
    /// <c>TryRecord</c> 登记收据（它们模拟的是"商店侧的已购清单"）。如果把这个同一实例再交给协调器，
    /// 每笔购买都会被判成 <c>receipt already consumed</c>，永远发不出货。真实商店（StoreKit / Play Billing）
    /// 不参与台账，因此正确形态是两份：<b>商店侧台账（适配器私有）</b> + <b>游戏侧发放台账（协调器持有）</b>。
    /// </para>
    /// <para>
    /// <b>到账数值的取值顺序（目录优先，显式参数补空缺）</b>：
    /// <list type="number">
    /// <item>商品在目录里且 <see cref="IapProduct.GrantItemId"/> / <see cref="IapProduct.GrantCount"/> 都 &gt; 0 → 用商品自身配置。</item>
    /// <item>否则若调用方用重载给了 <c>overrideCount &gt; 0</c> → 用 <c>overrideItemId</c> 与 <c>overrideCount</c>；
    /// <c>overrideItemId &lt;= 0</c> 时道具 ID 退回构造参数 <c>goldItemId</c>（"这个 SKU 就是给 N 金币"的场景）。</item>
    /// <item>两者都没有 → 本次不发道具（订阅/功能解锁），但台账照常登记、埋点照常记，结果仍是
    /// <see cref="StorePurchaseOutcome.Granted"/> 为 <c>true</c>。</item>
    /// </list>
    /// 目录优先是刻意的：到账内容是**结算事实**，只能有一个事实源（目录/后台配置）。
    /// 若让调用方传参覆盖，一次误传就能把"首充双倍"变成永久双倍。
    /// </para>
    /// <para>
    /// <b>失败即不发物品</b>：商店非 <see cref="IapPurchaseState.Purchased"/>、台账拒收、发奖指令没送达，
    /// 三条路径都在 <c>GrantItemCommand</c> 之前返回，绝不"先发了再说"。
    /// 其中<b>台账拒收最重要</b>：重复收据与已拥有拥有型商品都在这里被拦下，这是防双发的唯一闸门。
    /// </para>
    /// <para>
    /// <b>已知缺口（本类不掩盖）</b>：台账是内存态，跨启动不落盘，因此"重装后第一次恢复"会按本地缺失补发，
    /// 而"同一台设备反复恢复"靠的是本次进程内的台账。持久化（把已发放集合写进存档）属于装配层后续工作；
    /// 另外"台账已登记但发奖指令失败"是**不可回滚**状态（收据已被消耗），补偿只能靠上层 outbox/服务端补单，
    /// 因此这类原因单独用 <see cref="ReasonGrantFailed"/> 标出，见 <see cref="Settle"/> 内注释。
    /// </para>
    /// <para>线程模型：非线程安全。购买发起、回调处理与发货都必须串行化到同一线程（与 <see cref="IapPurchaseLedger"/> 一致）。</para>
    /// </summary>
    public sealed class StorePurchaseCoordinator
    {
        /// <summary>
        /// 发奖指令使用的因果标签：沿用 <see cref="MetaReasonTags.ShopGrant"/>（6102，"商店：发货"）。
        /// <para>
        /// <b>为什么不是新常量</b>：<see cref="MetaReasonTags"/> 里没有 IAP 专用标签，而契约层不许在本任务里改动；
        /// 6102 的语义（商店发货）与 IAP 到账完全一致，选它是"最接近且不误导"的一个。
        /// 后续若要区分"金币购买 vs 真实货币购买"，应在 Contracts 追加新常量（例如 6103）再改这里一处。
        /// </para>
        /// </summary>
        public const int GrantReasonTagId = MetaReasonTags.ShopGrant;

        /// <summary>成功原因：台账已登记且道具已发出。</summary>
        public const string ReasonGranted = "granted";

        /// <summary>成功原因：台账已登记，但该商品本来就不发道具（订阅/功能解锁）。</summary>
        public const string ReasonGrantedNoItem = "granted no item";

        /// <summary>
        /// 失败原因：台账**已**登记，但 <see cref="GrantItemCommand"/> 没有送达（未注册处理器或处理器抛异常）。
        /// <para>收据已被消耗，重放同一张收据不会再发；需要上层按此原因补偿（outbox/服务端补单）。</para>
        /// </summary>
        public const string ReasonGrantFailed = "grant failed";

        /// <summary>失败原因：商品 ID 为 <c>null</c>/空串（调用方参数错误，未触达商店）。</summary>
        public const string ReasonInvalidProduct = "invalid product";

        /// <summary>
        /// 失败原因：商店说成功，但目录里没有这件商品，调用方也没给兜底道具 → 不知道发什么，
        /// 且**刻意不登记**收据（登记即消耗，钱花了却永远发不出货；不登记至少还能由补单流程重新结算）。
        /// </summary>
        public const string ReasonUnknownProduct = "unknown product";

        /// <summary>失败原因：商店实现抛了异常（违反"禁止抛异常"契约，按失败处理，绝不放行发货）。</summary>
        public const string ReasonStoreFailed = "store failed";

        /// <summary>价格档位取模基数：把 32 位稳定哈希折成 <c>0..999</c> 的档位，便于看板分组。</summary>
        private const int PriceTierModulus = 1000;

        /// <summary>FNV-1a 32 位偏移基（写死在这里是为了"跨进程/跨平台稳定"，见 <see cref="PriceTierOf"/>）。</summary>
        private const uint FnvOffsetBasis = 2166136261u;

        /// <summary>FNV-1a 32 位质数。</summary>
        private const uint FnvPrime = 16777619u;

        /// <summary>IAP 端口；构造后不可变。</summary>
        private readonly IIapService _iap;

        /// <summary>游戏侧发放台账（收据去重 + 拥有型一次性发放）；<b>不是</b>商店侧的已购清单。</summary>
        private readonly IapPurchaseLedger _ledger;

        /// <summary>CQE 总线（发 <see cref="GrantItemCommand"/>）。</summary>
        private readonly ICqeBus _bus;

        /// <summary>埋点端口（<c>null</c> = 不埋点，链路照常闭环）。</summary>
        private readonly IAnalyticsService? _analytics;

        /// <summary>兜底到账道具 ID（默认 <see cref="MetaItemIds.Gold"/>）：只在调用方只给了数量时使用。</summary>
        private readonly int _defaultGrantItemId;

        /// <summary>创建购买协调器。</summary>
        /// <param name="iap">IAP 端口（非 <c>null</c>）。</param>
        /// <param name="ledger">游戏侧发放台账（非 <c>null</c>；<b>不要</b>与商店适配器内部那份共用，见类型注释）。</param>
        /// <param name="bus">CQE 总线（非 <c>null</c>；必须已注册 <see cref="GrantItemCommand"/> 处理器）。</param>
        /// <param name="analytics">埋点端口；<c>null</c> 表示不埋点（链路行为不变，只是没有事件）。</param>
        /// <param name="goldItemId">兜底到账道具 ID（默认 1 = <see cref="MetaItemIds.Gold"/>；<c>&lt;= 0</c> 时禁用兜底）。</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="iap"/> / <paramref name="ledger"/> / <paramref name="bus"/> 为 <c>null</c>（编程错误，装配期暴露）。
        /// </exception>
        public StorePurchaseCoordinator(
            IIapService iap,
            IapPurchaseLedger ledger,
            ICqeBus bus,
            IAnalyticsService? analytics = null,
            int goldItemId = MetaItemIds.Gold)
        {
            _iap = iap ?? throw new ArgumentNullException(nameof(iap));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _analytics = analytics;
            _defaultGrantItemId = goldItemId;
        }

        /// <summary>
        /// 商品目录（用于商店 UI 陈列）。
        /// <para>
        /// <b>每次访问都向商店要一次最新目录</b>：真实商店的商品是异步拉回来的，构造期快照会把
        /// "还没拉到"永久锁成空目录。代价是每次访问一次分配，购买/陈列不是热路径，可以接受。
        /// </para>
        /// <para>
        /// <b>返回的一定是新数组</b>：契约要求实现返回副本，这里再复制一次（二次防御），
        /// 调用方改写返回数组既不会污染协调器，也不会影响商店后续的购买与恢复。
        /// 商店实现抛异常或返回 <c>null</c> 时返回空数组，绝不把异常抛给 UI。
        /// </para>
        /// </summary>
        public IapProduct[] Catalog
        {
            get { return SnapshotCatalog(); }
        }

        /// <summary>
        /// 购买并闭环：IAP 成功 → 台账登记去重 → <see cref="GrantItemCommand"/> → 埋点；
        /// 任何一步失败都不发物品。
        /// </summary>
        /// <param name="productId">商店商品 ID。</param>
        /// <returns>闭环结果（<b>永不抛异常</b>）。</returns>
        public StorePurchaseOutcome Purchase(string productId)
        {
            return PurchaseCore(productId, 0, 0);
        }

        /// <summary>
        /// 购买并闭环，并用调用方给的道具**补足目录里没有配置的到账内容**
        /// （商品 ID 直接映射道具的场景：目录只列 SKU 与价格，到账数值由这里决定）。
        /// <para>
        /// <b>只在目录缺配置时生效</b>：商品自身 <see cref="IapProduct.GrantItemId"/> /
        /// <see cref="IapProduct.GrantCount"/> 都 &gt; 0 时以目录为准（见类型注释的取值顺序）。
        /// <paramref name="overrideCount"/> <c>&lt;= 0</c> 时本次重载参数完全忽略。
        /// </para>
        /// <para>
        /// <b>目录里也找不到这件商品时</b>：按 <see cref="IapProductKind.Consumable"/> 合成一件（这类 SKU 通常是
        /// 货币/体力包，本来就允许反复购买）；拥有型商品必须在目录里配置，否则"只发一次"只能靠商店侧拦截。
        /// </para>
        /// </summary>
        /// <param name="productId">商店商品 ID。</param>
        /// <param name="overrideItemId">兜底到账道具 ID（<c>&lt;= 0</c> 时用构造参数 <c>goldItemId</c>）。</param>
        /// <param name="overrideCount">兜底到账数量（<c>&lt;= 0</c> 时本重载不起作用）。</param>
        /// <returns>闭环结果（<b>永不抛异常</b>）。</returns>
        public StorePurchaseOutcome Purchase(string productId, int overrideItemId, int overrideCount)
        {
            return PurchaseCore(productId, overrideItemId, overrideCount);
        }

        /// <summary>
        /// 恢复购买：对商店回报的每条 <see cref="IapPurchaseState.Purchased"/> 走同样的
        /// "台账登记 → 发货 → 埋点"，<b>只补本地缺失，不重复发</b>。
        /// <para>
        /// <b>返回值的口径 = 本次恢复"确认"的拥有型商品件数</b>，包含两类：
        /// ① 本地台账没有、本次真补发了的；② 本地台账已经有、这次只做确认（不发物品、不埋点）的。
        /// 这样同一台设备第一次恢复与第二次恢复都返回同一个数字，UI 才能区分"没有可恢复项（0）"
        /// 与"都已恢复（N）"；若只数真发件数，第二次必然返回 0，玩家会看到"没有可恢复的商品"。
        /// </para>
        /// <para>
        /// <b>刻意不处理的输入</b>：消耗品（商店不提供消耗品补单，硬发就是刷道具漏洞）、
        /// 目录里查不到的商品（不知道发什么，登记了也发不出货）、非 <c>Purchased</c> 状态（商店不可用等）。
        /// 商店实现抛异常或返回 <c>null</c> 时按 0 处理，不把异常抛给 UI。
        /// </para>
        /// </summary>
        /// <returns>本次恢复确认的件数（<c>0</c> = 没有可恢复项 / 商店不可用）。</returns>
        public int RestorePurchases()
        {
            IapPurchaseResult[]? results;
            try
            {
                results = _iap.RestorePurchases();
            }
            catch (Exception)
            {
                // 契约要求"永不抛异常"，实现违约时按"没有可恢复项"处理：恢复是玩家主动点的按钮，
                // 一次商店抖动不该让游戏崩在商店页。
                return 0;
            }

            if (results == null || results.Length == 0)
            {
                return 0;
            }

            IapProduct[] catalog = SnapshotCatalog();
            int restored = 0;
            for (int i = 0; i < results.Length; i++)
            {
                IapPurchaseResult result = results[i];
                if (result.State != IapPurchaseState.Purchased)
                {
                    continue;
                }

                IapProduct product;
                if (!TryFindInCatalog(catalog, result.ProductId, out product))
                {
                    continue;
                }

                // 消耗品不参与补单：商店侧根本不提供这个能力，客户端"恢复"消耗品等于凭空造道具。
                if (product.Kind == IapProductKind.Consumable)
                {
                    continue;
                }

                // 本地已经有这份权益：不重复发、不重复记收入，只计入"已确认"。
                if (_ledger.IsOwned(product.ProductId))
                {
                    restored++;
                    continue;
                }

                StorePurchaseOutcome outcome = Settle(in product, result.Receipt, 0, 0);
                if (outcome.Granted)
                {
                    restored++;
                }
            }

            return restored;
        }

        /// <summary>
        /// 购买主流程：商店 → 解析商品 → 结算。失败一律在发奖指令之前返回。
        /// </summary>
        /// <param name="productId">商品 ID。</param>
        /// <param name="overrideItemId">兜底道具 ID（0 = 未给）。</param>
        /// <param name="overrideCount">兜底数量（<c>&lt;= 0</c> = 未给）。</param>
        /// <returns>闭环结果。</returns>
        private StorePurchaseOutcome PurchaseCore(string productId, int overrideItemId, int overrideCount)
        {
            if (string.IsNullOrEmpty(productId))
            {
                // 连商店都不用问：空 ID 不可能是合法 SKU，让它进适配器只会白跑一次平台调用。
                return new StorePurchaseOutcome(false, 0, 0, ReasonInvalidProduct);
            }

            IapPurchaseResult result;
            try
            {
                result = _iap.Purchase(productId);
            }
            catch (Exception)
            {
                // 契约要求不抛；实现违约时按失败处理——绝不能让一个第三方 SDK 的异常变成"已付款"。
                return new StorePurchaseOutcome(false, 0, 0, ReasonStoreFailed);
            }

            if (result.State != IapPurchaseState.Purchased)
            {
                // AlreadyOwned / Cancelled / StoreUnavailable / InvalidReceipt / Pending 一律不发货，
                // 并且把原始 state 名原样带出去：UI 可以按它分流（已拥有 = 提示、取消 = 静默、待处理 = 稍后恢复）。
                return new StorePurchaseOutcome(false, 0, 0, result.State.ToString());
            }

            IapProduct product;
            if (!TryResolveProduct(productId, overrideItemId, overrideCount, out product))
            {
                // 商店说成功但目录里没有这件商品、也没给兜底：不知道怎么发，因此**不登记**。
                // 登记会把收据标成已消耗，钱花了却永远发不出货；不登记至少留给补单流程一次机会。
                return new StorePurchaseOutcome(false, 0, 0, ReasonUnknownProduct);
            }

            return Settle(in product, result.Receipt, overrideItemId, overrideCount);
        }

        /// <summary>
        /// 结算一次"已经被商店确认成功"的购买：台账登记 → 发货 → 埋点。
        /// <para>
        /// <b>顺序不可调换</b>：先登记后发货。反过来（先发货再登记）时，一旦登记被拒
        /// （重复收据/已拥有），道具已经进了背包，这就是赤裸裸的双发。
        /// </para>
        /// <para>
        /// <b>为什么发奖失败不回滚台账</b>：<see cref="IapPurchaseLedger"/> 没有撤销接口，而且"撤销"本身更危险——
        /// 收据重新变回可发放状态后，同一条失败路径再走一次就是双发。正确做法是把
        /// <see cref="ReasonGrantFailed"/> 交给上层做补偿（outbox 重放 / 服务端补单），因此这里如实标出。
        /// 另外"未注册处理器"属于装配期错误（总线是进程内同步调用，不会运行期抖动），
        /// 正常接线后这条路径不可达。
        /// </para>
        /// </summary>
        /// <param name="product">已确认购买的商品。</param>
        /// <param name="receipt">商店收据（发货凭据）。</param>
        /// <param name="overrideItemId">兜底道具 ID。</param>
        /// <param name="overrideCount">兜底数量。</param>
        /// <returns>闭环结果。</returns>
        private StorePurchaseOutcome Settle(in IapProduct product, string receipt, int overrideItemId, int overrideCount)
        {
            ResolveGrant(in product, overrideItemId, overrideCount, out int itemId, out int count);

            if (!_ledger.TryRecord(in product, receipt, out string ledgerReason))
            {
                // 重复收据 / 拥有型已发放 / 收据校验失败 / 空收据：全部不发物品。
                // reason 直接透传台账常量，排障时一眼能看出是哪条闸门拦下的。
                return new StorePurchaseOutcome(false, 0, 0, ledgerReason);
            }

            if (itemId > 0 && count > 0)
            {
                try
                {
                    _bus.Send(new GrantItemCommand(itemId, count, GrantReasonTagId));
                }
                catch (Exception)
                {
                    // 两种可能：没注册处理器（装配遗漏），或背包处理器自己抛了。对协调器而言都是
                    // "指令没有确认落地"。捕获而不是让异常穿透，是因为玩家此时已经付钱，
                    // 崩在商店页比返回一个明确的失败结果更糟。
                    return new StorePurchaseOutcome(false, 0, 0, ReasonGrantFailed);
                }
            }

            TrackPurchase(in product, itemId, count);

            return itemId > 0 && count > 0
                ? new StorePurchaseOutcome(true, itemId, count, ReasonGranted)
                : new StorePurchaseOutcome(true, 0, 0, ReasonGrantedNoItem);
        }

        /// <summary>
        /// 解析本次到账内容：目录优先，调用方的兜底参数只补空缺（见类型注释的取值顺序）。
        /// </summary>
        /// <param name="product">商品。</param>
        /// <param name="overrideItemId">兜底道具 ID。</param>
        /// <param name="overrideCount">兜底数量。</param>
        /// <param name="itemId">到账道具 ID（0 = 不发道具）。</param>
        /// <param name="count">到账数量（0 = 不发道具）。</param>
        private void ResolveGrant(in IapProduct product, int overrideItemId, int overrideCount, out int itemId, out int count)
        {
            if (product.GrantItemId > 0 && product.GrantCount > 0)
            {
                itemId = product.GrantItemId;
                count = product.GrantCount;
                return;
            }

            // overrideCount <= 0 视为"调用方没给兜底内容"：不猜数量，宁可不发。
            if (overrideCount > 0)
            {
                int fallbackItemId = overrideItemId > 0 ? overrideItemId : _defaultGrantItemId;
                if (fallbackItemId > 0)
                {
                    itemId = fallbackItemId;
                    count = overrideCount;
                    return;
                }
            }

            itemId = 0;
            count = 0;
        }

        /// <summary>
        /// 在目录里按 ID 找商品；找不到且调用方给了兜底数量时，按消耗品合成一件。
        /// <para>
        /// <b>只合成消耗品</b>：合成的商品没有类型信息，按"可反复购买"处理；若它其实是拥有型商品，
        /// 重复购买会被商店侧（<see cref="IapPurchaseState.AlreadyOwned"/>）拦下，不会变成双发。
        /// 反过来按"拥有型"合成则会因为台账只发一次而让玩家第二次购买拿不到货。
        /// </para>
        /// </summary>
        /// <param name="productId">商品 ID。</param>
        /// <param name="overrideItemId">兜底道具 ID。</param>
        /// <param name="overrideCount">兜底数量。</param>
        /// <param name="product">解析出的商品。</param>
        /// <returns>是否解析成功（<c>false</c> 表示无法确定到账内容）。</returns>
        private bool TryResolveProduct(string productId, int overrideItemId, int overrideCount, out IapProduct product)
        {
            IapProduct[] catalog = SnapshotCatalog();
            if (TryFindInCatalog(catalog, productId, out product))
            {
                return true;
            }

            if (overrideCount > 0)
            {
                product = new IapProduct(
                    productId, IapProductKind.Consumable, string.Empty, overrideItemId, overrideCount);
                return true;
            }

            product = default;
            return false;
        }

        /// <summary>在目录快照里线性查找（目录通常个位数条目，不建字典、不分配）。</summary>
        /// <param name="catalog">目录快照。</param>
        /// <param name="productId">商品 ID。</param>
        /// <param name="product">命中的商品。</param>
        /// <returns>是否命中。</returns>
        private static bool TryFindInCatalog(IapProduct[] catalog, string? productId, out IapProduct product)
        {
            if (!string.IsNullOrEmpty(productId))
            {
                for (int i = 0; i < catalog.Length; i++)
                {
                    if (string.Equals(catalog[i].ProductId, productId, StringComparison.Ordinal))
                    {
                        product = catalog[i];
                        return true;
                    }
                }
            }

            product = default;
            return false;
        }

        /// <summary>取一份目录副本；商店违约（抛异常/返回 null）时返回空数组。</summary>
        /// <returns>目录副本（永不为 <c>null</c>）。</returns>
        private IapProduct[] SnapshotCatalog()
        {
            IapProduct[]? source;
            try
            {
                source = _iap.GetCatalog();
            }
            catch (Exception)
            {
                return Array.Empty<IapProduct>();
            }

            if (source == null || source.Length == 0)
            {
                return Array.Empty<IapProduct>();
            }

            var copy = new IapProduct[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        /// <summary>
        /// 记一条 <see cref="AnalyticsEventIds.IapPurchased"/> 埋点。
        /// <para>
        /// <b>字段口径</b>：<c>Value0 = itemId</c>、<c>Value1 = count</c>、<c>Value2 = 价格档</c>、
        /// <c>Label = productId</c>。注意 <see cref="AnalyticsEventIds.IapPurchased"/> 的契约注释写的是
        /// "value0 = 商品 id 哈希代理、value1 = 价格档"，与 T5-3 锁定的口径不一致；本实现按 T5-3 执行
        /// （道具与数量是结算主键，看板要按 itemId 聚合到账量），注释与实现待后续统一。
        /// </para>
        /// <para>
        /// <b>价格档为什么不用 <c>string.GetHashCode()</c></b>：.NET Core 起字符串哈希按进程随机化，
        /// 同一份数据每次启动算出的档位都不同，看板上会出现"同样的价格分到几十个档"。
        /// 这里用 FNV-1a 32 位（偏移基 2166136261、质数 16777619）对 <see cref="IapProduct.PriceLabel"/>
        /// 的 UTF-16 字符逐个混淆，再对 1000 取模，得到 <c>0..999</c> 的稳定档位，跨进程/跨平台/跨引擎一致；
        /// 空价格标签记为 0（未知价格）。
        /// </para>
        /// <para>
        /// <b>埋点失败不得反向污染业务</b>：<see cref="IAnalyticsService.Track"/> 契约要求实现不抛异常，
        /// 但实现违约时这里必须吞掉——此时道具已经发完，把异常抛给 UI 会让"已到账"被当成"购买失败"。
        /// </para>
        /// </summary>
        /// <param name="product">商品（取 priceLabel 与 productId）。</param>
        /// <param name="itemId">本次到账道具 ID（0 = 无道具）。</param>
        /// <param name="count">本次到账数量（0 = 无道具）。</param>
        private void TrackPurchase(in IapProduct product, int itemId, int count)
        {
            IAnalyticsService? analytics = _analytics;
            if (analytics == null)
            {
                return;
            }

            try
            {
                analytics.Track(new AnalyticsEvent(
                    AnalyticsEventIds.IapPurchased,
                    itemId,
                    count,
                    PriceTierOf(product.PriceLabel),
                    product.ProductId));
            }
            catch (Exception)
            {
                // 埋点吞掉：见方法注释（已经发货的事实不能被一条埋点异常改写）。
            }
        }

        /// <summary>把价格标签折成稳定的价格档位（FNV-1a 32 位 % 1000；空标签 = 0）。</summary>
        /// <param name="priceLabel">展示用价格文本（只用于分组，不参与结算）。</param>
        /// <returns><c>0..999</c> 的稳定档位。</returns>
        private static int PriceTierOf(string priceLabel)
        {
            if (string.IsNullOrEmpty(priceLabel))
            {
                return 0;
            }

            uint hash = FnvOffsetBasis;
            for (int i = 0; i < priceLabel.Length; i++)
            {
                hash ^= priceLabel[i];
                hash *= FnvPrime;
            }

            return (int)(hash % PriceTierModulus);
        }
    }
}
