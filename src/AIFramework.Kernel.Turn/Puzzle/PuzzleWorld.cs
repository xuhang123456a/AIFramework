#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Puzzle
{
    /// <summary>
    /// 解谜沙盒（推箱子，<see cref="GameplayKernelType.Turn"/>）：Kernel.Turn 的第五套内容，
    /// 验证"零引擎依赖的纯数据网格解谜"也能挂进同一套回合进出门。
    /// <para>
    /// <b>为什么复用 Turn 卡带</b>：推箱子对主板而言只是"另一个回合制会话"——进门拿
    /// <see cref="GameplayInitData.LevelId"/> 选关卡，出门交 <see cref="GameplayResult"/>；
    /// 新增 <c>GameplayKernelType</c> 枚举值会逼装配层与存档协议同步扩容，收益为零。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：内置 3 关（8×8 以内，见 <see cref="PuzzleLevel"/>，
    /// <b>关卡数据后续由配置表提供</b>）；<see cref="GameplayInitData.LevelId"/> 取 <c>1/2/3</c>，
    /// 其它值一律夹到 1 且 <see cref="LevelId"/> 回报夹取后的值。
    /// <see cref="TryMove"/> 按 0=上 1=右 2=下 3=左 推进：前方越界/墙/虚空 → false 且不动任何计数；
    /// 前方是箱子时要求箱子后方是 <see cref="PuzzleCell.Floor"/> 或 <see cref="PuzzleCell.Goal"/>，
    /// 否则 false（同样不消耗步数）；推成功则箱子与玩家同时前移、<see cref="Moves"/> +1。
    /// </para>
    /// <para>
    /// <b>无失败态</b>：<see cref="IsFinished"/> 恒等于 <see cref="IsSolved"/>（所有箱子都在目标点上），
    /// 不存在步数上限、血量或超时判负——走死了就 <see cref="Reset"/> 从头再来。
    /// 解出后玩家输入被冻结（返回 false），否则"已通关"会因再推走一个箱子而翻回未解出，
    /// 与 <see cref="IsFinished"/> 的单调语义冲突；重试必须先 <see cref="Reset"/>。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：没有待播的动画队列，<see cref="Step(float)"/> 只累加 <see cref="StepCount"/>
    /// （<c>deltaSeconds</c> 被忽略），解出后为空操作，且 <see cref="Reset"/> 不清零（属于会话统计）。
    /// </para>
    /// <para>
    /// <b>零分配</b>：地形与箱位两个数组在构造期按关卡尺寸各分配一次，
    /// <see cref="TryMove"/> / <see cref="Reset"/> / <see cref="Step(float)"/> 不申请托管堆内存；
    /// 只有 <see cref="CollectResult"/> 按结果分配一次战利品数组（<see cref="GameplayResult"/> 契约要求一次构造）。
    /// </para>
    /// </summary>
    public sealed class PuzzleWorld : IGameplaySession, IPuzzleReadView, IPuzzleInput
    {
        /// <summary>方向：上。</summary>
        private const int DirectionUp = 0;

        /// <summary>方向：右。</summary>
        private const int DirectionRight = 1;

        /// <summary>方向：下。</summary>
        private const int DirectionDown = 2;

        /// <summary>方向：左。</summary>
        private const int DirectionLeft = 3;

        /// <summary>通关战利品模板 ID。</summary>
        private const int VictoryLootItemId = 1003;

        /// <summary>通关战利品数量。</summary>
        private const int VictoryLootCount = 1;

        /// <summary>关卡定义（不可变，<see cref="Reset"/> 的唯一还原来源）。</summary>
        private readonly PuzzleLevel _level;

        /// <summary>地形层：<c>y * Width + x</c>，构造期分配，<see cref="Reset"/> 整表覆写。</summary>
        private readonly PuzzleCell[] _cells;

        /// <summary>箱子层：<c>y * Width + x</c>，构造期分配，推箱时只改两格。</summary>
        private readonly bool[] _boxes;

        private int _playerX;

        private int _playerY;

        private int _moves;

        private int _boxesOnGoal;

        private int _stepCount;

        private bool _resultCollected;

        public PuzzleWorld(in GameplayInitData init)
        {
            int levelId = init.LevelId;
            if (levelId < 1 || levelId > PuzzleLevel.LevelCount)
            {
                levelId = 1;
            }

            _level = PuzzleLevel.Create(levelId);
            _cells = new PuzzleCell[_level.Width * _level.Height];
            _boxes = new bool[_level.Width * _level.Height];

            _playerX = 0;
            _playerY = 0;
            _moves = 0;
            _boxesOnGoal = 0;
            _stepCount = 0;
            _resultCollected = false;

            LoadInitialState();
        }

        /// <summary>关卡宽（列数）。</summary>
        public int Width
        {
            get { return _level.Width; }
        }

        /// <summary>关卡高（行数，<c>y = 0</c> 为底行）。</summary>
        public int Height
        {
            get { return _level.Height; }
        }

        /// <summary>夹取后的关卡 ID（1..3）。</summary>
        public int LevelId
        {
            get { return _level.LevelId; }
        }

        /// <summary>累计推演步数（<see cref="Step(float)"/> 的有效调用次数，解出后不再增长）。</summary>
        public int StepCount
        {
            get { return _stepCount; }
        }

        /// <summary>已走步数：只统计成功的移动/推箱，被拒的输入不计数。</summary>
        public int Moves
        {
            get { return _moves; }
        }

        /// <summary>当前停在目标点上的箱子数。</summary>
        public int BoxesOnGoal
        {
            get { return _boxesOnGoal; }
        }

        /// <summary>本关箱子总数。</summary>
        public int BoxCount
        {
            get { return _level.BoxCount; }
        }

        /// <summary>是否所有箱子都在目标点上。</summary>
        public bool IsSolved
        {
            get { return _boxesOnGoal == _level.BoxCount; }
        }

        /// <summary>对局是否结束。解谜没有失败态，因此恒等于 <see cref="IsSolved"/>。</summary>
        public bool IsFinished
        {
            get { return IsSolved; }
        }

        /// <summary>玩家所在列。</summary>
        public int PlayerX
        {
            get { return _playerX; }
        }

        /// <summary>玩家所在行。</summary>
        public int PlayerY
        {
            get { return _playerY; }
        }

        /// <summary>
        /// 取 (x, y) 的地形。越界返回 <see cref="PuzzleCell.Wall"/>；
        /// 箱子是否存在要另外问 <see cref="HasBox"/>（压在目标点上的箱子不改变地形）。
        /// </summary>
        public PuzzleCell GetCell(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return PuzzleCell.Wall;
            }

            return _cells[(y * Width) + x];
        }

        /// <summary>(x, y) 是否有箱子；越界返回 false。</summary>
        public bool HasBox(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height)
            {
                return false;
            }

            return _boxes[(y * Width) + x];
        }

        /// <summary>
        /// 朝 <paramref name="direction"/> 推一步：0=上 1=右 2=下 3=左。
        /// 非法方向、越界、撞墙、箱子后方被挡、已解出，一律返回 false 且 <b>不消耗步数</b>；
        /// 成功（含推箱）才 <see cref="Moves"/> +1。
        /// </summary>
        public bool TryMove(int direction)
        {
            if (IsSolved)
            {
                return false;
            }

            int dx;
            int dy;
            switch (direction)
            {
                case DirectionUp:
                    dx = 0;
                    dy = 1;
                    break;
                case DirectionRight:
                    dx = 1;
                    dy = 0;
                    break;
                case DirectionDown:
                    dx = 0;
                    dy = -1;
                    break;
                case DirectionLeft:
                    dx = -1;
                    dy = 0;
                    break;
                default:
                    return false;
            }

            int targetX = _playerX + dx;
            int targetY = _playerY + dy;

            if (!IsWalkable(GetCell(targetX, targetY)))
            {
                return false;
            }

            if (HasBox(targetX, targetY))
            {
                int behindX = targetX + dx;
                int behindY = targetY + dy;

                if (!IsWalkable(GetCell(behindX, behindY)))
                {
                    return false;
                }

                // 箱子离开的格子若是目标点，归位数先减；箱子进入的格子若是目标点，再增。
                if (GetCell(targetX, targetY) == PuzzleCell.Goal)
                {
                    _boxesOnGoal--;
                }

                if (GetCell(behindX, behindY) == PuzzleCell.Goal)
                {
                    _boxesOnGoal++;
                }

                _boxes[(targetY * Width) + targetX] = false;
                _boxes[(behindY * Width) + behindX] = true;
            }

            _playerX = targetX;
            _playerY = targetY;
            _moves++;
            return true;
        }

        /// <summary>
        /// 回到初始局面：地形与箱位整表还原、玩家回起点、<see cref="Moves"/> 归零、<see cref="IsSolved"/> 变 false。
        /// <see cref="StepCount"/> 不清零（会话统计）；结算报告已取回后为空操作（会话所有权已交还）。
        /// </summary>
        public void Reset()
        {
            if (_resultCollected)
            {
                return;
            }

            LoadInitialState();
        }

        /// <summary>
        /// 推进一个逻辑步：只累加 <see cref="StepCount"/>（供 UI 播动画与统计），<c>deltaSeconds</c> 被忽略。
        /// 解出后为空操作，不累加。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            _stepCount++;
        }

        /// <summary>
        /// 取回结算报告：未解出或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 固定报 <see cref="GameplayKernelType.Turn"/> + 必胜，<see cref="GameplayResult.StepCount"/> 取
        /// <see cref="StepCount"/>，<see cref="GameplayResult.RemainingUnits"/> 取 <see cref="Moves"/>
        /// （解谜没有兵力/剩余资源，这里复用该字段承载"最优步数对比"用的实际步数），
        /// 战利品固定 <c>new LootItem(1003, 1)</c>。
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!IsSolved)
            {
                throw new InvalidOperationException("解谜尚未完成，禁止取回 GameplayResult。");
            }

            _resultCollected = true;

            IReadOnlyList<LootItem> loots = new LootItem[]
            {
                new LootItem(VictoryLootItemId, VictoryLootCount)
            };

            return new GameplayResult(
                GameplayKernelType.Turn,
                true,
                _stepCount,
                _moves,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>可通行：只有地面与目标点；墙、虚空与越界（返回 <see cref="PuzzleCell.Wall"/>）都不可进入。</summary>
        private static bool IsWalkable(PuzzleCell cell)
        {
            return cell == PuzzleCell.Floor || cell == PuzzleCell.Goal;
        }

        /// <summary>把关卡定义整表拷回运行期数组并复位玩家/箱子计数（构造与 <see cref="Reset"/> 共用）。</summary>
        private void LoadInitialState()
        {
            _level.CopyTo(_cells, _boxes);
            _playerX = _level.PlayerX;
            _playerY = _level.PlayerY;
            _moves = 0;
            _boxesOnGoal = _level.BoxesOnGoal;
        }
    }
}
