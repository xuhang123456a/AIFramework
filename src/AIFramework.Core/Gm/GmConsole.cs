#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gm;
using AIFramework.Contracts.Ports;

namespace AIFramework.Core.Gm
{
    /// <summary>
    /// GM 控制台纯逻辑实现：命令注册表 + 权限分级 + 环形审计。
    /// <para>
    /// <b>三条不可动摇的规则</b>：
    /// <list type="number">
    /// <item>每次 <see cref="Execute"/> 必定写一条审计——包括未知命令、越权、参数不符、处理器抛异常；
    /// "没执行成功所以不用记"是审计里最危险的漏洞。</item>
    /// <item>权限检查在参数检查之前：越权调用方连"这条命令要几个参数"都不该试探出来。</item>
    /// <item>处理器异常一律捕获成失败结果，绝不冒泡到宿主主循环——GM 命令是运营工具，不能把游戏打挂。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>线程模型</b>：全同步、非线程安全。注册与执行都假定在宿主主线程串行发生（与 <c>ICqeBus</c> 一致），
    /// 内部无锁；多线程下发场景由宿主在调用点串行化。
    /// </para>
    /// <para>
    /// <b>分配</b>：不在热路径上——每条命令都会拼参数串并写一条审计，属于运营工具级别开销，
    /// 与因果日志（每帧数千条、零分配）不是同一量级，不为此牺牲可读性。
    /// </para>
    /// </summary>
    public sealed class GmConsole : IGmConsole
    {
        /// <summary>失败原因：命令未注册（大小写敏感，<c>GM.GIVE</c> 也算未知命令）。</summary>
        public const string UnknownCommandReason = "unknown command";

        /// <summary>失败原因：调用方权限低于命令要求的最低权限。</summary>
        public const string PermissionDeniedReason = "permission denied";

        /// <summary>失败原因：参数个数与命令声明不符。</summary>
        public const string BadArgumentsReason = "bad arguments";

        /// <summary>失败原因前缀：处理器抛异常时拼上异常类型名（如 <c>handler threw: InvalidOperationException</c>）。</summary>
        public const string HandlerThrewReasonPrefix = "handler threw: ";

        /// <summary>操作者标识为空时的归一值。</summary>
        public const string UnknownOperatorId = "unknown";

        private readonly ITimeProvider _time;

        /// <summary>按注册顺序保存的描述（<see cref="Commands"/> 的顺序来源）。</summary>
        private readonly List<GmCommandDescriptor> _descriptors;

        /// <summary>命令名 → 描述 + 处理器。比较器固定 <see cref="StringComparer.Ordinal"/>（大小写敏感）。</summary>
        private readonly Dictionary<string, CommandEntry> _entries;

        /// <summary>审计环形缓冲（定长，构造后不变）。</summary>
        private readonly GmAuditEntry[] _audit;

        /// <summary>下一条审计的写入位置。</summary>
        private int _auditWriteIndex;

        /// <summary>当前缓冲里的有效审计条数（不超过容量）。</summary>
        private int _auditValidCount;

        /// <summary>创建一个 GM 控制台。</summary>
        /// <param name="time">时间源（非 <c>null</c>）；审计时间戳只从这里取，禁止直接读系统时钟。</param>
        /// <param name="auditCapacity">审计环形缓冲容量，必须大于 0（默认 128 条，够一次排障会话回看）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="time"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="auditCapacity"/> 小于等于 0。</exception>
        public GmConsole(ITimeProvider time, int auditCapacity = 128)
        {
            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            if (auditCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(auditCapacity),
                    auditCapacity,
                    "GM audit capacity must be greater than zero.");
            }

            _time = time;
            _descriptors = new List<GmCommandDescriptor>();
            _entries = new Dictionary<string, CommandEntry>(StringComparer.Ordinal);
            _audit = new GmAuditEntry[auditCapacity];
        }

        /// <summary>已注册命令数。</summary>
        public int CommandCount
        {
            get { return _descriptors.Count; }
        }

        /// <summary>已注册命令描述，按注册顺序返回独立副本。</summary>
        public GmCommandDescriptor[] Commands
        {
            get { return _descriptors.ToArray(); }
        }

