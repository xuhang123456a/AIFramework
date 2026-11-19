#nullable enable
using System;

namespace AIFramework.Kernel.Turn.Roguelike
{
    /// <summary>
    /// 牌堆：卡组（本轮全部牌）/ 抽牌堆 / 弃牌堆 / 手牌四个定长区，外加确定性洗牌。
    /// <para>
    /// <b>容量与守恒</b>：四个区都在构造期一次分配（卡组、抽牌堆、弃牌堆各 <see cref="RoguelikeRules.MaxDeckSize"/>，
    /// 手牌 <see cref="RoguelikeRules.HandSize"/>）。抽牌堆 + 手牌 + 弃牌堆的牌数恒等于"本场战斗开始时参战的牌数"，
    /// 弃牌回流只搬牌不造牌；战斗奖励/商店购买只往卡组尾部追加，下一场战斗
    /// <see cref="BeginBattle"/> 重建抽牌堆时才会进入战斗。
    /// </para>
    /// <para>
    /// <b>手牌上限</b>：抽牌时手牌若已满（理论上不可达：每回合先 <see cref="DiscardHand"/> 再抽 5），
    /// 该牌直接进弃牌堆，不丢牌——与 <c>CardBattleDeck</c> 的爆牌口径一致。
    /// </para>
    /// </summary>
    internal sealed class RoguelikeDeck
    {
        /// <summary>卡组本体（本轮全部牌，按获得顺序追加）。</summary>
        private readonly RoguelikeCard[] _cards;

        private readonly RoguelikeCard[] _drawPile;

        private readonly RoguelikeCard[] _discardPile;

        private readonly RoguelikeCard[] _hand;

        private int _cardCount;

        private int _drawCount;

        private int _discardCount;

        private int _handCount;

        internal RoguelikeDeck()
        {
            _cards = new RoguelikeCard[RoguelikeRules.MaxDeckSize];
            _drawPile = new RoguelikeCard[RoguelikeRules.MaxDeckSize];
            _discardPile = new RoguelikeCard[RoguelikeRules.MaxDeckSize];
            _hand = new RoguelikeCard[RoguelikeRules.HandSize];

            ResetStarterDeck();
        }

        /// <summary>卡组总牌数（本轮 run 的卡组大小）。</summary>
        internal int CardCount
        {
            get { return _cardCount; }
        }

        /// <summary>手牌张数。</summary>
        internal int HandCount
        {
            get { return _handCount; }
        }

        /// <summary>
        /// 回到初始卡组：打击 ×3 + 防御 ×2，清空三个牌区。
        /// 卡组之外的"已获得奖励牌"因此全部丢弃——重开一轮 run 不继承上一轮的卡组。
        /// </summary>
        internal void ResetStarterDeck()
        {
            _cardCount = 0;
            _drawCount = 0;
            _discardCount = 0;
            _handCount = 0;

            for (int i = 0; i < RoguelikeRules.StarterStrikeCount; i++)
            {
                AddCard(RoguelikeCards.Strike);
            }

            for (int i = 0; i < RoguelikeRules.StarterDefendCount; i++)
            {
                AddCard(RoguelikeCards.Defend);
            }
        }

        /// <summary>
        /// 往卡组尾部追加一张牌（战斗奖励 / 商店购买）。
        /// 容量上限 <see cref="RoguelikeRules.MaxDeckSize"/> 远高于单轮理论峰值，溢出说明内核不变量被破坏，
        /// 显式抛异常而不是静默丢牌。
        /// </summary>
        internal void AddCard(in RoguelikeCard card)
        {
            if (_cardCount >= _cards.Length)
            {
                throw new InvalidOperationException("卡组容量溢出，MaxDeckSize 不变量被破坏。");
            }

            _cards[_cardCount] = card;
            _cardCount++;
        }

        /// <summary>取第 <paramref name="index"/> 张手牌；越界返回 <c>false</c>，不抛异常。</summary>
        internal bool TryGetHandCard(int index, out RoguelikeCard card)
        {
            if (index < 0 || index >= _handCount)
            {
                card = default;
                return false;
            }

            card = _hand[index];
            return true;
        }

        /// <summary>取走第 <paramref name="index"/> 张手牌（后续手牌前移）；越界返回 <c>false</c>。</summary>
        internal bool TryRemoveHandCard(int index, out RoguelikeCard card)
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
        internal void AddToDiscard(in RoguelikeCard card)
        {
            if (_discardCount >= _discardPile.Length)
            {
                // 四个区总数守恒，这里只可能是内部计数被破坏；显式失败优于静默覆盖。
                throw new InvalidOperationException("弃牌堆容量溢出，牌区守恒被破坏。");
            }

            _discardPile[_discardCount] = card;
            _discardCount++;
        }

        /// <summary>回合结束清空手牌：全部进弃牌堆（不是销毁）。</summary>
        internal void DiscardHand()
        {
            for (int i = 0; i < _handCount; i++)
            {
                RoguelikeCard card = _hand[i];
                AddToDiscard(in card);
            }

            _handCount = 0;
        }

        /// <summary>
        /// 开战：用卡组重建抽牌堆并洗牌，然后抽 <see cref="RoguelikeRules.HandSize"/> 张。
        /// 上一场战斗残留的手牌/弃牌堆在这里被整体丢弃（它们的牌都在卡组里）。
        /// </summary>
        internal void BeginBattle(RoguelikeRandom random)
        {
            _handCount = 0;
            _discardCount = 0;
            _drawCount = _cardCount;

            for (int i = 0; i < _cardCount; i++)
            {
                _drawPile[i] = _cards[i];
            }

            ShuffleDrawPile(random);
            Draw(RoguelikeRules.HandSize, random);
        }

        /// <summary>抽 <paramref name="count"/> 张；抽牌堆空则先把弃牌堆洗回，再抽不到就提前结束。</summary>
        internal void Draw(int count, RoguelikeRandom random)
        {
            for (int i = 0; i < count; i++)
            {
                if (!DrawOne(random))
                {
                    return;
                }
            }
        }

        private bool DrawOne(RoguelikeRandom random)
        {
            if (_drawCount == 0 && !TryReshuffleDiscardIntoDrawPile(random))
            {
                return false;
            }

            _drawCount--;
            RoguelikeCard card = _drawPile[_drawCount];

            if (_handCount >= RoguelikeRules.HandSize)
            {
                AddToDiscard(in card);
                return true;
            }

            _hand[_handCount] = card;
            _handCount++;
            return true;
        }

        private bool TryReshuffleDiscardIntoDrawPile(RoguelikeRandom random)
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

            // 弃牌回流复用同一随机流：同种子的整局序列才不会因为"是否发生过回流"而分叉。
            ShuffleDrawPile(random);
            return true;
        }

        private void ShuffleDrawPile(RoguelikeRandom random)
        {
            for (int i = _drawCount - 1; i > 0; i--)
            {
                int j = random.NextInt(i + 1);
                RoguelikeCard swap = _drawPile[i];
                _drawPile[i] = _drawPile[j];
                _drawPile[j] = swap;
            }
        }
    }
}
