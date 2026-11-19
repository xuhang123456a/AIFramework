using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Ports;

namespace AIFramework.Kernel.Turn.CardBattle
{
    /// <summary>
    /// 卡牌回合战斗内核（<see cref="GameplayKernelType.CardBattle"/>）：Kernel.Turn 的第二套内容，
    /// 与 <see cref="TurnWorld"/> 共享"进出门 + 逐步推演"的回合流形态，但不共享任何类型。
    /// <para>
    /// <b>子表 TbCard（Phase 1 夹具）</b>：小端 <c>[int32 rowCount]</c> +
    /// <c>rowCount × { int32 cardId; int32 cost; int32 damage; int32 block; int32 copies; }</c>（20 字节/行）。
    /// 表缺失或 <c>rowCount == 0</c> → <see cref="InvalidOperationException"/>；
    /// 长度不符 / cardId 重复 / cost &lt; 0 / copies &lt;= 0 → <see cref="System.IO.InvalidDataException"/>。
    /// 无 loader 时用内置默认牌库（同布局）：斩击 <c>(1,1,6,0,3)</c>、防御 <c>(2,1,0,5,3)</c>、
    /// 重击 <c>(3,2,12,0,2)</c>、小刀 <c>(4,0,3,3,2)</c>，共 10 张。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：
    /// 玩家 HP 30；敌人 HP = <c>Math.Max(10, UnitCount * 10)</c>；每回合能量 3、抽 5 张，手牌上限 10（满则爆牌进弃牌堆）。
    /// 开局按种子（xorshift32 + Fisher–Yates）洗牌并抽 5 张。出牌：扣能量、手牌移除、伤害直扣敌人（敌人无护甲）、
    /// 护甲加给玩家、进弃牌堆；越界/已结束/能量不足/费用非法返回 <c>false</c> 且不改状态。
    /// 结束回合：敌人固定攻击 6，先由护甲吸收再扣 HP、护甲清零、回合数 +1、能量回到 3、抽 5 张。
    /// 敌人 HP &lt;= 0 判胜；玩家 HP &lt;= 0 或回合数 &gt; 30 判负。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：与 <see cref="TurnWorld"/> 的"步数"一致——<c>deltaSeconds</c> 只被忽略，
    /// 每调用一次推进一格待结算队列并累加 <see cref="ProcessedSteps"/>（Phase 1 没有驻留的待结算动作，
    /// 计数照样推进）；一次调用最多推进一次状态，且不分配托管堆内存。
    /// </para>
    /// <para>
    /// <b>零分配</b>：所有数组在构造期一次分配（牌堆/手牌/弃牌堆），<see cref="Step(float)"/>、
    /// <see cref="TryPlayCard"/>、<see cref="EndTurn"/> 内不申请托管堆；只有 <see cref="CollectResult"/>
    /// 按结算结果分配一次（战利品数组）。
    /// </para>
    /// </summary>
    public sealed class CardBattleWorld : IGameplaySession, ICardBattleInput, ICardBattleReadView
    {
        private readonly CardBattleState _state;
        private readonly CardBattleDeck _deck;

        /// <summary>使用内置默认牌库（Phase 1）：无需 loader，测试与冒烟直接用。</summary>
        public CardBattleWorld(in GameplayInitData init)
            : this(in init, CardTable.CreateDefault())
        {
        }

        /// <summary>从 TbCard 子表建牌库；表缺失/非法一律在构造期显式失败，不静默降级为空牌堆。</summary>
        public CardBattleWorld(in GameplayInitData init, IBinaryTableLoader loader)
            : this(in init, CardTable.Load(loader))
        {
        }

        private CardBattleWorld(in GameplayInitData init, CardDefinition[] cards)
        {
            _state = new CardBattleState(ResolveEnemyHp(init.UnitCount));
            _deck = new CardBattleDeck(cards, init.RandomSeed);

            // 开局抽牌：第 1 回合的 5 张手牌。
            _deck.Draw(CardBattleConstants.DrawPerTurn);
        }

        /// <summary>当前回合序号（从 1 开始）。</summary>
        public int TurnIndex
        {
            get { return _state.TurnIndex; }
        }

        /// <summary>剩余能量。</summary>
        public int Energy
        {
            get { return _state.Energy; }
        }

        /// <summary>玩家 HP。</summary>
        public int PlayerHp
        {
            get { return _state.PlayerHp; }
        }

        /// <summary>敌人 HP（可为负；判定只看 &lt;= 0）。</summary>
        public int EnemyHp
        {
            get { return _state.EnemyHp; }
        }

        /// <summary>玩家护甲（回合结束清零）。</summary>
        public int PlayerBlock
        {
            get { return _state.PlayerBlock; }
        }

        /// <summary>手牌数量。</summary>
        public int HandCount
        {
            get { return _deck.HandCount; }
        }

        /// <summary>抽牌堆剩余。</summary>
        public int DrawPileCount
        {
            get { return _deck.DrawCount; }
        }

