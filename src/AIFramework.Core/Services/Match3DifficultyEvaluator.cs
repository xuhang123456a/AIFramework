using System;
using System.Collections.Generic;

namespace AIFramework.Core.Services
{
    /// <summary>三消棋盘的难度评估结论。</summary>
    public readonly struct Match3DifficultyReport
    {
        /// <summary>构造结论。</summary>
        /// <param name="width">棋盘宽。</param>
        /// <param name="height">棋盘高。</param>
        /// <param name="availableMoves">当前可形成的三连交换数。</param>
        /// <param name="difficultyScore">难度分（0..100，越大越难）。</param>
        /// <param name="isSolvable">是否至少有一手可走。</param>
        /// <param name="reasons">判定原因。</param>
        public Match3DifficultyReport(int width, int height, int availableMoves, int difficultyScore, bool isSolvable, string[] reasons)
        {
            Width = width;
            Height = height;
            AvailableMoves = availableMoves;
            DifficultyScore = difficultyScore;
            IsSolvable = isSolvable;
            Reasons = reasons ?? new string[0];
        }

        /// <summary>棋盘宽。</summary>
        public int Width { get; }

        /// <summary>棋盘高。</summary>
        public int Height { get; }

        /// <summary>可形成三连的交换数（越高越好打）。</summary>
        public int AvailableMoves { get; }

        /// <summary>难度分（0..100）。</summary>
        public int DifficultyScore { get; }

        /// <summary>是否至少有一手可走（false = 死局，需要洗牌）。</summary>
        public bool IsSolvable { get; }

        /// <summary>判定原因。</summary>
        public string[] Reasons { get; }

        /// <summary>一行报告（口径固定，便于关卡批量导出/横向对比）。</summary>
        public string ToReportLine()
        {
            return "[Match3] " + Width + "x" + Height
                + " 可行手=" + AvailableMoves
                + " 难度=" + DifficultyScore
                + " 可解=" + (IsSolvable ? "是" : "否")
                + (Reasons.Length == 0 ? string.Empty : "（" + string.Join("；", Reasons) + "）");
        }
    }

    /// <summary>
    /// 三消关卡难度评估：不依赖内核内部结构，只吃"颜色网格"。
    /// <para>
    /// <b>为什么难度评估要独立于内核</b>：关卡设计（编辑器/配置表）需要在**批量导入**时给出难度报告，
    /// 那时并没有运行中的 <c>Match3World</c>；把算法放在数值层（只吃 <c>int[]</c>），
    /// 编辑器/CI/内核都能复用同一套口径，不会出现"编辑器说简单、玩家觉得难"的两套标准。
    /// </para>
    /// <para>
    /// <b>为什么用"可行手数量"作为主指标</b>：三消的可玩性最直接取决于玩家有多少种可走的手；
    /// 可行手越少，越依赖运气/道具，体感越难。颜色分布与连锁潜力作为辅助原因写进 <see cref="Match3DifficultyReport.Reasons"/>。
    /// </para>
    /// <para>
    /// 本类纯算术、无分配（除结果数组），可在无头环境批量跑。
    /// </para>
    /// </summary>
    public static class Match3DifficultyEvaluator
    {
        /// <summary>有效颜色数（0 表示空位；1..colorCount 为颜色）。</summary>
        public const int DefaultColorCount = 5;

