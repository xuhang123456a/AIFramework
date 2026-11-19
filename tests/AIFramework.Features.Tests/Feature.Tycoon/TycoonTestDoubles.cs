using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.Tycoon;

namespace AIFramework.Features.Tests.Tycoon
{
    /// <summary><c>TbTycoonBuilding</c> 的一行夹具数据。</summary>
    public readonly struct TycoonRowFixture
    {
        public TycoonRowFixture(int buildingId, TycoonBuildingKind kind, int buildCost, int baseIncome, int maxLevel)
        {
            BuildingId = buildingId;
            Kind = kind;
            BuildCost = buildCost;
            BaseIncome = baseIncome;
            MaxLevel = maxLevel;
        }

        /// <summary>建筑配置 ID。</summary>
        public int BuildingId { get; }

        /// <summary>建筑类型。</summary>
        public TycoonBuildingKind Kind { get; }

        /// <summary>建造成本。</summary>
        public int BuildCost { get; }

        /// <summary>1 级 0 员工时的每 tick 基础收入。</summary>
        public int BaseIncome { get; }

        /// <summary>等级上限。</summary>
        public int MaxLevel { get; }
    }

    /// <summary>
    /// 模拟经营子表加载桩：按 CONTRACT.md §3 的 Phase 1 夹具格式造 <c>TbTycoonBuilding</c> 字节，
    /// 也能通过不登记任何表来模拟"表缺失"。只属于本切片测试，不改共享 <c>Fakes/</c>。
    /// </summary>
    public sealed class TycoonLoaderStub : IBinaryTableLoader
    {
        /// <summary>建筑子表的表名（与切片内常量一致）。</summary>
        public const string TableName = "TbTycoonBuilding";

        private const int HeaderBytes = 4;
        private const int RowBytes = 20;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public TycoonLoaderStub()
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
        public static TycoonLoaderStub Missing()
        {
            return new TycoonLoaderStub();
        }

        /// <summary>按给定行顺序写一张合法 <c>TbTycoonBuilding</c>。</summary>
        public static TycoonLoaderStub WithRows(params TycoonRowFixture[] rows)
        {
            return new TycoonLoaderStub().AddTable(BuildPayload(rows));
        }

        /// <summary>登记（或覆盖）建筑子表；传 <c>null</c> 表示该表缺失。</summary>
        public TycoonLoaderStub AddTable(byte[]? payload)
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
        public static byte[] BuildPayload(params TycoonRowFixture[] rows)
        {
            return BuildPayloadDeclaringRows(rows.Length, rows);
        }

        /// <summary>造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。</summary>
        public static byte[] BuildPayloadDeclaringRows(int declaredRowCount, params TycoonRowFixture[] rows)
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
                WriteInt32LittleEndian(payload, offset + 4, (int)rows[i].Kind);
                WriteInt32LittleEndian(payload, offset + 8, rows[i].BuildCost);
                WriteInt32LittleEndian(payload, offset + 12, rows[i].BaseIncome);
                WriteInt32LittleEndian(payload, offset + 16, rows[i].MaxLevel);
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
    /// 背包只读查询桩：只回放测试注入的金币数，并记录每次查询的道具 ID。
    /// 扣减/入账由钱包桩结算，本类不参与任何业务判断。
    /// </summary>
    public sealed class TycoonInventoryStub : IQueryHandler<InventoryQueryRequest, InventorySnapshot>
    {
        private readonly Dictionary<int, int> _counts;
        private readonly List<int> _queriedItemIds;

        public TycoonInventoryStub()
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
    /// 记账钱包桩：记录切片发出的每一条扣款/发奖指令，并按需把它们真的落到
    /// <see cref="TycoonInventoryStub"/> 上。
    /// <para>
    /// <c>settleInventory = false</c> 用来模拟"背包延迟落账"：指令照收，金币数不立即变，
    /// 这正是断言"连续建造不超支"依赖切片内部缓存的场景。
    /// </para>
    /// </summary>
    public sealed class TycoonWalletStub : ICommandHandler<ConsumeItemCommand>, ICommandHandler<GrantItemCommand>
    {
        private readonly TycoonInventoryStub _inventory;
        private readonly bool _settleInventory;
        private readonly List<ConsumeItemCommand> _consumes;
        private readonly List<GrantItemCommand> _grants;

        public TycoonWalletStub(TycoonInventoryStub inventory, bool settleInventory)
        {
            if (inventory == null)
            {
                throw new ArgumentNullException(nameof(inventory));
            }

            _inventory = inventory;
            _settleInventory = settleInventory;
            _consumes = new List<ConsumeItemCommand>();
            _grants = new List<GrantItemCommand>();
        }

        /// <summary>按发出顺序记录的扣款指令。</summary>
        public IReadOnlyList<ConsumeItemCommand> Consumes
        {
            get { return _consumes; }
        }

        /// <summary>按发出顺序记录的发奖指令。</summary>
        public IReadOnlyList<GrantItemCommand> Grants
        {
            get { return _grants; }
        }

