using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Features.DailySignIn;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 背包装配侧的只读查询处理器。
    /// <para>
    /// 注意：查询请求体 <see cref="InventoryQueryRequest"/> 由签到切片定义（切片私有的 TQuery 实参），
    /// Phase 1 没有独立背包切片，因此由适配层代为实现处理器；将来背包切片落地后，
    /// 这个类整体移入背包切片，Console 侧不需要改。
    /// </para>
    /// </summary>
    public sealed class MockInventoryQueryHandler : IQueryHandler<InventoryQueryRequest, InventorySnapshot>
    {
        private readonly MockInventory _inventory;

        public MockInventoryQueryHandler(MockInventory inventory)
        {
            _inventory = inventory;
        }

        public InventorySnapshot Handle(in InventoryQueryRequest query)
        {
            return new InventorySnapshot(query.ItemId, _inventory.GetCount(query.ItemId));
        }
    }
}
