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
    /// Unity 侧账号服务：<b>不联网、不接任何平台 SDK</b>，用本地内存 + 可选落盘模拟一个账号后端。
    /// <para>
    /// <b>为什么这里没有 Apple/Google 登录</b>：真机第三方登录必须由平台 SDK 弹窗/静默取票、
    /// 后端换票、再带反作弊校验，一旦写进适配层，无头测试与断网演示就都用不上这份代码。
    /// 因此 Phase 1 的边界是：本类只模拟<b>账号语义</b>（游客号本地持久化、第三方 token 映射、
    /// 绑定=合并、登出保号），真实 SDK 由后续独立实现替换——切过去时 <see cref="IAccountService"/>
    /// 的调用方一行都不用改。
    /// </para>
    /// <para>
    /// <b><see cref="IsAvailable"/> 默认 <c>false</c></b>：没接线的后端必须如实报不可用，
    /// 让上层走"离线可玩"的降级路径，而不是在真机上"假装登录成功"然后写出一份无主存档。
    /// 演示/验收时用 <c>new UnityAccountService(storage: ..., available: true)</c> 打开本地模拟。
    /// </para>
    /// <para>
    /// <b>游客号落盘 = 换机前唯一的身份锚点</b>：注入 <see cref="IStoragePort"/> 后，
    /// 玩家 id 写进 <see cref="DefaultPlayerSlotKey"/>（UTF-8 文本 <c>playerId|displayName|createdAt</c>），
    /// 重启后同一个号，<b>卸载即丢</b>——这正是"游客号不可找回"的真实语义，
    /// 也是 UI 必须提示"绑定账号以保存进度"的原因。
    /// </para>
    /// <para>
    /// <b>不使用 UnityEngine</b>：本类只依赖 <c>Contracts</c> 的端口与 <c>System</c>，
    /// 因此可以在没有 Unity 的环境里做形状级编译校验（见 <c>unity/tools/check-adapter-compile.ps1</c>）。
    /// </para>
    /// <para>
    /// <b>永不抛异常</b>：落盘失败（磁盘满/权限）、存储键非法、时间源违约都被收敛成
    /// "本次拿不到/写不进"的降级结果，登录流程不允许被一次 IO 异常打断。
    /// </para>
    /// <para>线程模型：非线程安全。登录/绑定/登出都在 Unity 主线程串行调用。</para>
    /// </summary>
    public sealed class UnityAccountService : IAccountService
    {
        /// <summary>默认的玩家 id 存档键（<see cref="IStoragePort"/> 逻辑键，只允许 <c>[A-Za-z0-9._-]</c>）。</summary>
        public const string DefaultPlayerSlotKey = "account-player.txt";

        /// <summary>日志环形容量（最近 32 条，诊断用）。</summary>
        public const int LogCapacity = 32;

        /// <summary>平台 token 上限，与 <see cref="AccountSessionManager.MaxPlatformTokenLength"/> 同一口径。</summary>
        public const int MaxPlatformTokenLength = AccountSessionManager.MaxPlatformTokenLength;

        /// <summary>游客玩家 id 前缀。</summary>
        public const string GuestIdPrefix = "guest-";

        /// <summary>第三方玩家 id 前缀。</summary>
        public const string PlatformIdPrefix = "tp-";

        /// <summary>触发"封禁"分支的特殊 token（本地模拟用；真机上它来自服务端裁决）。</summary>
        public const string BannedToken = "banned";

        /// <summary>失败原因：未启用本地模拟（<c>available: false</c>）。</summary>
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

        /// <summary>失败原因：该平台账号已被别的游戏账号占用。</summary>
        public const string ReasonPlatformClaimed = "platform account claimed";

        /// <summary>成功原因：本次建立了新会话。</summary>
        public const string ReasonSignedIn = "signed in";

        /// <summary>成功原因：绑定成功（账号类型升级为第三方，玩家 id 不变）。</summary>
        public const string ReasonBound = "bound";

        /// <summary>可选存档端口；<c>null</c> = 不落盘（重启即换游客号，等价于"没绑定的游客号丢了"）。</summary>
        private readonly IStoragePort? _storage;

        /// <summary>可选时间源（只用于账号创建时间；<c>null</c> 时记 0）。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>玩家 id 存档键（默认 <see cref="DefaultPlayerSlotKey"/>）。</summary>
        private readonly string _playerSlotKey;

        /// <summary>是否启用本地模拟（构造后不可变）。</summary>
        private readonly bool _available;

        /// <summary>游客账号（设备级，只创建一次）；<c>null</c> = 还没登录过。</summary>
        private AccountInfo? _guestAccount;

        /// <summary>平台账号映射：token → 该平台身份对应的游戏账号（绑定后指向被合并的玩家号）。</summary>
        private readonly Dictionary<string, AccountInfo> _platformAccounts =
            new Dictionary<string, AccountInfo>(StringComparer.Ordinal);

        /// <summary>当前会话状态。</summary>
        private AccountState _state;

        /// <summary>当前账号快照；<b>不变量</b>：非 <see cref="AccountState.SignedIn"/> 时恒为 <c>default</c>。</summary>
        private AccountInfo _info;

        /// <summary>游客号分配序号（本进程内递增；落盘命中时不再分配）。</summary>
        private int _guestSequence;

        /// <summary>第三方号分配序号。</summary>
        private int _platformSequence;

        /// <summary>加载存档时被跳过的脏记录数（诊断用）。</summary>
        private int _dirtyRecords;

        /// <summary>登录日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置。</summary>
        private int _logNext;

        /// <summary>创建 Unity 账号服务。</summary>
        /// <param name="storage">可选存档端口（建议传 <c>new UnityStoragePort("account")</c> 之外的宿主共享实例）。</param>
        /// <param name="time">可选时间源（只用于账号创建时间）。</param>
        /// <param name="available">是否启用本地模拟；默认 <c>false</c>（未接线必须如实报不可用）。</param>
        /// <param name="playerSlotKey">玩家 id 存档键（默认 <see cref="DefaultPlayerSlotKey"/>）。</param>
        public UnityAccountService(
            IStoragePort? storage = null,
            ITimeProvider? time = null,
            bool available = false,
            string playerSlotKey = DefaultPlayerSlotKey)
        {
            _storage = storage;
            _time = time;
            _available = available;
            _playerSlotKey = playerSlotKey;

            // 存档里已经有账号时，构造期就把"我是谁"恢复出来：这样宿主建完服务立刻就能读玩家 id 做
            // 存档/排行榜预取，而不用等第一次 EnsureSignedIn（那一步可能因为玩家先看登录页而延后）。
            if (_available)
            {
                RestoreGuestAccount();
            }
        }

        /// <inheritdoc />
        /// <remarks>只有"被显式启用"时才为 <c>true</c>：没接线的后端必须如实报告不可用。</remarks>
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <inheritdoc />
        public AccountState State
        {
            get { return _state; }
        }

        /// <summary>加载时被跳过的脏记录数（诊断用；0 = 存档干净）。</summary>
        public int DirtyRecords
        {
            get { return _dirtyRecords; }
        }

        /// <summary>当前已分配的平台账号数量（诊断用）。</summary>
        public int PlatformAccountCount
        {
            get { return _platformAccounts.Count; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 游客号是设备级的：落盘命中时直接复用（重启后同 id），否则分配 <c>guest-1</c> 并立即落盘。
        /// 已登录时返回 <see cref="AccountLoginResult.AlreadySignedIn"/>（换号请先 <see cref="SignOut"/>）。
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
        /// 同一 token 永远映射到同一个 <c>tp-N</c>（含绑定后指向被合并的玩家号），
        /// 因此"换号再换回来"不会丢档。空/超长 → <see cref="AccountLoginResult.Rejected"/>；
        /// <see cref="BannedToken"/> → <see cref="AccountLoginResult.Banned"/>。
        /// <para><b>没有真实平台鉴权</b>：本实现不做任何签名/票根校验（Phase 1 只校验非空，见契约）。</para>
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
        /// 绑定 = 合并：玩家 id 不变、账号类型升级为 <see cref="AccountKind.ThirdParty"/>，
        /// 该 token 登记为映射到当前号，之后用同一 token 重新登录拿回的就是这个号（不是新建 <c>tp-N</c>）。
        /// 失败（未登录/空/超长/已绑定/被别的号占用/封禁）一律不改会话。
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
            SavePlayer(bound);

            reason = ReasonBound;
            AppendLog("bind -> Success(" + bound.PlayerId + ")");
            return true;
        }

        /// <inheritdoc />
        /// <remarks>只清本地登录态：游客号与平台映射都保留（登出再登录拿回同一个号）。重复调用无副作用。</remarks>
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

        /// <summary>
        /// 让当前会话过期（模拟服务端踢人 / token 失效），供演示与 PlayMode 验收注入。
        /// 只在已登录时生效；过期后重新 <see cref="SignInAsGuest"/> 会拿回同一个游客号。
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

        /// <summary>创建游客号：优先进存档恢复，恢复失败才分配新号。</summary>
        /// <returns>游客账号（已落盘）。</returns>
        private AccountInfo CreateGuestAccount()
        {
            AccountInfo restored;
            if (TryLoadPlayer(out restored))
            {
                _guestSequence = ExtractSequence(restored.PlayerId, GuestIdPrefix, _guestSequence);
                AppendLog("guest restored(" + restored.PlayerId + ")");
                return restored;
            }

            _guestSequence++;
            var account = new AccountInfo(
                GuestIdPrefix + _guestSequence.ToString(CultureInfo.InvariantCulture),
                AccountKind.Guest,
                "Guest-" + _guestSequence.ToString(CultureInfo.InvariantCulture),
                NowUnixSeconds());

            SavePlayer(account);
            return account;
        }

        /// <summary>构造期尝试从存档恢复游客号（只填字段，不改状态：启动时是否登录由上层决定）。</summary>
        private void RestoreGuestAccount()
        {
            AccountInfo restored;
            if (TryLoadPlayer(out restored))
            {
                _guestAccount = restored;
                _guestSequence = ExtractSequence(restored.PlayerId, GuestIdPrefix, _guestSequence);
            }
        }

        /// <summary>解析平台 token 对应的账号：命中映射就复用，否则新建 <c>tp-N</c>。</summary>
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
            string sequence = _platformSequence.ToString(CultureInfo.InvariantCulture);
            var account = new AccountInfo(
                PlatformIdPrefix + sequence,
                AccountKind.ThirdParty,
                "ThirdParty-" + sequence,
                NowUnixSeconds());

            _platformAccounts[platformToken] = account;
            return account;
        }

        /// <summary>进入登录态。</summary>
        /// <param name="account">要采纳的账号。</param>
        private void AdoptSession(in AccountInfo account)
        {
            _state = AccountState.SignedIn;
            _info = account;
        }

        /// <summary>从存档恢复玩家 id：读失败/解析失败/空 id 一律按"没有存档"处理（并计数）。</summary>
        /// <param name="account">恢复出的账号（默认游客类型）。</param>
        /// <returns>是否恢复成功。</returns>
        private bool TryLoadPlayer(out AccountInfo account)
        {
            account = default;

            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return false;
            }

            byte[]? bytes;
            try
            {
                bytes = storage.Read(_playerSlotKey);
            }
            catch (Exception)
            {
                // 读失败等价于"没有存档"：宁可发新游客号继续玩，也不能把启动打断。
                return false;
            }

            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }

            string text = Encoding.UTF8.GetString(bytes).Trim();
            string[] parts = text.Split('|');
            if (parts.Length < 1 || parts[0].Length == 0)
            {
                _dirtyRecords++;
                return false;
            }

            string displayName = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0];

            long createdAtUnixSeconds = 0L;
            if (parts.Length > 2)
            {
                long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out createdAtUnixSeconds);
            }

            account = new AccountInfo(parts[0], AccountKind.Guest, displayName, createdAtUnixSeconds);
            return true;
        }

        /// <summary>把玩家 id 写进存档（写失败只影响"重启后同 id"，不影响本次登录）。</summary>
        /// <param name="account">要落盘的账号。</param>
        private void SavePlayer(in AccountInfo account)
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            string text = account.PlayerId
                + "|" + account.DisplayName
                + "|" + account.CreatedAtUnixSeconds.ToString(CultureInfo.InvariantCulture);

            try
            {
                storage.Write(_playerSlotKey, Encoding.UTF8.GetBytes(text));
            }
            catch (Exception)
            {
                // 落盘失败不打断登录：本次会话照常可用，代价只是重启后可能换号（由 UI 的绑定提示兜住）。
                AppendLog("save player failed(" + _playerSlotKey + ")");
            }
        }

        /// <summary>从 <c>guest-N</c> 里解出序号，避免下次新建时与存档号撞号。</summary>
        /// <param name="playerId">已存在的玩家 id。</param>
        /// <param name="prefix">该 id 的前缀。</param>
        /// <param name="current">当前序号（解析失败时原样返回）。</param>
        /// <returns>新的序号。</returns>
        private static int ExtractSequence(string playerId, string prefix, int current)
        {
            if (!playerId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return current;
            }

            int parsed;
            if (int.TryParse(
                playerId.Substring(prefix.Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out parsed)
                && parsed > current)
            {
                return parsed;
            }

            return current;
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

        /// <summary>UTC 时间 → Unix 秒（不依赖 <c>DateTimeKind</c> 推导，跨平台结果一致）。</summary>
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
