using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.Gacha;

namespace AIFramework.Features.Tests.Gacha
{
    /// <summary><c>TbGachaPool</c> 的一行夹具数据（<c>IsRareRaw</c> 故意保留整数，便于造非法值反例）。</summary>
    internal readonly struct GachaRowFixture
    {
        internal GachaRowFixture(int itemId, int weight, bool isRare)
            : this(itemId, weight, isRare ? 1 : 0)
        {
        }

        internal GachaRowFixture(int itemId, int weight, int isRareRaw)
        {
            ItemId = itemId;
            Weight = weight;
            IsRareRaw = isRareRaw;
        }

        internal int ItemId { get; }

        internal int Weight { get; }

        internal int IsRareRaw { get; }
    }

    /// <summary>
    /// 抽卡池子表加载桩：按 CONTRACT.md 的 Phase 1 夹具格式造 <c>TbGachaPool</c> 字节，
    /// 也能通过不登记任何表来模拟"表缺失"。只属于本切片测试，不改共享 <c>Fakes/</c>。
    /// </summary>
    internal sealed class GachaLoaderStub : IBinaryTableLoader
    {
        internal const string TableName = "TbGachaPool";

        private const int HeaderBytes = 4;
        private const int RowBytes = 12;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        internal GachaLoaderStub()
        {
            _tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            _requestedTables = new List<string>();
        }

        /// <summary>被请求过的表名（按调用顺序），用于断言切片确实按名字取子表。</summary>
        internal IReadOnlyList<string> RequestedTables
        {
            get { return _requestedTables; }
        }

        /// <summary>空加载器：任何表都返回 <c>null</c>，模拟"表没打进包"。</summary>
        internal static GachaLoaderStub Missing()
        {
            return new GachaLoaderStub();
        }

        /// <summary>标准夹具：按给定行顺序写一张合法 <c>TbGachaPool</c>。</summary>
        internal static GachaLoaderStub WithRows(params GachaRowFixture[] rows)
        {
            return new GachaLoaderStub().AddTable(BuildPayload(rows));
        }

        /// <summary>登记（或覆盖）一张表；<paramref name="payload"/> 传 null 表示该表缺失。</summary>
        internal GachaLoaderStub AddTable(string tableName, byte[]? payload)
        {
            _tables[tableName] = payload;
            return this;
        }

        /// <summary>登记本切片子表。</summary>
        internal GachaLoaderStub AddTable(byte[]? payload)
        {
            return AddTable(TableName, payload);
        }

        /// <inheritdoc />
        public byte[]? Load(string tableName)
        {
            _requestedTables.Add(tableName);

            byte[]? payload;
            return _tables.TryGetValue(tableName, out payload) ? payload : null;
        }

        /// <summary>造一份"头部声明行数 = 实际行数"的合法字节。</summary>
        internal static byte[] BuildPayload(params GachaRowFixture[] rows)
        {
            return BuildPayloadDeclaringRows(rows.Length, rows);
        }

        /// <summary>造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。</summary>
        internal static byte[] BuildPayloadDeclaringRows(int declaredRowCount, params GachaRowFixture[] rows)
        {
            byte[] payload = new byte[HeaderBytes + (rows.Length * RowBytes)];
            WriteInt32LittleEndian(payload, 0, declaredRowCount);

            for (int i = 0; i < rows.Length; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                WriteInt32LittleEndian(payload, offset, rows[i].ItemId);
                WriteInt32LittleEndian(payload, offset + 4, rows[i].Weight);
                WriteInt32LittleEndian(payload, offset + 8, rows[i].IsRareRaw);
            }

            return payload;
        }

        private static void WriteInt32LittleEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }

    /// <summary>
    /// 背包只读查询桩：只回放测试注入的数量，并记录每次查询的道具 ID。
    /// 扣减由 <see cref="GachaWalletStub"/> 结算，切片只负责发指令。
    /// </summary>
    internal sealed class GachaInventoryStub : IQueryHandler<InventoryQueryRequest, InventorySnapshot>
    {
        private readonly Dictionary<int, int> _counts;
        private readonly List<int> _queriedItemIds;

        internal GachaInventoryStub()
        {
            _counts = new Dictionary<int, int>();
            _queriedItemIds = new List<int>();
        }

        /// <summary>被查询的总次数。</summary>
        internal int QueryCount
        {
            get { return _queriedItemIds.Count; }
        }

        /// <summary>按调用顺序记录的被查询道具 ID。</summary>
        internal IReadOnlyList<int> QueriedItemIds
        {
            get { return _queriedItemIds; }
        }

