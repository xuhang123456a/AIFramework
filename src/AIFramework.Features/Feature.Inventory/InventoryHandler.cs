using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;

namespace AIFramework.Features.Inventory
{
    /// <summary>
    /// 背包账本：唯一允许改写 <c>itemId → count</c> 的地方。
    /// <para>
    /// 写操作只来自 <see cref="GrantItemCommand"/> / <see cref="ConsumeItemCommand"/>，读操作只来自
    /// <see cref="InventoryQueryRequest"/> 与 <see cref="Snapshot()"/>。业务拒绝（参数非法、余额不足）
    /// 一律"账本不变 + 广播 <see cref="ItemChangeRejectedEvent"/>"，绝不抛异常；本切片唯一允许抛的异常
    /// 是总线重复注册（由 <c>CqeBus</c> 抛出，本类原样放行）。
    /// </para>
    /// <para>
    /// 构造里不注册总线：装配入口必须先调用 <see cref="Register"/>。未接入总线时收发链路不可用，
    /// 但 <c>Handle</c> 仍不抛异常——事件静默丢弃、账本照常按规则变更，用于无总线的纯账本演示。
    /// </para>
    /// </summary>
    public sealed class InventoryHandler
        : ICommandHandler<GrantItemCommand>,
          ICommandHandler<ConsumeItemCommand>,
          IQueryHandler<InventoryQueryRequest, InventorySnapshot>,
          IQueryHandler<InventoryListQueryRequest, InventoryListSnapshot>
    {
        /// <summary>空账本快照的共享实例：高频轮询时避免反复分配零长度数组。</summary>
        private static readonly InventoryEntry[] EmptyEntries = Array.Empty<InventoryEntry>();

        private readonly Dictionary<int, int> _counts;
        private ICqeBus? _bus;

        /// <summary>创建空账本；构造里不注册总线，注册只发生在 <see cref="Register"/>。</summary>
        public InventoryHandler()
        {
            _counts = new Dictionary<int, int>();
        }

        /// <summary>
        /// 注册 2 条命令处理器（<see cref="GrantItemCommand"/>、<see cref="ConsumeItemCommand"/>）
        /// 与 1 条查询处理器（<see cref="InventoryQueryRequest"/>）。
        /// 重复注册由总线抛 <see cref="InvalidOperationException"/>，本切片不吞异常。
        /// </summary>
        /// <param name="bus">装配入口注入的总线。</param>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            // 先把三条处理器挂稳再记总线：注册冲突抛出时保持"未接入总线"的原状态，避免半接入。
            bus.RegisterCommandHandler<GrantItemCommand>(this);
            bus.RegisterCommandHandler<ConsumeItemCommand>(this);
            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(this);
            bus.RegisterQueryHandler<InventoryListQueryRequest, InventoryListSnapshot>(this);
            _bus = bus;
        }

        /// <summary>发放道具：参数非法即拒绝（账本不变），合法则累加并广播事实。</summary>
        /// <param name="command">发放请求。</param>
        public void Handle(in GrantItemCommand command)
        {
            if (command.ItemId <= 0 || command.Count <= 0)
            {
                Reject(command.ItemId, command.Count, MetaReasonTags.InvalidRequest);
                return;
            }

            int current = GetCount(command.ItemId);
            _counts[command.ItemId] = SaturatingAdd(current, command.Count);
            Publish(new ItemGrantedEvent(command.ItemId, command.Count));
        }

        /// <summary>消耗道具：参数非法或余额不足即拒绝（账本不变），足够则扣减（扣到 0 删除条目）并广播事实。</summary>
        /// <param name="command">消耗请求。</param>
        public void Handle(in ConsumeItemCommand command)
        {
            if (command.ItemId <= 0 || command.Count <= 0)
            {
                Reject(command.ItemId, command.Count, MetaReasonTags.InvalidRequest);
                return;
            }

            int current = GetCount(command.ItemId);
            if (current < command.Count)
            {
                Reject(command.ItemId, command.Count, MetaReasonTags.NotEnoughItem);
                return;
            }

            int remaining = current - command.Count;
            if (remaining == 0)
            {
                // 扣到 0 即删除条目：GetCount 仍为 0，Snapshot 不会出现 0 数量条目。
                _counts.Remove(command.ItemId);
            }
            else
            {
                _counts[command.ItemId] = remaining;
            }

            Publish(new ItemConsumedEvent(command.ItemId, command.Count));
        }