        /// <summary>弃牌堆数量。</summary>
        public int DiscardPileCount
        {
            get { return _deck.DiscardCount; }
        }

        /// <summary>因手牌已满而进弃牌堆的累计张数（爆牌）。</summary>
        public int BurnedCount
        {
            get { return _deck.BurnedCount; }
        }

        /// <summary>累计成功打出的卡牌张数（测试/日志用）。</summary>
        public int PlayedCardCount
        {
            get { return _state.PlayedCardCount; }
        }

        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _state.ProcessedSteps; }
        }

        /// <summary>
        /// 牌库随机状态（xorshift32）的当前值：同种子的两个会话在任意同一步必须相等，
        /// 供测试做可复现断言（不暴露牌面，只暴露状态）。
        /// </summary>
        public int RandomState
        {
            get { return _deck.RandomState; }
        }

        /// <summary>敌人 HP &lt;= 0（胜）、玩家 HP &lt;= 0 或回合数 &gt; 30（负），或结算已取回。</summary>
        public bool IsFinished
        {
            get { return _state.IsFinished; }
        }

        /// <summary>
        /// 推演一步：<paramref name="deltaSeconds"/> 数值被忽略（回合制不按时间推进），
        /// 每调用一次计一步。已结束（含结算已取回）时为空操作，不再累加。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (_state.IsFinished)
            {
                return;
            }

            _state.ProcessedSteps++;
        }

        /// <summary>
        /// 打出第 <paramref name="handIndex"/> 张手牌。任一校验不过都返回 <c>false</c> 且<b>不改任何状态</b>：
        /// 对局已结束 / 索引越界 / 费用非法 / 能量不足。
        /// </summary>
        public bool TryPlayCard(int handIndex)
        {
            if (_state.IsFinished)
            {
                return false;
            }

            if (!_deck.TryGetHandCard(handIndex, out CardDefinition definition))
            {
                return false;
            }

            if (definition.Cost < 0 || _state.Energy < definition.Cost)
            {
                return false;
            }

            _deck.TryRemoveHandCard(handIndex, out CardDefinition played);
            _state.Energy -= played.Cost;

            // 敌人没有护甲：伤害直接扣 HP；可能为负，判定在 IsFinished 里统一做（扣血后立即可见）。
            if (played.Damage > 0)
            {
                _state.EnemyHp -= played.Damage;
            }

            if (played.Block > 0)
            {
                _state.PlayerBlock += played.Block;
            }

            _deck.AddToDiscard(in played);
            _state.PlayedCardCount++;
            return true;
        }

        /// <summary>
        /// 结束本回合：敌人固定攻击 6（先由护甲吸收，剩余扣 HP），护甲清零，
        /// 回合数 +1、能量重置、抽 5 张；随后由 <see cref="IsFinished"/> 判定玩家死亡或回合超限。
        /// 对局已结束时直接返回。
        /// </summary>
        public void EndTurn()
        {
            if (_state.IsFinished)
            {
                return;
            }

            int attack = CardBattleConstants.EnemyAttackDamage;
            int absorbed = _state.PlayerBlock < attack ? _state.PlayerBlock : attack;
            int unblocked = attack - absorbed;

            if (unblocked > 0)
            {
                _state.PlayerHp -= unblocked;
            }

            _state.PlayerBlock = 0;
            _state.TurnIndex++;
            _state.Energy = CardBattleConstants.EnergyPerTurn;
            _deck.Draw(CardBattleConstants.DrawPerTurn);
        }

        /// <summary>取第 <paramref name="index"/> 张手牌；越界返回 <c>false</c>，不抛异常。</summary>
        public bool TryGetHandCard(int index, out CardViewData card)
        {
            if (!_deck.TryGetHandCard(index, out CardDefinition definition))
            {
                card = default;
                return false;
            }

            card = new CardViewData(definition.CardId, definition.Cost, definition.Damage, definition.Block);
            return true;
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 数组只在这里按结果分配一次；<see cref="GameplayResult.RemainingUnits"/> 取玩家剩余 HP。
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_state.ResultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_state.IsFinished)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _state.ResultCollected = true;
            bool isVictory = _state.IsVictory;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(CardBattleConstants.VictoryLootItemId, CardBattleConstants.VictoryLootCount) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.CardBattle,
                isVictory,
                _state.ProcessedSteps,
                _state.PlayerHp,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>敌人 HP = <c>Math.Max(10, UnitCount * 10)</c>；用 long 计算避免 UnitCount 极大时溢出。</summary>
        private static int ResolveEnemyHp(int unitCount)
        {
            long scaled = (long)unitCount * CardBattleConstants.EnemyHpPerUnit;
            if (scaled > int.MaxValue)
            {
                return int.MaxValue;
            }

            return scaled < CardBattleConstants.MinEnemyHp
                ? CardBattleConstants.MinEnemyHp
                : (int)scaled;
        }
    }
}
