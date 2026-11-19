using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Monopoly;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="MonopolyWorld"/>（T4-8 大富翁玩法内核）的契约用例。
    /// <para>
    /// <b>覆盖口径</b>：20 格固定布局与价格序列、初始状态、掷骰移动与换手、经过/停在起点奖励、
    /// 买地/租金/升级/满级、税务/奖励/机会/监狱四类事件格、破产归还与淘汰、40 回合封顶与平局取小索引、
    /// 同种子确定性对拍、非法操作（已结束后再掷/结算前取报告/二次结算）、
    /// <c>CollectResult</c> 口径、<c>Step</c> 计数、玩家数夹取与零分配。
    /// </para>
    /// <para>
    /// <b>为什么事件用例用"扫描种子找事件"而不是全部写死</b>：内核只暴露公开 API（<see cref="IMonopolyReadView"/> /
    /// <see cref="IMonopolyInput"/>），测试无法直接摆盘。因此每个事件用例都在若干固定种子的确定性牌局里
    /// 找到该事件首次发生的时刻，再用<b>测试侧独立算出的期望值</b>核对现金流与棋盘状态——
    /// 既不复刻内核内部结构，也不靠"拿内核答案验内核"。
    /// </para>
    /// <para>
    /// <b>确定性锚点</b>：固定种子下整局完全可复现，快照类用例（首 7 手、封顶结局、平局、胜负）
    /// 直接写死种子与期望；随机流消耗顺序（先骰子、后机会卡）是契约的一部分，改顺序会让这些用例失败，
    /// 这是刻意保留的回归护栏。
    /// </para>
    /// </summary>
    public sealed class MonopolyWorldTests
    {
        /// <summary>棋盘格子总数。</summary>
        private const int TileCount = 20;

        /// <summary>地产格总数（20 格中 5 格非地产）。</summary>
        private const int PropertyCount = 15;

        /// <summary>每名玩家在封顶前可掷骰的次数（名义回合上限）。</summary>
        private const int MaxRounds = 40;

        /// <summary>起点索引。</summary>
        private const int GoTile = 0;

        /// <summary>监狱索引。</summary>
        private const int JailTile = 4;

        /// <summary>机会索引。</summary>
        private const int ChanceTile = 8;

        /// <summary>税务索引。</summary>
        private const int TaxTile = 12;

        /// <summary>奖励索引。</summary>
        private const int BonusTile = 16;

        /// <summary>地产索引（按价格升序）。</summary>
        private static readonly int[] PropertyTiles = { 1, 2, 3, 5, 6, 7, 9, 10, 11, 13, 14, 15, 17, 18, 19 };

        /// <summary>地产价格（与 <see cref="PropertyTiles"/> 一一对应）。</summary>
        private static readonly int[] PropertyPrices = { 200, 220, 240, 260, 280, 300, 320, 340, 360, 380, 400, 420, 440, 460, 480 };

        /// <summary>一局保险的掷骰循环上限（2 人局封顶 80 手，留足余量）。</summary>
        private const int RollGuard = 400;

        // ── 初始状态与固定布局 ─────────────────────────────────────────

        [Fact]
        public void Constructor_ExposesDefaultTwoPlayerSession()
        {
            MonopolyWorld world = CreateWorld(20261120);

            Assert.Equal(TileCount, world.TileCount);
            Assert.Equal(2, world.PlayerCount);
            Assert.Equal(0, world.TurnIndex);
            Assert.Equal(0, world.CurrentPlayer);
            Assert.Equal(1, world.RoundNumber);
            Assert.False(world.IsFinished);
            Assert.Equal(-1, world.WinnerPlayer);
            Assert.Equal(0, world.LastDice);
            Assert.Equal(-1, world.LastMovedPlayer);
            Assert.Equal(-1, world.LastMovedTo);
            Assert.Equal(0, world.ChanceEvents);
            Assert.Equal(0, world.SkippedPurchaseCount);
            Assert.Equal(0, world.ProcessedSteps);

            for (int player = 0; player < world.PlayerCount; player++)
            {
                MonopolyPlayerView view = world.GetPlayer(player);
                Assert.Equal(player, view.PlayerIndex);
                Assert.Equal(1500, view.Cash);
                Assert.Equal(0, view.Position);
                Assert.False(view.IsBankrupt);
                Assert.Equal(0, view.PropertyCount);
            }

            for (int tile = 0; tile < world.TileCount; tile++)
            {
                Assert.Equal(-1, world.GetTile(tile).Owner);
                Assert.Equal(0, world.GetTile(tile).Level);
            }
        }

        [Fact]
        public void FixedLayout_HasTwentyTilesWithHardcodedKindsAndPrices()
        {
            MonopolyWorld world = CreateWorld(1);

            Assert.Equal(MonopolyTileKind.Go, world.GetTile(GoTile).Kind);
            Assert.Equal(MonopolyTileKind.Jail, world.GetTile(JailTile).Kind);
            Assert.Equal(MonopolyTileKind.Chance, world.GetTile(ChanceTile).Kind);
            Assert.Equal(MonopolyTileKind.Tax, world.GetTile(TaxTile).Kind);
            Assert.Equal(MonopolyTileKind.Bonus, world.GetTile(BonusTile).Kind);

            int propertyCount = 0;
            int previousPrice = 0;
            for (int tile = 0; tile < world.TileCount; tile++)
            {
                MonopolyTileView view = world.GetTile(tile);
                if (view.Kind != MonopolyTileKind.Property)
                {
                    Assert.Equal(-1, Array.IndexOf(PropertyTiles, tile));
                    Assert.Equal(0, view.Price);
                    continue;
                }

                propertyCount++;
                Assert.Equal(PropertyTiles[propertyCount - 1], tile);
                Assert.Equal(PropertyPrices[propertyCount - 1], view.Price);
                Assert.True(view.Price > previousPrice, "地产价格必须按索引严格递增。");
                previousPrice = view.Price;
            }

            Assert.Equal(PropertyCount, propertyCount);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(TileCount)]
        [InlineData(99)]
        public void GetTile_OutOfRange_Throws(int index)
        {
            MonopolyWorld world = CreateWorld(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetTile(index));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        [InlineData(9)]
        public void GetPlayer_OutOfRange_Throws(int index)
        {
            MonopolyWorld world = CreateWorld(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetPlayer(index));
        }

        // ── 掷骰与换手 ─────────────────────────────────────────────────

        [Fact]
        public void FirstSevenRolls_WithFixedSeed_MatchesHardcodedSnapshot()
        {
            MonopolyWorld world = CreateWorld(1);
            int[] expectedDice = { 1, 1, 4, 1, 4, 2, 1 };

            for (int roll = 0; roll < expectedDice.Length; roll++)
            {
                Assert.True(world.TryRoll());
                Assert.Equal(expectedDice[roll], world.LastDice);
            }

            Assert.Equal(7, world.TurnIndex);
            Assert.Equal(1, world.CurrentPlayer);
            Assert.Equal(4, world.RoundNumber);
            Assert.False(world.IsFinished);
            Assert.Equal(-1, world.WinnerPlayer);
            Assert.Equal(1, world.LastDice);
            Assert.Equal(0, world.LastMovedPlayer);
            Assert.Equal(10, world.LastMovedTo);
            Assert.Equal(0, world.ChanceEvents);
            Assert.Equal(0, world.SkippedPurchaseCount);

            // 玩家 0：买下 1/5/9/10 四块地（200/260/320/340），并经手租金 20。
            MonopolyPlayerView player0 = world.GetPlayer(0);
            Assert.Equal(400, player0.Cash);
            Assert.Equal(10, player0.Position);
            Assert.Equal(4, player0.PropertyCount);

            // 玩家 1：买下 2（220），踩到监狱被送回起点，付过 20 租金。
            MonopolyPlayerView player1 = world.GetPlayer(1);
            Assert.Equal(1260, player1.Cash);
            Assert.Equal(0, player1.Position);
            Assert.Equal(1, player1.PropertyCount);

            Assert.Equal(0, world.GetTile(1).Owner);
            Assert.Equal(0, world.GetTile(5).Owner);
            Assert.Equal(0, world.GetTile(9).Owner);
            Assert.Equal(0, world.GetTile(10).Owner);
            Assert.Equal(1, world.GetTile(2).Owner);
            Assert.Equal(-1, world.GetTile(3).Owner);
        }

        [Fact]
        public void RollRotation_TwoPlayers_AlternatesAndAdvancesTurn()
        {
            MonopolyWorld world = CreateWorld(1);
            for (int roll = 0; roll < 20; roll++)
            {
                int mover = world.CurrentPlayer;
                int turnBefore = world.TurnIndex;

                Assert.True(world.TryRoll());

                Assert.Equal(turnBefore + 1, world.TurnIndex);
                Assert.Equal(mover, world.LastMovedPlayer);
                Assert.InRange(world.LastDice, 1, 6);
                Assert.Equal(world.GetPlayer(mover).Position, world.LastMovedTo);
                Assert.NotEqual(mover, world.CurrentPlayer);
                Assert.Equal(mover == 0 ? 1 : 0, world.CurrentPlayer);
                Assert.False(world.IsFinished);
            }

            Assert.Equal(20, world.TurnIndex);
            Assert.Equal(11, world.RoundNumber);
        }

        [Fact]
        public void RollRotation_FourPlayers_CyclesAllIndices()
        {
            MonopolyWorld world = CreateWorld(7, 4);

            for (int roll = 0; roll < 12; roll++)
            {
                Assert.Equal(roll % 4, world.CurrentPlayer);
                Assert.True(world.TryRoll());
                Assert.Equal(roll % 4, world.LastMovedPlayer);
                Assert.Equal((roll + 1) % 4, world.CurrentPlayer);
                Assert.False(world.IsFinished);
            }

            Assert.Equal(12, world.TurnIndex);
            Assert.Equal(4, world.RoundNumber);
        }

        [Fact]
        public void LastMovedFields_StartAtSentinelsAndTrackLatestAction()
        {
            MonopolyWorld world = CreateWorld(1);

            Assert.Equal(0, world.LastDice);
            Assert.Equal(-1, world.LastMovedPlayer);
            Assert.Equal(-1, world.LastMovedTo);

            for (int roll = 0; roll < 10; roll++)
            {
                int mover = world.CurrentPlayer;
                Assert.True(world.TryRoll());
                Assert.Equal(mover, world.LastMovedPlayer);
                Assert.InRange(world.LastDice, 1, 6);
                Assert.Equal(world.GetPlayer(mover).Position, world.LastMovedTo);
            }
        }

        // ── 确定性 ─────────────────────────────────────────────────────

        [Fact]
        public void SameSeed_And_SameRollSequence_ProduceIdenticalWorldState()
        {
            MonopolyWorld first = CreateWorld(1);
            MonopolyWorld second = CreateWorld(1);

            for (int roll = 0; roll < 50 && !first.IsFinished; roll++)
            {
                Assert.Equal(first.IsFinished, second.IsFinished);
                Assert.True(first.TryRoll());
                Assert.True(second.TryRoll());
                AssertSameWorld(first, second);
            }

            Assert.Equal(50, first.TurnIndex);
        }

        [Fact]
        public void DifferentSeeds_ProduceDifferentDiceSequences()
        {
            MonopolyWorld first = CreateWorld(1);
            MonopolyWorld second = CreateWorld(2);
            bool different = false;

            for (int roll = 0; roll < 20 && !first.IsFinished && !second.IsFinished; roll++)
            {
                first.TryRoll();
                second.TryRoll();
                if (first.LastDice != second.LastDice)
                {
                    different = true;
                }
            }

            Assert.True(different, "不同种子的骰子序列不应完全一致。");
        }

        // ── 起点 / 事件格 ──────────────────────────────────────────────

        [Fact]
        public void LandsOnGo_AwardsExactly200()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Landing != GoTile)
                {
                    return false;
                }

                Assert.True(roll.CrossedGo);
                Assert.Equal(GoTile, roll.Final);
                Assert.Equal(GoTile, world.GetPlayer(roll.Mover).Position);
                Assert.Equal(roll.CashBefore[roll.Mover] + 200, roll.CashAfter);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次停在起点。");
        }

        [Fact]
        public void LandsOnJail_TeleportsToGoWithoutGoReward()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Landing != JailTile || roll.CrossedGo)
                {
                    return false;
                }

                Assert.Equal(GoTile, roll.Final);
                Assert.Equal(GoTile, world.GetPlayer(roll.Mover).Position);
                Assert.Equal(roll.CashBefore[roll.Mover], roll.CashAfter);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次未越环踩到监狱。");
        }

        [Fact]
        public void CrossesGo_ThenLandsOnJail_StillAwards200Once()
        {
            bool found = ScanFor(120, 300, (world, roll) =>
            {
                if (roll.Landing != JailTile || !roll.CrossedGo)
                {
                    return false;
                }

                Assert.Equal(GoTile, roll.Final);
                Assert.Equal(roll.CashBefore[roll.Mover] + 200, roll.CashAfter);
                return true;
            });

            Assert.True(found, "120 个种子里必须至少出现一次越环后踩到监狱。");
        }

        [Fact]
        public void LandsOnTax_PaysExactly100ClampedAtZero()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Landing != TaxTile || roll.Final != TaxTile)
                {
                    return false;
                }

                int expected = roll.CashBefore[roll.Mover] - 100;
                if (expected < 0)
                {
                    expected = 0;
                }

                Assert.Equal(expected, roll.CashAfter);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次踩到税务格。");
        }

        [Fact]
        public void LandsOnBonus_GainsExactly150()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Landing != BonusTile || roll.Final != BonusTile)
                {
                    return false;
                }

                Assert.Equal(roll.CashBefore[roll.Mover] + 150, roll.CashAfter);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次踩到奖励格。");
        }

        [Fact]
        public void LandsOnChance_DrawsOneOfFourCards_AndForwardThreeMovesToTile11()
        {
            bool smallGain = false;
            bool bigGain = false;
            bool lose = false;
            bool forwardThree = false;

            bool found = ScanFor(200, 300, (world, roll) =>
            {
                if (roll.Landing != ChanceTile)
                {
                    return false;
                }

                // 落在机会格：必然抽 1 张卡（本卡带唯一的随机消耗点）。
                Assert.Equal(roll.ChanceEventsBefore + 1, world.ChanceEvents);

                if (roll.Final == ChanceTile)
                {
                    int delta = roll.CashAfter - roll.CashBefore[roll.Mover];
                    if (delta == 100)
                    {
                        smallGain = true;
                    }
                    else if (delta == 200)
                    {
                        bigGain = true;
                    }
                    else
                    {
                        // -100 卡：现金不足时只扣到 0。
                        Assert.Equal(Math.Max(0, roll.CashBefore[roll.Mover] - 100), roll.CashAfter);
                        lose = true;
                    }
                }
                else
                {
                    // 前进 3 格：8 + 3 = 11，落地后照常结算新格子。
                    Assert.Equal(11, roll.Final);
                    Assert.Equal(MonopolyTileKind.Property, world.GetTile(11).Kind);
                    forwardThree = true;
                }

                return smallGain && bigGain && lose && forwardThree;
            });

            Assert.True(found, "200 个种子里必须集齐机会卡四种结果。");
        }

        [Fact]
        public void ChanceEvents_IncrementOnlyOnChanceTile()
        {
            MonopolyWorld world = CreateWorld(3);

            while (!world.IsFinished)
            {
                int chanceBefore = world.ChanceEvents;
                int mover = world.CurrentPlayer;
                int before = world.GetPlayer(mover).Position;

                Assert.True(world.TryRoll());

                int landing = (before + world.LastDice) % TileCount;
                int expectedDelta = landing == ChanceTile ? 1 : 0;
                Assert.Equal(expectedDelta, world.ChanceEvents - chanceBefore);
            }

            Assert.True(world.ChanceEvents > 0);
        }

        // ── 地产：买 / 租 / 升级 ───────────────────────────────────────

        [Fact]
        public void LandsOnUnownedProperty_AutoBuysAtListPrice()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Final != roll.Landing)
                {
                    return false;
                }

                MonopolyTileView tile = world.GetTile(roll.Final);
                if (tile.Kind != MonopolyTileKind.Property || roll.OwnersBefore[roll.Final] != -1)
                {
                    return false;
                }

                int goBonus = roll.CrossedGo ? 200 : 0;
                if (roll.CashBefore[roll.Mover] + goBonus < tile.Price)
                {
                    return false;
                }

                Assert.Equal(roll.Mover, tile.Owner);
                Assert.Equal(0, tile.Level);
                Assert.Equal(roll.CashBefore[roll.Mover] + goBonus - tile.Price, roll.CashAfter);
                Assert.Equal(roll.PropertyCountBefore[roll.Mover] + 1, world.GetPlayer(roll.Mover).PropertyCount);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次自动买地。");
        }

        [Fact]
        public void LandsOnUnownedProperty_WithInsufficientCash_SkipsPurchaseAndCounts()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Final != roll.Landing)
                {
                    return false;
                }

                MonopolyTileView tile = world.GetTile(roll.Final);
                if (tile.Kind != MonopolyTileKind.Property || roll.OwnersBefore[roll.Final] != -1)
                {
                    return false;
                }

                int goBonus = roll.CrossedGo ? 200 : 0;
                if (roll.CashBefore[roll.Mover] + goBonus >= tile.Price)
                {
                    return false;
                }

                Assert.Equal(-1, tile.Owner);
                Assert.Equal(0, tile.Level);
                Assert.Equal(roll.CashBefore[roll.Mover] + goBonus, roll.CashAfter);
                Assert.Equal(roll.SkippedPurchaseBefore + 1, world.SkippedPurchaseCount);
                Assert.Equal(roll.PropertyCountBefore[roll.Mover], world.GetPlayer(roll.Mover).PropertyCount);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次现金不足跳过买地。");
        }

        [Fact]
        public void LandsOnOwnProperty_PaysHalfPriceAndUpgrades()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Final != roll.Landing)
                {
                    return false;
                }

                MonopolyTileView tile = world.GetTile(roll.Final);
                if (tile.Kind != MonopolyTileKind.Property || roll.OwnersBefore[roll.Final] != roll.Mover)
                {
                    return false;
                }

                int previousLevel = roll.LevelsBefore[roll.Final];
                if (previousLevel >= 3)
                {
                    return false;
                }

                int goBonus = roll.CrossedGo ? 200 : 0;
                int cost = tile.Price / 2;
                if (roll.CashBefore[roll.Mover] + goBonus < cost)
                {
                    return false;
                }

                Assert.Equal(previousLevel + 1, tile.Level);
                Assert.Equal(roll.CashBefore[roll.Mover] + goBonus - cost, roll.CashAfter);
                Assert.Equal(roll.PropertyCountBefore[roll.Mover], world.GetPlayer(roll.Mover).PropertyCount);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次踩到自己的地产并升级。");
        }

        [Fact]
        public void OwnProperty_UpgradeCostAndLevelCapFollowRules()
        {
            bool sawLevelThree = false;

            for (int seed = 1; seed <= 60 && !sawLevelThree; seed++)
            {
                MonopolyWorld world = CreateWorld(seed);
                while (!world.IsFinished)
                {
                    RollSnapshot roll = NextRoll(world);

                    for (int tile = 0; tile < world.TileCount; tile++)
                    {
                        MonopolyTileView view = world.GetTile(tile);
                        Assert.InRange(view.Level, 0, 3);
                        if (view.Level == 3)
                        {
                            sawLevelThree = true;
                        }
                    }

                    if (roll.Final != roll.Landing || world.GetPlayer(roll.Mover).IsBankrupt)
                    {
                        continue;
                    }

                    MonopolyTileView landed = world.GetTile(roll.Final);
                    if (landed.Kind != MonopolyTileKind.Property || roll.OwnersBefore[roll.Final] != roll.Mover)
                    {
                        continue;
                    }

                    int goBonus = roll.CrossedGo ? 200 : 0;
                    int previousLevel = roll.LevelsBefore[roll.Final];
                    int cost = landed.Price / 2;

                    if (previousLevel >= 3)
                    {
                        Assert.Equal(3, landed.Level);
                        Assert.Equal(roll.CashBefore[roll.Mover] + goBonus, roll.CashAfter);
                    }
                    else if (roll.CashBefore[roll.Mover] + goBonus >= cost)
                    {
                        Assert.Equal(previousLevel + 1, landed.Level);
                        Assert.Equal(roll.CashBefore[roll.Mover] + goBonus - cost, roll.CashAfter);
                    }
                }
            }

            Assert.True(sawLevelThree, "60 个种子里必须至少有一块地产升到满级。");
        }

        [Fact]
        public void LandsOnOtherPlayerProperty_PaysBaseRentAndTransfersIt()
        {
            bool found = ScanFor(80, 300, (world, roll) =>
            {
                if (roll.Final != roll.Landing)
                {
                    return false;
                }

                MonopolyTileView tile = world.GetTile(roll.Final);
                if (tile.Kind != MonopolyTileKind.Property)
                {
                    return false;
                }

                int owner = roll.OwnersBefore[roll.Final];
                if (owner < 0 || owner == roll.Mover || roll.LevelsBefore[roll.Final] != 0)
                {
                    return false;
                }

                int goBonus = roll.CrossedGo ? 200 : 0;
                int rent = tile.Price / 10;
                if (roll.CashBefore[roll.Mover] + goBonus < rent)
                {
                    return false;
                }

                Assert.Equal(roll.CashBefore[roll.Mover] + goBonus - rent, roll.CashAfter);
                Assert.Equal(roll.CashBefore[owner] + rent, world.GetPlayer(owner).Cash);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次向他人支付 0 级租金。");
        }

        [Fact]
        public void Rent_ScalesWithPropertyLevel()
        {
            bool found = ScanFor(80, 400, (world, roll) =>
            {
                if (roll.Final != roll.Landing)
                {
                    return false;
                }

                MonopolyTileView tile = world.GetTile(roll.Final);
                if (tile.Kind != MonopolyTileKind.Property)
                {
                    return false;
                }

                int owner = roll.OwnersBefore[roll.Final];
                int level = roll.LevelsBefore[roll.Final];
                if (owner < 0 || owner == roll.Mover || level < 1)
                {
                    return false;
                }

                int goBonus = roll.CrossedGo ? 200 : 0;
                int rent = tile.Price * (10 + (15 * level)) / 100;
                if (roll.CashBefore[roll.Mover] + goBonus < rent)
                {
                    return false;
                }

                Assert.Equal(roll.CashBefore[roll.Mover] + goBonus - rent, roll.CashAfter);
                Assert.Equal(roll.CashBefore[owner] + rent, world.GetPlayer(owner).Cash);
                return true;
            });

            Assert.True(found, "80 个种子里必须至少出现一次升级后地产的租金结算。");
        }

        [Fact]
        public void PropertyOwnership_StaysConsistentDuringWholeGame()
        {
            MonopolyWorld world = CreateWorld(3);

            while (!world.IsFinished)
            {
                NextRoll(world);
                AssertOwnershipInvariants(world);
            }

            AssertOwnershipInvariants(world);
        }

        // ── 破产与淘汰 ─────────────────────────────────────────────────

        [Fact]
        public void Bankruptcy_ReleasesAllPropertiesAndEndsTwoPlayerGame()
        {
            bool found = ScanFor(60, 400, (world, roll) =>
            {
                if (!roll.MoverBankrupt)
                {
                    return false;
                }

                // 两人局：一人破产即终局，幸存者胜。
                Assert.Equal(0, roll.CashAfter);
                Assert.True(world.IsFinished);
                Assert.Equal(2, world.PlayerCount);
                Assert.Equal(0, world.GetPlayer(roll.Mover).PropertyCount);

                int survivor = 1 - roll.Mover;
                Assert.False(world.GetPlayer(survivor).IsBankrupt);
                Assert.Equal(survivor, world.WinnerPlayer);

                for (int tile = 0; tile < world.TileCount; tile++)
                {
                    if (roll.OwnersBefore[tile] != roll.Mover)
                    {
                        continue;
                    }

                    Assert.Equal(-1, world.GetTile(tile).Owner);
                    Assert.Equal(0, world.GetTile(tile).Level);
                }

                return true;
            });

            Assert.True(found, "60 个种子里必须至少出现一次付不起租金的破产。");
        }

        [Fact]
        public void Bankruptcy_WithThreePlayers_RemainingPlayersContinueAndDeadTurnIsSkipped()
        {
            MonopolyWorld world = CreateWorld(1, 3);
            bool found = false;
            int deadPlayer = -1;

            while (!world.IsFinished && !found)
            {
                int mover = world.CurrentPlayer;
                bool[] bankruptBefore = new bool[world.PlayerCount];
                int[] cashBefore = new int[world.PlayerCount];
                for (int player = 0; player < world.PlayerCount; player++)
                {
                    MonopolyPlayerView view = world.GetPlayer(player);
                    bankruptBefore[player] = view.IsBankrupt;
                    cashBefore[player] = view.Cash;
                }

                int[] ownersBefore = CaptureOwners(world);
                int[] levelsBefore = CaptureLevels(world);

                Assert.True(world.TryRoll());

                if (bankruptBefore[mover] || !world.GetPlayer(mover).IsBankrupt)
                {
                    continue;
                }

                found = true;
                deadPlayer = mover;
                Assert.Equal(0, world.GetPlayer(mover).Cash);
                Assert.Equal(0, world.GetPlayer(mover).PropertyCount);
                Assert.False(world.IsFinished);
                Assert.Equal(3, world.PlayerCount);

                for (int tile = 0; tile < world.TileCount; tile++)
                {
                    if (ownersBefore[tile] != mover)
                    {
                        continue;
                    }

                    Assert.Equal(-1, world.GetTile(tile).Owner);
                    Assert.Equal(0, world.GetTile(tile).Level);
                }

                Assert.True(world.TurnIndex > 0);
                Assert.True(cashBefore[mover] >= 0);
            }

            Assert.True(found, "3 人局 seed=1 必须出现一次破产（且此时仍有 2 人存活）。");

            // 破产者不再出现在当前玩家指针上，剩余玩家继续。
            int guard = 0;
            while (!world.IsFinished && guard++ < RollGuard)
            {
                Assert.NotEqual(deadPlayer, world.CurrentPlayer);
                Assert.True(world.TryRoll());
            }

            Assert.True(world.IsFinished);
            Assert.Equal(3 * MaxRounds, world.TurnIndex);
        }

        // ── 封顶与胜者 ─────────────────────────────────────────────────

        [Fact]
        public void MaxRounds_EndsWithHighestCashWinner()
        {
            MonopolyWorld world = CreateWorld(1);
            PlayUntilFinished(world);

            Assert.True(world.IsFinished);
            Assert.Equal(2 * MaxRounds, world.TurnIndex);
            Assert.False(world.GetPlayer(0).IsBankrupt);
            Assert.False(world.GetPlayer(1).IsBankrupt);

            int expectedWinner = ExpectedWinnerByCash(world);
            Assert.Equal(expectedWinner, world.WinnerPlayer);
            Assert.True(world.GetPlayer(world.WinnerPlayer).Cash >= world.GetPlayer(1 - world.WinnerPlayer).Cash);
        }

        [Fact]
        public void MaxRounds_ReportsFortyNominalRounds()
        {
            MonopolyWorld world = CreateWorld(3);
            PlayUntilFinished(world);

            Assert.True(world.IsFinished);
            Assert.Equal(2 * MaxRounds, world.TurnIndex);
            Assert.Equal(MaxRounds, world.TurnIndex / world.PlayerCount);
            // RoundNumber 是 1 基公式读数：40 个完整回合后进入"第 41 回合"瞬间收口。
            Assert.Equal(MaxRounds + 1, world.RoundNumber);
            Assert.Equal(0, world.WinnerPlayer);
        }

        [Fact]
        public void MaxRounds_TieBreaksToSmallerPlayerIndex()
        {
            MonopolyWorld world = CreateWorld(1345);
            PlayUntilFinished(world);

            Assert.True(world.IsFinished);
            Assert.Equal(2 * MaxRounds, world.TurnIndex);
            Assert.False(world.GetPlayer(0).IsBankrupt);
            Assert.False(world.GetPlayer(1).IsBankrupt);
            Assert.Equal(world.GetPlayer(0).Cash, world.GetPlayer(1).Cash);
            Assert.Equal(0, world.WinnerPlayer);
        }

        [Fact]
        public void SinglePlayerSession_PlaysFortyTurnsAndIsVictory()
        {
            MonopolyWorld world = CreateWorld(1, 1);

            Assert.Equal(1, world.PlayerCount);
            Assert.False(world.IsFinished);

            int guard = 0;
            while (!world.IsFinished && guard++ < RollGuard)
            {
                Assert.Equal(0, world.CurrentPlayer);
                Assert.True(world.TryRoll());
            }

            Assert.True(world.IsFinished);
            Assert.Equal(MaxRounds, world.TurnIndex);
            Assert.Equal(0, world.WinnerPlayer);

            GameplayResult result = world.CollectResult();
            Assert.True(result.IsVictory);
            Assert.Equal(MaxRounds, result.StepCount);
            Assert.Equal(world.GetPlayer(0).Cash, result.RemainingUnits);
        }

        [Theory]
        [InlineData(-5, 2)]
        [InlineData(0, 2)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 3)]
        [InlineData(4, 4)]
        [InlineData(5, 4)]
        public void PlayerCount_ClampsUnitCount(int unitCount, int expectedPlayers)
        {
            MonopolyWorld world = CreateWorld(1, unitCount);

            Assert.Equal(expectedPlayers, world.PlayerCount);
            for (int player = 0; player < expectedPlayers; player++)
            {
                Assert.Equal(1500, world.GetPlayer(player).Cash);
                Assert.Equal(0, world.GetPlayer(player).Position);
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetPlayer(expectedPlayers));
            Assert.False(world.IsFinished);
        }

        // ── 非法操作 / 结算门禁 ────────────────────────────────────────

        [Fact]
        public void TryRoll_AfterFinish_ReturnsFalseAndKeepsState()
        {
            MonopolyWorld world = CreateWorld(1);
            PlayUntilFinished(world);

            int turnBefore = world.TurnIndex;
            int cash0Before = world.GetPlayer(0).Cash;
            int cash1Before = world.GetPlayer(1).Cash;
            int lastDiceBefore = world.LastDice;
            int winnerBefore = world.WinnerPlayer;

            Assert.False(world.TryRoll());
            Assert.False(world.TryRoll());

            Assert.Equal(turnBefore, world.TurnIndex);
            Assert.Equal(cash0Before, world.GetPlayer(0).Cash);
            Assert.Equal(cash1Before, world.GetPlayer(1).Cash);
            Assert.Equal(lastDiceBefore, world.LastDice);
            Assert.Equal(winnerBefore, world.WinnerPlayer);
            Assert.True(world.IsFinished);
        }

        [Fact]
        public void CollectResult_BeforeFinish_Throws()
        {
            MonopolyWorld world = CreateWorld(1);

            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            Assert.True(world.TryRoll());
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
            Assert.Equal(1, world.TurnIndex);
        }

        [Fact]
        public void CollectResult_Victory_ReportsTurnKernelAndPlayer0CashAsScore()
        {
            MonopolyWorld world = CreateWorld(3);
            PlayUntilFinished(world);
            Assert.Equal(0, world.WinnerPlayer);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Equal(world.TurnIndex, result.StepCount);
            Assert.Equal(world.GetPlayer(0).Cash, result.RemainingUnits);
            Assert.Empty(result.TriggeredTags);

            LootItem loot = Assert.Single(result.Loots);
            Assert.Equal(1003, loot.ItemId);
            Assert.Equal(1, loot.Count);
        }

        [Fact]
        public void CollectResult_Defeat_HasNoLootAndReportsPlayer0Cash()
        {
            MonopolyWorld world = CreateWorld(1);
            PlayUntilFinished(world);
            Assert.NotEqual(0, world.WinnerPlayer);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Equal(world.TurnIndex, result.StepCount);
            Assert.Equal(world.GetPlayer(0).Cash, result.RemainingUnits);
            Assert.Empty(result.Loots);
        }

        [Fact]
        public void CollectResult_SecondCall_ThrowsAndFreezesSession()
        {
            MonopolyWorld world = CreateWorld(3);
            PlayUntilFinished(world);

            world.CollectResult();
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            int turnBefore = world.TurnIndex;
            Assert.True(world.IsFinished);
            Assert.False(world.TryRoll());
            Assert.Equal(turnBefore, world.TurnIndex);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        // ── Step 语义 ──────────────────────────────────────────────────

        [Fact]
        public void Step_CountsProcessedSteps_ButResultStepCountIsTurnIndex()
        {
            MonopolyWorld world = CreateWorld(3);

            world.Step(1f / 60f);
            world.Step(1f / 60f);
            world.Step(1f / 60f);
            Assert.Equal(3, world.ProcessedSteps);

            PlayUntilFinished(world);
            GameplayResult result = world.CollectResult();

            Assert.Equal(world.TurnIndex, result.StepCount);
            Assert.Equal(3, world.ProcessedSteps);
            Assert.NotEqual(world.ProcessedSteps, result.StepCount);
        }

        [Fact]
        public void Step_AfterFinish_DoesNotIncrement()
        {
            MonopolyWorld world = CreateWorld(1);
            PlayUntilFinished(world);

            int processedBefore = world.ProcessedSteps;
            world.Step(1f / 60f);
            world.Step(1f / 60f);

            Assert.Equal(processedBefore, world.ProcessedSteps);
        }

        // ── 零分配 ─────────────────────────────────────────────────────

        [Fact]
        public void TryRoll_AfterWarmup_DoesNotAllocate()
        {
            MonopolyWorld world = CreateWorld(1345);

            for (int roll = 0; roll < 10; roll++)
            {
                Assert.True(world.TryRoll());
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int roll = 0; roll < 1000; roll++)
            {
                world.TryRoll();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
            Assert.True(world.IsFinished);
        }

        [Fact]
        public void Step_AfterWarmup_DoesNotAllocate()
        {
            MonopolyWorld world = CreateWorld(1);
            world.Step(1f / 60f);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int step = 0; step < 1000; step++)
            {
                world.Step(1f / 60f);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        // ── 测试侧工具 ─────────────────────────────────────────────────

        private static MonopolyWorld CreateWorld(int seed, int unitCount = 2)
        {
            return new MonopolyWorld(new GameplayInitData(1, seed, unitCount));
        }

        /// <summary>一局打完（带保险上限），并断言确实结束。</summary>
        private static void PlayUntilFinished(MonopolyWorld world)
        {
            int guard = 0;
            while (!world.IsFinished && guard++ < RollGuard)
            {
                Assert.True(world.TryRoll());
            }

            Assert.True(world.IsFinished, "对局未在保险上限内结束，规则或测试口径异常。");
        }

        /// <summary>掷一手并返回前后快照（测试用，允许分配）。</summary>
        private static RollSnapshot NextRoll(MonopolyWorld world)
        {
            RollSnapshot snapshot = RollSnapshot.Capture(world);
            Assert.True(world.TryRoll());
            snapshot.Complete(world);
            return snapshot;
        }

        /// <summary>
        /// 在固定种子序列里扫描"首次满足断言的事件"：断言直接写在 <paramref name="predicate"/> 里，
        /// 找到即返回 true（事件用例用它避免写死具体种子）。
        /// </summary>
        private static bool ScanFor(int seedLimit, int rollLimit, Func<MonopolyWorld, RollSnapshot, bool> predicate)
        {
            for (int seed = 1; seed <= seedLimit; seed++)
            {
                MonopolyWorld world = CreateWorld(seed);
                for (int roll = 0; roll < rollLimit && !world.IsFinished; roll++)
                {
                    RollSnapshot snapshot = NextRoll(world);
                    if (predicate(world, snapshot))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>按"现金最高、平局取小索引"独立算出期望胜者（只读公开视图）。</summary>
        private static int ExpectedWinnerByCash(MonopolyWorld world)
        {
            int best = -1;
            int bestCash = int.MinValue;
            for (int player = 0; player < world.PlayerCount; player++)
            {
                MonopolyPlayerView view = world.GetPlayer(player);
                if (view.IsBankrupt)
                {
                    continue;
                }

                if (view.Cash > bestCash)
                {
                    bestCash = view.Cash;
                    best = player;
                }
            }

            return best;
        }

        private static int[] CaptureOwners(MonopolyWorld world)
        {
            int[] owners = new int[world.TileCount];
            for (int tile = 0; tile < world.TileCount; tile++)
            {
                owners[tile] = world.GetTile(tile).Owner;
            }

            return owners;
        }

        private static int[] CaptureLevels(MonopolyWorld world)
        {
            int[] levels = new int[world.TileCount];
            for (int tile = 0; tile < world.TileCount; tile++)
            {
                levels[tile] = world.GetTile(tile).Level;
            }

            return levels;
        }

        /// <summary>棋盘/玩家视图的跨字段一致性：非地产无主、所有者存活、地产数与棋盘持有数相等。</summary>
        private static void AssertOwnershipInvariants(MonopolyWorld world)
        {
            int[] ownedCount = new int[world.PlayerCount];
            int ownedTiles = 0;

            for (int tile = 0; tile < world.TileCount; tile++)
            {
                MonopolyTileView view = world.GetTile(tile);
                if (view.Kind != MonopolyTileKind.Property)
                {
                    Assert.Equal(-1, view.Owner);
                    Assert.Equal(0, view.Level);
                    Assert.Equal(0, view.Price);
                    continue;
                }

                Assert.InRange(view.Level, 0, 3);
                if (view.Owner < 0)
                {
                    Assert.Equal(0, view.Level);
                    continue;
                }

                Assert.InRange(view.Owner, 0, world.PlayerCount - 1);
                Assert.False(world.GetPlayer(view.Owner).IsBankrupt);
                ownedCount[view.Owner]++;
                ownedTiles++;
            }

            int reportedOwned = 0;
            for (int player = 0; player < world.PlayerCount; player++)
            {
                MonopolyPlayerView view = world.GetPlayer(player);
                Assert.Equal(ownedCount[player], view.PropertyCount);
                reportedOwned += view.PropertyCount;
                if (view.IsBankrupt)
                {
                    Assert.Equal(0, view.Cash);
                    Assert.Equal(0, view.PropertyCount);
                }
            }

            Assert.Equal(ownedTiles, reportedOwned);
        }

        /// <summary>两个同种子 world 的逐字段对拍（含玩家、格子、计数与最近行动快照）。</summary>
        private static void AssertSameWorld(MonopolyWorld expected, MonopolyWorld actual)
        {
            Assert.Equal(expected.TileCount, actual.TileCount);
            Assert.Equal(expected.PlayerCount, actual.PlayerCount);
            Assert.Equal(expected.TurnIndex, actual.TurnIndex);
            Assert.Equal(expected.CurrentPlayer, actual.CurrentPlayer);
            Assert.Equal(expected.RoundNumber, actual.RoundNumber);
            Assert.Equal(expected.IsFinished, actual.IsFinished);
            Assert.Equal(expected.WinnerPlayer, actual.WinnerPlayer);
            Assert.Equal(expected.LastDice, actual.LastDice);
            Assert.Equal(expected.LastMovedPlayer, actual.LastMovedPlayer);
            Assert.Equal(expected.LastMovedTo, actual.LastMovedTo);
            Assert.Equal(expected.ChanceEvents, actual.ChanceEvents);
            Assert.Equal(expected.SkippedPurchaseCount, actual.SkippedPurchaseCount);

            for (int player = 0; player < expected.PlayerCount; player++)
            {
                MonopolyPlayerView left = expected.GetPlayer(player);
                MonopolyPlayerView right = actual.GetPlayer(player);
                Assert.Equal(left.Cash, right.Cash);
                Assert.Equal(left.Position, right.Position);
                Assert.Equal(left.IsBankrupt, right.IsBankrupt);
                Assert.Equal(left.PropertyCount, right.PropertyCount);
            }

            for (int tile = 0; tile < expected.TileCount; tile++)
            {
                MonopolyTileView left = expected.GetTile(tile);
                MonopolyTileView right = actual.GetTile(tile);
                Assert.Equal(left.Kind, right.Kind);
                Assert.Equal(left.Owner, right.Owner);
                Assert.Equal(left.Level, right.Level);
                Assert.Equal(left.Price, right.Price);
            }
        }

        /// <summary>一手掷骰的前后快照：事件用例靠它独立计算期望现金流（不反射内核内部字段）。</summary>
        private sealed class RollSnapshot
        {
            internal int Mover { get; set; }

            internal int BeforePosition { get; set; }

            internal bool[] BankruptBefore { get; set; } = Array.Empty<bool>();

            internal int[] CashBefore { get; set; } = Array.Empty<int>();

            internal int[] PropertyCountBefore { get; set; } = Array.Empty<int>();

            internal int[] OwnersBefore { get; set; } = Array.Empty<int>();

            internal int[] LevelsBefore { get; set; } = Array.Empty<int>();

            internal int SkippedPurchaseBefore { get; set; }

            internal int ChanceEventsBefore { get; set; }

            internal int Dice { get; set; }

            internal int Landing { get; set; }

            internal int Final { get; set; }

            internal bool CrossedGo { get; set; }

            internal int CashAfter { get; set; }

            internal bool MoverBankrupt { get; set; }

            internal static RollSnapshot Capture(MonopolyWorld world)
            {
                int playerCount = world.PlayerCount;
                int tileCount = world.TileCount;
                RollSnapshot snapshot = new RollSnapshot
                {
                    Mover = world.CurrentPlayer,
                    BankruptBefore = new bool[playerCount],
                    CashBefore = new int[playerCount],
                    PropertyCountBefore = new int[playerCount],
                    OwnersBefore = new int[tileCount],
                    LevelsBefore = new int[tileCount],
                    SkippedPurchaseBefore = world.SkippedPurchaseCount,
                    ChanceEventsBefore = world.ChanceEvents
                };

                for (int player = 0; player < playerCount; player++)
                {
                    MonopolyPlayerView view = world.GetPlayer(player);
                    snapshot.BankruptBefore[player] = view.IsBankrupt;
                    snapshot.CashBefore[player] = view.Cash;
                    snapshot.PropertyCountBefore[player] = view.PropertyCount;
                }

                snapshot.BeforePosition = world.GetPlayer(snapshot.Mover).Position;

                for (int tile = 0; tile < tileCount; tile++)
                {
                    MonopolyTileView view = world.GetTile(tile);
                    snapshot.OwnersBefore[tile] = view.Owner;
                    snapshot.LevelsBefore[tile] = view.Level;
                }

                return snapshot;
            }

            internal void Complete(MonopolyWorld world)
            {
                Dice = world.LastDice;
                Landing = (BeforePosition + Dice) % TileCount;
                CrossedGo = BeforePosition + Dice >= TileCount;
                Final = world.LastMovedTo;
                CashAfter = world.GetPlayer(Mover).Cash;
                MoverBankrupt = world.GetPlayer(Mover).IsBankrupt;
            }
        }
    }
}
