#nullable enable
namespace AIFramework.Kernel.Turn.Tactics
{
    /// <summary>
    /// 敌方 AI（Phase 1）：每个敌人按 <see cref="TacticsBoard"/> 的 index 升序行动一次，
    /// <b>若最近玩家单位已在攻击距离内就攻击，否则朝它走一步</b>。
    /// <para>
    /// <b>为什么只走一步</b>：一次走满移动力会让"玩家先手 4 格、敌方 3 格"的先后手差瞬间抹平；
    /// 一步一格保证玩家永远有拉开距离的机会，同时让 AI 行为可逐回合断言。
    /// </para>
    /// <para>
    /// <b>为什么按 UnitId 而不是 index 遍历</b>：敌方攻击可能打死玩家单位，导致数组 index 整体前移；
    /// 先把这一轮要行动的敌方 UnitId 抄进预分配缓冲，再逐个按 ID 重新定位，阵亡者跳过，全程不分配。
    /// </para>
    /// </summary>
    internal static class TacticsAi
    {
        /// <summary>执行一整轮敌方行动；玩家已全灭时提前收手。</summary>
        internal static void RunEnemyTurn(TacticsBoard board, int[] enemyIds)
        {
            int count = CollectEnemyIds(board, enemyIds);

            for (int i = 0; i < count; i++)
            {
                if (board.CountTeam(TacticsRules.PlayerTeam) == 0)
                {
                    return;
                }

                if (!board.TryFindIndex(enemyIds[i], out int enemyIndex))
                {
                    continue;
                }

                TacticsUnit enemy = board.GetUnit(enemyIndex);
                int targetIndex = board.NearestTeamUnit(TacticsRules.PlayerTeam, enemy.X, enemy.Y);
                if (targetIndex < 0)
                {
                    return;
                }

                TacticsUnit target = board.GetUnit(targetIndex);
                if (TacticsBoard.Manhattan(enemy.X, enemy.Y, target.X, target.Y) <= enemy.AttackRange)
                {
                    board.ApplyDamage(targetIndex, enemy.Attack);
                }
                else
                {
                    StepToward(board, enemyIndex, target.X, target.Y);
                }
            }
        }

        /// <summary>把当前存活敌方的 UnitId 按 index 升序抄进缓冲，返回抄入个数。</summary>
        private static int CollectEnemyIds(TacticsBoard board, int[] enemyIds)
        {
            int count = 0;
            for (int i = 0; i < board.UnitCount && count < enemyIds.Length; i++)
            {
                TacticsUnit unit = board.GetUnit(i);
                if (unit.Team != TacticsRules.EnemyTeam)
                {
                    continue;
                }

                enemyIds[count] = unit.UnitId;
                count++;
            }

            return count;
        }

        /// <summary>
        /// 朝 (targetX, targetY) 走一格：优先走距离更远的那根轴，该方向被占再退让到另一根轴；
        /// 两根轴都被挡则原地不动（不绕远路，避免 AI 在两格间来回抖动）。
        /// </summary>
        private static void StepToward(TacticsBoard board, int enemyIndex, int targetX, int targetY)
        {
            TacticsUnit enemy = board.GetUnit(enemyIndex);

            int dx = targetX - enemy.X;
            int dy = targetY - enemy.Y;
            int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int stepY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
            int distanceX = dx < 0 ? -dx : dx;
            int distanceY = dy < 0 ? -dy : dy;

            if (distanceX >= distanceY)
            {
                if (stepX != 0 && board.MoveUnit(enemyIndex, enemy.X + stepX, enemy.Y))
                {
                    return;
                }

                if (stepY != 0)
                {
                    board.MoveUnit(enemyIndex, enemy.X, enemy.Y + stepY);
                }

                return;
            }

            if (stepY != 0 && board.MoveUnit(enemyIndex, enemy.X, enemy.Y + stepY))
            {
                return;
            }

            if (stepX != 0)
            {
                board.MoveUnit(enemyIndex, enemy.X + stepX, enemy.Y);
            }
        }
    }
}
