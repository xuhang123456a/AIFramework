#nullable enable
namespace AIFramework.Features.Quest
{
    /// <summary>单条任务展示数据：由 Presenter 一次算好，视图只读、不回写。</summary>
    public readonly struct QuestItemViewData
    {
        public QuestItemViewData(
            int questId,
            int progress,
            int targetCount,
            bool claimed,
            bool claimable,
            int rewardItemId,
            int rewardCount)
        {
            QuestId = questId;
            Progress = progress;
            TargetCount = targetCount;
            Claimed = claimed;
            Claimable = claimable;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        /// <summary>任务 ID（领取点击回传的就是它）。</summary>
        public int QuestId { get; }

        /// <summary>当前进度，已封顶到 <see cref="TargetCount"/>。</summary>
        public int Progress { get; }

        /// <summary>完成所需进度。</summary>
        public int TargetCount { get; }

        /// <summary>是否已领取奖励。</summary>
        public bool Claimed { get; }

        /// <summary>是否现在可领（Presenter 算好）。</summary>
        public bool Claimable { get; }

        /// <summary>完成后发放的道具 ID。</summary>
        public int RewardItemId { get; }

        /// <summary>完成后发放的数量。</summary>
        public int RewardCount { get; }
    }
}