        internal void SetCount(int itemId, int count)
        {
            _counts[itemId] = count;
        }

        internal int GetCount(int itemId)
        {
            int count;
            return _counts.TryGetValue(itemId, out count) ? count : 0;
        }

        public InventorySnapshot Handle(in InventoryQueryRequest query)
        {
            _queriedItemIds.Add(query.ItemId);
            return new InventorySnapshot(query.ItemId, GetCount(query.ItemId));
        }
    }

    /// <summary>
    /// 可控随机源：按构造时给定的序列逐个返回，并记录每次请求的区间，
    /// 用于断言"切片只在合法的 <c>[0, TotalWeight)</c> 上取值"以及保底改判只取稀有池。
    /// 序列耗尽或返回值越界都直接抛异常——静默兜底会让测试漏掉多取/少取随机数的实现 bug。
    /// </summary>
    internal sealed class SequenceRandomSource : IRandomSource
    {
        private readonly int[] _values;
        private readonly List<int> _minInclusiveValues;
        private readonly List<int> _maxExclusiveValues;
        private int _index;

        internal SequenceRandomSource(params int[] values)
        {
            _values = values;
            _minInclusiveValues = new List<int>();
            _maxExclusiveValues = new List<int>();
        }

        /// <summary>已消耗的随机数个数。</summary>
        internal int CallCount
        {
            get { return _index; }
        }

        /// <summary>按调用顺序记录的区间下界。</summary>
        internal IReadOnlyList<int> MinInclusiveValues
        {
            get { return _minInclusiveValues; }
        }

        /// <summary>按调用顺序记录的区间上界（exclusive）。</summary>
        internal IReadOnlyList<int> MaxExclusiveValues
        {
            get { return _maxExclusiveValues; }
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (_index >= _values.Length)
            {
                throw new InvalidOperationException(
                    "随机序列已耗尽：第 " + (_index + 1) + " 次调用没有预置返回值。");
            }

            int value = _values[_index];
            if (value < minInclusive || value >= maxExclusive)
            {
                throw new InvalidOperationException(
                    "预置随机值 " + value + " 不在请求区间 [" + minInclusive + ", " + maxExclusive + ") 内。");
            }

            _minInclusiveValues.Add(minInclusive);
            _maxExclusiveValues.Add(maxExclusive);
            _index++;
            return value;
        }
    }

    /// <summary>可复现随机源（LCG，不用 <c>System.Random</c>）：同种子必然同序列，用于确定性/回放断言。</summary>
    internal sealed class SeededRandomSource : IRandomSource
    {
        private uint _state;

        internal SeededRandomSource(int seed)
        {
            _state = seed == 0 ? 1u : unchecked((uint)seed);
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "上界必须大于下界。");
            }

