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
    /// Unity 侧推送服务：<b>不联网、不接任何推送 SDK、不调用任何 Notification API</b>，
    /// 用本地内存 + 可选落盘模拟一个推送后端（权限三态 / token 登记与持久化 / 排期与覆盖 / 取消 / 降级）。
    /// <para>
    /// <b>为什么这里没有真实的本地通知与远端 token</b>：真实推送必须由平台授权、由系统调度器排期、
    /// 由 APNs/FCM 下发 token，一旦写进适配层，无头测试与断网演示就都用不上这份代码。
    /// 因此 Phase 1 的边界是：本类只模拟<b>推送语义</b>（权限、token 校验、排期覆盖、一次性/重复、降级），
    /// 真实 SDK 由后续独立实现替换——切过去时 <see cref="IPushService"/> 的调用方
    /// （<see cref="PushScheduler"/> 与 UI）一行都不用改。
    /// </para>
    /// <para>
    /// <b><see cref="IsAvailable"/> 默认 <c>false</c></b>：没接线的后端必须如实报不可用，
    /// 让上层走"推送入口置灰、游戏照常玩"的降级路径，而不是在真机上"假装排上了"然后玩家什么都没收到。
    /// 演示/验收时用 <c>new UnityPushService(storage, time, available: true)</c> 打开本地模拟。
    /// </para>
    /// <para>
    /// <b>真机接入点（替换本类时要接的东西，按平台列全）</b>：
    /// <list type="bullet">
    /// <item><b>iOS 授权</b>：<c>Unity.Notifications.iOS.iOSNotificationCenter.RequestAuthorization</c>
    /// （<c>AuthorizationOption.Alert | Badge | Sound</c>）；拿到结果后写入 <see cref="PermissionState"/>，
    /// <c>Denied</c> 时只能引导玩家去"设置 → 通知"手动打开（系统只会弹一次）。</item>
    /// <item><b>iOS 本地排期</b>：<c>iOSNotificationCenter.ScheduleNotification</c> +
    /// <c>iOSNotificationTrigger</c>（<c>repeats: true</c> 时由系统按日历重复，见下方"时区"限制）。</item>
    /// <item><b>iOS 远端 token</b>：开启 Push Notifications capability 后由
    /// <c>didRegisterForRemoteNotificationsWithDeviceToken</c> 回传，需转成 hex；本端口只接收最终字符串。</item>
    /// <item><b>Android 授权</b>：<c>Unity.Notifications.Android.AndroidNotificationCenter</c> 初始化 +
    /// <c>RequestNotificationPermissionAsync</c>（Android 13+ 需要 <c>POST_NOTIFICATIONS</c>）。</item>
    /// <item><b>Android 本地排期</b>：<c>RegisterNotificationChannel</c> 建通道 →
    /// <c>AndroidNotificationCenter.SendNotification</c>（重复同样交给系统）。</item>
    /// <item><b>Android 远端 token</b>：<c>Firebase.Messaging.FirebaseMessaging.TokenReceived</c>
    /// （失败走 <c>TokenRegistrationError</c>），结果喂给 <see cref="RegisterToken"/>。</item>
    /// <item><b>落盘</b>：真机路径 <c>Application.persistentDataPath</c>，由宿主传入
    /// <see cref="IStoragePort"/>（建议独立子目录实例，例如 <c>new UnityStoragePort("push")</c>）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>时区与夏令时（已知限制）</b>：本类排期用的是<b>绝对时刻</b>（Unix 秒）。
    /// 玩家跨时区后提醒仍按原绝对时刻触发，不会自动变成"新时区的 09:00"；
    /// 真机上若要"每天本地 09:00"，必须使用平台的日历重复触发器（iOS <c>UNCalendarNotificationTrigger</c> /
    /// Android 每日重复），那需要把"小时/分钟"而不是"绝对时刻"交给平台——本端口的契约没有这个字段，
    /// 属于后续真机适配时要补的语义，不是本阶段的遗漏。
    /// </para>
    /// <para>
    /// <b>落盘 = "重启后不用重新登记 token"</b>：注入 <see cref="IStoragePort"/> 后，登记成功会写
    /// <see cref="TokenStorageKey"/>（<c>push-token.txt</c>，UTF-8 字节），构造期读回并<b>重新校验</b>；
    /// 读回的内容形状不合法（被截断/改坏/旧版本格式）时只忽略不删除——删除是不可逆操作，
    /// 而下一次成功登记会直接覆盖它。
    /// </para>
    /// <para>
    /// <b>不使用 UnityEngine</b>：本类只依赖 <c>Contracts</c> 的端口与 <c>System</c>，
    /// 因此可以在没有 Unity 的环境里做形状级编译校验（见 <c>unity/tools/check-adapter-compile.ps1</c>）。
    /// </para>
    /// <para>
    /// <b>永不抛异常</b>：落盘失败（磁盘满/权限）、token 脏数据、时间源违约都被收敛成
    /// "本次没写进/没读到"的降级结果——IO 异常绝不允许打断一次普通的登录或设置页点击。
    /// </para>
    /// <para>线程模型：非线程安全。排期/登记/取消都在 Unity 主线程串行调用。</para>
    /// </summary>
    public sealed class UnityPushService : IPushService
    {
        /// <summary>日志环形容量（最近 32 条，诊断用）。</summary>
        public const int LogCapacity = 32;

        /// <summary>token 落盘键（契约指定；<see cref="IStoragePort"/> 的键是逻辑键，落盘命名由实现决定）。</summary>
        public const string TokenStorageKey = "push-token.txt";

        /// <summary>失败原因：未启用本地模拟（<c>available: false</c>）。</summary>
        public const string ReasonUnavailable = "push service unavailable";

        /// <summary>失败原因：通知权限未授予（Unknown 或 Denied）。</summary>
        public const string ReasonPermissionDenied = "notification permission not granted";

        /// <summary>失败原因：token 形状非法（空/超长/非可打印 ASCII）。</summary>
        public const string ReasonInvalidToken = "invalid token";

        /// <summary>可选存档端口；<c>null</c> = 不落盘（杀进程后需要重新登记 token）。</summary>
        private readonly IStoragePort? _storage;

        /// <summary>可选时间源（只用于把"立即"落成具体时刻；<c>null</c> 时保持 0）。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>是否启用本地模拟（构造后不可变，见类注释）。</summary>
        private readonly bool _available;

        /// <summary>权限状态（构造后为 <see cref="PushPermissionState.Unknown"/>）。</summary>
        private PushPermissionState _permission;

        /// <summary>已登记 token（空串 = 未登记；永远不为 <c>null</c>）。</summary>
        private string _token = string.Empty;

        /// <summary>本地计划表（id 唯一；读取时按触发时间排序）。</summary>
        private readonly Dictionary<int, PushNotification> _scheduled = new Dictionary<int, PushNotification>();

        /// <summary>日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置。</summary>
        private int _logNext;

        /// <summary>创建 Unity 推送服务。</summary>
        /// <param name="storage">
        /// 可选存档端口（建议传独立子目录实例，例如 <c>new UnityStoragePort("push")</c>；
        /// <c>null</c> = token 只留在内存里）。
        /// </param>
        /// <param name="time">可选时间源（把"立即"落成当前时刻；<c>null</c> 时保持 0 并排在计划表最前）。</param>
        /// <param name="available">是否启用本地模拟；默认 <c>false</c>（未接线必须如实报不可用）。</param>
        public UnityPushService(
            IStoragePort? storage = null,
            ITimeProvider? time = null,
            bool available = false)
        {
            _storage = storage;
            _time = time;
            _available = available;
            _permission = PushPermissionState.Unknown;

            // 构造期就把 token 读回来：它是本地事实，不需要后端可用；否则每次冷启动都要重新登记一次，
            // 而"重新登记"在真机上意味着一次授权往返与一次网络请求。
            LoadToken();
        }

        /// <inheritdoc />
        /// <remarks>只有"被显式启用"时才为 <c>true</c>：没接线的后端必须如实报告不可用。</remarks>
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 本地模拟不查询系统设置：构造后是 <see cref="PushPermissionState.Unknown"/>，
        /// 只能被 <see cref="RequestPermission"/> 与 <see cref="ExpirePermission"/> 改变。
        /// 真机实现必须在每次读取时向平台查询（玩家可以随时在系统设置里关掉通知）。
        /// </remarks>
        public PushPermissionState PermissionState
        {
            get { return _permission; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 可用 → <see cref="PushPermissionState.Granted"/>（本地模拟直接授权，不弹系统框）；
        /// 不可用 → <see cref="PushPermissionState.Denied"/>（请求发出去了却没被授权）。
        /// </remarks>
        public PushPermissionState RequestPermission()
        {
            if (!_available)
            {
                _permission = PushPermissionState.Denied;
                AppendLog("permission -> Denied (simulation disabled)");
                return _permission;
            }

            _permission = PushPermissionState.Granted;
            AppendLog("permission -> Granted");
            return _permission;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 校验顺序：token 形状（<see cref="PushScheduler.IsValidToken"/>）→ 可用性 → 权限 → 落盘。
        /// <b>落盘失败不影响结果</b>：token 已经在本会话生效，只是重启后需要重新登记（见类注释的降级约定）。
        /// </remarks>
        public PushRegisterResult RegisterToken(string token)
        {
            if (!PushScheduler.IsValidToken(token))
            {
                AppendLog("register-token(len=" + DescribeLength(token) + ") -> InvalidToken");
                return PushRegisterResult.InvalidToken;
            }

            if (!_available)
            {
                AppendLog("register-token(len=" + DescribeLength(token) + ") -> Unavailable");
                return PushRegisterResult.Unavailable;
            }

            if (_permission != PushPermissionState.Granted)
            {
                AppendLog("register-token(len=" + DescribeLength(token) + ") -> PermissionDenied");
                return PushRegisterResult.PermissionDenied;
            }

            _token = token;
            SaveToken(token);
            AppendLog("register-token(len=" + DescribeLength(token) + ") -> Success");
            return PushRegisterResult.Success;
        }

        /// <inheritdoc />
        /// <remarks>无论可用性与权限如何都清空内存与落盘 token：登出必须能清掉凭据，否则下一个账号会继承它。</remarks>
        public void UnregisterToken()
        {
            bool had = _token.Length > 0;
            _token = string.Empty;

            if (had)
            {
                DeleteToken();
                AppendLog("unregister-token -> cleared");
            }
        }

        /// <inheritdoc />
        /// <remarks>未登记返回<b>空串</b>（不是 <c>null</c>），见契约文档。</remarks>
        public string? RegisteredToken
        {
            get { return _token; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 同 id 覆盖旧计划。未启用模拟时返回 <c>false</c> 且不改计划表。
        /// <b>不校验权限</b>：本地通知的排期接口在未授权时通常照样接受，只是到点不显示（见类注释的不对称说明）。
        /// </remarks>
        public bool Schedule(in PushNotificationRequest request)
        {
            if (!_available)
            {
                AppendLog("schedule #" + request.NotificationId.ToString(CultureInfo.InvariantCulture) + " -> false (unavailable)");
                return false;
            }

            long fireAt = NormalizeFireAt(request.FireAtUnixSeconds);
            int repeat = request.RepeatIntervalSeconds > 0 ? request.RepeatIntervalSeconds : 0;
            var notification = new PushNotification(
                request.NotificationId,
                request.Title,
                request.Body,
                fireAt,
                repeat > 0);

            bool replaced = _scheduled.ContainsKey(request.NotificationId);
            _scheduled[request.NotificationId] = notification;

            AppendLog("schedule #" + request.NotificationId.ToString(CultureInfo.InvariantCulture)
                + " at " + fireAt.ToString(CultureInfo.InvariantCulture)
                + " repeat=" + repeat.ToString(CultureInfo.InvariantCulture)
                + (replaced ? " (replaced)" : string.Empty));

            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <c>false</c> = 计划里本来就没有（合法结果）。<b>不受可用性影响</b>：取消方向 fail-safe，
        /// 否则会留下谁也取消不掉的幽灵提醒。
        /// </remarks>
        public bool Cancel(int notificationId)
        {
            bool removed = _scheduled.Remove(notificationId);
            AppendLog("cancel #" + notificationId.ToString(CultureInfo.InvariantCulture) + " -> " + removed.ToString());

            return removed;
        }

        /// <inheritdoc />
        /// <remarks>清空整张计划表（不受可用性影响，理由同 <see cref="Cancel"/>）。</remarks>
        public void CancelAll()
        {
            int count = _scheduled.Count;
            _scheduled.Clear();
            AppendLog("cancel-all -> " + count.ToString(CultureInfo.InvariantCulture));
        }

        /// <inheritdoc />
        /// <remarks>
        /// 按触发时间升序、同一时刻按 id 升序返回<b>新数组</b>（调用方排序/改字段不影响内部计划）。
        /// 未启用模拟时返回空数组，与"不可用时什么都排不上"保持一致。
        /// </remarks>
        public PushNotification[] GetScheduled()
        {
            if (!_available || _scheduled.Count == 0)
            {
                return Array.Empty<PushNotification>();
            }

            var list = new List<PushNotification>(_scheduled.Values);
            list.Sort(CompareByFireTime);
            return list.ToArray();
        }

        /// <summary>
        /// 让权限状态"过期"：回到 <see cref="PushPermissionState.Unknown"/>（模拟首次安装 / 系统撤销授权）。
        /// <para>真机上玩家随时可能在系统设置里关掉通知，本方法让那条路径在无头环境里也可复现。</para>
        /// </summary>
        public void ExpirePermission()
        {
            _permission = PushPermissionState.Unknown;
            AppendLog("permission expired (forced) -> Unknown");
        }

        /// <summary>取走并清空日志（冒烟/排障用；取走即清空，避免同一批事件被重复打印）。</summary>
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

        /// <summary>
        /// 构造期读回落盘 token 并<b>重新校验</b>。
        /// <para>
        /// 读失败/无存档 → 视为未登记（不报错，首次安装就是这条路）。
        /// 读到但形状不合法 → 同样视为未登记，<b>但不删除</b>（删除不可逆，下次成功登记会覆盖）。
        /// </para>
        /// </summary>
        private void LoadToken()
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            string? loaded;
            try
            {
                byte[]? bytes = storage.Read(TokenStorageKey);
                if (bytes == null || bytes.Length == 0)
                {
                    return;
                }

                loaded = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                AppendLog("load token failed -> treated as unregistered");
                return;
            }

            if (!PushScheduler.IsValidToken(loaded))
            {
                AppendLog("stored token rejected(len=" + DescribeLength(loaded) + ")");
                return;
            }

            _token = loaded!;
            AppendLog("token restored(len=" + DescribeLength(loaded) + ")");
        }

        /// <summary>把 token 写进存档；失败只影响"重启后免登记"，本次会话已经生效。</summary>
        /// <param name="token">已校验通过的 token。</param>
        private void SaveToken(string token)
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            try
            {
                storage.Write(TokenStorageKey, Encoding.UTF8.GetBytes(token));
            }
            catch (Exception)
            {
                AppendLog("save token failed -> memory only");
            }
        }

        /// <summary>删除落盘 token；失败不重试（内存已经清空，凭据不会在本进程内继续存在）。</summary>
        private void DeleteToken()
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            try
            {
                storage.Delete(TokenStorageKey);
            }
            catch (Exception)
            {
                AppendLog("delete token failed -> stale file left behind");
            }
        }

        /// <summary>
        /// 把"立即"（<c>&lt;= 0</c>）落成当前时刻；正数原样返回。
        /// <para>无时钟或时钟违约时保持 0：0 仍表达"立即"，且在排序里稳定地排在最前。</para>
        /// </summary>
        /// <param name="fireAtUnixSeconds">请求里的触发时间。</param>
        /// <returns>计划表里要记的触发时间。</returns>
        private long NormalizeFireAt(long fireAtUnixSeconds)
        {
            if (fireAtUnixSeconds > 0)
            {
                return fireAtUnixSeconds;
            }

            return NowUnixSeconds();
        }

        /// <summary>当前 UTC 时间的 Unix 秒；无时钟/违约/早于 1970 时返回 0。</summary>
        /// <returns>Unix 秒（≥ 0）。</returns>
        private long NowUnixSeconds()
        {
            ITimeProvider? time = _time;
            if (time == null)
            {
                return 0;
            }

            try
            {
                long seconds = (long)(time.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                return seconds < 0 ? 0 : seconds;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>计划表排序：触发时间升序，同一时刻按 id 升序（顺序稳定可断言）。</summary>
        /// <param name="left">左侧通知。</param>
        /// <param name="right">右侧通知。</param>
        /// <returns>比较结果。</returns>
        private static int CompareByFireTime(PushNotification left, PushNotification right)
        {
            int byTime = left.FireAtUnixSeconds.CompareTo(right.FireAtUnixSeconds);
            return byTime != 0 ? byTime : left.NotificationId.CompareTo(right.NotificationId);
        }

        /// <summary>日志里描述 token 的<b>长度</b>而不是内容（token 是凭据，写进日志等于泄露）。</summary>
        /// <param name="token">token（可为 <c>null</c>）。</param>
        /// <returns>长度文本；<c>null</c> 记作 <c>&lt;null&gt;</c>。</returns>
        private static string DescribeLength(string? token)
        {
            if (token == null)
            {
                return "<null>";
            }

            return token.Length.ToString(CultureInfo.InvariantCulture);
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
