using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Puzzle;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="PuzzleWorld"/> 的推箱子契约：3 关初始读数与尺寸、<c>LevelId</c> 夹取、地形/箱子越界口径、
    /// 撞墙与推不动不计数、合法推箱与归位计数、3 关固定解法序列、<c>Reset</c> 还原、<c>Step</c> 计数、
    /// 结算门禁与战利品，以及 <c>TryMove</c> 零分配。
    /// <para>
    /// 用例全部走公开 API（<see cref="IPuzzleReadView"/> / <see cref="IPuzzleInput"/>），不反射内核内部数组；
    /// 坐标一律是"x 向右、y 向上（y=0 底行）"的棋盘坐标，解法序列与关卡文本地图一一对应。
    /// </para>
    /// </summary>
    public sealed class PuzzleTests
    {
        // 方向编码：0=上 1=右 2=下 3=左（契约锁定，测试用字面量常量而非引用内核常量）。
        private const int Up = 0;

        private const int Right = 1;

        private const int Down = 2;

        private const int Left = 3;

        // 第 1 关（7×5）：玩家 (1,2)、箱子 (2,2)、目标点 (5,2)，墙 (3,3)/(4,1)。
        private const int Level1Width = 7;

        private const int Level1Height = 5;

        private const int Level1StartX = 1;

        private const int Level1StartY = 2;

        // 第 2 关（7×7）：玩家 (2,2)、箱子 (3,2)、目标点 (2,4)，墙 (3,3)/(5,3)。
        private const int Level2Width = 7;

        private const int Level2Height = 7;

        // 第 3 关（8×8）：玩家 (4,1)、箱子 (2,4)/(5,4)、目标点 (2,5)/(5,5)，墙 (3,2)/(4,2)。
        private const int Level3Width = 8;

        private const int Level3Height = 8;

        /// <summary>三关固定解法的步数（与下面的 <see cref="RunSolution"/> 调用逐字对应）。</summary>
        private const int Level1SolutionMoves = 3;

        private const int Level2SolutionMoves = 9;

        private const int Level3SolutionMoves = 9;

        // ── 初始读数与关卡数据 ──────────────────────────────────────────

        [Fact]
        public void Constructor_Level1_ExposesInitialReadings()
        {
            PuzzleWorld world = CreateWorld(1);

            Assert.Equal(Level1Width, world.Width);
            Assert.Equal(Level1Height, world.Height);
            Assert.Equal(1, world.LevelId);
            Assert.Equal(0, world.Moves);
            Assert.Equal(0, world.StepCount);
            Assert.Equal(0, world.BoxesOnGoal);
            Assert.Equal(1, world.BoxCount);
            Assert.False(world.IsSolved);
            Assert.False(world.IsFinished);
            Assert.Equal(Level1StartX, world.PlayerX);
            Assert.Equal(Level1StartY, world.PlayerY);

            Assert.Equal(PuzzleCell.Floor, world.GetCell(2, 2));
            Assert.True(world.HasBox(2, 2));
            Assert.False(world.HasBox(Level1StartX, Level1StartY));
            Assert.Equal(PuzzleCell.Goal, world.GetCell(5, 2));
            Assert.Equal(PuzzleCell.Wall, world.GetCell(3, 3));
            Assert.Equal(PuzzleCell.Wall, world.GetCell(0, 0));
        }

        [Fact]
        public void AllLevels_AreWithinEightByEight_WithBoxesGoalsAndWalls()
        {
            int[] expectedBoxCounts = { 1, 1, 2 };

            for (int levelId = 1; levelId <= 3; levelId++)
            {
                PuzzleWorld world = CreateWorld(levelId);

                Assert.Equal(levelId, world.LevelId);
                Assert.InRange(world.Width, 1, 8);
                Assert.InRange(world.Height, 1, 8);
                Assert.Equal(expectedBoxCounts[levelId - 1], world.BoxCount);
                Assert.Equal(0, world.BoxesOnGoal);
                Assert.False(world.IsSolved);

                int goals = 0;
                int walls = 0;
                int boxes = 0;
                for (int y = 0; y < world.Height; y++)
                {
                    for (int x = 0; x < world.Width; x++)
                    {
                        PuzzleCell cell = world.GetCell(x, y);
                        if (cell == PuzzleCell.Goal)
                        {
                            goals++;
                        }
                        else if (cell == PuzzleCell.Wall)
                        {
                            walls++;
                        }

                        if (world.HasBox(x, y))
                        {
                            boxes++;
                            // 箱子脚下的地形必须是可站立的地面或目标点。
                            Assert.True(cell == PuzzleCell.Floor || cell == PuzzleCell.Goal);
                        }
                    }
                }

                Assert.True(boxes == world.BoxCount);
                Assert.True(goals >= world.BoxCount);
                Assert.True(walls >= 1);

                // 玩家起点必须是可站立格且没有箱子。
                PuzzleCell startCell = world.GetCell(world.PlayerX, world.PlayerY);
                Assert.True(startCell == PuzzleCell.Floor || startCell == PuzzleCell.Goal);
                Assert.False(world.HasBox(world.PlayerX, world.PlayerY));
            }
        }

        [Fact]
        public void AllLevels_EncloseBoardWithWalls_SoOutOfBoundsMoveFoldsIntoWall()
        {
            // 三关的边框全是墙：玩家永远走不到棋盘外，"越界 move"在数据层等价于撞边框墙
            // （GetCell 越界也报 Wall，见 GetCell_OutOfBounds_ReturnsWall）。
            for (int levelId = 1; levelId <= 3; levelId++)
            {
                PuzzleWorld world = CreateWorld(levelId);

                for (int x = 0; x < world.Width; x++)
                {
                    Assert.Equal(PuzzleCell.Wall, world.GetCell(x, 0));
                    Assert.Equal(PuzzleCell.Wall, world.GetCell(x, world.Height - 1));
                }

                for (int y = 0; y < world.Height; y++)
                {
                    Assert.Equal(PuzzleCell.Wall, world.GetCell(0, y));
                    Assert.Equal(PuzzleCell.Wall, world.GetCell(world.Width - 1, y));
                }
            }
        }

        [Fact]
        public void LevelId_OutOfRange_ClampsToOne()
        {
            Assert.Equal(1, CreateWorld(0).LevelId);
            Assert.Equal(1, CreateWorld(9).LevelId);
            Assert.Equal(1, CreateWorld(-5).LevelId);
            Assert.Equal(1, CreateWorld(4).LevelId);

            // 夹取后的关卡数据也要跟着回落到第 1 关，不能只改 ID 不换图。
            Assert.Equal(Level1Width, CreateWorld(9).Width);
            Assert.Equal(Level1Height, CreateWorld(0).Height);
            Assert.Equal(1, CreateWorld(99).BoxCount);
        }

        [Fact]
        public void LevelId_InRange_IsPreserved()
        {
            Assert.Equal(2, CreateWorld(2).LevelId);
            Assert.Equal(3, CreateWorld(3).LevelId);
            Assert.Equal(Level2Width, CreateWorld(2).Width);
            Assert.Equal(Level2Height, CreateWorld(2).Height);
            Assert.Equal(Level3Width, CreateWorld(3).Width);
            Assert.Equal(Level3Height, CreateWorld(3).Height);
            Assert.Equal(2, CreateWorld(3).BoxCount);
        }

        // ── 越界口径 ───────────────────────────────────────────────────

        [Fact]
        public void GetCell_OutOfBounds_ReturnsWall()
        {
            PuzzleWorld world = CreateWorld(1);

            Assert.Equal(PuzzleCell.Wall, world.GetCell(-1, 0));
            Assert.Equal(PuzzleCell.Wall, world.GetCell(0, -1));
            Assert.Equal(PuzzleCell.Wall, world.GetCell(world.Width, 0));
            Assert.Equal(PuzzleCell.Wall, world.GetCell(0, world.Height));
            Assert.Equal(PuzzleCell.Wall, world.GetCell(99, 99));
        }

        [Fact]
        public void HasBox_OutOfBounds_ReturnsFalse()
        {
            PuzzleWorld world = CreateWorld(3);

            Assert.True(world.HasBox(2, 4));
            Assert.False(world.HasBox(-1, 4));
            Assert.False(world.HasBox(2, -1));
            Assert.False(world.HasBox(world.Width, 4));
            Assert.False(world.HasBox(2, world.Height));
            Assert.False(world.HasBox(99, 99));
        }

        [Fact]
        public void GetCell_ReportsTerrainUnderBox()
        {
            // 箱子压在普通地面上：地形仍是 Floor，箱子靠 HasBox 叠出来。
            PuzzleWorld groundBox = CreateWorld(3);
            Assert.Equal(PuzzleCell.Floor, groundBox.GetCell(5, 4));
            Assert.True(groundBox.HasBox(5, 4));

            // 箱子推到目标点上：地形仍是 Goal（渲染层自己叠两层），不能变成 Floor。
            PuzzleWorld goalBox = CreateWorld(1);
            Assert.True(goalBox.TryMove(Right));
            Assert.True(goalBox.TryMove(Right));
            Assert.True(goalBox.TryMove(Right));
            Assert.Equal(PuzzleCell.Goal, goalBox.GetCell(5, 2));
            Assert.True(goalBox.HasBox(5, 2));
        }

        // ── 非法移动：一律 false 且不消耗步数 ───────────────────────────

        [Fact]
        public void TryMove_IntoBorderWall_ReturnsFalseAndKeepsMoves()
        {
            PuzzleWorld world = CreateWorld(1);

            // 玩家左手边就是棋盘边界（越界由 GetCell 折算成 Wall）。
            Assert.False(world.TryMove(Left));
            Assert.Equal(0, world.Moves);
            Assert.Equal(Level1StartX, world.PlayerX);
            Assert.Equal(Level1StartY, world.PlayerY);
        }

        [Fact]
        public void TryMove_IntoInteriorWall_ReturnsFalseAndKeepsMoves()
        {
            // 第 3 关玩家 (4,1) 正上方 (4,2) 是内墙。
            PuzzleWorld world = CreateWorld(3);

            Assert.False(world.TryMove(Up));
            Assert.Equal(0, world.Moves);
            Assert.Equal(4, world.PlayerX);
            Assert.Equal(1, world.PlayerY);
        }

        [Fact]
        public void TryMove_InvalidDirection_ReturnsFalseAndKeepsMoves()
        {
            PuzzleWorld world = CreateWorld(1);

            Assert.False(world.TryMove(4));
            Assert.False(world.TryMove(-1));
            Assert.False(world.TryMove(99));
            Assert.Equal(0, world.Moves);
            Assert.Equal(Level1StartX, world.PlayerX);
            Assert.Equal(Level1StartY, world.PlayerY);
        }

        [Fact]
        public void TryMove_BlockedPush_ReturnsFalseWithoutCountingMove()
        {
            // 第 2 关：箱子 (3,2) 正上方 (3,3) 是墙，从下方推上去必须被拒。
            PuzzleWorld world = CreateWorld(2);

            Assert.True(world.TryMove(Down));
            Assert.True(world.TryMove(Right));
            Assert.Equal(2, world.Moves);

            Assert.False(world.TryMove(Up));
            Assert.Equal(2, world.Moves);
            Assert.Equal(3, world.PlayerX);
            Assert.Equal(1, world.PlayerY);
            Assert.True(world.HasBox(3, 2));
            Assert.False(world.HasBox(3, 3));
            Assert.Equal(0, world.BoxesOnGoal);

            // 再补一次撞边界：向下是 y=0 的边框墙。
            Assert.False(world.TryMove(Down));
            Assert.Equal(2, world.Moves);
        }

        // ── 合法推箱 ───────────────────────────────────────────────────

        [Fact]
        public void TryMove_PushesBoxAndPlayerOneStep()
        {
            PuzzleWorld world = CreateWorld(1);

            Assert.True(world.TryMove(Right));
            Assert.Equal(1, world.Moves);
            Assert.Equal(2, world.PlayerX);
            Assert.Equal(2, world.PlayerY);
            Assert.True(world.HasBox(3, 2));
            Assert.False(world.HasBox(2, 2));
            Assert.Equal(0, world.BoxesOnGoal);
            Assert.False(world.IsSolved);
        }

        // ── 三关固定解法 ───────────────────────────────────────────────

        [Fact]
        public void Solve_Level1_WithFixedSequence()
        {
            PuzzleWorld world = CreateWorld(1);

            RunSolution(world, Right, Right, Right);

            Assert.Equal(Level1SolutionMoves, world.Moves);
            Assert.Equal(1, world.BoxesOnGoal);
            Assert.True(world.IsSolved);
            Assert.True(world.IsFinished);
            Assert.True(world.HasBox(5, 2));
            Assert.Equal(4, world.PlayerX);
            Assert.Equal(2, world.PlayerY);
        }

        [Fact]
        public void Solve_Level2_WithFixedSequence()
        {
            PuzzleWorld world = CreateWorld(2);

            // 绕到箱子右侧 → 左推一格 → 转到箱子下方 → 连推两格进目标点。
            RunSolution(world, Down, Right, Right, Up, Left, Down, Left, Up, Up);

            Assert.Equal(Level2SolutionMoves, world.Moves);
            Assert.Equal(1, world.BoxesOnGoal);
            Assert.True(world.IsSolved);
            Assert.True(world.HasBox(2, 4));
            Assert.Equal(PuzzleCell.Goal, world.GetCell(2, 4));
            Assert.Equal(2, world.PlayerX);
            Assert.Equal(3, world.PlayerY);
        }

        [Fact]
        public void Solve_Level3_WithFixedSequence()
        {
            PuzzleWorld world = CreateWorld(3);

            // 先走右侧通道把 (5,4) 推上 (5,5)，再横穿到 (2,3) 把 (2,4) 推上 (2,5)。
            RunSolution(world, Right, Up, Up, Up, Down, Left, Left, Left, Up);

            Assert.Equal(Level3SolutionMoves, world.Moves);
            Assert.Equal(2, world.BoxesOnGoal);
            Assert.True(world.IsSolved);
            Assert.True(world.HasBox(5, 5));
            Assert.True(world.HasBox(2, 5));
            Assert.Equal(2, world.PlayerX);
            Assert.Equal(4, world.PlayerY);
        }

        [Fact]
        public void TryMove_PushingBoxOffGoal_DecrementsBoxesOnGoal()
        {
            PuzzleWorld world = CreateWorld(3);

            // 第 3 关有两个箱子：先把 (5,4) 推上 (5,5) 归位，此时还没解出。
            RunSolution(world, Right, Up, Up, Up);
            Assert.Equal(1, world.BoxesOnGoal);
            Assert.False(world.IsSolved);

            // 绕到 (5,6) 把同一个箱子往下推回 (5,4)：归位数必须回落到 0。
            RunSolution(world, Left, Up, Up, Right);
            Assert.Equal(8, world.Moves);
            Assert.Equal(1, world.BoxesOnGoal);

            Assert.True(world.TryMove(Down));
            Assert.Equal(9, world.Moves);
            Assert.Equal(0, world.BoxesOnGoal);
            Assert.False(world.IsSolved);
            Assert.False(world.HasBox(5, 5));
            Assert.True(world.HasBox(5, 4));
        }

        [Fact]
        public void TryMove_AfterSolved_ReturnsFalseAndFreezesBoard()
        {
            PuzzleWorld world = CreateWorld(1);
            RunSolution(world, Right, Right, Right);

            Assert.False(world.TryMove(Left));
            Assert.False(world.TryMove(Down));
            Assert.Equal(Level1SolutionMoves, world.Moves);
            Assert.Equal(4, world.PlayerX);
            Assert.Equal(2, world.PlayerY);
            Assert.True(world.IsSolved);
        }

        // ── Reset / Step ───────────────────────────────────────────────

        [Fact]
        public void Reset_RestoresInitialState_AndKeepsStepCount()
        {
            PuzzleWorld world = CreateWorld(1);

            // Step 只在未解出时累加，所以先在解出之前推两步统计。
            world.Step(1f / 60f);
            world.Step(1f / 60f);
            RunSolution(world, Right, Right, Right);

            Assert.True(world.IsSolved);
            Assert.Equal(2, world.StepCount);

            world.Reset();

            Assert.Equal(0, world.Moves);
            Assert.Equal(0, world.BoxesOnGoal);
            Assert.False(world.IsSolved);
            Assert.False(world.IsFinished);
            Assert.Equal(2, world.StepCount);
            Assert.Equal(Level1StartX, world.PlayerX);
            Assert.Equal(Level1StartY, world.PlayerY);
            Assert.True(world.HasBox(2, 2));
            Assert.False(world.HasBox(5, 2));

            // Reset 之后必须能重来一遍并再次解出（解谜没有失败态，允许无限重试）。
            Assert.True(world.TryMove(Right));
            Assert.Equal(Level1StartX + 1, world.PlayerX);
            world.Reset();
            RunSolution(world, Right, Right, Right);
            Assert.True(world.IsSolved);
        }

        [Fact]
        public void Step_CountsCalls_AndStopsAfterSolved()
        {
            PuzzleWorld world = CreateWorld(2);

            for (int i = 0; i < 5; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.Equal(5, world.StepCount);
            Assert.Equal(0, world.Moves);
            Assert.False(world.IsFinished);

            RunSolution(world, Down, Right, Right, Up, Left, Down, Left, Up, Up);
            Assert.True(world.IsFinished);

            world.Step(1f / 60f);
            world.Step(1f / 60f);
            Assert.Equal(5, world.StepCount);
        }

        // ── 结算 ───────────────────────────────────────────────────────

        [Fact]
        public void CollectResult_BeforeSolved_Throws()
        {
            PuzzleWorld world = CreateWorld(1);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            // 差一步也不能结算。
            RunSolution(world, Right, Right);
            Assert.False(world.IsSolved);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            PuzzleWorld world = CreateWorld(1);
            RunSolution(world, Right, Right, Right);

            Assert.NotNull(world.CollectResult());
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_ReturnsTurnVictoryWithSingleLoot()
        {
            PuzzleWorld world = CreateWorld(2);
            world.Step(1f / 60f);
            world.Step(1f / 60f);
            world.Step(1f / 60f);
            RunSolution(world, Down, Right, Right, Up, Left, Down, Left, Up, Up);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Puzzle, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(3, result.StepCount);
            Assert.Equal(Level2SolutionMoves, result.RemainingUnits);
            Assert.Empty(result.TriggeredTags);
            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(1003, loot.ItemId);
            Assert.Equal(1, loot.Count);
        }

        [Fact]
        public void CollectResult_ThenLocksInputStepAndReset()
        {
            PuzzleWorld world = CreateWorld(1);
            world.Step(1f / 60f);
            Assert.Equal(1, world.StepCount);
            RunSolution(world, Right, Right, Right);

            world.CollectResult();

            Assert.True(world.IsFinished);
            Assert.False(world.TryMove(Left));
            Assert.Equal(Level1SolutionMoves, world.Moves);

            world.Step(1f / 60f);
            Assert.Equal(1, world.StepCount);

            // 结算已取回 = 会话所有权已交还，Reset 不得再改状态。
            world.Reset();
            Assert.Equal(Level1SolutionMoves, world.Moves);
            Assert.Equal(4, world.PlayerX);
            Assert.True(world.IsSolved);
        }

        // ── 零分配 ─────────────────────────────────────────────────────

        [Fact]
        public void TryMove_AfterWarmup_DoesNotAllocate()
        {
            // 每轮 4 次调用：左/右各一次成功移动（玩家 (4,1) 与 (3,1) 来回），
            // 上（内墙 (4,2)）与非法方向各一次失败分支，覆盖"成功 + 被拒"两条路径。
            const int warmupRounds = 200;
            const int measuredRounds = 1000;

            PuzzleWorld world = CreateWorld(3);

            for (int i = 0; i < warmupRounds; i++)
            {
                Assert.True(world.TryMove(Left));
                Assert.True(world.TryMove(Right));
                Assert.False(world.TryMove(Up));
                Assert.False(world.TryMove(9));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < measuredRounds; i++)
            {
                world.TryMove(Left);
                world.TryMove(Right);
                world.TryMove(Up);
                world.TryMove(9);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);

            // 1000 轮里成功移动 2000 次、被拒 2000 次；箱子一个都没动过。
            Assert.False(world.IsSolved);
            Assert.Equal(0, world.BoxesOnGoal);
        }

        // ── 辅助 ───────────────────────────────────────────────────────

        /// <summary>按关卡 ID 造世界（种子/单位数对解谜无关，固定值保证可复现）。</summary>
        private static PuzzleWorld CreateWorld(int levelId)
        {
            return new PuzzleWorld(new GameplayInitData(levelId, 20261120, 0));
        }

        /// <summary>逐条执行固定解法，任何一步被拒都立刻失败（这样 <c>Moves</c> 断言才有意义）。</summary>
        private static void RunSolution(PuzzleWorld world, params int[] directions)
        {
            for (int i = 0; i < directions.Length; i++)
            {
                Assert.True(world.TryMove(directions[i]), "固定解法第 " + i + " 步应被接受。");
            }
        }
    }
}
