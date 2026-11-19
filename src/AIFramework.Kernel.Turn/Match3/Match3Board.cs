using System;

namespace AIFramework.Kernel.Turn.Match3
{
    /// <summary>
    /// 三消网格：颜色存取、三连标记、清格、列内重力与顶部补格。不含分数/步数等会话状态。
    /// <para>
    /// <b>坐标约定</b>：x 向右（<c>0..Width-1</c>），y 向上（<c>0</c> 为最底行，<c>Height-1</c> 为最顶行）。
    /// 因此「下落」= y 减小，「顶部补格」= 从空位向上填到 <c>Height-1</c>。
    /// </para>
    /// <para>
    /// 所有缓冲（格值 / 标记位 / 候选颜色）都在构造期一次分配，消解路径零分配。
    /// </para>
    /// </summary>
    internal sealed class Match3Board
    {
        /// <summary>棋盘默认宽（列数）。</summary>
        internal const int DefaultWidth = 8;

        /// <summary>棋盘默认高（行数）。</summary>
        internal const int DefaultHeight = 8;

        /// <summary>颜色 ID 数量：合法格值为 <c>1..ColorCount</c>。</summary>
        internal const int ColorCount = 5;

        /// <summary>空格（已清除、待补格）的格值。</summary>
        internal const int Empty = 0;

        /// <summary>三连的判定长度。</summary>
        private const int MatchRunLength = 3;

        /// <summary>初始棋盘「生成后若有三连则重洗」的次数上限，超限抛异常。</summary>
        private const int MaxReshuffleAttempts = 100;

        /// <summary>格值数组，下标为 <c>y * Width + x</c>；0 表示空位。</summary>
        private readonly int[] _cells;

        /// <summary>三连标记位，与 <see cref="_cells"/> 同长，逐级消解时复用，不重新分配。</summary>
        private readonly bool[] _marks;

        /// <summary>候选颜色缓冲，长度 <see cref="ColorCount"/>，初始生成时按格挑选可用颜色。</summary>
        private readonly int[] _candidateColors;

        internal Match3Board(int width, int height)
        {
            if (width < MatchRunLength)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "列数不足以容纳一条三连。");
            }

            if (height < MatchRunLength)
            {
                throw new ArgumentOutOfRangeException(nameof(height), "行数不足以容纳一条三连。");
            }

