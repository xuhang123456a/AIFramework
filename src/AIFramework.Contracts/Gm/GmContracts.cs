#nullable enable
using System;

namespace AIFramework.Contracts.Gm
{
    /// <summary>
    /// GM 权限级别：数值越大权限越高，比较一律按数值序（<c>Player &lt; Support &lt; Admin</c>）。
    /// <para>
    /// <b>这层只防误操作，不防作弊</b>：真正的权限裁决必须在服务端完成（客户端拿到的权限位可被篡改），
    /// 客户端校验的唯一价值是让越权命令在本地就失败并留下审计，而不是把非法意图发给服务端。
    /// </para>
    /// </summary>
    public enum GmPermission : byte
    {
        /// <summary>普通玩家（0）：只允许只读类命令。</summary>
        Player = 0,

        /// <summary>客服（1）：可代替玩家调整展示态（语言、引导等），不碰经济数值。</summary>
        Support = 1,

        /// <summary>管理员（2）：可改经济/时间等影响数值的命令。</summary>
        Admin = 2,
    }

    /// <summary>一条 GM 命令的静态描述：注册时确定，执行期只读。</summary>
    public readonly struct GmCommandDescriptor
    {
        /// <summary>构造命令描述。合法性（名字、描述、参数个数）由 <c>GmConsole.Register</c> 统一校验。</summary>
        /// <param name="name">命令名（大小写敏感；只允许 <c>a-z</c>、<c>0-9</c>、<c>.</c>、<c>_</c>）。</param>
        /// <param name="description">给运营看的一句话说明，<c>gm.help</c> 直接展示，不允许为空。</param>
        /// <param name="minimumPermission">执行所需的最低权限。</param>
        /// <param name="argumentCount">固定参数个数（不支持可选参数/可变参数，便于审计口径统一）。</param>
        public GmCommandDescriptor(string name, string description, GmPermission minimumPermission, int argumentCount)
        {
            Name = name;
            Description = description;
            MinimumPermission = minimumPermission;
            ArgumentCount = argumentCount;
        }

        /// <summary>命令名，大小写敏感（<c>gm.give</c> 与 <c>GM.GIVE</c> 不是同一条命令）。</summary>
        public string Name { get; }

        /// <summary>一句话说明（中文，面向运营/客服）。</summary>
        public string Description { get; }

        /// <summary>执行所需的最低权限。</summary>
        public GmPermission MinimumPermission { get; }

        /// <summary>固定参数个数，必须大于等于 0。</summary>
        public int ArgumentCount { get; }
    }

    /// <summary>
    /// 一次 GM 命令的执行结果。
    /// <para>
    /// <b>为什么用 reason 字符串而不是异常</b>：GM 命令由运营手敲/脚本批量下发，失败是常态（拼错命令、权限不够、
    /// 参数写错），用异常表达会让调用点被迫写 try/catch；这里统一用"结果对象 + 稳定原因码"，
    /// 让审计与前端提示都能直接消费。
    /// </para>
    /// </summary>
    public readonly struct GmCommandResult
    {
        /// <summary>构造执行结果。</summary>
        /// <param name="success">是否执行成功（失败也一定写审计）。</param>
        /// <param name="output">成功时的回显文本（失败时通常为空串）。</param>
        /// <param name="reason">失败原因码；成功时为空串。约定值见 <c>GmConsole</c> 的常量注释。</param>
        public GmCommandResult(bool success, string output, string reason)
        {
            Success = success;
            Output = output;
            Reason = reason;
        }

        /// <summary>是否执行成功。</summary>
        public bool Success { get; }

        /// <summary>成功回显（如"发放后数量=100"），直接可打印给运营看。</summary>
        public string Output { get; }

        /// <summary>
        /// 失败原因码（成功时为空串）。稳定取值：
        /// <c>unknown command</c> / <c>permission denied</c> / <c>bad arguments</c> /
        /// <c>handler threw: &lt;异常类型名&gt;</c>，以及各命令自己的业务原因（如 <c>invalid itemId</c>）。
        /// </summary>
        public string Reason { get; }
    }

