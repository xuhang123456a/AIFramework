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
    /// 控制台推送服务：<b>内存假推送后端</b>，用来在无头环境验收 <see cref="IPushService"/> 的全部语义
    /// （权限三态 / token 登记校验 / 排期与覆盖 / 取消 / 计划排序 / 不可用降级）。
    /// <para>
    /// <b>它不是空实现</b>：它维护一份"平台侧计划表"（id → 通知快照），并完整执行与真机同一套 token 校验规则
    /// （复用 <see cref="PushScheduler.IsValidToken"/>，不另写一份）。真实实现（Unity Mobile Notifications /
    /// APNs / FCM）只是把这份表换成平台的调度器、把内存字符串换成远端下发的 token，语义必须与这里完全一致——
    /// 否则无头测试绿、真机上"改时间后收到两条"或"token 脏了却报成功"就变成直接事故。
    /// </para>
    /// <para>
    /// <b>权限与 token 的顺序与真机一致</b>：构造后 <see cref="PermissionState"/> 是
    /// <see cref="PushPermissionState.Unknown"/>，必须先 <see cref="RequestPermission"/>；
    /// 未授权时 <see cref="RegisterToken"/> 如实返回 <see cref="PushRegisterResult.PermissionDenied"/>。
    /// 假实现<b>不会</b>替你自动授权——那会让"忘了申请权限"这个真机上最典型的 bug 在无头测试里永远看不见。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="Schedule"/> 不校验权限，而 <see cref="RegisterToken"/> 校验</b>：这是真机上的真实差别。
    /// 本地通知的<b>排期</b>接口（iOS <c>UNUserNotificationCenter.AddNotificationRequest</c> /
    /// Android <c>NotificationManagerCompat.Notify</c>）在未授权时通常照样接受，只是到点不显示；
    /// 而<b>远端 token</b> 在没有授权时根本不会下发，登记必然失败。把这条不对称如实写进假实现，
    /// 才能让"排期成功但没弹"这种真机现象在无头环境里也能被复现和讨论。
    /// </para>
    /// <para>
    /// <b>日志是排空的环形缓冲</b>：只服务冒烟与排障（<see cref="DrainLog"/> 取走即清空），不参与业务判定，
    /// 因此允许在这里拼字符串（正式逻辑路径禁止动态拼接）。<b>日志里只记 token 长度，绝不记 token 本身</b>：
    /// token 是"能冒充本设备收推送"的凭据，写进日志等于泄露。
    /// </para>
    /// <para>线程模型：非线程安全。排期/登记/取消都在主线程串行调用（推送不是高频路径）。</para>
    /// </summary>
    public sealed class ConsolePushService : IPushService
    {
        /// <summary>日志环形容量（最近 32 条）。</summary>
        public const int LogCapacity = 32;

        /// <summary>失败原因：构造/<see cref="SetUnavailable"/> 配置为不可用。</summary>
        public const string ReasonUnavailable = "push service unavailable";

        /// <summary>失败原因：通知权限未授予（Unknown 或 Denied）。</summary>
        public const string ReasonPermissionDenied = "notification permission not granted";

        /// <summary>失败原因：token 形状非法（空/超长/非可打印 ASCII）。</summary>
        public const string ReasonInvalidToken = "invalid token";

        /// <summary>成功原因：token 已登记。</summary>
        public const string ReasonRegistered = "token registered";

        /// <summary>日志前缀：排期。</summary>
        private const string LogSchedule = "schedule ";

        /// <summary>可选时间源（<c>null</c> = "立即"通知保持 0，排在最前）。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>是否可用（可被 <see cref="SetUnavailable"/> / <see cref="SetAvailable"/> 拨动）。</summary>
        private bool _available;

        /// <summary>权限状态（构造后为 <see cref="PushPermissionState.Unknown"/>）。</summary>
        private PushPermissionState _permission;

        /// <summary>已登记 token（空串 = 未登记；永远不为 <c>null</c>）。</summary>
        private string _token = string.Empty;

        /// <summary>平台侧计划表（id 唯一；读取时按触发时间排序）。</summary>
        private readonly Dictionary<int, PushNotification> _scheduled = new Dictionary<int, PushNotification>();

        /// <summary>日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置；满员时指向最旧的一条。</summary>
        private int _logNext;

        /// <summary>创建控制台推送服务。</summary>
        /// <param name="time">
        /// 可选时间源（只用于把"立即"（<c>FireAtUnixSeconds &lt;= 0</c>）落成具体时刻；
        /// <c>null</c> 时保持 0，仍排在计划表最前）。
        /// </param>
        /// <param name="available">是否可用（默认 <c>true</c>；验收离线降级时传 <c>false</c> 或用 <see cref="SetUnavailable"/>）。</param>
        public ConsolePushService(ITimeProvider? time = null, bool available = true)
        {
            _time = time;
            _available = available;
            _permission = PushPermissionState.Unknown;
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <inheritdoc />
        /// <remarks>构造后是 <see cref="PushPermissionState.Unknown"/>（没问过），只能被 <see cref="RequestPermission"/> 与 <see cref="ExpirePermission"/> 改变。</remarks>
        public PushPermissionState PermissionState
        {
            get { return _permission; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 可用 → <see cref="PushPermissionState.Granted"/>（假实现直接授权，不弹任何系统框）；
        /// 不可用 → <see cref="PushPermissionState.Denied"/>（请求发出去了却没被授权，停在 Unknown 会让 UI 反复重试）。
        /// </remarks>
        public PushPermissionState RequestPermission()
        {
            if (!_available)
            {
                _permission = PushPermissionState.Denied;
                AppendLog("permission -> Denied (service unavailable)");
                return _permission;
            }

            _permission = PushPermissionState.Granted;
            AppendLog("permission -> Granted");
            return _permission;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 校验顺序：token 形状（<see cref="PushScheduler.IsValidToken"/>）→ 可用性 → 权限。
        /// 脏 token 先被拒，因为它换一条就能修好，不该被"后端不可用"掩盖成一次可重试的失败。
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
            AppendLog("register-token(len=" + DescribeLength(token) + ") -> Success");
            return PushRegisterResult.Success;
        }

        /// <inheritdoc />
        /// <remarks>无论可用性与权限如何都清空本地 token：登出必须能清掉凭据，否则下一个账号会继承它。</remarks>
        public void UnregisterToken()
        {
            bool had = _token.Length > 0;
            _token = string.Empty;

            if (had)
            {
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
        /// 同 id 覆盖旧计划。不可用返回 <c>false</c> 且不改计划表。
        /// <c>FireAtUnixSeconds &lt;= 0</c>（立即）在有时钟时落成当前时刻，让
        /// <see cref="GetScheduled"/> 的顺序与"实际先后"一致；无时钟时保持 0（仍排最前）。
        /// </remarks>
        public bool Schedule(in PushNotificationRequest request)
        {
            if (!_available)
            {
                AppendLog(LogSchedule + DescribeId(request.NotificationId) + " -> false (unavailable)");
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

            AppendLog(LogSchedule + DescribeId(request.NotificationId)
                + " at " + fireAt.ToString(CultureInfo.InvariantCulture)
                + " repeat=" + repeat.ToString(CultureInfo.InvariantCulture)
                + (replaced ? " (replaced)" : string.Empty));

            return true;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <c>false</c> = 计划里本来就没有（合法结果）。<b>不受可用性影响</b>：取消方向 fail-safe，
        /// 后端掉线时仍然要把本地计划清掉，否则会留下谁也取消不掉的幽灵提醒。
        /// </remarks>
        public bool Cancel(int notificationId)
        {
            bool removed = _scheduled.Remove(notificationId);
            AppendLog("cancel " + DescribeId(notificationId) + " -> " + removed.ToString());

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
        /// 不可用时返回空数组，与"不可用时什么都排不上"保持一致。
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

        /// <summary>把后端切成"不可用"（模拟断网 / SDK 未接线）。已排的计划不删除。</summary>
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
        /// 让权限状态"过期"：回到 <see cref="PushPermissionState.Unknown"/>（模拟首次安装、
        /// 玩家在系统设置里撤销授权、或系统重置了授权记录）。
        /// <para>
        /// 与"按时间自动过期"无关（权限没有自然过期这回事）：它只是一个可拨动的开关，
        /// 让"撤销后重新申请"这条路径能被无头测试覆盖，不必真去改系统设置。
        /// </para>
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
        /// 把"立即"（<c>&lt;= 0</c>）落成当前时刻；正数原样返回。
        /// <para>无时钟或时钟违约时保持 0：0 仍表达"立即"，且在排序里稳定地排在最前，不需要猜一个时间。</para>
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

        /// <summary>日志里描述一个通知 id。</summary>
        /// <param name="notificationId">通知 id。</param>
        /// <returns>可读描述（<c>#7</c>）。</returns>
        private static string DescribeId(int notificationId)
        {
            return "#" + notificationId.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 日志里描述 token 的<b>长度</b>而不是内容（token 是凭据，写进日志等于泄露）。
        /// </summary>
        /// <param name="token">token（可为 <c>null</c>）。</param>
        /// <returns>长度文本；<c>null</c> 记作 <c>&lt;null&gt;</c>，便于区分"UI 传了 null"与"传了空串"。</returns>
        private static string DescribeLength(string token)
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
