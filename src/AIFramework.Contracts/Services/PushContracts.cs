#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 通知权限的三态：<b>没问过 / 被拒绝 / 已授权</b>。
    /// <para>
    /// <b>为什么不是 <c>bool</c></b>："还没问过"和"玩家明确拒绝"在真机上是两种完全不同的处境——
    /// 前者要弹系统授权框，后者再弹也没用（iOS 一个 App 只允许弹一次，之后系统静默返回拒绝），
    /// 只能引导玩家去系统设置里手动打开。压成 <c>bool</c> 会把 Unknown 归到 <c>false</c>，
    /// 于是每次启动都"再问一次"，玩家看到的却是按钮毫无反应——这是推送功能最常见的假死。
    /// </para>
    /// <para>
    /// <b>底层类型固定 <c>byte</c></b>：状态会进存档/埋点，宽度变化会让老存档读出脏值。
    /// 数值顺序固定，只允许追加，不允许调整既有值。
    /// </para>
    /// </summary>
    public enum PushPermissionState : byte
    {
        /// <summary>尚未询问（首次安装 / 权限被系统重置 / 假实现刚构造）。此时应调用 <see cref="IPushService.RequestPermission"/>。</summary>
        Unknown = 0,

        /// <summary>玩家或系统拒绝了通知权限。再调用 <see cref="IPushService.RequestPermission"/> 通常无效，只能引导去系统设置。</summary>
        Denied = 1,

        /// <summary>已授权：可以登记 token、可以排期本地通知。</summary>
        Granted = 2
    }

    /// <summary>
    /// 一次远端 token 登记的结果码。
    /// <para>
    /// <b>为什么 <see cref="Unavailable"/> 与 <see cref="PermissionDenied"/> 必须分开</b>：
    /// 前者是"现在不行"（SDK 未初始化、断网、后端抖动），稍后重试有意义；
    /// 后者是"这个 App 没拿到授权"，重试一万次也是同一个答案，必须改走引导授权的路径。
    /// 合成一个码，调用方就只能无脑重试或直接放弃，两种都是错的。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="InvalidToken"/> 是结果码而不是异常</b>：token 由远端 SDK/服务端下发，
    /// 脏数据（空串、被截断、带上了 JSON 转义、超过字段上限）是常态而非编程错误。
    /// 把一次"这条 token 不能用"变成崩溃，会把登录流程整条打断；
    /// 而它又必须与"不可用"区分开——脏 token 重试无意义，只能换一条。
    /// </para>
    /// <para>数值顺序固定（会进日志与埋点），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum PushRegisterResult : byte
    {
        /// <summary>登记成功（或已经登记过同一条 token）。</summary>
        Success = 0,

        /// <summary>推送后端未接线 / 未初始化 / 当前不可用：可降级继续玩，稍后可重试。</summary>
        Unavailable = 1,

        /// <summary>通知权限未授予（<see cref="PushPermissionState.Unknown"/> 或 <see cref="PushPermissionState.Denied"/>）：重试无意义。</summary>
        PermissionDenied = 2,

        /// <summary>token 本身不可用（空/超长/含非可打印 ASCII 字符）：换一条再试。</summary>
        InvalidToken = 3
    }

    /// <summary>
    /// 一条"要排期的本地通知"请求：<b>意图</b>，不是已经排上的计划。
    /// <para>
    /// <b>为什么是 <c>readonly struct</c> + <c>in</c> 传参</b>：排期常发生在登录完成/进入大厅这种
    /// 一次性路径上，一条提醒一次堆分配看着不多，但"排 32 条"就是 32 次；更重要的是
    /// <c>readonly struct</c> 让"实现方偷偷改调用方的请求"在编译期就不可能发生
    /// （与 <c>AdShowRequest</c> / <c>AudioPlayRequest</c> 同一形状约定）。
    /// </para>
    /// <para>
    /// <b>为什么时间用 Unix 秒而不是 <see cref="System.DateTime"/></b>：Unix 秒跨平台、跨存档、
    /// 跨序列化的字节表示唯一，且不携带 <c>DateTimeKind</c> 这个"看着是 UTC 其实是本地"的语义陷阱。
    /// 纯逻辑层禁止读系统时钟，所以"现在几点"只能由实现方经 <c>ITimeProvider</c> 换算。
    /// </para>
    /// <para>
    /// <b>字符串在构造期原样保存、读取期兜底空串</b>：<c>default(PushNotificationRequest)</c> 会绕过构造器，
    /// 其字段是 <c>null</c>；因此 getter 对 <c>null</c> 一律返回空串。读方永远不用判 <c>null</c>。
    /// </para>
    /// </summary>
    public readonly struct PushNotificationRequest
    {
        /// <summary>标题的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _title;

        /// <summary>正文的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _body;

        /// <summary>构造一条排期请求。</summary>
        /// <param name="notificationId">
        /// 通知 id（调用方自定的稳定整数）。<b>同 id 覆盖旧计划</b>，因此"改提醒时间"必须复用同一个 id，
        /// 否则玩家会收到两条提醒。
        /// </param>
        /// <param name="title">标题（<c>null</c> 归一化为空串；允许为空，部分平台只用正文）。</param>
        /// <param name="body">正文（<c>null</c> 归一化为空串）。</param>
        /// <param name="fireAtUnixSeconds">计划触发时间（UTC 秒）；<b>&lt;= 0 表示立即</b>。</param>
        /// <param name="repeatIntervalSeconds">重复间隔（秒）；<b>&lt;= 0 表示一次性</b>。</param>
        public PushNotificationRequest(
            int notificationId,
            string title,
            string body,
            long fireAtUnixSeconds,
            int repeatIntervalSeconds)
        {
            NotificationId = notificationId;
            _title = title;
            _body = body;
            FireAtUnixSeconds = fireAtUnixSeconds;
            RepeatIntervalSeconds = repeatIntervalSeconds;
        }

        /// <summary>通知 id（唯一键；同 id 覆盖旧计划）。</summary>
        public int NotificationId { get; }

        /// <summary>标题（可能为空串；永远不为 <c>null</c>）。</summary>
        public string Title
        {
            get { return _title ?? string.Empty; }
        }

        /// <summary>正文（可能为空串；永远不为 <c>null</c>）。</summary>
        public string Body
        {
            get { return _body ?? string.Empty; }
        }

        /// <summary>
        /// 计划触发时间（UTC 秒）；<b>&lt;= 0 表示立即</b>。
        /// <para>
        /// "立即"用非正值表达，是为了让调用方不必持有时间源：想"现在就弹"的切片直接传 0，
        /// 由实现方按自己的 <c>ITimeProvider</c> 换算成具体时刻。
        /// </para>
        /// </summary>
        public long FireAtUnixSeconds { get; }

        /// <summary>
        /// 重复间隔（秒）；<b>&lt;= 0 表示一次性</b>。
        /// <para>
        /// 负数按"一次性"处理而不是"每 -5 秒"：脏配置应该退化成最保守的行为，
        /// 而不是让平台去解释一个不存在的周期。
        /// </para>
        /// </summary>
        public int RepeatIntervalSeconds { get; }
    }

    /// <summary>
    /// 一条<b>已经排进计划</b>的本地通知快照：id + 文案 + 触发时间 + "是不是重复"。
    /// <para>
    /// <b>为什么与 <see cref="PushNotificationRequest"/> 分开、且不暴露重复间隔</b>：请求是"意图"
    /// （含"每 86400 秒重复"这种排期实现细节），快照是"这一刻计划里有什么"。计划列表/诊断只需要知道
    /// "一次性还是重复"，把原始间隔暴露出去，UI 就会开始自己算"下一次是几点"——那等于在 UI 里
    /// 复刻一份排期算法，迟早与真实计划漂移（与 <c>LeaderboardEntry</c> 只给名次不给算分公式同理）。
    /// </para>
    /// <para>
    /// <b>快照只读</b>：<see cref="IPushService.GetScheduled"/> 每次返回<b>新数组</b>，
    /// 调用方排序/改字段都不会影响服务内部计划（否则 UI 能把一条已取消的提醒"改回来"）。
    /// </para>
    /// <para>
    /// <b>字符串在构造期原样保存、读取期兜底空串</b>：<c>default(PushNotification)</c> 会绕过构造器。
    /// </para>
    /// </summary>
    public readonly struct PushNotification
    {
        /// <summary>标题的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _title;

        /// <summary>正文的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _body;

        /// <summary>构造一条已排期的通知快照。</summary>
        /// <param name="notificationId">通知 id（唯一键）。</param>
        /// <param name="title">标题（<c>null</c> 归一化为空串）。</param>
        /// <param name="body">正文（<c>null</c> 归一化为空串）。</param>
        /// <param name="fireAtUnixSeconds">计划触发时间（UTC 秒；实现方应把"立即"落成具体时刻）。</param>
        /// <param name="isRepeating">是否为重复通知（由请求的重复间隔是否 &gt; 0 决定）。</param>
        public PushNotification(
            int notificationId,
            string title,
            string body,
            long fireAtUnixSeconds,
            bool isRepeating)
        {
            NotificationId = notificationId;
            _title = title;
            _body = body;
            FireAtUnixSeconds = fireAtUnixSeconds;
            IsRepeating = isRepeating;
        }

        /// <summary>通知 id（唯一键；同 id 在计划里只会有一条）。</summary>
        public int NotificationId { get; }

        /// <summary>标题（可能为空串；永远不为 <c>null</c>）。</summary>
        public string Title
        {
            get { return _title ?? string.Empty; }
        }

        /// <summary>正文（可能为空串；永远不为 <c>null</c>）。</summary>
        public string Body
        {
            get { return _body ?? string.Empty; }
        }

        /// <summary>计划触发时间（UTC 秒）。</summary>
        public long FireAtUnixSeconds { get; }

        /// <summary>是否为重复通知（<c>false</c> = 一次性，派发后从计划中消失）。</summary>
        public bool IsRepeating { get; }
    }

    /// <summary>
    /// 推送端口：<b>本地通知排期 + 远端 token 登记 + 权限状态</b>。
    /// 真实实现接 Unity Mobile Notifications / APNs / FCM；Phase 1 由适配层假实现
    /// （<c>ConsolePushService</c> / <c>UnityPushService</c>），不接任何真实推送 SDK。
    /// <para>
    /// <b>为什么整块都是同步签名</b>：契约必须能被"纯内存假推送后端"直接实现，这样排期、覆盖、取消、
    /// token 校验能在无头测试与断网真机上完整验收。真实 SDK 的异步（授权弹窗、token 回调、注册失败重试）
    /// 由适配层用"请求 + 回调/缓存"包一层，把结果喂进本端口；把 <c>Task</c> 写进契约会逼纯逻辑层依赖异步调度
    /// （与 <see cref="ISocialService"/>、<c>IAccountService</c> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>本接口只管"什么时候弹、token 有没有登记"，不管"弹什么内容"</b>：文案、活动 id、跳转参数由业务切片
    /// 决定，端口只负责把一条提醒交给平台。反过来，端口也<b>不</b>负责"玩家该不该收到这条提醒"——
    /// 那是业务策略（免打扰时段、活动是否开启），写进端口会让适配层长出业务分支。
    /// </para>
    /// <para>
    /// <b>权限与 token 的顺序在真机上是固定的</b>：iOS/Android 13+ 上，没有通知授权就拿不到可用的 token，
    /// 登记一定失败。因此调用方必须先 <see cref="RequestPermission"/> 再 <see cref="RegisterToken"/>；
    /// 顺序错了本端口会如实返回 <see cref="PushRegisterResult.PermissionDenied"/>，不会替你"先登记后补授权"。
    /// </para>
    /// <para>
    /// <b>禁止抛异常</b>：失败必须落成 <see cref="PushRegisterResult"/> / <c>false</c> / 空数组。
    /// 推送是外围系统，一次 SDK 抖动不允许把玩家挡在登录流程里（调用方仍会兜底捕获，但那是第二道防线）。
    /// </para>
    /// <para>线程模型：实现方自行声明；框架侧按单线程串行调用（排期、登记、取消都在主线程）。</para>
    /// </summary>
    public interface IPushService
    {
        /// <summary>
        /// 推送后端当前是否可用（SDK 未初始化/未接线/平台不支持都必须如实返回 <c>false</c>，不得"假装成功"）。
        /// <para>
        /// 调用方必须先判它：不可用时一律降级（排期返回 <c>false</c>、登记返回
        /// <see cref="PushRegisterResult.Unavailable"/>、计划列表为空数组），而不是反复弹失败提示。
        /// "假装成功"是最坏的降级——玩家以为提醒排上了，到点什么都没发生。
        /// </para>
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 当前通知权限状态（只读快照）。
        /// <para>
        /// 系统设置里改权限不会回调本端口，因此实现方应在读取时向平台查询，或至少在 App 恢复前台时刷新一次；
        /// 缓存一个永不更新的 <see cref="PushPermissionState.Granted"/> 会让"玩家关掉了通知"这件事永远不被发现。
        /// </para>
        /// </summary>
        PushPermissionState PermissionState { get; }

        /// <summary>
        /// 申请通知权限（假实现直接置 Granted/Denied，不弹任何系统框）。
        /// <para>
        /// 返回<b>最终状态</b>而不是"是否弹过框"：真实平台是异步弹窗，适配层负责在回调后把结果写进
        /// <see cref="PermissionState"/> 再返回，调用方拿到的永远是"现在能不能推"。
        /// </para>
        /// <para>
        /// 后端不可用时返回 <see cref="PushPermissionState.Denied"/> 而不是
        /// <see cref="PushPermissionState.Unknown"/>：请求已经发出去了却没被授权，
        /// 停在 Unknown 会让 UI 以为"还没问过"并反复触发授权流程。
        /// </para>
        /// </summary>
        /// <returns>申请后的权限状态（<b>永不抛异常</b>）。</returns>
        PushPermissionState RequestPermission();

        /// <summary>
        /// 登记远端推送 token。
        /// <para>
        /// 校验规则由实现方保证（假实现与 Core 共用同一套）：空/纯空白、长度超过上限、
        /// 含非可打印 ASCII 字符（真实 token 是 base64/hex）一律
        /// <see cref="PushRegisterResult.InvalidToken"/>。
        /// </para>
        /// </summary>
        /// <param name="token">远端下发的 token（可能为 <c>null</c>/脏数据，不得因此抛异常）。</param>
        /// <returns>登记结果（<b>永不抛异常</b>）。</returns>
        PushRegisterResult RegisterToken(string token);

        /// <summary>
        /// 取消登记（<b>登出时必须调用</b>）。
        /// <para>
        /// 不调用会造成最严重的推送事故：设备上的 token 还挂着上一个账号，换号后新玩家会收到前一个账号的
        /// 活动提醒（隐私泄露 + 客诉）。因此登出路径宁可多调一次（幂等），也不能漏。
        /// </para>
        /// </summary>
        void UnregisterToken();

        /// <summary>
        /// 已登记的 token；<b>未登记返回空串（不是 <c>null</c>）</b>。
        /// <para>
        /// 选空串而不是 <c>null</c>，与 <see cref="FriendInfo.PlayerId"/> 的归一化口径一致：
        /// 调用方只需要判 <c>Length == 0</c>，不用写 <c>string.IsNullOrEmpty</c>。
        /// 签名仍是 <c>string?</c>，因为违约实现可能返回 <c>null</c>——调用方判空更稳，
        /// 框架内的实现一律返回空串。
        /// </para>
        /// </summary>
        string? RegisteredToken { get; }

        /// <summary>
        /// 调度一条本地通知；<b>同 <see cref="PushNotificationRequest.NotificationId"/> 覆盖旧计划</b>。
        /// <para>
        /// 覆盖语义是刚需：活动倒计时改时间、文案换语言都发生在"已经排过一条"之后。
        /// 若同 id 变成两条，玩家就会在旧时间与新时间各收到一次。
        /// </para>
        /// </summary>
        /// <param name="request">排期请求（只读传参，实现方不得修改调用方的数据）。</param>
        /// <returns><c>true</c> = 平台已接受；<c>false</c> = 被拒绝（不可用/超额/参数不被平台接受），<b>永不抛异常</b>。</returns>
        bool Schedule(in PushNotificationRequest request);

        /// <summary>取消指定通知。</summary>
        /// <param name="notificationId">通知 id。</param>
        /// <returns><c>true</c> = 真的取消了；<c>false</c> = 计划里本来就没有这条（合法结果，不是错误）。</returns>
        bool Cancel(int notificationId);

        /// <summary>取消全部（<b>登出 / 玩家关闭推送时必须调用</b>）。</summary>
        void CancelAll();

        /// <summary>
        /// 列出当前计划中的通知（<b>按触发时间升序</b>；同一时刻按 id 升序，保证顺序稳定可断言）。
        /// <para>返回<b>新数组</b>（改数组不影响服务内部状态）；不可用时返回空数组，<b>不抛异常</b>。</para>
        /// </summary>
        /// <returns>计划快照数组（永不为 <c>null</c>）。</returns>
        PushNotification[] GetScheduled();
    }
}
