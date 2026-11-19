using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Quest
{
    /// <summary>领取指定任务的完成奖励。是否可领由切片子表 + 状态判定，不由 UI 决定。</summary>
    public readonly struct ClaimQuestRewardCommand : ICommand
    {
        public ClaimQuestRewardCommand(int questId)
        {
            QuestId = questId;
        }

        /// <summary>要领取的任务 ID。</summary>
        public int QuestId { get; }
    }

    /// <summary>任务进度真实变化事实（封顶后不再广播）。</summary>
    public readonly struct QuestProgressedEvent : IEvent
    {
        public QuestProgressedEvent(int questId, int progress, int targetCount)
        {
            QuestId = questId;
            Progress = progress;
            TargetCount = targetCount;
        }

        public int QuestId { get; }

        /// <summary>变化后的进度。</summary>
        public int Progress { get; }

        /// <summary>该任务的完成目标，供 UI 直接算百分比。</summary>
        public int TargetCount { get; }
    }

    /// <summary>任务奖励已发放事实。</summary>
    public readonly struct QuestClaimedEvent : IEvent
    {
        public QuestClaimedEvent(int questId, int rewardItemId, int rewardCount)
        {
            QuestId = questId;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        public int QuestId { get; }

        public int RewardItemId { get; }

        public int RewardCount { get; }
    }

    /// <summary>任务列表只读查询端口：UI 只问"当前所有任务长什么样"，不持有状态对象。</summary>
    public interface IQuestListQuery : IQuery<QuestListSnapshot>
    {
    }

    /// <summary>任务列表查询请求体：无参数。</summary>
    public readonly struct QuestListQueryRequest : IQuestListQuery
    {
    }
}
