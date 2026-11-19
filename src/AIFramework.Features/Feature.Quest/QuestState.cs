namespace AIFramework.Features.Quest
{
    /// <summary>
    /// 单任务纯数据状态（每任务一条，无业务方法，可整体序列化）。
    /// <see cref="Progress"/> 与 <see cref="Claimed"/> 只允许 <see cref="QuestHandler"/> 改写，
    /// 切片外（含测试）只能读，避免出现绕过 CQE 的隐式写入路径。
    /// </summary>
    public sealed class QuestState
    {
        public QuestState(int questId)
        {
            QuestId = questId;
        }

        /// <summary>任务 ID，对应 <see cref="QuestDef.QuestId"/>。</summary>
        public int QuestId { get; }

        /// <summary>当前进度，取值 0..<c>targetCount</c>。</summary>
        public int Progress { get; internal set; }

        /// <summary>是否已领取奖励；已领取的任务不再推进进度。</summary>
        public bool Claimed { get; internal set; }
    }
}
