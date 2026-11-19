#nullable enable
using System;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Contracts.Meta
{
    /// <summary>
    /// 模拟经营（T2-11）建筑类型。<b>Phase 1 只有三档</b>：摊位 / 商店 / 餐厅，
    /// 数值差异全部来自子表，不在这里做任何分支判断。
    /// </summary>
    public enum TycoonBuildingKind : byte
    {
        /// <summary>摊位：最便宜、单 tick 收入最低。</summary>
        Stall = 0,

        /// <summary>商店：中档建造成本与收入。</summary>
        Shop = 1,

        /// <summary>餐厅：最贵、单 tick 收入最高，等级上限最低。</summary>
        Restaurant = 2,
    }

    /// <summary>
    /// 模拟经营建筑配置行（来自本切片子表 <c>TbTycoonBuilding</c>）。
    /// 只读值类型：表加载后即冻结，运行时状态另存 <see cref="TycoonBuildingState"/>。
    /// </summary>
    public readonly struct TycoonBuildingDef
    {
        public TycoonBuildingDef(int buildingId, TycoonBuildingKind kind, int buildCost, int baseIncome, int maxLevel)
        {
            BuildingId = buildingId;
            Kind = kind;
            BuildCost = buildCost;
            BaseIncome = baseIncome;
            MaxLevel = maxLevel;
        }

        /// <summary>建筑配置 ID，子表内唯一。</summary>
        public int BuildingId { get; }

        /// <summary>建筑类型，仅用于分类展示，不参与任何数值计算。</summary>
        public TycoonBuildingKind Kind { get; }

        /// <summary>建造一次的金币消耗；同时是升级成本（<c>BuildCost / 2 × level</c>）与雇员工成本（<c>BuildCost / 4</c>）的基数。</summary>
        public int BuildCost { get; }

        /// <summary>1 级、0 员工时每 tick 的基础收入。</summary>
        public int BaseIncome { get; }

        /// <summary>等级上限，合法区间 ≥ 1。</summary>
        public int MaxLevel { get; }
    }

    /// <summary>
    /// 一栋<b>已建造</b>建筑的运行时状态快照。等级与员工数是本切片的运行时状态，
    /// 绝不从配置表读初值（建造成立即 1 级 0 员工）。
    /// </summary>
    public readonly struct TycoonBuildingState
    {
        public TycoonBuildingState(int buildingId, int level, int staffCount, int incomePerTick)
        {
            BuildingId = buildingId;
            Level = level;
            StaffCount = staffCount;
            IncomePerTick = incomePerTick;
        }

        /// <summary>建筑配置 ID。</summary>
        public int BuildingId { get; }

        /// <summary>当前等级，建造成功后从 1 起。</summary>
        public int Level { get; }

        /// <summary>当前员工数，上限为 <c>2 × Level</c>。</summary>
        public int StaffCount { get; }

        /// <summary>
        /// 本建筑每 tick 收入：<c>(BaseIncome + StaffCount × (BaseIncome / 2)) × Level</c>，
        /// 整数除法，由处理器按当前配置实时算出。
        /// </summary>
        public int IncomePerTick { get; }
    }

    /// <summary>
    /// 模拟经营整包只读快照：UI / 存档只依赖这份值，不接触切片内部状态。
    /// 构造时复制入参数组，查询方改自己的数组不会污染切片内部状态。
    /// </summary>
    public sealed class TycoonSnapshot
    {
        public TycoonSnapshot(int gold, int totalIncomePerTick, int tickCount, TycoonBuildingState[] buildings)
        {
            Gold = gold;
            TotalIncomePerTick = totalIncomePerTick;
            TickCount = tickCount;

            TycoonBuildingState[] source = buildings ?? Array.Empty<TycoonBuildingState>();
            TycoonBuildingState[] copy = new TycoonBuildingState[source.Length];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = source[i];
            }

            Buildings = copy;
        }

        /// <summary>本切片金币缓存值；与背包切片通过 <see cref="InventoryQueryRequest"/> 查到的数值一致。</summary>
        public int Gold { get; }

        /// <summary>所有已建造建筑的每 tick 收入之和。</summary>
        public int TotalIncomePerTick { get; }

        /// <summary>已结算的经营 tick 数。</summary>
        public int TickCount { get; }

        /// <summary>已建造建筑的状态，顺序等于子表物理行序；空数组表示一栋都没建。</summary>
        public TycoonBuildingState[] Buildings { get; }
    }

    /// <summary>
    /// 建造一栋建筑（初始 1 级、0 员工）。未知 ID / 已建造 / 金币不足一律静默拒绝，重复建造不会叠加。
    /// </summary>
    public readonly struct BuildTycoonCommand : ICommand
    {
        public BuildTycoonCommand(int buildingId)
        {
            BuildingId = buildingId;
        }

        /// <summary>建筑配置 ID，对应子表 <c>TbTycoonBuilding.buildingId</c>。</summary>
        public int BuildingId { get; }
    }

    /// <summary>升级一栋已建造建筑（等级 +1，收入按等级倍增）。</summary>
    public readonly struct UpgradeTycoonCommand : ICommand
    {
        public UpgradeTycoonCommand(int buildingId)
        {
            BuildingId = buildingId;
        }

        /// <summary>建筑配置 ID；未建造或已满级时静默拒绝。</summary>
        public int BuildingId { get; }
    }

    /// <summary>给一栋已建造建筑雇一名员工（该建筑收入按员工数加成）。</summary>
    public readonly struct HireStaffCommand : ICommand
    {
        public HireStaffCommand(int buildingId)
        {
            BuildingId = buildingId;
        }

        /// <summary>建筑配置 ID；未建造或员工数已达 <c>2 × Level</c> 时静默拒绝。</summary>
        public int BuildingId { get; }
    }

    /// <summary>
    /// 结算一次经营 tick（由时间驱动或手动调用）。<see cref="Ticks"/> 会被夹到 1..5，
    /// 每次 tick 按 <see cref="TycoonSnapshot.TotalIncomePerTick"/> 发放金币。
    /// </summary>
    public readonly struct TycoonTickCommand : ICommand
    {
        public TycoonTickCommand(int ticks)
        {
            Ticks = ticks;
        }

        /// <summary>本次要结算的 tick 数，合法区间 1..5，越界由处理器夹取（不拒绝命令）。</summary>
        public int Ticks { get; }
    }

    /// <summary>模拟经营快照查询请求体：空结构体，不需要任何参数。</summary>
    public readonly struct TycoonSnapshotQueryRequest : IQuery<TycoonSnapshot>
    {
    }
}
