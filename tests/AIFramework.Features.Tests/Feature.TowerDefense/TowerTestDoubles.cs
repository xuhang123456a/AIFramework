using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Tests.Fakes;
using AIFramework.Features.TowerDefense;

namespace AIFramework.Features.Tests.TowerDefense
{
    /// <summary><c>TbTower</c> 的一行夹具数据。</summary>
    public readonly struct TowerRowFixture
    {
        public TowerRowFixture(int towerId, int cost, int damage, int range)
        {
            TowerId = towerId;
            Cost = cost;
            Damage = damage;
            Range = range;
        }

        /// <summary>塔 ID。</summary>
        public int TowerId { get; }

        /// <summary>建造单价（金币）。</summary>
        public int Cost { get; }

        /// <summary>单发伤害。</summary>
        public int Damage { get; }

        /// <summary>射程（格）。</summary>
        public int Range { get; }
    }

    /// <summary>
    /// 塔防子表加载桩：按 CONTRACT.md §3 的 Phase 1 夹具格式造 <c>TbTower</c> 字节，
    /// 也能通过不登记任何表来模拟"表缺失"。只属于本切片测试，不改共享 <c>Fakes/</c>。
    /// </summary>
    public sealed class TowerLoaderStub : IBinaryTableLoader
    {
        /// <summary>塔防子表的表名（与切片内常量一致）。</summary>
        public const string TableName = TowerTables.TableNameValue;

        private const int HeaderBytes = 4;
        private const int RowBytes = 16;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public TowerLoaderStub()
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
        public static TowerLoaderStub Missing()
        {
            return new TowerLoaderStub();
        }

        /// <summary>按给定行顺序写一张合法 <c>TbTower</c>。</summary>
        public static TowerLoaderStub WithRows(params TowerRowFixture[] rows)
        {
            return new TowerLoaderStub().AddTable(BuildPayload(rows));
        }

        /// <summary>登记（或覆盖）塔防子表。</summary>
        public TowerLoaderStub AddTable(byte[]? payload)
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
        public static byte[] BuildPayload(params TowerRowFixture[] rows)
        {
            return BuildPayloadDeclaringRows(rows.Length, rows);
        }

        /// <summary>造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。</summary>
        public static byte[] BuildPayloadDeclaringRows(int declaredRowCount, params TowerRowFixture[] rows)
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
                WriteInt32LittleEndian(payload, offset, rows[i].TowerId);
                WriteInt32LittleEndian(payload, offset + 4, rows[i].Cost);
                WriteInt32LittleEndian(payload, offset + 8, rows[i].Damage);
                WriteInt32LittleEndian(payload, offset + 12, rows[i].Range);
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
    /// 背包只读查询桩：只回放测试注入的数量，从不扣减
    /// （扣减是背包切片的事，塔防切片只负责发 <c>ConsumeItemCommand</c>），并记录每次查询的道具 ID。
    /// </summary>
    public sealed class TowerInventoryStub : IQueryHandler<InventoryQueryRequest, InventorySnapshot>
    {
        private readonly Dictionary<int, int> _counts;
        private readonly List<int> _queriedItemIds;

        public TowerInventoryStub()
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
    /// 极简钱包：把 <see cref="ConsumeItemCommand"/> 真的结算进 <see cref="TowerInventoryStub"/>，
    /// 这样"建完塔金币真的变少"才可断言。
    /// </summary>
    public sealed class TowerWalletStub : ICommandHandler<ConsumeItemCommand>
    {
        private readonly TowerInventoryStub _inventory;

        public TowerWalletStub(TowerInventoryStub inventory)
        {
            if (inventory == null)
            {
                throw new ArgumentNullException(nameof(inventory));
            }

            _inventory = inventory;
        }

        /// <inheritdoc />
        public void Handle(in ConsumeItemCommand command)
        {
            _inventory.SetCount(command.ItemId, _inventory.GetCount(command.ItemId) - command.Count);
        }
    }