    /// <summary>
    /// 一条 GM 审计记录：谁（<see cref="OperatorId"/>）、在什么时候（<see cref="ExecutedAtUnixSeconds"/>）、
    /// 以哪个权限（<see cref="Permission"/>）、执行了什么（<see cref="Command"/> + <see cref="Arguments"/>）、
    /// 结果如何（<see cref="Success"/> / <see cref="Output"/>）。
    /// <para>
    /// <b>越权尝试也要留痕</b>：权限不足、参数不符、未知命令都会产生一条 <c>Success=false</c> 的记录，
    /// 否则"谁在反复试 Admin 命令"这件事在日志里根本看不见。
    /// </para>
    /// </summary>
    public readonly struct GmAuditEntry
    {
        /// <summary>构造一条审计记录（由 <c>GmConsole</c> 在每次 <c>Execute</c> 后写入）。</summary>
        /// <param name="command">命令名；未知命令记录调用方原样输入的名字（<c>null</c> 记为空串）。</param>
        /// <param name="arguments">参数原文，以单个空格拼接（无参数时为空串）。</param>
        /// <param name="operatorId">操作者标识；空值归一为 <c>unknown</c>。</param>
        /// <param name="permission">调用方**实际持有**的权限（不是命令要求的最低权限）。</param>
        /// <param name="success">本次执行是否成功。</param>
        /// <param name="executedAtUnixSeconds">执行时刻（UTC Unix 秒，来自 <c>ITimeProvider</c>）。</param>
        /// <param name="output">成功回显；失败时为空串。</param>
        public GmAuditEntry(
            string command,
            string arguments,
            string operatorId,
            GmPermission permission,
            bool success,
            long executedAtUnixSeconds,
            string output)
        {
            Command = command;
            Arguments = arguments;
            OperatorId = operatorId;
            Permission = permission;
            Success = success;
            ExecutedAtUnixSeconds = executedAtUnixSeconds;
            Output = output;
        }

        /// <summary>命令名（原样回显调用方输入，便于定位"拼错命令"类问题）。</summary>
        public string Command { get; }

        /// <summary>参数原文（单空格拼接；无参数为空串）。</summary>
        public string Arguments { get; }

        /// <summary>操作者标识（空值归一为 <c>unknown</c>）。</summary>
        public string OperatorId { get; }

        /// <summary>调用方实际持有的权限。</summary>
        public GmPermission Permission { get; }

        /// <summary>本次执行是否成功。</summary>
        public bool Success { get; }

        /// <summary>执行时刻（UTC Unix 秒）。</summary>
        public long ExecutedAtUnixSeconds { get; }

        /// <summary>成功回显（失败为空串）。</summary>
        public string Output { get; }
    }

    /// <summary>
    /// GM 控制台：命令注册 + 权限分级 + 审计。
    /// <para>实现必须是<b>全同步</b>的：注册与执行都假定在宿主主线程串行发生（与 CQE 总线一致），
    /// 内部不做加锁；若将来要多线程下发命令，由宿主在调用点串行化。</para>
    /// </summary>
    public interface IGmConsole
    {
        /// <summary>已注册命令数。</summary>
        int CommandCount { get; }

        /// <summary>已注册命令描述，**按注册顺序**返回独立副本（调用方改数组不影响内部）。</summary>
        GmCommandDescriptor[] Commands { get; }

        /// <summary>注册命令（重名抛 <see cref="InvalidOperationException"/>）。</summary>
        /// <param name="descriptor">命令描述；名字/描述/参数个数非法抛 <see cref="ArgumentException"/>。</param>
        /// <param name="handler">命令处理器；参数已按 <see cref="GmCommandDescriptor.ArgumentCount"/> 校验过。</param>
        void Register(in GmCommandDescriptor descriptor, Func<string[], GmCommandResult> handler);

        /// <summary>
        /// 执行：权限不足返回失败（reason=<c>permission denied</c>），未知命令返回失败。
        /// <para>无论成功失败都写一条审计；处理器异常被捕获为失败（<c>handler threw: XxxException</c>），不冒泡。</para>
        /// </summary>
        /// <param name="command">命令名（大小写敏感）。</param>
        /// <param name="arguments">参数数组；<c>null</c> 按零个参数处理。</param>
        /// <param name="callerPermission">调用方实际权限。</param>
        /// <param name="operatorId">操作者标识；空值归一为 <c>unknown</c>。</param>
        /// <returns>执行结果（永不抛异常）。</returns>
        GmCommandResult Execute(string command, string[] arguments, GmPermission callerPermission, string operatorId);

        /// <summary>
        /// 审计记录（环形缓冲，最近 N 条），按执行时间升序返回独立副本：索引 0 为当前最旧一条。
        /// </summary>
        GmAuditEntry[] AuditLog { get; }
    }
}
