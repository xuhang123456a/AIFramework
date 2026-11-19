#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;

namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 商店 UI 大脑：把"目录 + 金币余额"翻译成 <see cref="ShopItemViewData"/> 推给视图，
    /// 并把视图点击转发成 <see cref="BuyShopItemCommand"/>。
    /// <para>
    /// 这里不算价、不扣钱、不写背包：<see cref="ShopItemViewData.Affordable"/> 只是展示标记，
    /// 能否成交永远由 <see cref="ShopHandler"/> 裁决（被拒绝时它静默返回，这里不做二次判断）。
    /// 唯一持有的字段是 <see cref="Gold"/> 这份"最近一次查询到的金币"镜像，绝不在本地加减。
    /// </para>
    /// </summary>
    public sealed class ShopPresenter
    {
        private readonly ICqeBus _bus;
        private readonly IShopView _view;
        private int _gold;

        /// <summary>装配 Presenter 并接管视图的购买点击。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="view"/> 为 <c>null</c>。</exception>
        public ShopPresenter(ICqeBus bus, IShopView view)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            _bus = bus;
            _view = view;
            _view.OnBuyClicked += HandleBuyClicked;
        }

        /// <summary>
        /// 最近一次刷新到的金币（测试/断言用）。它不是本地账本：购买后必须靠重新查询更新，
        /// 视图点击与 <see cref="Buy"/> 都不在这里做加减。
        /// </summary>
        public int Gold
        {
            get { return _gold; }
        }

        /// <summary>
        /// 查目录 + 查金币，再先 <see cref="IShopView.RenderGold"/> 后 <see cref="IShopView.RenderItems"/>。
        /// <para>
        /// 金币只查一次且与商品行数无关：空商店（子表 0 行）是合法配置，也必须照常渲染余额，
        /// 所以余额查询不能藏在"遍历每件商品"的循环里。每次刷新允许分配（非帧内热路径）。
        /// </para>
        /// </summary>
        public void Refresh()
        {
            ShopCatalogSnapshot catalog = _bus.Query<ShopCatalogQueryRequest, ShopCatalogSnapshot>(
                new ShopCatalogQueryRequest());

            // Phase 1 的计价货币固定是金币（见 CONTRACT.md §1）；多货币接 Luban 后再改成按行查 PriceItemId。
            int gold = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold)).Count;
            _gold = gold;

            IReadOnlyList<ShopItemDef> defs = catalog.Items;
            List<ShopItemViewData> items = new List<ShopItemViewData>(defs.Count);
            for (int i = 0; i < defs.Count; i++)
            {
                ShopItemDef def = defs[i];
                items.Add(new ShopItemViewData(def.ItemId, def.PriceItemId, def.PriceCount, gold >= def.PriceCount));
            }

            // 顺序契约：先金币后列表，视图在一次刷新里读到的是同一份余额推导出来的结果。
            _view.RenderGold(gold);
            _view.RenderItems(items);
        }

        /// <summary>
        /// 购买指定商品：先发 <see cref="BuyShopItemCommand"/>，再无条件刷新一次。
        /// 买没买成都要刷新——被拒绝时处理器静默返回，UI 必须回到账本里的真实状态。
        /// </summary>
        /// <param name="itemId">要购买的商品 ID，未知商品由处理器静默拒绝。</param>
        public void Buy(int itemId)
        {
            _bus.Send(new BuyShopItemCommand(itemId, 1));
            Refresh();
        }

        private void HandleBuyClicked(int itemId)
        {
            Buy(itemId);
        }
    }
}
