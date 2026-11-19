namespace AIFramework.Features.Inventory
{
    /// <summary>背包条目（只读快照，UI/断言用）。</summary>
    public readonly struct InventoryEntry
    {
        /// <summary>创建一条只读背包条目。</summary>
        /// <param name="itemId">道具 ID，快照内恒为正数。</param>
        /// <param name="count">持有数量，快照内恒为正数（0 数量条目不入快照）。</param>
        public InventoryEntry(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        /// <summary>道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>持有数量。</summary>
        public int Count { get; }
    }
}
