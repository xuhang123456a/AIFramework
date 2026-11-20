using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Kernel.Turn.Puzzle;
using Xunit;

namespace AIFramework.Core.Tests.Puzzle
{
    /// <summary>
    /// 外部关卡（配置表 / 编辑器导出）进入内核的入口验收。
    /// <para>
    /// 为什么值得单独测：这条构造是"数据驱动关卡"的唯一入口。若它悄悄退化成"只认内置关卡"，
    /// 编辑器导出的 JSON 就永远进不了棋盘，而所有既有测试仍然全绿——这是最容易被忽略的一类退化。
    /// </para>
    /// </summary>
    public sealed class PuzzleExternalLevelTests
    {
        /// <summary>一个最小可解关卡（7×5，与内置第 1 关同型但玩家在右侧）。</summary>
        private static readonly string[] ExternalRows =
        {
            "#######",
            "#  #  #",
            "# .$@ #",
            "#   # #",
            "#######"
        };

        [Fact]
        public void ExternalLevel_ConstructsAndReportsDimensions()
        {
            var init = new GameplayInitData(levelId: 99, randomSeed: 1, unitCount: 1);
            var world = new PuzzleWorld(in init, ExternalRows, "puzzle-demo.json");

            Assert.Equal(7, world.Width);
            Assert.Equal(5, world.Height);
            Assert.Equal(99, world.LevelId);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void ExternalLevel_PlayerCanMoveAndPushBox()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            var world = new PuzzleWorld(in init, ExternalRows);

            // 玩家在 (4,2)，箱子在 (3,2)，目标在 (2,2)：向左推一次即把箱子推到目标，立刻解出。
            Assert.True(world.TryMove(3));
            Assert.True(world.IsFinished);
            Assert.Equal(1, world.Moves);

            // 已解出后再移动一律被拒（不消耗步数）。
            Assert.False(world.TryMove(3));
            Assert.Equal(1, world.Moves);
        }

        [Fact]
        public void ExternalLevel_WallBlocksMovement()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            var world = new PuzzleWorld(in init, ExternalRows);

            // 玩家上方 (5,3) 是空地，但边界 (5,4) 是墙：连按两次向上，第二次必须被拒。
            Assert.True(world.TryMove(0));
            Assert.False(world.TryMove(0));

            Assert.Equal(1, world.Moves);
        }

        [Fact]
        public void ExternalLevel_ResetRestoresInitialLayout()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            var world = new PuzzleWorld(in init, ExternalRows);
            world.TryMove(3);

            world.Reset();

            Assert.Equal(0, world.Moves);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void ExternalLevel_CollectResultProducesGameplayResult()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            var world = new PuzzleWorld(in init, ExternalRows);
            world.TryMove(3);

            GameplayResult result = world.CollectResult();

            Assert.True(result.IsVictory);
            Assert.Equal(GameplayKernelType.Puzzle, result.KernelType);
            Assert.Equal(1, result.RemainingUnits); // 结算口径：推箱步数
            Assert.Single(result.Loots);
        }

        [Fact]
        public void ExternalLevel_EmptyRows_ThrowsArgumentException()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);

            Assert.Throws<ArgumentException>(() => new PuzzleWorld(in init, Array.Empty<string>()));
        }

        [Fact]
        public void ExternalLevel_UnevenWidths_ThrowsArgumentException()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            string[] rows = { "####", "#@ .#", "###" };

            Assert.Throws<ArgumentException>(() => new PuzzleWorld(in init, rows));
        }

        [Fact]
        public void ExternalLevel_UnknownCharacter_ThrowsArgumentException()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            string[] rows = { "####", "#@$?", "####" };

            Assert.Throws<ArgumentException>(() => new PuzzleWorld(in init, rows));
        }

        [Fact]
        public void ExternalLevel_MissingPlayer_ThrowsArgumentException()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            string[] rows = { "####", "# $.", "####" };

            Assert.Throws<ArgumentException>(() => new PuzzleWorld(in init, rows));
        }

        [Fact]
        public void ExternalLevel_DuplicatePlayers_ThrowsArgumentException()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            string[] rows = { "#####", "#@$@.", "#####" };

            Assert.Throws<ArgumentException>(() => new PuzzleWorld(in init, rows));
        }

        [Fact]
        public void ExternalLevel_TooWide_ThrowsArgumentException()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            string[] rows = { new string('#', 9), "#@$....#", new string('#', 9) };

            Assert.Throws<ArgumentException>(() => new PuzzleWorld(in init, rows));
        }

        [Fact]
        public void ExternalLevel_ErrorMentionsSourceName()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            string[] rows = { "####" };

            ArgumentException exception = Assert.Throws<ArgumentException>(
                () => new PuzzleWorld(in init, rows, "puzzle-broken.json"));

            Assert.Contains("puzzle-broken.json", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ExternalLevel_WithBoxOnGoalAndPlayerOnGoal_Parses()
        {
            var init = new GameplayInitData(levelId: 1, randomSeed: 1, unitCount: 1);
            string[] rows =
            {
                "#####",
                "#*+ #",
                "#####"
            };

            var world = new PuzzleWorld(in init, rows);

            Assert.Equal(1, world.BoxCount);
            Assert.True(world.IsFinished); // 唯一箱子已在目标点上，开局即完成
        }

        [Fact]
        public void BuiltInLevels_StillWorkAfterExternalEntryAdded()
        {
            var init = new GameplayInitData(levelId: 3, randomSeed: 1, unitCount: 1);
            var world = new PuzzleWorld(in init);

            Assert.Equal(8, world.Width);
            Assert.Equal(2, world.BoxCount);
        }
    }
}
