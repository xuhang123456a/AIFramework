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
    /// 控制台广告服务：**内存假填充**，用来在无头环境验收 <see cref="IAdService"/> 的全部语义
    /// （可用性降级 / 完播发奖 / 跳过不发奖 / 冷却 / 每日配额 / 排空环形日志）。
    /// <para>
    /// <b>它不是空实现</b>：真正的展示结果由 <see cref="AdShowScheduler"/> 统一记账，
    /// 本服务只负责"假 SDK 那一段"——决定这次填充是完播还是被用户关掉（<see cref="SetNextCompletion"/>），
    /// 并把每次尝试落成一行诊断日志。真实 SDK（IronSource / AdMob）只是把"是否完播"换成关闭回调、
    /// 把日志换成埋点，记住的结算语义必须与这里完全一致，否则无头测试全绿、真机上要么发不出奖励、要么被刷。
    /// </para>
    /// <para>
    /// <b>默认假填充总是完播</b>（<c>_nextCompletion = true</c>）：这样冒烟/自检里的广告链路不会因为
    /// 随机跳过而变得不可复现。<see cref="SetNextCompletion"/>(<c>false</c>) 是<b>一次性</b>脚本——
    /// 只影响下一次真正展示出去的广告，被冷却/配额拦下的调用不消费它（那些调用根本没有展示）。
    /// </para>
    /// <para>
    /// <b>横幅没有完播概念</b>：横幅永远返回 <see cref="AdShowState.Shown"/>，既不受脚本化跳过影响，
    /// 也不会消费脚本。
    /// </para>
    /// <para>
    /// <b>时间源只做装配期校验</b>：冷却与跨日全部由 <see cref="AdShowScheduler"/> 用注入的时钟计算，
    /// 本服务不再持有第二套时间源——两套时钟是"冷却永远差几秒"这类幽灵 bug 的常见来源。
    /// </para>
    /// <para>
    /// <b>日志是排空的环形缓冲</b>：只服务冒烟与排障（<see cref="DrainLog"/> 取走即清空），
    /// 不参与业务判定，因此允许在这里拼字符串（正式逻辑路径禁止动态拼接）。
    /// </para>
    /// <para>线程模型：非线程安全。展示与发奖同线程串行调用（广告不是高频路径）。</para>
    /// </summary>
    public sealed class ConsoleAdService : IAdService
    {
        /// <summary>展示日志环形容量（最近 32 条）。</summary>
        public const int LogCapacity = 32;

        /// <summary>成功原因：<see cref="CanShow"/> 判定"现在可以展示"（尚未真正展示）。</summary>
        public const string ReasonReady = "ready";

        /// <summary>成功原因：已展示并完播（激励视频在此状态下可以发奖）。</summary>
        public const string ReasonShown = "shown";

        /// <summary>失败原因：用户中途关闭（本次已消耗配额/冷却，但不得发奖）。</summary>
        public const string ReasonSkipped = "skipped";

        /// <summary>失败原因：该广告位还在冷却。</summary>
        public const string ReasonCoolingDown = "cooling_down";

        /// <summary>失败原因：当日配额用尽（自然日重置）。</summary>
        public const string ReasonQuotaExceeded = "quota_exceeded";

        /// <summary>失败原因：未接线 / 无填充 / 未知广告位。</summary>
        public const string ReasonUnavailable = "unavailable";

        /// <summary>展示调度器（冷却 / 配额 / 完播记账的唯一真相源）；构造后不可变。</summary>
        private readonly AdShowScheduler _scheduler;

        /// <summary>是否可用（构造后不可变，避免运行中"假装掉线/上线"造成不确定行为）。</summary>
        private readonly bool _available;

        /// <summary>展示日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置；满员时指向最旧的一条。</summary>
        private int _logNext;

        /// <summary>下一次真实展示是否完播（一次性脚本；默认 <c>true</c>）。</summary>
        private bool _nextCompletion = true;

        /// <summary>累计成功展示次数（含横幅；不含被拦截与跳过）。</summary>
        private int _totalShown;

        /// <summary>累计跳过次数（激励视频/插屏被用户中途关闭）。</summary>
        private int _totalSkipped;

        /// <summary>创建控制台广告服务。</summary>
        /// <param name="scheduler">展示调度器（非 <c>null</c>；可被多个入口共享，冷却/配额随之全局一致）。</param>
        /// <param name="time">时间源（非 <c>null</c>；必须与调度器同源。本服务只做装配期校验，见类注释）。</param>
        /// <param name="available">是否可用；<c>false</c> 时所有调用如实返回 <see cref="AdShowState.Unavailable"/> 且不记账。</param>
        /// <exception cref="ArgumentNullException"><paramref name="scheduler"/> 或 <paramref name="time"/> 为 <c>null</c>。</exception>
        public ConsoleAdService(AdShowScheduler scheduler, ITimeProvider time, bool available = true)
        {
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

            // 时间源与调度器必须同源；这里只校验非空并在装配期暴露"忘了传时钟"的接线错误，
            // 不保存第二份引用（否则迟早有人用它算冷却，与调度器的单调钟产生漂移）。
            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            _available = available;
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <summary>累计成功展示次数（含横幅；被拦截与跳过都不计入）。</summary>
        public int TotalShown
        {
            get { return _totalShown; }
        }

        /// <summary>累计跳过次数（用户中途关闭；只有激励视频/插屏可能发生）。</summary>
        public int TotalSkipped
        {
            get { return _totalSkipped; }
        }

        /// <summary>
        /// 脚本化下一次真实展示的结果：<c>false</c> 模拟"用户中途关闭"。
        /// <para><b>一次性</b>：只影响下一次真正展示出去的广告，随后自动回到"总是完播"；
        /// 被冷却/配额拦下的调用不消费它。横幅不受影响。</para>
        /// </summary>
        /// <param name="completed">下一次展示是否完播。</param>
        public void SetNextCompletion(bool completed)
        {
            _nextCompletion = completed;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 未接线 → <see cref="AdShowState.Unavailable"/>；否则先查调度器：被
        /// <see cref="AdShowState.CoolingDown"/> / <see cref="AdShowState.QuotaExceeded"/> /
        /// <see cref="AdShowState.Unavailable"/> 拦下时直接返回对应状态且<b>不记账</b>；
        /// 可展示时记录一次消耗，返回 <see cref="AdShowState.Shown"/>（完播）或
        /// <see cref="AdShowState.Skipped"/>（跳过）。横幅恒为 <see cref="AdShowState.Shown"/>。
        /// </remarks>
        public AdShowResult Show(in AdShowRequest request)
        {
            if (!_available)
            {
                return Blocked(AdShowState.Unavailable, ReasonUnavailable, in request);
            }

            AdShowState state = _scheduler.CanShow(in request);
            if (state != AdShowState.Shown)
            {
                return Blocked(state, ReasonOf(state), in request);
            }

            bool completed = request.Placement == AdPlacement.Banner || _nextCompletion;

            _scheduler.RecordShown(in request, completed);

            if (completed)
            {
                _totalShown++;
            }
            else
            {
                _totalSkipped++;
            }

            if (request.Placement != AdPlacement.Banner)
            {
                // 脚本只服务激励/插屏；横幅不看也从不消费它。
                _nextCompletion = true;
            }

            AdShowState resultState = completed ? AdShowState.Shown : AdShowState.Skipped;
            AppendLog(request, resultState);

            return new AdShowResult(
                resultState,
                request.Placement,
                request.PlacementId,
                completed ? ReasonShown : ReasonSkipped);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 未接线 → <see cref="AdShowState.Unavailable"/>；否则把调度器的判定映射成结果：
        /// 可展示返回 <see cref="AdShowState.Shown"/>（原因 <see cref="ReasonReady"/>，表示"尚未展示"），
        /// 冷却/超额/不可用原样返回。<b>不消耗配额、不消费脚本、不写日志</b>（UI 可能按秒轮询）。
        /// </remarks>
        public AdShowResult CanShow(in AdShowRequest request)
        {
            if (!_available)
            {
                return new AdShowResult(
                    AdShowState.Unavailable, request.Placement, request.PlacementId, ReasonUnavailable);
            }

            AdShowState state = _scheduler.CanShow(in request);
            return new AdShowResult(
                state,
                request.Placement,
                request.PlacementId,
                state == AdShowState.Shown ? ReasonReady : ReasonOf(state));
        }

        /// <summary>取走并清空展示日志（冒烟/排障用；取走即清空，避免同一批事件被重复打印）。</summary>
        /// <returns>按发生顺序排列的日志条目，形如 <c>ad#1 rewarded shown</c>。</returns>
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

        /// <summary>广告位类型的日志名（未知枚举保持 <c>placement(数值)</c>，不抛异常）。</summary>
        /// <param name="placement">广告位类型。</param>
        /// <returns>小写英文名，用于拼装诊断日志。</returns>
        public static string NameOf(AdPlacement placement)
        {
            switch (placement)
            {
                case AdPlacement.RewardedVideo: return "rewarded";
                case AdPlacement.Interstitial: return "interstitial";
                case AdPlacement.Banner: return "banner";
                default:
                    return "placement(" + ((byte)placement).ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        /// <summary>
        /// 状态到诊断原因文本的映射。未知状态值按 <see cref="ReasonUnavailable"/> 处理：
        /// <see cref="AdShowState"/> 是跨层枚举，版本不一致时宁可报告"不可用"，也不要返回空原因。
        /// </summary>
        private static string ReasonOf(AdShowState state)
        {
            switch (state)
            {
                case AdShowState.Shown: return ReasonShown;
                case AdShowState.Skipped: return ReasonSkipped;
                case AdShowState.CoolingDown: return ReasonCoolingDown;
                case AdShowState.QuotaExceeded: return ReasonQuotaExceeded;
                case AdShowState.Unavailable: return ReasonUnavailable;
                default: return ReasonUnavailable;
            }
        }

        /// <summary>构造一个被拦截的结果并记一行日志（被拦截的调用不改变任何记账状态）。</summary>
        private AdShowResult Blocked(AdShowState state, string reason, in AdShowRequest request)
        {
            AppendLog(request, state);
            return new AdShowResult(state, request.Placement, request.PlacementId, reason);
        }

        /// <summary>写入一条日志；满员时覆盖最旧的一条（仅诊断路径，允许分配）。</summary>
        private void AppendLog(in AdShowRequest request, AdShowState state)
        {
            string entry = "ad#" + request.PlacementId.ToString(CultureInfo.InvariantCulture)
                + " " + NameOf(request.Placement)
                + " " + NameOfState(state);

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

        /// <summary>状态的小写日志名（与上面的原因常量保持同一文本，便于 grep 日志与代码）。</summary>
        private static string NameOfState(AdShowState state)
        {
            switch (state)
            {
                case AdShowState.Shown: return "shown";
                case AdShowState.Skipped: return "skipped";
                case AdShowState.CoolingDown: return "cooling_down";
                case AdShowState.QuotaExceeded: return "quota_exceeded";
                case AdShowState.Unavailable: return "unavailable";
                default: return "unknown";
            }
        }
    }
}
