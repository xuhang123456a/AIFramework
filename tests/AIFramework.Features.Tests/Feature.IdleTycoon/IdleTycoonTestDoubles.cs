using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.IdleTycoon;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.IdleTycoon
{
    /// <summary><c>TbBuilding</c> 的一行夹具数据。</summary>
    public readonly struct IdleBuildingRow
    {
        public IdleBuildingRow(int buildingId, int baseIncome, int upgradeCost, int maxLevel)
        {
            BuildingId = buildingId;
            BaseIncome = baseIncome;
            UpgradeCost = upgradeCost;
            MaxLevel = maxLevel;
        }

        /// <summary>建筑 ID。</summary>
        public int BuildingId { get; }

        /// <summary>每级每秒产出。</summary>
        public int BaseIncome { get; }

        /// <summary>升级一次的金币消耗。</summary>
        public int UpgradeCost { get; }

        /// <summary>等级上限。</summary>
        public int MaxLevel { get; }
    }

    /// <summary>
    /// 放置经营子表加载桩：按 CONTRACT.md §3 的 Phase 1 夹具格式造 <c>TbBuilding</c> 字节，
    /// 也能通过不登记任何表来模拟"表缺失"。只属于本切片测试，不改共享 <c>Fakes/</c>。
    /// </summary>
    public sealed class IdleLoaderStub : IBinaryTableLoader
    {
        /// <summary>建筑子表的表名（与切片内常量一致）。</summary>
        public const string TableName = "TbBuilding";

        private const int HeaderBytes = 4;
        private const int RowBytes = 16;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public IdleLoaderStub()
        {
            _tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            _requestedTables = new List<string>();
        }

        /// <summary>被请求过的表名（按调用顺序），用于断言切片确实按名字取子表。</summary>
        public IReadOnlyList<string> RequestedTables
        {
            get { return _requestedTables; }
        }

        /// <summary>空加载器：任何表都返回 <c>null</c>，模拟"表没打进包"。</summary>
        public static IdleLoaderStub Missing()
        {
            return new IdleLoaderStub();
        }

        /// <summary>按给定行顺序写一张合法 <c>TbBuilding</c>。</summary>
        public static IdleLoaderStub WithRows(params IdleBuildingRow[] rows)
        {
            return new IdleLoaderStub().AddTable(BuildPayload(rows));
        }

        /// <summary>登记（或覆盖）建筑子表；传 <c>null</c> 表示该表缺失。</summary>
        public IdleLoaderStub AddTable(byte[]? payload)
        {
            _tables[TableName] = payload;
            return this;
        }

        /// <inheritdoc />
        public byte[]? Load(string tableName)
        {
            _requestedTables.Add(tableName);

            byte[]? payload;
            if (_tables.TryGetValue(tableName, out payload))
            {
                return payload;
            }

            return null;
        }

        /// <summary>造一份"头部声明行数 = 实际行数"的合法字节。</summary>
        public static byte[] BuildPayload(params IdleBuildingRow[] rows)
        {
            return BuildPayloadDeclaringRows(rows.Length, rows);
        }

        /// <summary>造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。</summary>
        public static byte[] BuildPayloadDeclaringRows(int declaredRowCount, params IdleBuildingRow[] rows)
        {
            if (declaredRowCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(declaredRowCount), declaredRowCount, "行数不能为负。");
            }

            byte[] payload = new byte[HeaderBytes + (rows.Length * RowBytes)];
            WriteInt32LittleEndian(payload, 0, declaredRowCount);

            for (int i = 0; i < rows.Length; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                WriteInt32LittleEndian(payload, offset, rows[i].BuildingId);
                WriteInt32LittleEndian(payload, offset + 4, rows[i].BaseIncome);
                WriteInt32LittleEndian(payload, offset + 8, rows[i].UpgradeCost);
                WriteInt32LittleEndian(payload, offset + 12, rows[i].MaxLevel);
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
    /// 本切片自带的假时间源：<see cref="ElapsedSeconds"/> 由测试显式推进，
    /// 因此"离线收益 / 24 小时封顶 / 连续两次结算"这类用例不依赖真实墙钟，也不需要等真实时间。
    /// </summary>
    public sealed class IdleTimeStub : ITimeProvider
    {
        private static readonly DateTime BaseUtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private double _elapsedSeconds;

        public IdleTimeStub(double elapsedSeconds)
        {
            if (elapsedSeconds < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), elapsedSeconds, "起始秒数不能为负。");
            }

            _elapsedSeconds = elapsedSeconds;
            UtcNow = BaseUtcNow.AddSeconds(elapsedSeconds);
        }

        /// <inheritdoc />
        public double ElapsedSeconds
        {
            get { return _elapsedSeconds; }
        }

        /// <inheritdoc />
        public DateTime UtcNow { get; private set; }

        /// <summary>推进任意秒数（只能向前）。</summary>
        public void AdvanceSeconds(double seconds)
        {
            if (seconds < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "时间只能向前推进。");
            }

            _elapsedSeconds += seconds;
            UtcNow = UtcNow.AddSeconds(seconds);
        }

        /// <summary>推进任意小时数：离线收益用例的统一时间入口。</summary>
        public void AdvanceHours(double hours)
        {
            AdvanceSeconds(hours * 3600d);
        }

        /// <summary>直接把单调时钟拨到指定秒数（可为负向，用于验证时钟回拨的兜底）。</summary>
        public void SetElapsedSeconds(double seconds)
        {
            _elapsedSeconds = seconds;
            UtcNow = BaseUtcNow.AddSeconds(seconds);
        }
    }

    /// <summary>
    /// 背包只读查询桩：只回放测试注入的数量，从不扣减
    /// （扣减是背包切片的事，本切片只负责发 <c>ConsumeItemCommand</c>），并记录每次查询的道具 ID。
    /// </summary>
    public sealed class IdleInventoryStub : IQueryHandler<InventoryQueryRequest, InventorySnapshot>
    {
        private readonly Dictionary<int, int> _counts;
        private readonly List<int> _queriedItemIds;

        public IdleInventoryStub()
        {
            _counts = new Dictionary<int, int>();
            _queriedItemIds = new List<int>();
        }

        /// <summary>被查询的总次数。</summary>
        public int QueryCount
        {
            get { return _queriedItemIds.Count; }
        }

        /// <summary>按调用顺序记录的被查询道具 ID。</summary>
        public IReadOnlyList<int> QueriedItemIds
        {
            get { return _queriedItemIds; }
        }

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
            _queriedItemIds.Add(query.ItemId);
            return new InventorySnapshot(query.ItemId, GetCount(query.ItemId));
        }
    }

    /// <summary>
    /// 极简钱包：把 <see cref="ConsumeItemCommand"/> / <see cref="GrantItemCommand"/> 真的结算进
    /// <see cref="IdleInventoryStub"/>，这样"升级后金币真的变少 / 结算后金币真的到账"才可断言。
    /// 它只做账，不做规则：能否升级、扣多少、发多少全部由 <see cref="IdleTycoonHandler"/> 决定。
    /// </summary>
    public sealed class IdleWalletStub : ICommandHandler<ConsumeItemCommand>, ICommandHandler<GrantItemCommand>
    {
        private readonly IdleInventoryStub _inventory;

        public IdleWalletStub(IdleInventoryStub inventory)
        {
            if (inventory == null)
            {
                throw new ArgumentNullException(nameof(inventory));
            }

            _inventory = inventory;
        }

        void ICommandHandler<ConsumeItemCommand>.Handle(in ConsumeItemCommand command)
        {
            _inventory.SetCount(command.ItemId, _inventory.GetCount(command.ItemId) - command.Count);
        }

        void ICommandHandler<GrantItemCommand>.Handle(in GrantItemCommand command)
        {
            _inventory.SetCount(command.ItemId, _inventory.GetCount(command.ItemId) + command.Count);
        }
    }

    /// <summary>
    /// 本切片的一次性装配：真实 <see cref="IdleTycoonHandler"/> + 真实 <c>CqeBus</c>
    /// （经共享 <see cref="RecordingBus"/> 记录）+ 本切片自带的桩端口。
    /// </summary>
    public sealed class IdleTycoonHarness
    {
        /// <summary>夹具建筑：工厂（产金币主力，可升 3 级）。</summary>
        public const int FactoryId = 1;

        /// <summary>夹具建筑：农场（高产出，只可升 2 级）。</summary>
        public const int FarmId = 2;

        /// <summary>夹具建筑：矿场（低产出，只可升 1 级）。</summary>
        public const int MineId = 3;

        /// <summary>工厂每级每秒产出。</summary>
        public const int FactoryBaseIncome = 5;

        /// <summary>工厂单次升级费用。</summary>
        public const int FactoryUpgradeCost = 100;

        /// <summary>农场每级每秒产出。</summary>
        public const int FarmBaseIncome = 12;

        /// <summary>农场单次升级费用。</summary>
        public const int FarmUpgradeCost = 250;

        private IdleTycoonHarness(RecordingBus bus, IdleInventoryStub inventory, IdleTimeStub time, IdleTycoonHandler handler)
        {
            Bus = bus;
            Inventory = inventory;
            Time = time;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        public RecordingBus Bus { get; }

        /// <summary>背包查询桩（钱包桩把扣款/发货结算到这里）。</summary>
        public IdleInventoryStub Inventory { get; }

        /// <summary>可控时间源。</summary>
        public IdleTimeStub Time { get; }

        /// <summary>被测处理器。</summary>
        public IdleTycoonHandler Handler { get; }

        /// <summary>标准 3 行夹具：工厂 5/100/3、农场 12/250/2、矿场 1/40/1。</summary>
        public static IdleBuildingRow[] DefaultRows()
        {
            return new[]
            {
                new IdleBuildingRow(FactoryId, FactoryBaseIncome, FactoryUpgradeCost, 3),
                new IdleBuildingRow(FarmId, FarmBaseIncome, FarmUpgradeCost, 2),
                new IdleBuildingRow(MineId, 1, 40, 1),
            };
        }

        /// <summary>用标准 3 行夹具装配一套夹具（时钟从 0 秒开始）。</summary>
        public static IdleTycoonHarness CreateWithDefaultRows()
        {
            return Create(IdleLoaderStub.WithRows(DefaultRows()));
        }

        /// <summary>用指定加载器装配一套夹具（表缺失/畸形场景走这里）。</summary>
        public static IdleTycoonHarness Create(IBinaryTableLoader loader)
        {
            return Create(loader, 0d);
        }

        /// <summary>用指定加载器 + 指定起始秒数装配：用于证明实现只认 <c>ITimeProvider</c>、不认绝对时刻。</summary>
        public static IdleTycoonHarness Create(IBinaryTableLoader loader, double startElapsedSeconds)
        {
            RecordingBus bus = new RecordingBus();
            IdleInventoryStub inventory = new IdleInventoryStub();
            IdleTimeStub time = new IdleTimeStub(startElapsedSeconds);

            IdleWalletStub wallet = new IdleWalletStub(inventory);
            bus.RegisterCommandHandler<ConsumeItemCommand>(wallet);
            bus.RegisterCommandHandler<GrantItemCommand>(wallet);
            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(inventory);

            IdleTycoonHandler handler = new IdleTycoonHandler(bus, time, loader);
            handler.Register(bus);

            return new IdleTycoonHarness(bus, inventory, time, handler);
        }

        /// <summary>走真实总线发一条升级命令。</summary>
        public void Upgrade(int buildingId)
        {
            Bus.Send(new UpgradeBuildingCommand(buildingId));
        }

        /// <summary>结算一次离线收益。</summary>
        public int Settle()
        {
            return Handler.SettleOfflineIncome();
        }
    }
}
