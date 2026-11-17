namespace AIFramework.Contracts.Meta
{
    /// <summary>发放道具指令：由业务切片发出，由背包切片（或 Phase 1 的模拟实现）处理。</summary>
    public readonly struct GrantItemCommand : Cqe.ICommand
    {
        public GrantItemCommand(int itemId, int count, int reasonTagId)
        {
            ItemId = itemId;
            Count = count;
            ReasonTagId = reasonTagId;
        }

        public int ItemId { get; }

        public int Count { get; }

        /// <summary>发放原因数值标签，用于因果日志追溯。</summary>
        public int ReasonTagId { get; }
    }

    /// <summary>消耗道具指令。</summary>
    public readonly struct ConsumeItemCommand : Cqe.ICommand
    {
        public ConsumeItemCommand(int itemId, int count, int reasonTagId)
        {
            ItemId = itemId;
            Count = count;
            ReasonTagId = reasonTagId;
        }

        public int ItemId { get; }

        public int Count { get; }

        public int ReasonTagId { get; }
    }

    /// <summary>道具已发放事件。</summary>
    public readonly struct ItemGrantedEvent : Cqe.IEvent
    {
        public ItemGrantedEvent(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }

        public int Count { get; }
    }

    /// <summary>服务器日切事件：跨天刷新类业务统一监听它，禁止自己读系统时间判断。</summary>
    public readonly struct DayChangedEvent : Cqe.IEvent
    {
        public DayChangedEvent(int dayIndex)
        {
            DayIndex = dayIndex;
        }

        /// <summary>自纪元起的整数天序号。</summary>
        public int DayIndex { get; }
    }

    /// <summary>背包快照查询结果。</summary>
    public readonly struct InventorySnapshot
    {
        public InventorySnapshot(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }

        public int Count { get; }
    }

    /// <summary>背包只读查询端口：切片只能读数量，写必须走 Command。</summary>
    public interface IInventoryQuery : Cqe.IQuery<InventorySnapshot>
    {
        int ItemId { get; }
    }
}
