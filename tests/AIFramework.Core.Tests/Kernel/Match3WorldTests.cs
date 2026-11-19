using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Match3;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="Match3World"/> 的三消棋盘契约：初始棋盘无三连、交换门禁（越界/非相邻/无三连不消耗步数）、
    /// 一次 <c>Step</c> 只结算一级、每格 10 分、列内重力与顶部补格、步数耗尽/分数达标两种结束口径、结算单次性。
    /// <para>
    /// 用例全部走公开 API（不反射内部棋盘）：涉及"是否能连出三连/哪些格会被清"的判断，都在快照上做等价的
    /// 网格演算再与内核行为对账。对局类用例按种子搜索（全链路确定性，无时间与系统随机参与）。
    /// </para>
    /// </summary>
    public sealed class Match3WorldTests
    {
        private const int Width = 8;

        private const int Height = 8;

        private const int InitialMoves = 20;

        private const int TargetScore = 1000;

        private const int ScorePerGem = 10;

        // ── 初始状态 ────────────────────────────────────────────────────

        [Fact]
        public void Constructor_InitialState_MatchesLevelConfiguration()
        {
            Match3World world = CreateWorld(9);

            Assert.Equal(Width, world.Width);
            Assert.Equal(Height, world.Height);
            Assert.Equal(InitialMoves, world.MovesLeft);
            Assert.Equal(TargetScore, world.TargetScore);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.ClearedCount);
            Assert.Equal(0, world.CascadeDepth);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void Constructor_InitialBoard_IsFullAndOnlyContainsValidColors()
        {
            int[] cells = Snapshot(CreateWorld(1));

            Assert.Equal(Width * Height, cells.Length);
            foreach (int gem in cells)
            {
                Assert.InRange(gem, 1, 5);
            }
        }

        [Fact]
        public void Constructor_InitialBoard_HasNoThreeInARow()
        {
            // 规则 1：初始棋盘不得已经存在三连（生成阶段会重洗，超限才抛异常）。
            for (int seed = 1; seed <= 8; seed++)
            {
                Assert.False(HasThreeInARow(Snapshot(CreateWorld(seed))), "seed=" + seed);
            }
        }

        [Fact]
        public void Constructor_SameSeed_ProducesIdenticalBoardsAndRandomState()
        {
            Match3World first = CreateWorld(2024);
            Match3World second = CreateWorld(2024);

            Assert.Equal(first.RandomState, second.RandomState);
            Assert.True(AreEqual(Snapshot(first), Snapshot(second)));
        }

        [Fact]
        public void Constructor_DifferentSeeds_ProduceDifferentBoards()
        {
            Match3World first = CreateWorld(1234);
            Match3World second = CreateWorld(9876);

            Assert.NotEqual(first.RandomState, second.RandomState);
            Assert.False(AreEqual(Snapshot(first), Snapshot(second)));
        }

        // ── 交换门禁 ────────────────────────────────────────────────────

        [Fact]
        public void TrySwap_OutOfBounds_ReturnsFalseAndChangesNothing()
        {
            Match3World world = CreateWorld(1);
            int[] board = Snapshot(world);

            Assert.False(world.TrySwap(-1, 0, 0, 0));
            Assert.False(world.TrySwap(0, -1, 0, 0));
            Assert.False(world.TrySwap(0, 0, Width, 0));
            Assert.False(world.TrySwap(0, Height - 1, 0, Height));
            Assert.False(world.TrySwap(Width, Height, Width - 1, Height - 1));

            Assert.Equal(InitialMoves, world.MovesLeft);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.True(AreEqual(board, Snapshot(world)));
        }

        [Fact]
        public void TrySwap_NonAdjacent_ReturnsFalseAndChangesNothing()
        {
            Match3World world = CreateWorld(1);
            int[] board = Snapshot(world);

            Assert.False(world.TrySwap(0, 0, 2, 0));
            Assert.False(world.TrySwap(0, 0, 0, 2));
            Assert.False(world.TrySwap(0, 0, 1, 1));
            Assert.False(world.TrySwap(3, 3, 3, 3));

            Assert.Equal(InitialMoves, world.MovesLeft);
            Assert.True(AreEqual(board, Snapshot(world)));
        }

        [Fact]
        public void TrySwap_WithoutMatch_SwapsBackAndKeepsMove()
        {
            Match3World world = CreateWorld(1);
            int[] board = Snapshot(world);

            Assert.True(TryFindQuietSwap(board, out int x1, out int y1, out int x2, out int y2));
            Assert.False(world.TrySwap(x1, y1, x2, y2));

            Assert.Equal(InitialMoves, world.MovesLeft);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.ClearedCount);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.True(AreEqual(board, Snapshot(world)));
        }

        [Fact]
        public void TrySwap_WithMatch_ConsumesOneMoveButClearsNothingUntilStep()
        {
            Match3World world = CreateWorldWithMatchingSwap(out int x1, out int y1, out int x2, out int y2);
            int[] before = Snapshot(world);

            Assert.True(world.TrySwap(x1, y1, x2, y2));

            Assert.Equal(InitialMoves - 1, world.MovesLeft);
            Assert.Equal(0, world.Score);
            Assert.Equal(0, world.ClearedCount);
            Assert.Equal(0, world.ProcessedSteps);

            // 交换已经落到棋盘上，但还没有任何格被清掉（棋盘仍是满的）。
            int[] afterSwap = Snapshot(world);
            Assert.Equal(0, CountEmpty(afterSwap));
            Assert.Equal(before[(y1 * Width) + x1], afterSwap[(y2 * Width) + x2]);
            Assert.Equal(before[(y2 * Width) + x2], afterSwap[(y1 * Width) + x1]);
            Assert.True(HasThreeInARow(afterSwap));
        }

        [Fact]
        public void TrySwap_WhileCascadePending_ReturnsFalse()
        {
            Match3World world = CreateWorldWithMatchingSwap(out int x1, out int y1, out int x2, out int y2);
            Assert.True(world.TrySwap(x1, y1, x2, y2));
            int[] pending = Snapshot(world);

            Assert.False(world.TrySwap(0, 0, 1, 0));
            Assert.False(world.TrySwap(x2, y2, x2, y2));

            Assert.Equal(InitialMoves - 1, world.MovesLeft);
            Assert.True(AreEqual(pending, Snapshot(world)));
        }

        // ── 单级消解 ────────────────────────────────────────────────────

        [Fact]
        public void Step_ClearsExactlyOneLevelAndScoresTenPerGem()
        {
            Match3World world = CreateWorldWithMatchingSwap(out int x1, out int y1, out int x2, out int y2);
            Assert.True(world.TrySwap(x1, y1, x2, y2));
            int expectedCleared = CountMarked(ComputeMatched(Snapshot(world)));

            world.Step(1f);

            Assert.True(expectedCleared >= 3);
            Assert.Equal(expectedCleared, world.ClearedCount);
            Assert.Equal(ScorePerGem * expectedCleared, world.Score);
            Assert.Equal(1, world.CascadeDepth);
            Assert.Equal(1, world.ProcessedSteps);

            // 清完立刻下落补格：棋盘重新填满，不残留空位。
            Assert.Equal(0, CountEmpty(Snapshot(world)));
        }

        [Fact]
        public void Step_AppliesGravityKeepSurvivorOrderAndRefillsFromTop()
        {
            Match3World world = CreateWorldWithMatchingSwap(out int x1, out int y1, out int x2, out int y2);
            Assert.True(world.TrySwap(x1, y1, x2, y2));

            int[] swapped = Snapshot(world);
            bool[] cleared = ComputeMatched(swapped);
            world.Step(1f);
            int[] after = Snapshot(world);

            for (int x = 0; x < Width; x++)
            {
                int write = 0;
                for (int y = 0; y < Height; y++)
                {
                    if (cleared[(y * Width) + x])
                    {
                        continue;
                    }

                    // 幸存宝石整体下移、相对次序不变：列内第 write 格必须等于原来的第 y 格。
                    Assert.Equal(swapped[(y * Width) + x], after[(write * Width) + x]);
                    write++;
                }

                // 空位全部集中在顶部，由新宝石填满。
                for (int y = write; y < Height; y++)
                {
                    Assert.InRange(after[(y * Width) + x], 1, 5);
                }
            }
        }

        [Fact]
        public void Step_EachCallScoresTenPerGemAndAdvancesExactlyOneLevel()
        {
            for (int seed = 1; seed <= 12; seed++)
            {
                Match3World world = CreateWorld(seed);

                for (int move = 0; move < 3 && !world.IsFinished; move++)
                {
                    Assert.True(TryFindSwap(Snapshot(world), out int x1, out int y1, out int x2, out int y2));
                    Assert.True(world.TrySwap(x1, y1, x2, y2));

                    int guard = 0;
                    while (!world.IsFinished && HasThreeInARow(Snapshot(world)) && guard++ < 64)
                    {
                        int scoreBefore = world.Score;
                        int clearedBefore = world.ClearedCount;
                        int depthBefore = world.CascadeDepth;

                        world.Step(1f);

                        int clearedDelta = world.ClearedCount - clearedBefore;
                        Assert.True(clearedDelta >= 3);
                        Assert.Equal(ScorePerGem * clearedDelta, world.Score - scoreBefore);
                        Assert.Equal(depthBefore + 1, world.CascadeDepth);
                    }

                    // 消解结束后棋盘必须没有三连，否则下一个 Step 还会继续消。
                    Assert.False(HasThreeInARow(Snapshot(world)) && !world.IsFinished);
                }
            }
        }

        [Fact]
        public void CascadeDepth_ReachesTwoOnChainedResolution()
        {
            bool found = false;
            for (int seed = 1; seed <= 16 && !found; seed++)
            {
                Match3World world = CreateWorld(seed);
                Assert.True(TryFindSwap(Snapshot(world), out int x1, out int y1, out int x2, out int y2));
                Assert.True(world.TrySwap(x1, y1, x2, y2));

                while (!world.IsFinished && HasThreeInARow(Snapshot(world)))
                {
                    world.Step(1f);
                }

                found = world.CascadeDepth >= 2;
            }

            Assert.True(found, "16 个种子内没有触发过二级以上连消。");
        }

        // ── 结束与结算 ──────────────────────────────────────────────────

        [Fact]
        public void MovesExhausted_FinishesWithoutVictory()
        {
            bool found = false;
            for (int seed = 1; seed <= 40 && !found; seed++)
            {
                Match3World world = CreateWorld(seed);
                PlayUntilFinished(world);

                if (!world.IsFinished || world.MovesLeft != 0 || world.Score >= world.TargetScore)
                {
                    continue;
                }

                found = true;

                GameplayResult result = world.CollectResult();

                Assert.Equal(GameplayKernelType.Match3, result.KernelType);
                Assert.False(result.IsVictory);
                Assert.Equal(0, result.RemainingUnits);
                Assert.Equal(world.ProcessedSteps, result.StepCount);
                Assert.Empty(result.Loots);
                Assert.Empty(result.TriggeredTags);
            }

            Assert.True(found, "40 个种子内没有出现“步数耗尽且未达标”的对局。");
        }

        [Fact]
        public void TargetScoreReached_FinishesImmediatelyWithVictoryAndLoot()
        {
            Match3World world = CreateWorld(1);
            PlayUntilFinished(world);

            Assert.True(world.IsFinished);
            Assert.True(world.Score >= TargetScore);
            Assert.True(world.MovesLeft > 0);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Match3, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(world.MovesLeft, result.RemainingUnits);
            Assert.Equal(world.ProcessedSteps, result.StepCount);
            Assert.Single(result.Loots);
            Assert.Equal(1002, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Empty(result.TriggeredTags);
        }

        [Fact]
        public void CollectResult_BeforeFinish_Throws()
        {
            Match3World world = CreateWorld(1);

            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            Match3World world = CreateWorld(1);
            PlayUntilFinished(world);

            world.CollectResult();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void GetGem_OutOfBounds_ReturnsZero()
        {
            Match3World world = CreateWorld(3);

            Assert.Equal(0, world.GetGem(-1, 0));
            Assert.Equal(0, world.GetGem(0, -1));
            Assert.Equal(0, world.GetGem(Width, 0));
            Assert.Equal(0, world.GetGem(0, Height));
            Assert.Equal(0, world.GetGem(int.MaxValue, int.MinValue));

            int[] cells = Snapshot(world);
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Assert.Equal(cells[(y * Width) + x], world.GetGem(x, y));
                }
            }
        }

        [Fact]
        public void StepAndSwap_AfterFinish_HaveNoSideEffect()
        {
            Match3World world = CreateWorld(1);
            PlayUntilFinished(world);

            int[] board = Snapshot(world);
            int score = world.Score;
            int moves = world.MovesLeft;
            int steps = world.ProcessedSteps;
            int cleared = world.ClearedCount;
            int depth = world.CascadeDepth;
            int randomState = world.RandomState;

            world.Step(1f);
            world.Step(0f);
            world.Step(1f);
            Assert.False(world.TrySwap(0, 0, 1, 0));

            Assert.Equal(score, world.Score);
            Assert.Equal(moves, world.MovesLeft);
            Assert.Equal(steps, world.ProcessedSteps);
            Assert.Equal(cleared, world.ClearedCount);
            Assert.Equal(depth, world.CascadeDepth);
            Assert.Equal(randomState, world.RandomState);
            Assert.True(AreEqual(board, Snapshot(world)));
            Assert.True(world.IsFinished);
        }

        [Fact]
        public void SameSeedWithSameSwaps_ProducesIdenticalOutcome()
        {
            Match3World first = CreateWorld(777);
            Match3World second = CreateWorld(777);

            for (int move = 0; move < 6; move++)
            {
                Assert.False(first.IsFinished);
                Assert.True(TryFindSwap(Snapshot(first), out int x1, out int y1, out int x2, out int y2));
                Assert.Equal(first.TrySwap(x1, y1, x2, y2), second.TrySwap(x1, y1, x2, y2));

                while (!first.IsFinished && HasThreeInARow(Snapshot(first)))
                {
                    first.Step(1f);
                    second.Step(1f);
                }
            }

            Assert.Equal(first.Score, second.Score);
            Assert.Equal(first.MovesLeft, second.MovesLeft);
            Assert.Equal(first.ProcessedSteps, second.ProcessedSteps);
            Assert.Equal(first.ClearedCount, second.ClearedCount);
            Assert.Equal(first.CascadeDepth, second.CascadeDepth);
            Assert.Equal(first.RandomState, second.RandomState);
            Assert.True(AreEqual(Snapshot(first), Snapshot(second)));
        }

        // ── 辅助 ────────────────────────────────────────────────────────

        private static Match3World CreateWorld(int seed)
        {
            return new Match3World(new GameplayInitData(1, seed, 0));
        }

        private static Match3World CreateWorldWithMatchingSwap(out int x1, out int y1, out int x2, out int y2)
        {
            for (int seed = 1; seed <= 32; seed++)
            {
                Match3World world = CreateWorld(seed);
                if (TryFindSwap(Snapshot(world), out x1, out y1, out x2, out y2))
                {
                    return world;
                }
            }

            throw new InvalidOperationException("32 个种子内没有可交换出三连的初始棋盘。");
        }

        private static int[] Snapshot(IMatch3ReadView view)
        {
            int[] cells = new int[view.Width * view.Height];
            for (int y = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++)
                {
                    cells[(y * Width) + x] = view.GetGem(x, y);
                }
            }

            return cells;
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

        private static int CountEmpty(int[] cells)
        {
            int empty = 0;
            foreach (int gem in cells)
            {
                if (gem == 0)
                {
                    empty++;
                }
            }

            return empty;
        }

        private static bool HasThreeInARow(int[] cells)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x + 2 < Width; x++)
                {
                    int color = cells[(y * Width) + x];
                    if (color != 0 && cells[(y * Width) + x + 1] == color && cells[(y * Width) + x + 2] == color)
                    {
                        return true;
                    }
                }
            }

            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y + 2 < Height; y++)
                {
                    int color = cells[(y * Width) + x];
                    if (color != 0 && cells[((y + 1) * Width) + x] == color && cells[((y + 2) * Width) + x] == color)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>独立复算"哪些格处在三连里"（与内核实现等价，用于对账）。</summary>
        private static bool[] ComputeMatched(int[] cells)
        {
            bool[] matched = new bool[cells.Length];

            for (int y = 0; y < Height; y++)
            {
                int runStart = 0;
                while (runStart < Width)
                {
                    int color = cells[(y * Width) + runStart];
                    int runEnd = runStart + 1;
                    while (color != 0 && runEnd < Width && cells[(y * Width) + runEnd] == color)
                    {
                        runEnd++;
                    }

                    if (color != 0 && runEnd - runStart >= 3)
                    {
                        for (int x = runStart; x < runEnd; x++)
                        {
                            matched[(y * Width) + x] = true;
                        }
                    }

                    runStart = runEnd;
                }
            }

            for (int x = 0; x < Width; x++)
            {
                int runStart = 0;
                while (runStart < Height)
                {
                    int color = cells[(runStart * Width) + x];
                    int runEnd = runStart + 1;
                    while (color != 0 && runEnd < Height && cells[(runEnd * Width) + x] == color)
                    {
                        runEnd++;
                    }

                    if (color != 0 && runEnd - runStart >= 3)
                    {
                        for (int y = runStart; y < runEnd; y++)
                        {
                            matched[(y * Width) + x] = true;
                        }
                    }

                    runStart = runEnd;
                }
            }

            return matched;
        }

        private static int CountMarked(bool[] marks)
        {
            int count = 0;
            foreach (bool mark in marks)
            {
                if (mark)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>找一对"交换后能形成三连"的相邻格（在快照上模拟，不触碰真实棋盘）。</summary>
        private static bool TryFindSwap(int[] cells, out int x1, out int y1, out int x2, out int y2)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (x + 1 < Width && SwapCreatesMatch(cells, x, y, x + 1, y))
                    {
                        x1 = x;
                        y1 = y;
                        x2 = x + 1;
                        y2 = y;
                        return true;
                    }

                    if (y + 1 < Height && SwapCreatesMatch(cells, x, y, x, y + 1))
                    {
                        x1 = x;
                        y1 = y;
                        x2 = x;
                        y2 = y + 1;
                        return true;
                    }
                }
            }

            return Fail(out x1, out y1, out x2, out y2);
        }

        /// <summary>找一对"交换后仍无三连"的相邻格（应被内核拒绝且不消耗步数）。</summary>
        private static bool TryFindQuietSwap(int[] cells, out int x1, out int y1, out int x2, out int y2)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (x + 1 < Width && !SwapCreatesMatch(cells, x, y, x + 1, y))
                    {
                        x1 = x;
                        y1 = y;
                        x2 = x + 1;
                        y2 = y;
                        return true;
                    }

                    if (y + 1 < Height && !SwapCreatesMatch(cells, x, y, x, y + 1))
                    {
                        x1 = x;
                        y1 = y;
                        x2 = x;
                        y2 = y + 1;
                        return true;
                    }
                }
            }

            return Fail(out x1, out y1, out x2, out y2);
        }

        private static bool Fail(out int x1, out int y1, out int x2, out int y2)
        {
            x1 = -1;
            y1 = -1;
            x2 = -1;
            y2 = -1;
            return false;
        }

        private static bool SwapCreatesMatch(int[] cells, int x1, int y1, int x2, int y2)
        {
            int[] copy = (int[])cells.Clone();
            int first = (y1 * Width) + x1;
            int second = (y2 * Width) + x2;
            int value = copy[first];
            copy[first] = copy[second];
            copy[second] = value;
            return HasThreeInARow(copy);
        }

        /// <summary>推演到结束：有可消交换就交换并逐级消解，否则空推（覆盖"无可消交换"的棋盘）。</summary>
        private static void PlayUntilFinished(Match3World world)
        {
            int guard = 0;
            while (!world.IsFinished && guard++ < 2000)
            {
                if (!TryFindSwap(Snapshot(world), out int x1, out int y1, out int x2, out int y2))
                {
                    world.Step(1f);
                    continue;
                }

                Assert.True(world.TrySwap(x1, y1, x2, y2));

                while (!world.IsFinished && HasThreeInARow(Snapshot(world)))
                {
                    world.Step(1f);

                    // 规则 5：分数达标当步立即结束，不等剩余级联与步数。
                    if (world.Score >= world.TargetScore)
                    {
                        Assert.True(world.IsFinished, "分数达标后必须立即结束对局。");
                    }
                }
            }

            Assert.True(world.IsFinished, "2000 个保护步内未结束对局。");
        }
    }
}
