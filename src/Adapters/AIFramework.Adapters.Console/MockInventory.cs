using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using System;
using System.Collections.Generic;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 无头背包：Phase 1 只做内存记账，用来验证"切片发指令 → 主板入账"这条链路。
    /// 真机换成真实背包切片，Console 侧代码不需要改。
    /// </summary>
    public sealed class MockInventory : ICommandHandler<GrantItemCommand>, ICommandHandler<ConsumeItemCommand>
    {
        private readonly Dictionary<int, int> _counts = new Dictionary<int, int>();

        public int GrantCount { get; private set; }

        public int ConsumeCount { get; private set; }

        public int LastGrantedItemId { get; private set; }

        public int LastGrantedCount { get; private set; }

        public void Handle(in GrantItemCommand command)
        {
            int current;
            _counts.TryGetValue(command.ItemId, out current);
            _counts[command.ItemId] = current + command.Count;

            GrantCount++;
            LastGrantedItemId = command.ItemId;
            LastGrantedCount = command.Count;
        }

        public void Handle(in ConsumeItemCommand command)
        {
            int current;
            _counts.TryGetValue(command.ItemId, out current);
            int next = current - command.Count;
            _counts[command.ItemId] = next < 0 ? 0 : next;

            ConsumeCount++;
        }

        /// <summary>直接注入数量，仅供演示与测试准备初始状态。</summary>
        public void Seed(int itemId, int count)
        {
            _counts[itemId] = count;
        }

        public int GetCount(int itemId)
        {
            int current;
            return _counts.TryGetValue(itemId, out current) ? current : 0;
        }

        public void Reset()
        {
            _counts.Clear();
            GrantCount = 0;
            ConsumeCount = 0;
            LastGrantedItemId = 0;
            LastGrantedCount = 0;
        }

        /// <summary>把内部账本导出成可断言的快照（顺序稳定，便于打印）。</summary>
        public IReadOnlyList<KeyValuePair<int, int>> Snapshot()
        {
            var list = new List<KeyValuePair<int, int>>(_counts.Count);
            foreach (KeyValuePair<int, int> pair in _counts)
            {
                list.Add(pair);
            }

            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }
    }
}
