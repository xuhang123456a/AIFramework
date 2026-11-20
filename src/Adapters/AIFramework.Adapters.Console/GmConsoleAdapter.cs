#nullable enable
using System;
using System.Globalization;
using System.Text;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gm;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Gm;
using AIFramework.Features.Guide;
using AIFramework.Features.Localization;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// GM 命令的可选回调集：适配层只负责"命令 → 已有能力"的翻译，**不持有任何业务状态**，
    /// 需要读统计或推进时间时一律通过这里的委托向宿主取值。
    /// <para>
    /// 全部成员可空：为 <c>null</c> 表示宿主没接这条能力，对应命令要么输出缺省占位、要么明确返回失败
    /// （如 <see cref="AdvanceTime"/> 为 <c>null</c> 且时间源不可拨时，<c>gm.advance.time</c> 报
    /// <c>time source not adjustable</c>，而不是假装成功）。
    /// </para>
    /// <para>属性可在构造适配器前后随时赋值；命令执行时才读取，因此装配顺序不敏感。</para>
    /// </summary>
    public sealed class GmConsoleHooks
    {
        /// <summary>按道具 id 查当前持有数（<c>gm.give</c> 用它回显新数量）。</summary>
        public Func<int, int>? ItemCountById { get; set; }

        /// <summary>引导当前步 id（<c>gm.stats</c> 用）。</summary>
        public Func<int>? GuideStepId { get; set; }

        /// <summary>引导已完成步数（<c>gm.stats</c> 用）。</summary>
        public Func<int>? GuideCompletedCount { get; set; }

        /// <summary>引导总步数（<c>gm.stats</c> 用）。</summary>
        public Func<int>? GuideTotalCount { get; set; }

        /// <summary>额外统计片段（如背包明细），<c>null</c> 或空串表示不追加。</summary>
        public Func<string?>? ExtraStats { get; set; }

        /// <summary>推进时间源（<c>gm.advance.time</c> 用）；为 <c>null</c> 时适配器会尝试识别可拨的 Console 时间源。</summary>
        public Action<double>? AdvanceTime { get; set; }
    }

    /// <summary>
    /// 把 Console 已有的演示能力包成 GM 命令（T9-10）：<b>gm.give</b> / <b>gm.advance.time</b> /
    /// <b>gm.set.language</b> / <b>gm.reset.guide</b> / <b>gm.stats</b> / <b>gm.help</b>。
    /// <para>
    /// <b>为什么命令都走 CQE 而不是直接改切片</b>：GM 与普通业务必须走同一条路径，否则"GM 发的东西
    /// 为什么没触发任务/成就/红点"这类问题永远查不清；走总线意味着 GM 命令天然复用切片的全部校验与事件。
    /// </para>
    /// <para>
    /// <b>它不做的事</b>：不注册任何 CQE 处理器（那是装配入口的职责），不做服务端鉴权，
    /// 不落盘审计（本层只维护内存环形缓冲，上报由宿主负责）。
    /// </para>
    /// <para>线程模型：全同步、非线程安全，由宿主主线程串行调用（与 <see cref="GmConsole"/> 一致）。</para>
    /// </summary>
    public sealed class GmConsoleAdapter
    {
        /// <summary>
        /// GM 发货原因标签（9xxx 段为 GM 预留）：因果日志/审计据此把"运营手动发放"与玩法产出区分开。
        /// </summary>
        public const int GmGrantReasonTagId = 9101;

        /// <summary>单次发放上限：防止手抖多打几个 0 把经济打崩（需求外的防御性上限）。</summary>
        public const int MaxGrantCount = 1000000;

        private readonly ICqeBus _bus;
        private readonly ITimeProvider _time;
        private readonly IGmConsole _console;

        /// <summary>
        /// 创建适配器并注册全部 GM 命令。
        /// </summary>
        /// <param name="bus">CQE 总线（非 <c>null</c>）；<c>gm.give</c> 等命令要求对应处理器已注册。</param>
        /// <param name="time">时间源（非 <c>null</c>），同时用于审计时间戳与 <c>gm.advance.time</c> 的可拨判定。</param>
        /// <param name="hooks">可选回调集（<c>null</c> = 全部能力未接，统计命令输出占位）。</param>
        /// <param name="auditCapacity">审计环形缓冲容量（默认 128）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="time"/> 为 <c>null</c>。</exception>
        public GmConsoleAdapter(
            ICqeBus bus,
            ITimeProvider time,
            GmConsoleHooks? hooks = null,
            int auditCapacity = 128)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            _bus = bus;
            _time = time;
            Hooks = hooks ?? new GmConsoleHooks();
            _console = new GmConsole(time, auditCapacity);

            _console.Register(
                new GmCommandDescriptor(
                    "gm.give",
                    "发放道具：gm.give <itemId> <count>（走 GrantItemCommand，Admin）",
                    GmPermission.Admin,
                    2),
                HandleGive);

            _console.Register(
                new GmCommandDescriptor(
                    "gm.advance.time",
                    "推进时间源 N 秒：gm.advance.time <seconds>（时间源不可拨则失败，Admin）",
                    GmPermission.Admin,
                    1),
                HandleAdvanceTime);

            _console.Register(
                new GmCommandDescriptor(
                    "gm.set.language",
                    "切换语言：gm.set.language <0=简中|1=英文|2=日文>（发 SetLanguageCommand，Support）",
                    GmPermission.Support,
                    1),
                HandleSetLanguage);

            _console.Register(
                new GmCommandDescriptor(
                    "gm.reset.guide",
                    "重置新手引导：发 GuideSkipAllCommand，等价于玩家点\"不再提示\"（Support）",
                    GmPermission.Support,
                    0),
                HandleResetGuide);

            _console.Register(
                new GmCommandDescriptor(
                    "gm.stats",
                    "查看运行时统计：命令数 / 引导进度 / 背包摘要（Player）",
                    GmPermission.Player,
                    0),
                HandleStats);

            _console.Register(
                new GmCommandDescriptor(
                    "gm.help",
                    "列出全部 GM 命令及权限、参数个数（Player）",
                    GmPermission.Player,
                    0),
                HandleHelp);
        }

        /// <summary>命令注册表 + 权限 + 审计（宿主直接持有它来 <c>Execute</c> / 读 <c>AuditLog</c>）。</summary>
        public IGmConsole Console
        {
            get { return _console; }
        }

        /// <summary>可选回调集（可在构造后继续补赋值）。</summary>
        public GmConsoleHooks Hooks { get; }

        /// <summary><c>gm.give &lt;itemId&gt; &lt;count&gt;</c>：只发 <see cref="GrantItemCommand"/>，数量回显靠宿主回调。</summary>
        private GmCommandResult HandleGive(string[] arguments)
        {
            if (!TryParseInt(arguments[0], out int itemId) || itemId <= 0)
            {
                return Fail("invalid itemId");
            }

            if (!TryParseInt(arguments[1], out int count) || count <= 0)
            {
                return Fail("invalid count");
            }

            if (count > MaxGrantCount)
            {
                return Fail("count exceeds cap " + MaxGrantCount.ToString(CultureInfo.InvariantCulture));
            }

            _bus.Send(new GrantItemCommand(itemId, count, GmGrantReasonTagId));

            Func<int, int>? itemCount = Hooks.ItemCountById;
            if (itemCount != null)
            {
                return new GmCommandResult(true, "item " + itemId + " -> " + itemCount(itemId), string.Empty);
            }

            return new GmCommandResult(true, "granted " + itemId + "x" + count, string.Empty);
        }

        /// <summary>
        /// <c>gm.advance.time &lt;seconds&gt;</c>：优先用宿主回调推进；否则识别 Console 的可拨时间源
        /// （<see cref="SystemTimeProvider"/>）；都不可拨则明确失败，绝不假装成功。
        /// </summary>
        private GmCommandResult HandleAdvanceTime(string[] arguments)
        {
            if (!double.TryParse(arguments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || double.IsNaN(seconds)
                || double.IsInfinity(seconds)
                || seconds <= 0d)
            {
                return Fail("invalid seconds");
            }

            Action<double>? advance = Hooks.AdvanceTime;
            if (advance != null)
            {
                advance(seconds);
            }
            else if (_time is SystemTimeProvider systemTime)
            {
                systemTime.Advance(seconds);
            }
            else
            {
                return Fail("time source not adjustable");
            }

            string elapsed = _time.ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            return new GmCommandResult(
                true,
                "advanced " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + "s elapsed=" + elapsed,
                string.Empty);
        }

        /// <summary><c>gm.set.language &lt;0|1|2&gt;</c>：白名单校验后发 <see cref="SetLanguageCommand"/>。</summary>
        private GmCommandResult HandleSetLanguage(string[] arguments)
        {
            if (!TryParseInt(arguments[0], out int value) || value < 0 || value > 2)
            {
                return Fail("unsupported language (0=zh,1=en,2=ja)");
            }

            var language = (GameLanguage)value;
            _bus.Send(new SetLanguageCommand(language));
            return new GmCommandResult(true, "language=" + value + "(" + language + ")", string.Empty);
        }

        /// <summary><c>gm.reset.guide</c>：发 <see cref="GuideSkipAllCommand"/>，引导切片负责幂等。</summary>
        private GmCommandResult HandleResetGuide(string[] arguments)
        {
            _bus.Send(new GuideSkipAllCommand());
            return new GmCommandResult(true, "guide skipped", string.Empty);
        }

        /// <summary><c>gm.stats</c>：命令数 + 引导进度 + 宿主给的额外片段（拿不到的部分直接省略）。</summary>
        private GmCommandResult HandleStats(string[] arguments)
        {
            var builder = new StringBuilder("stats");
            builder.Append(" commands=").Append(_console.CommandCount);

            Func<int>? stepId = Hooks.GuideStepId;
            if (stepId != null)
            {
                builder.Append(" guideStep=").Append(stepId());
            }

            Func<int>? completed = Hooks.GuideCompletedCount;
            Func<int>? total = Hooks.GuideTotalCount;
            if (completed != null && total != null)
            {
                builder.Append(" guideProgress=").Append(completed()).Append('/').Append(total());
            }

            Func<string?>? extra = Hooks.ExtraStats;
            if (extra != null)
            {
                string? text = extra();
                if (!string.IsNullOrEmpty(text))
                {
                    builder.Append(' ').Append(text);
                }
            }

            return new GmCommandResult(true, builder.ToString(), string.Empty);
        }

        /// <summary><c>gm.help</c>：列出命令名、最低权限、参数个数（描述太长不进单行回显）。</summary>
        private GmCommandResult HandleHelp(string[] arguments)
        {
            GmCommandDescriptor[] commands = _console.Commands;
            var builder = new StringBuilder("commands=").Append(commands.Length);

            for (int i = 0; i < commands.Length; i++)
            {
                GmCommandDescriptor command = commands[i];
                builder.Append(i == 0 ? " " : " | ")
                    .Append(command.Name)
                    .Append('(')
                    .Append(command.MinimumPermission)
                    .Append(',')
                    .Append(command.ArgumentCount)
                    .Append(')');
            }

            return new GmCommandResult(true, builder.ToString(), string.Empty);
        }

        /// <summary>按不变区域性解析整数：GM 命令来自脚本/手敲，不允许受宿主语言环境影响。</summary>
        private static bool TryParseInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>构造失败结果（<c>output</c> 恒为空串，避免把回显误当成功输出）。</summary>
        private static GmCommandResult Fail(string reason)
        {
            return new GmCommandResult(false, string.Empty, reason);
        }
    }
}