            Width = width;
            Height = height;
            _cells = new int[width * height];
            _marks = new bool[width * height];
            _candidateColors = new int[ColorCount];
        }

        /// <summary>列数。</summary>
        internal int Width { get; }

        /// <summary>行数。</summary>
        internal int Height { get; }

        /// <summary>取格值，越界返回 <see cref="Empty"/>（不抛异常）。</summary>
        internal int Get(int x, int y)
        {
            if (!InBounds(x, y))
            {
                return Empty;
            }

            return _cells[(y * Width) + x];
        }

        /// <summary>坐标是否落在棋盘内。</summary>
        internal bool InBounds(int x, int y)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height;
        }

        /// <summary>两格是否四邻相接（曼哈顿距离恰为 1）。</summary>
        internal static bool AreAdjacent(int x1, int y1, int x2, int y2)
        {
            int dx = x1 > x2 ? x1 - x2 : x2 - x1;
            int dy = y1 > y2 ? y1 - y2 : y2 - y1;
            return dx + dy == 1;
        }

        /// <summary>交换两格颜色；调用方负责保证坐标已校验。</summary>
        internal void Swap(int x1, int y1, int x2, int y2)
        {
            int first = (y1 * Width) + x1;
            int second = (y2 * Width) + x2;
            int value = _cells[first];
            _cells[first] = _cells[second];
            _cells[second] = value;
        }

        /// <summary>
        /// 生成初始棋盘：逐格剔除会与左侧/下方两格构成三连的颜色，再校验整盘。
        /// <para>
        /// 为什么不用「整盘随机 + 有三连就重洗」的纯拒绝采样：8×8、5 色时随机整盘无三连的概率约 2%，
        /// 100 次重洗仍有约 10% 概率误抛 <see cref="InvalidOperationException"/>。
        /// 构造式填充让「生成即无三连」成为常态，100 次上限退化为兜底守卫（实测不可达）。
        /// </para>
        /// </summary>
        internal void Generate(ref Match3Random random)
        {
            for (int attempt = 0; attempt < MaxReshuffleAttempts; attempt++)
            {
                FillWithoutMatches(ref random);
                if (!HasMatch())
                {
                    return;
                }
            }

            throw new InvalidOperationException("初始棋盘重洗 100 次后仍有三连，随机源或颜色配置不可用。");
        }

        /// <summary>棋盘上是否存在任意横/纵三连（只判存在，早退，不写标记位）。</summary>
        internal bool HasMatch()
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x + MatchRunLength <= Width; x++)
                {
                    int color = _cells[(y * Width) + x];
                    if (color == Empty)
                    {
                        continue;
                    }

                    if (_cells[(y * Width) + x + 1] == color && _cells[(y * Width) + x + 2] == color)
                    {
                        return true;
                    }
                }
            }

            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y + MatchRunLength <= Height; y++)
                {
                    int color = _cells[(y * Width) + x];
                    if (color == Empty)
                    {
                        continue;
                    }

                    if (_cells[((y + 1) * Width) + x] == color && _cells[((y + 2) * Width) + x] == color)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 标记所有横向/纵向 <c>&gt;= 3</c> 连的格子（交叉处只计一次），返回被标记的格数。
        /// 标记结果留在 <see cref="_marks"/>，供 <see cref="ClearMarked"/> 消费。
        /// </summary>
        internal int MarkMatches()
        {
            for (int i = 0; i < _marks.Length; i++)
            {
                _marks[i] = false;
            }

            int marked = 0;

            for (int y = 0; y < Height; y++)
            {
                int runStart = 0;
                while (runStart < Width)
                {
                    int color = _cells[(y * Width) + runStart];
                    int runEnd = runStart + 1;
                    while (color != Empty && runEnd < Width && _cells[(y * Width) + runEnd] == color)
                    {
                        runEnd++;
                    }

                    if (color != Empty && runEnd - runStart >= MatchRunLength)
                    {
                        for (int x = runStart; x < runEnd; x++)
                        {
                            marked += MarkCell(x, y);
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
                    int color = _cells[(runStart * Width) + x];
                    int runEnd = runStart + 1;
                    while (color != Empty && runEnd < Height && _cells[(runEnd * Width) + x] == color)
                    {
                        runEnd++;
                    }

                    if (color != Empty && runEnd - runStart >= MatchRunLength)
                    {
                        for (int y = runStart; y < runEnd; y++)
                        {
                            marked += MarkCell(x, y);
                        }
                    }

                    runStart = runEnd;
                }
            }

            return marked;
        }

        /// <summary>把 <see cref="MarkMatches"/> 标记过的格子清成空位。</summary>
        internal void ClearMarked()
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                if (_marks[i])
                {
                    _cells[i] = Empty;
                    _marks[i] = false;
                }
            }
        }

        /// <summary>
        /// 列内重力 + 顶部补格：每列把非空格按原相对顺序压到底部，空位集中在顶部后用随机颜色填满。
        /// 幸存宝石的上下顺序不变，只整体下移。
        /// </summary>
        internal void CollapseAndRefill(ref Match3Random random)
        {
            for (int x = 0; x < Width; x++)
            {
                int write = 0;
                for (int read = 0; read < Height; read++)
                {
                    int index = (read * Width) + x;
                    int value = _cells[index];
                    if (value == Empty)
                    {
                        continue;
                    }

                    if (write != read)
                    {
                        _cells[(write * Width) + x] = value;
                        _cells[index] = Empty;
                    }

                    write++;
                }

                for (int y = write; y < Height; y++)
                {
                    _cells[(y * Width) + x] = random.NextColor(ColorCount);
                }
            }
        }

        /// <summary>整盘按「不与左两格 / 下两格同色」的约束填充，结果必然无三连。</summary>
        private void FillWithoutMatches(ref Match3Random random)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int candidateCount = CollectAllowedColors(x, y);
                    int pick = random.NextIndex(candidateCount);
                    _cells[(y * Width) + x] = _candidateColors[pick];
                }
            }
        }

        /// <summary>
        /// 收集 (x, y) 处不会立刻构成三连的候选颜色，返回数量。
        /// 最多被排除 2 种（横向 1 种 + 纵向 1 种），5 色时至少剩 3 种，缓冲不会越界。
        /// </summary>
        private int CollectAllowedColors(int x, int y)
        {
            int count = 0;
            for (int color = 1; color <= ColorCount; color++)
            {
                if (CompletesRun(x, y, color))
                {
                    continue;
                }

                _candidateColors[count] = color;
                count++;
            }

            return count;
        }

        /// <summary>在 (x, y) 放 <paramref name="color"/> 是否会与左边两格或下边两格连成三连。</summary>
        private bool CompletesRun(int x, int y, int color)
        {
            if (x >= MatchRunLength - 1
                && _cells[(y * Width) + x - 1] == color
                && _cells[(y * Width) + x - 2] == color)
            {
                return true;
            }

            if (y >= MatchRunLength - 1
                && _cells[((y - 1) * Width) + x] == color
                && _cells[((y - 2) * Width) + x] == color)
            {
                return true;
            }

            return false;
        }

        /// <summary>标记单格；已标记过则不重复计数（三连交叉点只算一格）。</summary>
        private int MarkCell(int x, int y)
        {
            int index = (y * Width) + x;
            if (_marks[index])
            {
                return 0;
            }

            _marks[index] = true;
            return 1;
        }
    }
}
