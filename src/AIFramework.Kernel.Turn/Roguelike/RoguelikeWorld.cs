#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Roguelike
{
    /// <summary>
    /// 肉鸽卡牌沙盒（<see cref="GameplayKernelType.Turn"/>）：Kernel.Turn 的又一套内容，
    /// 验证"关卡地图 + 卡组成长 + 战斗奖励"能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么不新增 <c>GameplayKernelType</c> 枚举值</b>：肉鸽对主板而言只是"另一个回合制会话"，
    /// 胜负、战利品与流程与既有 Turn 内容完全一致；新增枚举值会强逼装配层与存档协议同步扩容。
    /// </para>
    /// <para>
    /// <b>地图</b>：固定 8 层线性节点（每层只有一个候选节点），种类由 <see cref="GameplayInitData.RandomSeed"/>
    /// 生成的"地图流"决定，并且强制保证：第 1 层 Battle、第 8 层 Boss、中段至少 1 个 Rest、至少 1 个 Shop。
    /// 同种子 = 同地图；<see cref="GameplayInitData.RandomSeed"/> 同时驱动洗牌与奖励抽卡。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：玩家 50/50 HP、0 金币、初始卡组 5 张（打击 1 费 6 伤 ×3、防御 1 费 5 格挡 ×2）。
    /// 每场战斗敌人 HP 由节点种类决定（Battle 20 / Elite 32 / Boss 60），玩家每回合抽 5 张、能量 3；
    /// 出牌先扣能量，伤害扣敌人 HP（Phase 1 敌人没有格挡），格挡加给玩家；
    /// <see cref="EndBattleTurn"/> = 敌人攻击（先由格挡吸收）→ 手牌清空 → 重抽 5 张 → 能量回到 3
    /// （玩家阵亡则清空后直接结束，不再重抽）；
    /// 敌人 HP <c>&lt;= 0</c> 判胜（发 15/25/60 金币 + 随机 1 张奖励牌 + <see cref="CardsAdded"/> / <see cref="BattlesWon"/> 各 +1），
    /// 玩家 HP <c>&lt;= 0</c> 判负。商店：金币 &gt;= 50 则花 50 买 1 张随机牌；营地：回复 20 HP（上限 50）。
    /// 打赢第 8 层 Boss → <see cref="IsRunOver"/> 与 <see cref="IsVictory"/> 同时为 <c>true</c>。
    /// <b>胜利口径</b>：出牌打死敌人立刻结算胜利（不让尸体再打一次），<see cref="EndBattleTurn"/> 入口再兜底一次。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：没有待结算动画队列，<see cref="Step(float)"/> 只累加 <see cref="ProcessedSteps"/>
    /// （<c>deltaSeconds</c> 被忽略），run 结束或结算已取回后为空操作。<b>零分配</b>：地图、牌堆四个区、
    /// 随机源与奖励牌池都在构造期（或类型初始化期）一次分配，<see cref="Step(float)"/> /
    /// <see cref="TryEnterNextNode"/> / <see cref="TryPlayCard"/> / <see cref="EndBattleTurn"/> /
    /// <see cref="Restart"/> 不申请托管堆内存；只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组。
    /// </para>
    /// </summary>
    public sealed partial class RoguelikeWorld : IGameplaySession, IRoguelikeReadView, IRoguelikeInput
    {
        private readonly RoguelikeMap _map;

        private readonly RoguelikeDeck _deck;

        /// <summary>战斗流随机源：洗牌、弃牌回流洗牌、奖励抽卡、商店选牌共用（与地图流互不影响）。</summary>
        private readonly RoguelikeRandom _random;

        private readonly int _seed;

        private int _currentNodeIndex;

        private int _depth;

        private int _playerHp;

        private int _gold;

        private int _energy;

        private int _playerBlock;

        private int _enemyHp;

        private int _enemyMaxHp;

        private RoguelikeNodeKind _battleKind;

        private bool _inBattle;

        private bool _runOver;

        private bool _victory;

        private bool _resultCollected;

        private int _processedSteps;

        private int _battlesWon;

        private int _cardsAdded;

        public RoguelikeWorld(in GameplayInitData init)
        {
            _seed = init.RandomSeed;
            _map = new RoguelikeMap(init.RandomSeed);
            _deck = new RoguelikeDeck();
            _random = new RoguelikeRandom(init.RandomSeed);

            ResetRunState();
        }

        // ── IRoguelikeReadView ─────────────────────────────────────────

        /// <summary>地图节点总数（固定 8）。</summary>
        public int NodeCount
        {
            get { return _map.NodeCount; }
        }

        /// <summary>当前所在节点的 index；<c>-1</c> 表示还没进入任何节点。</summary>
        public int CurrentNodeIndex
        {
            get { return _currentNodeIndex; }
        }

        /// <summary>玩家 HP（扣到负数不截断，判定只看 <c>&lt;= 0</c>）。</summary>
        public int PlayerHp
        {
            get { return _playerHp; }
        }

        /// <summary>玩家 HP 上限（固定 50）。</summary>
        public int PlayerMaxHp
        {
            get { return RoguelikeRules.PlayerMaxHp; }
        }

        /// <summary>当前金币。</summary>
        public int Gold
        {
            get { return _gold; }
        }

        /// <summary>本轮卡组的牌数（初始 5）。</summary>
        public int DeckSize
        {
            get { return _deck.CardCount; }
        }

        /// <summary>当前手牌张数（战斗外为 0）。</summary>
        public int HandCount
        {
            get { return _deck.HandCount; }
        }

        /// <summary>剩余能量（战斗外为 0）。</summary>
        public int Energy
        {
            get { return _energy; }
        }

        /// <summary>当前敌人 HP（战斗外为 0）。</summary>
        public int EnemyHp
        {
            get { return _enemyHp; }
        }

        /// <summary>当前敌人 HP 上限（战斗外为 0）。</summary>
        public int EnemyMaxHp
        {
            get { return _enemyMaxHp; }
        }

        /// <summary>是否正在战斗中。</summary>
        public bool InBattle
        {
            get { return _inBattle; }
        }

        /// <summary>本轮 run 是否已结束（阵亡或通关）。</summary>
        public bool IsRunOver
        {
            get { return _runOver; }
        }

        /// <summary>是否通关（仅打赢第 8 层 Boss 为 <c>true</c>）。</summary>
        public bool IsVictory
        {
            get { return _victory; }
        }

        /// <summary>当前层号（1..8）；未进入任何节点时为 0。</summary>
        public int Depth
        {
            get { return _depth; }
        }

        /// <summary>取第 <paramref name="index"/> 个节点的只读快照；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public RoguelikeNodeView GetNode(int index)
        {
            return _map.GetNode(index);
        }

        /// <summary>取第 <paramref name="index"/> 张手牌；越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public RoguelikeCardView GetHandCard(int index)
        {
            if (!_deck.TryGetHandCard(index, out RoguelikeCard card))
            {
                throw new ArgumentOutOfRangeException(nameof(index), "手牌 index 越界。");
            }

            return new RoguelikeCardView(card.CardId, card.Name, card.Cost, card.Damage, card.Block, true);
        }

        // ── IRoguelikeInput ────────────────────────────────────────────

        /// <summary>
        /// 沿地图前进一层并结算进入效果（战斗布场 / 商店购买 / 营地回血）。
        /// run 已结束、正在战斗中、已到达最后一层、或下一节点已被进入过时返回 <c>false</c> 且不改状态。
        /// </summary>
        public bool TryEnterNextNode()
        {
            if (IsFinished || _inBattle)
            {
                return false;
            }

            int next = _currentNodeIndex + 1;
            if (next >= _map.NodeCount || _map.IsVisited(next))
            {
                return false;
            }

            _map.MarkVisited(next);
            _currentNodeIndex = next;
            _depth = next + 1;

            RoguelikeNodeKind kind = _map.KindAt(next);
            if (RoguelikeRules.IsBattleKind(kind))
            {
                BeginBattle(kind);
                return true;
            }

            if (kind == RoguelikeNodeKind.Shop)
            {
                ResolveShop();
                return true;
            }

            ResolveRest();
            return true;
        }

        /// <summary>
        /// 打出第 <paramref name="handIndex"/> 张手牌。任一校验不过都返回 <c>false</c> 且<b>不改任何状态</b>：
        /// run 已结束 / 不在战斗中 / 索引越界 / 费用为负 / 能量不足。
        /// 伤害先扣敌人格挡再扣 HP（Phase 1 敌人没有格挡，等价于直扣 HP），格挡加给玩家；
        /// 敌人 HP <c>&lt;= 0</c> 时立即结算战斗胜利。
        /// </summary>
        public bool TryPlayCard(int handIndex)
        {
            if (IsFinished || !_inBattle)
            {
                return false;
            }

            if (!_deck.TryGetHandCard(handIndex, out RoguelikeCard card))
            {
                return false;
            }

            if (card.Cost < 0 || _energy < card.Cost)
            {
                return false;
            }

            _deck.TryRemoveHandCard(handIndex, out RoguelikeCard played);
            _energy -= played.Cost;

            if (played.Damage > 0)
            {
                _enemyHp -= played.Damage;
            }

            if (played.Block > 0)
            {
                _playerBlock += played.Block;
            }

            _deck.AddToDiscard(in played);

            if (_enemyHp <= 0)
            {
                ResolveBattleVictory();
            }

            return true;
        }

        /// <summary>
        /// 结束本回合：敌人攻击（先由格挡吸收）、清空手牌、重抽 5 张、能量回到 3；
        /// 玩家 HP <c>&lt;= 0</c> 时 run 结束（失败，手牌清空后不再重抽）。run 已结束或不在战斗中时为空操作。
        /// </summary>
        public void EndBattleTurn()
        {
            if (IsFinished || !_inBattle)
            {
                return;
            }

            if (_enemyHp <= 0)
            {
                // 不变量兜底：出牌致死必然已在 TryPlayCard 里结算，这里只防内部状态被破坏。
                ResolveBattleVictory();
                return;
            }

            int attack = RoguelikeRules.EnemyAttackFor(_battleKind);
            int absorbed = _playerBlock < attack ? _playerBlock : attack;
            int unblocked = attack - absorbed;

            _playerBlock = 0;

            if (unblocked > 0)
            {
                _playerHp -= unblocked;
            }

            _deck.DiscardHand();

            if (_playerHp <= 0)
            {
                // 玩家阵亡：战斗已结束，"重抽 5 张 + 能量重置"不再有意义，只保证战斗外读数为 0。
                _inBattle = false;
                _runOver = true;
                _energy = 0;
                return;
            }

            _deck.Draw(RoguelikeRules.HandSize, _random);
            _energy = RoguelikeRules.EnergyPerTurn;
        }

        /// <summary>
        /// 重开整轮 run：HP/金币/卡组/地图进度回到初始态，同种子重放出同一张地图与同一套奖励序列。
        /// <see cref="ProcessedSteps"/> / <see cref="BattlesWon"/> / <see cref="CardsAdded"/> 为累计诊断值，不清零。
        /// 结算报告已取回后为空操作（会话所有权已交还）。
        /// </summary>
        public void Restart()
        {
            if (_resultCollected)
            {
                return;
            }

            ResetRunState();
        }

        // ── 内部流程 ───────────────────────────────────────────────────

        /// <summary>战斗布场：按节点种类定敌人属性，重置能量与格挡，重建并洗好牌堆后抽 5 张。</summary>
        private void BeginBattle(RoguelikeNodeKind kind)
        {
            _battleKind = kind;
            _inBattle = true;
            _playerBlock = 0;
            _enemyMaxHp = RoguelikeRules.EnemyHpFor(kind);
            _enemyHp = _enemyMaxHp;
            _energy = RoguelikeRules.EnergyPerTurn;

            _deck.BeginBattle(_random);
        }

        /// <summary>
        /// 战斗胜利结算：清空手牌与能量 → 发金币 → 随机奖励 1 张牌进卡组 → 胜场与加牌计数 +1；
        /// 若打赢的是第 8 层 Boss，同时把 run 标记为"通关"。
        /// </summary>
        private void ResolveBattleVictory()
        {
            _inBattle = false;
            _playerBlock = 0;
            _energy = 0;
            _deck.DiscardHand();

            _gold += RoguelikeRules.GoldRewardFor(_battleKind);

            _deck.AddCard(RoguelikeCards.Reward(_random.NextInt(RoguelikeCards.RewardCount)));
            _cardsAdded++;
            _battlesWon++;

            if (_battleKind == RoguelikeNodeKind.Boss)
            {
                _runOver = true;
                _victory = true;
            }
        }

        /// <summary>商店：金币 &gt;= 50 花 50 买 1 张随机牌；不购买时金币与卡组都不变。</summary>
        private void ResolveShop()
        {
            if (_gold < RoguelikeRules.ShopPrice)
            {
                return;
            }

            _gold -= RoguelikeRules.ShopPrice;
            _deck.AddCard(RoguelikeCards.Reward(_random.NextInt(RoguelikeCards.RewardCount)));
        }

        /// <summary>营地：回复 20 HP，封顶 <see cref="RoguelikeRules.PlayerMaxHp"/>。</summary>
        private void ResolveRest()
        {
            int healed = _playerHp + RoguelikeRules.RestHealAmount;
            _playerHp = healed > RoguelikeRules.PlayerMaxHp ? RoguelikeRules.PlayerMaxHp : healed;
        }
    }
}
