using System;

namespace AIFramework.Features.Merge
{
    /// <summary>
    /// 一次棋盘查询的只读快照：行优先等级数组的防御性副本。
    /// <para>
    /// 快照一旦建成就与处理器彻底解耦：查询之后棋盘再怎么合并/生成，都不会改变已发出的快照，
    /// 避免 UI 拿着引用读到"查询之后才发生"的状态。
    /// </para>
    /// </summary>
    public sealed class MergeGridSnapshot
    {
        private readonly int[] _levels;
        private readonly int _usedCellCount;

        public MergeGridSnapshot(int width, int height, int[] levels)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "棋盘宽度必须为正。");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "棋盘高度必须为正。");
            }

            if (levels == null)
            {
                throw new ArgumentNullException(nameof(levels));
            }

            if ((long)width * height != levels.Length)
            {
                throw new ArgumentException(
                    "棋盘等级数组长度 " + levels.Length + " 与 " + width + "×" + height + " 不符。",
                    nameof(levels));
            }

            Width = width;
            Height = height;

            // 复制而不是持有：调用方的数组之后被改了也不能污染快照。
            _levels = new int[levels.Length];
            Array.Copy(levels, _levels, levels.Length);

            int used = 0;
            for (int i = 0; i < _levels.Length; i++)
            {
                if (_levels[i] != 0)
                {
                    used++;
                }
            }

            _usedCellCount = used;
        }

        /// <summary>棋盘宽度（列数）。</summary>
        public int Width { get; }

        /// <summary>棋盘高度（行数）。</summary>
        public int Height { get; }

        /// <summary>取 <c>(x, y)</c> 的等级，0 = 空；越界返回 -1（越界不是"空"，必须与 0 区分）。</summary>
        public int LevelAt(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return -1;
            }

            return _levels[(y * Width) + x];
        }

        /// <summary>已占用的格子数（等级 ≠ 0 的格子）。</summary>
        public int UsedCellCount
        {
            get { return _usedCellCount; }
        }
    }
}
