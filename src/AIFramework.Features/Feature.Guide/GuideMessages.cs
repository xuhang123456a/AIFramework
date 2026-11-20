#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Guide
{
    /// <summary>
    /// 完成当前引导步：由"玩家真的做完了这一步"的上报方发出（UI 点击完成、教学关结算等）。
    /// <para>
    /// 规则：<see cref="StepId"/> 必须等于当前步、且当前步已 <c>Reached</c>；
    /// 否则静默忽略（不抛、不改状态、不发事件）。命令通道没有返回值。
    /// </para>
    /// </summary>
    public readonly struct GuideAdvanceCommand : ICommand
    {
        public GuideAdvanceCommand(int stepId)
        {
            StepId = stepId;
        }

        /// <summary>被完成的那一步的 <c>stepId</c>（等于当前步才生效）。</summary>
        public int StepId { get; }
    }

    /// <summary>
    /// 跳过当前引导步：只在该步 <c>skippable=1</c> 时生效。
    /// <para>
    /// 规则：<see cref="StepId"/> 必须等于当前步、且该步允许跳过；否则静默忽略。
    /// 跳过与完成是两种不同的结束方式（快照里 <c>skipped</c> 与 <c>completed</c> 分开记）。
    /// </para>
    /// </summary>
    public readonly struct GuideSkipCommand : ICommand
    {
        public GuideSkipCommand(int stepId)
        {
            StepId = stepId;
        }

        /// <summary>被跳过的那一步的 <c>stepId</c>（等于当前步才生效）。</summary>
        public int StepId { get; }
    }

    /// <summary>
    /// 用户选择"不再提示"：把引导整体标记为已跳过并置为非激活。
    /// <para>
    /// 规则：<c>IsSkippedAll=true</c>、<c>IsActive=false</c>；**不发任何事件**（这是用户的拒绝，不是玩法事实）。
    /// 已结束（完成 / 空表）或已跳过的引导再收到本命令静默忽略（幂等）。
    /// </para>
    /// </summary>
    public readonly struct GuideSkipAllCommand : ICommand
    {
    }

    /// <summary>
    /// 外部事实触发：由装配层把"进大厅 / 开商店 / 打完一局 / 领签到 / 到等级"翻译成本命令。
    /// <para>
    /// 命中当前步条件（<c>trigger</c> 相等，且该步 <c>targetId</c> 为 0 或与 <see cref="TargetId"/> 相等）时
    /// 只把当前步标记为 <c>Reached</c> 并广播 <see cref="GuideStepChangedEvent"/>；**不自动推进**——
    /// "做完了"必须由 <see cref="GuideAdvanceCommand"/> 显式上报，避免把"看到了"当成"做到了"。
    /// </para>
    /// </summary>
    public readonly struct GuideTriggerCommand : ICommand
    {
        public GuideTriggerCommand(int trigger, int targetId)
        {
            Trigger = trigger;
            TargetId = targetId;
        }

        /// <summary>事实类别，取值见 <see cref="GuideTriggers"/>。</summary>
        public int Trigger { get; }

        /// <summary>该事实的具体目标 id（如商店商品 id、签到天序号）；0 表示"不限定目标"。</summary>
        public int TargetId { get; }
    }

    /// <summary>引导态变化事实：触发命中（首次）或切换到下一步时各广播一次。</summary>
    public readonly struct GuideStepChangedEvent : IEvent
    {
        public GuideStepChangedEvent(int stepId, string title)
        {
            StepId = stepId;
            Title = title;
        }

        /// <summary>变化后（或刚被命中）的那一步的 <c>stepId</c>。</summary>
        public int StepId { get; }

        /// <summary>该步展示标题，UI 直接拿来刷浮层，不必再查表。</summary>
        public string Title { get; }
    }

    /// <summary>引导走完全部步骤的事实：只在最后一步"完成"或"跳过"之后广播一次，不带任何数据。</summary>
    public readonly struct GuideCompletedEvent : IEvent
    {
    }

    /// <summary>引导只读查询端口：UI 只问"引导现在长什么样"，不持有内部状态对象。</summary>
    public interface IGuideQuery : IQuery<GuideSnapshot>
    {
    }

    /// <summary>引导快照查询请求：无参数。</summary>
    public readonly struct GuideQueryRequest : IGuideQuery
    {
    }
}
