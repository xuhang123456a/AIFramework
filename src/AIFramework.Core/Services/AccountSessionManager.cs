#nullable enable
using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 登录态状态机：把 <see cref="IAccountService"/>（后端）包成"可启动、可过期、可换号"的会话大脑，
    /// 让切片与 UI 只需要问三个问题——现在是什么状态、我是谁、我要不要重新登录。
    /// <para>
    /// <b>为什么这段逻辑必须在 Core 而不是某个适配器里</b>：会话过期、换号、不可用降级这三条规则
    /// 在 Console 与 Unity、以及未来的真实 SDK 上必须<b>逐字一致</b>；写进任一适配器，
    /// 另一端就会漂移出"只在真机上丢档"的差异。Core 是唯一能被两个装配入口共用、
    /// 又能被无头单测逐条钉死的位置（与 <see cref="StorePurchaseCoordinator"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>状态机（唯一合法的事实源就是 <see cref="State"/>）</b>：
    /// <code>
    /// SignedOut --SignInAsGuest/SignInWithThirdParty 成功--> SignedIn
    /// SignedIn  --Tick 超时 / 后端 Expired---------------> Expired
    /// SignedIn  --后端 Banned / 登录返回 Banned-----------> Banned
    /// Expired   --显式 SignIn* 成功（命中后端既有会话）----> SignedIn
    /// Expired   --SignOut()----------------------------> SignedOut
    /// SignedIn/Expired/Banned --SignOut()---------------> SignedOut
    /// </code>
    /// 任何时刻 <see cref="TryGetAccount"/> 为 <c>true</c> ⟺ <see cref="State"/> ==
    /// <see cref="AccountState.SignedIn"/>（见类型内不变量注释），失败态一律不返回账号。
    /// </para>
    /// <para>
    /// <b><see cref="EnsureSignedIn"/> 刻意不做静默续期</b>：它是"启动时静默登录"，不是"用户点了重新登录"。
    /// 过期后自动再登一次，最可能的结局是拿到一个<b>新游客号</b>——玩家看到的是一切正常，
    /// 实际上进度已经挂在一个空账号上。因此过期后的静默登录固定返回
    /// <see cref="AccountLoginResult.Rejected"/>（原因 <see cref="ReasonSessionExpired"/>），
    /// 由 UI 显式引导玩家重新登录；只有显式 <see cref="SignInAsGuest"/> / <see cref="SignInWithThirdParty"/>
    /// 才会真正续期（且续期命中后端既有会话时玩家 id 不变，见 <see cref="SignInAsGuest"/> 注释）。
    /// </para>
    /// <para>
    /// <b>登录态判定双保险</b>：本类自己算 TTL（单调钟，见 <see cref="ITimeProvider.ElapsedSeconds"/>，
    /// 不读墙钟——玩家改系统时间不该延长会话），同时每 <see cref="Tick"/> 拉一次后端状态
    /// （后端说 Expired/Banned 立刻跟随）。任一条成立都算失效，宁可早失效，不可晚失效。
    /// </para>
    /// <para>
    /// <b>线程模型：非线程安全</b>。所有调用（登录、绑定、登出、<see cref="Tick"/>）必须串行在主线程；
    /// 本类不加锁、不做重入保护，<see cref="Tick"/> 与 UI 事件回调同线程即可（回调里再调本类是允许的）。
    /// </para>
    /// </summary>
    public sealed class AccountSessionManager
    {
        /// <summary>默认会话有效期（秒，24 小时）。</summary>
        public const long DefaultSessionTtlSeconds = 86400;

        /// <summary>
        /// 平台 token 长度上限（字符数）。
        /// <para>超长串不是合法凭据，且会原样进日志/请求头，必须在进后端之前挡掉；适配层共用同一上限。</para>
        /// </summary>
        public const int MaxPlatformTokenLength = 512;

        /// <summary>失败原因：后端不可用（未接线 / 断网 / 实现违约）。</summary>
        public const string ReasonUnavailable = "account service unavailable";

        /// <summary>失败原因：会话已过期，需要显式重新登录。</summary>
        public const string ReasonSessionExpired = "session expired";

        /// <summary>失败原因：账号被封禁（客户端只展示）。</summary>
        public const string ReasonBanned = "account banned";

        /// <summary>失败原因：当前不是登录态（绑定/续期类操作要求已登录）。</summary>
        public const string ReasonNotSignedIn = "not signed in";

        /// <summary>失败原因：平台 token 为空。</summary>
        public const string ReasonEmptyToken = "empty platform token";

        /// <summary>失败原因：平台 token 超过 <see cref="MaxPlatformTokenLength"/>。</summary>
        public const string ReasonTokenTooLong = "platform token too long";

        /// <summary>失败原因：后端返回成功却没有玩家 id（违约实现，登录结果不可用）。</summary>
        public const string ReasonEmptyPlayerId = "empty player id";

        /// <summary>成功原因：已登录且未过期，直接复用现有会话（没有触达后端）。</summary>
        public const string ReasonSessionReused = "session reused";

        /// <summary>成功原因：过期后显式重新登录，命中后端既有会话（玩家 id 未变）。</summary>
        public const string ReasonSessionRenewed = "session renewed";

        /// <summary>失败原因前缀：后端实现违约抛异常（后接异常类型名，不含堆栈）。</summary>
        public const string ReasonServiceThrew = "account service threw";

        /// <summary>后端端口；构造后不可变。</summary>
        private readonly IAccountService _accounts;

        /// <summary>时间源（单调钟算 TTL）；构造后不可变。</summary>
        private readonly ITimeProvider _time;

        /// <summary>会话有效期（秒）；<c>&lt;= 0</c> 表示关闭 TTL 判定（见构造函数注释）。</summary>
        private readonly long _sessionTtlSeconds;

        /// <summary>当前状态（唯一事实源）。</summary>
        private AccountState _state;

        /// <summary>当前账号快照；<b>不变量</b>：非 <see cref="AccountState.SignedIn"/> 时恒为 <c>default</c>。</summary>
        private AccountInfo _info;

        /// <summary>会话到期时刻（单调秒）。只在登录成功时写；不随 <see cref="EnsureSignedIn"/> 延长。</summary>
        private double _expiresAtElapsedSeconds;

        /// <summary>
        /// 最近一次成功登录/绑定使用的第三方凭据；<c>null</c> = 当前是游客态或已登出。
        /// <para>只用于<b>换号安全</b>与诊断（见 <see cref="HasPlatformToken"/>），绝不落盘、绝不进日志。</para>
        /// </summary>
        private string? _platformToken;

        /// <summary>
        /// 状态变化通知（只在与上次不同时触发一次）。
        /// <para>
        /// <b>为什么选事件而不是构造注入回调</b>：订阅方数量与时机都不固定（UI 面板会随场景创建/销毁，
        /// 而管理器是跨场景长生命周期对象）；用 <c>+=</c>/<c>-=</c> 让订阅方自己管生命周期，
        /// 比在构造期把回调钉死更不容易泄漏，也不要求装配顺序。
        /// </para>
        /// <para>
        /// <b>订阅方抛异常不影响会话</b>：状态先落地、再通知，且每个订阅者单独 try/catch 吞掉异常
        /// （一次 UI 空引用不该让"登录成功"变成"登录失败"）。
        /// </para>
        /// </summary>
        public event Action<AccountState>? StateChanged;

        /// <summary>创建登录态管理器。</summary>
        /// <param name="accounts">账号后端（非 <c>null</c>）。</param>
        /// <param name="time">时间源（非 <c>null</c>；TTL 走单调钟）。</param>
        /// <param name="sessionTtlSeconds">
        /// 会话有效期（秒，默认 <see cref="DefaultSessionTtlSeconds"/>）。
        /// <para>
        /// <b><c>&lt;= 0</c> 视为关闭 TTL 判定</b>：0 秒有效期等于"登录即过期"，
        /// 那是配置事故而不是需求；显式降级成"永不过期"能让游戏继续可玩，同时把问题留在配置侧。
        /// 需要"强制每次启动都重新登录"的用例请用 <see cref="Tick"/> 或后端 Expired 表达。
        /// </para>
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="accounts"/> 或 <paramref name="time"/> 为 <c>null</c>（装配期编程错误，必须立刻响）。
        /// </exception>
        public AccountSessionManager(IAccountService accounts, ITimeProvider time, long sessionTtlSeconds = DefaultSessionTtlSeconds)
        {
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _sessionTtlSeconds = sessionTtlSeconds;

            // 装配顺序可能让后端先登录（例如 Host 先建服务、再建管理器）：构造期接管已有会话，
            // 否则会出现"后端已登录、管理器却认为未登录"，EnsureSignedIn 会再登一次并触发 AlreadySignedIn。
            AdoptExistingBackendSession();
        }

        /// <summary>当前会话状态（<b>业务判定只看它</b>）。</summary>
        public AccountState State
        {
            get { return _state; }
        }

        /// <summary>
        /// 当前是否持有第三方凭据（最近一次成功登录/绑定用的是第三方 token）。
        /// <para>换号 UI 可据此区分"游客号（建议提示绑定）"与"已绑定平台账号"；本类不暴露 token 本身。</para>
        /// </summary>
        public bool HasPlatformToken
        {
            get { return !string.IsNullOrEmpty(_platformToken); }
        }

        /// <summary>
        /// 读取当前账号；<b>只有</b> 已登录时返回 <c>true</c>（过期/封禁/未登录一律 <c>false</c>）。
        /// <para>这条不变量是刻意收死的：调用方拿它当"能不能写存档/拉排行榜"的闸门，比读 <see cref="State"/> 更稳。</para>
        /// </summary>
        /// <param name="info">账号快照（失败时为 <c>default</c>）。</param>
        /// <returns>是否有可用账号。</returns>
        public bool TryGetAccount(out AccountInfo info)
        {
            if (_state == AccountState.SignedIn)
            {
                info = _info;
                return true;
            }

            info = default;
            return false;
        }

        /// <summary>
        /// 启动时静默登录：已登录（且未过期）直接复用；未登录则走游客登录；后端不可用则降级为
        /// <see cref="AccountLoginResult.Unavailable"/>。<b>永不抛异常</b>。
        /// <para>
        /// <b>已过期时不自动重登</b>：返回 <see cref="AccountLoginResult.Rejected"/>（原因
        /// <see cref="ReasonSessionExpired"/>），由 UI 显式引导重新登录——理由见类型注释
        /// （自动重登极可能换来一个全新游客号，等于静默丢档）。
        /// </para>
        /// <para>
        /// <b>复用不收钱、不续期</b>：命中已登录分支时既不调用后端、也不刷新 TTL。
        /// 若这里续期，一个每帧调用 <c>EnsureSignedIn</c> 的切片就能把"24 小时会话"变成"永不过期"，
        /// 服务端的踢人/换机策略将彻底失效。需要续期请走显式登录。
        /// </para>
        /// </summary>
        /// <returns>登录结果（<b>永不抛异常</b>）。</returns>
        public AccountLoginOutcome EnsureSignedIn()
        {
            if (_state == AccountState.SignedIn)
            {
                if (IsPastTtl())
                {
                    // 与 Tick 等价的惰性过期检查：即使没人调 Tick，也不会把过期会话当成有效会话用。
                    Transition(AccountState.Expired);
                    return Failure(AccountLoginResult.Rejected, ReasonSessionExpired);
                }

                return new AccountLoginOutcome(AccountLoginResult.Success, _info, ReasonSessionReused);
            }

            if (_state == AccountState.Banned)
            {
                return Failure(AccountLoginResult.Banned, ReasonBanned);
            }

            if (_state == AccountState.Expired)
            {
                return Failure(AccountLoginResult.Rejected, ReasonSessionExpired);
            }

            // 未登录：后端不可用时不能"假装登录成功"，也不能抛——如实降级，让游戏以离线态继续。
            if (!SafeIsAvailable())
            {
                return Failure(AccountLoginResult.Unavailable, ReasonUnavailable);
            }

            return SignInAsGuest();
        }

        /// <summary>
        /// 游客登录（显式）。成功即建立新会话并刷新 TTL。
        /// <para>
        /// <b>过期后调它会发生什么</b>：后端通常还记得本机游客号，返回
        /// <see cref="AccountLoginResult.AlreadySignedIn"/>；本类会把这次调用当作<b>续期</b>——
        /// 采纳后端账号、返回 <see cref="AccountLoginResult.Success"/>（原因
        /// <see cref="ReasonSessionRenewed"/>），玩家 id 保持不变。这不是"静默换号"：
        /// 换号必须显式 <see cref="SignOut"/>，而且这里采纳的账号就是后端确认的同一个会话。
        /// </para>
        /// <para>
        /// <b>已登录时调它返回 <see cref="AccountLoginResult.AlreadySignedIn"/></b>（不换号、不改状态）：
        /// 想换号请先 <see cref="SignOut"/>。理由见 <see cref="AccountLoginResult"/> 的丢档说明。
        /// </para>
        /// </summary>
        /// <returns>登录结果（<b>永不抛异常</b>）。</returns>
        public AccountLoginOutcome SignInAsGuest()
        {
            if (!SafeIsAvailable())
            {
                return Failure(AccountLoginResult.Unavailable, ReasonUnavailable);
            }

            return ApplyLogin(SignInSafely(guest: true, platformToken: null), platformToken: null);
        }

        /// <summary>
        /// 第三方登录（显式）。token 为空或超长时直接 <see cref="AccountLoginResult.Rejected"/>，
        /// 不触达后端（省一次无效平台调用，也避免超长串进日志）。
        /// <para>已登录时返回 <see cref="AccountLoginResult.AlreadySignedIn"/>：换号必须先登出。</para>
        /// </summary>
        /// <param name="platformToken">平台凭据（由平台 SDK 提供）。</param>
        /// <returns>登录结果（<b>永不抛异常</b>）。</returns>
        public AccountLoginOutcome SignInWithThirdParty(string platformToken)
        {
            if (string.IsNullOrEmpty(platformToken))
            {
                // 空凭据没有"可能成功"的余地：交给后端只会白跑一次平台调用。
                return Failure(AccountLoginResult.Rejected, ReasonEmptyToken);
            }

            if (platformToken.Length > MaxPlatformTokenLength)
            {
                return Failure(AccountLoginResult.Rejected, ReasonTokenTooLong);
            }

            if (!SafeIsAvailable())
            {
                return Failure(AccountLoginResult.Unavailable, ReasonUnavailable);
            }

            return ApplyLogin(SignInSafely(guest: false, platformToken: platformToken), platformToken);
        }

        /// <summary>
        /// 绑定第三方到当前账号（要求已登录）。token 为空/超长/未登录时直接失败并给出原因，不触达后端。
        /// <para>
        /// <b>绑定成功只升级账号类型，不换号</b>：玩家 id 与创建时间保持不变（绑定=保住进度），
        /// 因此不发 <see cref="StateChanged"/>（状态没变）；UI 应在成功返回后主动重读
        /// <see cref="TryGetAccount"/> 刷新展示（新的 <see cref="AccountInfo.Kind"/> 是
        /// <see cref="AccountKind.ThirdParty"/>）。
        /// </para>
        /// <para>
        /// <b>失败不踢人</b>：绑定是一次附加操作，后端拒绝（token 无效/已被占用/平台封禁）只影响这次绑定，
        /// 当前会话与 TTL 都不动——绑不上不该让正在游戏的玩家掉线。
        /// </para>
        /// </summary>
        /// <param name="platformToken">平台凭据。</param>
        /// <param name="reason">诊断原因（成功与失败都给出；调用方不得按文案分支）。</param>
        /// <returns>是否绑定成功。</returns>
        public bool TryBindThirdParty(string platformToken, out string reason)
        {
            if (_state != AccountState.SignedIn)
            {
                reason = _state == AccountState.Expired ? ReasonSessionExpired : ReasonNotSignedIn;
                return false;
            }

            if (string.IsNullOrEmpty(platformToken))
            {
                reason = ReasonEmptyToken;
                return false;
            }

            if (platformToken.Length > MaxPlatformTokenLength)
            {
                reason = ReasonTokenTooLong;
                return false;
            }

            bool bound;
            string backendReason;
            try
            {
                bound = _accounts.TryBindThirdParty(platformToken, out backendReason);
            }
            catch (Exception ex)
            {
                // 契约要求不抛；实现违约时按失败处理，并只带类型名（不含堆栈，避免日志泄露内部路径）。
                reason = ReasonServiceThrew + ": " + ex.GetType().Name;
                return false;
            }

            reason = backendReason ?? string.Empty;
            if (!bound)
            {
                return false;
            }

            _platformToken = platformToken;

            // 以后端为准确认绑定结果；后端没刷新类型时本地补上（绑定成功即"不再是纯游客"）。
            if (TryAdoptBackendAccount(out AccountInfo boundAccount)
                && string.Equals(boundAccount.PlayerId, _info.PlayerId, StringComparison.Ordinal))
            {
                _info = boundAccount;
            }

            if (_info.Kind != AccountKind.ThirdParty)
            {
                _info = new AccountInfo(_info.PlayerId, AccountKind.ThirdParty, _info.DisplayName, _info.CreatedAtUnixSeconds);
            }

            return true;
        }

        /// <summary>
        /// 登出：清本地登录态与凭据，状态回到 <see cref="AccountState.SignedOut"/>。重复调用幂等。
        /// <para>
        /// <b>后端异常也必须登出成功</b>：本地清态是玩家点"退号"的可见结果，而服务端会话失效由服务端负责
        /// （真机上它还会被超时踢掉）。这条路径若因为一次网络异常把玩家卡在"退不出去"的状态，
        /// 共享设备上的下一个玩家就会直接进到别人的存档。
        /// </para>
        /// </summary>
        public void SignOut()
        {
            try
            {
                _accounts.SignOut();
            }
            catch (Exception)
            {
                // 见方法注释：本地清态必须成功，异常在这里终结。
            }

            _platformToken = null;
            _expiresAtElapsedSeconds = 0d;
            Transition(AccountState.SignedOut);
        }

        /// <summary>
        /// 每帧/定时调用：同步 TTL 与后端状态，超时或后端失效则转为
        /// <see cref="AccountState.Expired"/> / <see cref="AccountState.Banned"/>。
        /// <para>
        /// <b>事件只发一次</b>：状态机只在"值真的变了"时通知，因此反复 <see cref="Tick"/> 不会刷屏；
        /// UI 可以安全地每帧调用。
        /// </para>
        /// <para>
        /// <b>不跟随后端 <see cref="AccountState.SignedOut"/></b>：真实 SDK 在初始化/重连期间会短暂报
        /// SignedOut，跟着它走会让在线玩家被莫名踢下线。只有 Expired/Banned 是明确的服务端事实，才跟随。
        /// </para>
        /// </summary>
        public void Tick()
        {
            AccountState backendState = ReadBackendState();

            if (backendState == AccountState.Banned)
            {
                _platformToken = null;
                Transition(AccountState.Banned);
                return;
            }

            if (backendState == AccountState.Expired)
            {
                Transition(AccountState.Expired);
                return;
            }

            if (_state != AccountState.SignedIn)
            {
                return;
            }

            if (IsPastTtl())
            {
                Transition(AccountState.Expired);
            }
        }

        /// <summary>
        /// 构造期接管后端已有会话：装配顺序（先建服务后建管理器）不应该让管理器把已登录玩家当成未登录。
        /// <para>拿不到账号的新后端（SignedIn 但 <see cref="IAccountService.TryGetAccount"/> 为 <c>false</c>）不接管，
        /// 保持 <see cref="AccountState.SignedOut"/>——宁可在启动时再登一次，也不让一个空账号顶着"已登录"。</para>
        /// </summary>
        private void AdoptExistingBackendSession()
        {
            AccountState backendState = ReadBackendState();

            if (backendState == AccountState.Banned)
            {
                _state = AccountState.Banned;
                _info = default;
                return;
            }

            if (backendState != AccountState.SignedIn)
            {
                return;
            }

            if (!TryAdoptBackendAccount(out AccountInfo info))
            {
                return;
            }

            _state = AccountState.SignedIn;
            _info = info;
            _expiresAtElapsedSeconds = _time.ElapsedSeconds + (double)_sessionTtlSeconds;
        }

        /// <summary>
        /// 登录结果的统一落地：成功才进入 <see cref="AccountState.SignedIn"/>，失败只做状态同步（不改账号）。
        /// </summary>
        /// <param name="raw">后端原始结果。</param>
        /// <param name="platformToken">本次使用的第三方凭据（游客登录传 <c>null</c>）。</param>
        /// <returns>对外结果（可能把续期用的 AlreadySignedIn 归一化成 Success）。</returns>
        private AccountLoginOutcome ApplyLogin(AccountLoginOutcome raw, string? platformToken)
        {
            switch (raw.Result)
            {
                case AccountLoginResult.Success:
                {
                    AccountInfo info = raw.Info;
                    if (string.IsNullOrEmpty(info.PlayerId))
                    {
                        // 违约实现：声称成功却没有玩家 id。采纳它等于把所有写操作挂到空主键上，
                        // 因此拒绝进入 SignedIn，并按"后端不可用"报出去（可重试，不会误伤现有会话）。
                        // 同时把后端也退回未登录：否则后端会一直回 AlreadySignedIn，
                        // 之后每次登录都换不回可用会话（客户端与后端状态永久劈叉）。
                        ResetBackendSession();
                        return Failure(AccountLoginResult.Unavailable, ReasonEmptyPlayerId);
                    }

                    AdoptAccount(in info, platformToken);
                    return raw;
                }

                case AccountLoginResult.AlreadySignedIn:
                {
                    // 只有"过期后续期"这一种情况值得归一化：后端确认会话仍然有效，采纳它即可恢复登录态。
                    // 已登录时的 AlreadySignedIn 原样透出（没换号就是没换号）。
                    if (_state == AccountState.Expired && TryAdoptBackendAccount(out AccountInfo renewed))
                    {
                        AdoptAccount(in renewed, platformToken);
                        return new AccountLoginOutcome(AccountLoginResult.Success, renewed, ReasonSessionRenewed);
                    }

                    return raw;
                }

                case AccountLoginResult.Banned:
                {
                    // 封禁是终态：凭据一律丢弃，避免后续请求继续带一个已被封的 token。
                    _platformToken = null;
                    Transition(AccountState.Banned);
                    return raw;
                }

                default:
                {
                    // Unavailable / Rejected：本次尝试失败，但**不改动**已有会话
                    // （已登录玩家不会因为一次失败的换号尝试被踢下线）。
                    return raw;
                }
            }
        }

        /// <summary>采纳一个账号并进入登录态（刷新 TTL、落事件）。</summary>
        /// <param name="info">账号快照（调用方已保证玩家 id 非空）。</param>
        /// <param name="platformToken">本次使用的第三方凭据（游客为 <c>null</c>）。</param>
        private void AdoptAccount(in AccountInfo info, string? platformToken)
        {
            _info = info;
            _platformToken = platformToken;
            _expiresAtElapsedSeconds = _time.ElapsedSeconds + (double)_sessionTtlSeconds;
            Transition(AccountState.SignedIn);
        }

        /// <summary>调后端登录；任何异常都收敛成 <see cref="AccountLoginResult.Unavailable"/>（原因带类型名）。</summary>
        /// <param name="guest">是否游客登录。</param>
        /// <param name="platformToken">第三方凭据（游客传 <c>null</c>）。</param>
        /// <returns>后端原始结果。</returns>
        private AccountLoginOutcome SignInSafely(bool guest, string? platformToken)
        {
            try
            {
                return guest
                    ? _accounts.SignInAsGuest()
                    : _accounts.SignInWithThirdParty(platformToken ?? string.Empty);
            }
            catch (Exception ex)
            {
                // 规则：SignIn* 抛异常 → Unavailable，原因带异常类型名、不带堆栈。
                return Failure(AccountLoginResult.Unavailable, ReasonServiceThrew + ": " + ex.GetType().Name);
            }
        }

        /// <summary>读后端可用性；实现违约抛异常时按"不可用"处理（降级总比崩溃好）。</summary>
        /// <returns>后端是否可用。</returns>
        private bool SafeIsAvailable()
        {
            try
            {
                return _accounts.IsAvailable;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>读后端状态；实现违约抛异常时返回本地状态（本次 Tick 不做状态同步）。</summary>
        /// <returns>后端状态。</returns>
        private AccountState ReadBackendState()
        {
            try
            {
                return _accounts.State;
            }
            catch (Exception)
            {
                return _state;
            }
        }

        /// <summary>从后端取账号（空玩家 id 视为拿不到：空主键的账号不可用）。</summary>
        /// <param name="info">后端给出的账号。</param>
        /// <returns>是否拿到了可用账号。</returns>
        private bool TryAdoptBackendAccount(out AccountInfo info)
        {
            try
            {
                if (_accounts.TryGetAccount(out info) && !string.IsNullOrEmpty(info.PlayerId))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // 读账号失败等价于"后端没给出可接管的会话"。
            }

            info = default;
            return false;
        }

        /// <summary>是否已超过 TTL（单调钟判定；TTL 被关闭时恒为 <c>false</c>）。</summary>
        /// <returns>是否过期。</returns>
        private bool IsPastTtl()
        {
            if (_sessionTtlSeconds <= 0)
            {
                return false;
            }

            return _time.ElapsedSeconds >= _expiresAtElapsedSeconds;
        }

        /// <summary>
        /// 把后端会话退回未登录（只在"后端给了不可用的成功结果"时调用）。
        /// <para>这不是业务登出，而是<b>状态对齐</b>：留着那个空账号会让后端一直回 AlreadySignedIn，
        /// 之后每次登录都拿不回可用会话。异常在这里终结（对齐失败也不该让登录路径抛）。</para>
        /// </summary>
        private void ResetBackendSession()
        {
            try
            {
                _accounts.SignOut();
            }
            catch (Exception)
            {
                // 对齐是尽力而为：失败时本地状态依然是"未登录"，下一次登录会重新尝试。
            }
        }

        /// <summary>
        /// 状态迁移（唯一入口）：值相同则什么都不做（事件只发一次），否则先落状态再通知。
        /// <para><b>不变量维护</b>：离开 <see cref="AccountState.SignedIn"/> 时清空账号快照，
        /// 保证 <see cref="TryGetAccount"/> 与 <see cref="State"/> 永远一致。</para>
        /// </summary>
        /// <param name="next">目标状态。</param>
        private void Transition(AccountState next)
        {
            if (_state == next)
            {
                return;
            }

            _state = next;

            if (next != AccountState.SignedIn)
            {
                _info = default;
            }

            if (next == AccountState.SignedOut || next == AccountState.Banned)
            {
                _expiresAtElapsedSeconds = 0d;
            }

            RaiseStateChanged(next);
        }

        /// <summary>
        /// 通知订阅者：逐个 try/catch，任何一个订阅者抛异常都不影响其余订阅者与会话状态。
        /// <para>状态迁移是低频路径（登录/过期/登出），<c>GetInvocationList</c> 的分配可以接受。</para>
        /// </summary>
        /// <param name="state">迁移后的状态。</param>
        private void RaiseStateChanged(AccountState state)
        {
            Action<AccountState>? handlers = StateChanged;
            if (handlers == null)
            {
                return;
            }

            Delegate[] subscribers = handlers.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                try
                {
                    ((Action<AccountState>)subscribers[i]).Invoke(state);
                }
                catch (Exception)
                {
                    // 订阅方异常不得反向破坏会话状态：状态已经落地，异常在这里终结。
                }
            }
        }

        /// <summary>构造一个失败结果（账号快照为 <c>default</c>）。</summary>
        /// <param name="result">失败结果码。</param>
        /// <param name="reason">诊断原因。</param>
        /// <returns>失败结果。</returns>
        private static AccountLoginOutcome Failure(AccountLoginResult result, string reason)
        {
            return new AccountLoginOutcome(result, default, reason);
        }
    }
}