        /// <summary>只读查询：未持有的道具返回数量 0 的快照，任何输入都不抛异常。</summary>
        /// <param name="query">查询请求。</param>
        /// <returns>该道具的当前数量快照。</returns>
        public InventorySnapshot Handle(in InventoryQueryRequest query)
        {
            return new InventorySnapshot(query.ItemId, GetCount(query.ItemId));
        }

        /// <summary>整包查询：背包 UI 一次拿全，按 ItemId 升序。</summary>
        /// <param name="query">空请求体。</param>
        /// <returns>整包快照（无道具时返回空数组）。</returns>
        public InventoryListSnapshot Handle(in InventoryListQueryRequest query)
        {
            IReadOnlyList<InventoryEntry> entries = Snapshot();
            var items = new InventoryEntrySnapshot[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                items[i] = new InventoryEntrySnapshot(entries[i].ItemId, entries[i].Count);
            }

            return new InventoryListSnapshot(items);
        }

        /// <summary>取某道具的持有数量；未持有或 ID 非法一律返回 0。</summary>
        /// <param name="itemId">道具 ID。</param>
        /// <returns>持有数量，恒为非负数。</returns>
        public int GetCount(int itemId)
        {
            if (itemId <= 0)
            {
                return 0;
            }

            return _counts.TryGetValue(itemId, out int count) ? count : 0;
        }

        /// <summary>
        /// 账本只读快照：按 <see cref="InventoryEntry.ItemId"/> 升序、不含 0 数量条目、每次返回独立数组。
        /// 调用方改返回值不影响内部账本，顺序只由 ItemId 决定（不泄漏字典枚举顺序）。
        /// </summary>
        /// <returns>升序排列的只读条目列表，空账本返回零长度列表。</returns>
        public IReadOnlyList<InventoryEntry> Snapshot()
        {
            if (_counts.Count == 0)
            {
                return EmptyEntries;
            }

            int[] itemIds = new int[_counts.Count];
            _counts.Keys.CopyTo(itemIds, 0);
            Array.Sort(itemIds);

            InventoryEntry[] entries = new InventoryEntry[itemIds.Length];
            for (int i = 0; i < itemIds.Length; i++)
            {
                int itemId = itemIds[i];
                entries[i] = new InventoryEntry(itemId, _counts[itemId]);
            }

            return entries;
        }

        /// <summary>
        /// 仅供演示/测试注入初始账本：<paramref name="count"/> 大于 0 时覆盖为指定数量，
        /// 小于等于 0 时删除该条目；<paramref name="itemId"/> 非法时静默忽略。生产装配禁止调用。
        /// </summary>
        /// <param name="itemId">道具 ID。</param>
        /// <param name="count">注入数量。</param>
        public void Seed(int itemId, int count)
        {
            if (itemId <= 0)
            {
                return;
            }

            if (count <= 0)
            {
                _counts.Remove(itemId);
                return;
            }

            _counts[itemId] = count;
        }

        /// <summary>广播拒绝事实：账本不变，只发事件。</summary>
        /// <param name="itemId">请求里的道具 ID。</param>
        /// <param name="count">请求里的数量。</param>
        /// <param name="reasonTagId">拒绝原因标签（<see cref="MetaReasonTags"/>）。</param>
        private void Reject(int itemId, int count, int reasonTagId)
        {
            Publish(new ItemChangeRejectedEvent(itemId, count, reasonTagId));
        }

        /// <summary>统一出口：未接入总线时静默丢弃，已接入则原样广播。</summary>
        /// <typeparam name="TEvent">事件类型。</typeparam>
        /// <param name="evt">事件内容。</param>
        private void Publish<TEvent>(in TEvent evt)
            where TEvent : struct, IEvent
        {
            ICqeBus? bus = _bus;
            if (bus == null)
            {
                return;
            }

            bus.Publish(in evt);
        }

        /// <summary>饱和加法：账本上限为 <see cref="int.MaxValue"/>，绝不回绕成负数。</summary>
        /// <param name="current">当前数量（非负）。</param>
        /// <param name="delta">本次增量（正数）。</param>
        /// <returns>累加结果，超出上限时封顶。</returns>
        private static int SaturatingAdd(int current, int delta)
        {
            int headroom = int.MaxValue - current;
            return delta > headroom ? int.MaxValue : current + delta;
        }
    }
}
