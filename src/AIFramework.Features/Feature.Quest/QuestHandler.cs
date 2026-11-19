#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Quest
{
    /// <summary>
    /// 任务业务规则：唯一允许改写 <see cref="QuestState"/> 的地方。
    /// <para>
    /// 跨模块通信一律走注入的 <see cref="ICqeBus"/>：进度推进 / 领奖只发 CQE 消息，
    /// 不改背包、不碰红点树（红点只发 <see cref="RedDotDeltaCommand"/> 增量）。
    /// </para>
    /// <para>
    /// 状态数组按下标与 <see cref="QuestTables"/> 对齐（表已按 questId 升序），
    /// 因此查询天然有序、<see cref="GetState"/> 可用二分查找。
    /// </para>
    /// </summary>
    public sealed class QuestHandler : IEventHandler<GameFinishedEvent>,
                                       ICommandHandler<ClaimQuestRewardCommand>,
                                       IQueryHandler<QuestListQueryRequest, QuestListSnapshot>
    {
        /// <summary>任务发奖原因标签（因果日志追溯用）。</summary>
        private const int ClaimRewardReasonTagId = 6201;

        private readonly ICqeBus _bus;
        private readonly QuestTables _tables;
        private readonly QuestState[] _states;

        /// <summary>加载子表并初始化全部任务状态（进度 0、未领取）。构造里不注册总线。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">任务子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">任务子表数据非法。</exception>
        public QuestHandler(ICqeBus bus, IBinaryTableLoader loader)
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
            _tables = new QuestTables(loader);

            QuestState[] states = new QuestState[_tables.Count];
            for (int i = 0; i < states.Length; i++)
            {
                states[i] = new QuestState(_tables.Get(i).QuestId);
            }

            _states = states;
        }

        /// <summary>
        /// 订阅 <see cref="GameFinishedEvent"/> 并注册领取命令 / 列表查询处理器。
        /// 装配入口必须传入构造本对象时的同一个总线实例（发送侧用的是构造注入的总线）。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.Subscribe<GameFinishedEvent>(this);
            bus.RegisterCommandHandler<ClaimQuestRewardCommand>(this);
            bus.RegisterQueryHandler<QuestListQueryRequest, QuestListSnapshot>(this);
        }

        /// <summary>
        /// 一局战斗结束：所有未领取任务进度 +1（封顶 <c>targetCount</c>）。
        /// 进度真变化时先广播 <see cref="QuestProgressedEvent"/>，再给红点节点累加 +1；
        /// 已到上限或已领取的任务一条消息都不发。
        /// </summary>
        public void Handle(in GameFinishedEvent evt)
        {
            // Phase 1 任务只按"局数"推进：kernelType / levelId / isVictory 均不参与过滤，
            // 需要按关卡或胜负区分时扩展子表列，而不是在这里加 if。
            for (int i = 0; i < _states.Length; i++)
            {
                QuestState state = _states[i];
                if (state.Claimed)
                {
                    continue;
                }

                QuestDef def = _tables.Get(i);
                if (state.Progress >= def.TargetCount)
                {
                    continue;
                }

                state.Progress++;
                _bus.Publish(new QuestProgressedEvent(def.QuestId, state.Progress, def.TargetCount));
                _bus.Send(new RedDotDeltaCommand(RedDotNodeIds.Quest, 1));
            }
        }

        /// <summary>
        /// 领取任务奖励。未知任务 / 未完成 / 已领取一律静默拒绝（不回消息、不改状态）；
        /// 合法时顺序固定为：发奖指令 → 已领取事件 → 红点 -1 → 落 <c>Claimed</c> 标记。
        /// </summary>
        public void Handle(in ClaimQuestRewardCommand command)
        {
            int index = IndexOfQuest(command.QuestId);
            if (index < 0)
            {
                // 未知任务：调用方给了表里没有的 ID，静默拒绝，不抛异常（异常只留给 GetState 这类编程错误）。
                return;
            }

            QuestState state = _states[index];
            if (state.Claimed)
            {
                return;
            }

            QuestDef def = _tables.Get(index);
            if (state.Progress < def.TargetCount)
            {
                return;
            }

            _bus.Send(new GrantItemCommand(def.RewardItemId, def.RewardCount, ClaimRewardReasonTagId));
            _bus.Publish(new QuestClaimedEvent(def.QuestId, def.RewardItemId, def.RewardCount));
            _bus.Send(new RedDotDeltaCommand(RedDotNodeIds.Quest, -1));
            state.Claimed = true;
        }

        /// <summary>按 questId 升序返回全部任务的展示快照（含未完成 / 已领取）。</summary>
        public QuestListSnapshot Handle(in QuestListQueryRequest query)
        {
            QuestViewData[] quests = new QuestViewData[_states.Length];
            for (int i = 0; i < _states.Length; i++)
            {
                QuestDef def = _tables.Get(i);
                QuestState state = _states[i];
                quests[i] = new QuestViewData(
                    def.QuestId,
                    state.Progress,
                    def.TargetCount,
                    state.Claimed,
                    def.RewardItemId,
                    def.RewardCount);
            }

            return new QuestListSnapshot(quests);
        }

        /// <summary>取单任务状态（活引用，测试只读）。未知 questId 属于调用方 bug，直接抛异常。</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="questId"/> 不在子表中。</exception>
        public QuestState GetState(int questId)
        {
            int index = IndexOfQuest(questId);
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(questId), questId, "任务 " + questId + " 不在 TbQuest 中。");
            }

            return _states[index];
        }

        /// <summary>二分查找任务下标（表按 questId 升序）；未命中返回 -1。</summary>
        private int IndexOfQuest(int questId)
        {
            int low = 0;
            int high = _tables.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                int middleQuestId = _tables.Get(middle).QuestId;
                if (middleQuestId == questId)
                {
                    return middle;
                }

                if (middleQuestId < questId)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return -1;
        }
    }
}
