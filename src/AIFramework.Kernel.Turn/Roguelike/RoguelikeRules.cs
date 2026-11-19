#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Roguelike
{
    /// <summary>
    /// 肉鸽卡牌规则常量：全部锁成编译期常量，测试直接写期望值，运行期不读配置（Phase 1 没有肉鸽表）。
    /// <para>
    /// 数值口径（与 <see cref="RoguelikeWorld"/> 类注释的规则清单一一对应）：
    /// 地图 8 层线性；玩家 50 HP；初始卡组 5 张（打击 ×3 / 防御 ×2）；每回合抽 5 张、能量 3；
    /// 敌人 Battle 20HP·攻 6·赏 15 / Elite 32HP·攻 9·赏 25 / Boss 60HP·攻 12·赏 60；
    /// 商店花 50 金币买 1 张牌；营地回 20 HP。
    /// </para>
    /// </summary>
    internal static class RoguelikeRules
    {
        /// <summary>地图层数（也是节点总数：每层只有一个候选节点）。</summary>
        internal const int MapNodeCount = 8;

        /// <summary>第 1 层的 index（固定普通战斗）。</summary>
        internal const int FirstNodeIndex = 0;

        /// <summary>最后一层的 index（固定 Boss）。</summary>
        internal const int BossNodeIndex = MapNodeCount - 1;

        /// <summary>玩家 HP 上限（也是初始 HP）。</summary>
        internal const int PlayerMaxHp = 50;

        /// <summary>营地回复量（受 <see cref="PlayerMaxHp"/> 上限约束）。</summary>
        internal const int RestHealAmount = 20;

        /// <summary>商店单价；金币不足即跳过购买。</summary>
        internal const int ShopPrice = 50;

        /// <summary>每回合抽牌数（也是手牌上限）。</summary>
        internal const int HandSize = 5;

        /// <summary>每回合能量。</summary>
        internal const int EnergyPerTurn = 3;

        /// <summary>初始卡组里的打击张数。</summary>
        internal const int StarterStrikeCount = 3;

        /// <summary>初始卡组里的防御张数。</summary>
        internal const int StarterDefendCount = 2;

        /// <summary>
        /// 卡组容量上限：<see cref="RoguelikeDeck"/> 在构造期按它一次分配定长数组。
        /// <para>
        /// 理论峰值 = 初始 5 + 战斗奖励 7（中段最多 6 场 + Boss 1 场）+ 商店最多 5 = 17；
        /// 取 32 留一倍冗余，溢出属于内核不变量被破坏（<see cref="RoguelikeDeck.AddCard"/> 显式抛异常），
        /// 不会静默丢牌。
        /// </para>
        /// </summary>
        internal const int MaxDeckSize = 32;

        /// <summary>普通战斗的敌人 HP。</summary>
        internal const int BattleEnemyHp = 20;

        /// <summary>精英战斗的敌人 HP。</summary>
        internal const int EliteEnemyHp = 32;

        /// <summary>Boss 战斗的敌人 HP。</summary>
        internal const int BossEnemyHp = 60;

        /// <summary>普通战斗的敌人攻击力。</summary>
        internal const int BattleEnemyAttack = 6;

        /// <summary>精英战斗的敌人攻击力。</summary>
        internal const int EliteEnemyAttack = 9;

        /// <summary>Boss 战斗的敌人攻击力。</summary>
        internal const int BossEnemyAttack = 12;

        /// <summary>普通战斗胜利的金币。</summary>
        internal const int BattleGoldReward = 15;

        /// <summary>精英战斗胜利的金币。</summary>
        internal const int EliteGoldReward = 25;

        /// <summary>Boss 战斗胜利的金币。</summary>
        internal const int BossGoldReward = 60;

        /// <summary>通关战利品模板 ID（Boss 通关）。</summary>
        internal const int ClearLootItemId = 1003;

        /// <summary>通关战利品数量。</summary>
        internal const int ClearLootCount = 1;

        /// <summary>通关金币战利品模板 ID（Boss 通关）。</summary>
        internal const int ClearGoldLootItemId = 1001;

        /// <summary>通关金币战利品数量。</summary>
        internal const int ClearGoldLootCount = 50;

        /// <summary>节点种类 → 敌人 HP。<c>Shop</c>/<c>Rest</c> 不会被当成战斗节点，回落成普通战斗值。</summary>
        internal static int EnemyHpFor(RoguelikeNodeKind kind)
        {
            switch (kind)
            {
                case RoguelikeNodeKind.Elite:
                    return EliteEnemyHp;
                case RoguelikeNodeKind.Boss:
                    return BossEnemyHp;
                default:
                    return BattleEnemyHp;
            }
        }

        /// <summary>节点种类 → 敌人每回合攻击力。</summary>
        internal static int EnemyAttackFor(RoguelikeNodeKind kind)
        {
            switch (kind)
            {
                case RoguelikeNodeKind.Elite:
                    return EliteEnemyAttack;
                case RoguelikeNodeKind.Boss:
                    return BossEnemyAttack;
                default:
                    return BattleEnemyAttack;
            }
        }

        /// <summary>节点种类 → 胜利金币。</summary>
        internal static int GoldRewardFor(RoguelikeNodeKind kind)
        {
            switch (kind)
            {
                case RoguelikeNodeKind.Elite:
                    return EliteGoldReward;
                case RoguelikeNodeKind.Boss:
                    return BossGoldReward;
                default:
                    return BattleGoldReward;
            }
        }

        /// <summary>是否为战斗节点（Battle / Elite / Boss）。</summary>
        internal static bool IsBattleKind(RoguelikeNodeKind kind)
        {
            return kind == RoguelikeNodeKind.Battle
                || kind == RoguelikeNodeKind.Elite
                || kind == RoguelikeNodeKind.Boss;
        }
    }
}
