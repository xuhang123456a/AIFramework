namespace AIFramework.Features.Merge
{
    /// <summary>
    /// 合成链的一级：<c>TbMergeItem</c> 的一行，纯数据、无行为。
    /// 等级从 1 连续到 <see cref="MergeTables.MaxLevel"/>；棋盘上的 0 保留表示空格，不属于任何一行。
    /// </summary>
    public readonly struct MergeItemDef
    {
        public MergeItemDef(int level, int itemId, int sellGold)
        {
            Level = level;
            ItemId = itemId;
            SellGold = sellGold;
        }

        /// <summary>物品等级（1..N）。</summary>
        public int Level { get; }

        /// <summary>该等级对应的道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>卖掉这一级能得到的金币；Phase 1 只做配置读取，卖出流程不实现。</summary>
        public int SellGold { get; }
    }
}
