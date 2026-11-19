#nullable enable
namespace AIFramework.Kernel.Turn.AutoChess
{
    /// <summary>
    /// 自走棋战斗推演：30 个 tick 的确定性自动战斗，以及"打完之后谁赢"的判定口径。
    /// 只读写 <see cref="AutoChessBoard"/>，不碰基地血量与回合流程（那属于 <see cref="AutoChessWorld"/>）。
    /// <para>
    /// <b>tick 内行动顺序</b>：按槽位 0 → SlotCount-1 逐个行动，即先玩家（1..3）后敌方（11..13）。
    /// 每个存活单位：与最近敌人的曼哈顿距离 <c>&lt;= Range</c> 就打最近敌人（等距取槽位更小者），
    /// 否则朝最近敌人走 1 格。本 tick 内被击杀的单位会让出槽位（死亡即清占位），轮到它时因血量 &lt;= 0 直接跳过。
    /// </para>
    /// <para>
    /// <b>走位与随机</b>：直线可达（只有一轴需要缩短）时不抽随机；两轴都需要缩短时，
    /// 由 <see cref="AutoChessRandom"/> 抽一次决定本 tick 先沿哪一轴走。若该轴的目标格被占，
    /// 按契约"目标格被占则不动"原地不动，<b>不做二次轴回退</b>——回退会让走位依赖遍历顺序，
    /// 也违背"一格一步"的可解释性。
    /// </para>
    /// <para>
    /// <b>胜负口径</b>：敌方全灭 → 玩家胜；否则玩家全灭（含一个都没上场）→ 玩家负；
    /// 都没全灭则比双方棋盘上存活单位的总血量，<b>血量相等判玩家负</b>（守方守住；契约只有胜负两态，没有平局）。
    /// </para>
    /// <para>
    /// <b>零分配</b>：只有一个双重循环与结构体拷贝，不申请托管堆内存。
    /// </para>
    /// </summary>
    internal static class AutoChessBattle
    {
        /// <summary>
        /// 推演一轮战斗：连续 <see cref="AutoChessRules.BattleTickCount"/> 个 tick，每个 tick 让所有存活单位按槽位顺序各行动一次。
        /// 一方全灭后剩余 tick 里的单位找不到目标，自动空转（不消耗随机数），因此提前结束与跑满 30 tick 的随机流完全一致。
        /// </summary>
        internal static void Resolve(AutoChessBoard board, ref AutoChessRandom random)
        {
            for (int tick = 0; tick < AutoChessRules.BattleTickCount; tick++)
            {
                int slotCount = board.SlotCount;
                for (int slot = 0; slot < slotCount; slot++)
                {
                    Act(board, ref random, slot);
                }
            }
        }

        /// <summary>按剩余局面判胜负：返回胜方阵营（<see cref="AutoChessRules.PlayerOwner"/> / <see cref="AutoChessRules.EnemyOwner"/>）。</summary>
        internal static int ResolveWinnerOwner(AutoChessBoard board)
        {
            if (board.CountOnBoardAlive(AutoChessRules.EnemyOwner) == 0)
            {
                return AutoChessRules.PlayerOwner;
            }

            if (board.CountOnBoardAlive(AutoChessRules.PlayerOwner) == 0)
            {
                return AutoChessRules.EnemyOwner;
            }

            int playerHp = board.TotalOnBoardHp(AutoChessRules.PlayerOwner);
            int enemyHp = board.TotalOnBoardHp(AutoChessRules.EnemyOwner);

            return playerHp > enemyHp ? AutoChessRules.PlayerOwner : AutoChessRules.EnemyOwner;
        }

        /// <summary>让第 <paramref name="slot"/> 个槽位行动一次：能打就打，否则走一格。</summary>
        private static void Act(AutoChessBoard board, ref AutoChessRandom random, int slot)
        {
            AutoChessUnit unit = board.GetUnit(slot);
            if (!unit.IsOnBoard)
            {
                return;
            }

            int target = board.FindNearestOpponent(slot);
            if (target < 0)
            {
                return;
            }

            AutoChessUnit enemy = board.GetUnit(target);
            if (AutoChessBoard.Manhattan(unit.X, unit.Y, enemy.X, enemy.Y) <= unit.Range)
            {
                board.ApplyDamage(target, unit.Attack);
                return;
            }

            StepToward(board, slot, unit, enemy, ref random);
        }

        /// <summary>朝 <paramref name="enemy"/> 走 1 格；两轴都要缩短时用随机源决定先走哪一轴。</summary>
        private static void StepToward(
            AutoChessBoard board,
            int slot,
            in AutoChessUnit unit,
            in AutoChessUnit enemy,
            ref AutoChessRandom random)
        {
            int dx = enemy.X - unit.X;
            int dy = enemy.Y - unit.Y;
            int stepX = dx == 0 ? 0 : (dx > 0 ? 1 : -1);
            int stepY = dy == 0 ? 0 : (dy > 0 ? 1 : -1);

            if (stepX != 0 && stepY != 0)
            {
                // 两轴都有效：本 tick 先试哪一轴由种子决定（同种子 ⇒ 同走位）。
                if (random.NextIndex(2) == 0)
                {
                    board.TryStep(slot, unit.X + stepX, unit.Y);
                }
                else
                {
                    board.TryStep(slot, unit.X, unit.Y + stepY);
                }

                return;
            }

            if (stepX != 0)
            {
                board.TryStep(slot, unit.X + stepX, unit.Y);
                return;
            }

            if (stepY != 0)
            {
                board.TryStep(slot, unit.X, unit.Y + stepY);
            }
        }
    }
}
