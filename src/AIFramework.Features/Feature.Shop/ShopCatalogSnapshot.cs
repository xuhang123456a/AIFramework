#nullable enable
using System;
using System.Collections.Generic;

namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 商店目录快照：进 UI 前一次性取全量商品，<b>允许分配</b>（不在帧热路径上）。
    /// 构造时复制一份，之后外部再改传入集合也不会影响已发出的快照。
    /// </summary>
    public sealed class ShopCatalogSnapshot
    {
        private readonly ShopItemDef[] _items;

        public ShopCatalogSnapshot(IReadOnlyList<ShopItemDef> items)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            _items = new ShopItemDef[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                _items[i] = items[i];
            }
        }

        /// <summary>全部商品，顺序等于子表物理行序（不做排序，也不按 itemId 重排）。</summary>
        public IReadOnlyList<ShopItemDef> Items
        {
            get { return _items; }
        }
    }
}
