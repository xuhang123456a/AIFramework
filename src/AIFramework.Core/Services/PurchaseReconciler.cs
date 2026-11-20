#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 一条待对账的收据记录（来自商店本地补单清单 / 服务端对账接口），<b>只读</b>。
    /// <para>
    /// <b>为什么要这个结构体而不是直接复用 <see cref="IapPurchaseResult"/></b>：对账的输入不是"一次购买动作"，
    /// 而是"一笔已经发生、可能已经发过货、也可能已经退款的交易"。它必须自带到账内容
    /// （<see cref="ItemId"/> / <see cref="Count"/>）与业务状态（<see cref="State"/>），
    /// 否则对账端只能回头查目录——而目录是**可变的运营配置**，用它反推历史到账内容，
    /// 一次配置改动就会把"60 金币"算成"300 金币"。
    /// </para>
    /// <para>
    /// <b><see cref="PurchasedAtUnixSeconds"/> 不参与任何判定</b>：它只随动作一起留在记录里供排障/看板使用。
    /// 时间戳来自客户端或商店，不能作为"谁先谁后"的裁决依据（设备时间会被改），
    /// 因此对账顺序永远以调用方传入的批次顺序为准，绝不按时间戳排序。
    /// </para>
    /// <para>
    /// <b>字符串字段全部在构造期归一化</b>（<c>null</c> → 空串）：读方不用再判 <c>null</c>，
    /// 空 <see cref="ReceiptId"/> 表示这条记录没有可用主键，对账会直接忽略它。
    /// </para>
    /// </summary>
    public readonly struct PurchaseReceipt
    {
        /// <summary>构造一条待对账的收据记录。</summary>
        /// <param name="receiptId">商店交易号/收据 ID（<c>null</c> 归一化为空串；空串条目会被对账忽略）。</param>
        /// <param name="productId">商品 ID（<c>null</c> 归一化为空串；空串时台账可拒收，补发仍会发生但不计成功）。</param>
        /// <param name="itemId">该收据应当到账的道具 ID（&lt;= 0 表示本收据不发道具）。</param>
        /// <param name="count">该收据应当到账的数量（&lt;= 0 表示本收据不发道具）。</param>
        /// <param name="purchasedAtUnixSeconds">购买时间（Unix 秒；仅供诊断，不参与判定）。</param>
        /// <param name="state">商店/服务端给出的交易状态（<c>"granted"</c> / <c>"refunded"</c> / 其它；<c>null</c> 归一化为空串）。</param>
        public PurchaseReceipt(
            string receiptId,
            string productId,
            int itemId,
            int count,
            long purchasedAtUnixSeconds,
            string state)
        {
            ReceiptId = receiptId ?? string.Empty;
            ProductId = productId ?? string.Empty;
            ItemId = itemId;
            Count = count;
            PurchasedAtUnixSeconds = purchasedAtUnixSeconds;
            State = state ?? string.Empty;
        }

        /// <summary>商店交易号/收据 ID（对账主键；空串表示该条目不可对账）。</summary>
        public string ReceiptId { get; }

        /// <summary>商品 ID（台账登记与排障用；空串会被台账拒收）。</summary>
        public string ProductId { get; }

        /// <summary>该收据应当到账的道具 ID（&lt;= 0 = 本收据不发道具，例如订阅/功能解锁）。</summary>
        public int ItemId { get; }

        /// <summary>该收据应当到账的数量（&lt;= 0 = 本收据不发道具）。</summary>
        public int Count { get; }

        /// <summary>购买时间（Unix 秒；仅供诊断，<b>禁止</b>用于排序或先后裁决）。</summary>
        public long PurchasedAtUnixSeconds { get; }

        /// <summary>交易状态文本（<c>"granted"</c> / <c>"refunded"</c> / 其它，比较时忽略首尾空白与大小写）。</summary>
        public string State { get; }
    }

    /// <summary>
    /// 对账判定结果。
    /// <para>
    /// <b>数值顺序固定</b>（会进日志/看板/存档）：只允许追加，不允许调整既有值。
    /// </para>
    /// </summary>
    public enum ReconcileState : byte
    {
        /// <summary>无法判定：<see cref="PurchaseReceipt.State"/> 既不是 <c>granted</c> 也不是 <c>refunded</c>（待人工/服务端补充信息）。</summary>
        Unknown = 0,

        /// <summary>已发货：收据说 <c>granted</c>，本地台账也有这张收据的发放记录（一致，无需处理）。</summary>
        Granted = 1,

        /// <summary>掉单：收据说 <c>granted</c>，但本地台账没有这张收据的发放记录（需要补发）。</summary>
        Missing = 2,

        /// <summary>重复：收据说 <c>granted</c>，且这张收据已经由本类补发过、商店又重复报告了一次（<b>不得</b>再次发货）。</summary>
        Duplicated = 3,

        /// <summary>已退款：收据说 <c>refunded</c>，<b>不发货</b>，只上报动作；回收/扣减由装配层裁决。</summary>
        Refunded = 4,

        /// <summary>孤儿：本地有该收据的发放记录，但本批商店收据里没有它（可能是刷单/台账异常；本类<b>不</b>自动撤销）。</summary>
        Orphan = 5
    }

    /// <summary>
    /// 一条对账动作：让装配层知道"哪张收据、该怎么处理"，<b>只读</b>。
    /// <para>
    /// <b>业务判定只看 <see cref="State"/></b>：<see cref="Reason"/> 是稳定常量（见
    /// <see cref="PurchaseReconciler"/> 上的 <c>Reason*</c>），给日志与看板聚合用，禁止拿字符串比较做分支。
    /// </para>
    /// <para>
    /// <b><see cref="ItemId"/> / <see cref="Count"/> 是收据声明的到账内容，不是"已经发出去的"</b>：
    /// 对 <see cref="ReconcileState.Missing"/> 它是待补发的内容，对 <see cref="ReconcileState.Refunded"/>
    /// 它是应当回收的内容，对 <see cref="ReconcileState.Orphan"/> 它是本地已发放的内容。
    /// </para>
    /// </summary>
    public readonly struct ReconcileAction
    {
        /// <summary>构造一条对账动作。</summary>
        /// <param name="receiptId">商店交易号/收据 ID（<c>null</c> 归一化为空串）。</param>
        /// <param name="state">判定结果。</param>
        /// <param name="itemId">收据声明的道具 ID（&lt;= 0 = 无道具）。</param>
        /// <param name="count">收据声明的数量（&lt;= 0 = 无道具）。</param>
        /// <param name="reason">稳定诊断原因（<c>null</c> 归一化为空串；禁止作为业务判定依据）。</param>
        public ReconcileAction(string receiptId, ReconcileState state, int itemId, int count, string reason)
        {
            ReceiptId = receiptId ?? string.Empty;
            State = state;
            ItemId = itemId;
            Count = count;
            Reason = reason ?? string.Empty;
        }

        /// <summary>商店交易号/收据 ID。</summary>
        public string ReceiptId { get; }

        /// <summary>判定结果（业务分支只看这个）。</summary>
        public ReconcileState State { get; }

        /// <summary>收据声明的道具 ID（&lt;= 0 = 无道具）。</summary>
        public int ItemId { get; }

        /// <summary>收据声明的数量（&lt;= 0 = 无道具）。</summary>
        public int Count { get; }

        /// <summary>稳定诊断原因（取值见 <see cref="PurchaseReconciler"/> 的 <c>Reason*</c> 常量）。</summary>
        public string Reason { get; }
    }

    /// <summary>
    /// 对账所需的"本地发放台账"最小端口。
    /// <para>
    /// <b>为什么还要一层端口，而不是直接依赖 <see cref="IapPurchaseLedger"/></b>：那个类型是 <c>sealed</c>
    /// 且没有实现任何接口，直接依赖它就等于把对账逻辑钉死在"内存台账"上——而对账恰恰是**最需要落盘**的一环
    /// （进程重启后，"这张收据发过没有"必须还能回答）。收口成端口后，装配层可以把已发放集合写进存档，
    /// 或在服务端权威模式下换成"服务端台账"实现，对账逻辑本身零改动。
    /// </para>
    /// <para>
    /// <b>实现契约</b>：全同步、<b>不抛异常</b>、按收据 ID 做 Ordinal（区分大小写）比较；
    /// <see cref="TryRecord"/> 返回 <c>false</c> 时必须保证台账状态完全不变（调用方据此判定"没写进去"）。
    /// </para>
    /// <para>
    /// 现成的接入方式：<see cref="IapPurchaseLedgerAdapter"/>（包装 <see cref="IapPurchaseLedger"/>）。
    /// </para>
    /// </summary>
    public interface IIapPurchaseLedger
    {
        /// <summary>该收据是否已有发放记录（空串恒为 <c>false</c>）。</summary>
        /// <param name="receiptId">商店交易号/收据 ID。</param>
        /// <returns>是否已登记过。</returns>
        bool IsReceiptConsumed(string receiptId);

        /// <summary>
        /// 登记一条发放记录（去重）。
        /// </summary>
        /// <param name="productId">商品 ID（实现方可拒收空串）。</param>
        /// <param name="receiptId">商店交易号/收据 ID（实现方可拒收空串/超长值）。</param>
        /// <param name="itemId">到账道具 ID。</param>
        /// <param name="count">到账数量。</param>
        /// <param name="reason">成功或拒绝的稳定原因（必须非 <c>null</c>；只做诊断）。</param>
        /// <returns>是否登记成功（<c>false</c> 表示台账未变）。</returns>
        bool TryRecord(string productId, string receiptId, int itemId, int count, out string reason);
    }

    /// <summary>
    /// 把已有的 <see cref="IapPurchaseLedger"/> 接到 <see cref="IIapPurchaseLedger"/> 上的薄适配器。
    /// <para>
    /// <b>为什么商品类型固定 <see cref="IapProductKind.Consumable"/></b>：对账是按"每张收据一次结算"处理的，
    /// 收据本身就是幂等键；若冒充拥有型，台账会按商品 ID 只放行一次，同一商品的多笔真实交易会被自己拦掉。
    /// 拥有型商品的"只发一次"由购买链路（<see cref="StorePurchaseCoordinator"/>）负责，不在对账路径上重复实现。
    /// </para>
    /// <para>
    /// <b>被包装台账的既有闸门照样生效</b>：收据为空/超过 <see cref="IapPurchaseLedger.MaxReceiptLength"/>、
    /// 校验器判负、商品 ID 为空都会让 <see cref="TryRecord"/> 返回 <c>false</c>。
    /// 对账接入时应给台账一个"服务端已验签"语义的 <c>IReceiptValidator</c>，
    /// 否则真实平台的长收据会被 256 字符上限挡在门外（见交付报告"真机未验证点"）。
    /// </para>
    /// </summary>
    public sealed class IapPurchaseLedgerAdapter : IIapPurchaseLedger
    {
        /// <summary>被包装的台账（非 <c>null</c>）。</summary>
        private readonly IapPurchaseLedger _ledger;

        /// <summary>创建适配器。</summary>
        /// <param name="ledger">已有台账（非 <c>null</c>）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="ledger"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public IapPurchaseLedgerAdapter(IapPurchaseLedger ledger)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        /// <inheritdoc />
        public bool IsReceiptConsumed(string receiptId)
        {
            return _ledger.IsReceiptConsumed(receiptId);
        }

        /// <inheritdoc />
        public bool TryRecord(string productId, string receiptId, int itemId, int count, out string reason)
        {
            var product = new IapProduct(productId, IapProductKind.Consumable, string.Empty, itemId, count);
            return _ledger.TryRecord(in product, receiptId, out reason);
        }
    }

    /// <summary>
    /// 商店掉单对账器：收据批次 → 逐条判定（掉单/已发/重复/退款/孤儿）→ 只对掉单补发，
    /// 全程不碰引擎、不读时钟、不落盘。
    /// <para>
    /// <b>为什么在 Core</b>：它同时依赖"IAP 收据语义"（Contracts）与"本地台账"（Core），
    /// 而 Features 只允许引用 Contracts（AIF004）；放进 Contracts 又会把结算流程塞进契约层。
    /// Core 是唯一既能被 Console/Unity 两个装配入口共用、又能被无头单测逐条钉死的位置。
    /// </para>
    /// <para>
    /// <b>判定顺序（固定，不允许调换）</b>：
    /// <list type="number">
    /// <item>先把 <see cref="PurchaseReceipt.State"/> 去掉首尾空白，按 <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// 与常量 <c>granted</c> / <c>refunded</c> 比较；两者都不是 → <see cref="ReconcileState.Unknown"/>（不改任何状态）。</item>
    /// <item><c>refunded</c> → <see cref="ReconcileState.Refunded"/>，<b>不发货</b>，只计数 + 出动作。
    /// 退款优先于台账：钱已经退回来了，哪怕本地发过货也不能再补。</item>
    /// <item><c>granted</c> → 先查本类自己的补发记录：补发过 → <see cref="ReconcileState.Duplicated"/>；
    /// 再查台账：有记录 → <see cref="ReconcileState.Granted"/>；都没有 → <see cref="ReconcileState.Missing"/>。</item>
    /// </list>
    /// 顺序固定的意义：同一份输入在任何时候、任何适配器下都得到同一个结论，排障时不必猜"是哪条规则先命中"。
    /// </para>
    /// <para>
    /// <b>幂等</b>：同一 receiptId 的同一结论只会产出一次动作；重复喂同一批（或批内重复条目）返回空数组。
    /// 但<b>状态变化不算重复</b>——已发 → 退款、掉单 → 补发后商店再报 granted，都会如实产出一条新动作
    /// （否则退款事件会被幂等吞掉，这是最危险的静默失败）。
    /// </para>
    /// <para>
    /// <b>补发只走 <see cref="TryGrantMissing"/></b>：<see cref="Reconcile"/> 本身<b>永远不发指令</b>，
    /// 只出判定。补发时先 <c>bus.Send(GrantItemCommand)</c> 再写台账（顺序按任务锁定）；
    /// 台账登记失败返回 <c>false</c>，但**不会**再次发货（"已发出"这一事实记在本类里，
    /// 不依赖台账是否写成功），防的是"写台账失败 → 重试 → 双发"。代价是这一笔的台账缺口无法自愈，
    /// 需要上层按 <see cref="GrantedCount"/> 与台账实际内容对账补偿。
    /// </para>
    /// <para>
    /// <b>Orphan（孤儿）的口径必须说清</b>：<see cref="IIapPurchaseLedger"/> 没有枚举 API，本类也无法读取
    /// <see cref="IapPurchaseLedger"/> 的内部集合，因此"本地有发放记录但商店没有收据"只能对
    /// **本类亲眼见过的本地发放记录**成立：台账里已发（判为 <see cref="ReconcileState.Granted"/>）的收据，
    /// 以及本类补发过的收据。它们在后一批收据里消失 → 追加一条 <see cref="ReconcileState.Orphan"/> 动作。
    /// 由此产生两条使用约束：① 调用方必须**一次喂完整批次**（分页喂会误报孤儿）；
    /// ② 从进程启动就存在、但从未出现在任何批次里的台账记录，本类看不到，也就报不出来。
    /// </para>
    /// <para>
    /// <b>孤儿与退款都只上报、不执行</b>：撤销玩家已到手的资源是需要服务端/人工裁决的资损动作
    /// （客户端一算错就等于白送或白扣），所以本类只给状态与内容，回收动作由装配层决定。
    /// </para>
    /// <para>
    /// <b>异常策略</b>：只有构造参数与 <see cref="Reconcile"/> 入参为 <c>null</c> 时抛
    /// <see cref="ArgumentNullException"/>（编程错误，装配期暴露）。其余失败全部用
    /// <c>false</c> / <see cref="ReconcileState"/> 表达；<c>bus.Send</c> 抛异常按"指令没有送达"处理并吞掉，
    /// 因为对账是后台任务，一次装配遗漏不该把游戏打断。
    /// </para>
    /// <para>
    /// <b>已知缺口（本类不掩盖）</b>：补发幂等在**进程内**成立——"这张收据已经发过"记在实例字段里；
    /// 跨启动的幂等完全取决于 <see cref="IIapPurchaseLedger"/> 是否落盘。
    /// <see cref="IapPurchaseLedgerAdapter"/> 包装的是内存台账，重启后同一张收据会被重新判为
    /// <see cref="ReconcileState.Missing"/> 并再补一次，因此真机必须接持久化台账实现。
    /// </para>
    /// <para>
    /// <b>线程模型：全同步、<b>非线程安全</b></b>。所有状态都在实例字段里，必须由同一线程串行调用
    /// （对账本身是低频后台路径，不需要锁；加锁只会在购买主线程上制造无谓争用）。
    /// </para>
    /// </summary>
    public sealed class PurchaseReconciler
    {
        /// <summary>
        /// 补发指令使用的因果标签：沿用 <see cref="MetaReasonTags.ShopGrant"/>（6102，"商店：发货"）。
        /// <para>
        /// <b>为什么不新增常量</b>：<see cref="MetaReasonTags"/> 属于 Contracts，本任务禁止改动；
        /// 6102 的语义（商店发货）与掉单补发完全一致，选它是"最接近且不误导"的一个。
        /// 将来若追加 IAP 补单专用标签，只需改这一处。
        /// </para>
        /// </summary>
        public const int GrantReasonTagId = MetaReasonTags.ShopGrant;

        /// <summary>动作原因：收据已发货且与台账一致。</summary>
        public const string ReasonGranted = "granted";

        /// <summary>动作原因：收据声称已发货，但台账没有发放记录（掉单）。</summary>
        public const string ReasonMissing = "missing grant";

        /// <summary>动作原因：本类已补发过的收据被商店重复报告。</summary>
        public const string ReasonDuplicated = "duplicate receipt";

        /// <summary>动作原因：收据已退款（只上报，不回收）。</summary>
        public const string ReasonRefunded = "refunded";

        /// <summary>动作原因：本地有发放记录，但本批收据里没有它（孤儿）。</summary>
        public const string ReasonOrphan = "orphan grant";

        /// <summary>动作原因：状态文本既不是 granted 也不是 refunded。</summary>
        public const string ReasonUnknownState = "unknown state";

        /// <summary>状态文本常量：已发货。</summary>
        private const string StateGranted = "granted";

        /// <summary>状态文本常量：已退款。</summary>
        private const string StateRefunded = "refunded";

        /// <summary>本地发放台账端口；构造后不可变。</summary>
        private readonly IIapPurchaseLedger _ledger;

        /// <summary>CQE 总线（补发时发 <see cref="GrantItemCommand"/>）。</summary>
        private readonly ICqeBus _bus;

        /// <summary>每条收据最近一次产出的结论（幂等闸门）。</summary>
        private readonly Dictionary<string, ReconcileState> _decided =
            new Dictionary<string, ReconcileState>(StringComparer.Ordinal);

        /// <summary>已判定为掉单、尚未尝试补发的收据（<see cref="MissingCount"/> 的口径来源）。</summary>
        private readonly Dictionary<string, PurchaseReceipt> _pending =
            new Dictionary<string, PurchaseReceipt>(StringComparer.Ordinal);

        /// <summary>本类已知的本地发放记录（孤儿检测的依据；台账没有枚举 API）。</summary>
        private readonly Dictionary<string, LocalGrant> _grants =
            new Dictionary<string, LocalGrant>(StringComparer.Ordinal);

        /// <summary>发放记录的插入顺序（保证孤儿动作输出稳定，不依赖字典遍历顺序）。</summary>
        private readonly List<string> _grantOrder = new List<string>();

        /// <summary>已判定为退款的收据（<see cref="RefundedCount"/> 的去重集合）。</summary>
        private readonly HashSet<string> _refunded = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>已经上报过孤儿的收据（同一条孤儿只刷一次，避免每批重复报警）。</summary>
        private readonly HashSet<string> _orphanReported = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>累计补发成功（台账也写成功）的收据数。</summary>
        private int _grantedCount;

        /// <summary>创建对账器。</summary>
        /// <param name="ledger">本地发放台账端口（非 <c>null</c>；可用 <see cref="IapPurchaseLedgerAdapter"/> 包装既有台账）。</param>
        /// <param name="bus">CQE 总线（非 <c>null</c>；补发时需要已注册 <see cref="GrantItemCommand"/> 处理器）。</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="ledger"/> / <paramref name="bus"/> 为 <c>null</c>（装配期编程错误）。
        /// </exception>
        public PurchaseReconciler(IIapPurchaseLedger ledger, ICqeBus bus)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        /// <summary>
        /// 当前仍待补发的掉单数（已判定 <see cref="ReconcileState.Missing"/>、尚未调用
        /// <see cref="TryGrantMissing"/> 的收据数量）。
        /// <para>补发尝试（无论台账是否写成功）都会让它减 1：收据已经发出去过了，不再算"待补"。</para>
        /// </summary>
        public int MissingCount
        {
            get { return _pending.Count; }
        }

        /// <summary>
        /// 累计补发成功数（<see cref="TryGrantMissing"/> 返回 <c>true</c> 的次数，同一收据最多计 1 次）。
        /// <para>
        /// <b>口径 = 台账也写成功了</b>：指令发出但台账登记失败的那一笔不计入（它需要人工补偿），
        /// 因此 <see cref="MissingCount"/> 与 <see cref="GrantedCount"/> 之和不等于"累计发现的掉单数"。
        /// </para>
        /// </summary>
        public int GrantedCount
        {
            get { return _grantedCount; }
        }

        /// <summary>累计判定为退款的收据数（按收据去重；同一张收据不会因为反复对账而重复计数）。</summary>
        public int RefundedCount
        {
            get { return _refunded.Count; }
        }

        /// <summary>
        /// 对账一批收据：逐条判定并返回**本批新增的**动作（幂等；返回数组按输入顺序，
        /// 孤儿动作追加在末尾且按发放记录插入顺序排列）。
        /// <para>
        /// <b>本方法不发任何指令</b>：判定与执行分离，补发只发生在 <see cref="TryGrantMissing"/>。
        /// 这样"先看清全局再动手"成为默认路径，也保证对账本身可重复执行。
        /// </para>
        /// <para>
        /// <b>空批次返回空数组</b>（即便存在已跟踪的发放记录也不做孤儿扫描）：
        /// 空批次在真实链路里通常意味着"商店没拉到/接口失败"，把它当成"商店一张收据都没有"会批量误报孤儿。
        /// </para>
        /// <para>
        /// <b>非法条目（<see cref="PurchaseReceipt.ReceiptId"/> 为空）直接忽略</b>：没有主键就无法查台账、
        /// 无法去重、也无法补发，放进去只会污染统计与幂等表。
        /// </para>
        /// </summary>
        /// <param name="receipts">商店/服务端给出的收据批次（非 <c>null</c>；<b>必须是完整批次</b>）。</param>
        /// <returns>本批新增的动作（可能为空数组；顺序确定）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="receipts"/> 为 <c>null</c>（编程错误）。</exception>
        public ReconcileAction[] Reconcile(PurchaseReceipt[] receipts)
        {
            if (receipts == null)
            {
                throw new ArgumentNullException(nameof(receipts));
            }

            if (receipts.Length == 0)
            {
                return Array.Empty<ReconcileAction>();
            }

            var actions = new List<ReconcileAction>(receipts.Length);
            var batch = new HashSet<string>(StringComparer.Ordinal);
            bool processedAny = false;

            for (int i = 0; i < receipts.Length; i++)
            {
                PurchaseReceipt receipt = receipts[i];
                string receiptId = receipt.ReceiptId;
                if (string.IsNullOrEmpty(receiptId))
                {
                    // 没有主键的条目不可对账：直接丢弃（不计数、不出动作、不进幂等表）。
                    continue;
                }

                processedAny = true;
                batch.Add(receiptId);

                ReconcileState state = Classify(in receipt);
                switch (state)
                {
                    case ReconcileState.Missing:
                        // 记录待补发内容：补发时用的是**这张收据声明的**到账内容，不是当前目录配置。
                        _pending[receiptId] = receipt;
                        break;

                    case ReconcileState.Refunded:
                        // 退款：从待补发里摘掉，并停掉孤儿跟踪（"退款后商店不再列它"是预期行为，不该刷孤儿）。
                        _pending.Remove(receiptId);
                        ForgetGrant(receiptId);
                        _refunded.Add(receiptId);
                        break;

                    case ReconcileState.Granted:
                    case ReconcileState.Duplicated:
                        _pending.Remove(receiptId);
                        LockGrant(receiptId, receipt.ItemId, receipt.Count);
                        break;

                    default:
                        // Unknown：不改任何状态，只产出一次诊断动作，等下次拿到可识别状态再裁决。
                        break;
                }

                if (_decided.TryGetValue(receiptId, out ReconcileState previous) && previous == state)
                {
                    // 同一收据的同一结论只产出一次动作：重复喂批次不会重复产生动作（幂等）。
                    continue;
                }

                _decided[receiptId] = state;
                actions.Add(new ReconcileAction(receiptId, state, receipt.ItemId, receipt.Count, ReasonOf(state)));
            }

            if (processedAny)
            {
                AppendOrphanActions(batch, actions);
            }

            return actions.ToArray();
        }

        /// <summary>
        /// 补发一张掉单收据：<b>只</b>对已判定为 <see cref="ReconcileState.Missing"/> 的收据生效。
        /// <para>
        /// <b>顺序固定为"先发货、后写台账"</b>（按任务锁定）。因此台账登记失败时：
        /// 指令已经发出去了，本类只返回 <c>false</c> 并**拒绝任何后续重发**——"已发出"这一事实记在本类内部，
        /// 不依赖台账写成功。若反过来依赖台账，一次写失败就会让下一轮对账再发一份，变成资损。
        /// </para>
        /// <para>
        /// <b>返回值口径</b>：<c>true</c> = 本次完整闭环（需要发货的已发、台账已登记）；
        /// <c>false</c> = 没有可补发的收据（未知/已补发/已退款）、指令没送达（未注册处理器或处理器抛异常，
        /// 此时状态完全不变，可安全重试）、或台账登记失败（已发货但缺台账，需上层补偿）。
        /// </para>
        /// <para>
        /// <b>无道具收据（<see cref="PurchaseReceipt.ItemId"/> 或 <see cref="PurchaseReceipt.Count"/> &lt;= 0）</b>
        /// 不发空指令，但仍登记台账并标记为已处理，返回台账登记结果——订阅/功能解锁这类收据本来就无道具可发。
        /// </para>
        /// <para>
        /// <b>防双发的边界</b>：本方法只保证"同一张收据不会由本类发出第二条指令"。
        /// 若背包处理器在<b>已改状态后</b>抛异常，这里的重试仍会再发一次——那属于处理器自身要保证的幂等
        /// （正常接线后未注册处理器是装配期错误，不会运行期抖动）。
        /// </para>
        /// </summary>
        /// <param name="receiptId">商店交易号/收据 ID（<c>null</c>/空串返回 <c>false</c>）。</param>
        /// <returns>是否完成补发闭环（见注释中的口径）。</returns>
        public bool TryGrantMissing(string receiptId)
        {
            if (string.IsNullOrEmpty(receiptId))
            {
                return false;
            }

            if (!_pending.TryGetValue(receiptId, out PurchaseReceipt receipt))
            {
                // 未知收据 / 已补发过 / 已退款 / 从未被判为掉单 —— 一律不发。
                return false;
            }

            bool hasItem = receipt.ItemId > 0 && receipt.Count > 0;
            if (hasItem)
            {
                try
                {
                    _bus.Send(new GrantItemCommand(receipt.ItemId, receipt.Count, GrantReasonTagId));
                }
                catch (Exception)
                {
                    // 指令没送达：pending 保留、台账不动，允许下一次对账重试。
                    return false;
                }
            }

            // 到这一步"已经发过（或本来就不需要发）"：先落本类状态，再写台账。
            // 台账写失败也不回退，防的是"回退 → 重试 → 双发"。
            _pending.Remove(receiptId);
            MarkGrantAttempted(receiptId, receipt.ItemId, receipt.Count);
            _decided[receiptId] = ReconcileState.Granted;

            bool recorded = _ledger.TryRecord(
                receipt.ProductId, receiptId, receipt.ItemId, receipt.Count, out _);
            if (recorded)
            {
                _grantedCount++;
            }

            return recorded;
        }

        /// <summary>
        /// 判定一条收据（顺序见类型注释：退款 → 补发记录 → 台账 → 掉单）。
        /// </summary>
        /// <param name="receipt">待判定收据。</param>
        /// <returns>判定结果。</returns>
        private ReconcileState Classify(in PurchaseReceipt receipt)
        {
            // 归一化放在比较处而不是构造处：结构体可能被 default 出来（State 为 null）。
            string stateText = (receipt.State ?? string.Empty).Trim();

            if (string.Equals(stateText, StateRefunded, StringComparison.OrdinalIgnoreCase))
            {
                // 退款优先于一切：钱退回去了，哪怕本地发过货也不能再补。
                return ReconcileState.Refunded;
            }

            if (string.Equals(stateText, StateGranted, StringComparison.OrdinalIgnoreCase))
            {
                if (_grants.TryGetValue(receipt.ReceiptId, out LocalGrant grant))
                {
                    // 本类亲手补发过的收据又出现：商店重复报告，绝不再发（Duplicated）。
                    return grant.Attempted ? ReconcileState.Duplicated : ReconcileState.Granted;
                }

                return _ledger.IsReceiptConsumed(receipt.ReceiptId)
                    ? ReconcileState.Granted
                    : ReconcileState.Missing;
            }

            return ReconcileState.Unknown;
        }

        /// <summary>
        /// 写入/更新一条本地发放记录（保留 <see cref="LocalGrant.Attempted"/>：一旦由本类补发就永远是 <c>true</c>）。
        /// <para>调用方为 <see cref="Reconcile"/>（台账已有记录 / 商店重复报告），不改变"是否由本类补发"的标记。</para>
        /// </summary>
        /// <param name="receiptId">收据 ID。</param>
        /// <param name="itemId">收据声明的道具 ID。</param>
        /// <param name="count">收据声明的数量。</param>
        private void LockGrant(string receiptId, int itemId, int count)
        {
            if (_grants.TryGetValue(receiptId, out LocalGrant existing))
            {
                if (existing.ItemId == itemId && existing.Count == count)
                {
                    return;
                }

                // 同号收据内容变化（商店/服务端数据异常）：以最新内容为准，但"已补发"标记不丢。
                _grants[receiptId] = new LocalGrant(itemId, count, existing.Attempted);
                return;
            }

            _grants.Add(receiptId, new LocalGrant(itemId, count, false));
            _grantOrder.Add(receiptId);
        }

        /// <summary>把一条收据标记为"由本类补发"（幂等：重复调用不会重复插序）。</summary>
        /// <param name="receiptId">收据 ID。</param>
        /// <param name="itemId">收据声明的道具 ID。</param>
        /// <param name="count">收据声明的数量。</param>
        private void MarkGrantAttempted(string receiptId, int itemId, int count)
        {
            if (_grants.TryGetValue(receiptId, out LocalGrant existing))
            {
                _grants[receiptId] = new LocalGrant(itemId, count, true);
                return;
            }

            _grants.Add(receiptId, new LocalGrant(itemId, count, true));
            _grantOrder.Add(receiptId);
            _orphanReported.Remove(receiptId);
        }

        /// <summary>忘掉一条发放记录（退款后用：商店不再列它是预期行为，不该再算孤儿）。</summary>
        /// <param name="receiptId">收据 ID。</param>
        private void ForgetGrant(string receiptId)
        {
            if (!_grants.Remove(receiptId))
            {
                return;
            }

            _grantOrder.Remove(receiptId);
            _orphanReported.Remove(receiptId);
        }

        /// <summary>
        /// 追加孤儿动作：本地有发放记录、但本批收据里没有它（每条只报一次，按插入顺序稳定输出）。
        /// </summary>
        /// <param name="batch">本批出现过的收据 ID 集合。</param>
        /// <param name="actions">动作收集器（追加到末尾）。</param>
        private void AppendOrphanActions(HashSet<string> batch, List<ReconcileAction> actions)
        {
            for (int i = 0; i < _grantOrder.Count; i++)
            {
                string receiptId = _grantOrder[i];
                if (batch.Contains(receiptId))
                {
                    // 收据重新出现：清掉"已上报"标记，将来再次消失还能再报一次。
                    _orphanReported.Remove(receiptId);
                    continue;
                }

                if (!_grants.TryGetValue(receiptId, out LocalGrant grant))
                {
                    continue;
                }

                if (!_orphanReported.Add(receiptId))
                {
                    continue;
                }

                actions.Add(new ReconcileAction(
                    receiptId, ReconcileState.Orphan, grant.ItemId, grant.Count, ReasonOrphan));
            }
        }

        /// <summary>把判定结果映射成稳定的动作原因常量。</summary>
        /// <param name="state">判定结果。</param>
        /// <returns>原因常量。</returns>
        private static string ReasonOf(ReconcileState state)
        {
            switch (state)
            {
                case ReconcileState.Granted:
                    return ReasonGranted;
                case ReconcileState.Missing:
                    return ReasonMissing;
                case ReconcileState.Duplicated:
                    return ReasonDuplicated;
                case ReconcileState.Refunded:
                    return ReasonRefunded;
                case ReconcileState.Orphan:
                    return ReasonOrphan;
                default:
                    return ReasonUnknownState;
            }
        }

        /// <summary>
        /// 一条本地发放记录：收据声明的内容 + 是否由本类补发（后者决定"重复出现"是否算 Duplicated）。
        /// </summary>
        private readonly struct LocalGrant
        {
            /// <summary>构造一条发放记录。</summary>
            /// <param name="itemId">收据声明的道具 ID。</param>
            /// <param name="count">收据声明的数量。</param>
            /// <param name="attempted">是否由本类补发过。</param>
            internal LocalGrant(int itemId, int count, bool attempted)
            {
                ItemId = itemId;
                Count = count;
                Attempted = attempted;
            }

            /// <summary>收据声明的道具 ID。</summary>
            internal int ItemId { get; }

            /// <summary>收据声明的数量。</summary>
            internal int Count { get; }

            /// <summary>是否由本类补发过（<c>true</c> 时该收据再次出现判为 <see cref="ReconcileState.Duplicated"/>）。</summary>
            internal bool Attempted { get; }
        }
    }
}
