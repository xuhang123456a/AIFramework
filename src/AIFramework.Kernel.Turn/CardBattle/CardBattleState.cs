#nullable enable
namespace AIFramework.Kernel.Turn.CardBattle
{
    /// <summary>
    /// Phase 1 规则常量：全部锁成编译期常量，测试直接写期望值，也避免运行期配置漂移。
    /// <para>数值来源见 <see cref="CardBattleWorld"/> 类注释里的规则清单。</para>
    /// </summary>
    internal static class CardBattleConstants
    {
        /// <summary>玩家初始 HP。</summary>
        internal const int PlayerInitialHp = 30;

        /// <summary>每个出战单位给敌人的 HP 贡献。</summary>
        internal const int EnemyHpPerUnit = 10;

        /// <summary>敌人 HP 下限（<c>Math.Max(10, UnitCount * 10)</c> 的 10）。</summary>
        internal const int MinEnemyHp = 10;

        /// <summary>每回合能量上限（同时也是回合开始时的重置值）。</summary>
        internal const int EnergyPerTurn = 3;

        /// <summary>每回合抽牌张数。</summary>
        internal const int DrawPerTurn = 5;

        /// <summary>手牌上限，超过即爆牌（进弃牌堆并计入 <c>BurnedCount</c>）。</summary>
        internal const int HandLimit = 10;

        /// <summary>敌人每回合的固定攻击力。</summary>
        internal const int EnemyAttackDamage = 6;

        /// <summary>回合数上限：回合序号超过它即判负。</summary>
        internal const int MaxTurns = 30;

        /// <summary>胜利战利品条目 ID。</summary>
        internal const int VictoryLootItemId = 1001;

        /// <summary>胜利战利品条目数量。</summary>
        internal const int VictoryLootCount = 1;
    }

    /// <summary>
    /// 卡牌战斗的纯数据状态：血量 / 护甲 / 能量 / 回合序号 / 各类计数。
    /// <para>
    /// 这里只有字段与派生判定，<b>不含</b>抽牌、出牌、敌人行动等任何流程分支——
    /// 流程归 <see cref="CardBattleWorld"/>，牌堆归 <see cref="CardBattleDeck"/>。
    /// </para>
    /// </summary>
    internal sealed class CardBattleState
    {
        internal CardBattleState(int enemyHp)
        {
            PlayerHp = CardBattleConstants.PlayerInitialHp;
            EnemyHp = enemyHp;
            PlayerBlock = 0;
            Energy = CardBattleConstants.EnergyPerTurn;
            TurnIndex = 1;
            ProcessedSteps = 0;
            PlayedCardCount = 0;
            ResultCollected = false;
        }

        /// <summary>玩家 HP，可为 0 或负（判定只看 &lt;= 0）。</summary>
        internal int PlayerHp { get; set; }

        /// <summary>敌人 HP，可为负（溢出伤害不截断，判定只看 &lt;= 0）。</summary>
        internal int EnemyHp { get; set; }

        /// <summary>玩家护甲，回合结束时清零。</summary>
        internal int PlayerBlock { get; set; }

        /// <summary>剩余能量。</summary>
        internal int Energy { get; set; }

        /// <summary>当前回合序号，从 1 开始。</summary>
        internal int TurnIndex { get; set; }

        /// <summary>累计推演步数（<see cref="CardBattleWorld.Step(float)"/> 的次数）。</summary>
        internal int ProcessedSteps { get; set; }

        /// <summary>累计成功打出的卡牌张数。</summary>
        internal int PlayedCardCount { get; set; }

        /// <summary>结算报告是否已被取回（取回后会话所有权已交还）。</summary>
        internal bool ResultCollected { get; set; }

        /// <summary>胜利条件：敌人 HP 被打空（敌人没有护甲，伤害直接落地）。</summary>
        internal bool IsVictory
        {
            get { return EnemyHp <= 0; }
        }

        /// <summary>失败条件：玩家 HP 被打空，或回合数超过 <see cref="CardBattleConstants.MaxTurns"/>。</summary>
        internal bool IsDefeat
        {
            get { return PlayerHp <= 0 || TurnIndex > CardBattleConstants.MaxTurns; }
        }

        /// <summary>
        /// 对局结束：胜负任一成立，或结算报告已被取回。
        /// 取回后仍保持"已结束"，保证 <c>Step</c>/<c>EndTurn</c>/<c>CollectResult</c> 不会二次推进。
        /// </summary>
        internal bool IsFinished
        {
            get { return ResultCollected || IsVictory || IsDefeat; }
        }
    }
}
