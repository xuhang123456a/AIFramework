using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Merge;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.Merge
{
    /// <summary><c>TbMergeItem</c> 的一行夹具数据。</summary>
    public readonly struct MergeRowFixture
    {
        public MergeRowFixture(int level, int itemId, int sellGold)
        {
            Level = level;
            ItemId = itemId;
            SellGold = sellGold;
        }

        /// <summary>物品等级（1..N）。</summary>
        public int Level { get; }

        /// <summary>该等级的道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>该等级的金币售价。</summary>
        public int SellGold { get; }
    }

    /// <summary>
    /// 合成子表加载桩：按 CONTRACT.md §3 的 Phase 1 夹具格式造 <c>TbMergeItem</c> 字节，
    /// 也能通过不登记任何表来模拟"表缺失"。只属于本切片测试，不改共享 <c>Fakes/</c>。
    /// </summary>
    public sealed class MergeLoaderStub : IBinaryTableLoader
    {
        /// <summary>合成子表的表名（与切片内常量一致）。</summary>
        public const string TableName = "TbMergeItem";

        private const int HeaderBytes = 4;
        private const int RowBytes = 12;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public MergeLoaderStub()
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
        public static MergeLoaderStub Missing()
        {
            return new MergeLoaderStub();
        }

        /// <summary>按给定行顺序写一张合法 <c>TbMergeItem</c> 的加载器。</summary>
        public static MergeLoaderStub WithRows(params MergeRowFixture[] rows)
        {
            return new MergeLoaderStub().AddTable(BuildPayload(rows));
        }

        /// <summary>登记（或覆盖）合成子表；<paramref name="payload"/> 传 null 表示该表缺失。</summary>
        public MergeLoaderStub AddTable(byte[]? payload)
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
        public static byte[] BuildPayload(params MergeRowFixture[] rows)
        {
            return BuildPayloadDeclaringRows(rows.Length, rows);
        }

        /// <summary>造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。</summary>
        public static byte[] BuildPayloadDeclaringRows(int declaredRowCount, params MergeRowFixture[] rows)
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
                WriteInt32LittleEndian(payload, offset, rows[i].Level);
                WriteInt32LittleEndian(payload, offset + 4, rows[i].ItemId);
                WriteInt32LittleEndian(payload, offset + 8, rows[i].SellGold);
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
    /// 极简钱包：把 <c>GrantItemCommand</c> 真的结算成道具数量，
    /// 这样"合成后金币真的入账"才可断言（共享 <see cref="NoOpCommandHandler{TCommand}"/> 只吞指令不落账）。
    /// 它只做账，不做规则：发多少金币完全由 <see cref="MergeHandler"/> 决定。
    /// </summary>
    public sealed class MergeWalletStub : ICommandHandler<GrantItemCommand>
    {
        private readonly Dictionary<int, int> _counts;

        public MergeWalletStub()
        {
            _counts = new Dictionary<int, int>();
        }

        /// <summary>读取当前持有的道具数量（未注入按 0 处理）。</summary>
        public int GetCount(int itemId)
        {
            int count;
            return _counts.TryGetValue(itemId, out count) ? count : 0;
        }

        /// <inheritdoc />
        public void Handle(in GrantItemCommand command)
        {
            _counts[command.ItemId] = GetCount(command.ItemId) + command.Count;
        }
    }

    /// <summary>
    /// 合成切片的一次性装配：真实 <see cref="MergeHandler"/> + 真实 <c>CqeBus</c>
    /// （经共享 <see cref="RecordingBus"/> 记录）+ 本切片自带的钱包桩。
    /// </summary>
    public sealed class MergeTestHarness
    {
        /// <summary>标准夹具的最高等级。</summary>
        public const int MaxLevel = 4;

        /// <summary>标准夹具里 1 级物品的售价（合成不会产出 1 级，仅配置齐全用）。</summary>
        public const int Level1SellGold = 10;

        /// <summary>标准夹具里 1+1 合成出的 2 级物品售价。</summary>
        public const int Level2SellGold = 30;

        /// <summary>标准夹具里 2+2 合成出的 3 级物品售价。</summary>
        public const int Level3SellGold = 70;

        /// <summary>标准夹具里 3+3 合成出的 4 级物品售价。</summary>
        public const int Level4SellGold = 150;

        /// <summary>标准夹具 1 级物品的道具 ID。</summary>
        public const int Level1ItemId = 3101;

        private MergeTestHarness(RecordingBus bus, MergeWalletStub wallet, MergeHandler handler)
        {
            Bus = bus;
            Wallet = wallet;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        public RecordingBus Bus { get; }

        /// <summary>金币/道具结算桩。</summary>
        public MergeWalletStub Wallet { get; }

        /// <summary>被测处理器。</summary>
        public MergeHandler Handler { get; }

        /// <summary>标准 4 行夹具：等级 1..4，售价 10 / 30 / 70 / 150。</summary>
        public static MergeRowFixture[] DefaultRows()
        {
            return new[]
            {
                new MergeRowFixture(1, Level1ItemId, Level1SellGold),
                new MergeRowFixture(2, Level1ItemId + 1, Level2SellGold),
                new MergeRowFixture(3, Level1ItemId + 2, Level3SellGold),
                new MergeRowFixture(4, Level1ItemId + 3, Level4SellGold),
            };
        }

        /// <summary>用标准 4 行夹具装配一套夹具。</summary>
        public static MergeTestHarness CreateWithDefaultRows()
        {
            return Create(MergeLoaderStub.WithRows(DefaultRows()));
        }

        /// <summary>用指定加载器装配一套夹具（畸形表/缺失表场景由测试直接 new 处理器）。</summary>
        public static MergeTestHarness Create(IBinaryTableLoader loader)
        {
            RecordingBus bus = new RecordingBus();
            MergeWalletStub wallet = new MergeWalletStub();
            bus.RegisterCommandHandler<GrantItemCommand>(wallet);

            MergeHandler handler = new MergeHandler(bus, loader);
            handler.Register(bus);

            return new MergeTestHarness(bus, wallet, handler);
        }

        /// <summary>经真实总线发一条合成命令。</summary>
        public void Merge(int fromX, int fromY, int toX, int toY)
        {
            Bus.Send(new MergeCellsCommand(fromX, fromY, toX, toY));
        }

        /// <summary>经真实总线发一条生成命令。</summary>
        public void Spawn(int x, int y, int level)
        {
            Bus.Send(new SpawnItemCommand(x, y, level));
        }

        /// <summary>经真实总线读一次棋盘快照。</summary>
        public MergeGridSnapshot Query()
        {
            return Bus.Query<MergeGridQueryRequest, MergeGridSnapshot>(new MergeGridQueryRequest());
        }
    }
}
