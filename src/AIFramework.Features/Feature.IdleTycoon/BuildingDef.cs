#nullable enable
namespace AIFramework.Features.IdleTycoon
{
    /// <summary>
    /// 建筑子表行（纯数据）：一栋建筑的静态定义，不含任何运行时状态。
    /// 运行时等级由 <see cref="IdleTycoonHandler"/> 单独维护，禁止回写本结构。
    /// </summary>
    public readonly struct BuildingDef
    {
        public BuildingDef(int buildingId, int baseIncome, int upgradeCost, int maxLevel)
        {
            BuildingId = buildingId;
            BaseIncome = baseIncome;
            UpgradeCost = upgradeCost;
            MaxLevel = maxLevel;
        }

        /// <summary>建筑唯一 ID（子表内不允许重复）。</summary>
        public int BuildingId { get; }

        /// <summary>每级每秒产出的金币，必须大于 0。</summary>
        public int BaseIncome { get; }

        /// <summary>升级一次消耗的金币（每次升级同价，Phase 1 不做曲线），不允许为负。</summary>
        public int UpgradeCost { get; }

        /// <summary>等级上限，必须大于 0；等级达到本值后升级被静默拒绝。</summary>
        public int MaxLevel { get; }
    }
}
