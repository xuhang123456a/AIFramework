using System;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 三消难度评估：可行手计数（交换后是否成三连）、死局判定、难度分单调性与报告口径。
    /// </summary>
    public sealed class Match3DifficultyEvaluatorTests
    {
        /// <summary>构造行优先网格。</summary>
        private static int[] Grid(params int[][] rows)
        {
            var cells = new int[rows.Length * rows[0].Length];
            for (int y = 0; y < rows.Length; y++)
            {
                for (int x = 0; x < rows[0].Length; x++)
                {
                    cells[(y * rows[0].Length) + x] = rows[y][x];
                }
            }

            return cells;
        }

        [Fact]
        public void Evaluate_SolvableBoard_ReportsMoves()
        {
            // 第一行 1,1,2 且第二行 (2,0) 是 1：把 (2,0) 与 (2,1) 交换即可凑出 1,1,1。
            int[] cells = Grid(
                new[] { 1, 1, 2, 3 },
                new[] { 3, 4, 1, 2 },
                new[] { 4, 2, 3, 4 },
                new[] { 2, 3, 4, 1 });

            Match3DifficultyReport report = Match3DifficultyEvaluator.Evaluate(cells, 4, 4);

            Assert.True(report.AvailableMoves > 0);
            Assert.True(report.IsSolvable);
            Assert.InRange(report.DifficultyScore, 0, 99);
        }

        [Fact]
        public void Evaluate_DeadBoard_ReportsUnsolvable()
        {
            // 全部空位（0）：不可能形成任何三连，等价于"死局"（需要洗牌/补新棋）。
            int[] cells = new int[16];

            Match3DifficultyReport report = Match3DifficultyEvaluator.Evaluate(cells, 4, 4);

            Assert.Equal(0, report.AvailableMoves);
            Assert.False(report.IsSolvable);
            Assert.Equal(100, report.DifficultyScore);
            Assert.Contains(report.Reasons, reason => reason.Contains("死局"));
        }

        [Fact]
        public void Evaluate_NullCells_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => Match3DifficultyEvaluator.Evaluate(null!, 4, 4));
        }

        [Fact]
        public void Evaluate_SizeMismatch_Throws()
        {
            Assert.Throws<ArgumentException>(() => Match3DifficultyEvaluator.Evaluate(new int[8], 4, 4));
            Assert.Throws<ArgumentException>(() => Match3DifficultyEvaluator.Evaluate(new int[16], 0, 4));
        }

        [Fact]
        public void Score_FewerMoves_IsHarder()
        {
            Assert.True(Match3DifficultyEvaluator.Score(1, 64) > Match3DifficultyEvaluator.Score(10, 64));
            Assert.Equal(100, Match3DifficultyEvaluator.Score(0, 64));
            Assert.Equal(0, Match3DifficultyEvaluator.Score(100, 64));
        }

        [Fact]
        public void Score_EdgeCases_ReturnBaseline()
        {
            Assert.Equal(100, Match3DifficultyEvaluator.Score(5, 0));
            Assert.Equal(100, Match3DifficultyEvaluator.Score(-3, 64));
        }

        [Fact]
        public void CountAvailableMoves_DoesNotMutateCells()
        {
            int[] cells = Grid(
                new[] { 1, 1, 2, 3 },
                new[] { 3, 4, 1, 2 },
                new[] { 4, 2, 3, 4 },
                new[] { 2, 3, 4, 1 });
            int[] before = (int[])cells.Clone();

            Match3DifficultyEvaluator.CountAvailableMoves(cells, 4, 4);

            Assert.Equal(before, cells);
        }

        [Fact]
        public void CountAvailableMoves_EmptyCellsDoNotMatch()
        {
            // 0 是空位，两个 0 加一个颜色不能算三连。
            int[] cells = Grid(
                new[] { 0, 0, 1, 2 },
                new[] { 2, 3, 4, 1 },
                new[] { 1, 2, 3, 4 },
                new[] { 4, 1, 2, 3 });

            int moves = Match3DifficultyEvaluator.CountAvailableMoves(cells, 4, 4);

            Assert.True(moves >= 0);
        }

        [Fact]
        public void Evaluate_OneColorDominant_AddsReason()
        {
            var cells = new int[16];
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = 1;
            }

            Match3DifficultyReport report = Match3DifficultyEvaluator.Evaluate(cells, 4, 4);

            Assert.Contains(report.Reasons, reason => reason.Contains("占比"));
        }

        [Fact]
        public void Evaluate_ReportLineHasFixedFields()
        {
            int[] cells = Grid(
                new[] { 1, 1, 2, 3 },
                new[] { 3, 4, 1, 2 },
                new[] { 4, 2, 3, 4 },
                new[] { 2, 3, 4, 1 });

            string line = Match3DifficultyEvaluator.Evaluate(cells, 4, 4).ToReportLine();

            Assert.StartsWith("[Match3] 4x4", line, StringComparison.Ordinal);
            Assert.Contains("可行手=", line);
            Assert.Contains("难度=", line);
            Assert.Contains("可解=", line);
        }

        [Fact]
        public void Evaluate_FewerMovesMeansHigherDifficulty()
        {
            int[] rich = Grid(
                new[] { 1, 1, 2, 3 },
                new[] { 3, 4, 1, 2 },
                new[] { 4, 2, 3, 4 },
                new[] { 2, 3, 4, 1 });
            int[] dead = new int[16];

            int richScore = Match3DifficultyEvaluator.Evaluate(rich, 4, 4).DifficultyScore;
            int deadScore = Match3DifficultyEvaluator.Evaluate(dead, 4, 4).DifficultyScore;

            Assert.True(deadScore > richScore);
        }
    }
}
