using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 商店业务规则：商品存在性、单价校验、余额校验与"先扣后给"的指令顺序全部收口在这里。
    /// 消息收发一律经注入的 <see cref="ICqeBus"/>，禁止直接改背包或货币数量。
    /// </summary>
    public sealed class ShopHandler : ICommandHandler<BuyShopItemCommand>,
                                      IQueryHandler<ShopCatalogQueryRequest, ShopCatalogSnapshot>
    {
        /// <summary>扣货币原因标签（因果日志用）。</summary>
        private const int ConsumeReasonTagId = 6101;

        /// <summary>发货原因标签（因果日志用）。</summary>
        private const int GrantReasonTagId = 6102;

        /// <summary>Phase 1 只支持单价购买，数量不为此值一律静默拒绝。</summary>
        private const int SupportedBuyCount = 1;

        private readonly ICqeBus _bus;
        private readonly ShopTables _tables;
        private readonly ShopCatalogSnapshot _catalog;

        public ShopHandler(ICqeBus bus, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _tables = new ShopTables(loader);
            _catalog = BuildCatalog(_tables);
        }

        /// <summary>注册命令与目录查询处理器（构造里不自动注册，装配层负责调用）。</summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<BuyShopItemCommand>(this);
            bus.RegisterQueryHandler<ShopCatalogQueryRequest, ShopCatalogSnapshot>(this);
        }

        /// <summary>
        /// 购买：数量非 1 或商品不存在 → 静默拒绝（连背包都不查）；余额不足 → 静默拒绝；
        /// 余额足够 → 严格按"先扣钱、后给货、最后广播"三步走。
        /// </summary>
        public void Handle(in BuyShopItemCommand command)
        {
            if (command.Count != SupportedBuyCount)
            {
                // Phase 1 没有部分成交，也没有"买 N 件"的价格换算：直接拒绝，不查背包。
                return;
            }

            ShopItemDef def;
            if (!TryFindItem(command.ItemId, out def))
            {
                // 未知商品：静默拒绝，不发指令、不发事件。
                return;
            }

            InventorySnapshot inventory = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(def.PriceItemId));
            if (inventory.Count < def.PriceCount)
            {
                // 货币不足：静默拒绝，状态不变。
                return;
            }

            // 顺序契约：先扣钱，再给货，两者都成功后才广播事实。
            // 顺序颠倒会让"给了货没扣钱"的脏状态出现在因果日志里，无法回放定位。
            _bus.Send(new ConsumeItemCommand(def.PriceItemId, def.PriceCount, ConsumeReasonTagId));
            _bus.Send(new GrantItemCommand(def.ItemId, SupportedBuyCount, GrantReasonTagId));
            _bus.Publish(new ShopItemPurchasedEvent(
                def.ItemId,
                SupportedBuyCount,
                def.PriceItemId,
                def.PriceCount));
        }

        /// <summary>目录查询：返回构造期冻结的快照，顺序等于子表行序。</summary>
        public ShopCatalogSnapshot Handle(in ShopCatalogQueryRequest query)
        {
            return _catalog;
        }

        /// <summary>按 itemId 线性查找商品行；子表已保证 itemId 唯一，找不到返回 <c>false</c>。</summary>
        private bool TryFindItem(int itemId, out ShopItemDef def)
        {
            int count = _tables.Count;
            for (int i = 0; i < count; i++)
            {
                ShopItemDef candidate = _tables.Get(i);
                if (candidate.ItemId == itemId)
                {
                    def = candidate;
                    return true;
                }
            }

            def = default;
            return false;
        }

        /// <summary>构造期把子表整个复制成目录快照；之后每次查询复用同一份（表不可变）。</summary>
        private static ShopCatalogSnapshot BuildCatalog(ShopTables tables)
        {
            ShopItemDef[] items = new ShopItemDef[tables.Count];
            for (int i = 0; i < tables.Count; i++)
            {
                items[i] = tables.Get(i);
            }

            return new ShopCatalogSnapshot(items);
        }
    }
}
