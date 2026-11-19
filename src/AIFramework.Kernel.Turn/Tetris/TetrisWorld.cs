#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Tetris
{
    /// <summary>
    /// 俄罗斯方块沙盒（<see cref="GameplayKernelType.Turn"/>）：Kernel.Turn 的第五套内容，
    /// 验证"重力下落 + 旋转踢墙 + 消行计分"这类<b>经典状态机</b>能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么不新增 <c>GameplayKernelType</c> 枚举值</b>：本卡带对主板而言只是"又一个回合制会话"，
    /// 胜负、战利品与流程完全一致；新增枚举值会强逼装配层与存档协议同步扩容（与战棋、三消同一条理由）。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：棋盘 10×20，<c>y = 0</c> 在顶、下落即 <c>y + 1</c>；
    /// 方块序列由 <see cref="GameplayInitData.RandomSeed"/> 驱动的 7-bag（<see cref="TetrisBag"/>）；
    /// 出生点固定 <c>(x = 3, y = 0)</c>，出生即碰撞 → 对局结束；锁定后消行，1/2/3/4 行分别得
    /// 100/300/500/800 × <b>消行前的等级</b>；等级 = <c>1 + ClearedLines / 10</c>，
    /// 下落间隔 = <c>max(100, 800 - (等级 - 1) * 70)</c> 毫秒。
    /// </para>
    /// <para>
    /// <b>计分用"消行前等级"</b>：这样一次消行跨级时不会凭空按新等级多给分，
    /// 与"等级只由累计消行数决定"的单调口径一致；注释即契约，不是笔误。
    /// </para>
    /// <para>
    /// <b>输入语义</b>：<see cref="TryMoveLeft"/> / <see cref="TryMoveRight"/> / <see cref="TryRotate"/> 被拒时
    /// 不改变任何状态；<see cref="TrySoftDrop"/> 返回"本次有没有真的下移一格"，<b>触底那一次返回 false 并顺手锁定</b>
    /// （这样"软降到底"和"硬降"都能结束一个方块的生命周期，不必再引入一个额外的 Lock 方法）；
    /// <see cref="HardDrop"/> 直落到底、锁死，并按 <c>下落格数 × 2</c> 加分；<see cref="Tick"/> 按间隔自动下落一格。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：<c>Step(dt) = Tick(dt) + ProcessedSteps++</c>（规则 6），对局结束后为空操作。
    /// <b>零分配</b>：棋盘、7-bag 槽位都在构造期一次分配，<see cref="TryMoveLeft"/> / <see cref="TryMoveRight"/> /
    /// <see cref="TryRotate"/> / <see cref="TrySoftDrop"/> / <see cref="HardDrop"/> / <see cref="Tick"/> /
    /// <see cref="Step"/> 全程不申请托管堆内存；只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组。
    /// </para>
    /// </summary>
    public sealed class TetrisWorld : IGameplaySession, ITetrisReadView, ITetrisInput
    {
        /// <summary>棋盘列数（固定 10）。</summary>
        private const int FieldWidth = 10;

        /// <summary>棋盘行数（固定 20）。</summary>
        private const int FieldHeight = 20;

        /// <summary>出生列：包围盒左上角落在第 3 列（3 格宽方块占 3..5，I 占 3..6）。</summary>
        private const int SpawnX = 3;

        /// <summary>出生行：顶行。</summary>
        private const int SpawnY = 0;

        /// <summary>1 级的下落间隔（毫秒）。</summary>
        private const int BaseDropIntervalMs = 800;

        /// <summary>每升一级缩短的下落间隔（毫秒）。</summary>
        private const int DropIntervalStepMs = 70;

        /// <summary>下落间隔下限（毫秒）。</summary>
        private const int MinDropIntervalMs = 100;

        /// <summary>升一级所需的累计消行数。</summary>
        private const int LinesPerLevel = 10;

        /// <summary>硬降每下落一格的加分。</summary>
        private const int HardDropScorePerRow = 2;

        /// <summary>胜利战利品模板 ID。</summary>
        private const int VictoryLootItemId = 1003;

        private readonly TetrisBoard _board;
        private readonly TetrisBag _bag;

        private TetrominoKind _currentKind;
        private int _currentRotation;
        private int _currentX;
        private int _currentY;
        private int _clearedLines;
        private int _score;
        private int _level;
        private int _processedSteps;
        private int _pieceCount;

        /// <summary>自动下落的时间累加器（毫秒）；出生新方块时归零，让每个方块都吃满一格间隔。</summary>
        private double _accumulatedMs;

        private bool _gameOver;
        private bool _resultCollected;

        public TetrisWorld(in GameplayInitData init)
        {
            _board = new TetrisBoard(FieldWidth, FieldHeight);
            _bag = new TetrisBag(init.RandomSeed);

            _level = 1;
            _clearedLines = 0;
            _score = 0;
            _processedSteps = 0;
            _pieceCount = 0;
            _accumulatedMs = 0.0;
            _gameOver = false;
            _resultCollected = false;

            SpawnNextPiece();
        }

        /// <summary>棋盘列数。</summary>
        public int Width
        {
            get { return _board.Width; }
        }

        /// <summary>棋盘行数。</summary>
        public int Height
        {
            get { return _board.Height; }
        }

        /// <summary>已锁定方块占用的格数（扫描棋盘得出，永远与 <see cref="CellAt"/> 一致）。</summary>
        public int FilledCellCount
        {
            get { return _board.FilledCellCount; }
        }

        /// <summary>本局累计消除的行数。</summary>
        public int ClearedLines
        {
            get { return _clearedLines; }
        }

        /// <summary>当前分数。</summary>
        public int Score
        {
            get { return _score; }
        }

        /// <summary>当前等级（<c>1 + ClearedLines / 10</c>）。</summary>
        public int Level
        {
            get { return _level; }
        }

        /// <summary>正在下落的方块类型。</summary>
        public TetrominoKind CurrentKind
        {
            get { return _currentKind; }
        }

        /// <summary>正在下落的方块包围盒左上角列坐标。</summary>
        public int CurrentX
        {
            get { return _currentX; }
        }

        /// <summary>正在下落的方块包围盒左上角行坐标。</summary>
        public int CurrentY
        {
            get { return _currentY; }
        }

        /// <summary>下一个方块的 <see cref="TetrominoKind"/> 数值（纯读，不消耗随机）。</summary>
        public int NextKindRaw
        {
            get { return (int)_bag.NextKind; }
        }

        /// <summary>0 空；否则为 <c>TetrominoKind + 1</c>；越界返回 <c>-1</c>。</summary>
        public int CellAt(int x, int y)
        {
            return _board.Get(x, y);
        }

        /// <summary>是否已判负（新方块出生即碰撞）。</summary>
        public bool IsGameOver
        {
            get { return _gameOver; }
        }

        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>本局已经锁定落地的方块数量（正在下落的那个不算）。</summary>
        public int PieceCount
        {
            get { return _pieceCount; }
        }

        /// <summary>当前下落间隔（毫秒）：<c>max(100, 800 - (等级 - 1) * 70)</c>。</summary>
        public int DropIntervalMs
        {
            get
            {
                int interval = BaseDropIntervalMs - ((_level - 1) * DropIntervalStepMs);
                return interval < MinDropIntervalMs ? MinDropIntervalMs : interval;
            }
        }

        /// <summary>对局是否结束：已判负或结算报告已被取回。</summary>
        public bool IsFinished
        {
            get { return _gameOver || _resultCollected; }
        }

        /// <summary>
        /// 推演一步 = <see cref="Tick(double)"/> + <see cref="ProcessedSteps"/> +1（规则 6）。
        /// 已结束（含结算已取回）时为空操作，不累加。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            Tick(deltaSeconds);
            _processedSteps++;
        }

        /// <summary>左移一格；出界或与已锁定格碰撞返回 false，位置不变。</summary>
        public bool TryMoveLeft()
        {
            if (IsFinished || CollidesAt(_currentX - 1, _currentY, _currentRotation))
            {
                return false;
            }

            _currentX--;
            return true;
        }

        /// <summary>右移一格；出界或与已锁定格碰撞返回 false，位置不变。</summary>
        public bool TryMoveRight()
        {
            if (IsFinished || CollidesAt(_currentX + 1, _currentY, _currentRotation))
            {
                return false;
            }

            _currentX++;
            return true;
        }

        /// <summary>
        /// 顺时针旋转 90°。原位放不下时依次尝试左、右各踢 1 格；两处都不行则整体回退
        /// （朝向与坐标都不变）并返回 false。
        /// </summary>
        public bool TryRotate()
        {
            if (IsFinished)
            {
                return false;
            }

            int target = (_currentRotation + 1) & 3;

            if (!CollidesAt(_currentX, _currentY, target))
            {
                _currentRotation = target;
                return true;
            }

            if (!CollidesAt(_currentX - 1, _currentY, target))
            {
                _currentRotation = target;
                _currentX--;
                return true;
            }

            if (!CollidesAt(_currentX + 1, _currentY, target))
            {
                _currentRotation = target;
                _currentX++;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 下移一格。触底时锁定当前方块并返回 false（本次没有真的位移，但方块已经落地）。
        /// </summary>
        public bool TrySoftDrop()
        {
            if (IsFinished)
            {
                return false;
            }

            return DropOneRow();
        }

        /// <summary>
        /// 直落到底并锁定：不加推演步数（规则 5 的"步数"专指 <see cref="Step(float)"/>），
        /// 但每下落一格加 2 分；落地后的消行与出生流程与软降完全一致。
        /// </summary>
        public void HardDrop()
        {
            if (IsFinished)
            {
                return;
            }

            int dropped = 0;
            while (!CollidesAt(_currentX, _currentY + 1, _currentRotation))
            {
                _currentY++;
                dropped++;
            }

            _score += dropped * HardDropScorePerRow;
            LockCurrentPiece();
        }

        /// <summary>
        /// 自动下落：把 <paramref name="deltaSeconds"/> 累加到毫秒累加器，攒够 <see cref="DropIntervalMs"/>
        /// 就下移一格（触底即锁定），每次调用最多下落一格，避免一帧补算多格导致观感跳变。
        /// 非正数/NaN 的 <paramref name="deltaSeconds"/> 被忽略。
        /// </summary>
        public void Tick(double deltaSeconds)
        {
            if (IsFinished || !(deltaSeconds > 0.0))
            {
                return;
            }

            _accumulatedMs += deltaSeconds * 1000.0;
            if (_accumulatedMs < DropIntervalMs)
            {
                return;
            }

            _accumulatedMs -= DropIntervalMs;
            DropOneRow();
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// <para>
        /// 胜利口径：<c>ClearedLines &gt;= 1</c>（至少消一行），胜利战利品为 <c>new LootItem(1003, 1)</c>；
        /// <see cref="GameplayResult.RemainingUnits"/> = 棋盘高度 - 堆高（全空时为 20）。
        /// </para>
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_gameOver)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;
            bool isVictory = _clearedLines >= 1;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(VictoryLootItemId, 1) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _processedSteps,
                _board.TopFilledY,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>方块在 (<paramref name="originX"/>, <paramref name="originY"/>) 的指定朝向下是否撞墙/撞块。</summary>
        private bool CollidesAt(int originX, int originY, int rotation)
        {
            return _board.Collides(TetrisShapes.Mask(_currentKind, rotation), originX, originY);
        }

        /// <summary>
        /// 下落一格；触底则锁定并让下一块出生。返回值 = 是否真的下移（false 表示本次触底）。
        /// </summary>
        private bool DropOneRow()
        {
            if (CollidesAt(_currentX, _currentY + 1, _currentRotation))
            {
                LockCurrentPiece();
                return false;
            }

            _currentY++;
            return true;
        }

        /// <summary>
        /// 把当前方块烤进棋盘 → 消行结算 → 出生下一个。
        /// <para>计分顺序：先按<b>消行前</b>的等级取分，再累加消行数、重算等级，最后才刷新下落间隔。</para>
        /// </summary>
        private void LockCurrentPiece()
        {
            _board.Lock(
                TetrisShapes.Mask(_currentKind, _currentRotation),
                _currentX,
                _currentY,
                (int)_currentKind + 1);
            _pieceCount++;

            int cleared = _board.ClearFullRows();
            if (cleared > 0)
            {
                _score += LineClearScore(cleared) * _level;
                _clearedLines += cleared;
                _level = 1 + (_clearedLines / LinesPerLevel);
            }

            SpawnNextPiece();
        }

        /// <summary>从 7-bag 取下一块放到出生点；出生即碰撞则置 <see cref="_gameOver"/>（不再出生）。</summary>
        private void SpawnNextPiece()
        {
            _currentKind = _bag.Take();
            _currentRotation = 0;
            _currentX = SpawnX;
            _currentY = SpawnY;
            _accumulatedMs = 0.0;

            if (CollidesAt(_currentX, _currentY, _currentRotation))
            {
                _gameOver = true;
            }
        }

        /// <summary>消行数的分值表（1/2/3/4 行 = 100/300/500/800），再乘消行前等级。</summary>
        private static int LineClearScore(int lines)
        {
            switch (lines)
            {
                case 1:
                    return 100;
                case 2:
                    return 300;
                case 3:
                    return 500;
                case 4:
                    return 800;
                default:
                    return 0;
            }
        }
    }
}
