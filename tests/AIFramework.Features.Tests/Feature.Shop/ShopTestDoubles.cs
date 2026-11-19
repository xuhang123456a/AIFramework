using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Shop;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.Shop
{
    /// <summary><c>TbShopItem</c> 的一行夹具数据。</summary>
    public readonly struct ShopRowFixture
    {
        public ShopRowFixture(int itemId, int priceItemId, int priceCount)
        {
            ItemId = itemId;
            PriceItemId = priceItemId;
            PriceCount = priceCount;
        }

        /// <summary>商品道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>计价货币道具 ID。</summary>
        public int PriceItemId { get; }

        /// <summary>单价。</summary>
        public int PriceCount { get; }
    }

    /// <summary>
    /// 商店子表加载桩：按 CONTRACT.md §3 的 Phase 1 夹具格式造 <c>TbShopItem</c> 字节，
    /// 也能通过不登记任何表来模拟"表缺失"。只属于本切片测试，不改共享 <c>Fakes/</c>。
    /// </summary>
    public sealed class ShopLoaderStub : IBinaryTableLoader
    {
        /// <summary>商店子表的表名（与切片内常量一致）。</summary>
        public const string TableName = "TbShopItem";

        private const int HeaderBytes = 4;
        private const int RowBytes = 12;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public ShopLoaderStub()
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
        public static ShopLoaderStub Missing()
        {
            return new ShopLoaderStub();
        }

        /// <summary>标准夹具：按给定行顺序写一张合法 <c>TbShopItem</c>。</summary>
        public static ShopLoaderStub WithRows(params ShopRowFixture[] rows)
        {
            return new ShopLoaderStub().AddTable(BuildPayload(rows));
        }

        /// <summary>登记（或覆盖）商店子表。</summary>
        public ShopLoaderStub AddTable(byte[]? payload)
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
        public static byte[] BuildPayload(params ShopRowFixture[] rows)
        {
            return BuildPayloadDeclaringRows(rows.Length, rows);
        }

        /// <summary>造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。</summary>
        public static byte[] BuildPayloadDeclaringRows(int declaredRowCount, params ShopRowFixture[] rows)
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
                WriteInt32LittleEndian(payload, offset, rows[i].ItemId);
                WriteInt32LittleEndian(payload, offset + 4, rows[i].PriceItemId);
                WriteInt32LittleEndian(payload, offset + 8, rows[i].PriceCount);
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
    /// （扣减是背包切片的事，商店切片只负责发 <c>ConsumeItemCommand</c>），并记录每次查询的道具 ID。
    /// </summary>
    public sealed class ShopInventoryStub : IQueryHandler<InventoryQueryRequest, InventorySnapshot>
    {
        private readonly Dictionary<int, int> _counts;
        private readonly List<int> _queriedItemIds;

        public ShopInventoryStub()
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
    /// <see cref="ShopInventoryStub"/>，这样"买完金币真的变少"才可断言（共享 <c>NoOpCommandHandler</c> 只吞指令不落账）。
    /// <para>
    /// 它只做账，不做规则：能否购买、扣多少、发什么货全部由 <see cref="ShopHandler"/> 决定。
    /// </para>
    /// </summary>
    public sealed class ShopWalletStub : ICommandHandler<ConsumeItemCommand>, ICommandHandler<GrantItemCommand>
    {
        private readonly ShopInventoryStub _inventory;

        public ShopWalletStub(ShopInventoryStub inventory)
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
    /// 商店切片的一次性装配：真实 <see cref="ShopHandler"/> + 真实 <c>CqeBus</c>
    /// （经共享 <see cref="RecordingBus"/> 记录）+ 本切片自带的桩端口。
    /// </summary>
    public sealed class ShopTestHarness
    {
        /// <summary>标准夹具里生命药水的价格。</summary>
        public const int HealthPotionPrice = 100;

        /// <summary>标准夹具里魔力药水的价格。</summary>
        public const int ManaPotionPrice = 250;

        /// <summary>标准夹具里复活币的价格。</summary>
        public const int ReviveTokenPrice = 500;

        private ShopTestHarness(RecordingBus bus, ShopInventoryStub inventory, ShopHandler handler)
        {
            Bus = bus;
            Inventory = inventory;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        public RecordingBus Bus { get; }

        /// <summary>背包查询桩。</summary>
        public ShopInventoryStub Inventory { get; }

        /// <summary>被测处理器。</summary>
        public ShopHandler Handler { get; }

        /// <summary>标准 3 行夹具：2001/100、2002/250、2003/500，货币均为金币。</summary>
        public static ShopRowFixture[] DefaultRows()
        {
            return new[]
            {
                new ShopRowFixture(ShopItemIds.HealthPotion, MetaItemIds.Gold, HealthPotionPrice),
                new ShopRowFixture(ShopItemIds.ManaPotion, MetaItemIds.Gold, ManaPotionPrice),
                new ShopRowFixture(ShopItemIds.ReviveToken, MetaItemIds.Gold, ReviveTokenPrice),
            };
        }

        /// <summary>用标准 3 行夹具装配一套夹具。</summary>
        public static ShopTestHarness CreateWithDefaultRows()
        {
            return Create(ShopLoaderStub.WithRows(DefaultRows()));
        }

        /// <summary>用指定加载器装配一套夹具（表缺失/畸形场景走这里）。</summary>
        public static ShopTestHarness Create(IBinaryTableLoader loader)
        {
            return Create(loader, settleWithWallet: false);
        }

        /// <summary>
        /// 用指定加载器装配一套"余额会真的变化"的夹具：扣款/发货经 <see cref="ShopWalletStub"/> 落到
        /// <see cref="Inventory"/> 上，供 Presenter 用例断言购买后的金币。
        /// </summary>
        public static ShopTestHarness CreateWithWallet(IBinaryTableLoader loader)
        {
            return Create(loader, settleWithWallet: true);
        }

        private static ShopTestHarness Create(IBinaryTableLoader loader, bool settleWithWallet)
        {
            RecordingBus bus = new RecordingBus();
            ShopInventoryStub inventory = new ShopInventoryStub();

            // 扣款/发货由背包切片结算：默认只提供槽位，避免真实总线因未注册抛异常；
            // 需要余额变动时改由钱包桩落账。
            if (settleWithWallet)
            {
                ShopWalletStub wallet = new ShopWalletStub(inventory);
                bus.RegisterCommandHandler<ConsumeItemCommand>(wallet);
                bus.RegisterCommandHandler<GrantItemCommand>(wallet);
            }
            else
            {
                bus.RegisterCommandHandler<ConsumeItemCommand>(new NoOpCommandHandler<ConsumeItemCommand>());
                bus.RegisterCommandHandler<GrantItemCommand>(new NoOpCommandHandler<GrantItemCommand>());
            }

            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(inventory);

            ShopHandler handler = new ShopHandler(bus, loader);
            handler.Register(bus);

            return new ShopTestHarness(bus, inventory, handler);
        }

        /// <summary>发一条购买命令。</summary>
        public void Buy(int itemId, int count)
        {
            Bus.Send(new BuyShopItemCommand(itemId, count));
        }

        /// <summary>走真实总线发一条目录查询。</summary>
        public ShopCatalogSnapshot QueryCatalog()
        {
            return Bus.Query<ShopCatalogQueryRequest, ShopCatalogSnapshot>(new ShopCatalogQueryRequest());
        }
    }
}
