#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Guide
{
    /// <summary>
    /// 新手引导（FTUE）表驱动步骤机：唯一允许改写引导进度的实体。
    /// <para>
    /// <b>状态机只有三态</b>：进行中（<c>IsActive</c>）、已完成（<c>IsCompleted</c>）、
    /// 用户选择不再提示（<c>IsSkippedAll</c>）；后两者都让引导失活，且互不覆盖。
    /// </para>
    /// <para>
    /// <b>观测与完成分离</b>：<see cref="GuideTriggerCommand"/> 只把当前步标成 <c>Reached</c>（触发条件已被看到），
    /// 真正的推进必须由 <see cref="GuideAdvanceCommand"/> 显式上报——把"看到了"当成"做到了"会让引导在
    /// 玩家还没操作时就自己滑过去。
    /// </para>
    /// <para>
    /// <b>内存策略</b>：内部状态是 2 个与子表同长的 <c>bool[]</c>（完成位 / 跳过位）+ 4 个标量；
    /// 查询时才分配一份 <see cref="GuideSnapshot"/>，不缓存快照、不在热路径重建字符串。
    /// </para>
    /// <para>
    /// <b>存档</b>：只提供 <see cref="CaptureState"/> / <see cref="TryRestoreState"/> 这对纯内存快照出入口，
    /// 落盘由适配层用 <c>SaveService</c> 完成——切片不碰存储端口（AIF004）。
    /// </para>
    /// </summary>
    public sealed class GuideHandler
        : ICommandHandler<GuideAdvanceCommand>,
          ICommandHandler<GuideSkipCommand>,
          ICommandHandler<GuideSkipAllCommand>,
          ICommandHandler<GuideTriggerCommand>,
          IQueryHandler<GuideQueryRequest, GuideSnapshot>
    {
        private readonly ICqeBus _bus;
        private readonly GuideTables _tables;

        /// <summary>与子表行对齐的完成位（表已按 stepId 升序，位下标 i 即 stepId i+1）。</summary>
        private readonly bool[] _completed;

        /// <summary>与子表行对齐的跳过位；与完成位互斥。</summary>
        private readonly bool[] _skipped;

        /// <summary>当前步下标（0 起）；引导结束后等于 <see cref="GuideTables.Count"/>。</summary>
        private int _currentIndex;

        /// <summary>已完成步数（不含跳过的步），与完成位保持同步。</summary>
        private int _completedCount;

        /// <summary>当前步的触发条件是否已被观测到；推进/跳过时重置。</summary>
        private bool _reached;

        /// <summary>是否已走完全部步骤（完成或逐步跳过到最后）。</summary>
        private bool _finished;

        /// <summary>用户是否点过"不再提示"。</summary>
        private bool _skippedAll;

        /// <summary>加载子表并初始化引导状态（激活第 1 步、<c>Reached=false</c>）；构造里不注册总线、不发事件。</summary>
        /// <param name="bus">切片消息总线（只用于广播事实事件）。</param>
        /// <param name="loader">子表加载端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">引导子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">引导子表数据非法。</exception>
        public GuideHandler(ICqeBus bus, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _tables = new GuideTables(loader);
            _completed = new bool[_tables.Count];
            _skipped = new bool[_tables.Count];
            _currentIndex = 0;

            // 空表是合法配置：整段引导"无事可做"即已完成，不抛异常也不留一个永远不会推进的激活态。
            _finished = _tables.Count == 0;
        }

        /// <summary>引导是否正在展示中（未完成、未被"不再提示"，且表非空）。</summary>
        public bool IsActive
        {
            get { return !_finished && !_skippedAll; }
        }

        /// <summary>是否已走完全部步骤（空表也算已完成）。</summary>
        public bool IsCompleted
        {
            get { return _finished; }
        }

        /// <summary>用户是否选择了"不再提示"。</summary>
        public bool IsSkippedAll
        {
            get { return _skippedAll; }
        }

        /// <summary>
        /// 注册推进 / 跳过 / 不再提示 / 外部触发四条命令与全量查询处理器。
        /// 装配入口必须传入构造本对象时的同一个总线实例（广播用的是构造注入的总线）。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<GuideAdvanceCommand>(this);
            bus.RegisterCommandHandler<GuideSkipCommand>(this);
            bus.RegisterCommandHandler<GuideSkipAllCommand>(this);
            bus.RegisterCommandHandler<GuideTriggerCommand>(this);
            bus.RegisterQueryHandler<GuideQueryRequest, GuideSnapshot>(this);
        }

        /// <summary>
        /// 外部事实触发：命中当前步的 <c>trigger</c>（且 <c>targetId</c> 为 0 或与命令相等）时
        /// 把当前步标成 <c>Reached</c> 并广播一次 <see cref="GuideStepChangedEvent"/>。
        /// <para>
        /// 静默忽略：引导未激活、<c>trigger</c> 不匹配、<c>targetId</c> 被限定却不相等、当前步已 <c>Reached</c>（去重，不重复刷事件）。
        /// <b>本方法不推进步骤</b>。
        /// </para>
        /// </summary>
        public void Handle(in GuideTriggerCommand command)
        {
            if (!IsActive || _reached)
            {
                return;
            }

            GuideDef def = _tables.Get(_currentIndex);
            if (command.Trigger != def.Trigger)
            {
                return;
            }

            if (def.TargetId != 0 && command.TargetId != def.TargetId)
            {
                return;
            }

            _reached = true;
            _bus.Publish(new GuideStepChangedEvent(def.StepId, def.Title));
        }

        /// <summary>
        /// 完成当前步：必须 <c>StepId</c> 等于当前步且当前步已 <c>Reached</c>，否则静默忽略
        /// （**不支持跳过触发条件直接完成**）。
        /// <para>
        /// 合法路径顺序固定：先落完成位与计数 → 再推进 → 最后广播
        /// （下一步的 <see cref="GuideStepChangedEvent"/>，或最后一步时的 <see cref="GuideCompletedEvent"/>）。
        /// </para>
        /// </summary>
        public void Handle(in GuideAdvanceCommand command)
        {
            if (!IsActive)
            {
                return;
            }

            GuideDef def = _tables.Get(_currentIndex);
            if (command.StepId != def.StepId || !_reached)
            {
                return;
            }

            _completed[_currentIndex] = true;
            _completedCount++;
            AdvanceToNextStep();
        }

        /// <summary>
        /// 跳过当前步：仅当 <c>StepId</c> 等于当前步且该步 <c>skippable=1</c> 时生效。
        /// <para>跳过的步不计入 <see cref="GuideSnapshot.CompletedCount"/>；跳过最后一步同样广播 <see cref="GuideCompletedEvent"/>。</para>
        /// </summary>
        public void Handle(in GuideSkipCommand command)
        {
            if (!IsActive)
            {
                return;
            }

            GuideDef def = _tables.Get(_currentIndex);
            if (command.StepId != def.StepId || !def.Skippable)
            {
                return;
            }

            _skipped[_currentIndex] = true;
            AdvanceToNextStep();
        }

        /// <summary>
        /// 用户选择"不再提示"：置 <c>IsSkippedAll=true</c>、<c>IsActive=false</c>，
        /// <b>不广播任何事件</b>、清空 <c>Reached</c>、保留已产生的完成/跳过位（供存档记录）。
        /// <para>静默忽略：引导已结束（完成 / 空表）或已置位（幂等）。</para>
        /// </summary>
        public void Handle(in GuideSkipAllCommand command)
        {
            if (_finished || _skippedAll)
            {
                return;
            }

            _skippedAll = true;
            _reached = false;
        }

        /// <summary>
        /// 返回引导全量只读快照。未激活时当前步字段全为 0 / 空串（见 <see cref="GuideSnapshot"/>）。
        /// </summary>
        public GuideSnapshot Handle(in GuideQueryRequest query)
        {
            GuideStepState[] steps = new GuideStepState[_tables.Count];
            for (int i = 0; i < steps.Length; i++)
            {
                GuideDef row = _tables.Get(i);
                steps[i] = new GuideStepState(
                    row.StepId,
                    row.Title,
                    row.Trigger,
                    row.TargetId,
                    row.Skippable,
                    _completed[i],
                    _skipped[i]);
            }

            if (!IsActive)
            {
                return new GuideSnapshot(
                    false,
                    _finished,
                    _skippedAll,
                    0,
                    string.Empty,
                    string.Empty,
                    GuideTriggers.Launch,
                    0,
                    false,
                    false,
                    _completedCount,
                    steps);
            }

            GuideDef def = _tables.Get(_currentIndex);
            return new GuideSnapshot(
                true,
                false,
                false,
                def.StepId,
                def.Title,
                def.Hint,
                def.Trigger,
                def.TargetId,
                def.Skippable,
                _reached,
                _completedCount,
                steps);
        }

        /// <summary>
        /// 导出可序列化的进度快照：当前步 <c>stepId</c>（未激活时 0）、完成位图、跳过位图、是否"不再提示"。
        /// <para>不包含 <c>Reached</c>：读档后触发事实必须重新观测（见 <see cref="TryRestoreState"/>）。</para>
        /// </summary>
        public GuideSaveState CaptureState()
        {
            int currentStepId = IsActive ? _tables.Get(_currentIndex).StepId : 0;
            return new GuideSaveState(currentStepId, BuildMask(_completed), BuildMask(_skipped), _skippedAll);
        }

        /// <summary>
        /// 从存档快照恢复进度：校验通过才整体覆盖内存状态并返回 true，校验不过返回 false 且状态一字不改。
        /// <para>
        /// 校验项（任一不成立即拒绝）：位图越界 / 完成位与跳过位重叠 / <c>stepId</c> 不在子表中 /
        /// 当前步本身已完成或已跳过 / 有"当前步之后"的步已有进度 / 声称已结束时仍有未结束的步 /
        /// <c>SkippedAll</c> 与当前步或空表冲突。
        /// </para>
        /// <para>
        /// <b>读档不广播事件</b>（避免重放历史给 UI），且 <c>Reached</c> 一律重置为 false：
        /// 玩家必须重新做出触发动作才能完成该步。可重复调用，每次整体覆盖。
        /// </para>
        /// </summary>
        public bool TryRestoreState(in GuideSaveState state)
        {
            int count = _tables.Count;

            if (count == 0)
            {
                // 空表只有一个合法存档：什么都没做、也没有"不再提示"可言。
                if (state.CurrentStepId != 0 || state.CompletedMask != 0 || state.SkippedMask != 0 || state.SkippedAll)
                {
                    return false;
                }

                _completedCount = 0;
                _skippedAll = false;
                _finished = true;
                _currentIndex = 0;
                _reached = false;
                return true;
            }

            if (!IsMaskWithinRange(state.CompletedMask, count) || !IsMaskWithinRange(state.SkippedMask, count))
            {
                return false;
            }

            if ((state.CompletedMask & state.SkippedMask) != 0)
            {
                // 同一步不可能既完成又跳过：位图重叠说明存档被写坏或来自另一份配置。
                return false;
            }

            int currentIndex;
            bool finished;

            if (state.SkippedAll)
            {
                if (state.CurrentStepId != 0)
                {
                    // "不再提示"是终态：存档里不该还挂着当前步。
                    return false;
                }

                currentIndex = 0;
                finished = false;
            }
            else if (state.CurrentStepId == 0)
            {
                // 没有当前步 = 引导已结束；此时必须每一步都已结束，否则就是"丢了当前步"的坏档。
                if ((state.CompletedMask | state.SkippedMask) != FullMask(count))
                {
                    return false;
                }

                currentIndex = count;
                finished = true;
            }
            else
            {
                currentIndex = state.CurrentStepId - 1;
                if (currentIndex < 0 || currentIndex >= count)
                {
                    return false;
                }

                int doneMask = state.CompletedMask | state.SkippedMask;
                int currentBit = 1 << currentIndex;
                if ((doneMask & currentBit) != 0)
                {
                    return false;
                }

                if ((doneMask & FutureMask(count, currentIndex)) != 0)
                {
                    // 步骤机是线性的：当前步之后的步不可能已经有进度。
                    return false;
                }

                finished = false;
            }

            for (int i = 0; i < count; i++)
            {
                _completed[i] = (state.CompletedMask & (1 << i)) != 0;
                _skipped[i] = (state.SkippedMask & (1 << i)) != 0;
            }

            _completedCount = CountBits(state.CompletedMask);
            _skippedAll = state.SkippedAll;
            _finished = finished;
            _currentIndex = finished ? count : currentIndex;
            _reached = false;
            return true;
        }

        /// <summary>落位后推进：跳过"已完成/已跳过"的行（读档允许出现稀疏进度），到尽头则收尾并广播完成事实。</summary>
        private void AdvanceToNextStep()
        {
            _reached = false;

            int next = _currentIndex + 1;
            while (next < _tables.Count && (_completed[next] || _skipped[next]))
            {
                next++;
            }

            if (next >= _tables.Count)
            {
                _currentIndex = _tables.Count;
                _finished = true;
                _bus.Publish(new GuideCompletedEvent());
                return;
            }

            _currentIndex = next;
            GuideDef def = _tables.Get(_currentIndex);
            _bus.Publish(new GuideStepChangedEvent(def.StepId, def.Title));
        }

        /// <summary>把与子表等长的标志位折成 int 位图（第 i 位 = stepId i+1）。</summary>
        private static int BuildMask(bool[] flags)
        {
            int mask = 0;
            for (int i = 0; i < flags.Length; i++)
            {
                if (flags[i])
                {
                    mask |= 1 << i;
                }
            }

            return mask;
        }

        /// <summary>位图是否落在 <paramref name="count"/> 步之内（负值说明最高位被置，同样非法）。</summary>
        private static bool IsMaskWithinRange(int mask, int count)
        {
            if (mask < 0)
            {
                return false;
            }

            return (mask & ~FullMask(count)) == 0;
        }

        /// <summary>低 <paramref name="count"/> 位全 1 的掩码；<paramref name="count"/> 达到 32 时返回 -1（全 1）。</summary>
        private static int FullMask(int count)
        {
            return count >= 32 ? -1 : (1 << count) - 1;
        }

        /// <summary>下标严格大于 <paramref name="index"/> 的那些位的掩码。</summary>
        private static int FutureMask(int count, int index)
        {
            return FullMask(count) & ~((1 << (index + 1)) - 1);
        }

        /// <summary>位图里 1 的个数（只用于统计已完成步数，输入保证非负）。</summary>
        private static int CountBits(int value)
        {
            int count = 0;
            int bits = value;
            while (bits != 0)
            {
                bits &= bits - 1;
                count++;
            }

            return count;
        }
    }
}
