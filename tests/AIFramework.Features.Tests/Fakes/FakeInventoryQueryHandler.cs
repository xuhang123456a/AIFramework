using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Features.DailySignIn;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>
    /// 背包只读查询桩：只回放测试注入的数量，从不扣减
    /// （扣减是背包切片的事，签到切片只负责发 <c>ConsumeItemCommand</c>）。
    /// </summary>
    public sealed class FakeInventoryQueryHandler : IQueryHandler<InventoryQueryRequest, InventorySnapshot>
    {
        private readonly Dictionary<int, int> _counts;

        public FakeInventoryQueryHandler()
        {
            _counts = new Dictionary<int, int>();
        }

        /// <summary>被查询次数：用于断言"补签先读背包，再判断周期边界"，顺序不得颠倒。</summary>
        public int QueryCount { get; private set; }

        /// <summary>注入某道具的数量。</summary>
        public void SetCount(int itemId, int count)
        {
            _counts[itemId] = count;
        }

        /// <summary>读取当前注入的数量（未注入按 0 处理）。</summary>
        public int GetCount(int itemId)
        {
            int count;
            return _counts.TryGetValue(itemId, out count) ? count : 0;
        }

        /// <inheritdoc />
        public InventorySnapshot Handle(in InventoryQueryRequest query)
        {
            QueryCount++;
            return new InventorySnapshot(query.ItemId, GetCount(query.ItemId));
        }
    }
}
