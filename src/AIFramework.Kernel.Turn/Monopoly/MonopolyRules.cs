#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Monopoly
{
    /// <summary>
    /// 大富翁规则常量与纯函数：数值全部锁成编译期常量（Phase 1 不读配置表），测试直接写期望值。
    /// <para>
    /// <b>数值口径</b>（与 <see cref="MonopolyWorld"/> 类注释的规则清单一一对应）：
    /// 20 格环形棋盘；起始现金 1500；起点奖励 200；税务 100；奖励格 150；
    /// 地产价格 200 起、每块 +20（共 15 块：200..480）；租金 = 地价 × (10% + 15% × 等级)；
    /// 升级费 = 地价 × 50%，最高 3 级；每回合掷 1d6（不做双数再掷）；40 个名义回合封顶。
    /// </para>
    /// </summary>
    internal static class MonopolyRules
    {
        /// <summary>棋盘格子总数（环形，索引 0..19）。</summary>
        internal const int TileCount = 20;

        /// <summary>起点格索引。</summary>
        internal const int GoTileIndex = 0;

        /// <summary>监狱格索引（踩到直接跳回起点，不发起点奖励）。</summary>
        internal const int JailTileIndex = 4;

        /// <summary>机会格索引。</summary>
        internal const int ChanceTileIndex = 8;

        /// <summary>税务格索引。</summary>
        internal const int TaxTileIndex = 12;

        /// <summary>奖励格索引。</summary>
        internal const int BonusTileIndex = 16;

        /// <summary>骰子面数（每次掷 1 个 d6）。</summary>
        internal const int DiceSides = 6;

        /// <summary>起始现金。</summary>
        internal const int StartingCash = 1500;

        /// <summary>经过/停在起点的奖励。</summary>
        internal const int GoReward = 200;

        /// <summary>税务格固定支付额。</summary>
        internal const int TaxAmount = 100;

        /// <summary>奖励格固定获得额。</summary>
        internal const int BonusAmount = 150;

        /// <summary>地产最高等级（0 = 刚买入，3 = 满级）。</summary>
        internal const int MaxPropertyLevel = 3;

        /// <summary>名义回合上限：<c>TurnIndex / PlayerCount + 1</c> 达到 40 个回合后收口。</summary>
        internal const int MaxRounds = 40;

        /// <summary>玩家数上限（<see cref="AIFramework.Contracts.Gameplay.GameplayInitData.UnitCount"/> 超过即夹到 4）。</summary>
        internal const int MaxPlayers = 4;

        /// <summary><see cref="AIFramework.Contracts.Gameplay.GameplayInitData.UnitCount"/> ≤ 0 时的默认玩家数。</summary>
        internal const int DefaultPlayers = 2;

        /// <summary>第一块地产的买入价。</summary>
        internal const int BasePropertyPrice = 200;

        /// <summary>地产价格递增量（按索引顺序每块 +20）。</summary>
        internal const int PropertyPriceStep = 20;

        /// <summary>0 级地产的租金百分比（地价的 10%）。</summary>
        internal const int RentPercentAtLevel0 = 10;

        /// <summary>每升 1 级额外增加的租金百分比（15%）。</summary>
        internal const int RentPercentPerLevel = 15;

        /// <summary>升级费占地价的百分比（50%）。</summary>
        internal const int UpgradeCostPercent = 50;

        /// <summary>机会卡：小额收入。</summary>
        internal const int ChanceGainSmall = 100;

        /// <summary>机会卡：大额收入。</summary>
        internal const int ChanceGainBig = 200;

        /// <summary>机会卡：损失金额。</summary>
        internal const int ChanceLose = 100;

        /// <summary>机会卡：前进格数。</summary>
        internal const int ChanceMoveSteps = 3;

        /// <summary>机会卡种类数（4 选 1：+100 / +200 / -100 / 前进 3 格）。</summary>
        internal const int ChanceCardCount = 4;

        /// <summary>机会卡索引：小额收入（+100）。</summary>
        internal const int ChanceCardGainSmall = 0;

        /// <summary>机会卡索引：大额收入（+200）。</summary>
        internal const int ChanceCardGainBig = 1;

        /// <summary>机会卡索引：损失（-100，扣到 0 为止）。</summary>
        internal const int ChanceCardLose = 2;

        /// <summary>机会卡索引：前进 3 格（越环照常发起点奖励并结算新落点）。</summary>
        internal const int ChanceCardMove = 3;

        /// <summary>
        /// 机会卡"前进 3 格"落到新格子后的连锁结算深度上限。
        /// <para>
        /// 固定布局下 8 + 3 = 11 是地产，正常不可能再连锁；本上限只是防御性兜底，
        /// 避免后续改布局时把内核拖进无限递归。
        /// </para>
        /// </summary>
        internal const int MaxChanceChainDepth = 4;

        /// <summary>胜利战利品模板 ID（与既有 Turn 卡带约定一致）。</summary>
        internal const int VictoryLootItemId = 1003;

        /// <summary>胜利战利品数量。</summary>
        internal const int VictoryLootCount = 1;

        /// <summary>第 <paramref name="ordinal"/> 块地产（按棋盘索引递增顺序，0 基）的买入价。</summary>
        internal static int PriceForOrdinal(int ordinal)
        {
            return BasePropertyPrice + (PropertyPriceStep * ordinal);
        }

        /// <summary>地产升级费：<c>地价 × 50%</c>（全部地价都是 20 的倍数，除尽无余数）。</summary>
        internal static int UpgradeCostFor(int price)
        {
            return (price * UpgradeCostPercent) / 100;
        }

        /// <summary>
        /// 租金：<c>地价 × (10% + 15% × 等级)</c>，整数除法向下取整（地价恒为正，等价于 floor）。
        /// </summary>
        internal static int RentFor(int price, int level)
        {
            return (price * (RentPercentAtLevel0 + (RentPercentPerLevel * level))) / 100;
        }

        /// <summary>
        /// 把 <c>GameplayInitData.UnitCount</c> 夹成参战玩家数：≤ 0 → 默认 2、1..4 原样、&gt; 4 → 4。
        /// </summary>
        internal static int ClampPlayerCount(int unitCount)
        {
            if (unitCount <= 0)
            {
                return DefaultPlayers;
            }

            return unitCount > MaxPlayers ? MaxPlayers : unitCount;
        }

        /// <summary>是否为可买/可升级/可收租的地产格。</summary>
        internal static bool IsProperty(MonopolyTileKind kind)
        {
            return kind == MonopolyTileKind.Property;
        }
    }
}
