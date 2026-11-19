using System;
using System.Reflection;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Tetris;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="TetrisWorld"/> 的俄罗斯方块契约：10×20 空盘与出生点、7-bag 可复现性、左右移动与边界拒绝、
    /// 旋转（含踢墙与拒绝）、软降/硬降的落点与计分、1/2/3/4 行消行的分数与等级/间隔换算、Game Over、结算门禁，
    /// 以及"预热后移动 + 推进"的零分配基线。
    /// <para>
    /// <b>布场方式（白盒夹具）</b>：契约刻意不暴露任何"写棋盘"入口（只有左右/旋转/下落这类意图），
    /// 而"消 1/2/4 行"必须从<b>精确初始局面</b>出发才有意义，因此本文件用反射把已锁定格写进内核棋盘数组，
    /// 再逐格回读校验（<see cref="InjectFixture"/>）。内核一旦改字段名或索引公式，回读断言会立刻失败，
    /// 不会静默跳过；除此之外的用例全部只走公开 API。
    /// </para>
    /// <para>
    /// <b>几何口径</b>：<see cref="HasCell"/> 系列是测试侧独立复刻的方块几何（4×4 包围盒 + 顺时针
    /// <c>(x, y) → (3 - y, x)</c>），用来断言"转完之后应该落在哪、占哪几格"；它与内核实现是两份独立数据，
    /// 对不上就说明规则被改坏了。
    /// </para>
    /// </summary>
    public sealed class TetrisTests
    {
        private const int BoardWidth = 10;

        private const int BoardHeight = 20;

        private const int Seed = 20261120;

        /// <summary>推演/循环的安全上限，防止行为回归时测试挂死。</summary>
        private const int LoopGuard = 600;

        // ── 测试侧方块几何（与规则文档逐格对应）────────────────────────────
        // 4×4 包围盒，行优先位序 bit(y * 4 + x)。七种方块各一份出生朝向：
        // I: 顶行四格 | O: 中间 2×2 | T: 顶行三格 + 下方中格 | S/Z: 两行错位 | J/L: 一角 + 下方三格
        private static readonly uint[] BaseMasks =
        {
            0x000Fu,
            0x0660u,
            0x0027u,
            0x0036u,
            0x0063u,
            0x0071u,
            0x0074u
        };

        // ── 初始状态 ────────────────────────────────────────────────────

        [Fact]
        public void Constructor_InitialBoard_IsTenByTwentyAndEmpty()
        {
            TetrisWorld world = CreateWorld();

            Assert.Equal(BoardWidth, world.Width);
            Assert.Equal(BoardHeight, world.Height);
            Assert.Equal(0, world.FilledCellCount);
            Assert.Equal(0, world.ClearedLines);
            Assert.Equal(0, world.Score);
            Assert.Equal(1, world.Level);
            Assert.Equal(800, world.DropIntervalMs);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(0, world.PieceCount);
            Assert.False(world.IsGameOver);
            Assert.False(world.IsFinished);

            for (int y = 0; y < BoardHeight; y++)
            {
                for (int x = 0; x < BoardWidth; x++)
                {
                    Assert.Equal(0, world.CellAt(x, y));
                }
            }
        }

        [Fact]
        public void Constructor_SpawnsFirstPieceAtTopCenter()
        {
            TetrisWorld world = CreateWorld();

            Assert.Equal(3, world.CurrentX);
            Assert.Equal(0, world.CurrentY);
            Assert.InRange((int)world.CurrentKind, 0, 6);
            Assert.InRange(world.NextKindRaw, 0, 6);

            // 7-bag 同一袋内七种各发一次，所以"当前块"与"下一块"绝不可能是同一种。
            Assert.NotEqual((int)world.CurrentKind, world.NextKindRaw);

            // 预取是纯读：连读两次值不变，也不会把随机推进掉（落点见 Bag_NextKindRaw_PredictsNextSpawn）。
            int firstRead = world.NextKindRaw;
            int secondRead = world.NextKindRaw;
            Assert.Equal(firstRead, secondRead);
        }

        [Fact]
        public void CellAt_OutOfBounds_ReturnsMinusOne()
        {
            TetrisWorld world = CreateWorld();

            Assert.Equal(-1, world.CellAt(-1, 0));
            Assert.Equal(-1, world.CellAt(0, -1));
            Assert.Equal(-1, world.CellAt(BoardWidth, 0));
            Assert.Equal(-1, world.CellAt(0, BoardHeight));
            Assert.Equal(-1, world.CellAt(-1, -1));
            Assert.Equal(-1, world.CellAt(int.MinValue, int.MaxValue));
            Assert.Equal(0, world.CellAt(BoardWidth - 1, BoardHeight - 1));
        }

        [Fact]
        public void CellAt_LockedCells_ReturnKindPlusOne()
        {
            TetrisWorld world = CreateWorld();
            TetrominoKind kind = world.CurrentKind;

            world.HardDrop();

            int[] cells = CollectCells(world);
            Assert.Equal(8, cells.Length);
            Assert.Equal(4, world.FilledCellCount);
            for (int i = 0; i < cells.Length; i += 2)
            {
                Assert.Equal((int)kind + 1, world.CellAt(cells[i], cells[i + 1]));
            }
        }

        // ── 7-bag 序列 ──────────────────────────────────────────────────

        [Fact]
        public void Bag_SameSeed_ProducesSameFourteenPieceSequence()
        {
            int[] first = CollectKindSequence(CreateWorld(Seed), 14);
            int[] second = CollectKindSequence(CreateWorld(Seed), 14);

            Assert.Equal(14, first.Length);
            AssertSameSequence(first, second);
        }

        [Fact]
        public void Bag_FirstSevenPieces_ContainEachKindExactlyOnce()
        {
            int[] sequence = CollectKindSequence(CreateWorld(Seed), 7);

            int[] seen = new int[7];
            foreach (int kind in sequence)
            {
                seen[kind]++;
            }

            for (int kind = 0; kind < 7; kind++)
            {
                Assert.Equal(1, seen[kind]);
            }
        }

        [Fact]
        public void Bag_DifferentSeeds_ProduceDifferentSequences()
        {
            int[] first = CollectKindSequence(CreateWorld(1), 7);
            int[] second = CollectKindSequence(CreateWorld(2), 7);

            Assert.False(AreEqual(first, second));
        }

        [Fact]
        public void Bag_NextKindRaw_PredictsNextSpawn()
        {
            TetrisWorld world = CreateWorld();

            int predicted = world.NextKindRaw;
            world.HardDrop();

            Assert.Equal(predicted, (int)world.CurrentKind);
            Assert.Equal(1, world.PieceCount);
        }

        // ── 左右移动与边界 ──────────────────────────────────────────────

        [Fact]
        public void TryMoveLeft_StopsAtLeftBoundary_AndRejectsFurtherMoves()
        {
            TetrisWorld world = CreateWorld();
            int limit = MinOriginX((int)world.CurrentKind, 0);

            int moves = 0;
            while (world.TryMoveLeft())
            {
                moves++;
                Assert.True(moves <= BoardWidth, "左移未在边界停下");
            }

            Assert.Equal(3 - limit, moves);
            Assert.Equal(limit, world.CurrentX);
            Assert.Equal(0, world.CurrentY);

            int[] before = Snapshot(world);
            Assert.False(world.TryMoveLeft());
            Assert.Equal(limit, world.CurrentX);
            AssertSameSequence(before, Snapshot(world));
        }

        [Fact]
        public void TryMoveRight_StopsAtRightBoundary_AndRejectsFurtherMoves()
        {
            TetrisWorld world = CreateWorld();
            int limit = MaxOriginX((int)world.CurrentKind, 0);

            int moves = 0;
            while (world.TryMoveRight())
            {
                moves++;
                Assert.True(moves <= BoardWidth, "右移未在边界停下");
            }

            Assert.Equal(limit - 3, moves);
            Assert.Equal(limit, world.CurrentX);

            Assert.False(world.TryMoveRight());
            Assert.Equal(limit, world.CurrentX);
        }

        [Fact]
        public void MoveLeftAndRight_ShiftColumnByOne_WithoutTouchingScoreOrSteps()
        {
            TetrisWorld world = CreateWorld();
            int startX = world.CurrentX;

            Assert.True(world.TryMoveLeft());
            Assert.Equal(startX - 1, world.CurrentX);

            Assert.True(world.TryMoveRight());
            Assert.Equal(startX, world.CurrentX);

            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(0, world.FilledCellCount);
        }

        [Fact]
        public void HardDrop_Twice_StacksWithoutOverwriting()
        {
            TetrisWorld world = CreateWorld();

            world.HardDrop();
            Assert.Equal(4, world.FilledCellCount);

            world.HardDrop();
            Assert.Equal(8, world.FilledCellCount);
            Assert.Equal(2, world.PieceCount);
            Assert.True(TopFilledY(world) <= BoardHeight - 2, "第二块必须叠在第一块上方，不能原地覆盖");
        }

        // ── 旋转 ────────────────────────────────────────────────────────

        [Fact]
        public void TryRotate_OnTSpawn_ProducesClockwiseFootprint()
        {
            int seed = FindSeedWithKind(TetrominoKind.T);

            TetrisWorld upright = CreateWorld(seed);
            upright.HardDrop();
            int[] uprightCells = CollectCells(upright);
            AssertSameSequence(ExpectedLandingCells((int)TetrominoKind.T, 0), uprightCells);

            TetrisWorld turned = CreateWorld(seed);
            Assert.True(turned.TryRotate());
            Assert.Equal(3, turned.CurrentX);
            turned.HardDrop();
            int[] turnedCells = CollectCells(turned);
            AssertSameSequence(ExpectedLandingCells((int)TetrominoKind.T, 1), turnedCells);

            // 顺时针与逆时针的落点不同：这一条把"转反了"钉死。
            Assert.False(AreEqual(turnedCells, ExpectedLandingCells((int)TetrominoKind.T, 3)));
        }

        [Fact]
        public void TryRotate_OShape_KeepsFootprintIdentical()
        {
            int seed = FindSeedWithKind(TetrominoKind.O);

            TetrisWorld plain = CreateWorld(seed);
            plain.HardDrop();

            TetrisWorld rotated = CreateWorld(seed);
            Assert.True(rotated.TryRotate());
            rotated.HardDrop();

            AssertSameSequence(CollectCells(plain), CollectCells(rotated));
        }

        [Fact]
        public void TryRotate_AgainstRightWall_KicksOneColumnLeft()
        {
            int seed = FindSeedWithKind(TetrominoKind.T);
            TetrisWorld world = CreateWorld(seed);

            while (world.TryMoveRight())
            {
            }

            Assert.Equal(7, world.CurrentX);
            Assert.Equal(0, world.CurrentY);

            Assert.True(world.TryRotate());

            // 原位转不下（包围盒右侧越界），左踢一格成功：列坐标恰好 -1。
            Assert.Equal(6, world.CurrentX);

            world.HardDrop();
            AssertSameSequence(ExpectedLandingCells((int)TetrominoKind.T, 1, 6), CollectCells(world));
        }

        [Fact]
        public void TryRotate_OnFloor_IsRejectedAndKeepsBoard()
        {
            int seed = FindSeedWithKind(TetrominoKind.I);
            TetrisWorld world = CreateWorld(seed);
            int landing = MaxOriginY((int)world.CurrentKind, 0);

            // 一字长条横躺在最底行时，转竖直需要 4 行空间，必然被地板挡住；左右踢墙也救不回来。
            // 注意只降到"落点行"而不触发锁定，否则当前方块已经换成下一块了。
            while (world.CurrentY < landing && world.TrySoftDrop())
            {
            }

            Assert.Equal(landing, world.CurrentY);
            Assert.Equal(0, world.PieceCount);
            int[] before = Snapshot(world);
            int x = world.CurrentX;
            int y = world.CurrentY;

            Assert.False(world.TryRotate());
            Assert.Equal(x, world.CurrentX);
            Assert.Equal(y, world.CurrentY);
            Assert.Equal(0, world.PieceCount);
            AssertSameSequence(before, Snapshot(world));
        }

        // ── 下落与硬降 ──────────────────────────────────────────────────

        [Fact]
        public void TrySoftDrop_DescendsOneRowPerCall()
        {
            TetrisWorld world = CreateWorld();

            Assert.True(world.TrySoftDrop());
            Assert.Equal(1, world.CurrentY);
            Assert.True(world.TrySoftDrop());
            Assert.Equal(2, world.CurrentY);

            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.PieceCount);
            Assert.Equal(0, world.ProcessedSteps);
        }

        [Fact]
        public void TrySoftDrop_AtFloor_LocksPiece()
        {
            TetrisWorld world = CreateWorld();
            int kind = (int)world.CurrentKind;
            int expectedDrops = MaxOriginY(kind, 0);

            int drops = 0;
            while (world.TrySoftDrop())
            {
                drops++;
                Assert.True(drops <= BoardHeight, "软降没有触底");
            }

            Assert.Equal(expectedDrops, drops);
            Assert.Equal(1, world.PieceCount);
            Assert.Equal(4, world.FilledCellCount);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.CurrentY);
            Assert.Equal(3, world.CurrentX);
            Assert.False(world.IsGameOver);
        }

        [Fact]
        public void HardDrop_LandsOnBottom_AndScoresTwoPerRowWithoutSteps()
        {
            TetrisWorld world = CreateWorld();
            int kind = (int)world.CurrentKind;
            int distance = MaxOriginY(kind, 0);

            world.HardDrop();

            Assert.Equal(distance * 2, world.Score);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(1, world.PieceCount);
            Assert.Equal(4, world.FilledCellCount);

            int[] cells = CollectCells(world);
            int lowest = 0;
            for (int i = 1; i < cells.Length; i += 2)
            {
                if (cells[i] > lowest)
                {
                    lowest = cells[i];
                }
            }

            Assert.Equal(BoardHeight - 1, lowest);
            AssertSameSequence(ExpectedLandingCells(kind, 0), cells);
        }

        [Fact]
        public void HardDrop_SpawnsNextPieceAtSpawnPoint()
        {
            TetrisWorld world = CreateWorld();
            TetrominoKind first = world.CurrentKind;

            world.HardDrop();

            Assert.Equal(3, world.CurrentX);
            Assert.Equal(0, world.CurrentY);
            Assert.NotEqual(first, world.CurrentKind);
            Assert.Equal(1, world.PieceCount);
        }

        // ── 消行、等级与下落间隔 ────────────────────────────────────────

        [Fact]
        public void Clear_OneLine_Scores100AtLevelOne()
        {
            ClearScenario scenario = RunClearScenario(TetrominoKind.I, 0);
            TetrisWorld world = scenario.World;

            Assert.Equal(1, scenario.Lines);
            Assert.Equal(1, world.ClearedLines);
            Assert.Equal(100, world.Score);
            Assert.Equal(1, world.Level);
            Assert.Equal(800, world.DropIntervalMs);
            Assert.Equal(1, world.PieceCount);
            Assert.Equal(0, CountRow(world, BoardHeight - 1));

            // 一字长条四格全在底行：消完之后整盘必须回到全空。
            Assert.Equal(0, world.FilledCellCount);
        }

        [Fact]
        public void Clear_TwoLines_Scores300()
        {
            ClearScenario scenario = RunClearScenario(TetrominoKind.T, 0);
            TetrisWorld world = scenario.World;

            Assert.Equal(2, scenario.Lines);
            Assert.Equal(2, world.ClearedLines);
            Assert.Equal(300, world.Score);
            Assert.Equal(1, world.Level);
            Assert.Equal(800, world.DropIntervalMs);
            Assert.Equal(0, world.FilledCellCount);
        }

        [Fact]
        public void Clear_ThreeLines_Scores500()
        {
            // J 转 90° 后是"竖三格 + 顶角一格"的 L 形，落点恰好跨 3 行；T 的凸起格下不到最深处，只能算 2 行。
            ClearScenario scenario = RunClearScenario(TetrominoKind.J, 1);
            TetrisWorld world = scenario.World;

            Assert.Equal(3, scenario.Lines);
            Assert.Equal(3, world.ClearedLines);
            Assert.Equal(500, world.Score);
            Assert.Equal(1, world.Level);
            Assert.Equal(0, world.FilledCellCount);
        }

        [Fact]
        public void Clear_FourLines_Scores800()
        {
            ClearScenario scenario = RunClearScenario(TetrominoKind.I, 1);
            TetrisWorld world = scenario.World;

            Assert.Equal(4, scenario.Lines);
            Assert.Equal(4, world.ClearedLines);
            Assert.Equal(800, world.Score);
            Assert.Equal(1, world.Level);
            Assert.Equal(800, world.DropIntervalMs);

            // 四条满行一次消完，棋盘必须回到全空。
            Assert.Equal(0, world.FilledCellCount);
        }

        [Fact]
        public void Level_ReachesTwoAfterTenLines_AndIntervalShrinks()
        {
            TetrisWorld world = CreateWorld(Seed);
            TetrisWorld shadow = CreateWorld(Seed);

            for (int i = 0; i < 10; i++)
            {
                RunSingleLineClear(world, shadow);
            }

            Assert.Equal(10, world.ClearedLines);
            Assert.Equal(2, world.Level);
            Assert.Equal(730, world.DropIntervalMs);
            Assert.Equal(1000, world.Score);

            // 跨级后再消一行：按"消行前等级"2 计 200 分；11 行未满 20 行，等级仍是 2。
            RunSingleLineClear(world, shadow);
            Assert.Equal(11, world.ClearedLines);
            Assert.Equal(2, world.Level);
            Assert.Equal(730, world.DropIntervalMs);
            Assert.Equal(1200, world.Score);
        }

        // ── Game Over 与结算 ────────────────────────────────────────────

        [Fact]
        public void GameOver_WhenSpawnAreaFillsUp()
        {
            TetrisWorld world = CreateWorld();

            int guard = 0;
            while (!world.IsGameOver && guard < LoopGuard)
            {
                world.HardDrop();
                guard++;
            }

            Assert.True(world.IsGameOver, "出生区堆满后必须判负");
            Assert.True(world.IsFinished);
            Assert.Equal(0, world.ClearedLines);
            Assert.True(world.PieceCount > 1);

            // 判负后所有输入都必须是空操作（不改变棋盘）。
            int[] before = Snapshot(world);
            Assert.False(world.TryMoveLeft());
            Assert.False(world.TryMoveRight());
            Assert.False(world.TryRotate());
            Assert.False(world.TrySoftDrop());
            world.HardDrop();
            world.Tick(10.0);
            world.Step(10.0f);
            AssertSameSequence(before, Snapshot(world));
        }

        [Fact]
        public void CollectResult_BeforeFinished_Throws()
        {
            TetrisWorld world = CreateWorld();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_WithoutClears_IsTurnLossWithoutLoot()
        {
            TetrisWorld world = RunToGameOver(CreateWorld());

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Tetris, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
            Assert.Empty(result.TriggeredTags);
            Assert.Equal(0, result.StepCount);
            Assert.InRange(result.RemainingUnits, 0, BoardHeight);
        }

        [Fact]
        public void CollectResult_AfterClear_IsVictoryWithLoot()
        {
            ClearScenario scenario = RunClearScenario(TetrominoKind.I, 0);
            TetrisWorld world = RunToGameOver(scenario.World);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Tetris, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Single(result.Loots);
            Assert.Equal(1003, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            TetrisWorld world = RunToGameOver(CreateWorld());

            world.CollectResult();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
            Assert.True(world.IsFinished);
        }

        [Fact]
        public void RemainingUnits_EqualsRowsAboveTheStack()
        {
            TetrisWorld world = RunToGameOver(CreateWorld());

            int firstFilledRow = TopFilledY(world);
            GameplayResult result = world.CollectResult();

            Assert.Equal(firstFilledRow, result.RemainingUnits);
            Assert.InRange(result.RemainingUnits, 0, BoardHeight - 1);
            Assert.True(result.RemainingUnits < BoardHeight / 2, "堆满出生区后剩余高度应明显小于半盘");
        }

        // ── Step / Tick ────────────────────────────────────────────────

        [Fact]
        public void Step_IncrementsProcessedSteps_AndDrivesGravity()
        {
            TetrisWorld world = CreateWorld();

            world.Step(0.1f);
            Assert.Equal(1, world.ProcessedSteps);
            Assert.Equal(0, world.CurrentY);

            for (int i = 0; i < 7; i++)
            {
                world.Step(0.1f);
            }

            Assert.Equal(8, world.ProcessedSteps);
            Assert.Equal(1, world.CurrentY);
        }

        [Fact]
        public void Tick_BeforeInterval_DoesNotMove_ThenDropsOneRow()
        {
            TetrisWorld world = CreateWorld();

            world.Tick(0.5);
            Assert.Equal(0, world.CurrentY);
            Assert.Equal(0, world.ProcessedSteps);

            world.Tick(0.4);
            Assert.Equal(1, world.CurrentY);
            Assert.Equal(0, world.ProcessedSteps);
        }

        [Fact]
        public void Tick_IgnoresNonPositiveAndNaNDelta()
        {
            TetrisWorld world = CreateWorld();

            world.Tick(0.0);
            world.Tick(-1.0);
            world.Tick(double.NaN);

            Assert.Equal(0, world.CurrentY);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(800, world.DropIntervalMs);
        }

        [Fact]
        public void Tick_AutomaticallyDropsAndLocksPiece()
        {
            TetrisWorld world = CreateWorld();

            int guard = 0;
            while (world.PieceCount == 0 && guard < LoopGuard)
            {
                world.Tick(0.8);
                guard++;
            }

            Assert.Equal(1, world.PieceCount);
            Assert.Equal(4, world.FilledCellCount);
            Assert.Equal(0, world.ProcessedSteps);
        }

        // ── 零分配基线 ──────────────────────────────────────────────────

        [Fact]
        public void TryMoveLeftAndTick_AfterWarmup_DoNotAllocate()
        {
            TetrisWorld world = CreateWorld(Seed);

            for (int i = 0; i < 200; i++)
            {
                world.TryMoveLeft();
                world.TryMoveRight();
                world.Tick(1.0 / 60.0);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.TryMoveLeft();
                world.Tick(1.0 / 60.0);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void TryMoveAndRotate_AfterWarmup_DoNotAllocate()
        {
            TetrisWorld world = CreateWorld(Seed);

            // 这一条只走"纯位移"路径：方块不会落地，也就不会因为判负而退化成空操作。
            for (int i = 0; i < 200; i++)
            {
                SpinPieces(world);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                SpinPieces(world);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        private static void SpinPieces(TetrisWorld world)
        {
            world.TryMoveLeft();
            world.TryMoveRight();
            world.TryRotate();
            world.TryRotate();
            world.TryRotate();
            world.TryRotate();
        }

        // ── 夹具与小工具 ────────────────────────────────────────────────

        private static TetrisWorld CreateWorld()
        {
            return CreateWorld(Seed);
        }

        private static TetrisWorld CreateWorld(int seed)
        {
            return new TetrisWorld(new GameplayInitData(1, seed, 0));
        }

        private static TetrisWorld RunToGameOver(TetrisWorld world)
        {
            int guard = 0;
            while (!world.IsGameOver && guard < LoopGuard)
            {
                world.HardDrop();
                guard++;
            }

            Assert.True(world.IsGameOver, "硬降循环未能触发判负");
            return world;
        }

        /// <summary>
        /// "一次消 N 行"场景：同种子探针盘先空盘硬降，量出这个方块（含指定旋转）的真实落点，
        /// 再把真实盘布成"落点以外的格子全部填满"——于是方块落地那一瞬恰好补齐所有行。
        /// </summary>
        private static ClearScenario RunClearScenario(TetrominoKind kind, int rotations)
        {
            int seed = FindSeedWithKind(kind);

            TetrisWorld probe = CreateWorld(seed);
            for (int i = 0; i < rotations; i++)
            {
                Assert.True(probe.TryRotate());
            }

            probe.HardDrop();
            int[] landing = CollectCells(probe);
            int firstRow = MinFilledY(probe);
            int lines = BoardHeight - firstRow;

            TetrisWorld world = CreateWorld(seed);
            for (int i = 0; i < rotations; i++)
            {
                Assert.True(world.TryRotate());
            }

            InjectFixture(world, landing, firstRow);

            int drops = 0;
            while (world.TrySoftDrop())
            {
                drops++;
                Assert.True(drops <= BoardHeight, "软降循环未能落地");
            }

            return new ClearScenario(world, lines);
        }

        /// <summary>用"阴影盘 + 白盒单行夹具"反复做单行消除，供等级/间隔用例累积消行数。</summary>
        private static void RunSingleLineClear(TetrisWorld world, TetrisWorld shadow)
        {
            Assert.Equal(world.CurrentKind, shadow.CurrentKind);

            shadow.HardDrop();
            int[] landing = CollectCells(shadow);
            InjectEmpty(shadow);

            InjectFixture(world, landing, BoardHeight - 1);

            int drops = 0;
            while (world.TrySoftDrop())
            {
                drops++;
                Assert.True(drops <= BoardHeight, "软降循环未能落地");
            }
        }

        /// <summary>
        /// 白盒布场：清空棋盘后把 <paramref name="firstRow"/>..19 行填满，但挖掉方块从出生点一路下落到落点
        /// 所经过的整条竖井（按落点列整列清空）。
        /// <para>
        /// <b>为什么挖整列而不是只挖落点格</b>：非凸方块（例如 T 的凸起格）下落到最深处时，凸起格必须先经过
        /// 上方行；只挖落点格会让方块提前卡在浅一层的位置，"消 3 行"就变成"消 2 行"。按列挖出通道既保证
        /// 下落路径通畅，也保证深层落点是可达的最深位置。
        /// </para>
        /// <para>写完立刻按 <see cref="ITetrisReadView.CellAt"/> 回读校验，字段名或索引公式一变就失败。</para>
        /// </summary>
        private static void InjectFixture(TetrisWorld world, int[] cells, int firstRow)
        {
            bool[] isSourceColumn = new bool[BoardWidth];
            int[] deepestRow = new int[BoardWidth];

            for (int i = 0; i < cells.Length; i += 2)
            {
                int x = cells[i];
                int y = cells[i + 1];
                isSourceColumn[x] = true;
                if (y > deepestRow[x])
                {
                    deepestRow[x] = y;
                }
            }

            int[] board = GetBoardCells(world);
            Array.Clear(board, 0, board.Length);

            int expectedFilled = 0;
            for (int y = firstRow; y < BoardHeight; y++)
            {
                for (int x = 0; x < BoardWidth; x++)
                {
                    if (isSourceColumn[x] && y <= deepestRow[x])
                    {
                        continue;
                    }

                    board[(y * BoardWidth) + x] = 1;
                    expectedFilled++;
                }
            }

            Assert.Equal(expectedFilled, world.FilledCellCount);
            Assert.True(expectedFilled > 0, "夹具没有填任何格子");
            Assert.Equal(0, world.CellAt(cells[0], cells[1]));
        }

        private static void InjectEmpty(TetrisWorld world)
        {
            int[] board = GetBoardCells(world);
            Array.Clear(board, 0, board.Length);
            Assert.Equal(0, world.FilledCellCount);
        }

        private static int[] GetBoardCells(TetrisWorld world)
        {
            FieldInfo? boardField = typeof(TetrisWorld).GetField(
                "_board",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (boardField == null)
            {
                throw new InvalidOperationException("TetrisWorld 私有字段 _board 不存在：白盒布场夹具已失效。");
            }

            object? board = boardField.GetValue(world);
            if (board == null)
            {
                throw new InvalidOperationException("TetrisWorld._board 为 null。");
            }

            FieldInfo? cellsField = board.GetType().GetField(
                "_cells",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (cellsField == null)
            {
                throw new InvalidOperationException("TetrisBoard 私有字段 _cells 不存在：白盒布场夹具已失效。");
            }

            int[]? cells = cellsField.GetValue(board) as int[];
            if (cells == null)
            {
                throw new InvalidOperationException("TetrisBoard._cells 不是 int[]。");
            }

            return cells;
        }

        private static int[] CollectCells(TetrisWorld world)
        {
            int[] buffer = new int[BoardWidth * BoardHeight * 2];
            int count = 0;
            for (int y = 0; y < BoardHeight; y++)
            {
                for (int x = 0; x < BoardWidth; x++)
                {
                    if (world.CellAt(x, y) == 0)
                    {
                        continue;
                    }

                    buffer[count] = x;
                    buffer[count + 1] = y;
                    count += 2;
                }
            }

            int[] result = new int[count];
            Array.Copy(buffer, result, count);
            return result;
        }

        private static int[] Snapshot(TetrisWorld world)
        {
            int[] cells = new int[BoardWidth * BoardHeight];
            for (int y = 0; y < BoardHeight; y++)
            {
                for (int x = 0; x < BoardWidth; x++)
                {
                    cells[(y * BoardWidth) + x] = world.CellAt(x, y);
                }
            }

            return cells;
        }

        private static int[] CollectKindSequence(TetrisWorld world, int count)
        {
            int[] kinds = new int[count];
            for (int i = 0; i < count; i++)
            {
                kinds[i] = (int)world.CurrentKind;

                // 左右交替铺开落点，避免 14 块全部堆在同一列把出生区堵死。
                if ((i & 1) == 0)
                {
                    world.TryMoveLeft();
                    world.TryMoveLeft();
                }
                else
                {
                    world.TryMoveRight();
                    world.TryMoveRight();
                }

                world.HardDrop();
            }

            return kinds;
        }

        private static int MinFilledY(TetrisWorld world)
        {
            for (int y = 0; y < BoardHeight; y++)
            {
                for (int x = 0; x < BoardWidth; x++)
                {
                    if (world.CellAt(x, y) != 0)
                    {
                        return y;
                    }
                }
            }

            return BoardHeight;
        }

        private static int TopFilledY(TetrisWorld world)
        {
            return MinFilledY(world);
        }

        private static int CountRow(TetrisWorld world, int y)
        {
            int count = 0;
            for (int x = 0; x < BoardWidth; x++)
            {
                if (world.CellAt(x, y) != 0)
                {
                    count++;
                }
            }

            return count;
        }

        private static int FindSeedWithKind(TetrominoKind kind)
        {
            for (int seed = 1; seed <= 2000; seed++)
            {
                if (CreateWorld(seed).CurrentKind == kind)
                {
                    return seed;
                }
            }

            throw new InvalidOperationException("在 1..2000 的种子里找不到首块为 " + kind + " 的序列。");
        }

        private static int[] ExpectedLandingCells(int kind, int rotation)
        {
            return ExpectedLandingCells(kind, rotation, 3);
        }

        private static int[] ExpectedLandingCells(int kind, int rotation, int originX)
        {
            int originY = MaxOriginY(kind, rotation);
            int[] cells = new int[8];
            int count = 0;

            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (!HasCell(kind, rotation, x, y))
                    {
                        continue;
                    }

                    cells[count] = originX + x;
                    cells[count + 1] = originY + y;
                    count += 2;
                }
            }

            Assert.Equal(8, count);
            return cells;
        }

        private static int MinOriginX(int kind, int rotation)
        {
            int min = 4;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (HasCell(kind, rotation, x, y) && x < min)
                    {
                        min = x;
                    }
                }
            }

            return -min;
        }

        private static int MaxOriginX(int kind, int rotation)
        {
            int max = 0;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (HasCell(kind, rotation, x, y) && x > max)
                    {
                        max = x;
                    }
                }
            }

            return BoardWidth - 1 - max;
        }

        private static int MaxOriginY(int kind, int rotation)
        {
            int max = 0;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if (HasCell(kind, rotation, x, y) && y > max)
                    {
                        max = y;
                    }
                }
            }

            return BoardHeight - 1 - max;
        }

        private static bool HasCell(int kind, int rotation, int x, int y)
        {
            uint mask = BaseMasks[kind];
            for (int i = 0; i < (rotation & 3); i++)
            {
                mask = RotateClockwise(mask);
            }

            return (mask & (1u << ((y << 2) | x))) != 0u;
        }

        private static uint RotateClockwise(uint mask)
        {
            uint rotated = 0u;
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    if ((mask & (1u << ((y << 2) | x))) == 0u)
                    {
                        continue;
                    }

                    rotated |= 1u << ((x << 2) | (3 - y));
                }
            }

            return rotated;
        }

        /// <summary>逐项比较：种类序列、整盘快照与坐标对都按生成顺序（行优先）对齐。</summary>
        private static void AssertSameSequence(int[] expected, int[] actual)
        {
            Assert.True(AreEqual(expected, actual), "序列不一致，实际：" + Describe(actual));
        }

        private static bool AreEqual(int[] first, int[] second)
        {
            if (first.Length != second.Length)
            {
                return false;
            }

            for (int i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string Describe(int[] pairs)
        {
            string text = string.Empty;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                text += "(" + pairs[i] + "," + pairs[i + 1] + ")";
            }

            return text;
        }

        private readonly struct ClearScenario
        {
            internal ClearScenario(TetrisWorld world, int lines)
            {
                World = world;
                Lines = lines;
            }

            internal TetrisWorld World { get; }

            internal int Lines { get; }
        }
    }
}