    /// <summary>
    /// 塔防切片的一次性装配：真实 <see cref="TowerDefenseHandler"/> + 真实 <c>CqeBus</c>
    /// （经共享 <see cref="RecordingBus"/> 记录）+ 本切片自带的桩端口。
    /// </summary>
    public sealed class TowerDefenseHarness
    {
        /// <summary>标准夹具里 1 号塔的单价。</summary>
        public const int ArrowTowerCost = 100;

        /// <summary>标准夹具里 1 号塔的伤害。</summary>
        public const int ArrowTowerDamage = 10;

        /// <summary>标准夹具里 1 号塔的射程（格）。</summary>
        public const int ArrowTowerRange = 2;

        /// <summary>标准夹具里 2 号塔的单价。</summary>
        public const int CannonTowerCost = 250;

        /// <summary>标准夹具里 2 号塔的伤害。</summary>
        public const int CannonTowerDamage = 24;

        /// <summary>标准夹具里 2 号塔的射程（格）。</summary>
        public const int CannonTowerRange = 3;

        private TowerDefenseHarness(RecordingBus bus, TowerInventoryStub inventory, TowerDefenseHandler handler)
        {
            Bus = bus;
            Inventory = inventory;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        public RecordingBus Bus { get; }

        /// <summary>背包查询桩。</summary>
        public TowerInventoryStub Inventory { get; }

        /// <summary>被测处理器。</summary>
        public TowerDefenseHandler Handler { get; }

        /// <summary>标准 2 行夹具：1 号塔 100/10/2，2 号塔 250/24/3，货币均为金币。</summary>
        public static TowerRowFixture[] DefaultRows()
        {
            return new[]
            {
                new TowerRowFixture(1, ArrowTowerCost, ArrowTowerDamage, ArrowTowerRange),
                new TowerRowFixture(2, CannonTowerCost, CannonTowerDamage, CannonTowerRange),
            };
        }

        /// <summary>用标准 2 行夹具装配一套夹具。</summary>
        public static TowerDefenseHarness CreateWithDefaultRows()
        {
            return Create(TowerLoaderStub.WithRows(DefaultRows()));
        }

        /// <summary>用指定加载器装配一套夹具（表缺失/畸形场景走这里）。</summary>
        public static TowerDefenseHarness Create(IBinaryTableLoader loader)
        {
            return Create(loader, settleWithWallet: false);
        }

        /// <summary>
        /// 用指定加载器装配一套"余额会真的变化"的夹具：扣款经 <see cref="TowerWalletStub"/> 落到
        /// <see cref="Inventory"/> 上，供断言"建完塔金币变少"。
        /// </summary>
        public static TowerDefenseHarness CreateWithWallet(IBinaryTableLoader loader)
        {
            return Create(loader, settleWithWallet: true);
        }

        private static TowerDefenseHarness Create(IBinaryTableLoader loader, bool settleWithWallet)
        {
            RecordingBus bus = new RecordingBus();
            TowerInventoryStub inventory = new TowerInventoryStub();

            // 扣款由背包切片结算：默认只提供槽位，避免真实总线因未注册抛异常；
            // 需要余额变动时改由钱包桩落账（真实总线不允许同一命令重复注册，故二选一）。
            if (settleWithWallet)
            {
                bus.RegisterCommandHandler<ConsumeItemCommand>(new TowerWalletStub(inventory));
            }
            else
            {
                bus.RegisterCommandHandler<ConsumeItemCommand>(new NoOpCommandHandler<ConsumeItemCommand>());
            }

            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(inventory);

            TowerDefenseHandler handler = new TowerDefenseHandler(bus, loader);
            handler.Register(bus);

            return new TowerDefenseHarness(bus, inventory, handler);
        }

        /// <summary>发一条建造命令。</summary>
        public void Build(int x, int y, int towerId)
        {
            Bus.Send(new BuildTowerCommand(x, y, towerId));
        }

        /// <summary>走真实总线查一次布局。</summary>
        public TowerLayoutSnapshot QueryLayout()
        {
            return Bus.Query<TowerLayoutQueryRequest, TowerLayoutSnapshot>(new TowerLayoutQueryRequest());
        }
    }
}
