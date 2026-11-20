using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.BoardGame;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="BoardGameWorld"/>（黑白棋 + 极小极大 AI）的契约用例。
    /// <para>
    /// <b>覆盖口径</b>：标准初始 4 子与黑白先手、黑方首步 4 个合法点、越界/占位/夹不住三类非法落子不换手、
    /// 翻转数与计数一致性、白方回合拒绝本地落子、无子可落自动跳过与 <c>PassCount</c>、
    /// 双方无子可落结束与胜负/平局、Easy/Normal/Hard 三档 AI 均落合法点且不破坏规则、
    /// Hard 在"有角可占"的局面优先占角、<c>Restart</c> 复位、<c>CollectResult</c> 门禁/胜负/战利品、
    /// <c>GetCell</c> 越界口径、<c>Step</c> 计数，以及 <c>PlayAiTurn(Normal)</c> 的零分配。
    /// </para>
    /// <para>
    /// <b>为什么测试自带一份规则参考实现</b>：用例只走公开 API（<see cref="IBoardGameReadView"/> /
    /// <see cref="IBoardGameInput"/>），不反射内核棋盘；"哪些点是合法手"必须由测试自己从 <c>GetCell</c>
    /// 独立算出来，才能证明内核的走法生成没写错（而不是拿内核的答案验内核）。
    /// </para>
    /// <para>
    /// <b>确定性脚本</b>：黑方（本地玩家）由测试按固定策略驱动——<see cref="PolicyFirstLegal"/> 扫描序第一个合法点、
    /// <see cref="PolicyLastLegal"/> 扫描序最后一个、<see cref="PolicyMaxFlips"/> 翻转数最大（同分取第一个），
    /// 白方交给内核 AI。整局因此完全可复现，终局子数可以写死断言。
    /// </para>
    /// </summary>
    public sealed class BoardGameTests
    {
        /// <summary>棋盘边长。</summary>
        private const int Size = 8;

        /// <summary>一局最多手数的循环兜底（正常 60 手收局）。</summary>
        private const int MaxPlies = 256;

        /// <summary>黑方编码。</summary>
        private const int PlayerBlack = 1;

        /// <summary>白方编码。</summary>
        private const int PlayerWhite = 2;

        /// <summary>黑方策略：扫描序第一个合法点。</summary>
        private const int PolicyFirstLegal = 0;

        /// <summary>黑方策略：扫描序最后一个合法点。</summary>
        private const int PolicyLastLegal = 1;

        /// <summary>黑方策略：翻转数最大（同分取扫描序第一个）。</summary>
        private const int PolicyMaxFlips = 2;

        /// <summary>胜利战利品模板 ID。</summary>
        private const int VictoryLootItemId = 1003;

        // ── 初始局面 ────────────────────────────────────────────────────

        [Fact]
        public void Constructor_ExposesStandardOpening()
        {
            BoardGameWorld world = CreateWorld(20261120);

            Assert.Equal(8, world.Size);
            Assert.Equal(2, world.BlackCount);
            Assert.Equal(2, world.WhiteCount);
            Assert.Equal(PlayerBlack, world.CurrentPlayer);
            Assert.Equal(0, world.MoveCount);
            Assert.Equal(0, world.PassCount);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(0, world.PlayerMoves);
            Assert.Equal(0, world.AiMoves);
            Assert.False(world.IsFinished);
            Assert.Equal(0, world.Winner);
            Assert.False(world.IsPassedLastTurn);
            Assert.Equal(-1, world.LastMoveX);
            Assert.Equal(-1, world.LastMoveY);

            // 中央 4 格：白 (3,3)/(4,4)、黑 (3,4)/(4,3)。
            Assert.Equal(BoardCell.White, world.GetCell(3, 3));
            Assert.Equal(BoardCell.White, world.GetCell(4, 4));
            Assert.Equal(BoardCell.Black, world.GetCell(3, 4));
            Assert.Equal(BoardCell.Black, world.GetCell(4, 3));
            Assert.Equal(BoardCell.Empty, world.GetCell(0, 0));
            Assert.Equal(BoardCell.Empty, world.GetCell(7, 7));

            AssertCountsMatchBoard(world);
        }

        [Fact]
        public void BlackOpening_LegalMoves_AreExactlyTheFourStandardPoints()
        {
            BoardGameWorld world = CreateWorld(1);

            List<int> legal = LegalMoves(world, PlayerBlack);

            Assert.Equal(4, legal.Count);
            Assert.Contains(CellIndex(2, 3), legal);
            Assert.Contains(CellIndex(3, 2), legal);
            Assert.Contains(CellIndex(4, 5), legal);
            Assert.Contains(CellIndex(5, 4), legal);

            // 开局四个点每个都只夹住一枚对方棋子。
            for (int i = 0; i < legal.Count; i++)
            {
                int cell = legal[i];
                Assert.Equal(1, CountFlips(world, cell % Size, cell / Size, PlayerBlack));
            }
        }

        [Fact]
        public void GetCell_OutOfBounds_ReturnsEmpty()
        {
            BoardGameWorld world = CreateWorld(1);

            Assert.Equal(BoardCell.Empty, world.GetCell(-1, 0));
            Assert.Equal(BoardCell.Empty, world.GetCell(0, -1));
            Assert.Equal(BoardCell.Empty, world.GetCell(Size, 0));
            Assert.Equal(BoardCell.Empty, world.GetCell(0, Size));
            Assert.Equal(BoardCell.Empty, world.GetCell(-8, 3));
            Assert.Equal(BoardCell.Empty, world.GetCell(99, -99));
        }

        // ── 非法落子 ────────────────────────────────────────────────────

        [Fact]
        public void TryPlace_OutOfBounds_IsRejectedWithoutChangingState()
        {
            BoardGameWorld world = CreateWorld(1);

            Assert.False(world.TryPlace(-1, 3));
            Assert.False(world.TryPlace(3, -1));
            Assert.False(world.TryPlace(Size, 3));
            Assert.False(world.TryPlace(3, Size));

            Assert.Equal(PlayerBlack, world.CurrentPlayer);
            Assert.Equal(0, world.MoveCount);
            Assert.Equal(0, world.PlayerMoves);
            Assert.Equal(2, world.BlackCount);
            Assert.Equal(2, world.WhiteCount);
            Assert.Equal(-1, world.LastMoveX);
            Assert.Equal(-1, world.LastMoveY);
        }

        [Fact]
        public void TryPlace_OccupiedCell_IsRejected()
        {
            BoardGameWorld world = CreateWorld(1);

            Assert.False(world.TryPlace(3, 3));
            Assert.False(world.TryPlace(4, 4));
            Assert.False(world.TryPlace(3, 4));
            Assert.False(world.TryPlace(4, 3));

            Assert.Equal(PlayerBlack, world.CurrentPlayer);
            Assert.Equal(0, world.MoveCount);
            Assert.Equal(2, world.BlackCount);
            Assert.Equal(2, world.WhiteCount);
        }

        [Fact]
        public void TryPlace_WithoutOutflank_IsRejected()
        {
            BoardGameWorld world = CreateWorld(1);

            // (0,0)/(7,7) 边角无子可夹；(2,2) 只看得到 (3,3) 一枚白子，末端不是黑子。
            Assert.False(world.TryPlace(0, 0));
            Assert.False(world.TryPlace(7, 7));
            Assert.False(world.TryPlace(2, 2));

            Assert.Equal(PlayerBlack, world.CurrentPlayer);
            Assert.Equal(0, world.MoveCount);
            Assert.Equal(BoardCell.Empty, world.GetCell(0, 0));
        }

        // ── 合法落子与翻转 ──────────────────────────────────────────────

        [Fact]
        public void TryPlace_FlipsOutflankedDiscs_AndHandsTurnToWhite()
        {
            BoardGameWorld world = CreateWorld(1);

            Assert.True(world.TryPlace(2, 3));

            Assert.Equal(4, world.BlackCount);
            Assert.Equal(1, world.WhiteCount);
            Assert.Equal(1, world.MoveCount);
            Assert.Equal(1, world.PlayerMoves);
            Assert.Equal(0, world.AiMoves);
            Assert.Equal(PlayerWhite, world.CurrentPlayer);
            Assert.Equal(2, world.LastMoveX);
            Assert.Equal(3, world.LastMoveY);
            Assert.Equal(BoardCell.Black, world.GetCell(2, 3));
            Assert.Equal(BoardCell.Black, world.GetCell(3, 3));
            Assert.False(world.IsPassedLastTurn);
            AssertCountsMatchBoard(world);
            AssertDiscTotalMatchesMoveCount(world);
        }

        [Fact]
        public void TryPlace_OnWhiteTurn_IsRejected()
        {
            BoardGameWorld world = CreateWorld(1);
            Assert.True(world.TryPlace(2, 3));

            List<int> whiteMoves = LegalMoves(world, PlayerWhite);
            Assert.NotEmpty(whiteMoves);

            int cell = whiteMoves[0];
            Assert.False(world.TryPlace(cell % Size, cell / Size));

            Assert.Equal(PlayerWhite, world.CurrentPlayer);
            Assert.Equal(1, world.MoveCount);
            Assert.Equal(0, world.AiMoves);
            Assert.Equal(4, world.BlackCount);
            Assert.Equal(1, world.WhiteCount);
            Assert.Equal(2, world.LastMoveX);
            Assert.Equal(3, world.LastMoveY);
        }

        [Fact]
        public void PlayAiTurn_OnBlackTurn_IsIgnored()
        {
            BoardGameWorld world = CreateWorld(1);

            world.PlayAiTurn(BoardGameDifficulty.Hard);

            Assert.Equal(PlayerBlack, world.CurrentPlayer);
            Assert.Equal(0, world.MoveCount);
            Assert.Equal(0, world.AiMoves);
            Assert.Equal(-1, world.LastMoveX);
            Assert.Equal(-1, world.LastMoveY);
            Assert.Equal(2, world.BlackCount);
            Assert.Equal(2, world.WhiteCount);
        }

        // ── 三档 AI ─────────────────────────────────────────────────────

        [Theory]
        [InlineData(BoardGameDifficulty.Easy)]
        [InlineData(BoardGameDifficulty.Normal)]
        [InlineData(BoardGameDifficulty.Hard)]
        public void Ai_EachDifficulty_PlacesALegalMoveWithoutBreakingRules(BoardGameDifficulty difficulty)
        {
            BoardGameWorld world = CreateWorld(20261120);
            Assert.True(world.TryPlace(2, 3));

            List<int> whiteMoves = LegalMoves(world, PlayerWhite);
            int moveBefore = world.MoveCount;
            int discsBefore = world.BlackCount + world.WhiteCount;

            world.PlayAiTurn(difficulty);

            Assert.Equal(1, world.AiMoves);
            Assert.Equal(moveBefore + 1, world.MoveCount);
            Assert.True(world.LastMoveX >= 0);
            Assert.True(world.LastMoveY >= 0);
            Assert.Equal(BoardCell.White, world.GetCell(world.LastMoveX, world.LastMoveY));
            Assert.Contains(CellIndex(world.LastMoveX, world.LastMoveY), whiteMoves);
            Assert.True(world.BlackCount + world.WhiteCount > discsBefore);
            AssertCountsMatchBoard(world);
            AssertDiscTotalMatchesMoveCount(world);
        }

        [Fact]
        public void Easy_IsDeterministicForSameSeed()
        {
            BoardGameWorld first = CreateWorld(12345);
            BoardGameWorld second = CreateWorld(12345);

            for (int round = 0; round < 3; round++)
            {
                int blackCell = PickMove(first, PlayerBlack, PolicyFirstLegal);
                Assert.True(first.TryPlace(blackCell % Size, blackCell / Size));
                Assert.True(second.TryPlace(blackCell % Size, blackCell / Size));

                first.PlayAiTurn(BoardGameDifficulty.Easy);
                second.PlayAiTurn(BoardGameDifficulty.Easy);

                Assert.Equal(first.LastMoveX, second.LastMoveX);
                Assert.Equal(first.LastMoveY, second.LastMoveY);
                Assert.Equal(first.MoveCount, second.MoveCount);
                Assert.Equal(first.BlackCount, second.BlackCount);
                Assert.Equal(first.WhiteCount, second.WhiteCount);
            }
        }

        [Fact]
        public void Normal_MaximizesFlippedDiscs()
        {
            BoardGameWorld world = CreateWorld(1);
            Assert.True(world.TryPlace(2, 3));

            int expectedCell = -1;
            int expectedFlips = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int flips = CountFlips(world, x, y, PlayerWhite);
                    if (flips > expectedFlips)
                    {
                        expectedFlips = flips;
                        expectedCell = CellIndex(x, y);
                    }
                }
            }

            world.PlayAiTurn(BoardGameDifficulty.Normal);

            Assert.True(expectedCell >= 0);
            Assert.Equal(expectedCell % Size, world.LastMoveX);
            Assert.Equal(expectedCell / Size, world.LastMoveY);
        }

        [Theory]
        [InlineData(9)]
        [InlineData(200)]
        public void InvalidDifficulty_IsTreatedAsNormal(int rawDifficulty)
        {
            BoardGameWorld invalid = CreateWorld(1);
            BoardGameWorld normal = CreateWorld(1);
            Assert.True(invalid.TryPlace(2, 3));
            Assert.True(normal.TryPlace(2, 3));

            invalid.PlayAiTurn((BoardGameDifficulty)rawDifficulty);
            normal.PlayAiTurn(BoardGameDifficulty.Normal);

            Assert.Equal(normal.LastMoveX, invalid.LastMoveX);
            Assert.Equal(normal.LastMoveY, invalid.LastMoveY);
            Assert.Equal(normal.BlackCount, invalid.BlackCount);
            Assert.Equal(normal.WhiteCount, invalid.WhiteCount);
        }

        [Fact]
        public void Hard_TakesAvailableCorner_InsteadOfLowerValueMoves()
        {
            BoardGameWorld world = CreateWorld(1);

            // 确定性脚本：黑方始终落"扫描序第一个合法点"，白方走 Hard。
            // 走到第 15 手（白方回合）时盘面是 黑 13 / 白 6，且白方唯一的角 (0,0) 刚好可占。
            int guard = 0;
            while (!world.IsFinished && world.MoveCount < 15 && guard < MaxPlies)
            {
                guard++;
                if (world.CurrentPlayer == PlayerBlack)
                {
                    int blackCell = PickMove(world, PlayerBlack, PolicyFirstLegal);
                    Assert.True(world.TryPlace(blackCell % Size, blackCell / Size));
                }
                else
                {
                    world.PlayAiTurn(BoardGameDifficulty.Hard);
                }
            }

            Assert.Equal(15, world.MoveCount);
            Assert.Equal(PlayerWhite, world.CurrentPlayer);
            Assert.Equal(13, world.BlackCount);
            Assert.Equal(6, world.WhiteCount);

            List<int> legalCorners = LegalCorners(world, PlayerWhite);
            Assert.Single(legalCorners);
            Assert.Equal(CellIndex(0, 0), legalCorners[0]);

            world.PlayAiTurn(BoardGameDifficulty.Hard);

            // 角 +30 压过其余位置（其余格权重 0、角邻 -12），Hard 必须占这个角。
            Assert.Equal(8, world.AiMoves);
            Assert.Equal(8, world.PlayerMoves);
            Assert.Equal(0, world.LastMoveX);
            Assert.Equal(0, world.LastMoveY);
            Assert.Equal(BoardCell.White, world.GetCell(0, 0));
            Assert.Equal(10, world.BlackCount);
            Assert.Equal(10, world.WhiteCount);
            AssertCountsMatchBoard(world);
            AssertDiscTotalMatchesMoveCount(world);
        }

        // ── 跳过与终局 ──────────────────────────────────────────────────

        [Fact]
        public void Pass_WhenOpponentHasNoMove_SkipsTurnAndKeepsMoverCurrent()
        {
            BoardGameWorld world = CreateWorld(1);
            bool sawPass = false;
            int guard = 0;

            while (!world.IsFinished && guard < MaxPlies)
            {
                guard++;
                int passBefore = world.PassCount;
                int moveBefore = world.MoveCount;
                int playerBefore = world.CurrentPlayer;

                if (playerBefore == PlayerBlack)
                {
                    int blackCell = PickMove(world, PlayerBlack, PolicyFirstLegal);
                    Assert.True(world.TryPlace(blackCell % Size, blackCell / Size));
                }
                else
                {
                    world.PlayAiTurn(BoardGameDifficulty.Normal);
                }

                // 跳过不是一手棋：手数只加这一手。
                Assert.Equal(moveBefore + 1, world.MoveCount);

                if (world.PassCount > passBefore)
                {
                    Assert.Equal(passBefore + 1, world.PassCount);
                    Assert.True(world.IsPassedLastTurn);
                    // 回合交还给刚落子的一方。
                    Assert.Equal(playerBefore, world.CurrentPlayer);
                    sawPass = true;
                    break;
                }

                Assert.Equal(passBefore, world.PassCount);
                Assert.False(world.IsPassedLastTurn);
            }

            // 这一局（Normal + 黑方扫描序第一个）在第 47 手出现首次单方跳过，用例必须真的走到。
            Assert.True(sawPass);
        }

        [Fact]
        public void WhenNeitherSideHasMove_GameFinishesWithWhiteWin()
        {
            BoardGameWorld world = CreateWorld(1);
            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyFirstLegal);

            Assert.True(world.IsFinished);
            Assert.Empty(LegalMoves(world, PlayerBlack));
            Assert.Empty(LegalMoves(world, PlayerWhite));
            Assert.True(world.PassCount >= 1);
            Assert.True(world.IsPassedLastTurn);
            Assert.Equal(60, world.MoveCount);
            Assert.Equal(64, world.BlackCount + world.WhiteCount);
            Assert.Equal(23, world.BlackCount);
            Assert.Equal(41, world.WhiteCount);
            Assert.Equal(PlayerWhite, world.Winner);
            AssertCountsMatchBoard(world);
        }

        [Fact]
        public void Winner_IsBlack_WhenBlackHasMoreDiscs()
        {
            BoardGameWorld world = CreateWorld(1);
            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyLastLegal);

            Assert.True(world.IsFinished);
            Assert.Equal(48, world.BlackCount);
            Assert.Equal(16, world.WhiteCount);
            Assert.Equal(PlayerBlack, world.Winner);
        }

        [Fact]
        public void Winner_IsZero_WhenCountsAreEqual()
        {
            BoardGameWorld world = CreateWorld(24);
            PlayUntilFinished(world, BoardGameDifficulty.Easy, PolicyMaxFlips);

            Assert.True(world.IsFinished);
            Assert.Equal(32, world.BlackCount);
            Assert.Equal(32, world.WhiteCount);
            Assert.Equal(0, world.Winner);
            Assert.Equal(64, world.BlackCount + world.WhiteCount);
            Assert.True(world.PassCount >= 1);
        }

        // ── 结算 ────────────────────────────────────────────────────────

        [Fact]
        public void CollectResult_BeforeFinish_Throws()
        {
            BoardGameWorld world = CreateWorld(1);

            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
            Assert.False(world.IsFinished);
            Assert.Equal(0, world.MoveCount);
        }

        [Fact]
        public void CollectResult_Victory_ReportsTurnKernelLootAndBlackDiscs()
        {
            BoardGameWorld world = CreateWorld(1);

            // Step 只在对局未结束时累加：先在开局前推 3 步，再打完（打的过程中不调 Step）。
            world.Step(1f / 60f);
            world.Step(1f / 60f);
            world.Step(1f / 60f);
            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyLastLegal);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(3, result.StepCount);
            Assert.Equal(3, world.ProcessedSteps);
            Assert.Equal(48, result.RemainingUnits);
            Assert.Equal(world.BlackCount, result.RemainingUnits);
            Assert.Empty(result.TriggeredTags);

            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(VictoryLootItemId, loot.ItemId);
            Assert.Equal(1, loot.Count);
        }

        [Fact]
        public void CollectResult_Defeat_HasNoLootButReportsBlackDiscs()
        {
            BoardGameWorld world = CreateWorld(1);
            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyFirstLegal);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
            Assert.Equal(23, result.RemainingUnits);
        }

        [Fact]
        public void CollectResult_Draw_HasNoLoot()
        {
            BoardGameWorld world = CreateWorld(24);
            PlayUntilFinished(world, BoardGameDifficulty.Easy, PolicyMaxFlips);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
            Assert.Equal(32, result.RemainingUnits);
        }

        [Fact]
        public void CollectResult_SecondCall_ThrowsAndLocksSession()
        {
            BoardGameWorld world = CreateWorld(1);
            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyLastLegal);

            world.CollectResult();

            Assert.True(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            // 所有权已交还：输入全部失效，重开也不再生效。
            Assert.False(world.TryPlace(2, 3));
            world.PlayAiTurn(BoardGameDifficulty.Hard);
            Assert.Equal(60, world.MoveCount);
            world.Restart();
            Assert.Equal(60, world.MoveCount);
            Assert.Equal(PlayerBlack, world.Winner);
        }

        // ── 重开与推演计数 ──────────────────────────────────────────────

        [Fact]
        public void Restart_ResetsBoardAndCountersButKeepsCumulativePassCount()
        {
            BoardGameWorld world = CreateWorld(1);
            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyFirstLegal);

            int passesBefore = world.PassCount;
            Assert.True(passesBefore >= 1);
            Assert.True(world.PlayerMoves > 0);
            Assert.True(world.AiMoves > 0);
            Assert.Equal(60, world.MoveCount);

            world.Restart();

            Assert.Equal(0, world.MoveCount);
            Assert.Equal(0, world.PlayerMoves);
            Assert.Equal(0, world.AiMoves);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(passesBefore, world.PassCount);
            Assert.Equal(PlayerBlack, world.CurrentPlayer);
            Assert.Equal(2, world.BlackCount);
            Assert.Equal(2, world.WhiteCount);
            Assert.False(world.IsFinished);
            Assert.Equal(0, world.Winner);
            Assert.False(world.IsPassedLastTurn);
            Assert.Equal(-1, world.LastMoveX);
            Assert.Equal(-1, world.LastMoveY);
            Assert.Equal(BoardCell.White, world.GetCell(3, 3));
            Assert.Equal(BoardCell.Black, world.GetCell(3, 4));
            Assert.Equal(BoardCell.Empty, world.GetCell(0, 0));

            // 重开后可以重新开局。
            Assert.True(world.TryPlace(2, 3));
            Assert.Equal(1, world.MoveCount);
        }

        [Fact]
        public void Step_CountsUntilFinish_AndRestartClearsIt()
        {
            BoardGameWorld world = CreateWorld(1);

            for (int i = 0; i < 5; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.Equal(5, world.ProcessedSteps);

            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyFirstLegal);
            int finishedSteps = world.ProcessedSteps;
            Assert.True(finishedSteps >= 5);

            world.Step(1f / 60f);
            Assert.Equal(finishedSteps, world.ProcessedSteps);

            world.Restart();
            Assert.Equal(0, world.ProcessedSteps);

            world.Step(1f / 60f);
            Assert.Equal(1, world.ProcessedSteps);
        }

        // ── 零分配 ──────────────────────────────────────────────────────

        [Fact]
        public void PlayAiTurn_Normal_AfterWarmup_DoesNotAllocate()
        {
            BoardGameWorld world = CreateWorld(1);

            // 预热：完整打一局，把棋盘数组、走法缓冲与 JIT 全部走热。
            PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyFirstLegal);

            // 最多量 3 轮、任一轮为 0 即通过：多层 JIT（分层编译/Tier1 升级）会在前几次测量中途插入
            // 编译期分配，把严格 0 断言变成与机器负载相关的偶发红。口径仍然是"稳态零分配"，
            // 只是允许一次测量被 JIT 噪声污染——真出现分配时 3 轮都会非 0，断言照样红。
            long bestAllocated = long.MaxValue;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    PlayUntilFinished(world, BoardGameDifficulty.Normal, PolicyFirstLegal);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                int aiCalls = 0;
                while (aiCalls < 1000)
                {
                    if (world.IsFinished)
                    {
                        world.Restart();
                    }

                    if (world.CurrentPlayer == PlayerBlack)
                    {
                        int blackCell = PickMove(world, PlayerBlack, PolicyFirstLegal);
                        world.TryPlace(blackCell % Size, blackCell / Size);
                    }
                    else
                    {
                        world.PlayAiTurn(BoardGameDifficulty.Normal);
                        aiCalls++;
                    }
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Equal(1000, aiCalls);

                if (allocated < bestAllocated)
                {
                    bestAllocated = allocated;
                }

                if (bestAllocated == 0L)
                {
                    break;
                }
            }

            Assert.Equal(0L, bestAllocated);
        }

        // ── 测试侧规则参考实现（只读公开视图，不碰内核内部数组） ─────────

        /// <summary>构造一个会话：关卡 ID 固定 1，单位数 0（黑白棋不读这两个字段，只读种子）。</summary>
        private static BoardGameWorld CreateWorld(int seed)
        {
            return new BoardGameWorld(new GameplayInitData(1, seed, 0));
        }

        /// <summary>行主序索引：<c>y * 8 + x</c>。</summary>
        private static int CellIndex(int x, int y)
        {
            return x + (y * Size);
        }

        private static bool InBounds(int x, int y)
        {
            return x >= 0 && x < Size && y >= 0 && y < Size;
        }

        /// <summary>
        /// 参考实现：在 (x, y) 落 <paramref name="player"/> 子能翻转多少枚；0 表示非法。
        /// 与内核规则一致，但完全由 <see cref="IBoardGameReadView.GetCell"/> 独立计算。
        /// </summary>
        private static int CountFlips(IBoardGameReadView view, int x, int y, int player)
        {
            if (!InBounds(x, y))
            {
                return 0;
            }

            if (view.GetCell(x, y) != BoardCell.Empty)
            {
                return 0;
            }

            int opponent = player == PlayerBlack ? PlayerWhite : PlayerBlack;
            int total = 0;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int cx = x + dx;
                    int cy = y + dy;
                    int run = 0;

                    while (InBounds(cx, cy) && (int)view.GetCell(cx, cy) == opponent)
                    {
                        run++;
                        cx += dx;
                        cy += dy;
                    }

                    if (run > 0 && InBounds(cx, cy) && (int)view.GetCell(cx, cy) == player)
                    {
                        total += run;
                    }
                }
            }

            return total;
        }

        /// <summary>参考实现：<paramref name="player"/> 的全部合法落子（行主序）。</summary>
        private static List<int> LegalMoves(IBoardGameReadView view, int player)
        {
            List<int> moves = new List<int>();
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (CountFlips(view, x, y, player) > 0)
                    {
                        moves.Add(CellIndex(x, y));
                    }
                }
            }

            return moves;
        }

        /// <summary>参考实现：四个角里当前可占的（行主序：左上、右上、左下、右下）。</summary>
        private static List<int> LegalCorners(IBoardGameReadView view, int player)
        {
            int[] corners = { CellIndex(0, 0), CellIndex(7, 0), CellIndex(0, 7), CellIndex(7, 7) };
            List<int> legal = new List<int>();

            for (int i = 0; i < corners.Length; i++)
            {
                int cell = corners[i];
                if (CountFlips(view, cell % Size, cell / Size, player) > 0)
                {
                    legal.Add(cell);
                }
            }

            return legal;
        }

        /// <summary>按固定策略替黑方选一手；无合法手返回 <c>-1</c>。</summary>
        private static int PickMove(IBoardGameReadView view, int player, int policy)
        {
            int best = -1;
            int bestFlips = -1;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int flips = CountFlips(view, x, y, player);
                    if (flips <= 0)
                    {
                        continue;
                    }

                    int cell = CellIndex(x, y);
                    if (policy == PolicyFirstLegal)
                    {
                        return cell;
                    }

                    if (policy == PolicyLastLegal)
                    {
                        best = cell;
                    }
                    else if (flips > bestFlips)
                    {
                        bestFlips = flips;
                        best = cell;
                    }
                }
            }

            return best;
        }

        /// <summary>用固定黑方策略 + 指定 AI 难度打完整局，并断言每一步都真的落下了。</summary>
        private static void PlayUntilFinished(BoardGameWorld world, BoardGameDifficulty difficulty, int policy)
        {
            int guard = 0;
            while (!world.IsFinished && guard < MaxPlies)
            {
                guard++;

                if (world.CurrentPlayer == PlayerBlack)
                {
                    int cell = PickMove(world, PlayerBlack, policy);
                    Assert.True(cell >= 0);
                    Assert.True(world.TryPlace(cell % Size, cell / Size));
                }
                else
                {
                    int movesBefore = world.MoveCount;
                    world.PlayAiTurn(difficulty);
                    Assert.True(world.MoveCount > movesBefore);
                }
            }

            Assert.True(world.IsFinished);
        }

        /// <summary>计数必须与盘面逐格扫描一致（防止增量计数写错）。</summary>
        private static void AssertCountsMatchBoard(IBoardGameReadView view)
        {
            int black = 0;
            int white = 0;

            for (int y = 0; y < view.Size; y++)
            {
                for (int x = 0; x < view.Size; x++)
                {
                    BoardCell cell = view.GetCell(x, y);
                    if (cell == BoardCell.Black)
                    {
                        black++;
                    }
                    else if (cell == BoardCell.White)
                    {
                        white++;
                    }
                }
            }

            Assert.Equal(black, view.BlackCount);
            Assert.Equal(white, view.WhiteCount);
        }

        /// <summary>总子数恒为"开局 4 子 + 已落手数"（每次落子只增 1 枚，翻转不改总数）。</summary>
        private static void AssertDiscTotalMatchesMoveCount(IBoardGameReadView view)
        {
            Assert.Equal(4 + view.MoveCount, view.BlackCount + view.WhiteCount);
        }
    }
}
