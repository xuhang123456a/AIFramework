#nullable enable
using System;

namespace AIFramework.Kernel.Turn.CardBattle
{
    /// <summary>
    /// 牌堆：抽牌堆 / 弃牌堆 / 手牌三个定长区，外加确定性的 xorshift32 洗牌源。
    /// <para>
    /// 容量约定：抽牌堆与弃牌堆各按"总牌数"一次分配，手牌按 <see cref="CardBattleConstants.HandLimit"/>
    /// 一次分配；三个区里的牌总数恒等于总牌数（爆牌是"进弃牌堆"，不是销毁），
    /// 因此 <c>HandCount + DrawPileCount + DiscardPileCount</c> 是不变量，弃牌回流不会凭空造牌。
    /// </para>
    /// <para>
    /// 随机源固定为 xorshift32：<b>不使用</b> <see cref="System.Random"/>，种子只来自
    /// <c>GameplayInitData.RandomSeed</c>，同种子必然同序列。
    /// 洗牌与"弃牌堆洗回抽牌堆"共用同一个状态，二者顺序一旦改变序列就会变（这正是可复现的前提）。
    /// </para>
    /// </summary>
    internal sealed class CardBattleDeck
    {
        /// <summary>种子为 0 时的替换值：xorshift32 的 0 是不动点，必须换一个非零种子。</summary>
        private const uint ZeroSeedReplacement = 0x9E3779B9u;

        private readonly CardDefinition[] _drawPile;
        private readonly CardDefinition[] _discardPile;
        private readonly CardDefinition[] _hand;
        private readonly int _totalCards;

        private int _drawCount;
        private int _discardCount;
        private int _handCount;
        private int _burnedCount;
        private uint _randomState;

        internal CardBattleDeck(CardDefinition[] cards, int seed)
        {
            if (cards == null)
            {
                throw new ArgumentNullException(nameof(cards));
            }

            _totalCards = cards.Length;
            _drawPile = new CardDefinition[_totalCards];
            _discardPile = new CardDefinition[_totalCards];
            _hand = new CardDefinition[CardBattleConstants.HandLimit];

            for (int i = 0; i < _totalCards; i++)
            {
                _drawPile[i] = cards[i];
            }

            _drawCount = _totalCards;
            _discardCount = 0;
            _handCount = 0;
            _burnedCount = 0;
            _randomState = seed == 0 ? ZeroSeedReplacement : unchecked((uint)seed);

            // 开局洗牌：Fisher–Yates，用种子初始化的同一 RNG。
            ShuffleDrawPile();
        }

        /// <summary>本牌堆的总牌数（展开 copies 之后）。</summary>
        internal int TotalCardCount
        {
            get { return _totalCards; }
        }

        /// <summary>xorshift32 当前状态（按位转 int），用于测试复现。</summary>
        internal int RandomState
        {
            get { return unchecked((int)_randomState); }
        }

        internal int HandCount
        {
            get { return _handCount; }
        }

        internal int DrawCount
        {
            get { return _drawCount; }
        }

        internal int DiscardCount
        {
            get { return _discardCount; }
        }

        /// <summary>因手牌已满而直接进弃牌堆的累计张数。</summary>
        internal int BurnedCount
        {
            get { return _burnedCount; }
        }

        /// <summary>抽 <paramref name="count"/> 张牌；抽牌堆空则先把弃牌堆洗回，再抽不到就提前结束。</summary>
        internal void Draw(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (!DrawOne())
                {
                    return;
                }
            }
        }

        internal bool TryGetHandCard(int index, out CardDefinition card)
        {
            if (index < 0 || index >= _handCount)
            {
                card = default;
                return false;
            }

            card = _hand[index];
            return true;
        }

        internal bool TryRemoveHandCard(int index, out CardDefinition card)
        {
            if (index < 0 || index >= _handCount)
            {
                card = default;
                return false;
            }

            card = _hand[index];
            for (int i = index; i < _handCount - 1; i++)
            {
                _hand[i] = _hand[i + 1];
            }

            _handCount--;
            return true;
        }

        /// <summary>把一张牌放进弃牌堆（出牌与爆牌都走这里）。</summary>
        internal void AddToDiscard(in CardDefinition card)
        {
            if (_discardCount >= _totalCards)
            {
                // 三个区总数守恒，这里只可能是内部计数被破坏；显式失败优于静默覆盖。
                throw new InvalidOperationException("弃牌堆容量溢出，牌区守恒被破坏。");
            }

            _discardPile[_discardCount] = card;
            _discardCount++;
        }

        /// <summary>抽一张：抽牌堆空则洗回弃牌堆；手牌已满则进弃牌堆并计入爆牌。</summary>
        private bool DrawOne()
        {
            if (_drawCount == 0 && !TryReshuffleDiscardIntoDrawPile())
            {
                return false;
            }

            _drawCount--;
            CardDefinition card = _drawPile[_drawCount];

            if (_handCount >= CardBattleConstants.HandLimit)
            {
                AddToDiscard(in card);
                _burnedCount++;
                return true;
            }

            _hand[_handCount] = card;
            _handCount++;
            return true;
        }

        private bool TryReshuffleDiscardIntoDrawPile()
        {
            if (_discardCount == 0)
            {
                return false;
            }

            for (int i = 0; i < _discardCount; i++)
            {
                _drawPile[i] = _discardPile[i];
            }

            _drawCount = _discardCount;
            _discardCount = 0;

            // 弃牌回流复用同一 RNG：同种子的整局序列才不会因为"是否发生过回流"而分叉。
            ShuffleDrawPile();
            return true;
        }

        private void ShuffleDrawPile()
        {
            for (int i = _drawCount - 1; i > 0; i--)
            {
                int j = NextInt(i + 1);
                CardDefinition swap = _drawPile[i];
                _drawPile[i] = _drawPile[j];
                _drawPile[j] = swap;
            }
        }

        /// <summary>xorshift32（13/17/5 位移）：周期 2^32-1，状态为 0 时恒为 0，故构造期已排除 0 种子。</summary>
        private uint NextUInt()
        {
            uint x = _randomState;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _randomState = x;
            return x;
        }

        /// <summary>[0, maxExclusive) 的整数。取模有极小偏差，卡牌洗牌的统计性质不受影响。</summary>
        private int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 1)
            {
                return 0;
            }

            return (int)(NextUInt() % (uint)maxExclusive);
        }
    }
}
