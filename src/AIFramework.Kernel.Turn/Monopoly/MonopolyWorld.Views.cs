#nullable enable
using System;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Monopoly
{
    /// <summary>
    /// <see cref="MonopolyWorld"/> 的"只读视图"半边：<see cref="IMonopolyReadView"/> 的全部成员与
    /// <see cref="GetPlayer"/> / <see cref="GetTile"/> 两个取数入口。
    /// <para>
    /// <b>为什么拆 partial</b>：单文件守住 400 行量级（AI 上下文自洽优先），拆分口径是"一个语义半边一整块"——
    /// 这里只读状态，回合推进与淘汰流程留在主文件，格子结算留在 <c>MonopolyWorld.Resolve.cs</c>。
    /// </para>
    /// <para>
    /// <b>零分配</b>：所有读数都是 <c>int</c> / <c>bool</c> / 值类型快照，不返回数组、不拼字符串。
    /// </para>
    /// </summary>
    public sealed partial class MonopolyWorld
    {
        /// <summary>棋盘格子总数（固定 20）。</summary>
        public int TileCount
        {
            get { return _board.TileCount; }
        }

        /// <summary>配置玩家数（1..4，局中不变；破产不会缩小本数值）。</summary>
        public int PlayerCount
        {
            get { return _playerCount; }
        }

        /// <summary>已执行的掷骰回合数（从 0 开始）。</summary>
        public int TurnIndex
        {
            get { return _turnIndex; }
        }

        /// <summary>下一个该掷骰的存活玩家索引。</summary>
        public int CurrentPlayer
        {
            get { return _currentPlayer; }
        }

        /// <summary>名义回合数（1 基）：<c>TurnIndex / PlayerCount + 1</c>。</summary>
        public int RoundNumber
        {
            get { return (_turnIndex / _playerCount) + 1; }
        }

        /// <summary>对局是否结束：收口判定已触发，或结算报告已被取回。</summary>
        public bool IsFinished
        {
            get { return _finished || _resultCollected; }
        }

        /// <summary>胜者玩家索引；<c>-1</c> = 未结束。</summary>
        public int WinnerPlayer
        {
            get { return _winnerPlayer; }
        }

        /// <summary>最近一次骰子点数（1..6）；尚未掷骰时为 0。</summary>
        public int LastDice
        {
            get { return _lastDice; }
        }

        /// <summary>最近一次行动的玩家索引；尚未掷骰时为 <c>-1</c>。</summary>
        public int LastMovedPlayer
        {
            get { return _lastMovedPlayer; }
        }

        /// <summary>最近一次行动结算完成后的最终落点；尚未掷骰时为 <c>-1</c>。</summary>
        public int LastMovedTo
        {
            get { return _lastMovedTo; }
        }

        /// <summary>累计抽到的机会卡张数。</summary>
        public int ChanceEvents
        {
            get { return _chanceEvents; }
        }

        /// <summary>累计"踩到无主地产但现金不足"的跳过次数。</summary>
        public int SkippedPurchaseCount
        {
            get { return _skippedPurchaseCount; }
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 名玩家的快照；越界（含负数）抛
        /// <see cref="ArgumentOutOfRangeException"/>（索引来自 <see cref="PlayerCount"/> 循环，不该越界）。
        /// </summary>
        public MonopolyPlayerView GetPlayer(int index)
        {
            if (index < 0 || index >= _playerCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "玩家 index 越界。");
            }

            return new MonopolyPlayerView(
                index,
                _cash[index],
                _positions[index],
                _bankrupt[index],
                _board.CountOwnedBy(index));
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 个格子的快照；越界（含负数）抛
        /// <see cref="ArgumentOutOfRangeException"/>（索引来自 <see cref="TileCount"/> 循环，不该越界）。
        /// </summary>
        public MonopolyTileView GetTile(int index)
        {
            if (index < 0 || index >= _board.TileCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "格子 index 越界。");
            }

            return _board.GetTile(index);
        }
    }
}
