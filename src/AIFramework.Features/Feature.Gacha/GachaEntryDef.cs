#nullable enable
namespace AIFramework.Features.Gacha
{
    /// <summary>
    /// 抽卡池的一行：<c>TbGachaPool</c> 的纯数据行。
    /// 权重是整数（不做浮点概率），概率公示 = <c>Weight / TotalWeight</c>，由 UI 自行换算。
    /// </summary>
    public readonly struct GachaEntryDef
    {
        public GachaEntryDef(int itemId, int weight, bool isRare)
        {
            ItemId = itemId;
            Weight = weight;
            IsRare = isRare;
        }

        /// <summary>抽中后发放的道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>整数权重；必须为正，概率占比 = <c>Weight / GachaTables.TotalWeight</c>。</summary>
        public int Weight { get; }

        /// <summary>是否稀有项：也是保底改判时唯一允许落入的池子。</summary>
        public bool IsRare { get; }
    }
}
