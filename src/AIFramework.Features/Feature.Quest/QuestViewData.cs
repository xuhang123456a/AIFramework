#nullable enable
using System;
using System.Collections.Generic;

namespace AIFramework.Features.Quest
{
    /// <summary>单条任务的只读展示数据：一次查询内状态与配置的合并结果，UI 不再回读状态对象。</summary>
    public readonly struct QuestViewData
    {
        public QuestViewData(int questId, int progress, int targetCount, bool claimed, int rewardItemId, int rewardCount)
        {
            QuestId = questId;
            Progress = progress;
            TargetCount = targetCount;
            Claimed = claimed;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        public int QuestId { get; }

        /// <summary>当前进度，已封顶到 <see cref="TargetCount"/>。</summary>
        public int Progress { get; }

        public int TargetCount { get; }

        /// <summary>是否已领取奖励，已领取的任务不会再推进进度。</summary>
        public bool Claimed { get; }

        public int RewardItemId { get; }

        public int RewardCount { get; }
    }

    /// <summary>任务列表快照：按 <see cref="QuestViewData.QuestId"/> 升序，构造后不可变。</summary>
    public sealed class QuestListSnapshot
    {
        public QuestListSnapshot(IReadOnlyList<QuestViewData> quests)
        {
            if (quests == null)
            {
                throw new ArgumentNullException(nameof(quests));
            }

            Quests = quests;
        }

        /// <summary>按任务 ID 升序排列的展示数据。</summary>
        public IReadOnlyList<QuestViewData> Quests { get; }
    }
}
