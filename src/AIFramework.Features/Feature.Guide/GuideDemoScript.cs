using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Guide
{
    /// <summary>演示脚本里的一步：做什么、给谁、等多久。</summary>
    public readonly struct GuideDemoStep
    {
        /// <summary>构造一步。</summary>
        /// <param name="kind">动作类型（0=触发 1=推进 2=跳过当前 3=跳过全部）。</param>
        /// <param name="value">动作参数（触发时是 trigger 值，推进/跳过时是 stepId）。</param>
        /// <param name="targetId">触发目标 id（不限定填 0）。</param>
        /// <param name="delayMs">相对上一步的等待毫秒（第 0 步相对开始）。</param>
        public GuideDemoStep(int kind, int value, int targetId, int delayMs)
        {
            Kind = kind;
            Value = value;
            TargetId = targetId;
            DelayMs = delayMs < 0 ? 0 : delayMs;
        }

        /// <summary>动作类型：0=触发 1=推进 2=跳过当前 3=跳过全部。</summary>
        public int Kind { get; }

        /// <summary>动作参数。</summary>
        public int Value { get; }

        /// <summary>触发目标 id。</summary>
        public int TargetId { get; }

        /// <summary>等待毫秒（&lt;0 按 0）。</summary>
        public int DelayMs { get; }
    }

    /// <summary>
    /// 新手引导演示脚本（数据）：一串"延迟 + 动作"，用来在演示/验收里自动走一遍引导。
    /// <para>
    /// <b>为什么需要脚本而不是硬编码调用</b>：策划/QA 要能改演示流程（先展示商店、再战斗），
    /// 硬编码在代码里每次都要发版；脚本化后可以放进表/配置文件，也能被自动化测试直接复用。
    /// </para>
    /// <para>
    /// <b>为什么动作只覆盖触发/推进/跳过</b>：这三类已经能表达完整引导演示；把"任意命令"都做进来
    /// 会让脚本可执行任意 CQE 命令（等于给演示脚本开了后门），风险不值得。
    /// </para>
    /// </summary>
    public readonly struct GuideDemoScript
    {
        /// <summary>构造脚本。</summary>
        /// <param name="name">脚本名（日志/报告用）。</param>
        /// <param name="steps">步骤。</param>
        public GuideDemoScript(string name, GuideDemoStep[] steps)
        {
            Name = name ?? string.Empty;
            Steps = steps ?? new GuideDemoStep[0];
        }

        /// <summary>脚本名。</summary>
        public string Name { get; }

        /// <summary>步骤。</summary>
        public GuideDemoStep[] Steps { get; }

        /// <summary>总时长（毫秒）。</summary>
        public int TotalDurationMs
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Steps.Length; i++)
                {
                    total += Steps[i].DelayMs;
                }

                return total;
            }
        }
    }

    /// <summary>
    /// 引导演示播放器：按脚本延迟发 CQE 命令（适配层每帧/每 tick 调 <see cref="Tick"/>）。
    /// <para>
    /// <b>为什么不用计时器/协程</b>：Features 层禁止引擎生命周期，用注入的 deltaMs 推进便于无头测试
    /// （直接 Tick(10_000) 跑完整个脚本），也便于时间加速。
    /// </para>
    /// <para>
    /// <b>失败语义</b>：某一步的命令被内核拒绝（例如该步还没 reached 就 advance）不会让播放器崩，
    /// 只记 <see cref="LastError"/> 并继续下一步——演示脚本是"演示"，不该中断整个流程。
    /// </para>
    /// </summary>
    public sealed class GuideDemoPlayer
    {
        private readonly ICqeBus _bus;
        private GuideDemoScript _script;
        private int _nextIndex;
        private int _elapsedMs;
        private int _waitingSinceMs;
        private int _executedSteps;
        private bool _started;
        private string _lastError = string.Empty;

        /// <summary>构造播放器。</summary>
        /// <param name="bus">CQE 总线（引导处理器必须已注册）。</param>
        public GuideDemoPlayer(ICqeBus bus)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        }

        /// <summary>是否已开始（调用过 <see cref="Start"/>）。</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>是否已执行完所有步骤（开始过且步骤耗尽；结束播放不会把"完成"变回 false）。</summary>
        public bool IsFinished
        {
            get { return _started && _nextIndex >= _script.Steps.Length; }
        }

        /// <summary>已执行步数。</summary>
        public int ExecutedSteps
        {
            get { return _executedSteps; }
        }

        /// <summary>最近一次命令失败原因（成功/无失败为空串）。</summary>
        public string LastError
        {
            get { return _lastError; }
        }

        /// <summary>当前脚本名（未开始为空串）。</summary>
        public string ScriptName
        {
            get { return _script.Name; }
        }

        /// <summary>开始播放（重置进度）。</summary>
        /// <param name="script">脚本。</param>
        public void Start(in GuideDemoScript script)
        {
            _script = script;
            _nextIndex = 0;
            _elapsedMs = 0;
            _waitingSinceMs = 0;
            _executedSteps = 0;
            _lastError = string.Empty;
            _started = true;
            IsPlaying = true;
        }

        /// <summary>停止播放（保留已执行计数）。</summary>
        public void Stop()
        {
            IsPlaying = false;
        }

        /// <summary>推进时间：执行所有已到期的步骤。</summary>
        /// <param name="deltaMs">经过的毫秒（&lt;=0 被忽略）。</param>
        /// <returns>本 tick 执行的步数。</returns>
        public int Tick(int deltaMs)
        {
            // deltaMs == 0 允许：脚本第一步通常是 0 延迟，调用方在同一帧"开始 + Tick(0)"就应该发出它。
            if (!IsPlaying || deltaMs < 0)
            {
                return 0;
            }

            _elapsedMs += deltaMs;
            int executed = 0;

            while (_nextIndex < _script.Steps.Length)
            {
                GuideDemoStep step = _script.Steps[_nextIndex];
                int dueAt = _waitingSinceMs + step.DelayMs;
                if (_elapsedMs < dueAt)
                {
                    break;
                }

                Execute(step);
                _waitingSinceMs = dueAt;
                _nextIndex++;
                executed++;
                _executedSteps++;
            }

            // 步数耗尽：停止播放，但 IsFinished 保持 true（由 _started + 索引判断）。
            if (_nextIndex >= _script.Steps.Length)
            {
                IsPlaying = false;
            }

            return executed;
        }

        private void Execute(in GuideDemoStep step)
        {
            try
            {
                switch (step.Kind)
                {
                    case 0:
                        var trigger = new GuideTriggerCommand(step.Value, step.TargetId);
                        _bus.Send(in trigger);
                        break;
                    case 1:
                        var advance = new GuideAdvanceCommand(step.Value);
                        _bus.Send(in advance);
                        break;
                    case 2:
                        var skip = new GuideSkipCommand(step.Value);
                        _bus.Send(in skip);
                        break;
                    case 3:
                        var skipAll = new GuideSkipAllCommand();
                        _bus.Send(in skipAll);
                        break;
                    default:
                        _lastError = "未知动作类型：" + step.Kind;
                        break;
                }
            }
            catch (InvalidOperationException exception)
            {
                // 演示脚本被内核拒绝（例如步未 reached 就 advance）只记录不中断。
                _lastError = exception.Message;
            }
        }
    }

    /// <summary>
    /// 演示脚本编解码（固定 JSON 形状 ↔ <see cref="GuideDemoScript"/>）。
    /// <para>
    /// <b>为什么手写解析而不用 System.Text.Json</b>：Features 是 netstandard2.1 且不引第三方包，
    /// 加 JSON 依赖会污染整个切片；本格式是我们自己导出的固定形状（name + steps 数组），
    /// 只需最小扫描即可，且解析失败必须给原因而不是抛异常。
    /// </para>
    /// </summary>
    public static class GuideDemoScriptCodec
    {
        /// <summary>导出 JSON（固定字段顺序，便于人读与 diff）。</summary>
        public static string ToJson(in GuideDemoScript script)
        {
            var builder = new StringBuilder();
            builder.Append("{\"name\":\"").Append(Escape(script.Name)).Append("\",\"steps\":[");
            for (int i = 0; i < script.Steps.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                GuideDemoStep step = script.Steps[i];
                builder.Append("{\"kind\":").Append(step.Kind.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"value\":").Append(step.Value.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"targetId\":").Append(step.TargetId.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"delayMs\":").Append(step.DelayMs.ToString(CultureInfo.InvariantCulture))
                    .Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>从 JSON 解析（只认本类导出的形状；失败给原因，不抛异常）。</summary>
        public static bool TryParse(string? json, out GuideDemoScript script, out string error)
        {
            script = default;
            if (string.IsNullOrEmpty(json))
            {
                error = "脚本为空。";
                return false;
            }

            string text = json!;
            int nameStart = text.IndexOf("\"name\":\"", StringComparison.Ordinal);
            string name = string.Empty;
            if (nameStart >= 0)
            {
                int valueStart = nameStart + "\"name\":\"".Length;
                int valueEnd = FindStringEnd(text, valueStart);
                if (valueEnd < 0)
                {
                    error = "name 字段的字符串未闭合。";
                    return false;
                }

                name = Unescape(text.Substring(valueStart, valueEnd - valueStart));
            }

            int stepsStart = text.IndexOf("\"steps\":[", StringComparison.Ordinal);
            if (stepsStart < 0)
            {
                error = "缺少 steps 数组。";
                return false;
            }

            var steps = new List<GuideDemoStep>();
            int cursor = stepsStart + "\"steps\":[".Length;
            while (cursor < text.Length)
            {
                int open = text.IndexOf('{', cursor);
                int close = text.IndexOf('}', cursor);
                if (open < 0)
                {
                    break;
                }

                if (close < 0 || close < open)
                {
                    error = "steps 数组元素未闭合。";
                    return false;
                }

                string body = text.Substring(open + 1, close - open - 1);
                if (!TryReadInt(body, "kind", out int kind)
                    || !TryReadInt(body, "value", out int value)
                    || !TryReadInt(body, "targetId", out int targetId)
                    || !TryReadInt(body, "delayMs", out int delayMs))
                {
                    error = "steps 元素缺少 kind/value/targetId/delayMs 字段。";
                    return false;
                }

                steps.Add(new GuideDemoStep(kind, value, targetId, delayMs));
                cursor = close + 1;
            }

            script = new GuideDemoScript(name, steps.ToArray());
            error = string.Empty;
            return true;
        }

        private static bool TryReadInt(string body, string field, out int value)
        {
            value = 0;
            int index = body.IndexOf("\"" + field + "\":", StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            int start = index + field.Length + 3;
            int end = start;
            while (end < body.Length && (body[end] == '-' || (body[end] >= '0' && body[end] <= '9')))
            {
                end++;
            }

            if (end == start)
            {
                return false;
            }

            return int.TryParse(
                body.Substring(start, end - start),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static int FindStringEnd(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (text[i] == '"')
                {
                    return i;
                }
            }

            return -1;
        }

        private static string Unescape(string value)
        {
            return value.Replace("\\\\", "\\").Replace("\\\"", "\"");
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}