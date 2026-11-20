#nullable enable
using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 侧广告服务：<b>不接任何真实广告 SDK、不联网、不产出 UI</b>，只提供两条明确路径——
    /// "明确不可用"（默认：<see cref="IsAvailable"/> 为 <c>false</c>，所有调用返回
    /// <see cref="AdShowState.Unavailable"/>）与"模拟已接 SDK"（<c>available: true</c>，结算语义与
    /// <c>ConsoleAdService</c> 同构，共用 <see cref="AdShowScheduler"/>）。
    /// <para>
    /// <b>为什么默认不可用</b>：广告最容易变成"按钮亮着、点了没反应"或"没看完也发奖"。
    /// 在聚合 SDK 真正接线（初始化、隐私同意、加载队列、关闭回调）之前，
    /// 宁可让上层如实看到"广告不可用"，也不要在演示里制造"广告能看"的假象。
    /// </para>
    /// <para>
    /// <b>为什么不产出 UI</b>：本类刻意不引用 <c>UnityEngine</c>（连 <c>Debug.Log</c> 都不用），
    /// 因此它能在纯命令行下被"形状级编译校验"覆盖，也能在无头测试里被直接 <c>new</c> 出来。
    /// 真机上的展示面板/倒计时 HUD 属于后续表现层接线，不写进端口实现。
    /// </para>
    /// <para>
    /// <b>完播口径</b>：没有真实 SDK 就没有真实的"用户关闭"事件，所以可用状态下每次展示都按完播处理
    /// （对应 Console 假填充的默认行为）。<see cref="AdShowState.Skipped"/> 只有等真实 SDK 的关闭回调
    /// 接进来才可能出现——本实现绝不伪造跳过（伪造跳过等于把玩家没看的广告也记成曝光）。
    /// </para>
    /// <para>
    /// <b>隐私/合规</b>：GDPR/CCPA 同意、iOS ATT、平台聚合初始化都不在本端口表达；
    /// 这些属于真实接线的先决条件，未完成前必须保持 <see cref="IsAvailable"/> 为 <c>false</c>。
    /// </para>
    /// <para>线程模型：非线程安全。Unity 主线程串行调用。</para>
    /// </summary>
    public sealed class UnityAdService : IAdService
    {
        /// <summary>成功原因：<see cref="CanShow"/> 判定"现在可以展示"（与 Console 实现同一文本）。</summary>
        public const string ReasonReady = "ready";

        /// <summary>成功原因：已展示并完播（与 Console 实现同一文本）。</summary>
        public const string ReasonShown = "shown";

        /// <summary>失败原因：用户中途关闭（当前实现不会产生，保留给真实 SDK 回调）。</summary>
        public const string ReasonSkipped = "skipped";

        /// <summary>失败原因：该广告位还在冷却。</summary>
        public const string ReasonCoolingDown = "cooling_down";

        /// <summary>失败原因：当日配额用尽（自然日重置）。</summary>
        public const string ReasonQuotaExceeded = "quota_exceeded";

        /// <summary>失败原因：SDK 未接线 / 无填充 / 未知广告位。</summary>
        public const string ReasonUnavailable = "unavailable";

        /// <summary>展示调度器（冷却 / 配额 / 完播记账的唯一真相源）；构造后不可变。</summary>
        private readonly AdShowScheduler _scheduler;

        /// <summary>是否启用（构造后不可变；默认 <c>false</c>，与"没接 SDK"的事实一致）。</summary>
        private readonly bool _available;

        /// <summary>创建 Unity 侧广告服务。</summary>
        /// <param name="time">时间源（非 <c>null</c>；建议用 <c>UnityTimeProvider</c> 或可控的测试时钟）。</param>
        /// <param name="available">是否模拟"已接 SDK"；默认 <c>false</c>，所有调用如实返回不可用。</param>
        /// <exception cref="ArgumentNullException"><paramref name="time"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public UnityAdService(ITimeProvider time, bool available = false)
        {
            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            // 冷却/配额是纯逻辑，双端共用同一份实现：Unity 侧不复制一份"看起来差不多"的规则。
            _scheduler = new AdShowScheduler(time);
            _available = available;
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <summary>
        /// 展示调度器（只读引用）：UI 需要读 <see cref="AdShowScheduler.RemainingRewardedToday"/>
        /// 之类的剩余次数，或与宿主共享同一份冷却状态时使用。
        /// </summary>
        public AdShowScheduler Scheduler
        {
            get { return _scheduler; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 未接线 → <see cref="AdShowState.Unavailable"/>（不记账）；否则先查调度器，被冷却/配额拦下时原样返回；
        /// 可展示时记录一次消耗并返回 <see cref="AdShowState.Shown"/>（本实现没有"跳过"来源）。
        /// </remarks>
        public AdShowResult Show(in AdShowRequest request)
        {
            if (!_available)
            {
                return new AdShowResult(
                    AdShowState.Unavailable, request.Placement, request.PlacementId, ReasonUnavailable);
            }

            AdShowState state = _scheduler.CanShow(in request);
            if (state != AdShowState.Shown)
            {
                return new AdShowResult(state, request.Placement, request.PlacementId, ReasonOf(state));
            }

            // 没有真实 SDK 就没有真实的关闭回调：可用状态下按完播记账（与 Console 默认行为一致）。
            _scheduler.RecordShown(in request, completed: true);

            return new AdShowResult(AdShowState.Shown, request.Placement, request.PlacementId, ReasonShown);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 纯查询：可展示返回 <see cref="AdShowState.Shown"/>（原因 <see cref="ReasonReady"/>，表示尚未展示），
        /// 其余状态原样返回；不消耗配额、不启动冷却、无任何副作用。
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

        /// <summary>状态到诊断原因文本的映射（未知状态按不可用处理，与 Console 实现同一文本）。</summary>
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
    }
}
