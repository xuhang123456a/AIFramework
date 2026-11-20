#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 广告位类型。
    /// <para>
    /// <b>为什么广告位类型进制契约</b>：决定"能不能发奖"的是<b>广告位语义</b>，不是某家聚合 SDK 的枚举。
    /// <see cref="RewardedVideo"/> 只有在用户完整看完时才允许发奖；<see cref="Interstitial"/> 天然没有奖励；
    /// <see cref="Banner"/> 是常驻展示，没有"这次展示"的概念。把这三条写进契约后，
    /// 换任何广告 SDK（IronSource / AdMob / 未来自建）发奖口径都不变。
    /// </para>
    /// <para>
    /// 底层类型固定 <c>byte</c>：类型值会进日志/埋点/存档，数值顺序一旦变更会让历史数据串档，只允许追加。
    /// </para>
    /// </summary>
    public enum AdPlacement : byte
    {
        /// <summary>激励视频：必须完播才发奖（点击/提前关闭只能得到 <see cref="AdShowState.Skipped"/>）。</summary>
        RewardedVideo = 0,

        /// <summary>插屏：无奖励，只有"展示成功/被关闭"两种结果。</summary>
        Interstitial = 1,

        /// <summary>横幅：常驻展示，Phase 1 只记账不改布局（不参与冷却/配额判定）。</summary>
        Banner = 2
    }

    /// <summary>
    /// 一次广告请求的处理结果状态。
    /// <para>
    /// <b>为什么不只有"成功/失败"</b>：广告链路里"冷却中"、"今日次数用尽"、"无填充"对玩家的提示完全不同
    /// （倒计时 / 明日再来 / 稍后再试），合并成失败会让 UI 只能弹一句无意义的红字。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="Shown"/> 与 <see cref="Skipped"/> 都算"展示过"</b>：平台侧只要广告起播就算一次曝光并计入
    /// eCPM，用户中途关掉同样消耗了这次展示。发奖与"是否消耗"必须分开判断，所以契约里把两件事拆成
    /// <see cref="AdShowState"/> 与调用方的奖励逻辑（见 <see cref="AdShowResult.IsRewarded"/>）。
    /// </para>
    /// <para>数值顺序固定（会被写进日志/埋点），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum AdShowState : byte
    {
        /// <summary>已展示并完播（激励视频在此状态下才允许发奖）。</summary>
        Shown = 0,

        /// <summary>用户中途关闭（本次展示已消耗配额/冷却，但不发奖）。</summary>
        Skipped = 1,

        /// <summary>冷却中：该广告位距上次展示还不满冷却秒数，调用方应展示倒计时。</summary>
        CoolingDown = 2,

        /// <summary>当日配额用尽（自然日重置，见 <c>AdShowScheduler</c> 的天序号口径）。</summary>
        QuotaExceeded = 3,

        /// <summary>无填充 / SDK 未接线 / 未知广告位：本次不可展示，可稍后重试。</summary>
        Unavailable = 4
    }

    /// <summary>
    /// 一次广告展示请求：广告位类型 + 广告位 id，<b>只读</b>。
    /// <para>
    /// <b>为什么要 <see cref="PlacementId"/></b>：冷却与配额是按"具体广告位"分组的（首页的激励视频和结算页的
    /// 激励视频是两个互不干扰的位），业务方自定义 id 即可，框架不需要理解它的含义。
    /// </para>
    /// <para>
    /// <b>构造期归一化</b>：<c>placementId &lt; 0</c> 一律视为 <c>0</c>。负 id 来自配置错误或调用方笔误，
    /// 让它进入冷却/配额的分组键只会制造"看起来能看、其实永远冷却"的幽灵广告位。
    /// </para>
    /// </summary>
    public readonly struct AdShowRequest
    {
        /// <summary>构造一次广告展示请求。</summary>
        /// <param name="placement">广告位类型。</param>
        /// <param name="placementId">业务方自定义的广告位 id（<c>&lt; 0</c> 归一化为 <c>0</c>）。</param>
        public AdShowRequest(AdPlacement placement, int placementId)
        {
            Placement = placement;
            PlacementId = placementId < 0 ? 0 : placementId;
        }

        /// <summary>广告位类型（决定是否可发奖、用哪套冷却与配额）。</summary>
        public AdPlacement Placement { get; }

        /// <summary>广告位 id（已归一化，恒 <c>&gt;= 0</c>；用于冷却/配额分组）。</summary>
        public int PlacementId { get; }
    }

    /// <summary>
    /// 一次广告请求的处理结果：状态 + 广告位 + 人类可读原因，<b>只读</b>。
    /// <para>
    /// <b>业务判定只看状态</b>：<see cref="IsRewarded"/> 才是"可以发奖"的唯一依据；
    /// <see cref="Reason"/> 是诊断文本，允许各实现给不同的可读描述，禁止拿字符串比较做分支。
    /// </para>
    /// <para>
    /// <b><see cref="IAdService.CanShow"/> 也会返回本结构</b>：此时 <see cref="AdShowState.Shown"/> 表示
    /// "当前可展示"（尚未真正展示），<see cref="IsRewarded"/> 不应被当作"已发奖"使用——
    /// 只有 <see cref="IAdService.Show"/> 的返回才代表一次真实展示的结果。
    /// </para>
    /// </summary>
    public readonly struct AdShowResult
    {
        /// <summary>构造一次广告处理结果。</summary>
        /// <param name="state">结果状态。</param>
        /// <param name="placement">广告位类型。</param>
        /// <param name="placementId">广告位 id（<c>&lt; 0</c> 归一化为 <c>0</c>）。</param>
        /// <param name="reason">诊断原因（<c>null</c> 归一化为空串；禁止作为业务判定依据）。</param>
        public AdShowResult(AdShowState state, AdPlacement placement, int placementId, string? reason)
        {
            State = state;
            Placement = placement;
            PlacementId = placementId < 0 ? 0 : placementId;
            Reason = reason ?? string.Empty;
        }

        /// <summary>结果状态。</summary>
        public AdShowState State { get; }

        /// <summary>广告位类型（与请求一致；未知枚举值原样带回，便于定位配置错误）。</summary>
        public AdPlacement Placement { get; }

        /// <summary>广告位 id（已归一化，恒 <c>&gt;= 0</c>）。</summary>
        public int PlacementId { get; }

        /// <summary>诊断原因（可能为空串）。</summary>
        public string Reason { get; }

        /// <summary>
        /// 本次是否"可以发放激励视频奖励"：<b>只有</b>"已完播的激励视频"为 <c>true</c>。
        /// <para>
        /// 插屏/横幅永远为 <c>false</c>（它们没有奖励）；<see cref="AdShowState.Skipped"/>、
        /// <see cref="AdShowState.CoolingDown"/>、<see cref="AdShowState.QuotaExceeded"/>、
        /// <see cref="AdShowState.Unavailable"/> 也都为 <c>false</c>——
        /// 用户中途关闭绝不能因为"状态看起来像成功了"而白送奖励。
        /// </para>
        /// </summary>
        public bool IsRewarded
        {
            get { return State == AdShowState.Shown && Placement == AdPlacement.RewardedVideo; }
        }
    }

    /// <summary>
    /// 广告端口：真实 SDK 由适配层实现；框架只定义"能否展示 / 展示一次"两条语义。
    /// <para>
    /// <b>为什么是同步签名</b>：广告 SDK 的真实拉起是异步（加载 → 展示 → 关闭回调），但契约必须能被
    /// "纯内存假填充"直接实现，这样冷却/配额/发奖逻辑能在无头环境逐条验收；真实 SDK 的异步流程由适配层
    /// 用"预加载队列 + 关闭回调"包一层再喂进本端口（与 <see cref="IIapService"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>禁止抛异常</b>：任何失败都必须落成 <see cref="AdShowState"/>；广告是打断性很强的功能，
    /// 不允许因为一次无填充把正在玩的游戏打断。
    /// </para>
    /// <para>
    /// <b>发奖口径</b>：调用方只认 <see cref="AdShowResult.IsRewarded"/>。展示被跳过（<see cref="AdShowState.Skipped"/>）
    /// 时配额与冷却已经消耗，但奖励一个字都不能发。
    /// </para>
    /// <para>线程模型：由实现方声明；框架侧按单线程串行调用（展示发起与发奖都在主线程）。</para>
    /// </summary>
    public interface IAdService
    {
        /// <summary>
        /// 广告当前是否可用（SDK 未初始化/未接线/无填充时必须如实返回 <c>false</c>，不得"假装成功"）。
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 尝试展示一次广告；实现方负责真正拉起 SDK，并返回"是否完播"（见 <see cref="AdShowResult.State"/>）。
        /// <para>
        /// 实现方必须保证：只有真正展示出去才消耗配额/冷却；被
        /// <see cref="AdShowState.CoolingDown"/> / <see cref="AdShowState.QuotaExceeded"/> /
        /// <see cref="AdShowState.Unavailable"/> 拦下的调用不得改变任何记账状态。
        /// </para>
        /// </summary>
        /// <param name="request">展示请求（<c>in</c> 传参，不复制结构体）。</param>
        /// <returns>展示结果（永不抛异常）。</returns>
        AdShowResult Show(in AdShowRequest request);

        /// <summary>
        /// 查询某广告位当前是否可展示（可用性 / 配额 / 冷却的综合判断），<b>纯查询、无副作用</b>。
        /// <para>
        /// 返回 <see cref="AdShowState.Shown"/> 表示"现在可以展示"（尚未展示），供 UI 决定按钮可点性与倒计时；
        /// 它不会消耗配额、不会启动冷却，调用方可以按帧/按秒重复查询。
        /// </para>
        /// </summary>
        /// <param name="request">查询请求（<c>in</c> 传参，不复制结构体）。</param>
        /// <returns>可展示性结果（永不抛异常）。</returns>
        AdShowResult CanShow(in AdShowRequest request);
    }
}
