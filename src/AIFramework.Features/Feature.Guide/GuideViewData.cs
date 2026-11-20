#nullable enable
using System;

namespace AIFramework.Features.Guide
{
    /// <summary>
    /// 单步引导的只读展示态：配置列 + 本步是否已完成/已跳过，一次查询内合并完成。
    /// <para>快照数组按 <c>stepId</c> 升序（表解析阶段已排好），UI 直接按下标画进度点。</para>
    /// </summary>
    public readonly struct GuideStepState
    {
        public GuideStepState(
            int stepId,
            string title,
            int trigger,
            int targetId,
            bool skippable,
            bool completed,
            bool skipped)
        {
            StepId = stepId;
            Title = title;
            Trigger = trigger;
            TargetId = targetId;
            Skippable = skippable;
            Completed = completed;
            Skipped = skipped;
        }

        /// <summary>步号（配置主键，1 起连续）。</summary>
        public int StepId { get; }

        /// <summary>步标题（来自子表，非空）。</summary>
        public string Title { get; }

        /// <summary>触发类别，取值见 <see cref="GuideTriggers"/>。</summary>
        public int Trigger { get; }

        /// <summary>该触发的具体目标 id；0 表示不限定。</summary>
        public int TargetId { get; }

        /// <summary>该步是否允许跳过。</summary>
        public bool Skippable { get; }

        /// <summary>该步是否以"完成"结束。</summary>
        public bool Completed { get; }

        /// <summary>该步是否以"跳过"结束；与 <see cref="Completed"/> 互斥。</summary>
        public bool Skipped { get; }
    }

    /// <summary>
    /// 引导全量只读快照：构造后不可变。
    /// <para>
    /// <b>当前步字段（<see cref="StepId"/> / <see cref="Title"/> / <see cref="Hint"/> / <see cref="Trigger"/> /
    /// <see cref="TargetId"/> / <see cref="Skippable"/> / <see cref="Reached"/>）只在 <see cref="IsActive"/> 为 true 时有意义</b>；
    /// 引导未激活（已完成 / 已跳过全部 / 空表）时一律为 0 与空串，UI 据此走"不展示浮层"分支，不必自己判空当前步。
    /// </para>
    /// <para>
    /// 内存策略：只在查询时分配一个 <see cref="GuideStepState"/> 数组（行数由配置决定，通常十行），
    /// 切片内部状态是定长 <c>bool[行数] × 2</c> + 4 个标量，不缓存快照。
    /// </para>
    /// </summary>
    public sealed class GuideSnapshot
    {
        public GuideSnapshot(
            bool isActive,
            bool isCompleted,
            bool isSkippedAll,
            int stepId,
            string title,
            string hint,
            int trigger,
            int targetId,
            bool skippable,
            bool reached,
            int completedCount,
            GuideStepState[] steps)
        {
            if (title == null)
            {
                throw new ArgumentNullException(nameof(title));
            }

            if (hint == null)
            {
                throw new ArgumentNullException(nameof(hint));
            }

            if (steps == null)
            {
                throw new ArgumentNullException(nameof(steps));
            }

            IsActive = isActive;
            IsCompleted = isCompleted;
            IsSkippedAll = isSkippedAll;
            StepId = stepId;
            Title = title;
            Hint = hint;
            Trigger = trigger;
            TargetId = targetId;
            Skippable = skippable;
            Reached = reached;
            CompletedCount = completedCount;
            Steps = steps;
        }

        /// <summary>引导是否正在展示中（未完成、未被"不再提示"，且表非空）。</summary>
        public bool IsActive { get; }

        /// <summary>是否已走完全部步骤（空表也算已完成）。</summary>
        public bool IsCompleted { get; }

        /// <summary>用户是否选择了"不再提示"；与 <see cref="IsCompleted"/> 互不覆盖。</summary>
        public bool IsSkippedAll { get; }

        /// <summary>当前步 <c>stepId</c>；未激活时为 0。</summary>
        public int StepId { get; }

        /// <summary>当前步标题；未激活时为空串。</summary>
        public string Title { get; }

        /// <summary>当前步提示文案；未激活时为空串。</summary>
        public string Hint { get; }

        /// <summary>当前步触发类别；未激活时为 <see cref="GuideTriggers.Launch"/>。</summary>
        public int Trigger { get; }

        /// <summary>当前步触发目标 id（0 = 不限定）；未激活时为 0。</summary>
        public int TargetId { get; }

        /// <summary>当前步是否允许跳过；未激活时为 false。</summary>
        public bool Skippable { get; }

        /// <summary>当前步的触发条件是否已被观测到（决定"完成"按钮能否出现）。</summary>
        public bool Reached { get; }

        /// <summary>已完成步数（**不含**跳过的步）。</summary>
        public int CompletedCount { get; }

        /// <summary>配置表总步数（等于 <see cref="Steps"/> 长度）。</summary>
        public int TotalCount
        {
            get { return Steps.Length; }
        }

        /// <summary>全部步骤的展示态，按 <c>stepId</c> 升序。</summary>
        public GuideStepState[] Steps { get; }
    }

    /// <summary>
    /// 引导的可序列化存档快照：4 个字段，由适配层塞进 <c>SaveService</c> 的负载里。
    /// <para>
    /// <b>它是"进度"，不是"运行时状态"</b>：<c>Reached</c> 刻意不入档（触发事实在读档后必须重新观测），
    /// 也不带当前步的标题/提示——那些永远从配置表重新读，避免存档里出现可用于欺骗 UI 的文案。
    /// </para>
    /// <para>
    /// 位图语义：<c>completedMask</c> / <c>skippedMask</c> 的第 <c>i</c> 位（0 起）对应子表第 <c>i</c> 行，
    /// 即 <c>stepId = i + 1</c>。表步数上限 31（见 <c>GuideTables</c> 的 MaxSteps：int 位图的最高位是符号位），所以 int 位图够用。
    /// </para>
    /// </summary>
    public readonly struct GuideSaveState
    {
        public GuideSaveState(int currentStepId, int completedMask, int skippedMask, bool skippedAll)
        {
            CurrentStepId = currentStepId;
            CompletedMask = completedMask;
            SkippedMask = skippedMask;
            SkippedAll = skippedAll;
        }

        /// <summary>存档时的当前步 <c>stepId</c>；0 表示引导已结束（完成 / 空表）。</summary>
        public int CurrentStepId { get; }

        /// <summary>已完成步的位图（第 i 位 = stepId i+1）。</summary>
        public int CompletedMask { get; }

        /// <summary>已跳过步的位图（第 i 位 = stepId i+1）。</summary>
        public int SkippedMask { get; }

        /// <summary>用户是否点过"不再提示"。</summary>
        public bool SkippedAll { get; }
    }
}