        /// <summary>审计记录，按执行时间升序返回独立副本；索引 0 为当前最旧一条。</summary>
        public GmAuditEntry[] AuditLog
        {
            get
            {
                var copy = new GmAuditEntry[_auditValidCount];

                // 最旧记录 = 写指针往回退 validCount 格（负数回绕到缓冲尾部）。
                int physicalIndex = _auditWriteIndex - _auditValidCount;
                if (physicalIndex < 0)
                {
                    physicalIndex += _audit.Length;
                }

                for (int i = 0; i < copy.Length; i++)
                {
                    copy[i] = _audit[physicalIndex];
                    physicalIndex++;
                    if (physicalIndex == _audit.Length)
                    {
                        physicalIndex = 0;
                    }
                }

                return copy;
            }
        }

        /// <summary>
        /// 注册命令。
        /// <para>
        /// 校验顺序固定为：处理器非空 → 名字合法 → 描述非空 → 参数个数非负 → 不重名。
        /// 任何一步失败都不改变注册表（不会留下"注册了一半"的命令）。
        /// </para>
        /// </summary>
        /// <param name="descriptor">命令描述。</param>
        /// <param name="handler">命令处理器。</param>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException">名字为空/含非法字符、描述为空、参数个数为负。</exception>
        /// <exception cref="InvalidOperationException">同名命令已注册（<c>StringComparer.Ordinal</c>，大小写敏感）。</exception>
        public void Register(in GmCommandDescriptor descriptor, Func<string[], GmCommandResult> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            string name = RequireValidName(descriptor.Name);
            string description = RequireValidDescription(descriptor.Description);

            if (descriptor.ArgumentCount < 0)
            {
                throw new ArgumentException(
                    "GM command argument count must not be negative: " + name,
                    nameof(descriptor));
            }

            if (_entries.ContainsKey(name))
            {
                throw new InvalidOperationException("GM command is already registered: " + name);
            }

            // 两张表存同一份归一化描述：名字/描述已过校验，执行期只读。
            var normalized = new GmCommandDescriptor(
                name,
                description,
                descriptor.MinimumPermission,
                descriptor.ArgumentCount);

            _entries.Add(name, new CommandEntry(normalized, handler));
            _descriptors.Add(normalized);
        }

        /// <summary>
        /// 执行一条 GM 命令。每次调用都写一条审计，返回值永不抛异常。
        /// </summary>
        /// <param name="command">命令名（大小写敏感）。</param>
        /// <param name="arguments">参数数组；<c>null</c> 按零个参数处理。</param>
        /// <param name="callerPermission">调用方实际权限。</param>
        /// <param name="operatorId">操作者标识；<c>null</c>/空串归一为 <c>unknown</c>。</param>
        /// <returns>执行结果。</returns>
        public GmCommandResult Execute(
            string command,
            string[] arguments,
            GmPermission callerPermission,
            string operatorId)
        {
            string normalizedOperator = NormalizeOperatorId(operatorId);
            string[] args = arguments ?? Array.Empty<string>();
            string argsText = string.Join(" ", args);

            string? name = command;
            if (name == null)
            {
                return Reject(string.Empty, argsText, normalizedOperator, callerPermission, UnknownCommandReason);
            }

            if (!_entries.TryGetValue(name, out CommandEntry entry))
            {
                return Reject(name, argsText, normalizedOperator, callerPermission, UnknownCommandReason);
            }

            // 先判权限再判参数：越权调用方不该从原因码里反推出命令签名。
            if (callerPermission < entry.Descriptor.MinimumPermission)
            {
                return Reject(name, argsText, normalizedOperator, callerPermission, PermissionDeniedReason);
            }

            if (args.Length != entry.Descriptor.ArgumentCount)
            {
                return Reject(name, argsText, normalizedOperator, callerPermission, BadArgumentsReason);
            }

            GmCommandResult result;
            try
            {
                result = entry.Handler(args);
            }
            catch (Exception ex)
            {
                // 处理器异常不冒泡：转成失败结果并留痕，异常类型名足够定位到具体命令实现。
                result = new GmCommandResult(false, string.Empty, HandlerThrewReasonPrefix + ex.GetType().Name);
            }

            AppendAudit(
                name,
                argsText,
                normalizedOperator,
                callerPermission,
                result.Success,
                NormalizeOutput(result.Output));

            return result;
        }

