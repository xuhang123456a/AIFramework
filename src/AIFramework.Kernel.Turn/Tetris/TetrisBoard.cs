#nullable enable
namespace AIFramework.Kernel.Turn.Tetris
{
    /// <summary>
    /// 定长一维棋盘：<c>index = y * Width + x</c>，<c>0</c> 表示空格，否则存 <c>(int)kind + 1</c>
    /// （与 <c>ITetrisReadView.CellAt</c> 的约定一致，渲染层不需要再做一次枚举映射）。
    /// <para>
    /// <b>为什么一维定长数组</b>：规则 8 要求移动/旋转/硬降全程零分配，二维数组要么是交错数组
    /// （构造期 20 次分配），要么是 <c>int[,]</c>（每次索引都要做一次乘加边界检查）；一维数组既一次分配，
    /// 又让行搬移（消行）退化成一段连续的 <c>Array.Copy</c> 语义。
    /// </para>
    /// <para>
    /// <b>为什么不缓存 <c>FilledCellCount</c> / 堆高</b>：这两个值随时可以由棋盘本身推出，
    /// 缓存就多一份真相，任何一条写入路径漏更新都会让视图与棋盘不一致。棋盘只有 200 格，
    /// 按需扫描的成本远低于"状态不同步"的排查成本。
    /// </para>
    /// </summary>
    internal sealed class TetrisBoard
    {
        /// <summary>空格编码。</summary>
        private const int Empty = 0;

        private readonly int[] _cells;
        private readonly int _width;
        private readonly int _height;

        internal TetrisBoard(int width, int height)
        {
            _width = width;
            _height = height;
            _cells = new int[width * height];
        }

        /// <summary>棋盘列数。</summary>
        internal int Width
        {
            get { return _width; }
        }

        /// <summary>棋盘行数。</summary>
        internal int Height
        {
            get { return _height; }
        }

        /// <summary>已锁定格总数（不含正在下落的方块）。</summary>
        internal int FilledCellCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _cells.Length; i++)
                {
                    if (_cells[i] != Empty)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// 堆高换算出的"剩余空间"：从顶行往下数，第一个有方块的行号；整盘全空时返回 <see cref="Height"/>。
        /// 也就是说返回值 = 棋盘高度 - 堆高，可直接作为结算报告的 <c>RemainingUnits</c>。
        /// </summary>
        internal int TopFilledY
        {
            get
            {
                for (int y = 0; y < _height; y++)
                {
                    for (int x = 0; x < _width; x++)
                    {
                        if (_cells[(y * _width) + x] != Empty)
                        {
                            return y;
                        }
                    }
                }

                return _height;
            }
        }

        /// <summary>取 (x, y) 的格值；越界（含负数）返回 <c>-1</c>。</summary>
        internal int Get(int x, int y)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height)
            {
                return -1;
            }

            return _cells[(y * _width) + x];
        }

        /// <summary>(x, y) 是否不可落子：越界（含顶边之上）或已有方块。</summary>
        internal bool IsBlocked(int x, int y)
        {
            if (x < 0 || x >= _width || y < 0 || y >= _height)
            {
                return true;
            }

            return _cells[(y * _width) + x] != Empty;
        }

        /// <summary>
        /// 包围盒左上角放在 (<paramref name="originX"/>, <paramref name="originY"/>) 时，
        /// <paramref name="mask"/> 描述的方块是否与边界或已锁定格冲突。
        /// </summary>
        internal bool Collides(uint mask, int originX, int originY)
        {
            for (int cellY = 0; cellY < TetrisShapes.Box; cellY++)
            {
                for (int cellX = 0; cellX < TetrisShapes.Box; cellX++)
                {
                    if (!TetrisShapes.HasCell(mask, cellX, cellY))
                    {
                        continue;
                    }

                    if (IsBlocked(originX + cellX, originY + cellY))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>把方块烤进棋盘（消行前必须先落地）。</summary>
        internal void Lock(uint mask, int originX, int originY, int cellValue)
        {
            for (int cellY = 0; cellY < TetrisShapes.Box; cellY++)
            {
                for (int cellX = 0; cellX < TetrisShapes.Box; cellX++)
                {
                    if (!TetrisShapes.HasCell(mask, cellX, cellY))
                    {
                        continue;
                    }

                    _cells[((originY + cellY) * _width) + originX + cellX] = cellValue;
                }
            }
        }

        /// <summary>
        /// 消掉所有满行并让上方行整体下落，返回本次消掉的行数（0..4）。
        /// <para>
        /// 算法是自底向上的行压缩：<c>target</c> 指向"下一个存活行要落到的位置"，
        /// 满行跳过、非满行搬下来，最后把顶部剩余行清零。单趟即可，不需要反复重扫，
        /// 也不会出现"消完一行又凑出新满行"的级联（方块游戏本来也没有级联）。
        /// </para>
        /// </summary>
        internal int ClearFullRows()
        {
            int cleared = 0;
            int target = _height - 1;

            for (int y = _height - 1; y >= 0; y--)
            {
                if (IsRowFull(y))
                {
                    cleared++;
                    continue;
                }

                if (target != y)
                {
                    CopyRow(y, target);
                }

                target--;
            }

            for (int y = target; y >= 0; y--)
            {
                ClearRow(y);
            }

            return cleared;
        }

        /// <summary>整行是否填满。</summary>
        private bool IsRowFull(int y)
        {
            int rowStart = y * _width;
            for (int x = 0; x < _width; x++)
            {
                if (_cells[rowStart + x] == Empty)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>把第 <paramref name="sourceRow"/> 行整体写到第 <paramref name="targetRow"/> 行。</summary>
        private void CopyRow(int sourceRow, int targetRow)
        {
            int sourceStart = sourceRow * _width;
            int targetStart = targetRow * _width;

            for (int x = 0; x < _width; x++)
            {
                _cells[targetStart + x] = _cells[sourceStart + x];
            }
        }

        /// <summary>把一行清零（用于消行后腾出的顶部行）。</summary>
        private void ClearRow(int y)
        {
            int rowStart = y * _width;
            for (int x = 0; x < _width; x++)
            {
                _cells[rowStart + x] = Empty;
            }
        }
    }
}
