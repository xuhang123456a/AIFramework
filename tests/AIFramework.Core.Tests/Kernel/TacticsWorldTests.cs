using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Tactics;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="TacticsWorld"/> 的战棋契约：初始布阵与九字段读数、选中/移动/攻击的准入与拒绝
    /// （越界、超距、敌方单位、被占格、路径被堵）、阵亡移除后的 index 前移、敌方 AI 的"能打就打、否则走一步"、
    /// 全灭胜负、50 回合平局、<c>Step</c> 计数、结算门禁与战利品，以及移动路径零分配。
    /// <para>
    /// 用例全部走公开 API（<see cref="ITacticsReadView"/> / <see cref="ITacticsInput"/>），
    /// 不反射内核内部数组；坐标一律是"x 向右、y 向上"的棋盘坐标。
    /// </para>
    /// </summary>
    public sealed class TacticsWorldTests
    {
        private const int BoardWidth = 8;

        private const int BoardHeight = 8;

        private const int TurnLimit = 50;

        private const int PlayerUnitId = 1;

        private const int SecondPlayerUnitId = 2;

        private const int ThirdPlayerUnitId = 3;

        private const int EnemyUnitId = 11;

        private const int PlayerHp = 20;

        private const int PlayerAttack = 6;

        private const int PlayerMoveRange = 4;

        private const int EnemyHp = 15;

        private const int EnemyAttack = 5;

        // ── 初始布阵与读数 ──────────────────────────────────────────────

        [Fact]
        public void Constructor_PreparesBoardAndPlayerTurn()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.Equal(BoardWidth, world.Width);
            Assert.Equal(BoardHeight, world.Height);
            Assert.Equal(1, world.TurnIndex);
            Assert.Equal(0, world.CurrentTeam);
            Assert.Equal(6, world.UnitCount);
            Assert.Equal(-1, world.SelectedUnitId);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(-1, world.VictoryTeam);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void Constructor_PlacesPlayerUnitsOnBottomRowWithFixedStats()
        {
            TacticsWorld world = CreateWorld(3);

            for (int i = 0; i < 3; i++)
            {
                TacticsUnitView unit = world.GetUnit(i);
                Assert.Equal(PlayerUnitId + i, unit.UnitId);
                Assert.Equal(0, unit.Team);
                Assert.Equal(i, unit.X);
                Assert.Equal(0, unit.Y);
                Assert.Equal(PlayerHp, unit.Hp);
                Assert.Equal(PlayerHp, unit.MaxHp);
                Assert.Equal(PlayerAttack, unit.Attack);
                Assert.Equal(PlayerMoveRange, unit.MoveRange);
                Assert.Equal(1, unit.AttackRange);
            }
        }

        [Fact]
        public void Constructor_PlacesEnemyUnitsOnTopRowWithFixedStats()
        {
            TacticsWorld world = CreateWorld(3);

            for (int i = 0; i < 3; i++)
            {
                TacticsUnitView unit = world.GetUnit(3 + i);
                Assert.Equal(EnemyUnitId + i, unit.UnitId);
                Assert.Equal(1, unit.Team);
                Assert.Equal(i, unit.X);
                Assert.Equal(BoardHeight - 1, unit.Y);
                Assert.Equal(EnemyHp, unit.Hp);
                Assert.Equal(EnemyHp, unit.MaxHp);
                Assert.Equal(EnemyAttack, unit.Attack);
                Assert.Equal(3, unit.MoveRange);
                Assert.Equal(1, unit.AttackRange);
            }
        }

        [Fact]
        public void Constructor_UnitCountDrivesPlayerCount_AndIsClampedToOneToThree()
        {
            Assert.Equal(4, CreateWorld(1).UnitCount);
            Assert.Equal(5, CreateWorld(2).UnitCount);
            Assert.Equal(6, CreateWorld(3).UnitCount);

            // 越界输入夹到 1..3：0 → 1 个玩家单位，99 → 3 个，不抛异常也不放空棋盘。
            Assert.Equal(4, CreateWorld(0).UnitCount);
            Assert.Equal(6, CreateWorld(99).UnitCount);
            Assert.Equal(1, CreateWorld(0).GetUnit(0).UnitId);
            Assert.Equal(3, CreateWorld(99).GetUnit(2).UnitId);
        }

        [Fact]
        public void GetUnit_OutOfRange_Throws()
        {
            TacticsWorld world = CreateWorld(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetUnit(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetUnit(world.UnitCount));
        }

        [Fact]
        public void UnitAt_ReturnsIndexForOccupiedCell_AndMinusOneForEmptyOrOutside()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.Equal(0, world.UnitAt(0, 0));
            Assert.Equal(5, world.UnitAt(2, BoardHeight - 1));
            Assert.Equal(-1, world.UnitAt(4, 4));
            Assert.Equal(-1, world.UnitAt(-1, 0));
            Assert.Equal(-1, world.UnitAt(BoardWidth, 0));
            Assert.Equal(-1, world.UnitAt(0, BoardHeight));
        }

        // ── 选中 ────────────────────────────────────────────────────────

        [Fact]
        public void TrySelect_OwnUnit_SelectsThenTogglesOff()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.True(world.TrySelect(SecondPlayerUnitId));
            Assert.Equal(SecondPlayerUnitId, world.SelectedUnitId);

            // 再选一次同一个 ID = 取消选中。
            Assert.True(world.TrySelect(SecondPlayerUnitId));
            Assert.Equal(-1, world.SelectedUnitId);

            Assert.True(world.TrySelect(ThirdPlayerUnitId));
            Assert.Equal(ThirdPlayerUnitId, world.SelectedUnitId);
        }

        [Fact]
        public void TrySelect_RejectsEnemyAndUnknownUnit_WithoutChangingSelection()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.True(world.TrySelect(PlayerUnitId));
            Assert.False(world.TrySelect(EnemyUnitId));
            Assert.Equal(PlayerUnitId, world.SelectedUnitId);

            Assert.False(world.TrySelect(999));
            Assert.Equal(PlayerUnitId, world.SelectedUnitId);
        }

        // ── 移动 ────────────────────────────────────────────────────────

        [Fact]
        public void TryMove_ToReachableCell_MovesUnitAndUpdatesOccupancy()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.True(world.TryMove(PlayerUnitId, 1, 1));

            TacticsUnitView moved = world.GetUnit(0);
            Assert.Equal(1, moved.X);
            Assert.Equal(1, moved.Y);
            Assert.Equal(-1, world.UnitAt(0, 0));
            Assert.Equal(0, world.UnitAt(1, 1));
        }

        [Fact]
        public void TryMove_DetoursAroundBlockedCell_WhenPathExists()
        {
            // 玩家单位排在 (0,0)/(1,0)/(2,0)：到 (2,2) 的曼哈顿最短路被 (1,0) 堵住，
            // 但 4 步绕行路径 (0,1)→(1,1)→(2,1)→(2,2) 仍然存在，必须放行。
            TacticsWorld world = CreateWorld(3);

            Assert.True(world.TryMove(PlayerUnitId, 2, 2));
            Assert.Equal(2, world.GetUnit(0).X);
            Assert.Equal(2, world.GetUnit(0).Y);
        }

        [Fact]
        public void TryMove_BeyondMoveRange_IsRejected()
        {
            TacticsWorld world = CreateWorld(3);

            // 曼哈顿距离 5 > 移动力 4。
            Assert.False(world.TryMove(PlayerUnitId, 0, 5));
            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(0, world.GetUnit(0).Y);
        }

        [Fact]
        public void TryMove_OutOfBounds_IsRejected()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.False(world.TryMove(PlayerUnitId, -1, 0));
            Assert.False(world.TryMove(PlayerUnitId, BoardWidth, 0));
            Assert.False(world.TryMove(PlayerUnitId, 0, -1));
            Assert.False(world.TryMove(PlayerUnitId, 0, BoardHeight));
            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(0, world.GetUnit(0).Y);
        }

        [Fact]
        public void TryMove_OccupiedTargetCell_IsRejected()
        {
            TacticsWorld world = CreateWorld(3);

            // 目标格站着另一个己方单位。
            Assert.False(world.TryMove(PlayerUnitId, 1, 0));

            // 目标格就是自己脚下，同样算"被占"。
            Assert.False(world.TryMove(PlayerUnitId, 0, 0));

            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(0, world.GetUnit(0).Y);
            Assert.Equal(1, world.GetUnit(1).X);
        }

        [Fact]
        public void TryMove_EnemyUnit_IsRejected()
        {
            TacticsWorld world = CreateWorld(3);

            // 敌方单位不是当前阵营（0），即使目标格空也不行。
            Assert.False(world.TryMove(EnemyUnitId, 0, 6));
            Assert.Equal(0, world.GetUnit(3).X);
            Assert.Equal(BoardHeight - 1, world.GetUnit(3).Y);
        }

        [Fact]
        public void TryMove_PathBlockedByUnits_IsRejected_EvenWithinRange()
        {
            TacticsWorld world = CreateWorld(3);

            // 先让两个同伴把 (0,0) 的双向出口 (1,0) 与 (0,1) 全部堵死。
            Assert.True(world.TryMove(SecondPlayerUnitId, 0, 1));
            Assert.True(world.TryMove(ThirdPlayerUnitId, 1, 0));

            // (3,0) 的曼哈顿距离只有 3，但四邻全被封死，BFS 不可达 → 必须拒绝。
            Assert.False(world.TryMove(PlayerUnitId, 3, 0));
            Assert.False(world.TryMove(PlayerUnitId, 0, 2));
            Assert.Equal(0, world.GetUnit(0).X);
            Assert.Equal(0, world.GetUnit(0).Y);
        }

        // ── 攻击 ────────────────────────────────────────────────────────

        [Fact]
        public void TryAttack_AdjacentEnemy_DealsAttackerDamage()
        {
            TacticsWorld world = CreateWorld(3);
            AdvanceTurns(world, 6);

            Assert.Equal(1, world.GetUnit(3).Y);
            Assert.True(world.TryAttack(PlayerUnitId, EnemyUnitId));
            Assert.Equal(EnemyHp - PlayerAttack, world.GetUnit(3).Hp);

            // 攻击不消耗回合：回合序号仍是 6 次 EndTurn 后的 7。
            Assert.Equal(7, world.TurnIndex);
        }

        [Fact]
        public void TryAttack_OutOfRange_IsRejected()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.False(world.TryAttack(PlayerUnitId, EnemyUnitId));
            Assert.Equal(EnemyHp, world.GetUnit(3).Hp);
        }

        [Fact]
        public void TryAttack_FriendlyTarget_IsRejected()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.False(world.TryAttack(PlayerUnitId, SecondPlayerUnitId));
            Assert.Equal(PlayerHp, world.GetUnit(1).Hp);
        }

        [Fact]
        public void TryAttack_EnemyAttacker_IsRejected()
        {
            TacticsWorld world = CreateWorld(3);
            AdvanceTurns(world, 6);

            Assert.False(world.TryAttack(EnemyUnitId, PlayerUnitId));
            Assert.Equal(PlayerHp, world.GetUnit(0).Hp);
        }

        [Fact]
        public void TryAttack_Kill_RemovesUnitAndShiftsFollowingIndices()
        {
            TacticsWorld world = CreateWorld(3);
            AdvanceTurns(world, 6);

            // 敌 1（HP 15）挨 3 次 6 点伤害：
            // 第 1 击 9，第 2 击 3，第 3 击 -3 → 阵亡并移除。
            Assert.True(world.TryAttack(PlayerUnitId, EnemyUnitId));
            Assert.True(world.TryAttack(PlayerUnitId, EnemyUnitId));
            Assert.Equal(3, world.GetUnit(3).Hp);
            Assert.True(world.TryAttack(PlayerUnitId, EnemyUnitId));

            Assert.Equal(5, world.UnitCount);
            Assert.Equal(-1, world.UnitAt(0, 1));

            // 后续 index 整体前移：原 4/5 号位变成 3/4，UnitId 保持稳定。
            Assert.Equal(EnemyUnitId + 1, world.GetUnit(3).UnitId);
            Assert.Equal(EnemyUnitId + 2, world.GetUnit(4).UnitId);
            Assert.False(world.TryAttack(PlayerUnitId, EnemyUnitId));
        }

        // ── 敌方 AI 与回合推进 ──────────────────────────────────────────

        [Fact]
        public void EndTurn_EnemyAiStepsOncePerTurnThenAttacksWhenAdjacent()
        {
            TacticsWorld world = CreateWorld(3);

            world.EndTurn();

            // 敌方每回合只走一格：第一回合从 y=7 走到 y=6，玩家满血。
            Assert.Equal(6, world.GetUnit(3).Y);
            Assert.Equal(PlayerHp, world.GetUnit(0).Hp);
            Assert.Equal(0, world.CurrentTeam);
            Assert.Equal(2, world.TurnIndex);

            AdvanceTurns(world, 5);
            Assert.Equal(1, world.GetUnit(3).Y);
            Assert.Equal(PlayerHp, world.GetUnit(0).Hp);

            // 第 7 回合开始贴脸：AI 先判定攻击距离，命中后扣 5 点。
            world.EndTurn();
            Assert.Equal(PlayerHp - EnemyAttack, world.GetUnit(0).Hp);
            Assert.Equal(PlayerHp - EnemyAttack, world.GetUnit(1).Hp);
            Assert.Equal(PlayerHp - EnemyAttack, world.GetUnit(2).Hp);
            Assert.Equal(8, world.TurnIndex);
        }

        [Fact]
        public void EndTurn_ClearsSelection()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.True(world.TrySelect(PlayerUnitId));
            world.EndTurn();

            Assert.Equal(-1, world.SelectedUnitId);
            Assert.Equal(0, world.CurrentTeam);
        }

        // ── 胜负与结算 ──────────────────────────────────────────────────

        [Fact]
        public void AllEnemiesDead_EndsWithPlayerVictory()
        {
            TacticsWorld world = CreateWorld(3);
            AdvanceTurns(world, 6);

            // 三名玩家单位各自打死正对面的敌人：6 伤害 × 3 次 = 18 ≥ 15。
            KillWith(world, PlayerUnitId, EnemyUnitId);
            Assert.False(world.IsFinished);

            KillWith(world, SecondPlayerUnitId, EnemyUnitId + 1);
            Assert.False(world.IsFinished);

            KillWith(world, ThirdPlayerUnitId, EnemyUnitId + 2);

            Assert.True(world.IsFinished);
            Assert.Equal(0, world.VictoryTeam);
            Assert.Equal(3, world.UnitCount);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(3, result.RemainingUnits);
            Assert.Equal(0, result.StepCount);
            Assert.Empty(result.TriggeredTags);
            Assert.Single(result.Loots);
            Assert.Equal(1003, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
        }

        [Fact]
        public void AllPlayersDead_EndsWithEnemyVictoryAndNoLoot()
        {
            TacticsWorld world = CreateWorld(3);

            // 6 回合走位 + 4 回合挨打（每回合每单位 5 点，20 点血量正好清空）。
            AdvanceTurns(world, 10);

            Assert.True(world.IsFinished);
            Assert.Equal(1, world.VictoryTeam);
            Assert.Equal(3, world.UnitCount);
            Assert.Equal(-1, world.UnitAt(0, 0));

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Equal(0, result.RemainingUnits);
            Assert.Empty(result.Loots);
        }

        [Fact]
        public void TurnLimit_AfterFiftyEndTurns_EndsInDrawWithoutLoot()
        {
            // 1 名玩家单位 vs 3 名敌人：玩家长腿（移 4）只逃不打，敌方每回合只走一格，必然拖到回合上限。
            TacticsWorld world = CreateWorld(1);

            for (int turn = 0; turn < TurnLimit; turn++)
            {
                FleeToFarthestCell(world, PlayerUnitId);
                world.EndTurn();
            }

            Assert.True(world.IsFinished);
            Assert.Equal(TurnLimit + 1, world.TurnIndex);
            Assert.Equal(1, world.VictoryTeam);

            // 玩家单位仍然活着：平局不是"被打死"。
            Assert.Equal(4, world.UnitCount);
            Assert.Equal(PlayerUnitId, world.GetUnit(0).UnitId);
            Assert.True(world.GetUnit(0).Hp > 0);

            GameplayResult result = world.CollectResult();
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
        }

        [Fact]
        public void EndTurn_AfterFinish_IsNoOp()
        {
            TacticsWorld world = CreateWorld(3);
            AdvanceTurns(world, 10);

            Assert.True(world.IsFinished);
            int turnIndex = world.TurnIndex;
            int unitCount = world.UnitCount;

            world.EndTurn();

            Assert.Equal(turnIndex, world.TurnIndex);
            Assert.Equal(unitCount, world.UnitCount);
            Assert.False(world.TryMove(PlayerUnitId, 0, 1));
        }

        [Fact]
        public void CollectResult_BeforeFinish_Throws()
        {
            TacticsWorld world = CreateWorld(3);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            TacticsWorld world = CreateWorld(3);
            AdvanceTurns(world, 10);

            world.CollectResult();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
            Assert.True(world.IsFinished);
        }

        // ── Step ────────────────────────────────────────────────────────

        [Fact]
        public void Step_CountsStepsOnly_AndStopsAfterFinish()
        {
            TacticsWorld world = CreateWorld(3);

            for (int i = 0; i < 5; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.Equal(5, world.ProcessedSteps);

            AdvanceTurns(world, 10);
            Assert.True(world.IsFinished);

            // 结束后 Step 为空操作，不累加计数。
            world.Step(1f / 60f);
            Assert.Equal(5, world.ProcessedSteps);
            Assert.Equal(5, world.CollectResult().StepCount);
        }

        // ── 零分配 ──────────────────────────────────────────────────────

        [Fact]
        public void TryMove_AfterWarmup_DoesNotAllocate()
        {
            TacticsWorld world = CreateWorld(3);

            // 预热：让 JIT/内联与首次路径分配都发生在测量之前。
            for (int i = 0; i < 200; i++)
            {
                world.TryMove(PlayerUnitId, 0, 1);
                world.TryMove(PlayerUnitId, 0, 0);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.TryMove(PlayerUnitId, 0, 1);
                world.TryMove(PlayerUnitId, 0, 0);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        // ── 辅助 ────────────────────────────────────────────────────────

        private static TacticsWorld CreateWorld(int unitCount)
        {
            return new TacticsWorld(new GameplayInitData(1, 20261120, unitCount));
        }

        private static void AdvanceTurns(TacticsWorld world, int turns)
        {
            for (int i = 0; i < turns; i++)
            {
                world.EndTurn();
            }
        }

        private static void KillWith(TacticsWorld world, int attackerUnitId, int targetUnitId)
        {
            while (world.TryAttack(attackerUnitId, targetUnitId))
            {
                // 一直打到目标阵亡（每击 6 点，15 点血最多 3 击）。
            }
        }

        /// <summary>
        /// 把单位挪到"离最近敌人最远"的格子：按分数降序尝试候选格，第一个可达的即落地。
        /// 平局按 <c>y * Width + x</c> 升序，保证同一局面的走位完全确定。
        /// </summary>
        private static void FleeToFarthestCell(TacticsWorld world, int unitId)
        {
            List<CellScore> candidates = new List<CellScore>(world.Width * world.Height);

            for (int y = 0; y < world.Height; y++)
            {
                for (int x = 0; x < world.Width; x++)
                {
                    if (world.UnitAt(x, y) >= 0)
                    {
                        continue;
                    }

                    candidates.Add(new CellScore(x, y, NearestEnemyDistance(world, x, y)));
                }
            }

            candidates.Sort(CompareByScoreThenPosition);

            for (int i = 0; i < candidates.Count; i++)
            {
                if (world.TryMove(unitId, candidates[i].X, candidates[i].Y))
                {
                    return;
                }
            }
        }

        private static int NearestEnemyDistance(TacticsWorld world, int x, int y)
        {
            int nearest = int.MaxValue;

            for (int i = 0; i < world.UnitCount; i++)
            {
                TacticsUnitView unit = world.GetUnit(i);
                if (unit.Team != 1)
                {
                    continue;
                }

                int distance = Math.Abs(unit.X - x) + Math.Abs(unit.Y - y);
                if (distance < nearest)
                {
                    nearest = distance;
                }
            }

            return nearest;
        }

        private static int CompareByScoreThenPosition(CellScore a, CellScore b)
        {
            if (a.Score != b.Score)
            {
                return b.Score.CompareTo(a.Score);
            }

            return ((a.Y * BoardWidth) + a.X).CompareTo((b.Y * BoardWidth) + b.X);
        }

        private readonly struct CellScore
        {
            internal CellScore(int x, int y, int score)
            {
                X = x;
                Y = y;
                Score = score;
            }

            internal int X { get; }

            internal int Y { get; }

            internal int Score { get; }
        }
    }
}
