using System;
using System.IO;
using System.Text;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Ports;
using AIFramework.Kernel.Turn.CardBattle;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="CardBattleWorld"/> 的规则契约：开局布置、xorshift32 确定性、出牌/能量校验、
    /// 敌人行动与护甲吸收、弃牌回流与爆牌、胜负判定与结算单次性、<c>TbCard</c> 子表校验。
    /// <para>
    /// 所有随机相关用例都显式固定 <see cref="GameplayInitData.RandomSeed"/>，不依赖墙钟/时间源；
    /// 需要"手牌里必须出现某类牌"的用例用 <see cref="CreateWorld"/> 在固定种子序列里搜首个满足条件的世界，
    /// 因此断言与具体种子无关，仍然完全可复现。
    /// </para>
    /// </summary>
    public sealed class CardBattleWorldTests
    {
        /// <summary>内置默认牌库：3+3+2+2 = 10 张。</summary>
        private const int DefaultDeckTotal = 10;

        // ── 开局布置 ─────────────────────────────────────────────────────────

        [Fact]
        public void Constructor_OpensWithFiveCardsAndThreeEnergy()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 2024, 2));

            Assert.Equal(1, world.TurnIndex);
            Assert.Equal(3, world.Energy);
            Assert.Equal(5, world.HandCount);
            Assert.Equal(5, world.DrawPileCount);
            Assert.Equal(0, world.DiscardPileCount);
            Assert.Equal(0, world.PlayedCardCount);
            Assert.Equal(0, world.BurnedCount);
            Assert.Equal(30, world.PlayerHp);
            Assert.Equal(0, world.PlayerBlock);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.False(world.IsFinished);
        }

        [Theory]
        [InlineData(0, 10)]
        [InlineData(1, 10)]
        [InlineData(2, 20)]
        [InlineData(5, 50)]
        [InlineData(-3, 10)]
        public void Constructor_EnemyHpIsTenPerUnitWithFloorOfTen(int unitCount, int expectedEnemyHp)
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 7, unitCount));

            Assert.Equal(expectedEnemyHp, world.EnemyHp);
        }

        [Fact]
        public void OpeningHand_ExposesCardViewDataForEveryIndex()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 5, 1));

            for (int i = 0; i < world.HandCount; i++)
            {
                Assert.True(world.TryGetHandCard(i, out CardViewData card));
                Assert.InRange(card.CardId, 1, 4);
                Assert.True(card.Cost >= 0);
                Assert.True(card.Damage >= 0);
                Assert.True(card.Block >= 0);
            }

            Assert.False(world.TryGetHandCard(-1, out _));
            Assert.False(world.TryGetHandCard(world.HandCount, out _));
        }

        // ── 确定性（xorshift32） ─────────────────────────────────────────────

        [Fact]
        public void SameSeed_ProducesIdenticalHandsAndRandomState()
        {
            CardBattleWorld first = new CardBattleWorld(new GameplayInitData(1, 987654, 3));
            CardBattleWorld second = new CardBattleWorld(new GameplayInitData(1, 987654, 3));

            Assert.Equal(first.RandomState, second.RandomState);
            Assert.Equal(Snapshot(first), Snapshot(second));

            // 抽牌与"弃牌洗回抽牌堆"走同一 RNG：连续几回合后序列仍逐张一致（第 2 回合起必然发生回流）。
            for (int turn = 0; turn < 3; turn++)
            {
                int index = IndexOfCard(first, c => c.Cost <= first.Energy);
                if (index >= 0)
                {
                    Assert.True(first.TryPlayCard(index));
                    Assert.True(second.TryPlayCard(IndexOfCard(second, c => c.Cost <= second.Energy)));
                }

                first.EndTurn();
                second.EndTurn();

                Assert.Equal(Snapshot(first), Snapshot(second));
                Assert.Equal(first.DrawPileCount, second.DrawPileCount);
                Assert.Equal(first.DiscardPileCount, second.DiscardPileCount);
                Assert.Equal(first.RandomState, second.RandomState);
            }
        }

        [Fact]
        public void DifferentSeeds_ProduceDifferentOpeningHands()
        {
            CardBattleWorld first = new CardBattleWorld(new GameplayInitData(1, 11111, 3));
            CardBattleWorld second = new CardBattleWorld(new GameplayInitData(1, 22222, 3));

            Assert.NotEqual(Snapshot(first), Snapshot(second));
            Assert.NotEqual(first.RandomState, second.RandomState);
        }

        // ── 出牌 ─────────────────────────────────────────────────────────────

        [Fact]
        public void TryPlayCard_DamageCard_SpendsEnergyAndHurtsEnemy()
        {
            CardBattleWorld world = CreateWorld(w => IndexOfCard(w, c => c.Damage > 0) >= 0);
            int index = IndexOfCard(world, c => c.Damage > 0);
            Assert.True(world.TryGetHandCard(index, out CardViewData card));
            int enemyHp = world.EnemyHp;

            Assert.True(world.TryPlayCard(index));

            Assert.Equal(3 - card.Cost, world.Energy);
            Assert.Equal(enemyHp - card.Damage, world.EnemyHp);
            Assert.Equal(1, world.PlayedCardCount);
        }

        [Fact]
        public void TryPlayCard_BlockCard_MovesToDiscardAndRaisesPlayerBlock()
        {
            // 只挑"纯护甲"牌（小刀是 damage=3 + block=3 的混合牌，会同时扣敌人血）。
            CardBattleWorld world = CreateWorld(w => IndexOfCard(w, c => c.Block > 0 && c.Damage == 0) >= 0);
            int index = IndexOfCard(world, c => c.Block > 0 && c.Damage == 0);
            Assert.True(world.TryGetHandCard(index, out CardViewData card));
            int enemyHp = world.EnemyHp;

            Assert.True(world.TryPlayCard(index));

            Assert.Equal(4, world.HandCount);
            Assert.Equal(1, world.DiscardPileCount);
            Assert.Equal(card.Block, world.PlayerBlock);
            Assert.Equal(enemyHp, world.EnemyHp);
            Assert.Equal(3 - card.Cost, world.Energy);
            Assert.Equal(1, world.PlayedCardCount);
        }

        [Fact]
        public void TryPlayCard_WhenEnergyIsInsufficient_ReturnsFalseWithoutChangingState()
        {
            // 手牌里至少两张 cost >= 2：先打一张把能量压到 1，另一张必然打不动。
            CardBattleWorld world = CreateWorld(w => CountCards(w, c => c.Cost >= 2) >= 2);
            int first = IndexOfCard(world, c => c.Cost >= 2);
            Assert.True(world.TryGetHandCard(first, out CardViewData expensive));
            Assert.True(world.TryPlayCard(first));
            Assert.True(world.Energy < expensive.Cost);

            int target = IndexOfCard(world, c => c.Cost > world.Energy);
            Assert.True(target >= 0);

            int energy = world.Energy;
            int enemyHp = world.EnemyHp;
            int hand = world.HandCount;
            int discard = world.DiscardPileCount;
            int played = world.PlayedCardCount;

            Assert.False(world.TryPlayCard(target));

            Assert.Equal(energy, world.Energy);
            Assert.Equal(enemyHp, world.EnemyHp);
            Assert.Equal(hand, world.HandCount);
            Assert.Equal(discard, world.DiscardPileCount);
            Assert.Equal(played, world.PlayedCardCount);
        }

        [Fact]
        public void TryPlayCard_WithOutOfRangeIndex_ReturnsFalseWithoutChangingState()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 4242, 1));

            Assert.False(world.TryPlayCard(-1));
            Assert.False(world.TryPlayCard(world.HandCount));
            Assert.False(world.TryPlayCard(int.MaxValue));

            Assert.Equal(3, world.Energy);
            Assert.Equal(5, world.HandCount);
            Assert.Equal(0, world.PlayedCardCount);
            Assert.Equal(0, world.DiscardPileCount);
        }

        // ── 回合结算 ─────────────────────────────────────────────────────────

        [Fact]
        public void EndTurn_EnemyAttackIsAbsorbedByBlockBeforeHp()
        {
            CardBattleWorld world = CreateWorld(w => IndexOfCard(w, c => c.Block > 0) >= 0);
            int index = IndexOfCard(world, c => c.Block > 0);
            Assert.True(world.TryPlayCard(index));
            int block = world.PlayerBlock;
            Assert.InRange(block, 1, 5);

            world.EndTurn();

            // 攻击 6 点：护甲先吃满，剩余才进 HP；护甲随后清零。
            Assert.Equal(30 - (6 - block), world.PlayerHp);
            Assert.Equal(0, world.PlayerBlock);
            Assert.Equal(2, world.TurnIndex);
        }

        [Fact]
        public void EndTurn_WithBlockCoveringAttack_KeepsPlayerHp()
        {
            CardBattleWorld world = CreateWorld(w => CountCards(w, c => c.Block >= 5) >= 2);
            Assert.True(world.TryPlayCard(IndexOfCard(world, c => c.Block >= 5)));
            Assert.True(world.TryPlayCard(IndexOfCard(world, c => c.Block >= 5)));
            Assert.True(world.PlayerBlock >= 6);

            world.EndTurn();

            Assert.Equal(30, world.PlayerHp);
            Assert.Equal(0, world.PlayerBlock);
        }

        [Fact]
        public void EndTurn_AdvancesTurnResetsEnergyAndDraws()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 31337, 1000));
            Assert.True(world.TryGetHandCard(0, out CardViewData card));
            Assert.True(world.TryPlayCard(0));
            Assert.Equal(3 - card.Cost, world.Energy);

            world.EndTurn();

            Assert.Equal(2, world.TurnIndex);
            Assert.Equal(3, world.Energy);
            Assert.Equal(9, world.HandCount);
            Assert.Equal(0, world.DrawPileCount);
            Assert.Equal(DefaultDeckTotal, world.HandCount + world.DrawPileCount + world.DiscardPileCount);
        }

        [Fact]
        public void EndTurn_WhenFinished_IsNoOp()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 55, 3));
            for (int i = 0; i < 5; i++)
            {
                world.EndTurn();
            }

            Assert.True(world.IsFinished);
            int turn = world.TurnIndex;
            int hp = world.PlayerHp;
            int hand = world.HandCount;

            world.EndTurn();

            Assert.Equal(turn, world.TurnIndex);
            Assert.Equal(hp, world.PlayerHp);
            Assert.Equal(hand, world.HandCount);
        }

        // ── 牌堆 ─────────────────────────────────────────────────────────────

        [Fact]
        public void DrawPileExhaustion_ReshufflesDiscardAndConservesCards()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 777, 1000));
            int reshuffles = 0;

            for (int turn = 0; turn < 4; turn++)
            {
                for (int plays = 0; plays < 2; plays++)
                {
                    int index = IndexOfCard(world, c => c.Cost <= world.Energy);
                    if (index < 0)
                    {
                        break;
                    }

                    Assert.True(world.TryPlayCard(index));
                }

                int drawBefore = world.DrawPileCount;
                int discardBefore = world.DiscardPileCount;
                world.EndTurn();

                if (drawBefore == 0 && discardBefore > 0)
                {
                    reshuffles++;
                }

                Assert.Equal(
                    DefaultDeckTotal,
                    world.HandCount + world.DrawPileCount + world.DiscardPileCount);
                Assert.False(world.IsFinished);
            }

            Assert.True(reshuffles > 0);
        }

        [Fact]
        public void FullHand_BurnsOverflowCardsIntoDiscard()
        {
            // 12 张牌 > 手牌上限 10：第 2 个回合开始必然爆牌，且爆掉的牌回到弃牌堆（总牌数守恒）。
            CardBattleWorld world = NewWorld(BuildTable(1, 1, 6, 0, 6, 2, 1, 0, 5, 6));
            Assert.Equal(12, world.HandCount + world.DrawPileCount + world.DiscardPileCount);

            world.EndTurn();
            Assert.Equal(10, world.HandCount);
            Assert.Equal(2, world.DrawPileCount);
            Assert.Equal(0, world.BurnedCount);

            world.EndTurn();
            Assert.Equal(10, world.HandCount);
            Assert.Equal(5, world.BurnedCount);
            Assert.Equal(12, world.HandCount + world.DrawPileCount + world.DiscardPileCount);
        }

        // ── 胜负与结算 ───────────────────────────────────────────────────────

        [Fact]
        public void EnemyHpZero_ReportsVictoryWithOneLoot()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 20241118, 1));
            int guard = 0;
            while (!world.IsFinished && guard++ < 60)
            {
                int index = IndexOfCard(world, c => c.Damage > 0 && c.Cost <= world.Energy);
                if (index >= 0)
                {
                    Assert.True(world.TryPlayCard(index));
                }
                else
                {
                    world.EndTurn();
                }
            }

            Assert.True(world.IsFinished);
            Assert.True(world.EnemyHp <= 0);

            GameplayResult result = world.CollectResult();

            Assert.True(result.IsVictory);
            Assert.Equal(GameplayKernelType.CardBattle, result.KernelType);
            Assert.Single(result.Loots);
            Assert.Equal(1001, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Empty(result.TriggeredTags);
            Assert.Equal(world.PlayerHp, result.RemainingUnits);
            Assert.Equal(world.ProcessedSteps, result.StepCount);
        }

        [Fact]
        public void PlayerHpZero_ReportsDefeatWithoutLoot()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 55, 3));
            for (int i = 0; i < 5; i++)
            {
                Assert.False(world.IsFinished);
                world.EndTurn();
            }

            Assert.True(world.IsFinished);
            Assert.Equal(0, world.PlayerHp);
            Assert.Equal(6, world.TurnIndex);

            GameplayResult result = world.CollectResult();

            Assert.False(result.IsVictory);
            Assert.Equal(0, result.RemainingUnits);
            Assert.Empty(result.Loots);
            Assert.Empty(result.TriggeredTags);
        }

        [Fact]
        public void TurnLimitExceeded_ReportsDefeatWhileBothSidesAlive()
        {
            // 敌人 HP = 10000（只打得出几百点伤害）→ 不会误判胜利；全程叠甲/泄牌把回合数推过 30。
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 2024, 1000));
            int guard = 0;

            while (!world.IsFinished && guard++ < 200)
            {
                while (true)
                {
                    int index = BestAffordableIndex(world);
                    if (index < 0 || !world.TryPlayCard(index))
                    {
                        break;
                    }
                }

                if (world.IsFinished)
                {
                    break;
                }

                world.EndTurn();
            }

            Assert.True(world.IsFinished);
            Assert.True(world.PlayerHp > 0);
            Assert.True(world.EnemyHp > 0);
            Assert.Equal(31, world.TurnIndex);

            GameplayResult result = world.CollectResult();

            Assert.False(result.IsVictory);
            Assert.True(result.RemainingUnits > 0);
            Assert.Empty(result.Loots);
        }

        [Fact]
        public void CollectResult_BeforeFinish_Throws()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 1, 2));

            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 9, 1));
            int guard = 0;
            while (!world.IsFinished && guard++ < 60)
            {
                world.EndTurn();
            }

            Assert.False(world.CollectResult().IsVictory);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void ReadView_RemainsReadableAfterFinish()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 9, 1));
            int guard = 0;
            while (!world.IsFinished && guard++ < 60)
            {
                world.EndTurn();
            }

            world.CollectResult();

            Assert.Equal(0, world.PlayerHp);
            Assert.Equal(10, world.EnemyHp);
            Assert.Equal(6, world.TurnIndex);
            Assert.True(world.HandCount > 0);
            Assert.False(world.TryGetHandCard(world.HandCount, out _));
            Assert.False(world.TryPlayCard(0));
            Assert.True(world.IsFinished);
        }

        // ── Step 语义 ────────────────────────────────────────────────────────

        [Fact]
        public void Step_CountsEveryCallAndMatchesResultStepCount()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 8, 1));
            Assert.Equal(0, world.ProcessedSteps);

            world.Step(0.016f);
            world.Step(1f);
            world.Step(-5f);
            Assert.Equal(3, world.ProcessedSteps);

            int guard = 0;
            while (!world.IsFinished && guard++ < 60)
            {
                int index = IndexOfCard(world, c => c.Damage > 0 && c.Cost <= world.Energy);
                if (index >= 0)
                {
                    world.TryPlayCard(index);
                }
                else
                {
                    world.EndTurn();
                }
            }

            Assert.True(world.IsFinished);
            Assert.Equal(3, world.ProcessedSteps);
            Assert.Equal(world.ProcessedSteps, world.CollectResult().StepCount);
        }

        [Fact]
        public void Step_AfterCollect_DoesNotThrowOrAdvance()
        {
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 8, 1));
            int guard = 0;
            while (!world.IsFinished && guard++ < 60)
            {
                int index = IndexOfCard(world, c => c.Damage > 0 && c.Cost <= world.Energy);
                if (index >= 0)
                {
                    world.TryPlayCard(index);
                }
                else
                {
                    world.EndTurn();
                }
            }

            world.Step(1f);
            int steps = world.ProcessedSteps;
            world.CollectResult();

            world.Step(1f);

            Assert.Equal(steps, world.ProcessedSteps);
            Assert.True(world.IsFinished);
        }

        // ── TbCard 子表 ──────────────────────────────────────────────────────

        [Fact]
        public void CardTable_CustomTable_ReplacesDefaultDeck()
        {
            StubTableLoader loader = new StubTableLoader(BuildTable(7, 1, 6, 0, 10));
            CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, 1, 1), loader);

            Assert.Equal("TbCard", loader.RequestedTable);
            Assert.Equal(5, world.HandCount);
            Assert.Equal(5, world.DrawPileCount);
            for (int i = 0; i < world.HandCount; i++)
            {
                Assert.True(world.TryGetHandCard(i, out CardViewData card));
                Assert.Equal(7, card.CardId);
                Assert.Equal(1, card.Cost);
                Assert.Equal(6, card.Damage);
                Assert.Equal(0, card.Block);
            }
        }

        [Fact]
        public void CardTable_MissingTable_ThrowsInvalidOperation()
        {
            StubTableLoader loader = new StubTableLoader(null);

            Assert.Throws<InvalidOperationException>(
                () => new CardBattleWorld(new GameplayInitData(1, 1, 1), loader));
        }

        [Fact]
        public void CardTable_ZeroRows_ThrowsInvalidOperation()
        {
            Assert.Throws<InvalidOperationException>(() => NewWorld(BuildTable()));
        }

        [Fact]
        public void CardTable_LengthMismatch_ThrowsInvalidData()
        {
            byte[] truncatedHeader = new byte[2];
            Assert.Throws<InvalidDataException>(() => NewWorld(truncatedHeader));

            byte[] declaresTwoRows = new byte[24];
            WriteInt32(declaresTwoRows, 0, 2);
            Assert.Throws<InvalidDataException>(() => NewWorld(declaresTwoRows));
        }

        [Fact]
        public void CardTable_InvalidRows_ThrowInvalidData()
        {
            // cardId 重复
            Assert.Throws<InvalidDataException>(() => NewWorld(BuildTable(1, 1, 6, 0, 1, 1, 1, 0, 5, 1)));

            // cost < 0
            Assert.Throws<InvalidDataException>(() => NewWorld(BuildTable(1, -1, 6, 0, 1)));

            // copies <= 0
            Assert.Throws<InvalidDataException>(() => NewWorld(BuildTable(1, 1, 6, 0, 0)));
        }

        // ── 测试辅助 ─────────────────────────────────────────────────────────

        /// <summary>在固定种子序列里找首个满足前置条件的世界；断言因此与具体种子无关。</summary>
        private static CardBattleWorld CreateWorld(Func<CardBattleWorld, bool> predicate)
        {
            for (int seed = 1; seed <= 512; seed++)
            {
                CardBattleWorld world = new CardBattleWorld(new GameplayInitData(1, seed, 3));
                if (predicate(world))
                {
                    return world;
                }
            }

            throw new InvalidOperationException("在 1..512 的种子里找不到满足前置条件的开局手牌。");
        }

        private static int IndexOfCard(CardBattleWorld world, Func<CardViewData, bool> predicate)
        {
            for (int i = 0; i < world.HandCount; i++)
            {
                if (world.TryGetHandCard(i, out CardViewData card) && predicate(card))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int CountCards(CardBattleWorld world, Func<CardViewData, bool> predicate)
        {
            int count = 0;
            for (int i = 0; i < world.HandCount; i++)
            {
                if (world.TryGetHandCard(i, out CardViewData card) && predicate(card))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>可打出的牌里优先叠甲、其次出伤（回合上限用例的"泄牌"策略）。</summary>
        private static int BestAffordableIndex(CardBattleWorld world)
        {
            int best = -1;
            int bestScore = int.MinValue;
            for (int i = 0; i < world.HandCount; i++)
            {
                if (!world.TryGetHandCard(i, out CardViewData card) || card.Cost > world.Energy)
                {
                    continue;
                }

                int score = (card.Block * 100) + card.Damage;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return best;
        }

        private static string Snapshot(CardBattleWorld world)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < world.HandCount; i++)
            {
                world.TryGetHandCard(i, out CardViewData card);
                builder.Append(card.CardId).Append(':').Append(card.Cost).Append(',');
            }

            return builder.ToString();
        }

        private static CardBattleWorld NewWorld(byte[] payload)
        {
            return new CardBattleWorld(new GameplayInitData(1, 1, 1), new StubTableLoader(payload));
        }

        /// <summary>按 TbCard 布局把小端行字段序列化为 payload（5 个 int 一行）。</summary>
        private static byte[] BuildTable(params int[] fields)
        {
            byte[] payload = new byte[4 + (fields.Length * 4)];
            WriteInt32(payload, 0, fields.Length / 5);
            for (int i = 0; i < fields.Length; i++)
            {
                WriteInt32(payload, 4 + (i * 4), fields[i]);
            }

            return payload;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private sealed class StubTableLoader : IBinaryTableLoader
        {
            private readonly byte[]? _payload;

            public StubTableLoader(byte[]? payload)
            {
                _payload = payload;
            }

            public string? RequestedTable { get; private set; }

            public byte[]? Load(string tableName)
            {
                RequestedTable = tableName;
                return _payload;
            }
        }
    }
}
