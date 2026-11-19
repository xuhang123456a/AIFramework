#nullable enable
namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 商店商品行：<c>TbShopItem</c> 的一行，纯数据、无行为。
    /// 价格与货币由配置决定，切片内不得再写折扣/限购逻辑（Phase 1）。
    /// </summary>
    public readonly struct ShopItemDef
    {
        public ShopItemDef(int itemId, int priceItemId, int priceCount)
        {
            ItemId = itemId;
            PriceItemId = priceItemId;
            PriceCount = priceCount;
        }

        /// <summary>售出的商品道具 ID（见 <see cref="ShopItemIds"/>）。</summary>
        public int ItemId { get; }

        /// <summary>计价货币的道具 ID（Phase 1 金币 = <c>MetaItemIds.Gold</c>）。</summary>
        public int PriceItemId { get; }

        /// <summary>单价：单次购买要消耗的货币数量。</summary>
        public int PriceCount { get; }
    }
}
