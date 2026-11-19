namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 单件商品展示数据：只读，视图层不得回写。
    /// <para>
    /// 视图接口 <c>IShopView</c> 由 <c>tools/viewgen</c> 从本切片的
    /// <c>Shop.bindings.json</c> 生成为 <c>IShopView.g.cs</c>（禁止手改），
    /// 本文件只承载展示数据结构。
    /// </para>
    /// </summary>
    public readonly struct ShopItemViewData
    {
        public ShopItemViewData(int itemId, int priceItemId, int priceCount, bool affordable)
        {
            ItemId = itemId;
            PriceItemId = priceItemId;
            PriceCount = priceCount;
            Affordable = affordable;
        }

        /// <summary>商品道具 ID（点击回传的就是它）。</summary>
        public int ItemId { get; }

        /// <summary>计价货币的道具 ID（Phase 1 = <c>MetaItemIds.Gold</c>）。</summary>
        public int PriceItemId { get; }

        /// <summary>单价：单次购买要消耗的货币数量。</summary>
        public int PriceCount { get; }

        /// <summary>当前金币是否买得起（Presenter 算好，视图不查账本）。</summary>
        public bool Affordable { get; }
    }
}
