#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// IAP 商品类型。
    /// <para>
    /// <b>为什么类型进契约而不是交给适配层各自判断</b>：是否允许重复到账是**发货规则**，不是商店实现的自由度。
    /// 同一件商品在 App Store / Google Play / 未来的第三方支付里都必须按同一语义结算：
    /// <see cref="Consumable"/> 每买一次发一次；<see cref="NonConsumable"/> 与 <see cref="Subscription"/>
    /// 是"拥有型"，重复购买只会得到 <see cref="IapPurchaseState.AlreadyOwned"/>，绝不二次发货。
    /// </para>
    /// <para>
    /// 底层类型固定 <c>byte</c>：类型值会进日志/埋点/存档，数值顺序一旦变更会让历史数据串档。
    /// </para>
    /// </summary>
    public enum IapProductKind : byte
    {
        /// <summary>消耗品：金币、体力等，每次成功购买都必须发放，可反复购买。</summary>
        Consumable = 0,

        /// <summary>非消耗品：去广告、解锁包等，同一账号永久拥有，只发一次。</summary>
        NonConsumable = 1,

        /// <summary>
        /// 订阅：本契约只把它当作"拥有型"处理（未过期即已拥有、不重复发货）。
        /// 续费/到期/宽限期的真实判定属于商店实现，不在本端口表达——框架不伪造订阅状态。
        /// </summary>
        Subscription = 2
    }

    /// <summary>
    /// 一件可售商品：商店 SKU + 类型 + 展示价 + 到账道具，<b>只读</b>。
    /// <para>
    /// <b>为什么不把引擎/商店的商品对象塞进契约</b>：切片只需要知道"买什么、买到什么"，
    /// 不需要知道 <c>Product</c>、<c>SKUDetails</c>、货币代码、地区差价这些平台细节（AIF002 同款纪律）。
    /// 真实商品元数据由适配层持有，这里只保留跨端稳定的最小集合。
    /// </para>
    /// <para>
    /// <b><see cref="PriceLabel"/> 不参与结算</b>：它只是展示文本（"¥6.00" / "$0.99"），
    /// 真实扣款金额永远以商店返回为准；用它算钱是线上事故的常见起点。
    /// </para>
    /// </summary>
    public readonly struct IapProduct
    {
        /// <summary>构造一件商品。</summary>
        /// <param name="productId">商店商品标识（SKU）；<c>null</c> 归一化为空串，是否合法由实现方校验。</param>
        /// <param name="kind">商品类型（决定是否允许重复到账）。</param>
        /// <param name="priceLabel">展示用价格文本（<c>null</c> 归一化为空串；不参与结算）。</param>
        /// <param name="grantItemId">购买到账的道具 ID（背包 itemId；0 表示不发道具）。</param>
        /// <param name="grantCount">购买到账的数量（消耗品通常 &gt; 1，拥有型通常为 1）。</param>
        public IapProduct(string productId, IapProductKind kind, string priceLabel, int grantItemId, int grantCount)
        {
            // 跨层数据构造期归一化：读方永远不用再判 null。
            ProductId = productId ?? string.Empty;
            Kind = kind;
            PriceLabel = priceLabel ?? string.Empty;
            GrantItemId = grantItemId;
            GrantCount = grantCount;
        }

        /// <summary>商店商品标识（SKU；空串表示调用方给了非法/缺失的 ID）。</summary>
        public string ProductId { get; }

        /// <summary>商品类型（决定"可重复购买"还是"只发一次"）。</summary>
        public IapProductKind Kind { get; }

        /// <summary>展示用价格文本（可能为空；<b>不得</b>用于结算或数值计算）。</summary>
        public string PriceLabel { get; }

        /// <summary>购买到账的道具 ID（背包 itemId；0 = 不发道具）。</summary>
        public int GrantItemId { get; }

        /// <summary>购买到账的数量（0 = 不发道具）。</summary>
        public int GrantCount { get; }
    }

    /// <summary>
    /// 一次购买（或恢复）的结果状态。
    /// <para>
    /// <b>为什么把 <see cref="Cancelled"/> / <see cref="Pending"/> 也写进契约</b>：真实商店里"用户点了取消"
    /// 与"家长审批待处理"都不是异常，而是**必须被 UI 与发货逻辑区分开的正常结果**。
    /// 只留"成功/失败"两态，调用方就只能把取消当失败弹红字，把待处理当失败重复发货。
    /// </para>
    /// <para>数值顺序固定（会被写进日志/埋点），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum IapPurchaseState : byte
    {
        /// <summary>成功（含"恢复购买"找回的历史购买）：调用方可以按商品类型发货。</summary>
        Purchased = 0,

        /// <summary>非消耗品/订阅重复购买：用户已拥有，<b>不得再次发货</b>，UI 应提示"已拥有"。</summary>
        AlreadyOwned = 1,

        /// <summary>用户主动取消：不发货、不报错、可再次发起。</summary>
        Cancelled = 2,

        /// <summary>商店不可用 / 未接线 / 商品未知：可稍后重试，调用方不得视为成功。</summary>
        StoreUnavailable = 3,

        /// <summary>收据校验失败（伪造、过期、被拒）：不发货，属于需要留痕的安全事件。</summary>
        InvalidReceipt = 4,

        /// <summary>待处理（如家长审批/SDK 延迟确认）：结果未定，调用方应等待后续回调或稍后恢复，不得提前发货。</summary>
        Pending = 5
    }

    /// <summary>
    /// 一次购买/恢复的结果：状态 + 商品 + 收据 + 人类可读原因，<b>只读</b>。
    /// <para>
    /// <b><see cref="Receipt"/> 是发货凭据</b>：真实实现里它是商店返回的原始收据（base64/JWS），
    /// 调用方应把它交给 <see cref="IReceiptValidator"/> 或服务端做验签后再发货；
    /// 失败/已拥有/待处理时可能为空串。
    /// </para>
    /// <para>
    /// <b>业务判定只看 <see cref="State"/></b>：<see cref="Reason"/> 是诊断文本，
    /// 允许各实现给不同的可读描述，禁止拿字符串比较做分支。
    /// </para>
    /// </summary>
    public readonly struct IapPurchaseResult
    {
        /// <summary>构造一次购买结果。</summary>
        /// <param name="state">结果状态。</param>
        /// <param name="productId">商品 ID（<c>null</c> 归一化为空串）。</param>
        /// <param name="receipt">收据（<c>null</c> 归一化为空串；失败时可能为空）。</param>
        /// <param name="reason">诊断原因（<c>null</c> 归一化为空串；禁止作为业务判定依据）。</param>
        public IapPurchaseResult(IapPurchaseState state, string productId, string receipt, string reason)
        {
            State = state;
            ProductId = productId ?? string.Empty;
            Receipt = receipt ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        /// <summary>结果状态。</summary>
        public IapPurchaseState State { get; }

        /// <summary>商品 ID（空串表示这次调用没有明确的商品，例如商店整体不可用）。</summary>
        public string ProductId { get; }

        /// <summary>商店收据（发货凭据；空串表示本次没有可校验的收据）。</summary>
        public string Receipt { get; }

        /// <summary>诊断原因（可能为空串）。</summary>
        public string Reason { get; }

        /// <summary>
        /// 是否"本次可以发货"：<b>只有</b> <see cref="IapPurchaseState.Purchased"/> 为 <c>true</c>。
        /// <see cref="IapPurchaseState.AlreadyOwned"/> 虽然对用户是好消息，但不代表可以再次发放道具。
        /// </summary>
        public bool IsSuccess
        {
            get { return State == IapPurchaseState.Purchased; }
        }
    }

    /// <summary>
    /// IAP 端口：真实商店由适配层实现；框架只定义"目录 / 购买 / 恢复 / 校验"。
    /// <para>
    /// <b>为什么是同步签名</b>：契约必须能被"纯内存假商店"直接实现，这样购买/去重/发货逻辑
    /// 能在无头测试与断网真机上完整验收；真实商店的异步 SDK（Google Play Billing / StoreKit）
    /// 由适配层用"请求队列 + 回调/后台等待"包一层再喂进本端口。把 <c>Task</c> 写进契约会让
    /// 纯逻辑层被迫依赖异步调度（与 <see cref="ICloudSaveService"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>禁止抛异常</b>：任何失败都必须落成 <see cref="IapPurchaseState"/>；
    /// 购买流程不允许因为一次商店抖动把正在玩的游戏打断。
    /// </para>
    /// <para>线程模型：由实现方声明；框架侧按单线程串行调用（购买发起与发货都在主线程）。</para>
    /// </summary>
    public interface IIapService
    {
        /// <summary>
        /// 商店当前是否可用（未登录/未初始化/尚未接线都必须如实返回 <c>false</c>，不得"假装成功"）。
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 读取商品目录（返回副本，调用方改写它不得影响实现内部状态）。
        /// <para>目录为空数组表示"暂不可售/未拉到配置"，调用方应禁用商店 UI，而不是显示空列表。</para>
        /// </summary>
        /// <returns>商品数组（可能为空；顺序由实现保证稳定）。</returns>
        IapProduct[] GetCatalog();

        /// <summary>
        /// 购买指定商品并返回结果。
        /// <para>实现方负责：未知商品 → <see cref="IapPurchaseState.StoreUnavailable"/>；
        /// 已拥有的拥有型商品 → <see cref="IapPurchaseState.AlreadyOwned"/>；
        /// 收据校验失败 → <see cref="IapPurchaseState.InvalidReceipt"/>；成功 → 带收据的 <see cref="IapPurchaseState.Purchased"/>。</para>
        /// <para>本方法只'发起并给出结果'，**不负责发货**：调用方拿到 <see cref="IapPurchaseResult.Receipt"/>
        /// 并通过 <see cref="IReceiptValidator"/>（或服务端）确认后，才调用背包/存档发放道具。</para>
        /// </summary>
        /// <param name="productId">商品 ID（未知/空串按商店不可用处理）。</param>
        /// <returns>购买结果（永不抛异常）。</returns>
        IapPurchaseResult Purchase(string productId);

        /// <summary>
        /// 补单/恢复：返回所有已购非消耗品（与订阅）的收据。
        /// <para>用途：重装、换机、掉单后把"用户确实买过、但本地没有记录"的拥有型商品找回来。
        /// 消耗品不参与恢复（商店本身不提供消耗品补单，硬造就是刷道具漏洞）。</para>
        /// <para>没有可恢复项返回空数组；商店不可用返回单条
        /// <see cref="IapPurchaseState.StoreUnavailable"/>（让调用方与"确实没有已购项"区分开）。</para>
        /// </summary>
        /// <returns>每条已购拥有型商品一条 <see cref="IapPurchaseState.Purchased"/> 结果（永不抛异常）。</returns>
        IapPurchaseResult[] RestorePurchases();
    }

    /// <summary>
    /// 收据校验端口：本地 mock（无头/离线）或真实服务端验签。
    /// <para>
    /// <b>同步 + 纯判定</b>：实现只回答"这张收据是否可信"，不得顺便发货、改存档或阻塞等待网络。
    /// 真机上的服务端验签应由网络层提前完成（购买成功后先把收据上传到游戏服务器，服务器回调结果），
    /// 本端口在这里只做最终判定；这样同一个校验端口在无头测试里可以被替换成纯内存 mock。
    /// </para>
    /// <para><b>校验失败返回 <c>false</c>，不要抛异常</b>；实现方抛出的异常由调用方按"校验失败"处理。</para>
    /// </summary>
    public interface IReceiptValidator
    {
        /// <summary>校验一张收据是否可信（同步、无副作用）。</summary>
        /// <param name="productId">商品 ID。</param>
        /// <param name="receipt">购买返回的原始收据。</param>
        /// <returns>是否通过校验。</returns>
        bool Validate(string productId, string receipt);
    }
}
