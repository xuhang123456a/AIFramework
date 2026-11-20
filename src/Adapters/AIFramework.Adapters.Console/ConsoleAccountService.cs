#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台账号服务：<b>内存假账号后端</b>，用来在无头环境验收 <see cref="IAccountService"/> 的全部语义
    /// （游客登录 / 第三方登录 / 绑定 / 登出 / 换号 / 封禁 / 过期 / 不可用降级）。
    /// <para>
    /// <b>它不是空实现</b>：它真的维护了一份"服务端账本"——
    /// <list type="bullet">
    /// <item>游客号是<b>设备级</b>的：同一个实例重复登录（含登出后再登）永远拿到同一个 <c>guest-1</c>，
    /// 这样才能验收"会话过期后重新登录不会换号丢档"这条最关键的行为；</item>
    /// <item>第三方号按 token 去重：同一个 token 永远映射到同一个 <c>tp-N</c>；</item>
    /// <item>绑定是<b>合并</b>而不是新建：绑定成功后该 token 直接映射到当前玩家 id，
    /// 之后用同一个 token 重新登录拿回的是同一个号（真实后端也是这么做的）。</item>
    /// </list>
    /// 真实 SDK（Apple / Google / 微信）只是把这些步骤换成"取平台凭据 → 调后端换票 → 拿玩家 id"，
    /// 语义必须与这里完全一致——否则无头测试绿、真机上换号就丢档。
    /// </para>
    /// <para>
    /// <b><see cref="BannedToken"/> 这条特殊 token 是刻意留的后门</b>：它让"封禁"这条分支在没有真实
    /// 后台的情况下可被稳定复现（真机上它来自服务端裁决）。它不是生产语义，只是测试夹具。
    /// </para>
    /// <para>
    /// <b>时间只用来填 <see cref="AccountInfo.CreatedAtUnixSeconds"/></b>：不参与任何判定，
    /// 因此没注入 <see cref="ITimeProvider"/> 时记 0 也是安全的（适配层允许读系统时间，但本类不读，
    /// 便于无头测试完全可复现）。
    /// </para>
    /// <para>
    /// <b>日志是排空的环形缓冲</b>：只服务冒烟与排障（<see cref="DrainLog"/> 取走即清空），
    /// 不参与业务判定，因此允许在这里拼字符串（正式逻辑路径禁止动态拼接）。
    /// </para>
    /// <para>线程模型：非线程安全。登录/绑定/登出都在主线程串行调用（账号不是高频路径）。</para>
    /// </summary>
    public sealed class ConsoleAccountService : IAccountService
    {
        /// <summary>日志环形容量（最近 32 条）。</summary>
        public const int LogCapacity = 32;

        /// <summary>平台 token 上限，与 <see cref="AccountSessionManager.MaxPlatformTokenLength"/> 同一口径。</summary>
        public const int MaxPlatformTokenLength = AccountSessionManager.MaxPlatformTokenLength;

        /// <summary>游客玩家 id 前缀。</summary>
        public const string GuestIdPrefix = "guest-";

        /// <summary>第三方玩家 id 前缀。</summary>
        public const string PlatformIdPrefix = "tp-";

        /// <summary>触发"封禁"分支的特殊 token（测试夹具，见类注释）。</summary>
        public const string BannedToken = "banned";

        /// <summary>失败原因：构造/<see cref="SetUnavailable"/> 配置为不可用。</summary>
        public const string ReasonUnavailable = "account service unavailable";

        /// <summary>失败原因：平台 token 为空。</summary>
        public const string ReasonEmptyToken = "empty platform token";

        /// <summary>失败原因：平台 token 超长。</summary>
        public const string ReasonTokenTooLong = "platform token too long";

        /// <summary>失败原因：账号被封禁。</summary>
        public const string ReasonBanned = "account banned";

        /// <summary>失败原因：未登录，不能绑定。</summary>
        public const string ReasonNotSignedIn = "not signed in";

        /// <summary>失败原因：本次调用没有换号（后端已有会话）。</summary>
        public const string ReasonAlreadySignedIn = "already signed in";

        /// <summary>失败原因：该平台账号已经绑定到当前账号。</summary>
        public const string ReasonAlreadyBound = "already bound";

        /// <summary>失败原因：该平台账号已被<b>别的</b>游戏账号占用（真实后端会这样拒绝，防止账号劫持）。</summary>
        public const string ReasonPlatformClaimed = "platform account claimed";

        /// <summary>成功原因：本次建立了新会话。</summary>
        public const string ReasonSignedIn = "signed in";

        /// <summary>成功原因：绑定成功（账号类型升级为第三方，玩家 id 不变）。</summary>
        public const string ReasonBound = "bound";

        /// <summary>游客账号（设备级，只创建一次）；<c>null</c> 表示还没登录过。</summary>
        private AccountInfo? _guestAccount;

        /// <summary>
        /// 平台账号映射：token → 该平台身份对应的游戏账号（登录时新建的 <c>tp-N</c>，或被绑定的玩家号）。
        /// <para>用序号而不是 token 哈希：哈希会碰撞（两个平台号合并成一个号是灾难），序号不会。</para>
        /// </summary>
        private readonly Dictionary<string, AccountInfo> _platformAccounts =
            new Dictionary<string, AccountInfo>(StringComparer.Ordinal);

        /// <summary>可选时间源（只用于填创建时间，见类注释）。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>是否可用（可被 <see cref="SetUnavailable"/> / <see cref="SetAvailable"/> 拨动）。</summary>
        private bool _available;

        /// <summary>当前会话状态。</summary>
        private AccountState _state;

        /// <summary>当前账号快照；<b>不变量</b>：非 <see cref="AccountState.SignedIn"/> 时恒为 <c>default</c>。</summary>
        private AccountInfo _info;

        /// <summary>游客号分配序号（每个实例从 1 开始，模拟"一台设备一个游客号"）。</summary>
        private int _guestSequence;

        /// <summary>第三方号分配序号（每个实例从 1 开始）。</summary>
        private int _platformSequence;

        /// <summary>登录日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置；满员时指向最旧的一条。</summary>
        private int _logNext;

        /// <summary>创建控制台账号服务。</summary>
        /// <param name="time">可选时间源（只用于账号创建时间；<c>null</c> 时记 0）。</param>
        /// <param name="available">是否可用（默认 <c>true</c>；验收离线降级时传 <c>false</c> 或用 <see cref="SetUnavailable"/>）。</param>
        public ConsoleAccountService(ITimeProvider? time = null, bool available = true)
        {
            _time = time;
            _available = available;
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <inheritdoc />
        public AccountState State
        {
            get { return _state; }
        }

        /// <summary>当前已分配的平台账号数量（诊断用）。</summary>
        public int PlatformAccountCount
        {
            get { return _platformAccounts.Count; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 游客号是设备级的：首次调用创建 <c>guest-1</c>，之后（包括登出后）重复调用都返回同一个号。
        /// <b>这是刻意的</b>：游客号一旦换了，玩家的进度就永久留在了旧号上。
        /// 想模拟"换号"请走第三方登录，或新建一个服务实例（等价于换设备）。
        /// </remarks>
        public AccountLoginOutcome SignInAsGuest()
        {
            if (!_available)
            {
                AppendLog("guest sign-in -> Unavailable");
                return Failure(AccountLoginResult.Unavailable, ReasonUnavailable);
            }

            if (_state == AccountState.SignedIn)
            {
                AppendLog("guest sign-in -> AlreadySignedIn(" + _info.PlayerId + ")");
                return Failure(AccountLoginResult.AlreadySignedIn, ReasonAlreadySignedIn);
            }

            if (_state == AccountState.Banned)
            {
                AppendLog("guest sign-in -> Banned");
                return Failure(AccountLoginResult.Banned, ReasonBanned);
            }

            AccountInfo account = _guestAccount ?? CreateGuestAccount();
            _guestAccount = account;
            AdoptSession(account);
            AppendLog("guest sign-in -> Success(" + account.PlayerId + ")");
            return new AccountLoginOutcome(AccountLoginResult.Success, account, ReasonSignedIn);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 同一 token 永远映射到同一个 <c>tp-N</c>（含绑定后映射到既有玩家号），
        /// 因此"换号再换回来"不会丢档。空 token / 超长 token → <see cref="AccountLoginResult.Rejected"/>；
        /// <see cref="BannedToken"/> → <see cref="AccountLoginResult.Banned"/>（并落到
        /// <see cref="AccountState.Banned"/>，凭据不再保留）。
        /// </remarks>
        public AccountLoginOutcome SignInWithThirdParty(string platformToken)
        {
            if (!_available)
            {
                AppendLog("third-party sign-in -> Unavailable");
                return Failure(AccountLoginResult.Unavailable, ReasonUnavailable);
            }

            if (_state == AccountState.SignedIn)
            {
                AppendLog("third-party sign-in -> AlreadySignedIn(" + _info.PlayerId + ")");
                return Failure(AccountLoginResult.AlreadySignedIn, ReasonAlreadySignedIn);
            }

            if (_state == AccountState.Banned)
            {
                AppendLog("third-party sign-in -> Banned");
                return Failure(AccountLoginResult.Banned, ReasonBanned);
            }

            if (string.IsNullOrEmpty(platformToken))
            {
                AppendLog("third-party sign-in -> Rejected(" + ReasonEmptyToken + ")");
                return Failure(AccountLoginResult.Rejected, ReasonEmptyToken);
            }

            if (platformToken.Length > MaxPlatformTokenLength)
            {
                AppendLog("third-party sign-in -> Rejected(" + ReasonTokenTooLong + ")");
                return Failure(AccountLoginResult.Rejected, ReasonTokenTooLong);
            }

            if (string.Equals(platformToken, BannedToken, StringComparison.Ordinal))
            {
                // 封禁是终态：清账号、清凭据；客户端不做解封（由运营后台裁决）。
                _state = AccountState.Banned;
                _info = default;
                AppendLog("third-party sign-in -> Banned");
                return Failure(AccountLoginResult.Banned, ReasonBanned);
            }

            AccountInfo account = ResolvePlatformAccount(platformToken);
            AdoptSession(account);
            AppendLog("third-party sign-in -> Success(" + account.PlayerId + ")");
            return new AccountLoginOutcome(AccountLoginResult.Success, account, ReasonSignedIn);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <b>绑定 = 合并</b>：成功后当前玩家 id 不变、账号类型升级为
        /// <see cref="AccountKind.ThirdParty"/>，且该 token 被登记为映射到当前号
        /// （之后用同一 token 重新登录拿回的就是这个号，而不是新建 <c>tp-N</c>）。
        /// <para>
        /// <b>失败不改会话</b>：绑定是附加操作，被拒（未登录 / 空 token / 超长 / 平台号已被别的账号占用 /
        /// 封禁 token）都不踢人、不动 TTL。
        /// </para>
        /// </remarks>
        public bool TryBindThirdParty(string platformToken, out string reason)
        {
            if (!_available)
            {
                reason = ReasonUnavailable;
                AppendLog("bind -> " + reason);
                return false;
            }

            if (_state != AccountState.SignedIn)
            {
                reason = ReasonNotSignedIn;
                AppendLog("bind -> " + reason);
                return false;
            }

            if (string.IsNullOrEmpty(platformToken))
            {
                reason = ReasonEmptyToken;
                AppendLog("bind -> " + reason);
                return false;
            }

            if (platformToken.Length > MaxPlatformTokenLength)
            {
                reason = ReasonTokenTooLong;
                AppendLog("bind -> " + reason);
                return false;
            }

            if (string.Equals(platformToken, BannedToken, StringComparison.Ordinal))
            {
                reason = ReasonBanned;
                AppendLog("bind -> " + reason);
                return false;
            }

            AccountInfo existing;
            if (_platformAccounts.TryGetValue(platformToken, out existing))
            {
                // 已经是同一个号 → 幂等性由调用方判定，这里如实报"已绑定"；
                // 属于别的号 → 账号劫持，必须拒绝（真实后端同样拒绝）。
                reason = string.Equals(existing.PlayerId, _info.PlayerId, StringComparison.Ordinal)
                    ? ReasonAlreadyBound
                    : ReasonPlatformClaimed;
                AppendLog("bind -> " + reason + "(" + existing.PlayerId + ")");
                return false;
            }

            AccountInfo bound = new AccountInfo(
                _info.PlayerId, AccountKind.ThirdParty, _info.DisplayName, _info.CreatedAtUnixSeconds);
            _info = bound;
            _platformAccounts[platformToken] = bound;

            reason = ReasonBound;
            AppendLog("bind -> Success(" + bound.PlayerId + ")");
            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 只清本地登录态；<b>账号本身不销毁</b>（游客号与平台映射都保留），
        /// 因此登出再登录拿回的是同一个号。重复调用无副作用。
        /// </remarks>
        public void SignOut()
        {
            bool wasSignedIn = _state != AccountState.SignedOut;
            _state = AccountState.SignedOut;
            _info = default;

            if (wasSignedIn)
            {
                AppendLog("sign-out -> SignedOut");
            }
        }

        /// <inheritdoc />
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

        /// <summary>把后端切成"不可用"（模拟断网 / SDK 未接线）。已建立的会话不受影响。</summary>
        public void SetUnavailable()
        {
            if (_available)
            {
                _available = false;
                AppendLog("available -> false");
            }
        }

        /// <summary>把后端切回"可用"（模拟网络恢复 / SDK 初始化完成）。</summary>
        public void SetAvailable()
        {
            if (!_available)
            {
                _available = true;
                AppendLog("available -> true");
            }
        }

        /// <summary>
        /// 让当前会话过期（模拟服务端把会话踢掉）。只在已登录时生效；过期后
        /// <see cref="TryGetAccount"/> 为 <c>false</c>，重新 <see cref="SignInAsGuest"/> 会拿回同一个游客号。
        /// </summary>
        public void ExpireSession()
        {
            if (_state != AccountState.SignedIn)
            {
                AppendLog("expire -> ignored(" + _state + ")");
                return;
            }

            _state = AccountState.Expired;
            _info = default;
            AppendLog("expire -> Expired");
        }

        /// <summary>取走并清空登录日志（冒烟/排障用；取走即清空，避免同一批事件被重复打印）。</summary>
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

        /// <summary>创建（并分配）设备游客号。</summary>
        /// <returns>新的游客账号。</returns>
        private AccountInfo CreateGuestAccount()
        {
            _guestSequence++;
            string playerId = GuestIdPrefix + _guestSequence.ToString(CultureInfo.InvariantCulture);
            return new AccountInfo(
                playerId,
                AccountKind.Guest,
                "Guest-" + _guestSequence.ToString(CultureInfo.InvariantCulture),
                NowUnixSeconds());
        }

        /// <summary>解析平台 token 对应的账号：命中映射就复用，否则新建 <c>tp-N</c> 并登记映射。</summary>
        /// <param name="platformToken">平台凭据（调用方已校验非空、未封禁）。</param>
        /// <returns>该平台身份对应的游戏账号。</returns>
        private AccountInfo ResolvePlatformAccount(string platformToken)
        {
            AccountInfo existing;
            if (_platformAccounts.TryGetValue(platformToken, out existing))
            {
                return existing;
            }

            _platformSequence++;
            string playerId = PlatformIdPrefix + _platformSequence.ToString(CultureInfo.InvariantCulture);
            var account = new AccountInfo(
                playerId,
                AccountKind.ThirdParty,
                "ThirdParty-" + _platformSequence.ToString(CultureInfo.InvariantCulture),
                NowUnixSeconds());

            _platformAccounts[platformToken] = account;
            return account;
        }

        /// <summary>进入登录态（状态 + 账号 + 日志由调用方负责）。</summary>
        /// <param name="account">要采纳的账号。</param>
        private void AdoptSession(in AccountInfo account)
        {
            _state = AccountState.SignedIn;
            _info = account;
        }

        /// <summary>当前 UTC 时间的 Unix 秒；无时间源或时间源违约时返回 0（创建时间不参与任何判定）。</summary>
        /// <returns>Unix 秒。</returns>
        private long NowUnixSeconds()
        {
            ITimeProvider? time = _time;
            if (time == null)
            {
                return 0L;
            }

            try
            {
                return ToUnixSeconds(time.UtcNow);
            }
            catch (Exception)
            {
                return 0L;
            }
        }

        /// <summary>UTC 时间 → Unix 秒（不用 <c>DateTimeOffset</c>：不依赖 Kind/区域设置，跨端结果一致）。</summary>
        /// <param name="utcNow">UTC 时间。</param>
        /// <returns>Unix 秒。</returns>
        private static long ToUnixSeconds(DateTime utcNow)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(utcNow - epoch).TotalSeconds;
        }

        /// <summary>构造一个失败结果（账号快照为 <c>default</c>）。</summary>
        /// <param name="result">失败结果码。</param>
        /// <param name="reason">诊断原因。</param>
        /// <returns>失败结果。</returns>
        private static AccountLoginOutcome Failure(AccountLoginResult result, string reason)
        {
            return new AccountLoginOutcome(result, default, reason);
        }

        /// <summary>写入一条日志；满员时覆盖最旧的一条（仅诊断路径，允许分配）。</summary>
        /// <param name="entry">日志文本。</param>
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
