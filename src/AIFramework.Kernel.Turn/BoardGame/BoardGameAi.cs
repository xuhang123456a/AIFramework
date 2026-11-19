#nullable enable
using System;

namespace AIFramework.Kernel.Turn.BoardGame
{
    /// <summary>
    /// 白方 AI（三档难度共用一条入口）：<see cref="BoardGameDifficulty.Easy"/> 随机、
    /// <see cref="BoardGameDifficulty.Normal"/> 贪心最大翻转、
    /// <see cref="BoardGameDifficulty.Hard"/> 极小极大 <see cref="BoardGameRules.HardDepth"/> 层。
    /// <para>
    /// <b>搜索缓冲全部在构造期分配</b>：<c>_plyMoves</c> / <c>_plyCells</c> 各 10 层（每层 64 槽 / 64 字节），
    /// 递归里只做 <see cref="Array.Copy(byte[], byte[], int)"/> 与原地翻转，<b>零托管分配</b>。
    /// 层数按"4 层落子 + 每层至多一次跳过"留到 10 层，最后还有一道 <c>ply</c> 越界兜底（直接返回静态评估），
    /// 保证任何局面都不会越界写缓冲。
    /// </para>
    /// <para>
    /// <b>为什么用 negamax 而不是两套 max/min 分支</b>：评估函数以"轮到谁"为视角，
    /// <c>score = -Search(对手)</c> 一行顶掉两组镜像代码，也避免两套分支写反评估视角这类经典 bug。
    /// 带 alpha-beta 剪枝，但剪掉的都是不可能更优的分支，<b>结果与全量极小极大一致</b>。
    /// </para>
    /// <para>
    /// <b>同分口径</b>：贪心与极大极小都取"行主序扫描到的第一手"（严格大于才替换），
    /// 因此同局面同难度必定选出同一手，测试可以直接钉死坐标。
    /// </para>
    /// </summary>
    internal sealed class BoardGameAi
    {
        /// <summary>搜索缓冲层数：4 层落子 + 每层至多一次跳过 + 余量。</summary>
        private const int PlyCount = (BoardGameRules.HardDepth * 2) + 2;

        /// <summary>每层的合法手缓冲（行主序抄入，递归各用各的层，互不覆盖）。</summary>
        private readonly int[][] _plyMoves;

        /// <summary>每层的棋盘副本（父层棋盘只读，子层写自己的层）。</summary>
        private readonly byte[][] _plyCells;

        internal BoardGameAi()
        {
            _plyMoves = new int[PlyCount][];
            _plyCells = new byte[PlyCount][];

            for (int i = 0; i < PlyCount; i++)
            {
                _plyMoves[i] = new int[BoardGameRules.CellCount];
                _plyCells[i] = new byte[BoardGameRules.CellCount];
            }
        }

        /// <summary>
        /// 为 <paramref name="player"/> 选一手：返回 false 表示无合法落子（正常流程里当前玩家恒有棋可下，
        /// 这只是一条防御分支）。<paramref name="random"/> 只被 Easy 使用，其它难度不推进随机流。
        /// </summary>
        internal bool TryChooseMove(
            byte[] cells,
            int player,
            BoardGameDifficulty difficulty,
            ref BoardGameRandom random,
            out int x,
            out int y)
        {
            if (difficulty == BoardGameDifficulty.Easy)
            {
                return TryChooseRandom(cells, player, ref random, out x, out y);
            }

            if (difficulty == BoardGameDifficulty.Hard)
            {
                return TryChooseMinimax(cells, player, out x, out y);
            }

            return TryChooseGreedy(cells, player, out x, out y);
        }

        /// <summary>Easy：在合法点里等概率抽一个（随机流由种子决定，重开回到同一条流）。</summary>
        private bool TryChooseRandom(
            byte[] cells,
            int player,
            ref BoardGameRandom random,
            out int x,
            out int y)
        {
            x = -1;
            y = -1;

            int[] moves = _plyMoves[0];
            int moveCount = BoardGameRules.CollectLegalMoves(cells, player, moves);
            if (moveCount == 0)
            {
                return false;
            }

            int cell = moves[random.NextIndex(moveCount)];
            x = cell % BoardGameRules.Size;
            y = cell / BoardGameRules.Size;
            return true;
        }