        /// <summary>评估一张棋盘。</summary>
        /// <param name="cells">行优先网格（长度 = width*height；0 表示空位，1..colorCount 为颜色）。</param>
        /// <param name="width">宽。</param>
        /// <param name="height">高。</param>
        /// <returns>难度结论。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="cells"/> 为 null。</exception>
        /// <exception cref="ArgumentException">尺寸为 0 或网格长度不符。</exception>
        public static Match3DifficultyReport Evaluate(int[] cells, int width, int height)
        {
            if (cells == null)
            {
                throw new ArgumentNullException(nameof(cells));
            }

            if (width <= 0 || height <= 0 || width * height != cells.Length)
            {
                throw new ArgumentException("网格长度必须等于 width*height，且尺寸为正。", nameof(cells));
            }

            var reasons = new List<string>(3);
            int availableMoves = CountAvailableMoves(cells, width, height);
            bool solvable = availableMoves > 0;

            if (!solvable)
            {
                reasons.Add("没有可形成三连的交换（死局，需要洗牌或补步）");
            }
            else if (availableMoves <= 3)
            {
                reasons.Add("可行手仅 " + availableMoves + " 种（偏难，容易卡住）");
            }

            // 颜色分布：某个颜色占比过高/过低都说明生成质量差，会影响连锁多样性。
            Span<int> counts = stackalloc int[DefaultColorCount + 1];
            for (int i = 0; i < cells.Length; i++)
            {
                int color = cells[i];
                if (color >= 0 && color <= DefaultColorCount)
                {
                    counts[color]++;
                }
            }

            int total = cells.Length;
            for (int color = 1; color <= DefaultColorCount; color++)
            {
                double ratio = (double)counts[color] / total;
                if (ratio > 0.4d)
                {
                    reasons.Add("颜色 " + color + " 占比 " + ratio.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)
                        + "（过高，连锁过于单一）");
                    break;
                }
            }

            int difficulty = Score(availableMoves, cells.Length);
            return new Match3DifficultyReport(width, height, availableMoves, difficulty, solvable, reasons.ToArray());
        }

        /// <summary>难度分：可行手越多越简单（每 1 手约降 6 分，死局 100 分）。</summary>
        public static int Score(int availableMoves, int cellCount)
        {
            if (cellCount <= 0 || availableMoves <= 0)
            {
                return 100;
            }

            int score = 100 - (availableMoves * 6);
            return score < 0 ? 0 : (score > 100 ? 100 : score);
        }

        /// <summary>统计可形成三连的交换数（交换后必须消除，且不重复计数同一对）。</summary>
        public static int CountAvailableMoves(int[] cells, int width, int height)
        {
            if (cells == null)
            {
                throw new ArgumentNullException(nameof(cells));
            }

            int moves = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (x + 1 < width)
                    {
                        Swap(cells, width, x, y, x + 1, y);
                        bool match = HasMatchAt(cells, width, height, x, y) || HasMatchAt(cells, width, height, x + 1, y);
                        Swap(cells, width, x, y, x + 1, y);
                        if (match)
                        {
                            moves++;
                        }
                    }

                    if (y + 1 < height)
                    {
                        Swap(cells, width, x, y, x, y + 1);
                        bool match = HasMatchAt(cells, width, height, x, y) || HasMatchAt(cells, width, height, x, y + 1);
                        Swap(cells, width, x, y, x, y + 1);
                        if (match)
                        {
                            moves++;
                        }
                    }
                }
            }

            return moves;
        }

        /// <summary>交换两格（行优先索引 = y*width+x）。</summary>
        private static void Swap(int[] cells, int width, int x1, int y1, int x2, int y2)
        {
            int first = (y1 * width) + x1;
            int second = (y2 * width) + x2;
            int temp = cells[first];
            cells[first] = cells[second];
            cells[second] = temp;
        }

        /// <summary>是否存在经过 (x,y) 的三连（横向或纵向）。</summary>
        private static bool HasMatchAt(int[] cells, int width, int height, int x, int y)
        {
            int color = cells[(y * width) + x];
            if (color == 0)
            {
                return false;
            }

            int horizontal = 1;
            for (int i = x - 1; i >= 0 && cells[(y * width) + i] == color; i--)
            {
                horizontal++;
            }

            for (int i = x + 1; i < width && cells[(y * width) + i] == color; i++)
            {
                horizontal++;
            }

            if (horizontal >= 3)
            {
                return true;
            }

            int vertical = 1;
            for (int i = y - 1; i >= 0 && cells[(i * width) + x] == color; i--)
            {
                vertical++;
            }

            for (int i = y + 1; i < height && cells[(i * width) + x] == color; i++)
            {
                vertical++;
            }

            return vertical >= 3;
        }
    }
}
