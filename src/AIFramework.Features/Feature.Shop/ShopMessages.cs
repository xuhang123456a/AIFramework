using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 购买商品。<b>Phase 1 只支持单价购买</b>：<see cref="Count"/> 必须为 1，
    /// 其余（0、负数、大于 1）一律静默拒绝，不做部分成交也不用事件报错。
    /// </summary>
    public readonly struct BuyShopItemCommand : ICommand
    {
        public BuyShopItemCommand(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        /// <summary>商品道具 ID，对应子表 <c>TbShopItem.itemId</c>；不存在则静默拒绝。</summary>
        public int ItemId { get; }

        /// <summary>购买份数；Phase 1 恒为 1。</summary>
        public int Count { get; }
    }

    /// <summary>
    /// 购买成功事实。只有真正完成"扣款 + 发货"后才广播，
    /// 被拒绝的请求不发任何事件（静默拒绝，UI 侧不据此做提示）。
    /// </summary>
    public readonly struct ShopItemPurchasedEvent : IEvent
    {
        public ShopItemPurchasedEvent(int itemId, int count, int priceItemId, int priceCount)
        {
            ItemId = itemId;
            Count = count;
            PriceItemId = priceItemId;
            PriceCount = priceCount;
        }

        /// <summary>本次发出的商品道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>本次发出的数量；Phase 1 恒为 1。</summary>
        public int Count { get; }

        /// <summary>本次实际扣掉的货币道具 ID。</summary>
        public int PriceItemId { get; }

        /// <summary>本次实际扣掉的货币数量。</summary>
        public int PriceCount { get; }
    }

    /// <summary>商店目录只读查询端口：返回全量商品，顺序等于子表物理行序。</summary>
    public interface IShopCatalogQuery : IQuery<ShopCatalogSnapshot>
    {
    }

    /// <summary>目录查询请求体：空结构体，不需要任何参数。</summary>
    public readonly struct ShopCatalogQueryRequest : IShopCatalogQuery
    {
    }
}
