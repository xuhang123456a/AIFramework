#nullable enable
namespace AIFramework.Kernel.Turn.BoardGame
{
    /// <summary>
    /// 实盘棋盘：持有 64 格 <c>byte[]</c> 与黑白计数，落子一次同步更新计数。
    /// <para>
    /// <b>计数为什么增量维护</b>：<see cref="BlackCount"/> / <see cref="WhiteCount"/> 是 HUD 每帧都读的口径，
    /// 每次都全盘扫描（64 格）会让"落子后刷新 UI"变成隐性 O(64)；一次落子只翻转有限枚棋子，
    /// 增量维护既准又便宜。
    /// </para>
    /// <para>
    /// <b>与搜索盘共享规则</b>：合法性、翻转与走法生成全部委托 <see cref="BoardGameRules"/>，
    /// 实盘只额外维护计数，因此"AI 算出来的手"必定能在实盘落下去。
    /// </para>
    /// <para>构造期分配一次数组；<see cref="ResetToInitial"/> / <see cref="TryPlace"/> 全程零分配。</para>
    /// </summary>
    internal sealed class BoardGameBoard
    {
        /// <summary>64 格棋盘，行主序（<c>y * 8 + x</c>）。</summary>
        private readonly byte[] _cells;

        private int _blackCount;

        private int _whiteCount;

        internal BoardGameBoard()
        {
            _cells = new byte[BoardGameRules.CellCount];
            ResetToInitial();
        }

        /// <summary>内部棋盘数组（只读语义：AI 只读，落子一律走 <see cref="TryPlace"/>）。</summary>
        internal byte[] Cells
        {
            get { return _cells; }
        }

        /// <summary>黑子数。</summary>
        internal int BlackCount
        {
            get { return _blackCount; }
        }

        /// <summary>白子数。</summary>
        internal int WhiteCount
        {
            get { return _whiteCount; }
        }

        /// <summary>取 (x, y) 的格子；越界返回 <see cref="BoardGameRules.Empty"/>。</summary>
        internal byte GetCell(int x, int y)
        {
            if (!BoardGameRules.InBounds(x, y))
            {
                return BoardGameRules.Empty;
            }

            return _cells[BoardGameRules.Index(x, y)];
        }

        /// <summary>回到初始 4 子局面（黑 2 / 白 2），计数同步复位。</summary>
        internal void ResetToInitial()
        {
            BoardGameRules.ResetCells(_cells);
            _blackCount = 2;
            _whiteCount = 2;
        }

        /// <summary>
        /// 在 (x, y) 落 <paramref name="player"/> 子：非法（越界/占位/夹不住）返回 false 且不改棋盘与计数；
        /// 成功时写入棋盘、翻转全部被夹棋子、同步镜像计数，并通过 <paramref name="flipped"/> 回报翻转数。
        /// </summary>
        internal bool TryPlace(int x, int y, int player, out int flipped)
        {
            flipped = BoardGameRules.CountFlips(_cells, x, y, player);
            if (flipped <= 0)
            {
                return false;
            }

            BoardGameRules.ApplyMove(_cells, x, y, player);

            if (player == BoardGameRules.Black)
            {
                _blackCount += flipped + 1;
                _whiteCount -= flipped;
            }
            else
            {
                _whiteCount += flipped + 1;
                _blackCount -= flipped;
            }

            return true;
        }
    }
}
