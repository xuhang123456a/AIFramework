using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Roguelike;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="RoguelikeWorld"/> 的肉鸽卡牌契约：8 层线性地图与保底节点、初始读数、前进/出牌/结束回合的准入与拒绝、
    /// 战斗胜负与奖励、商店购买、营地回血、Boss 通关战利品、<c>Step</c> 计数、<c>Restart</c> 复位与
    /// 累计计数保留、同种子复现，以及热路径零分配。
    /// <para>
    /// 用例全部走公开 API（<see cref="IRoguelikeReadView"/> / <see cref="IRoguelikeInput"/> / <see cref="IGameplaySession"/>），
    /// 不反射内核内部数组。
    /// </para>
    /// <para>
    /// <b>固定种子</b>：地图由种子决定，因此下面的场景用例钉死了三个"形状已知"的种子
    /// （见 <see cref="SeedBattleEliteRestEliteShop"/> 等常量与 <see cref="Map_KnownSeeds_MatchPinnedShape"/>）。
    /// 种子一旦换算法，这些用例会立刻失败并指出地图形状变了——这是刻意的，地图形状是可选口径的公开契约。
    /// </para>
    /// </summary>
    public sealed class RoguelikeTests
    {
        /// <summary>种子 1：Battle → Elite → Rest → Elite → Shop → Battle → Battle → Boss。</summary>
        private const int SeedBattleEliteRestEliteShop = 1;

        /// <summary>种子 2：Battle → Shop(金币不足) → Rest → Elite → Battle → Battle → Battle → Boss。</summary>
        private const int SeedEarlyShop = 2;

        /// <summary>种子 11：Battle → Elite → Rest → Rest → Elite → Shop → Rest → Boss。</summary>
        private const int SeedDoubleRest = 11;

        private const int StarterDeckSize = 5;

        private const int PlayerMaxHp = 50;

        private const int HandSize = 5;

        private const int EnergyPerTurn = 3;

        private const int ShopPrice = 50;

        private const int RestHeal = 20;

        // 地图形状断言用的种类简写（只关心种类，不关心具体枚举值）。
        private const RoguelikeNodeKind Battle = RoguelikeNodeKind.Battle;

        private const RoguelikeNodeKind Elite = RoguelikeNodeKind.Elite;

        private const RoguelikeNodeKind Shop = RoguelikeNodeKind.Shop;

        private const RoguelikeNodeKind Rest = RoguelikeNodeKind.Rest;

        private const RoguelikeNodeKind Boss = RoguelikeNodeKind.Boss;

        // ── 初始读数与地图 ─────────────────────────────────────────────

        [Fact]
        public void Constructor_ExposesInitialReadings()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            Assert.Equal(8, world.NodeCount);
            Assert.Equal(-1, world.CurrentNodeIndex);
            Assert.Equal(0, world.Depth);
            Assert.Equal(PlayerMaxHp, world.PlayerHp);
            Assert.Equal(PlayerMaxHp, world.PlayerMaxHp);
            Assert.Equal(0, world.Gold);
            Assert.Equal(StarterDeckSize, world.DeckSize);
            Assert.Equal(0, world.HandCount);
            Assert.Equal(0, world.Energy);
            Assert.Equal(0, world.EnemyHp);
            Assert.Equal(0, world.EnemyMaxHp);
            Assert.False(world.InBattle);
            Assert.False(world.IsRunOver);
            Assert.False(world.IsVictory);
            Assert.False(world.IsFinished);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(0, world.BattlesWon);
            Assert.Equal(0, world.CardsAdded);

            // 开局还没进节点：任何手牌访问都必须越界而不是返回默认牌。
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetHandCard(0));
        }

        [Fact]
        public void Map_AcrossSeeds_IsBattleFirstBossLastAndKeepsRestPlusShopInMiddle()
        {
            for (int seed = 1; seed <= 64; seed++)
            {
                RoguelikeWorld world = CreateWorld(seed);

                Assert.Equal(RoguelikeNodeKind.Battle, world.GetNode(0).Kind);
                Assert.Equal(RoguelikeNodeKind.Boss, world.GetNode(world.NodeCount - 1).Kind);

                int restCount = 0;
                int shopCount = 0;
                for (int i = 1; i <= world.NodeCount - 2; i++)
                {
                    RoguelikeNodeView node = world.GetNode(i);
                    Assert.Equal(i, node.NodeId);
                    Assert.Equal(i + 1, node.Depth);
                    Assert.False(node.Visited);
                    Assert.NotEqual(RoguelikeNodeKind.Boss, node.Kind);

                    if (node.Kind == RoguelikeNodeKind.Rest)
                    {
                        restCount++;
                    }
                    else if (node.Kind == RoguelikeNodeKind.Shop)
                    {
                        shopCount++;
                    }
                }

                Assert.True(restCount >= 1, "种子 " + seed + " 的中段没有 Rest。");
                Assert.True(shopCount >= 1, "种子 " + seed + " 的中段没有 Shop。");
            }
        }

        [Fact]
        public void Map_KnownSeeds_MatchPinnedShape()
        {
            // 这三个形状是后续场景用例的前提，钉死它们等于同时钉死"地图流"的确定性。
            AssertKinds(CreateWorld(SeedBattleEliteRestEliteShop), Battle, Elite, Rest, Elite, Shop, Battle, Battle, Boss);
            AssertKinds(CreateWorld(SeedEarlyShop), Battle, Shop, Rest, Elite, Battle, Battle, Battle, Boss);
            AssertKinds(CreateWorld(SeedDoubleRest), Battle, Elite, Rest, Rest, Elite, Shop, Rest, Boss);
        }

        // ── 节点推进 ───────────────────────────────────────────────────

        [Fact]
        public void TryEnterNextNode_StartsFirstBattle_AndRejectsSecondEntryWhileInBattle()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            Assert.True(world.TryEnterNextNode());

            Assert.Equal(0, world.CurrentNodeIndex);
            Assert.Equal(1, world.Depth);
            Assert.True(world.GetNode(0).Visited);
            Assert.False(world.GetNode(1).Visited);
            Assert.True(world.InBattle);
            Assert.Equal(20, world.EnemyHp);
            Assert.Equal(20, world.EnemyMaxHp);
            Assert.Equal(EnergyPerTurn, world.Energy);
            Assert.Equal(HandSize, world.HandCount);
            Assert.Equal(StarterDeckSize, world.DeckSize);
            Assert.False(world.IsRunOver);

            // 战斗中重复进入必须被拒：不前进、不换敌人、不重抽。
            Assert.False(world.TryEnterNextNode());
            Assert.Equal(0, world.CurrentNodeIndex);
            Assert.Equal(1, world.Depth);
            Assert.Equal(20, world.EnemyHp);
        }

        [Fact]
        public void TryEnterNextNode_AdvancesOneLayerPerCall()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            for (int expected = 0; expected < world.NodeCount; expected++)
            {
                Assert.Equal(expected, world.CurrentNodeIndex + 1);

                Assert.True(world.TryEnterNextNode());
                Assert.Equal(expected, world.CurrentNodeIndex);
                Assert.Equal(expected + 1, world.Depth);
                Assert.True(world.GetNode(expected).Visited);

                if (world.InBattle)
                {
                    WinBattle(world);
                }
            }

            // 走完第 8 层 Boss 即通关，之后不能再"进入下一层"。
            Assert.True(world.IsRunOver);
            Assert.True(world.IsVictory);
            Assert.False(world.TryEnterNextNode());
            Assert.Equal(7, world.CurrentNodeIndex);
        }

        // ── 出牌 ───────────────────────────────────────────────────────

        [Fact]
        public void TryPlayCard_AttackCard_DealsDamageSpendsEnergyAndShrinksHand()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());

            int attackIndex = FindAttackCard(world);
            Assert.True(attackIndex >= 0, "初始手牌必然含攻击牌。");

            RoguelikeCardView card = world.GetHandCard(attackIndex);
            int enemyHpBefore = world.EnemyHp;
            int handBefore = world.HandCount;

            Assert.True(world.TryPlayCard(attackIndex));

            Assert.Equal(enemyHpBefore - card.Damage, world.EnemyHp);
            Assert.Equal(EnergyPerTurn - card.Cost, world.Energy);
            Assert.Equal(handBefore - 1, world.HandCount);
            Assert.True(world.InBattle);

            // 打出的牌不再出现在手牌里（同名牌可能还有，但张数必然少一张）。
            for (int i = 0; i < world.HandCount; i++)
            {
                Assert.True(world.GetHandCard(i).CardId > 0);
            }
        }

        [Fact]
        public void TryPlayCard_BlockCard_AbsorbsPartOfEnemyAttack()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());

            int blockIndex = FindBlockCard(world);
            Assert.True(blockIndex >= 0, "初始手牌必然含格挡牌。");
            int block = world.GetHandCard(blockIndex).Block;
            Assert.True(block > 0);

            Assert.True(world.TryPlayCard(blockIndex));

            int hpBefore = world.PlayerHp;
            world.EndBattleTurn();

            // 普通战斗敌人攻击 6：格挡 5 吸收后只剩 1 点穿透。
            int expected = 6 - block;
            Assert.Equal(hpBefore - expected, world.PlayerHp);
        }

        [Fact]
        public void TryPlayCard_TwoBlockCards_FullyAbsorbEnemyAttack()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());

            int first = FindBlockCard(world);
            Assert.True(world.TryPlayCard(first));
            int second = FindBlockCard(world);
            Assert.True(second >= 0, "初始卡组有两张防御。");
            Assert.True(world.TryPlayCard(second));

            int hpBefore = world.PlayerHp;
            world.EndBattleTurn();

            // 2 × 5 = 10 点格挡 > 6 点攻击：完全不掉血。
            Assert.Equal(hpBefore, world.PlayerHp);
        }

        [Fact]
        public void TryPlayCard_RejectsOutOfRangeAndInsufficientEnergy_WithoutChangingState()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            // 不在战斗中：任何索引都拒绝。
            Assert.False(world.TryPlayCard(0));

            Assert.True(world.TryEnterNextNode());

            int energyBefore = world.Energy;
            int enemyHpBefore = world.EnemyHp;
            int handBefore = world.HandCount;

            Assert.False(world.TryPlayCard(-1));
            Assert.False(world.TryPlayCard(world.HandCount));
            Assert.False(world.TryPlayCard(99));

            Assert.Equal(energyBefore, world.Energy);
            Assert.Equal(enemyHpBefore, world.EnemyHp);
            Assert.Equal(handBefore, world.HandCount);

            // 把能量打到 0：剩下的 1 费牌必须因能量不足被拒。
            while (world.Energy > 0)
            {
                int index = FindAnyAffordableCard(world);
                Assert.True(index >= 0, "能量未耗尽前必然还有可打的牌。");
                Assert.True(world.TryPlayCard(index));
            }

            int handAtZeroEnergy = world.HandCount;
            int energyAtZero = world.Energy;
            Assert.Equal(0, energyAtZero);

            for (int i = 0; i < world.HandCount; i++)
            {
                if (world.GetHandCard(i).Cost > 0)
                {
                    Assert.False(world.TryPlayCard(i));
                }
            }

            Assert.Equal(handAtZeroEnergy, world.HandCount);
            Assert.Equal(0, world.Energy);
        }

        // ── 结束回合 ───────────────────────────────────────────────────

        [Fact]
        public void EndBattleTurn_EnemyAttacksThenRedrawsHandAndResetsEnergy()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());

            int attackIndex = FindAttackCard(world);
            Assert.True(world.TryPlayCard(attackIndex));

            int hpBefore = world.PlayerHp;
            int enemyHpBefore = world.EnemyHp;

            world.EndBattleTurn();

            // 没打格挡牌：普通战斗敌人攻击 6 全额命中。
            Assert.Equal(hpBefore - 6, world.PlayerHp);
            Assert.Equal(enemyHpBefore, world.EnemyHp);
            Assert.Equal(EnergyPerTurn, world.Energy);
            Assert.Equal(HandSize, world.HandCount);
            Assert.True(world.InBattle);
            Assert.False(world.IsRunOver);
        }

        [Fact]
        public void EndBattleTurn_OutsideBattle_IsNoOp()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            world.EndBattleTurn();

            Assert.Equal(PlayerMaxHp, world.PlayerHp);
            Assert.Equal(0, world.HandCount);
            Assert.Equal(0, world.Energy);
        }

        // ── 战斗奖励与胜负 ─────────────────────────────────────────────

        [Fact]
        public void BattleVictory_AwardsGoldAndRewardCard_AndCountsStats()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());

            WinBattle(world);

            Assert.False(world.InBattle);
            Assert.False(world.IsRunOver);
            Assert.True(world.EnemyHp <= 0);
            Assert.Equal(0, world.HandCount);
            Assert.Equal(0, world.Energy);
            Assert.Equal(15, world.Gold);
            Assert.Equal(StarterDeckSize + 1, world.DeckSize);
            Assert.Equal(1, world.CardsAdded);
            Assert.Equal(1, world.BattlesWon);
        }

        [Fact]
        public void EliteBattle_UsesEliteStatsAndPaysEliteGold()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            // 第 1 层普通战斗先打赢，再进第 2 层精英。
            Assert.True(world.TryEnterNextNode());
            WinBattle(world);
            Assert.True(world.TryEnterNextNode());

            Assert.Equal(RoguelikeNodeKind.Elite, world.GetNode(1).Kind);
            Assert.Equal(32, world.EnemyMaxHp);
            Assert.Equal(32, world.EnemyHp);

            WinBattle(world);

            // 15（普通）+ 25（精英）。
            Assert.Equal(40, world.Gold);
            Assert.Equal(StarterDeckSize + 2, world.DeckSize);
            Assert.Equal(2, world.CardsAdded);
            Assert.Equal(2, world.BattlesWon);
        }

        [Fact]
        public void BattleDefeat_EndsRunWithoutVictory()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());

            // 一张牌都不打：敌人每回合 6 点，玩家 50 血必然被打死（9 回合）。
            for (int i = 0; i < 20 && !world.IsRunOver; i++)
            {
                world.EndBattleTurn();
            }

            Assert.True(world.IsRunOver);
            Assert.False(world.IsVictory);
            Assert.False(world.InBattle);
            Assert.True(world.PlayerHp <= 0);
            Assert.Equal(0, world.HandCount);
            Assert.Equal(0, world.Energy);
            Assert.Equal(0, world.BattlesWon);
            Assert.Equal(0, world.Gold);
            Assert.True(world.IsFinished);

            // run 结束后一切输入都失效。
            Assert.False(world.TryEnterNextNode());
            Assert.False(world.TryPlayCard(0));
            int hp = world.PlayerHp;
            world.EndBattleTurn();
            Assert.Equal(hp, world.PlayerHp);

            GameplayResult result = world.CollectResult();
            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
            Assert.Equal(0, result.RemainingUnits);
            Assert.Empty(result.TriggeredTags);
        }

        // ── 商店与营地 ─────────────────────────────────────────────────

        [Fact]
        public void Shop_WithEnoughGold_BuysOneRandomCard()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            // 清到第 4 层（index 3），此时累计金币 65、卡组 8 张。
            ClearThrough(world, 3);
            int goldBefore = world.Gold;
            int deckBefore = world.DeckSize;
            int cardsAddedBefore = world.CardsAdded;

            Assert.True(goldBefore >= ShopPrice);

            Assert.True(world.TryEnterNextNode());

            Assert.Equal(RoguelikeNodeKind.Shop, world.GetNode(4).Kind);
            Assert.Equal(goldBefore - ShopPrice, world.Gold);
            Assert.Equal(deckBefore + 1, world.DeckSize);

            // 商店购买只改卡组，不计入"战斗奖励"计数。
            Assert.Equal(cardsAddedBefore, world.CardsAdded);
            Assert.False(world.InBattle);

            // 买到的牌下一场战斗就会参战（卡组 +1 后抽牌堆同步 +1）。
            Assert.True(world.TryEnterNextNode());
            Assert.True(world.InBattle);
            Assert.Equal(deckBefore + 1, world.DeckSize);
        }

        [Fact]
        public void Shop_WithoutEnoughGold_SkipsPurchase()
        {
            RoguelikeWorld world = CreateWorld(SeedEarlyShop);

            Assert.True(world.TryEnterNextNode());
            WinBattle(world);
            Assert.Equal(15, world.Gold);

            int deckBefore = world.DeckSize;
            Assert.True(world.TryEnterNextNode());

            Assert.Equal(RoguelikeNodeKind.Shop, world.GetNode(1).Kind);
            Assert.Equal(15, world.Gold);
            Assert.Equal(deckBefore, world.DeckSize);
            Assert.False(world.InBattle);
        }

        [Fact]
        public void Rest_HealsTwentyHp_AndDoesNotOverheal()
        {
            // 种子 1：第 3 层营地，进营地前 35 血 → 上限封顶到 50。
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            ClearThrough(world, 1);

            int hpBeforeRest = world.PlayerHp;
            Assert.True(hpBeforeRest < PlayerMaxHp);
            Assert.True(hpBeforeRest + RestHeal > PlayerMaxHp);

            Assert.True(world.TryEnterNextNode());

            Assert.Equal(RoguelikeNodeKind.Rest, world.GetNode(2).Kind);
            Assert.Equal(PlayerMaxHp, world.PlayerHp);
            Assert.False(world.InBattle);

            // 种子 11：第 3、4 层连续两个营地，第二个进营地时已经满血 → 不再加血。
            RoguelikeWorld overheal = CreateWorld(SeedDoubleRest);
            ClearThrough(overheal, 2);
            Assert.Equal(PlayerMaxHp, overheal.PlayerHp);

            Assert.True(overheal.TryEnterNextNode());
            Assert.Equal(RoguelikeNodeKind.Rest, overheal.GetNode(3).Kind);
            Assert.Equal(PlayerMaxHp, overheal.PlayerHp);
        }

        // ── Boss 通关与结算 ────────────────────────────────────────────

        [Fact]
        public void BossClear_SetsVictoryAndCollectsBossLoot()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            for (int i = 0; i < 5; i++)
            {
                world.Step(1f / 60f);
            }

            ClearThrough(world, 6);
            Assert.False(world.IsRunOver);

            Assert.True(world.TryEnterNextNode());
            Assert.Equal(7, world.CurrentNodeIndex);
            Assert.Equal(8, world.Depth);
            Assert.Equal(RoguelikeNodeKind.Boss, world.GetNode(7).Kind);
            Assert.Equal(60, world.EnemyMaxHp);
            Assert.Equal(60, world.EnemyHp);
            Assert.Equal(EnergyPerTurn, world.Energy);
            Assert.Equal(HandSize, world.HandCount);

            WinBattle(world);

            Assert.True(world.IsRunOver);
            Assert.True(world.IsVictory);
            Assert.True(world.IsFinished);
            Assert.False(world.InBattle);
            Assert.True(world.BattlesWon >= 1);
            Assert.False(world.TryEnterNextNode());

            int battlesWon = world.BattlesWon;
            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(5, result.StepCount);
            Assert.Equal(battlesWon, result.RemainingUnits);
            Assert.Empty(result.TriggeredTags);

            Assert.Equal(2, result.Loots.Count);
            Assert.Equal(1003, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Equal(1001, result.Loots[1].ItemId);
            Assert.Equal(50, result.Loots[1].Count);
        }

        [Fact]
        public void CollectResult_BeforeRunOver_Throws()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            // 打到一半（还在战斗中）同样不能结算。
            Assert.True(world.TryEnterNextNode());
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());

            // 一张牌都不打，等敌人把玩家打死，run 进入结束态。
            for (int i = 0; i < 20 && !world.IsRunOver; i++)
            {
                world.EndBattleTurn();
            }

            Assert.True(world.IsRunOver);
            Assert.NotNull(world.CollectResult());
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_ThenLocksInputsStepAndRestart()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);
            Assert.True(world.TryEnterNextNode());
            for (int i = 0; i < 20 && !world.IsRunOver; i++)
            {
                world.EndBattleTurn();
            }

            Assert.True(world.IsRunOver);
            int hpAtDeath = world.PlayerHp;
            int deckAtDeath = world.DeckSize;
            int goldAtDeath = world.Gold;

            world.Step(1f / 60f);
            int stepsBefore = world.ProcessedSteps;

            GameplayResult result = world.CollectResult();
            Assert.False(result.IsVictory);

            Assert.True(world.IsFinished);
            Assert.False(world.TryEnterNextNode());
            Assert.False(world.TryPlayCard(0));

            world.Step(1f / 60f);
            Assert.Equal(stepsBefore, world.ProcessedSteps);

            // 结算已取回 = 会话所有权已交还，Restart 不得再改状态。
            world.Restart();
            Assert.True(world.IsRunOver);
            Assert.False(world.IsVictory);
            Assert.True(world.IsFinished);
            Assert.Equal(hpAtDeath, world.PlayerHp);
            Assert.Equal(deckAtDeath, world.DeckSize);
            Assert.Equal(goldAtDeath, world.Gold);
        }

        // ── Step ───────────────────────────────────────────────────────

        [Fact]
        public void Step_CountsCalls_AndStopsAfterRunOver()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            Assert.True(world.TryEnterNextNode());

            for (int i = 0; i < 5; i++)
            {
                world.Step(1f / 60f);
            }

            Assert.Equal(5, world.ProcessedSteps);
            Assert.True(world.InBattle);

            // 打到输：结束后 Step 不再累加，StepCount 冻结在 5。
            for (int i = 0; i < 20 && !world.IsRunOver; i++)
            {
                world.EndBattleTurn();
            }

            Assert.True(world.IsRunOver);

            world.Step(1f / 60f);
            world.Step(1f / 60f);
            Assert.Equal(5, world.ProcessedSteps);
            Assert.Equal(5, world.CollectResult().StepCount);
        }

        // ── 确定性 ─────────────────────────────────────────────────────

        [Fact]
        public void SameSeed_ProducesIdenticalMapAndHand()
        {
            RoguelikeWorld first = CreateWorld(SeedBattleEliteRestEliteShop);
            RoguelikeWorld second = CreateWorld(SeedBattleEliteRestEliteShop);

            for (int i = 0; i < first.NodeCount; i++)
            {
                RoguelikeNodeView a = first.GetNode(i);
                RoguelikeNodeView b = second.GetNode(i);
                Assert.Equal(a.NodeId, b.NodeId);
                Assert.Equal(a.Depth, b.Depth);
                Assert.Equal(a.Kind, b.Kind);
                Assert.Equal(a.Visited, b.Visited);
            }

            Assert.True(first.TryEnterNextNode());
            Assert.True(second.TryEnterNextNode());
            Assert.Equal(first.HandCount, second.HandCount);

            for (int i = 0; i < first.HandCount; i++)
            {
                RoguelikeCardView a = first.GetHandCard(i);
                RoguelikeCardView b = second.GetHandCard(i);
                Assert.Equal(a.CardId, b.CardId);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.Cost, b.Cost);
                Assert.Equal(a.Damage, b.Damage);
                Assert.Equal(a.Block, b.Block);
                Assert.Equal(a.InDeck, b.InDeck);
            }

            // 同一串操作后状态仍然逐字段一致（洗牌/奖励都吃同一颗种子）。
            WinBattle(first);
            WinBattle(second);
            Assert.Equal(first.Gold, second.Gold);
            Assert.Equal(first.DeckSize, second.DeckSize);
            Assert.Equal(first.PlayerHp, second.PlayerHp);
        }

        [Fact]
        public void DifferentSeeds_ProduceDifferentMaps()
        {
            RoguelikeWorld first = CreateWorld(SeedBattleEliteRestEliteShop);
            RoguelikeWorld second = CreateWorld(SeedEarlyShop);

            int differences = 0;
            for (int i = 0; i < first.NodeCount; i++)
            {
                if (first.GetNode(i).Kind != second.GetNode(i).Kind)
                {
                    differences++;
                }
            }

            Assert.True(differences > 0, "不同种子必须产出不同地图。");

            // 两个固定种子的形状差异已由 Map_KnownSeeds_MatchPinnedShape 钉死，这里只确认"确实不同"。
            Assert.NotEqual(first.GetNode(1).Kind, second.GetNode(1).Kind);
        }

        // ── Restart ────────────────────────────────────────────────────

        [Fact]
        public void Restart_ResetsRun_ButKeepsCumulativeCounters()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            world.Step(1f / 60f);
            world.Step(1f / 60f);
            world.Step(1f / 60f);

            Assert.True(world.TryEnterNextNode());
            WinBattle(world);

            int hpAfterBattle = world.PlayerHp;
            Assert.NotEqual(PlayerMaxHp, hpAfterBattle);
            Assert.Equal(1, world.BattlesWon);
            Assert.Equal(1, world.CardsAdded);
            Assert.Equal(3, world.ProcessedSteps);

            world.Restart();

            Assert.Equal(PlayerMaxHp, world.PlayerHp);
            Assert.Equal(0, world.Gold);
            Assert.Equal(StarterDeckSize, world.DeckSize);
            Assert.Equal(-1, world.CurrentNodeIndex);
            Assert.Equal(0, world.Depth);
            Assert.False(world.InBattle);
            Assert.False(world.IsRunOver);
            Assert.False(world.IsVictory);
            Assert.Equal(0, world.Energy);
            Assert.Equal(0, world.EnemyHp);
            Assert.Equal(0, world.EnemyMaxHp);
            Assert.Equal(0, world.HandCount);

            // 累计值必须保留。
            Assert.Equal(1, world.BattlesWon);
            Assert.Equal(1, world.CardsAdded);
            Assert.Equal(3, world.ProcessedSteps);

            // 地图回退到"未访问"且形状不变（同种子重放）。
            for (int i = 0; i < world.NodeCount; i++)
            {
                RoguelikeNodeView node = world.GetNode(i);
                Assert.False(node.Visited);
                Assert.Equal(i, node.NodeId);
            }

            Assert.Equal(RoguelikeNodeKind.Elite, world.GetNode(1).Kind);

            // 重开后能从头再打一遍。
            Assert.True(world.TryEnterNextNode());
            Assert.Equal(0, world.CurrentNodeIndex);
            Assert.True(world.InBattle);
            Assert.Equal(20, world.EnemyMaxHp);
            Assert.Equal(StarterDeckSize, world.DeckSize);

            WinBattle(world);
            Assert.Equal(2, world.BattlesWon);
            Assert.Equal(2, world.CardsAdded);
        }

        // ── 边界 ───────────────────────────────────────────────────────

        [Fact]
        public void GetNode_AndGetHandCard_OutOfRange_Throw()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetNode(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetNode(world.NodeCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetHandCard(-1));

            Assert.True(world.TryEnterNextNode());
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetHandCard(world.HandCount));
        }

        // ── 零分配 ─────────────────────────────────────────────────────

        [Fact]
        public void HotPath_AfterWarmup_DoesNotAllocate()
        {
            RoguelikeWorld world = CreateWorld(SeedBattleEliteRestEliteShop);

            // 预热：跑完整轮 + 重开，覆盖地图生成、洗牌、奖励、商店、营地、Boss 与判定分支。
            for (int i = 0; i < 3; i++)
            {
                RunToEnd(world);
                world.Restart();
            }

            // 1) 活跃路径：重开 → 进战斗 → 结束回合，各 1000 次。
            long activeBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.Restart();
                world.TryEnterNextNode();
                world.EndBattleTurn();
            }

            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - activeBefore);

            // 2) 结束后空操作路径：把玩家打死后，两个入口各 1000 次。
            world.Restart();
            for (int i = 0; i < 40 && !world.IsRunOver; i++)
            {
                world.TryEnterNextNode();
                world.EndBattleTurn();
            }

            Assert.True(world.IsRunOver);

            long idleBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.TryEnterNextNode();
                world.EndBattleTurn();
            }

            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - idleBefore);
        }

        // ── 辅助 ───────────────────────────────────────────────────────

        private static RoguelikeWorld CreateWorld(int seed)
        {
            return new RoguelikeWorld(new GameplayInitData(1, seed, 0));
        }

        /// <summary>逐层断言地图形状（从左到右 8 个节点）。</summary>
        private static void AssertKinds(RoguelikeWorld world, params RoguelikeNodeKind[] expected)
        {
            Assert.Equal(expected.Length, world.NodeCount);

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], world.GetNode(i).Kind);
            }
        }

        /// <summary>
        /// 一路推进到 index 为 <paramref name="targetIndex"/> 的节点之后（该节点若是战斗则打到结束），
        /// 返回时保证"停在目标节点、不在战斗中"。
        /// </summary>
        private static void ClearThrough(RoguelikeWorld world, int targetIndex)
        {
            while (world.CurrentNodeIndex < targetIndex)
            {
                Assert.True(world.TryEnterNextNode(), "推进地图失败：下一层不可进入。");
                if (world.InBattle)
                {
                    WinBattle(world);
                }
            }

            Assert.Equal(targetIndex, world.CurrentNodeIndex);
            Assert.False(world.InBattle);
        }

        /// <summary>打到 run 结束（输或通关）；战斗全走贪心出牌。</summary>
        private static void RunToEnd(RoguelikeWorld world)
        {
            int guard = 0;
            while (!world.IsRunOver)
            {
                Assert.True(guard < 32, "run 未在保底层数内结束。");
                guard++;

                if (world.InBattle)
                {
                    WinBattle(world);
                    continue;
                }

                if (!world.TryEnterNextNode())
                {
                    break;
                }
            }
        }

        /// <summary>把当前战斗打到结束（胜负都可能）；结束时 <see cref="RoguelikeWorld.InBattle"/> 必为 false。</summary>
        private static void WinBattle(RoguelikeWorld world)
        {
            int guard = 0;
            while (world.InBattle && !world.IsRunOver)
            {
                Assert.True(guard < 300, "战斗未在保底回合内结束。");
                guard++;

                PlayAffordableCards(world);

                if (!world.InBattle || world.IsRunOver)
                {
                    return;
                }

                world.EndBattleTurn();
            }
        }

        /// <summary>
        /// 贪心出牌：优先打"付得起的最高伤害攻击牌"（同伤害取低费），
        /// 一张攻击牌都打不动时才用格挡牌，都打不动就停手等结束回合。
        /// </summary>
        private static void PlayAffordableCards(RoguelikeWorld world)
        {
            // 出牌打死敌人的瞬间战斗即结算（InBattle 变 false），此时手牌还在但不能再打。
            while (world.InBattle)
            {
                int index = FindAttackCard(world);
                if (index < 0)
                {
                    index = FindBlockCard(world);
                }

                if (index < 0)
                {
                    return;
                }

                Assert.True(world.TryPlayCard(index), "付得起的手牌必须被打出。");
            }
        }

        /// <summary>付得起的攻击牌里伤害最高的那张；同伤害取低费。没有则返回 -1。</summary>
        private static int FindAttackCard(RoguelikeWorld world)
        {
            int best = -1;
            int bestDamage = 0;
            int bestCost = 0;

            for (int i = 0; i < world.HandCount; i++)
            {
                RoguelikeCardView card = world.GetHandCard(i);
                if (card.Damage <= 0 || card.Cost > world.Energy)
                {
                    continue;
                }

                bool better = best < 0
                    || card.Damage > bestDamage
                    || (card.Damage == bestDamage && card.Cost < bestCost);

                if (better)
                {
                    best = i;
                    bestDamage = card.Damage;
                    bestCost = card.Cost;
                }
            }

            return best;
        }

        /// <summary>付得起的格挡牌里格挡最高的那张；没有则返回 -1。</summary>
        private static int FindBlockCard(RoguelikeWorld world)
        {
            int best = -1;
            int bestBlock = 0;

            for (int i = 0; i < world.HandCount; i++)
            {
                RoguelikeCardView card = world.GetHandCard(i);
                if (card.Block <= 0 || card.Cost > world.Energy)
                {
                    continue;
                }

                if (card.Block > bestBlock)
                {
                    best = i;
                    bestBlock = card.Block;
                }
            }

            return best;
        }

        /// <summary>任意一张付得起的牌（攻击优先）；没有则返回 -1。</summary>
        private static int FindAnyAffordableCard(RoguelikeWorld world)
        {
            int attack = FindAttackCard(world);
            return attack >= 0 ? attack : FindBlockCard(world);
        }
    }
}
