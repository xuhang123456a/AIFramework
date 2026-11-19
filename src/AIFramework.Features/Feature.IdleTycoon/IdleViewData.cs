namespace AIFramework.Features.IdleTycoon
{
    /// <summary>
    /// 放置面板的展示数据结构（文件按面板命名，类型按行命名——与 <c>ShopViewData.cs</c> 内含
    /// <c>ShopItemViewData</c> 的约定一致）。
    /// <para>
    /// 视图接口 <c>IIdleView</c> 由 <c>tools/viewgen</c> 从本切片的 <c>Idle.bindings.json</c>
    /// 生成为 <c>IIdleView.g.cs</c>（禁止手改），本文件只承载展示数据结构。
    /// </para>
    /// </summary>
    /// <remarks>
    /// <see cref="BuildingViewData"/> 是"某一刻的视图"而不是账本：等级与产出全部由
    /// <see cref="IdleTycoonHandler"/> 提供，本结构只做搬运，任何加减都必须在切片内核里发生。
    /// </remarks>
    public readonly struct BuildingViewData
    {
        public BuildingViewData(
            int buildingId,
            int level,
            int maxLevel,
            int upgradeCost,
            int incomePerSecond,
            bool affordable)
        {
            BuildingId = buildingId;
            Level = level;
            MaxLevel = maxLevel;
            UpgradeCost = upgradeCost;
            IncomePerSecond = incomePerSecond;
            Affordable = affordable;
        }

        /// <summary>建筑 ID（点击升级时原样回传给 <c>UpgradeBuildingCommand</c>）。</summary>
        public int BuildingId { get; }

        /// <summary>当前等级（构造后从 0 起）。</summary>
        public int Level { get; }

        /// <summary>等级上限；<see cref="Level"/> 达到本值后升级按钮必须置灰。</summary>
        public int MaxLevel { get; }

        /// <summary>升级一次消耗的金币（Phase 1 每次同价，不做曲线）。</summary>
        public int UpgradeCost { get; }

        /// <summary>本栋当前每秒产出：<c>Level × 基础产出</c>，不是全场合计。</summary>
        public int IncomePerSecond { get; }

        /// <summary>
        /// 当前是否可升级：<c>金币 ≥ UpgradeCost 且 未满级</c>。
        /// <para>
        /// 它<b>只是展示标记</b>：能否成交永远由 <see cref="IdleTycoonHandler"/> 裁决，
        /// 这里的判定条件刻意与处理器的受理条件逐条对齐（钱够 + 未满级），避免"按钮可点但点了没反应"。
        /// </para>
        /// </summary>
        public bool Affordable { get; }
    }
}
