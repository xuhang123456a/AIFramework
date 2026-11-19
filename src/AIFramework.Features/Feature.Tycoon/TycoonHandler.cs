#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Tycoon
{
    /// <summary>
    /// 模拟经营（T2-11）经营循环：建造 → 升级 / 雇员工 → 按 tick 结算收入。
    /// <para>
    /// 与 <c>Feature.IdleTycoon</c> 的区别：这里没有离线收益与时间系统，收入完全由
    /// "显式 tick 命令 × 当前建筑阵容"决定，且同时存在建造、升级、员工三条资源消耗链。
    /// </para>
    /// <para>
    /// 金币只走总线（<see cref="ConsumeItemCommand"/> / <see cref="GrantItemCommand"/>），
    /// 切片内另存一份 <c>_gold</c> 缓存：首次需要金币时从背包播种，之后只由本切片自己的
    /// 扣款/发奖同步，保证"连续建造不超支"（背包实现延迟落账时尤其关键）。
    /// </para>
    /// </summary>
    public sealed class TycoonHandler : ICommandHandler<BuildTycoonCommand>,
                                        ICommandHandler<UpgradeTycoonCommand>,
                                        ICommandHandler<HireStaffCommand>,
                                        ICommandHandler<TycoonTickCommand>,
                                        IQueryHandler<TycoonSnapshotQueryRequest, TycoonSnapshot>
    {
        /// <summary>建造扣款原因标签（因果日志用）。</summary>
        private const int ConsumeReasonTagId = 6811;

        /// <summary>经营 tick 收入发放原因标签（因果日志用）。</summary>
        private const int GrantReasonTagId = 6812;

        /// <summary>单条 tick 命令允许结算的最小次数。</summary>
        private const int MinTicksPerCommand = 1;

        /// <summary>单条 tick 命令允许结算的最大次数：防止一条命令刷爆经济。</summary>
        private const int MaxTicksPerCommand = 5;

        private readonly ICqeBus _bus;
        private readonly TycoonBuildingDef[] _defs;
        private readonly bool[] _built;
        private readonly int[] _levels;
        private readonly int[] _staff;

        private int _gold;
        private bool _goldSeeded;
        private int _builtCount;
        private int _upgradeCount;
        private int _hiredCount;
        private int _tickCount;

        public TycoonHandler(ICqeBus bus, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;

            TycoonTables tables = new TycoonTables(loader);
            _defs = new TycoonBuildingDef[tables.Count];
            _built = new bool[tables.Count];
            _levels = new int[tables.Count];
            _staff = new int[tables.Count];
            for (int i = 0; i < tables.Count; i++)
            {
                // 构造即全未建造：等级与员工数是运行时状态，绝不从表里读初值。
                _defs[i] = tables.Get(i);
                _built[i] = false;
                _levels[i] = 0;
                _staff[i] = 0;
            }
        }

        /// <summary>已成功建造的建筑数量（被拒绝的请求不计）。</summary>
        public int BuiltCount
        {
            get { return _builtCount; }
        }

        /// <summary>已成功升级的总次数（被拒绝的请求不计）。</summary>
        public int UpgradeCount
        {
            get { return _upgradeCount; }
        }

        /// <summary>已成功雇佣的员工总数（被拒绝的请求不计）。</summary>
        public int HiredCount
        {
            get { return _hiredCount; }
        }

        /// <summary>已结算的经营 tick 总数（含单条命令内的多次 tick）。</summary>
        public int TickCount
        {
            get { return _tickCount; }
        }

        /// <summary>所有已建造建筑的每 tick 收入之和；每次访问实时累加，不缓存。</summary>
        public int TotalIncomePerTick
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _defs.Length; i++)
                {
                    if (!_built[i])
                    {
                        continue;
                    }

                    total += ComputeIncome(_defs[i], _levels[i], _staff[i]);
                }

                return total;
            }
        }

        /// <summary>注册本切片的全部命令与查询处理器（构造里不自动注册，装配层负责调用）。</summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<BuildTycoonCommand>(this);
            bus.RegisterCommandHandler<UpgradeTycoonCommand>(this);
            bus.RegisterCommandHandler<HireStaffCommand>(this);
            bus.RegisterCommandHandler<TycoonTickCommand>(this);
            bus.RegisterQueryHandler<TycoonSnapshotQueryRequest, TycoonSnapshot>(this);
        }

        /// <summary>
        /// 建造：未知 ID / 已建造过 / 金币不足 → 静默拒绝（状态不变、不发指令）；
        /// 合法 → 先扣建造费、再落"1 级 0 员工"。
        /// </summary>
        public void Handle(in BuildTycoonCommand command)
        {
            int index = IndexOf(command.BuildingId);
            if (index < 0)
            {
                // 未知建筑：静默拒绝，连背包都不查。
                return;
            }

            if (_built[index])
            {
                // 同一栋只能建一次：重复建造不清等级、不重复扣款。
                return;
            }

            TycoonBuildingDef def = _defs[index];
            if (!TrySpend(def.BuildCost))
            {
                // 金币不足：静默拒绝，状态不变。
                return;
            }

            _built[index] = true;
            _levels[index] = 1;
            _staff[index] = 0;
            _builtCount++;
        }

        /// <summary>
        /// 升级：未知 / 未建造 / 已达 <c>MaxLevel</c> / 金币不足 → 静默拒绝；
        /// 成本 <c>BuildCost / 2 × 当前等级</c>，扣款成功后等级 +1。
        /// </summary>
        public void Handle(in UpgradeTycoonCommand command)
        {
            int index = IndexOf(command.BuildingId);
            if (index < 0 || !_built[index])
            {
                return;
            }

            TycoonBuildingDef def = _defs[index];
            int level = _levels[index];
            if (level >= def.MaxLevel)
            {
                // 已封顶：静默拒绝，同样不查背包。
                return;
            }

            int upgradeCost = (def.BuildCost / 2) * level;
            if (!TrySpend(upgradeCost))
            {
                return;
            }

            _levels[index] = level + 1;
            _upgradeCount++;
        }

        /// <summary>
        /// 雇员工：未知 / 未建造 / 员工数已达 <c>2 × 当前等级</c> / 金币不足 → 静默拒绝；
        /// 成本 <c>BuildCost / 4</c>，扣款成功后员工数 +1（该建筑收入随即提升）。
        /// </summary>
        public void Handle(in HireStaffCommand command)
        {
            int index = IndexOf(command.BuildingId);
            if (index < 0 || !_built[index])
            {
                return;
            }

            TycoonBuildingDef def = _defs[index];
            int level = _levels[index];
            int staff = _staff[index];
            if (staff >= 2 * level)
            {
                // 员工位已满：静默拒绝，不查背包。
                return;
            }

            int staffCost = def.BuildCost / 4;
            if (!TrySpend(staffCost))
            {
                return;
            }

            _staff[index] = staff + 1;
            _hiredCount++;
        }

        /// <summary>
        /// 结算经营 tick：<see cref="TycoonTickCommand.Ticks"/> 夹到 1..5，每次 tick 按
        /// <see cref="TotalIncomePerTick"/> 发奖并同步本地金币缓存；无论收入是否为 0，
        /// <see cref="TickCount"/> 都按实际结算次数累加。
        /// </summary>
        public void Handle(in TycoonTickCommand command)
        {
            int ticks = ClampTicks(command.Ticks);

            // 先播种金币缓存：即使还没有任何建筑，快照里的 Gold 也要与背包一致。
            EnsureGoldLoaded();

            int incomePerTick = TotalIncomePerTick;
            for (int i = 0; i < ticks; i++)
            {
                if (incomePerTick > 0)
                {
                    // 一次 tick 一条发奖指令：因果日志里 tick 与入账一一对应，便于回放核对。
                    _bus.Send(new GrantItemCommand(MetaItemIds.Gold, incomePerTick, GrantReasonTagId));
                    _gold += incomePerTick;
                }

                _tickCount++;
            }
        }

        /// <summary>快照查询：每次调用都新建建筑数组（防御性复制），调用方改数组不影响切片状态。</summary>
        public TycoonSnapshot Handle(in TycoonSnapshotQueryRequest query)
        {
            EnsureGoldLoaded();

            TycoonBuildingState[] states = new TycoonBuildingState[_builtCount];
            int cursor = 0;
            for (int i = 0; i < _defs.Length; i++)
            {
                if (!_built[i])
                {
                    continue;
                }

                TycoonBuildingDef def = _defs[i];
                states[cursor] = new TycoonBuildingState(
                    def.BuildingId,
                    _levels[i],
                    _staff[i],
                    ComputeIncome(def, _levels[i], _staff[i]));
                cursor++;
            }

            return new TycoonSnapshot(_gold, TotalIncomePerTick, _tickCount, states);
        }

        /// <summary>
        /// 每 tick 收入：<c>(BaseIncome + StaffCount × (BaseIncome / 2)) × Level</c>。
        /// 乘除全用整数，员工加成按"基础收入的一半向下取整"计算。
        /// </summary>
        private static int ComputeIncome(TycoonBuildingDef def, int level, int staffCount)
        {
            return (def.BaseIncome + (staffCount * (def.BaseIncome / 2))) * level;
        }

        /// <summary>
        /// 扣款统一入口：先播种缓存，余额不足直接拒绝（不发指令、不改状态）；
        /// 余额足够才发 <see cref="ConsumeItemCommand"/> 并同步缓存。
        /// 成本为 0 时只记账不发空扣款指令（避免在因果日志里制造零额扣款噪声）。
        /// </summary>
        private bool TrySpend(int cost)
        {
            EnsureGoldLoaded();
            if (_gold < cost)
            {
                return false;
            }

            if (cost > 0)
            {
                _bus.Send(new ConsumeItemCommand(MetaItemIds.Gold, cost, ConsumeReasonTagId));
                _gold -= cost;
            }

            return true;
        }

        /// <summary>金币缓存播种：只在首次需要金币时向背包查一次，之后完全由本切片的扣款/发奖维护。</summary>
        private void EnsureGoldLoaded()
        {
            if (_goldSeeded)
            {
                return;
            }

            InventorySnapshot inventory = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold));
            _gold = inventory.Count;
            _goldSeeded = true;
        }

        /// <summary>把请求的 tick 数夹到 1..5（越界不拒绝命令，按边界值结算）。</summary>
        private static int ClampTicks(int ticks)
        {
            if (ticks < MinTicksPerCommand)
            {
                return MinTicksPerCommand;
            }

            if (ticks > MaxTicksPerCommand)
            {
                return MaxTicksPerCommand;
            }

            return ticks;
        }

        /// <summary>线性查找建筑行下标；未找到返回 -1（建筑规模为个位数，不做字典）。</summary>
        private int IndexOf(int buildingId)
        {
            for (int i = 0; i < _defs.Length; i++)
            {
                if (_defs[i].BuildingId == buildingId)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
