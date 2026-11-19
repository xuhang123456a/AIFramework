#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Match3
{
    /// <summary>
    /// 三消棋盘沙盒（<see cref="GameplayKernelType.Match3"/>）：Kernel.Turn 的第三套内容。
    /// <para>
    /// <b>推演模型</b>：<see cref="TrySwap"/> 是玩家意图，只在「交换后能形成三连」时才真正消耗 1 步并进入待消解状态；
    /// 消解本身由 <see cref="Step(float)"/> 逐级推进 —— <b>一次 Step 只结算一级</b>（标记 → 清格计分 → 重力 → 顶部补格），
    /// 这样 UI 可以按级播动画。级联期间输入被拒绝，避免在未稳定的棋盘上再交换。
    /// </para>
    /// <para>
    /// <b>零分配</b>：棋盘、标记位、候选颜色缓冲与随机源都在构造期一次性建好，
    /// <see cref="Step(float)"/> / <see cref="TrySwap"/> 不申请托管堆内存；堆数组只在 <see cref="CollectResult"/> 里按结果分配。
    /// </para>
    /// </summary>
    public sealed class Match3World : IGameplaySession, IMatch3Input, IMatch3ReadView
    {
        /// <summary>棋盘列数。</summary>
        private const int BoardWidth = 8;

        /// <summary>棋盘行数。</summary>
        private const int BoardHeight = 8;

        /// <summary>初始步数。</summary>
        private const int InitialMoves = 20;

        /// <summary>胜利所需分数。</summary>
        private const int VictoryScore = 1000;

        /// <summary>每清除一格的得分。</summary>
        private const int ScorePerClearedGem = 10;

        /// <summary>胜利战利品模板 ID。</summary>
        private const int VictoryLootItemId = 1002;

        private readonly Match3Board _board;

        /// <summary>随机源，构造期按 <see cref="GameplayInitData.RandomSeed"/> 播种；补格时消费。</summary>
        private Match3Random _random;

        private int _movesLeft;

        private int _score;

        private int _clearedCount;

        private int _processedSteps;

        private int _cascadeDepth;

        /// <summary>是否有尚未结算完的级联：为真时 <see cref="Step(float)"/> 继续消解，<see cref="TrySwap"/> 一律拒绝。</summary>
        private bool _resolving;

        private bool _finished;

        private bool _resultCollected;

        public Match3World(in GameplayInitData init)
        {
            _board = new Match3Board(BoardWidth, BoardHeight);
            _random = new Match3Random(init.RandomSeed);
            _board.Generate(ref _random);

            _movesLeft = InitialMoves;
            _score = 0;
            _clearedCount = 0;
            _processedSteps = 0;
            _cascadeDepth = 0;
            _resolving = false;
            _finished = false;
            _resultCollected = false;
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

        /// <summary>剩余步数。</summary>
        public int MovesLeft
        {
            get { return _movesLeft; }
        }

        /// <summary>当前分数。</summary>
        public int Score
        {
            get { return _score; }
        }

        /// <summary>目标分数。</summary>
        public int TargetScore
        {
            get { return VictoryScore; }
        }

        /// <summary>最近一次有效交换触发了几级消除（每次有效交换归零，每结算一级 +1）。</summary>
        public int CascadeDepth
        {
            get { return _cascadeDepth; }
        }

        /// <summary>已推演步数（<see cref="Step(float)"/> 的有效调用次数，结束后不再增长）。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>本局累计清除格数。</summary>
        public int ClearedCount
        {
            get { return _clearedCount; }
        }

        /// <summary>当前随机状态（可复现性断言用）。</summary>
        public int RandomState
        {
            get { return _random.State; }
        }

        /// <summary>
        /// 取 (x, y) 的宝石颜色 ID（<c>1..5</c>）；越界或空位返回 0，不抛异常。
        /// </summary>
        public int GetGem(int x, int y)
        {
            return _board.Get(x, y);
        }

        /// <summary>
        /// 交换相邻两格。越界 / 曼哈顿距离不为 1 / 级联未结算完 / 对局已结束 一律返回 false；
        /// 交换后构不成三连则<b>换回原样并返回 false（不消耗步数）</b>；构成三连则消耗 1 步并返回 true，
        /// 真正的清除要等到下一次 <see cref="Step(float)"/>。
        /// </summary>
        public bool TrySwap(int x1, int y1, int x2, int y2)
        {
            if (_finished || _resolving)
            {
                return false;
            }

            if (!_board.InBounds(x1, y1) || !_board.InBounds(x2, y2))
            {
                return false;
            }

            if (!Match3Board.AreAdjacent(x1, y1, x2, y2))
            {
                return false;
            }

            if (_board.Get(x1, y1) == Match3Board.Empty || _board.Get(x2, y2) == Match3Board.Empty)
            {
                return false;
            }

            _board.Swap(x1, y1, x2, y2);
            if (!_board.HasMatch())
            {
                _board.Swap(x1, y1, x2, y2);
                return false;
            }

            _movesLeft--;
            _cascadeDepth = 0;
            _resolving = true;
            return true;
        }

        /// <summary>
        /// 推进一个逻辑步：有待消解的级联时只结算<b>一级</b>（标记 → 清格计分 → 重力 → 顶部补格），
        /// 否则仅累计步数。对局结束后调用为空操作，不改变任何状态。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (_finished)
            {
                return;
            }

            _processedSteps++;

            if (_resolving)
            {
                ResolveOneLevel();
            }

            RefreshFinishedState();
        }

        /// <summary>
        /// 对局是否结束：分数达标立即结束（无需等步数用尽）；否则步数用尽且无待消解级联时结束。
        /// </summary>
        public bool IsFinished
        {
            get { return _finished; }
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 战利品数组只在这里按胜负分配一次。
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

            bool isVictory = _score >= VictoryScore;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(VictoryLootItemId, 1) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Match3,
                isVictory,
                _processedSteps,
                _movesLeft,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>结算一级：标记 → 清格 → 计分 → 重力补格 → 判断是否还有下一级。</summary>
        private void ResolveOneLevel()
        {
            int cleared = _board.MarkMatches();
            if (cleared == 0)
            {
                // 防御分支：进入待消解状态时必然已有三连，正常流程不会走到这里。
                _resolving = false;
                return;
            }

            _board.ClearMarked();
            _score += ScorePerClearedGem * cleared;
            _clearedCount += cleared;
            _cascadeDepth++;
            _board.CollapseAndRefill(ref _random);
            _resolving = _board.HasMatch();
        }

        /// <summary>
        /// 刷新结束标志：分数达标立即胜利结束（丢弃剩余级联）；步数用尽且无待消解级联时结束。
        /// </summary>
        private void RefreshFinishedState()
        {
            if (_finished)
            {
                return;
            }

            if (_score >= VictoryScore)
            {
                _finished = true;
                _resolving = false;
                return;
            }

            if (_movesLeft <= 0 && !_resolving)
            {
                _finished = true;
            }
        }
    }
}