        /// <summary>写一条失败审计并返回失败结果（失败路径共用）。</summary>
        private GmCommandResult Reject(
            string command,
            string arguments,
            string operatorId,
            GmPermission permission,
            string reason)
        {
            AppendAudit(command, arguments, operatorId, permission, false, string.Empty);
            return new GmCommandResult(false, string.Empty, reason);
        }

        /// <summary>追加一条审计；缓冲满时覆盖最旧一条。</summary>
        private void AppendAudit(
            string command,
            string arguments,
            string operatorId,
            GmPermission permission,
            bool success,
            string output)
        {
            _audit[_auditWriteIndex] = new GmAuditEntry(
                command,
                arguments,
                operatorId,
                permission,
                success,
                ToUnixSeconds(_time.UtcNow),
                output);

            _auditWriteIndex++;
            if (_auditWriteIndex == _audit.Length)
            {
                _auditWriteIndex = 0;
            }

            if (_auditValidCount < _audit.Length)
            {
                _auditValidCount++;
            }
        }

        /// <summary>校验命令名：非空、且只含 <c>a-z</c>、<c>0-9</c>、<c>.</c>、<c>_</c>。</summary>
        private static string RequireValidName(string name)
        {
            string? candidate = name;
            if (candidate == null || candidate.Length == 0)
            {
                throw new ArgumentException(
                    "GM command name must not be empty.",
                    "descriptor");
            }

            for (int i = 0; i < candidate.Length; i++)
            {
                char c = candidate[i];
                bool allowed = (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '.'
                    || c == '_';

                if (!allowed)
                {
                    throw new ArgumentException(
                        "GM command name may only contain 'a'-'z', '0'-'9', '.' and '_' (case sensitive): " + candidate,
                        "descriptor");
                }
            }

            return candidate;
        }

        /// <summary>校验描述：不能为空或纯空白（<c>gm.help</c> 要展示它，空描述等于命令不可发现）。</summary>
        private static string RequireValidDescription(string description)
        {
            string? candidate = description;
            if (candidate == null || candidate.Trim().Length == 0)
            {
                throw new ArgumentException(
                    "GM command description must not be empty.",
                    "descriptor");
            }

            return candidate;
        }

        /// <summary>操作者标识归一：<c>null</c> 与空串都记为 <c>unknown</c>（空白串原样保留，便于发现脏数据）。</summary>
        private static string NormalizeOperatorId(string operatorId)
        {
            return string.IsNullOrEmpty(operatorId) ? UnknownOperatorId : operatorId;
        }

        /// <summary>处理器返回 <c>default(GmCommandResult)</c> 时把 null 文本归一为空串，审计字段永不存 null。</summary>
        private static string NormalizeOutput(string? output)
        {
            return output ?? string.Empty;
        }

        /// <summary>
        /// 把时间源给出的 UTC 时刻换成 Unix 秒。
        /// <para>
        /// <see cref="DateTimeKind.Unspecified"/> 一律按 UTC 解释：否则 <see cref="DateTimeOffset"/> 会按宿主本地时区
        /// 计算偏移，同一份代码在不同机器上得到不同审计时间戳（确定性红线）。
        /// </para>
        /// </summary>
        private static long ToUnixSeconds(DateTime utcNow)
        {
            DateTime normalized = utcNow.Kind == DateTimeKind.Utc
                ? utcNow
                : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

            return new DateTimeOffset(normalized).ToUnixTimeSeconds();
        }

        /// <summary>注册表条目：描述与处理器成对保存，避免两张表不同步。</summary>
        private sealed class CommandEntry
        {
            public CommandEntry(in GmCommandDescriptor descriptor, Func<string[], GmCommandResult> handler)
            {
                Descriptor = descriptor;
                Handler = handler;
            }

            public GmCommandDescriptor Descriptor { get; }

            public Func<string[], GmCommandResult> Handler { get; }
        }
    }
}
