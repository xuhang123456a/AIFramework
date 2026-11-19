using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.IdleRpg;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.IdleRpg
{
    /// <summary><c>TbIdleStage</c> 的一行夹具数据。</summary>
    public readonly struct IdleStageRow
    {
        public IdleStageRow(int stageId, int enemyHp, int enemyAttack, int goldReward, int requiredPower)
        {
            StageId = stageId;
            EnemyHp = enemyHp;
            EnemyAttack = enemyAttack;
            GoldReward = goldReward;
            RequiredPower = requiredPower;
        }

        /// <summary>关卡序号（合法表必须从 1 起连续）。</summary>
        public int StageId { get; }

        /// <summary>敌人满血值。</summary>
        public int EnemyHp { get; }

        /// <summary>每 3 个战斗 tick 的反击伤害。</summary>
        public int EnemyAttack { get; }

        /// <summary>通关金币。</summary>
        public int GoldReward { get; }

        /// <summary>推荐战力（Phase 1 只校验与展示）。</summary>
        public int RequiredPower { get; }
    }

    /// <summary>
    /// 挂机 RPG 子表加载桩：按 CONTRACT.md §3 的 Phase 1 夹具格式造 <c>TbIdleStage</c> 字节，
    /// 也能通过不登记任何表来模拟"表缺失"，或手工塞畸形字节来触发校验分支。
    /// 只属于本切片测试，不改共享 <c>Fakes/</c>。
    /// </summary>
    public sealed class IdleRpgLoaderStub : IBinaryTableLoader
    {
        /// <summary>关卡子表的表名（与切片内常量一致）。</summary>
        public const string TableName = "TbIdleStage";

        private const int HeaderBytes = 4;
        private const int RowBytes = 20;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public IdleRpgLoaderStub()
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
        public static IdleRpgLoaderStub Missing()
        {
            return new IdleRpgLoaderStub();
        }

        /// <summary>按给定行顺序写一张合法 <c>TbIdleStage</c>。</summary>
        public static IdleRpgLoaderStub WithRows(params IdleStageRow[] rows)
        {
            return new IdleRpgLoaderStub().AddTable(BuildPayload(rows));
        }

        /// <summary>登记（或覆盖）关卡子表；传 <c>null</c> 表示该表缺失。</summary>
        public IdleRpgLoaderStub AddTable(byte[]? payload)
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
        public static byte[] BuildPayload(params IdleStageRow[] rows)
        {
            return BuildPayloadDeclaringRows(rows.Length, rows);
        }

        /// <summary>造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。</summary>
        public static byte[] BuildPayloadDeclaringRows(int declaredRowCount, params IdleStageRow[] rows)
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
                WriteInt32LittleEndian(payload, offset, rows[i].StageId);
                WriteInt32LittleEndian(payload, offset + 4, rows[i].EnemyHp);
                WriteInt32LittleEndian(payload, offset + 8, rows[i].EnemyAttack);
                WriteInt32LittleEndian(payload, offset + 12, rows[i].GoldReward);
                WriteInt32LittleEndian(payload, offset + 16, rows[i].RequiredPower);
            }

            return payload;
        }

        /// <summary>把任意字节列表原样写成 payload（造"头部不足 4 字节""多出尾字节"等畸形表）。</summary>
        public static byte[] BuildRawPayload(int declaredRowCount, params int[] trailingHeaderBytes)
        {
            byte[] payload = new byte[HeaderBytes + (trailingHeaderBytes.Length * 4)];
            WriteInt32LittleEndian(payload, 0, declaredRowCount);
            for (int i = 0; i < trailingHeaderBytes.Length; i++)
            {
                WriteInt32LittleEndian(payload, HeaderBytes + (i * 4), trailingHeaderBytes[i]);
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
    /// 极简钱包：只结算 <see cref="GrantItemCommand"/>，并记录每一次发放
    /// （扣款指令故意不注册——本切片只发奖不发扣款，一旦误发就会在总线上炸出来）。
    /// </summary>
    public sealed class IdleRpgWalletStub : ICommandHandler<GrantItemCommand>
    {
        private readonly Dictionary<int, int> _counts;

        public IdleRpgWalletStub()
        {
            _counts = new Dictionary<int, int>();
        }

        /// <summary>读取当前累计到账数量（未到账按 0 处理）。</summary>
        public int GetCount(int itemId)
        {
            int count;
            return _counts.TryGetValue(itemId, out count) ? count : 0;
        }

        void ICommandHandler<GrantItemCommand>.Handle(in GrantItemCommand command)
        {
            _counts[command.ItemId] = GetCount(command.ItemId) + command.Count;
        }
    }

    /// <summary>
    /// 本切片的一次性装配：真实 <see cref="IdleRpgHandler"/> + 真实 <c>CqeBus</c>
    /// （经共享 <see cref="RecordingBus"/> 记录）+ 本切片自带的钱包桩与加载桩。
    /// 断言只落在"切片发出了什么消息"、"处理器只读属性"、"快照"与"金币到账"四处。
    /// </summary>
    public sealed class IdleRpgHarness
    {
        /// <summary>1 级英雄战力（= 10 + (1 - 1) × 6）。</summary>
        public const int BasePower = 10;

        /// <summary>响应标签：通关发金币（规则锁死 6911）。</summary>
        public const int GoldReasonTagId = 6911;

        private IdleRpgHarness(RecordingBus bus, IdleRpgWalletStub wallet, IdleRpgHandler handler)
        {
            Bus = bus;
            Wallet = wallet;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        public RecordingBus Bus { get; }

        /// <summary>金币到账桩（切片只发 <c>GrantItemCommand</c>）。</summary>
        public IdleRpgWalletStub Wallet { get; }

        /// <summary>被测处理器。</summary>
        public IdleRpgHandler Handler { get; }

        /// <summary>当前已到账金币。</summary>
        public int Gold
        {
            get { return Wallet.GetCount(MetaItemIds.Gold); }
        }

        /// <summary>
        /// CONTRACT §3 的 5 行夹具：
        /// (1, 30, 2, 20, 10) / (2, 60, 4, 40, 16) / (3, 120, 6, 80, 28) / (4, 240, 9, 160, 46) / (5, 480, 12, 320, 70)。
        /// </summary>
        public static IdleStageRow[] DefaultRows()
        {
            return new[]
            {
                new IdleStageRow(1, 30, 2, 20, 10),
                new IdleStageRow(2, 60, 4, 40, 16),
                new IdleStageRow(3, 120, 6, 80, 28),
                new IdleStageRow(4, 240, 9, 160, 46),
                new IdleStageRow(5, 480, 12, 320, 70),
            };
        }

        /// <summary>用标准 5 行夹具装配（1 级英雄）。</summary>
        public static IdleRpgHarness CreateWithDefaultRows()
        {
            return Create(IdleRpgLoaderStub.WithRows(DefaultRows()));
        }

        /// <summary>用指定加载器装配（表缺失/畸形、空表场景走这里）。</summary>
        public static IdleRpgHarness Create(IBinaryTableLoader loader)
        {
            return Create(loader, 1);
        }

        /// <summary>用指定加载器 + 指定初始等级装配。</summary>
        public static IdleRpgHarness Create(IBinaryTableLoader loader, int initialHeroLevel)
        {
            RecordingBus bus = new RecordingBus();
            IdleRpgWalletStub wallet = new IdleRpgWalletStub();
            bus.RegisterCommandHandler<GrantItemCommand>(wallet);

            IdleRpgHandler handler = new IdleRpgHandler(bus, loader, initialHeroLevel);
            handler.Register(bus);

            return new IdleRpgHarness(bus, wallet, handler);
        }

        /// <summary>走真实总线推进 N 个战斗 tick。</summary>
        public void Tick(int ticks)
        {
            Bus.Send(new IdleBattleTickCommand(ticks));
        }

        /// <summary>走真实总线手动进入下一关。</summary>
        public void Advance()
        {
            Bus.Send(new AdvanceStageCommand());
        }

        /// <summary>走真实总线用经验升一级。</summary>
        public void LevelUp()
        {
            Bus.Send(new LevelUpHeroCommand());
        }

        /// <summary>走真实总线查一次快照。</summary>
        public IdleRpgSnapshot Snapshot()
        {
            IdleRpgSnapshotQueryRequest request = default;
            return Bus.Query<IdleRpgSnapshotQueryRequest, IdleRpgSnapshot>(in request);
        }

        /// <summary>累计发放金币的指令条数。</summary>
        public int GoldGrantCount
        {
            get { return Bus.CountSent<GrantItemCommand>(); }
        }
    }
}