            // Numerical Recipes LCG 常数：确定性、无外部状态。
            _state = unchecked((_state * 1664525u) + 1013904223u);
            uint range = unchecked((uint)(maxExclusive - minInclusive));
            return minInclusive + (int)(_state % range);
        }
    }

    /// <summary>按发生顺序记录命令与事件的消息流水，用于断言"切片真实发出了什么、顺序如何"。</summary>
    internal sealed class GachaMessageRecorder
    {
        private readonly List<object> _entries;

        internal GachaMessageRecorder()
        {
            _entries = new List<object>();
        }

        internal IReadOnlyList<object> Entries
        {
            get { return _entries; }
        }

        internal void Record(object message)
        {
            _entries.Add(message);
        }

        internal List<T> Of<T>()
            where T : struct
        {
            List<T> result = new List<T>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i] is T typed)
                {
                    result.Add(typed);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 极简钱包：把 <see cref="ConsumeItemCommand"/> / <see cref="GrantItemCommand"/> 真的结算进
    /// <see cref="GachaInventoryStub"/> 并同时记录流水，这样"抽完金币真的变少"才可断言。
    /// 它只做账，不做规则：能否抽、扣多少、发什么全部由 <see cref="GachaHandler"/> 决定。
    /// </summary>
    internal sealed class GachaWalletStub : ICommandHandler<ConsumeItemCommand>, ICommandHandler<GrantItemCommand>
    {
        private readonly GachaInventoryStub _inventory;
        private readonly GachaMessageRecorder _recorder;

        internal GachaWalletStub(GachaInventoryStub inventory, GachaMessageRecorder recorder)
        {
            _inventory = inventory;
            _recorder = recorder;
        }

        void ICommandHandler<ConsumeItemCommand>.Handle(in ConsumeItemCommand command)
        {
            _recorder.Record(command);
            _inventory.SetCount(command.ItemId, _inventory.GetCount(command.ItemId) - command.Count);
        }

        void ICommandHandler<GrantItemCommand>.Handle(in GrantItemCommand command)
        {
            _recorder.Record(command);
            _inventory.SetCount(command.ItemId, _inventory.GetCount(command.ItemId) + command.Count);
        }
    }

    /// <summary>抽中事件桩：走真实 <see cref="CqeBus"/> 派发，只记录。</summary>
    internal sealed class GachaEventSpy : IEventHandler<GachaDrawnEvent>
    {
        private readonly GachaMessageRecorder _recorder;

        internal GachaEventSpy(GachaMessageRecorder recorder)
        {
            _recorder = recorder;
        }

        public void Handle(in GachaDrawnEvent evt)
        {
            _recorder.Record(evt);
        }
    }

    /// <summary>
    /// 本切片测试夹具：真实 <see cref="CqeBus"/> + 私有桩端口 + 真实 <see cref="GachaHandler"/>。
    /// 装配顺序与线上一致：先挂钱包/背包处理器，再注册切片自身。
    /// </summary>
    internal sealed class GachaHarness
    {
        private readonly GachaMessageRecorder _messages;

        private GachaHarness(
            CqeBus bus,
            GachaHandler handler,
            GachaInventoryStub inventory,
            GachaMessageRecorder messages)
        {
            Bus = bus;
            Handler = handler;
            Inventory = inventory;
            _messages = messages;
        }

        /// <summary>真实总线。</summary>
        internal CqeBus Bus { get; }

        /// <summary>被测处理器。</summary>
        internal GachaHandler Handler { get; }

        /// <summary>背包查询桩（金币余额也在这里）。</summary>
        internal GachaInventoryStub Inventory { get; }

        /// <summary>按发生顺序的消息流水。</summary>
        internal IReadOnlyList<object> Messages
        {
            get { return _messages.Entries; }
        }

        /// <summary>
        /// 用指定加载器、随机源与初始金币装配夹具。
        /// <paramref name="configureBus"/> 在切片注册之前执行，供"未知配置不影响已注册处理器"类用例挂前置处理器。
        /// </summary>
        internal static GachaHarness Create(
            IBinaryTableLoader loader,
            IRandomSource random,
            int gold,
            Action<CqeBus>? configureBus = null)
        {
            CqeBus bus = new CqeBus();
            GachaMessageRecorder messages = new GachaMessageRecorder();
            GachaInventoryStub inventory = new GachaInventoryStub();
            inventory.SetCount(MetaItemIds.Gold, gold);

            GachaWalletStub wallet = new GachaWalletStub(inventory, messages);
            bus.RegisterCommandHandler<ConsumeItemCommand>(wallet);
            bus.RegisterCommandHandler<GrantItemCommand>(wallet);
            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(inventory);
            bus.Subscribe<GachaDrawnEvent>(new GachaEventSpy(messages));

            if (configureBus != null)
            {
                configureBus(bus);
            }

            GachaHandler handler = new GachaHandler(bus, random, loader);
            handler.Register(bus);
            return new GachaHarness(bus, handler, inventory, messages);
        }

        /// <summary>走真实总线发一条抽卡命令。</summary>
        internal void Draw(int count)
        {
            Bus.Send(new DrawGachaCommand(count));
        }

        /// <summary>取指定类型的消息流水（按发生顺序）。</summary>
        internal List<T> MessagesOf<T>()
            where T : struct
        {
            return _messages.Of<T>();
        }
    }

    /// <summary>标准夹具行：权重 70/20/9/1，合计 100，最后一行是唯一稀有项。</summary>
    internal static class GachaFixture
    {
        internal const int CommonItemId = 20001;
        internal const int UncommonItemId = 20002;
        internal const int UncommonRareItemId = 20003;
        internal const int RareItemId = 20004;
        internal const int TotalWeight = 100;
        internal const int SingleDrawCost = 100;
        internal const int TenDrawCost = 900;
        internal const int ConsumeReasonTagId = 6501;
        internal const int GrantReasonTagId = 6502;

        internal static GachaRowFixture[] DefaultRows()
        {
            return new[]
            {
                new GachaRowFixture(CommonItemId, 70, false),
                new GachaRowFixture(UncommonItemId, 20, false),
                new GachaRowFixture(UncommonRareItemId, 9, false),
                new GachaRowFixture(RareItemId, 1, true),
            };
        }

        internal static GachaLoaderStub DefaultLoader()
        {
            return GachaLoaderStub.WithRows(DefaultRows());
        }
    }
}
