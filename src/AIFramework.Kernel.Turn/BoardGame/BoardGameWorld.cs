#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.BoardGame
{
    /// <summary>
    /// 黑白棋沙盒（<see cref="GameplayKernelType.Turn"/>）：Kernel.Turn 的又一套内容，
    /// 验证"回合制棋盘 + 搜索 AI"能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么继续复用 Turn 卡带</b>：对主板而言黑白棋只是"另一个回合制会话"——进门拿
    /// <see cref="GameplayInitData"/>、出门交 <see cref="GameplayResult"/>，胜负、战利品与流程完全一致；
    /// 新增 <c>GameplayKernelType</c> 枚举值会强逼装配层与存档协议同步扩容，收益为零。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：8×8 标准初始盘（中央 4 格黑白各 2，黑 (3,4)/(4,3)、白 (3,3)/(4,4)），
    /// 黑先手；合法落子 = 八个方向上至少夹住一枚对方棋子（见 <see cref="BoardGameRules.CountFlips"/>），
    /// 落子后翻转全部被夹棋子再换手；当前玩家无合法落子时<b>自动跳过</b>（<see cref="PassCount"/> +1，
    /// <see cref="IsPassedLastTurn"/> 置 true，回合交还给刚下完的一方），双方都无合法落子即结束；
    /// 结束时按子数判胜负（<see cref="Winner"/>：黑多 1、白多 2、相等 0 平局）。
    /// </para>
    /// <para>
    /// <b>谁能落子</b>：<see cref="TryPlace"/> 只收黑方（本地玩家），白方回合调用返回 false 且不改任何状态——
    /// 白方必须走 <see cref="PlayAiTurn"/>。<see cref="PlayAiTurn"/> 只在白方回合生效，黑方回合是空操作，
    /// 保证"适配层无论怎么乱调，规则都不会走出两条路径"。
    /// </para>
    /// <para>
    /// <b>难度</b>：Easy 在合法点里按 <see cref="GameplayInitData.RandomSeed"/> 随机；Normal 贪心最大化翻转数；
    /// Hard 极小极大 4 层（角 +30 / 角邻 -12）。非法难度（&gt; Hard）按 Normal 处理，不抛异常——
    /// 难度是表现层参数，不该让一局棋崩掉。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：没有待播的动画队列，<see cref="Step(float)"/> 只累加 <see cref="ProcessedSteps"/>
    /// （<c>deltaSeconds</c> 被忽略）；对局结束（含结算已取回）后为空操作，不累加。
    /// </para>
    /// <para>
    /// <b>零分配</b>：棋盘、搜索层缓冲、随机状态都在构造期就绪，
    /// <see cref="Step(float)"/> / <see cref="TryPlace"/> / <see cref="PlayAiTurn"/> / <see cref="Restart"/>
    /// 不申请托管堆内存；只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组（出门 DTO 只允许构造一次）。
    /// </para>
    /// </summary>
    public sealed class BoardGameWorld : IGameplaySession, IBoardGameReadView, IBoardGameInput
    {
        /// <summary>实盘棋盘（含黑白计数）。</summary>
        private readonly BoardGameBoard _board;

        /// <summary>白方 AI（含全部搜索缓冲）。</summary>
        private readonly BoardGameAi _ai;

        /// <summary>进门种子：<see cref="Restart"/> 用它把随机流拨回同一处。</summary>
        private readonly int _initialSeed;

        /// <summary>Easy 难度用的确定性随机流。</summary>
        private BoardGameRandom _random;

        private int _currentPlayer;

        private int _moveCount;

        private int _passCount;

        private int _winner;

        private int _lastMoveX;

        private int _lastMoveY;

        private bool _passedLastTurn;

        private bool _finished;

        private bool _resultCollected;

        private int _processedSteps;

        private int _playerMoves;

        private int _aiMoves;

        public BoardGameWorld(in GameplayInitData init)
        {
            _board = new BoardGameBoard();
            _ai = new BoardGameAi();
            _initialSeed = init.RandomSeed;
            _random = new BoardGameRandom(_initialSeed);

            _currentPlayer = BoardGameRules.Black;
            _moveCount = 0;
            _passCount = 0;
            _winner = BoardGameRules.Draw;
            _lastMoveX = -1;
            _lastMoveY = -1;
            _passedLastTurn = false;
            _finished = false;
            _resultCollected = false;
            _processedSteps = 0;
            _playerMoves = 0;
            _aiMoves = 0;
        }

        /// <summary>棋盘边长（固定 8）。</summary>
        public int Size
        {
            get { return BoardGameRules.Size; }
        }

        /// <summary>黑子数。</summary>
        public int BlackCount
        {
            get { return _board.BlackCount; }
        }

        /// <summary>白子数。</summary>
        public int WhiteCount
        {
            get { return _board.WhiteCount; }
        }

        /// <summary>当前该谁落子：1 = 黑（本地玩家），2 = 白（AI）。</summary>
        public int CurrentPlayer
        {
            get { return _currentPlayer; }
        }

        /// <summary>本局成功落子手数（黑 + 白；跳过与被拒输入不计，重开归零）。</summary>
        public int MoveCount
        {
            get { return _moveCount; }
        }

        /// <summary>会话级跳过累计次数：<b>只增不减</b>，<see cref="Restart"/> 刻意不清零（见方法注释）。</summary>
        public int PassCount
        {
            get { return _passCount; }
        }

        /// <summary>胜方：0 = 未结束或平局，1 = 黑胜，2 = 白胜。</summary>
        public int Winner
        {
            get { return _winner; }
        }

        /// <summary>最近一次换手是否发生了跳过（单方跳过与终局的双双无手都为 true）。</summary>
        public bool IsPassedLastTurn
        {
            get { return _passedLastTurn; }
        }

        /// <summary>最近一手列坐标；尚无落子为 <c>-1</c>。</summary>
        public int LastMoveX
        {
            get { return _lastMoveX; }
        }

        /// <summary>最近一手行坐标；尚无落子为 <c>-1</c>。</summary>
        public int LastMoveY
        {
            get { return _lastMoveY; }
        }

        /// <summary><see cref="Step(float)"/> 的有效调用次数，与结算报告 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>本局黑方（本地玩家）成功落子手数，重开归零。</summary>
        public int PlayerMoves
        {
            get { return _playerMoves; }
        }

        /// <summary>本局白方（AI）成功落子手数，重开归零。</summary>
        public int AiMoves
        {
            get { return _aiMoves; }
        }

        /// <summary>对局是否结束：双方都无合法落子，或结算报告已被取回。</summary>
        public bool IsFinished
        {
            get { return _finished || _resultCollected; }
        }

        /// <summary>取 (x, y) 的格子；越界返回 <see cref="BoardCell.Empty"/>。</summary>
        public BoardCell GetCell(int x, int y)
        {
            return (BoardCell)_board.GetCell(x, y);
        }

        /// <summary>
        /// 本地玩家（黑方）在 (x, y) 落子：只有 <see cref="CurrentPlayer"/> 为 1、格子在界内、为空、
        /// 且至少夹住一枚白子时才成功。失败一律返回 false 且<b>不换手、不计数</b>。
        /// 成功则落子翻转、<see cref="PlayerMoves"/> / <see cref="MoveCount"/> +1、记最后一手，
        /// 然后按"能换手就换手、不能就跳过"推进回合。
        /// </summary>
        public bool TryPlace(int x, int y)
        {
            if (IsFinished || _currentPlayer != BoardGameRules.Black)
            {
                return false;
            }

            if (!_board.TryPlace(x, y, BoardGameRules.Black, out _))
            {
                return false;
            }

            _playerMoves++;
            _moveCount++;
            _lastMoveX = x;
            _lastMoveY = y;
            AdvanceTurn(BoardGameRules.Black);
            return true;
        }

        /// <summary>
        /// 白方 AI 落子：只在白方回合生效（黑方回合、对局结束、结算已取回都是空操作）。
        /// 难度大于 <see cref="BoardGameDifficulty.Hard"/> 时按 Normal 处理。
        /// </summary>
        public void PlayAiTurn(BoardGameDifficulty difficulty)
        {
            if (IsFinished || _currentPlayer != BoardGameRules.White)
            {
                return;
            }

            if (difficulty > BoardGameDifficulty.Hard)
            {
                difficulty = BoardGameDifficulty.Normal;
            }

            int x;
            int y;
            if (!_ai.TryChooseMove(_board.Cells, BoardGameRules.White, difficulty, ref _random, out x, out y))
            {
                // 正常流程不可达（当前玩家恒有棋可下）；真到了这里只推进回合，不伪造一手棋。
                AdvanceTurn(BoardGameRules.White);
                return;
            }

            if (!_board.TryPlace(x, y, BoardGameRules.White, out _))
            {
                return;
            }

            _aiMoves++;
            _moveCount++;
            _lastMoveX = x;
            _lastMoveY = y;
            AdvanceTurn(BoardGameRules.White);
        }

        /// <summary>
        /// 重开：棋盘回到初始 4 子、黑先手、<see cref="Winner"/> 归 0、最后一手回 <c>-1</c>、
        /// 随机流拨回进门种子（同种子重开 = 同一条 AI 序列，便于复现），
        /// <see cref="ProcessedSteps"/> / <see cref="PlayerMoves"/> / <see cref="AiMoves"/> / <see cref="MoveCount"/>
        /// 全部清零。
        /// <para>
        /// <b><see cref="PassCount"/> 刻意不清零</b>：它是"这套卡带在本会话里累计跳过了几次"的诊断量，
        /// 用于观察 AI 是否会把自己下进无子可落的死局；重开一局不该把这条观测抹掉。
        /// </para>
        /// <para>结算报告已取回（会话所有权已交还）后为空操作。</para>
        /// </summary>
        public void Restart()
        {
            if (_resultCollected)
            {
                return;
            }

            _board.ResetToInitial();
            _random = new BoardGameRandom(_initialSeed);
            _currentPlayer = BoardGameRules.Black;
            _moveCount = 0;
            _winner = BoardGameRules.Draw;
            _lastMoveX = -1;
            _lastMoveY = -1;
            _passedLastTurn = false;
            _finished = false;
            _processedSteps = 0;
            _playerMoves = 0;
            _aiMoves = 0;
        }

        /// <summary>推演一步：只累加 <see cref="ProcessedSteps"/>（回合制不按时间推进），结束后为空操作。</summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            _processedSteps++;
        }

        /// <summary>
        /// 取回结算报告：未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 固定报 <see cref="GameplayKernelType.Turn"/>；<see cref="GameplayResult.IsVictory"/> = 黑方胜；
        /// 胜局发 <c>new LootItem(1003, 1)</c>，负/平局为空数组；
        /// <see cref="GameplayResult.RemainingUnits"/> 取黑子数（"我方还剩多少子"）。
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_finished)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;
            bool isVictory = _winner == BoardGameRules.Black;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(BoardGameRules.VictoryLootItemId, BoardGameRules.VictoryLootCount) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _processedSteps,
                _board.BlackCount,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>
        /// 换手：对手有棋可下就换给对手；否则对手跳过（<see cref="PassCount"/> +1、<see cref="IsPassedLastTurn"/> 置 true、
        /// 回合仍归刚下完的 <paramref name="mover"/>）；若连 mover 也无棋可下，双方都无子可落，直接结算。
        /// </summary>
        private void AdvanceTurn(int mover)
        {
            int next = BoardGameRules.Opposite(mover);

            if (BoardGameRules.HasLegalMove(_board.Cells, next))
            {
                _currentPlayer = next;
                _passedLastTurn = false;
                return;
            }

            _passCount++;
            _passedLastTurn = true;
            _currentPlayer = mover;

            if (!BoardGameRules.HasLegalMove(_board.Cells, mover))
            {
                Finish();
            }
        }

        /// <summary>按子数结算：黑多黑胜、白多白胜、相等 <see cref="BoardGameRules.Draw"/>（0）。</summary>
        private void Finish()
        {
            int black = _board.BlackCount;
            int white = _board.WhiteCount;

            if (black > white)
            {
                _winner = BoardGameRules.Black;
            }
            else if (white > black)
            {
                _winner = BoardGameRules.White;
            }
            else
            {
                _winner = BoardGameRules.Draw;
            }

            _finished = true;
        }
    }
}
