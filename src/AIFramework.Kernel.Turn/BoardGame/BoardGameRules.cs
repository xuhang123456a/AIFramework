#nullable enable
namespace AIFramework.Kernel.Turn.BoardGame
{
    /// <summary>
    /// 黑白棋规则数学：全部是作用在 <c>byte[]</c> 定长棋盘（<see cref="CellCount"/> 格、行主序 <c>y * Size + x</c>）
    /// 上的纯函数，不持有状态、不分配内存。
    /// <para>
    /// <b>为什么把规则抽成静态纯函数</b>：实盘（<see cref="BoardGameBoard"/>）与搜索盘的走法生成必须<b>逐位一致</b>，
    /// 否则 AI 会算出实盘走不出来的手。两者共用同一套 <see cref="CountFlips"/> / <see cref="ApplyMove"/>，
    /// 差异只剩"谁维护计数"。
    /// </para>
    /// <para>
    /// <b>合法性口径</b>：空格 + 八个方向上至少夹住一枚对方棋子、且该方向末端是本方棋子（见 <see cref="CountFlips"/>），
    /// 翻转数即所有被夹棋子数之和；<see cref="ApplyMove"/> 只负责落子与翻转，<b>不判合法性</b>——调用方必须先问
    /// <see cref="CountFlips"/>。
    /// </para>
    /// <para>
    /// <b>评估函数</b>：角点 +30、角邻（X 方格与 C 方格，共 12 格）-12、其余 0（见 <see cref="CellWeight"/>）。
    /// 其余格刻意不计权重：若按子数计 1 分，"多翻 30 子"就会压过"占一个角"，<see cref="HardDepth"/> 层搜索会退化成贪心，
    /// 与 <see cref="BoardGameDifficulty.Hard"/> 的语义冲突。终局另加 <see cref="TerminalWinScore"/> 的胜负奖励。
    /// </para>
    /// </summary>
    internal static class BoardGameRules
    {
        /// <summary>棋盘边长（8）。</summary>
        internal const int Size = 8;

        /// <summary>棋盘格数（64）。</summary>
        internal const int CellCount = Size * Size;

        /// <summary>空格编码。</summary>
        internal const int Empty = 0;

        /// <summary>黑方编码（本地玩家，先手）。</summary>
        internal const int Black = 1;

        /// <summary>白方编码（AI）。</summary>
        internal const int White = 2;

        /// <summary>平局/未结束的 <c>Winner</c> 值。</summary>
        internal const int Draw = 0;

        /// <summary>方向数：横、竖、两条对角线各正反两向。</summary>
        internal const int DirectionCount = 8;

        /// <summary>角点位置权重（占住角 = 该子永远翻不掉）。</summary>
        internal const int CornerWeight = 30;

        /// <summary>角邻位置权重（X 方格 / C 方格：占上去等于把角送给对手）。</summary>
        internal const int CornerAdjacentWeight = -12;

        /// <summary>Hard 搜索层数（含根层那一手：AI → 对手 → AI → 对手，然后评估）。</summary>
        internal const int HardDepth = 4;

        /// <summary>终局胜负奖励：远大于任何静态评估绝对值，保证"能赢就不会去赚角"。</summary>
        internal const int TerminalWinScore = 100000;

        /// <summary>搜索用的负无穷/正无穷哨兵（<c>int.MinValue</c> 取负会溢出，不能用它）。</summary>
        internal const int SearchInfinity = 1000000;

        /// <summary>胜利战利品模板 ID。</summary>
        internal const int VictoryLootItemId = 1003;

        /// <summary>胜利战利品数量。</summary>
        internal const int VictoryLootCount = 1;

        /// <summary>八个方向的 x 增量（类型初始化时分配一次，运行期只读）。</summary>
        private static readonly int[] DirectionX = { 1, -1, 0, 0, 1, 1, -1, -1 };

        /// <summary>八个方向的 y 增量，与 <see cref="DirectionX"/> 一一对应。</summary>
        private static readonly int[] DirectionY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>行主序索引：<c>y * Size + x</c>。</summary>
        internal static int Index(int x, int y)
        {
            return (y * Size) + x;
        }

        /// <summary>(x, y) 是否在 8×8 盘内。</summary>
        internal static bool InBounds(int x, int y)
        {
            return x >= 0 && x < Size && y >= 0 && y < Size;
        }

        /// <summary>对手编码：黑 ↔ 白。</summary>
        internal static int Opposite(int player)
        {
            return player == Black ? White : Black;
        }

        /// <summary>把棋盘清成标准初始局面：中央 4 格黑白各 2（黑 (3,4)/(4,3)、白 (3,3)/(4,4)）。</summary>
        internal static void ResetCells(byte[] cells)
        {
            for (int i = 0; i < CellCount; i++)
            {
                cells[i] = Empty;
            }

            cells[Index(3, 3)] = White;
            cells[Index(4, 4)] = White;
            cells[Index(3, 4)] = Black;
            cells[Index(4, 3)] = Black;
        }