        /// <summary>处理扣款：只记账，不做规则。</summary>
        public void Handle(in ConsumeItemCommand command)
        {
            _consumes.Add(command);
            if (_settleInventory)
            {
                _inventory.SetCount(command.ItemId, _inventory.GetCount(command.ItemId) - command.Count);
            }
        }

        /// <summary>处理发奖：只记账，不做规则。</summary>
        public void Handle(in GrantItemCommand command)
        {
            _grants.Add(command);
            if (_settleInventory)
            {
                _inventory.SetCount(command.ItemId, _inventory.GetCount(command.ItemId) + command.Count);
            }
        }
    }

    /// <summary>
    /// 模拟经营切片的一次性装配：真实 <see cref="CqeBus"/> + 本切片私有桩（背包 / 钱包 / 表加载器）。
    /// 命令与查询一律走真实总线派发，断言只落在"发出了什么指令"与"快照内容"两处。
    /// </summary>
    public sealed class TycoonTestHarness
    {
        /// <summary>夹具里摊位的 buildingId。</summary>
        public const int StallId = 1;

        /// <summary>夹具里商店的 buildingId。</summary>
        public const int ShopId = 2;

        /// <summary>夹具里餐厅的 buildingId。</summary>
        public const int RestaurantId = 3;

        private readonly CqeBus _bus;

        private TycoonTestHarness(
            CqeBus bus,
            TycoonInventoryStub inventory,
            TycoonWalletStub wallet,
            TycoonHandler handler)
        {
            _bus = bus;
            Inventory = inventory;
            Wallet = wallet;
            Handler = handler;
        }

        /// <summary>背包查询桩（金币余额的唯一事实来源）。</summary>
        public TycoonInventoryStub Inventory { get; }

        /// <summary>记账钱包桩（切片发出的扣款/发奖记录）。</summary>
        public TycoonWalletStub Wallet { get; }

        /// <summary>被测处理器。</summary>
        public TycoonHandler Handler { get; }

        /// <summary>真实总线：测试用它的 <c>Send</c>/<c>Query</c> 走真实派发路径。</summary>
        public CqeBus Bus
        {
            get { return _bus; }
        }

        /// <summary>CONTRACT.md §3 的 3 行夹具：(1, Stall, 100, 5, 5)、(2, Shop, 300, 12, 5)、(3, Restaurant, 800, 30, 3)。</summary>
        public static TycoonRowFixture[] DefaultRows()
        {
            return new[]
            {
                new TycoonRowFixture(StallId, TycoonBuildingKind.Stall, 100, 5, 5),
                new TycoonRowFixture(ShopId, TycoonBuildingKind.Shop, 300, 12, 5),
                new TycoonRowFixture(RestaurantId, TycoonBuildingKind.Restaurant, 800, 30, 3),
            };
        }

        /// <summary>标准夹具 + 指定初始金币，扣款/发奖会真的落到背包桩上。</summary>
        public static TycoonTestHarness CreateWithDefaultRows(int gold)
        {
            return Create(TycoonLoaderStub.WithRows(DefaultRows()), gold, settleInventory: true);
        }

        /// <summary>用指定加载器装配（表缺失/畸形场景走这里）。</summary>
        public static TycoonTestHarness Create(IBinaryTableLoader loader, int gold)
        {
            return Create(loader, gold, settleInventory: true);
        }

        /// <summary>
        /// 完整装配入口。<paramref name="settleInventory"/> 为 <c>false</c> 时钱包只记录指令、
        /// 不更新背包数量，用于验证切片金币缓存的防超支作用。
        /// </summary>
        public static TycoonTestHarness Create(IBinaryTableLoader loader, int gold, bool settleInventory)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            CqeBus bus = new CqeBus();
            TycoonInventoryStub inventory = new TycoonInventoryStub();
            inventory.SetCount(MetaItemIds.Gold, gold);

            TycoonWalletStub wallet = new TycoonWalletStub(inventory, settleInventory);
            bus.RegisterCommandHandler<ConsumeItemCommand>(wallet);
            bus.RegisterCommandHandler<GrantItemCommand>(wallet);
            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(inventory);

            TycoonHandler handler = new TycoonHandler(bus, loader);
            handler.Register(bus);

            return new TycoonTestHarness(bus, inventory, wallet, handler);
        }

        /// <summary>走真实总线建造一栋建筑。</summary>
        public void Build(int buildingId)
        {
            _bus.Send(new BuildTycoonCommand(buildingId));
        }

        /// <summary>走真实总线升级一栋建筑。</summary>
        public void Upgrade(int buildingId)
        {
            _bus.Send(new UpgradeTycoonCommand(buildingId));
        }

        /// <summary>走真实总线雇一名员工。</summary>
        public void Hire(int buildingId)
        {
            _bus.Send(new HireStaffCommand(buildingId));
        }

        /// <summary>走真实总线结算经营 tick。</summary>
        public void Tick(int ticks)
        {
            _bus.Send(new TycoonTickCommand(ticks));
        }

        /// <summary>走真实总线查询快照。</summary>
        public TycoonSnapshot Snapshot()
        {
            return _bus.Query<TycoonSnapshotQueryRequest, TycoonSnapshot>(new TycoonSnapshotQueryRequest());
        }
    }
}
