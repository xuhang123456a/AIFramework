using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.Inventory;
using Xunit;

namespace AIFramework.Features.Tests.Inventory
{
    /// <summary>
    /// 背包整包查询：背包 UI 一次拿全量条目，避免逐 ID 轮询（既慢又容易读到不同步的中间态）。
    /// </summary>
    public sealed class InventoryListQueryTests
    {
        private static (CqeBus Bus, InventoryHandler Handler) Create()
        {
            var bus = new CqeBus();
            var handler = new InventoryHandler();
            handler.Register(bus);
            return (bus, handler);
        }

        [Fact]
        public void EmptyInventory_ReturnsEmptyArray()
        {
            (CqeBus bus, _) = Create();

            var query = new InventoryListQueryRequest();
            InventoryListSnapshot snapshot = bus.Query<InventoryListQueryRequest, InventoryListSnapshot>(in query);

            Assert.Empty(snapshot.Items);
        }

        [Fact]
        public void ReturnsAllItemsSortedByItemId()
        {
            (CqeBus bus, InventoryHandler handler) = Create();
            handler.Seed(3003, 3);
            handler.Seed(1, 100);
            handler.Seed(2002, 2);

            var query = new InventoryListQueryRequest();
            InventoryListSnapshot snapshot = bus.Query<InventoryListQueryRequest, InventoryListSnapshot>(in query);

            Assert.Equal(3, snapshot.Items.Length);
            Assert.Equal(1, snapshot.Items[0].ItemId);
            Assert.Equal(100, snapshot.Items[0].Count);
            Assert.Equal(2002, snapshot.Items[1].ItemId);
            Assert.Equal(3003, snapshot.Items[2].ItemId);
        }

        [Fact]
        public void ReflectsConsumedItems()
        {
            (CqeBus bus, InventoryHandler handler) = Create();
            handler.Seed(MetaItemIds.Gold, 100);

            var consume = new ConsumeItemCommand(MetaItemIds.Gold, 100, MetaReasonTags.ShopConsume);
            bus.Send(in consume);

            var query = new InventoryListQueryRequest();
            InventoryListSnapshot snapshot = bus.Query<InventoryListQueryRequest, InventoryListSnapshot>(in query);

            Assert.Empty(snapshot.Items);
        }

        [Fact]
        public void SnapshotIsDetachedFromLedger()
        {
            (CqeBus bus, InventoryHandler handler) = Create();
            handler.Seed(MetaItemIds.Gold, 10);

            var query = new InventoryListQueryRequest();
            InventoryListSnapshot first = bus.Query<InventoryListQueryRequest, InventoryListSnapshot>(in query);
            first.Items[0] = new InventoryEntrySnapshot(MetaItemIds.Gold, 999);

            InventoryListSnapshot second = bus.Query<InventoryListQueryRequest, InventoryListSnapshot>(in query);
            Assert.Equal(10, second.Items[0].Count);
        }
    }
}