        /// <summary>Normal：取翻转数最大的合法点；同分取扫描序第一手。</summary>
        private bool TryChooseGreedy(byte[] cells, int player, out int x, out int y)
        {
            x = -1;
            y = -1;

            int bestFlips = 0;

            for (int cy = 0; cy < BoardGameRules.Size; cy++)
            {
                for (int cx = 0; cx < BoardGameRules.Size; cx++)
                {
                    int flips = BoardGameRules.CountFlips(cells, cx, cy, player);
                    if (flips > bestFlips)
                    {
                        bestFlips = flips;
                        x = cx;
                        y = cy;
                    }
                }
            }

            return bestFlips > 0;
        }

        /// <summary>
        /// Hard：根层逐手试算 <c>-Search(子盘)</c>，取分值最高者；同分保留扫描序第一手。
        /// <para>根层落子写在 <c>_plyCells[0]</c>，搜索从 ply=1 开始，父子层缓冲天然不重叠。</para>
        /// </summary>
        private bool TryChooseMinimax(byte[] cells, int player, out int x, out int y)
        {
            x = -1;
            y = -1;

            int[] moves = _plyMoves[0];
            int moveCount = BoardGameRules.CollectLegalMoves(cells, player, moves);
            if (moveCount == 0)
            {
                return false;
            }

            int opponent = BoardGameRules.Opposite(player);
            byte[] rootCells = _plyCells[0];
            int alpha = -BoardGameRules.SearchInfinity;
            int bestScore = -BoardGameRules.SearchInfinity;
            int bestCell = -1;

            for (int i = 0; i < moveCount; i++)
            {
                int cell = moves[i];
                int moveX = cell % BoardGameRules.Size;
                int moveY = cell / BoardGameRules.Size;

                Array.Copy(cells, rootCells, BoardGameRules.CellCount);
                BoardGameRules.ApplyMove(rootCells, moveX, moveY, player);

                int score = -Search(
                    rootCells,
                    opponent,
                    BoardGameRules.HardDepth - 1,
                    -BoardGameRules.SearchInfinity,
                    -alpha,
                    1);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCell = cell;
                }

                if (bestScore > alpha)
                {
                    alpha = bestScore;
                }
            }

            if (bestCell < 0)
            {
                return false;
            }

            x = bestCell % BoardGameRules.Size;
            y = bestCell / BoardGameRules.Size;
            return true;
        }

        /// <summary>
        /// negamax 一层：<paramref name="player"/> 无手可下就跳过（不消耗深度），双方都无手即终局；
        /// 否则对每一手递归取负值，并在窗口外剪枝。
        /// </summary>
        private int Search(byte[] cells, int player, int depth, int alpha, int beta, int ply)
        {
            if (depth <= 0 || ply >= PlyCount)
            {
                return BoardGameRules.Evaluate(cells, player);
            }

            int[] moves = _plyMoves[ply];
            int moveCount = BoardGameRules.CollectLegalMoves(cells, player, moves);
            int opponent = BoardGameRules.Opposite(player);

            if (moveCount == 0)
            {
                if (!BoardGameRules.HasLegalMove(cells, opponent))
                {
                    return BoardGameRules.EvaluateTerminal(cells, player);
                }

                // 单方无手：换手重试，深度不变（跳过不是一手棋）。
                return Search(cells, opponent, depth, alpha, beta, ply + 1);
            }

            byte[] childCells = _plyCells[ply];
            int best = -BoardGameRules.SearchInfinity;

            for (int i = 0; i < moveCount; i++)
            {
                int cell = moves[i];
                int moveX = cell % BoardGameRules.Size;
                int moveY = cell / BoardGameRules.Size;

                Array.Copy(cells, childCells, BoardGameRules.CellCount);
                BoardGameRules.ApplyMove(childCells, moveX, moveY, player);

                int score = -Search(childCells, opponent, depth - 1, -beta, -alpha, ply + 1);
                if (score > best)
                {
                    best = score;
                }

                if (best > alpha)
                {
                    alpha = best;
                }

                if (alpha >= beta)
                {
                    break;
                }
            }

            return best;
        }
    }
}
