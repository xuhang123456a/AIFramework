#nullable enable
namespace AIFramework.Kernel.Turn.Tactics
{
    /// <summary>
    /// 战棋规则常量：全部锁成编译期常量，测试直接写期望值，运行期不读配置（Phase 1 没有战棋表）。
    /// <para>
    /// 数值口径（与 <see cref="TacticsWorld"/> 类注释的规则清单一一对应）：
    /// 棋盘 8×8；玩家单位 HP 20 / 攻 6 / 移 4 / 攻程 1；敌方单位 HP 15 / 攻 5 / 移 3 / 攻程 1；
    /// 敌方固定 3 个单位、玩家 1..3 个；回合上限 50。
    /// </para>
    /// </summary>
    internal static class TacticsRules
    {
        /// <summary>棋盘列数。</summary>
        internal const int BoardWidth = 8;

        /// <summary>棋盘行数。</summary>
        internal const int BoardHeight = 8;

        /// <summary>玩家阵营编码。</summary>
        internal const int PlayerTeam = 0;

        /// <summary>敌方阵营编码。</summary>
        internal const int EnemyTeam = 1;

        /// <summary>玩家单位数上限（<see cref="AIFramework.Contracts.Gameplay.GameplayInitData.UnitCount"/> 超过即夹到 3）。</summary>
        internal const int MaxPlayerUnits = 3;

        /// <summary>敌方单位数：固定 3，不随 <c>UnitCount</c> 变化。</summary>
        internal const int EnemyUnitCount = 3;

        /// <summary>棋盘容量：双方存活单位总数上限。</summary>
        internal const int MaxUnits = MaxPlayerUnits + EnemyUnitCount;

        /// <summary>玩家单位血量。</summary>
        internal const int PlayerHp = 20;

        /// <summary>玩家单位攻击力。</summary>
        internal const int PlayerAttack = 6;

        /// <summary>玩家单位移动力。</summary>
        internal const int PlayerMoveRange = 4;

        /// <summary>玩家单位攻击距离。</summary>
        internal const int PlayerAttackRange = 1;

        /// <summary>敌方单位血量。</summary>
        internal const int EnemyHp = 15;

        /// <summary>敌方单位攻击力。</summary>
        internal const int EnemyAttack = 5;

        /// <summary>敌方单位移动力（Phase 1 敌方每回合只走一步）。</summary>
        internal const int EnemyMoveRange = 3;

        /// <summary>敌方单位攻击距离。</summary>
        internal const int EnemyAttackRange = 1;

        /// <summary>回合数上限：<c>EndTurn</c> 把回合序号推到它之上即判平局。</summary>
        internal const int MaxTurns = 50;

        /// <summary>玩家单位 ID 基数：第 i 个玩家单位 ID = <c>PlayerUnitIdBase + i</c>（1..3）。</summary>
        internal const int PlayerUnitIdBase = 1;

        /// <summary>敌方单位 ID 基数：第 j 个敌方单位 ID = <c>EnemyUnitIdBase + j</c>（11..13）。</summary>
        internal const int EnemyUnitIdBase = 11;

        /// <summary>胜利战利品模板 ID。</summary>
        internal const int VictoryLootItemId = 1003;

        /// <summary>胜利战利品数量。</summary>
        internal const int VictoryLootCount = 1;
    }
}