        /// <summary>
        /// 在 (x, y) 落 <paramref name="player"/> 子能翻转的棋子数；<b>0 表示非法</b>
        /// （越界、非空格、八个方向都夹不住对方棋子）。
        /// </summary>
        internal static int CountFlips(byte[] cells, int x, int y, int player)
        {
            if (!InBounds(x, y))
            {
                return 0;
            }

            if (cells[Index(x, y)] != Empty)
            {
                return 0;
            }

            int opponent = Opposite(player);
            int total = 0;

            for (int direction = 0; direction < DirectionCount; direction++)
            {
                int dx = DirectionX[direction];
                int dy = DirectionY[direction];
                int cx = x + dx;
                int cy = y + dy;
                int run = 0;

                while (InBounds(cx, cy) && cells[Index(cx, cy)] == opponent)
                {
                    run++;
                    cx += dx;
                    cy += dy;
                }

                if (run > 0 && InBounds(cx, cy) && cells[Index(cx, cy)] == player)
                {
                    total += run;
                }
            }

            return total;
        }

        /// <summary>
        /// 落子并翻转：写入 (x, y) 为己方子，再把八个方向所有被夹住的对方棋子翻成己方色。
        /// 返回翻转数（不含新落的这一手）。<b>不判合法性</b>，调用方须先用 <see cref="CountFlips"/> 确认。
        /// </summary>
        internal static int ApplyMove(byte[] cells, int x, int y, int player)
        {
            int opponent = Opposite(player);
            int total = 0;

            cells[Index(x, y)] = (byte)player;

            for (int direction = 0; direction < DirectionCount; direction++)
            {
                int dx = DirectionX[direction];
                int dy = DirectionY[direction];
                int cx = x + dx;
                int cy = y + dy;
                int run = 0;

                while (InBounds(cx, cy) && cells[Index(cx, cy)] == opponent)
                {
                    run++;
                    cx += dx;
                    cy += dy;
                }

                if (run == 0 || !InBounds(cx, cy) || cells[Index(cx, cy)] != player)
                {
                    continue;
                }

                for (int step = 1; step <= run; step++)
                {
                    cells[Index(x + (dx * step), y + (dy * step))] = (byte)player;
                }

                total += run;
            }

            return total;
        }

        /// <summary><paramref name="player"/> 在 (x, y) 是否有合法落子。</summary>
        internal static bool IsLegalMove(byte[] cells, int x, int y, int player)
        {
            return CountFlips(cells, x, y, player) > 0;
        }

        /// <summary><paramref name="player"/> 是否还有任何合法落子（行主序短路扫描）。</summary>
        internal static bool HasLegalMove(byte[] cells, int player)
        {
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (IsLegalMove(cells, x, y, player))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 把所有合法落子按<b>行主序</b>（先 y 后 x）抄进 <paramref name="buffer"/>，返回个数。
        /// <paramref name="buffer"/> 至少要有 <see cref="CellCount"/> 个槽位；AI 与测试都按这个顺序取"扫描序第一个"。
        /// </summary>
        internal static int CollectLegalMoves(byte[] cells, int player, int[] buffer)
        {
            int count = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (IsLegalMove(cells, x, y, player))
                    {
                        buffer[count] = Index(x, y);
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>盘面上 <paramref name="player"/> 的子数（全盘扫描，只给评估与断言用）。</summary>
        internal static int CountCells(byte[] cells, int player)
        {
            int count = 0;
            for (int i = 0; i < CellCount; i++)
            {
                if (cells[i] == player)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 位置权重：4 个角 +30；与角相邻的 12 个 X/C 方格 -12；其余 0。
        /// <para>角判定只看两条边界，角邻判定是"两轴都落在边界 2 格内且本身不是角"——恰好 16 - 4 = 12 格。</para>
        /// </summary>
        internal static int CellWeight(int x, int y)
        {
            bool nearX = x <= 1 || x >= Size - 2;
            bool nearY = y <= 1 || y >= Size - 2;

            if (!nearX || !nearY)
            {
                return 0;
            }

            bool corner = (x == 0 || x == Size - 1) && (y == 0 || y == Size - 1);
            return corner ? CornerWeight : CornerAdjacentWeight;
        }

        /// <summary>
        /// 静态评估（站在 <paramref name="player"/> 视角）：己方位置权重和 - 对方位置权重和。
        /// 只遍历 64 格一次，不分配。
        /// </summary>
        internal static int Evaluate(byte[] cells, int player)
        {
            int opponent = Opposite(player);
            int score = 0;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int cell = cells[Index(x, y)];
                    if (cell == Empty)
                    {
                        continue;
                    }

                    int weight = CellWeight(x, y);
                    if (cell == player)
                    {
                        score += weight;
                    }
                    else if (cell == opponent)
                    {
                        score -= weight;
                    }
                }
            }

            return score;
        }

        /// <summary>
        /// 终局评估：子数多者拿到 <see cref="TerminalWinScore"/> 奖励（差值再计入），平局为 0。
        /// 子数差是最终胜负的唯一口径，角点权重在终局没有任何意义。
        /// </summary>
        internal static int EvaluateTerminal(byte[] cells, int player)
        {
            int own = CountCells(cells, player);
            int opponent = CountCells(cells, Opposite(player));

            if (own > opponent)
            {
                return TerminalWinScore + (own - opponent);
            }

            if (own < opponent)
            {
                return -TerminalWinScore - (opponent - own);
            }

            return 0;
        }
    }
}
