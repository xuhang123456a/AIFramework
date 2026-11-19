#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Tetris
{
    /// <summary>
    /// 7-bag 方块序列：每 7 个方块为一轮，把七种方块洗牌后依次发出，因此任意连续 7 个方块必然是
    /// 七种各一个（不会出现长时间不来 I 的饥饿），而同一颗种子必然产生同一条序列。
    /// <para>
    /// <b>为什么不"直接随机取一种"</b>：独立同分布采样会出现连续 10 个 S 这种观感灾难，
    /// 且无法保证方块种类均衡。7-bag 是方块游戏的行业标准，也让"同种子 14 个序列一致"
    /// 这类可复现性断言有明确语义。
    /// </para>
    /// <para>
    /// <b>预取设计</b>：<see cref="NextKind"/> 在构造期就被填好，每次 <see cref="Take"/> 只做
    /// "取走预取 + 补一份新预取"；只在当前袋发完时才重洗，所以视图读 <see cref="NextKind"/> 是纯读、无副作用。
    /// 洗牌本身用 Fisher-Yates + 内核 xorshift32，无 <c>System.Random</c>、无 LINQ。
    /// </para>
    /// <para>
    /// <b>零分配</b>：唯一数组 <c>_slots</c> 在构造期分配（7 个 <c>int</c>），此后
    /// <see cref="Take"/> / <see cref="NextKind"/> 全程不申请托管堆内存。
    /// </para>
    /// </summary>
    internal sealed class TetrisBag
    {
        /// <summary>一袋的方块数（七种各一个）。</summary>
        private const int BagSize = 7;

        private readonly int[] _slots;
        private TetrisRandom _random;

        /// <summary>下一个待发出的袋子槽位下标（<c>0..BagSize</c>；等于 <c>BagSize</c> 表示需要重洗）。</summary>
        private int _cursor;

        internal TetrisBag(int seed)
        {
            _slots = new int[BagSize];
            _random = new TetrisRandom(seed);
            _cursor = 0;

            Shuffle();
            NextKind = (TetrominoKind)_slots[_cursor];
            _cursor++;
        }

        /// <summary>下一个将要出生的方块类型（纯读，不消耗随机）。</summary>
        internal TetrominoKind NextKind { get; private set; }

        /// <summary>取走预取的下一个方块，并立即补足新的预取（必要时重洗一袋）。</summary>
        internal TetrominoKind Take()
        {
            TetrominoKind current = NextKind;

            if (_cursor >= BagSize)
            {
                Shuffle();
                _cursor = 0;
            }

            NextKind = (TetrominoKind)_slots[_cursor];
            _cursor++;
            return current;
        }

        /// <summary>把七种方块顺序填回槽位后做 Fisher-Yates 洗牌（自尾向前，取样区间递减）。</summary>
        private void Shuffle()
        {
            for (int i = 0; i < BagSize; i++)
            {
                _slots[i] = i;
            }

            for (int i = BagSize - 1; i > 0; i--)
            {
                int j = _random.NextIndex(i + 1);
                int swap = _slots[i];
                _slots[i] = _slots[j];
                _slots[j] = swap;
            }
        }
    }
}
