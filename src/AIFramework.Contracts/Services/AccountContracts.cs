#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 账号类型：游客（设备匿名号）或第三方平台账号（Apple / Google / 微信…）。
    /// <para>
    /// <b>为什么类型进契约</b>：它决定"这个号能不能被找回"。游客号丢了就是永久丢档，
    /// 第三方号换机可恢复。UI 必须能区分两者（"绑定账号以保存进度"提示只在
    /// <see cref="Guest"/> 出现），客服/运营也必须能按类型区分申诉路径，
    /// 因此它不能只存在于某个适配器的私有字段里。
    /// </para>
    /// <para>底层类型固定 <c>byte</c>：类型值会进日志/埋点/存档，数值顺序一旦变更会让历史数据串档。</para>
    /// </summary>
    public enum AccountKind : byte
    {
        /// <summary>游客：不需要任何平台凭据，首次静默登录由后端分配一个匿名号。</summary>
        Guest = 0,

        /// <summary>第三方：由平台 SDK 提供 token，后端换出正式账号；可跨设备找回。</summary>
        ThirdParty = 1
    }

    /// <summary>
    /// 当前会话状态。<b>这是"客户端看到的账号状态"，不是玩家在后台的全部属性</b>。
    /// <para>
    /// <b>为什么 <see cref="Expired"/> 与 <see cref="SignedOut"/> 必须分开</b>：两者对玩家的含义完全不同。
    /// 未登录是"你还没登录"（可以随便点游客登录）；会话过期是"你本来是 <c>guest-1</c>，
    /// 但凭据已经不能用了"（必须重新登录，且必须尽量避免换号——换号等于丢档）。
    /// 合成一个状态，UI 就只能对过期玩家说"请登录"，玩家一点就拿到一个新游客号，进度全丢。
    /// </para>
    /// <para>
    /// <b><see cref="Banned"/> 客户端只展示</b>：封禁是服务端裁决，客户端不做解封、不做倒计时、
    /// 更不允许"重试登录绕过"。真实解封路径在运营后台，本框架不伪造。
    /// </para>
    /// <para>数值顺序固定（会进日志/埋点），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum AccountState : byte
    {
        /// <summary>未登录（首次启动 / 主动登出）。可以发起任何登录。</summary>
        SignedOut = 0,

        /// <summary>已登录：<see cref="IAccountService.TryGetAccount"/> 此时必须能给出账号。</summary>
        SignedIn = 1,

        /// <summary>会话过期（需要重新登录或静默续期）；账号身份仍应被记住，用于"别换号"提示。</summary>
        Expired = 2,

        /// <summary>封禁（客户端只展示，不做解封逻辑）。</summary>
        Banned = 3
    }

    /// <summary>
    /// 一个账号的只读快照：玩家 id + 类型 + 展示名 + 创建时间。
    /// <para>
    /// <b><see cref="PlayerId"/> 是全局唯一主键</b>：存档、排行榜、邮件、订单全部按它归属。
    /// 它由后端分配、客户端<b>只读</b>——客户端自己拼 id 就等于可以把别人的存档"抢"过来。
    /// </para>
    /// <para>
    /// <b>字符串在构造期归一化，读取期再兜底一次</b>：<c>default(AccountInfo)</c> 会绕过构造器，
    /// 其字段是 <c>null</c>；因此 getter 对 <c>null</c> 一律返回空串。读方永远不用判 <c>null</c>，
    /// 只需判 <see cref="PlayerId"/> 是否为空串（空串 = 没有账号）。
    /// </para>
    /// <para>
    /// <b><see cref="DisplayName"/> 不是唯一键、也不可信</b>：它来自玩家输入或平台昵称，
    /// 会变、会重名、可能含表情与敏感词。任何归属判定只许用 <see cref="PlayerId"/>。
    /// </para>
    /// </summary>
    public readonly struct AccountInfo
    {
        /// <summary>玩家 id 的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _playerId;

        /// <summary>展示名的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _displayName;

        /// <summary>构造一个账号快照。</summary>
        /// <param name="playerId">后端分配的玩家 id（<c>null</c> 归一化为空串；空串表示"没有账号"）。</param>
        /// <param name="kind">账号类型（决定能否跨设备找回）。</param>
        /// <param name="displayName">展示名（<c>null</c> 归一化为空串；不参与归属判定）。</param>
        /// <param name="createdAtUnixSeconds">账号创建时间（Unix 秒）；后端未给时允许为 0。</param>
        public AccountInfo(string playerId, AccountKind kind, string displayName, long createdAtUnixSeconds)
        {
            _playerId = playerId;
            _displayName = displayName;
            Kind = kind;
            CreatedAtUnixSeconds = createdAtUnixSeconds;
        }

        /// <summary>后端分配的玩家 id（空串 = 没有账号；<b>永远不为 <c>null</c></b>）。</summary>
        public string PlayerId
        {
            get { return _playerId ?? string.Empty; }
        }

        /// <summary>账号类型（游客 / 第三方）。</summary>
        public AccountKind Kind { get; }

        /// <summary>展示名（可能为空串；会变、会重名，禁止用于归属判定）。</summary>
        public string DisplayName
        {
            get { return _displayName ?? string.Empty; }
        }

        /// <summary>账号创建时间（Unix 秒；0 = 未知）。</summary>
        public long CreatedAtUnixSeconds { get; }
    }

    /// <summary>
    /// 一次登录尝试的结果码。
    /// <para>
    /// <b>为什么 <see cref="AlreadySignedIn"/> 不等于成功</b>：它的真实含义是"后端已有一个会话，
    /// 本次调用<b>没有</b>换号"。这时若谎报成功，玩家点了"用 Apple 账号登录"却仍停在游客号上，
    /// 下次换机就找不到进度——这是真实的丢档事故，必须让调用方显式看到"没换"。
    /// 想真正切号，流程固定为 <c>SignOut()</c> → <c>SignInWithThirdParty(token)</c>。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="Unavailable"/> 与 <see cref="Rejected"/> 必须分开</b>：前者是"后端没接/网不通"，
    /// 调用方应降级继续玩（游客态可离线落地）；后者是"凭据无效/被拒"，重试同一个 token 永远不会成功，
    /// 必须提示玩家换方式。把两者合并，离线玩家会被反复弹"登录失败"。
    /// </para>
    /// <para>数值顺序固定（会进日志/埋点），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum AccountLoginResult : byte
    {
        /// <summary>成功：账号已就绪，<see cref="AccountLoginOutcome.Info"/> 可用。</summary>
        Success = 0,

        /// <summary>后端已有会话，本次未换号（<see cref="AccountLoginOutcome.Info"/> 可能是<b>另一个</b>账号）。</summary>
        AlreadySignedIn = 1,

        /// <summary>后端未接线 / 网络不可用 / 实现违约抛异常：可稍后重试，调用方应降级而不是崩溃。</summary>
        Unavailable = 2,

        /// <summary>凭据无效或被拒（空 token、伪造 token、平台鉴权失败）：同一凭据重试无意义。</summary>
        Rejected = 3,

        /// <summary>该账号被封禁（客户端只展示，不解封、不重试）。</summary>
        Banned = 4
    }

    /// <summary>
    /// 一次登录尝试的完整结果：结果码 + 账号快照 + 人类可读原因，<b>只读</b>。
    /// <para>
    /// <b>业务判定只看 <see cref="Result"/></b>：<see cref="Reason"/> 是诊断文本，允许各实现给不同的
    /// 可读描述，禁止拿字符串比较做分支（否则换一个适配器、换一版文案就会静默走错分支）。
    /// </para>
    /// <para>
    /// <b>失败时 <see cref="Info"/> 是 <c>default</c></b>（玩家 id 为空串）：失败路径没有"登录到谁"可言，
    /// 留半截值只会让调用方误以为可以照着写存档。
    /// </para>
    /// </summary>
    public readonly struct AccountLoginOutcome
    {
        /// <summary>原因的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _reason;

        /// <summary>构造一次登录结果。</summary>
        /// <param name="result">结果码（业务判定唯一依据）。</param>
        /// <param name="info">账号快照（失败时传 <c>default</c>）。</param>
        /// <param name="reason">诊断原因（<c>null</c> 归一化为空串；禁止作为业务判定依据）。</param>
        public AccountLoginOutcome(AccountLoginResult result, AccountInfo info, string reason)
        {
            Result = result;
            Info = info;
            _reason = reason;
        }

        /// <summary>结果码。</summary>
        public AccountLoginResult Result { get; }

        /// <summary>账号快照（<see cref="Result"/> 非 <see cref="AccountLoginResult.Success"/> 时通常是 <c>default</c>）。</summary>
        public AccountInfo Info { get; }

        /// <summary>诊断原因（可能为空串；禁止作为业务判定依据）。</summary>
        public string Reason
        {
            get { return _reason ?? string.Empty; }
        }

        /// <summary>
        /// 是否"本次调用真正建立了登录态"：<b>只有</b> <see cref="AccountLoginResult.Success"/> 为 <c>true</c>。
        /// <para>
        /// <see cref="AccountLoginResult.AlreadySignedIn"/> 刻意算 <c>false</c>：它代表"没换号"，
        /// 见 <see cref="AccountLoginResult"/> 的丢档说明。调用方若要判断"当前是否有可用账号"，
        /// 应当读 <see cref="IAccountService.State"/> / <see cref="IAccountService.TryGetAccount"/>，
        /// 而不是复用本属性。
        /// </para>
        /// </summary>
        public bool IsSuccess
        {
            get { return Result == AccountLoginResult.Success; }
        }
    }

    /// <summary>
    /// 账号后端：真实实现走平台 SDK / 服务端；Phase 1 由适配层假实现。
    /// <para>
    /// <b>为什么整块都是同步签名</b>：契约必须能被"纯内存假账号后端"直接实现，这样
    /// 登录/绑定/登出/换号/会话失效能在无头测试与断网真机上完整验收；真实 SDK 的异步
    /// （Apple 登录弹窗、Google Play Games 静默登录）由适配层用"请求 + 回调/后台等待"包一层，
    /// 把结果喂进本端口。把 <c>Task</c> 写进契约会逼纯逻辑层依赖异步调度
    /// （与 <see cref="IIapService"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>实现方负责的状态口径</b>：<see cref="State"/> 必须与 <see cref="TryGetAccount"/> 自洽——
    /// 只有 <see cref="AccountState.SignedIn"/> 时才能给出账号；<see cref="AccountState.Expired"/>
    /// 时账号身份<b>不再可信</b>（服务端已拒），但 SDK 侧通常仍记得"上次是谁"，
    /// 需要展示时由上层自己缓存，不要指望本接口在过期态还能返回账号。
    /// </para>
    /// <para>
    /// <b>禁止抛异常</b>：登录失败必须落成 <see cref="AccountLoginResult"/>。
    /// 一次平台 SDK 抖动不允许把正在启动的游戏打断（调用方仍会兜底捕获，但那是第二道防线）。
    /// </para>
    /// <para>线程模型：实现方自行声明；框架侧按单线程串行调用（登录/绑定/登出都在主线程）。</para>
    /// </summary>
    public interface IAccountService
    {
        /// <summary>
        /// 后端当前是否可用（SDK 未初始化/未接线/断网都必须如实返回 <c>false</c>，不得"假装成功"）。
        /// <para>调用方必须先判它：不可用时登录一律降级为可离线游玩的游客态，而不是反复弹失败。</para>
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>游客静默登录（首次会生成玩家 id）。</summary>
        /// <returns>登录结果（永不抛异常）。</returns>
        AccountLoginOutcome SignInAsGuest();

        /// <summary>第三方登录（token 由平台 SDK 提供；Phase 1 只校验非空）。</summary>
        /// <param name="platformToken">平台凭据（空串 → <see cref="AccountLoginResult.Rejected"/>）。</param>
        /// <returns>登录结果（永不抛异常）。</returns>
        AccountLoginOutcome SignInWithThirdParty(string platformToken);

        /// <summary>绑定第三方到当前账号（已登录时）；绑定成功后账号类型变为 <see cref="AccountKind.ThirdParty"/>。</summary>
        /// <param name="platformToken">平台凭据。</param>
        /// <param name="reason">成功/失败的诊断原因（失败时永远给出可读原因）。</param>
        /// <returns>是否绑定成功。</returns>
        bool TryBindThirdParty(string platformToken, out string reason);

        /// <summary>登出（本地清态；后端会话失效由服务端处理）。重复调用必须幂等。</summary>
        void SignOut();

        /// <summary>当前会话状态。</summary>
        AccountState State { get; }

        /// <summary>读取当前账号；只有 <see cref="AccountState.SignedIn"/> 时返回 <c>true</c>。</summary>
        /// <param name="info">账号快照（失败时为 <c>default</c>）。</param>
        /// <returns>是否有可用账号。</returns>
        bool TryGetAccount(out AccountInfo info);
    }
}
