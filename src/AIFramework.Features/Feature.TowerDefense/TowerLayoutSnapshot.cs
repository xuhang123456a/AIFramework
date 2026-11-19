#nullable enable
using System;

namespace AIFramework.Features.TowerDefense
{
    /// <summary>
    /// 塔布局快照：把"哪格有哪座塔"一次拷出来交给 UI/适配层。
    /// <para>
    /// <b>为什么返回快照而不是把内部数组直接暴露</b>：内部数组是切片的可变状态，
    /// 一旦外部持有引用，UI 就能绕过命令改棋盘，扣款与落子立刻可能不同步。
    /// 快照在构造时**复制**，之后切片内部怎么变都与已发出的快照无关。
    /// </para>
    /// <para>允许分配：查询只在"建造完成 / 开局布场 / UI 刷新"这类非帧热路径上调用。</para>
    /// </summary>
    public sealed class TowerLayoutSnapshot
    {
        private readonly int[] _towerIds;

        private readonly int _towerCount;

        /// <summary>以行优先的塔 ID 数组构造快照（0 表示空格）。</summary>
        /// <param name="width">棋盘列数。</param>
        /// <param name="height">棋盘行数。</param>
        /// <param name="towerIds">行优先塔 ID 数组，长度应为 <paramref name="width"/> × <paramref name="height"/>。</param>
        /// <exception cref="ArgumentNullException"><paramref name="towerIds"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException">宽或高为负数。</exception>
        public TowerLayoutSnapshot(int width, int height, int[] towerIds)
        {
            if (towerIds == null)
            {
                throw new ArgumentNullException(nameof(towerIds));
            }

            if (width < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "棋盘列数不能为负。");
            }

            if (height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "棋盘行数不能为负。");
            }

            Width = width;
            Height = height;
            _towerIds = new int[towerIds.Length];
            Array.Copy(towerIds, _towerIds, towerIds.Length);

            int count = 0;
            for (int i = 0; i < _towerIds.Length; i++)
            {
                if (_towerIds[i] != 0)
                {
                    count++;
                }
            }

            _towerCount = count;
        }

        /// <summary>棋盘列数。</summary>
        public int Width { get; }

        /// <summary>棋盘行数。</summary>
        public int Height { get; }

        /// <summary>已建成的塔数量（空格不计）。</summary>
        public int TowerCount
        {
            get { return _towerCount; }
        }

        /// <summary>
        /// 取第 (<paramref name="x"/>, <paramref name="y"/>) 格的塔 ID；空格与越界一律返回 0。
        /// <b>越界返回 0 而不是抛异常</b>：这是给渲染循环用的读取口，
        /// 尺寸不对称时 UI 应表现成"空格"而不是每帧抛一次异常。
        /// </summary>
        public int TowerIdAt(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return 0;
            }

            int index = (y * Width) + x;
            return index >= 0 && index < _towerIds.Length ? _towerIds[index] : 0;
        }
    }
}
