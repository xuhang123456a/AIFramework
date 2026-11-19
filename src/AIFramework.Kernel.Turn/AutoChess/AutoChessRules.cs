#nullable enable
namespace AIFramework.Kernel.Turn.AutoChess
{
    /// <summary>
    /// 自走棋规则常量：全部锁成编译期常量，测试直接写期望值，运行期不读配置（Phase 1 没有自走棋表）。
    /// <para>
    /// 数值口径（与 <see cref="AutoChessWorld"/> 类注释的规则清单一一对应）：
    /// 棋盘 8×8（<c>y = 0..3</c> 玩家半场、<c>y = 4..7</c> 敌方半场）；
    /// 玩家单位 HP 100 / 攻 20 / 攻程 1；敌方单位 HP 80 / 攻 12 / 攻程 1；
    /// 玩家 1..3 个、敌方固定 3 个；一轮战斗 30 个 tick；双方基地血量各 100，每轮胜负扣 10。
    /// </para>
    /// </summary>
    internal static class AutoChessRules
    {
        /// <summary>棋盘列数。</summary>
        internal const int BoardWidth = 8;

        /// <summary>棋盘行数。</summary>
        internal const int BoardHeight = 8;

        /// <summary>玩家半场行数：<c>y = 0..PlayerRowCount-1</c> 属于玩家，布阵只允许落在这里。</summary>
        internal const int PlayerRowCount = 4;

        /// <summary>敌方半场起始行（含）。</summary>
        internal const int EnemyRowStart = PlayerRowCount;

        /// <summary>玩家阵营编码。</summary>
        internal const int PlayerOwner = 0;

        /// <summary>敌方阵营编码。</summary>
        internal const int EnemyOwner = 1;

        /// <summary>玩家单位数上限（<see cref="AIFramework.Contracts.Gameplay.GameplayInitData.UnitCount"/> 超过即夹到 3）。</summary>
        internal const int MaxPlayerUnits = 3;

        /// <summary>敌方单位数：每回合固定 3 个，不随 <c>UnitCount</c> 变化。</summary>
        internal const int EnemyUnitCount = 3;

        /// <summary>槽位总数：玩家上限 + 敌方固定数，构造期一次分配。</summary>
        internal const int MaxUnits = MaxPlayerUnits + EnemyUnitCount;

        /// <summary>玩家单位 ID 基数：第 i 个玩家单位 ID = <c>PlayerUnitIdBase + i</c>（1..3）。</summary>
        internal const int PlayerUnitIdBase = 1;

        /// <summary>敌方单位 ID 基数：第 j 个敌方单位 ID = <c>EnemyUnitIdBase + j</c>（11..13）。</summary>
        internal const int EnemyUnitIdBase = 11;

        /// <summary>玩家单位血量。</summary>
        internal const int PlayerUnitHp = 100;

        /// <summary>玩家单位攻击力。</summary>
        internal const int PlayerUnitAttack = 20;

        /// <summary>玩家单位攻击距离。</summary>
        internal const int PlayerUnitRange = 1;

        /// <summary>敌方单位血量。</summary>
        internal const int EnemyUnitHp = 80;

        /// <summary>敌方单位攻击力。</summary>
        internal const int EnemyUnitAttack = 12;

        /// <summary>敌方单位攻击距离。</summary>
        internal const int EnemyUnitRange = 1;

        /// <summary>一轮自动战斗的推演 tick 数：30 个 tick 后强制结算。</summary>
        internal const int BattleTickCount = 30;

        /// <summary>玩家基地血量初值。</summary>
        internal const int InitialPlayerHp = 100;

        /// <summary>敌方基地血量初值。</summary>
        internal const int InitialEnemyHp = 100;

        /// <summary>每轮胜负对败方基地造成的伤害。</summary>
        internal const int BaseHpDamagePerRound = 10;

        /// <summary>胜利战利品模板 ID（首条）。</summary>
        internal const int VictoryLootItemId = 1003;

        /// <summary>胜利战利品数量（首条）。</summary>
        internal const int VictoryLootCount = 1;

        /// <summary>胜利战利品模板 ID（次条）。</summary>
        internal const int VictoryLootItemIdSecond = 1001;

        /// <summary>胜利战利品数量（次条）。</summary>
        internal const int VictoryLootCountSecond = 20;

        /// <summary>备战席坐标哨兵：未上场单位的 X / Y 一律报 <c>-1</c>。</summary>
        internal const int BenchCoordinate = -1;
    }
}
