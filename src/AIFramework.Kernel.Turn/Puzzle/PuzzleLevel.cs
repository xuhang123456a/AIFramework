#nullable enable
using System;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Puzzle
{
    /// <summary>
    /// 解谜关卡定义（不可变）：地形、初始箱位、玩家起点。
    /// <para>
    /// <b>关卡数据后续由配置表提供</b>：Phase 1 把 3 关硬编码在此（8×8 以内），
    /// 结构上只暴露"宽/高 + 地形数组 + 箱子数组 + 玩家起点"，后续换成 Luban 导出的只读关卡表时，
    /// <see cref="PuzzleWorld"/> 一行都不用改，只需把 <see cref="Create"/> 的取数源换掉。
    /// </para>
    /// <para>
    /// <b>文本格式</b>（每关一段等高字符串，<b>第一行是棋盘顶部</b>）：
    /// <c>#</c> 墙、空格 地面、<c>.</c> 目标点、<c>$</c> 箱子、<c>@</c> 玩家、<c>*</c> 已在目标点上的箱子、
    /// <c>+</c> 站在目标点上的玩家。解析只发生在构造期，运行期不读字符。
    /// </para>
    /// <para>
    /// <b>为什么用文本而不是 <c>int[]</c></b>：关卡布局的可读性直接决定改关卡的出错率；
    /// 三维数组式的 <c>PuzzleCell[]</c> 字面量无法一眼看出"哪格是墙"，而这里的每关就是一张图。
    /// 解析成本只发生在 <see cref="PuzzleWorld"/> 构造期一次，与帧内零分配口径无关。
    /// </para>
    /// </summary>
    internal sealed class PuzzleLevel
    {
        /// <summary>墙字符。</summary>
        private const char WallChar = '#';

        /// <summary>地面字符。</summary>
        private const char FloorChar = ' ';

        /// <summary>目标点字符。</summary>
        private const char GoalChar = '.';

        /// <summary>箱子字符（落在普通地面上）。</summary>
        private const char BoxChar = '$';

        /// <summary>玩家字符（站在普通地面上）。</summary>
        private const char PlayerChar = '@';

        /// <summary>已在目标点上的箱子字符。</summary>
        private const char BoxOnGoalChar = '*';

        /// <summary>站在目标点上的玩家字符。</summary>
        private const char PlayerOnGoalChar = '+';

        /// <summary>单边最大尺寸（契约要求 8×8 以内）。</summary>
        internal const int MaxSide = 8;

        /// <summary>内置关卡数量。</summary>
        internal const int LevelCount = 3;

        // 文本关卡按"第一行在顶部"书写；解析时翻成 y=0 为底行。

        /// <summary>第 1 关（7×5）：直线推两步，教学关；玩家左手边是墙，用于验证"撞墙不计数"。</summary>
        private static readonly string[] Level1Rows =
        {
            "#######",
            "#  #  #",
            "#@$  .#",
            "#   # #",
            "#######"
        };

        /// <summary>第 2 关（7×7）：需要绕到箱子另一侧再推；箱子正上方是墙，直接上推会被拒。</summary>
        private static readonly string[] Level2Rows =
        {
            "#######",
            "#     #",
            "# .   #",
            "#  # ##",
            "# @$  #",
            "#     #",
            "#######"
        };

        /// <summary>第 3 关（8×8）：两个箱子两个目标点，中间一堵墙把下半场切成两条通道。</summary>
        private static readonly string[] Level3Rows =
        {
            "########",
            "#      #",
            "# .  . #",
            "# $  $ #",
            "#      #",
            "#  ##  #",
            "#   @  #",
            "########"
        };

        private readonly PuzzleCell[] _cells;

        private readonly bool[] _boxes;

        /// <summary>解析后构造，禁止外部直接 new（关卡数据只在 <see cref="Create"/> 里取）。</summary>
        private PuzzleLevel(
            int levelId,
            int width,
            int height,
            PuzzleCell[] cells,
            bool[] boxes,
            int playerX,
            int playerY,
            int boxCount,
            int boxesOnGoal)
        {
            LevelId = levelId;
            Width = width;
            Height = height;
            _cells = cells;
            _boxes = boxes;
            PlayerX = playerX;
            PlayerY = playerY;
            BoxCount = boxCount;
            BoxesOnGoal = boxesOnGoal;
        }

        /// <summary>关卡 ID（1..3）。</summary>
        internal int LevelId { get; }

        /// <summary>关卡宽。</summary>
        internal int Width { get; }

        /// <summary>关卡高。</summary>
        internal int Height { get; }

        /// <summary>玩家起点的 x。</summary>
        internal int PlayerX { get; }

        /// <summary>玩家起点的 y。</summary>
        internal int PlayerY { get; }

        /// <summary>箱子总数。</summary>
        internal int BoxCount { get; }

        /// <summary>初始就落在目标点上的箱子数（内置关卡为 0，留着是为了配置表关卡能带 <c>*</c>）。</summary>
        internal int BoxesOnGoal { get; }

        /// <summary>
        /// 按关卡 ID 取定义。调用方（<see cref="PuzzleWorld"/>）已把 ID 夹到 <c>1..3</c>；
        /// 这里仍然以 <c>default</c> 兜回第 1 关，避免将来配置表缺行时炸在构造期之外。
        /// </summary>
        internal static PuzzleLevel Create(int levelId)
        {
            switch (levelId)
            {
                case 2:
                    return Parse(2, Level2Rows);
                case 3:
                    return Parse(3, Level3Rows);
                default:
                    return Parse(1, Level1Rows);
            }
        }

        /// <summary>
        /// 从外部行数组建关（行序顶部→底部），供配置表/编辑器导出的关卡数据使用。
        /// <para>
        /// <b>为什么必须有这个入口</b>：编辑器和配置表产出的关卡如果不能进内核，所谓"数据驱动关卡"
        /// 就只是把 JSON 读出来打印一遍。这里把外部行送进与内置关卡**完全同一条**解析路径，
        /// 避免"内置关卡一套解析、外部关卡另一套解析"——两套解析迟早对字符语义产生分歧。
        /// </para>
        /// </summary>
        /// <param name="levelId">关卡 id（仅诊断用，不参与夹取）。</param>
        /// <param name="rows">行数组，第一行是棋盘顶部；字符语义与内置关卡一致。</param>
        /// <param name="sourceName">数据来源名（报错定位用，例如 <c>puzzle-demo.json</c>）。</param>
        /// <returns>解析后的不可变关卡定义。</returns>
        /// <exception cref="ArgumentException">行数据非法（空/尺寸越界/宽度不一致/未知字符/玩家数不为 1/缺箱子或目标点）。</exception>
        internal static PuzzleLevel CreateFromRows(int levelId, string[] rows, string sourceName)
        {
            if (rows == null)
            {
                throw new ArgumentNullException(nameof(rows));
            }

            string source = string.IsNullOrEmpty(sourceName) ? "外部关卡" : sourceName;
            try
            {
                return Parse(levelId, rows);
            }
            catch (InvalidOperationException exception)
            {
                throw new ArgumentException(source + "：" + exception.Message, nameof(rows));
            }
        }
        /// <summary>把地形与初始箱位拷进调用方的定长数组（<see cref="PuzzleWorld"/> 的 <c>Reset</c> 复用）。</summary>
        internal void CopyTo(PuzzleCell[] cells, bool[] boxes)
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                cells[i] = _cells[i];
                boxes[i] = _boxes[i];
            }
        }

        /// <summary>解析一关文本地图；数据写错（行宽不一致、缺玩家、无箱子/无目标点）抛 <see cref="InvalidOperationException"/>。</summary>
        private static PuzzleLevel Parse(int levelId, string[] rows)
        {
            if (rows.Length == 0)
            {
                throw new InvalidOperationException("解谜关卡数据为空。");
            }

            int height = rows.Length;
            int width = rows[0].Length;

            if (width <= 0 || width > MaxSide || height > MaxSide)
            {
                throw new InvalidOperationException("解谜关卡尺寸必须落在 1..8（宽高各自）。");
            }

            PuzzleCell[] cells = new PuzzleCell[width * height];
            bool[] boxes = new bool[width * height];

            int playerX = -1;
            int playerY = -1;
            int playerCount = 0;
            int boxCount = 0;
            int boxesOnGoal = 0;
            int goalCount = 0;

            for (int row = 0; row < height; row++)
            {
                string line = rows[row];
                if (line.Length != width)
                {
                    throw new InvalidOperationException("解谜关卡数据各行宽度必须一致。");
                }

                int y = height - 1 - row;
                for (int x = 0; x < width; x++)
                {
                    int index = (y * width) + x;
                    switch (line[x])
                    {
                        case WallChar:
                            cells[index] = PuzzleCell.Wall;
                            break;
                        case FloorChar:
                            cells[index] = PuzzleCell.Floor;
                            break;
                        case GoalChar:
                            cells[index] = PuzzleCell.Goal;
                            goalCount++;
                            break;
                        case BoxChar:
                            cells[index] = PuzzleCell.Floor;
                            boxes[index] = true;
                            boxCount++;
                            break;
                        case BoxOnGoalChar:
                            cells[index] = PuzzleCell.Goal;
                            boxes[index] = true;
                            boxCount++;
                            boxesOnGoal++;
                            goalCount++;
                            break;
                        case PlayerChar:
                            cells[index] = PuzzleCell.Floor;
                            playerX = x;
                            playerY = y;
                            playerCount++;
                            break;
                        case PlayerOnGoalChar:
                            cells[index] = PuzzleCell.Goal;
                            goalCount++;
                            playerX = x;
                            playerY = y;
                            playerCount++;
                            break;
                        default:
                            throw new InvalidOperationException("解谜关卡数据出现未知字符。");
                    }
                }
            }

            if (playerCount != 1)
            {
                throw new InvalidOperationException("解谜关卡必须且只能有一个玩家起点。");
            }

            if (boxCount < 1 || goalCount < 1)
            {
                throw new InvalidOperationException("解谜关卡至少要有 1 个箱子和 1 个目标点。");
            }

            return new PuzzleLevel(levelId, width, height, cells, boxes, playerX, playerY, boxCount, boxesOnGoal);
        }
    }
}
