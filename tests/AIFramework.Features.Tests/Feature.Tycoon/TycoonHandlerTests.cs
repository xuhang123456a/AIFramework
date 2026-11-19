using System;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Cqe;
using AIFramework.Features.Tycoon;
using Xunit;

namespace AIFramework.Features.Tests.Tycoon
{
    /// <summary>
    /// 模拟经营（T2-11）经营循环验收：真实 <see cref="CqeBus"/> + 本切片私有桩（背包 / 钱包 / 表加载器），
    /// 断言只落在"切片发出了什么指令"与"快照内容"两处。
    /// </summary>
    public sealed class TycoonHandlerTests
    {
        // ---------- 建造 ----------

        [Fact]
        public void Build_WithEnoughGold_ConsumesBuildCostAndRegistersLevelOneBuilding()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(500);

            harness.Build(TycoonTestHarness.ShopId);

            ConsumeItemCommand consume = Assert.Single(harness.Wallet.Consumes);
            Assert.Equal(MetaItemIds.Gold, consume.ItemId);
            Assert.Equal(300, consume.Count);
            Assert.Equal(6811, consume.ReasonTagId);

            Assert.Equal(1, harness.Handler.BuiltCount);
            Assert.Equal(200, harness.Inventory.GetCount(MetaItemIds.Gold));

            TycoonSnapshot snapshot = harness.Snapshot();
            Assert.Equal(200, snapshot.Gold);
            Assert.Equal(12, snapshot.TotalIncomePerTick);
            Assert.Equal(0, snapshot.TickCount);

            TycoonBuildingState building = Assert.Single(snapshot.Buildings);
            Assert.Equal(TycoonTestHarness.ShopId, building.BuildingId);
            Assert.Equal(1, building.Level);
            Assert.Equal(0, building.StaffCount);
            Assert.Equal(12, building.IncomePerTick);
        }

        [Fact]
        public void Build_WithExactlyEnoughGold_SucceedsBecauseBalanceCheckIsInclusive()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(300);

            harness.Build(TycoonTestHarness.ShopId);

            Assert.Single(harness.Wallet.Consumes);
            Assert.Equal(1, harness.Handler.BuiltCount);
            Assert.Equal(0, harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void Build_WithInsufficientGold_IsSilentlyRejectedAfterOneBalanceQuery()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(299);

            harness.Build(TycoonTestHarness.ShopId);

            // 余额不足是"查过之后才发现"：只发生一次播种查询，没有任何指令。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Empty(harness.Wallet.Consumes);
            Assert.Empty(harness.Wallet.Grants);
            Assert.Equal(0, harness.Handler.BuiltCount);
            Assert.Empty(harness.Snapshot().Buildings);

            // 被拒绝时缓存仍然完成播种，快照金币与背包一致。
            Assert.Equal(299, harness.Snapshot().Gold);
            Assert.Equal(299, harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void Build_SameBuildingTwice_IsRejectedWithoutSecondBalanceQuery()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            harness.Build(TycoonTestHarness.ShopId);
            harness.Build(TycoonTestHarness.ShopId);

            Assert.Single(harness.Wallet.Consumes);
            Assert.Equal(1, harness.Handler.BuiltCount);
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Equal(700, harness.Snapshot().Gold);
            Assert.Single(harness.Snapshot().Buildings);
        }

        [Fact]
        public void Build_UnknownBuilding_IsRejectedWithoutTouchingInventory()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            harness.Build(4242);

            // 未知建筑连背包都不该查。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Empty(harness.Wallet.Consumes);
            Assert.Equal(0, harness.Handler.BuiltCount);
            Assert.Empty(harness.Snapshot().Buildings);
        }

        // ---------- 收入公式 ----------

        [Fact]
        public void Income_AppliesStaffBonusAndLevelMultiplier()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);
            harness.Build(TycoonTestHarness.ShopId);

            Assert.Equal(12, harness.Handler.TotalIncomePerTick);

            harness.Hire(TycoonTestHarness.ShopId);
            Assert.Equal(18, harness.Handler.TotalIncomePerTick);
            Assert.Equal(18, harness.Snapshot().Buildings[0].IncomePerTick);

            harness.Hire(TycoonTestHarness.ShopId);
            Assert.Equal(24, harness.Handler.TotalIncomePerTick);

            harness.Upgrade(TycoonTestHarness.ShopId);

            // (12 + 2 × (12 / 2)) × 2 = 48：员工加成先算，等级最后乘。
            TycoonBuildingState building = harness.Snapshot().Buildings[0];
            Assert.Equal(2, building.Level);
            Assert.Equal(2, building.StaffCount);
            Assert.Equal(48, building.IncomePerTick);
            Assert.Equal(48, harness.Handler.TotalIncomePerTick);
        }

        [Fact]
        public void TotalIncomePerTick_SumsEveryBuiltBuilding()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            harness.Build(TycoonTestHarness.StallId);
            harness.Build(TycoonTestHarness.ShopId);

            Assert.Equal(5 + 12, harness.Handler.TotalIncomePerTick);
            Assert.Equal(17, harness.Snapshot().TotalIncomePerTick);

            // 摊位雇 1 人：(5 + 1 × (5 / 2)) = 7，总收入 12 + 7 = 19。
            harness.Hire(TycoonTestHarness.StallId);

            Assert.Equal(19, harness.Handler.TotalIncomePerTick);
            Assert.Equal(19, harness.Snapshot().TotalIncomePerTick);
        }

        // ---------- 升级 ----------

        [Fact]
        public void Upgrade_ChargesHalfBuildCostTimesCurrentLevel()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);
            harness.Build(TycoonTestHarness.ShopId);

            harness.Upgrade(TycoonTestHarness.ShopId);
            harness.Upgrade(TycoonTestHarness.ShopId);

            // 建造 300 → 升级 1→2 收 150 → 升级 2→3 收 300。
            Assert.Equal(3, harness.Wallet.Consumes.Count);
            Assert.Equal(300, harness.Wallet.Consumes[0].Count);
            Assert.Equal(150, harness.Wallet.Consumes[1].Count);
            Assert.Equal(300, harness.Wallet.Consumes[2].Count);

            Assert.Equal(2, harness.Handler.UpgradeCount);

            TycoonSnapshot snapshot = harness.Snapshot();
            Assert.Equal(3, snapshot.Buildings[0].Level);
            Assert.Equal(36, snapshot.Buildings[0].IncomePerTick);
            Assert.Equal(250, snapshot.Gold);
        }

        [Fact]
        public void Upgrade_AtMaxLevel_IsSilentlyRejected()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(3000);
            harness.Build(TycoonTestHarness.RestaurantId);
            harness.Upgrade(TycoonTestHarness.RestaurantId);
            harness.Upgrade(TycoonTestHarness.RestaurantId);

            int consumedBeforeCappedUpgrade = harness.Wallet.Consumes.Count;

            harness.Upgrade(TycoonTestHarness.RestaurantId);

            // 封顶后不再扣款、不再涨级：餐厅 maxLevel = 3。
            Assert.Equal(consumedBeforeCappedUpgrade, harness.Wallet.Consumes.Count);
            Assert.Equal(2, harness.Handler.UpgradeCount);
            Assert.Equal(3, harness.Snapshot().Buildings[0].Level);
            Assert.Equal(1000, harness.Snapshot().Gold);
        }

        [Fact]
        public void Upgrade_UnknownOrNotBuiltBuilding_IsSilentlyRejected()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            harness.Upgrade(4242);
            harness.Upgrade(TycoonTestHarness.ShopId);

            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Empty(harness.Wallet.Consumes);
            Assert.Equal(0, harness.Handler.UpgradeCount);
        }

        // ---------- 员工 ----------

        [Fact]
        public void Hire_ChargesQuarterBuildCostAndCapsAtTwiceLevel()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);
            harness.Build(TycoonTestHarness.ShopId);

            harness.Hire(TycoonTestHarness.ShopId);
            harness.Hire(TycoonTestHarness.ShopId);
            harness.Hire(TycoonTestHarness.ShopId);

            // 商店雇员工 300 / 4 = 75，1 级最多 2 人，第三次静默拒绝。
            Assert.Equal(3, harness.Wallet.Consumes.Count);
            Assert.Equal(300, harness.Wallet.Consumes[0].Count);
            Assert.Equal(75, harness.Wallet.Consumes[1].Count);
            Assert.Equal(75, harness.Wallet.Consumes[2].Count);
            Assert.Equal(2, harness.Handler.HiredCount);

            TycoonBuildingState capped = harness.Snapshot().Buildings[0];
            Assert.Equal(2, capped.StaffCount);
            Assert.Equal(550, harness.Snapshot().Gold);

            // 升到 2 级后上限变 4，可以继续雇。
            harness.Upgrade(TycoonTestHarness.ShopId);
            harness.Hire(TycoonTestHarness.ShopId);

            Assert.Equal(3, harness.Handler.HiredCount);
            Assert.Equal(3, harness.Snapshot().Buildings[0].StaffCount);
            Assert.Equal(325, harness.Snapshot().Gold);
        }

        [Fact]
        public void Hire_UnknownOrNotBuiltBuilding_IsSilentlyRejected()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            harness.Hire(4242);
            harness.Hire(TycoonTestHarness.StallId);

            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Empty(harness.Wallet.Consumes);
            Assert.Equal(0, harness.Handler.HiredCount);
        }

        // ---------- tick 结算 ----------

        [Fact]
        public void Tick_GrantsTotalIncomePerTickPerTick()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);
            harness.Build(TycoonTestHarness.ShopId);

            harness.Tick(3);

            Assert.Equal(3, harness.Wallet.Grants.Count);
            for (int i = 0; i < harness.Wallet.Grants.Count; i++)
            {
                Assert.Equal(MetaItemIds.Gold, harness.Wallet.Grants[i].ItemId);
                Assert.Equal(12, harness.Wallet.Grants[i].Count);
                Assert.Equal(6812, harness.Wallet.Grants[i].ReasonTagId);
            }

            Assert.Equal(3, harness.Handler.TickCount);
            Assert.Equal(736, harness.Inventory.GetCount(MetaItemIds.Gold));

            TycoonSnapshot snapshot = harness.Snapshot();
            Assert.Equal(736, snapshot.Gold);
            Assert.Equal(3, snapshot.TickCount);
            Assert.Equal(12, snapshot.TotalIncomePerTick);
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(5, 5)]
        [InlineData(9, 5)]
        [InlineData(-3, 1)]
        public void Tick_ClampsToRangeOneThroughFive(int requested, int expected)
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);
            harness.Build(TycoonTestHarness.ShopId);

            harness.Tick(requested);

            Assert.Equal(expected, harness.Wallet.Grants.Count);
            Assert.Equal(expected, harness.Handler.TickCount);
            Assert.Equal(expected, harness.Snapshot().TickCount);
            Assert.Equal(700 + (expected * 12), harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void Tick_WithoutBuildings_OnlyAdvancesCounterAndSendsNothing()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(500);

            harness.Tick(3);

            // 零收入不发空发奖指令，但 tick 计数照常累加。
            Assert.Empty(harness.Wallet.Grants);
            Assert.Equal(3, harness.Handler.TickCount);

            TycoonSnapshot snapshot = harness.Snapshot();
            Assert.Equal(3, snapshot.TickCount);
            Assert.Equal(500, snapshot.Gold);
            Assert.Empty(snapshot.Buildings);
        }

        // ---------- 快照与金币缓存 ----------

        [Fact]
        public void Snapshot_ReturnsDefensiveCopiesOfBuildingArray()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);
            harness.Build(TycoonTestHarness.StallId);
            harness.Build(TycoonTestHarness.ShopId);

            TycoonSnapshot first = harness.Snapshot();
            TycoonSnapshot second = harness.Snapshot();

            // 每次查询都是新数组：调用方拿到的不是切片内部状态。
            Assert.NotSame(first.Buildings, second.Buildings);
            Assert.NotSame(first, second);

            first.Buildings[0] = new TycoonBuildingState(9999, 99, 99, 9999);

            TycoonSnapshot third = harness.Snapshot();
            Assert.Equal(2, third.Buildings.Length);
            Assert.Equal(TycoonTestHarness.StallId, third.Buildings[0].BuildingId);
            Assert.Equal(1, third.Buildings[0].Level);
            Assert.Equal(0, third.Buildings[0].StaffCount);
            Assert.Equal(5, third.Buildings[0].IncomePerTick);

            // 行序 = 子表行序（摊位在商店之前），不按建造顺序。
            Assert.Equal(TycoonTestHarness.ShopId, third.Buildings[1].BuildingId);
        }

        [Fact]
        public void GoldCache_StaysEqualToInventoryAcrossBuildUpgradeHireAndTick()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            harness.Build(TycoonTestHarness.StallId);      // -100 → 900
            harness.Upgrade(TycoonTestHarness.StallId);    // -50  → 850
            harness.Hire(TycoonTestHarness.StallId);       // -25  → 825
            harness.Tick(2);                               // (5 + 1 × 2) × 2 = 14/次 → +28 → 853

            Assert.Equal(853, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(853, harness.Snapshot().Gold);
            Assert.Equal(2, harness.Snapshot().TickCount);
        }

        [Fact]
        public void GoldCache_PreventsOverspendWhenBackpackSettlementLags()
        {
            // 钱包只记录、不落账：背包永远显示 1000，只有切片缓存知道钱已经花掉。
            TycoonTestHarness harness = TycoonTestHarness.Create(
                TycoonLoaderStub.WithRows(TycoonTestHarness.DefaultRows()),
                1000,
                settleInventory: false);

            harness.Build(TycoonTestHarness.StallId);       // 缓存 900
            harness.Build(TycoonTestHarness.RestaurantId);  // 缓存 100
            harness.Build(TycoonTestHarness.ShopId);        // 100 < 300 → 拒绝

            Assert.Equal(2, harness.Handler.BuiltCount);
            Assert.Equal(2, harness.Wallet.Consumes.Count);
            Assert.Equal(100, harness.Snapshot().Gold);

            // 背包从未落账，且只被查询过一次（缓存不是每笔都轮询）。
            Assert.Equal(1000, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(1, harness.Inventory.QueryCount);

            // 快照顺序仍是子表行序：1 号摊位、3 号餐厅。
            TycoonSnapshot snapshot = harness.Snapshot();
            Assert.Equal(TycoonTestHarness.StallId, snapshot.Buildings[0].BuildingId);
            Assert.Equal(TycoonTestHarness.RestaurantId, snapshot.Buildings[1].BuildingId);
        }

        // ---------- 子表加载 ----------

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            TycoonLoaderStub loader = TycoonLoaderStub.Missing();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new TycoonTables(loader);
                });
            Assert.Contains(TycoonLoaderStub.TableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new TycoonHandler(new CqeBus(), loader);
                });
            Assert.Contains(TycoonLoaderStub.TableName, fromHandler.Message);

            // 两次构造都按名字取同一张子表，没有退化成"随便找一张表"。
            Assert.Equal(2, loader.RequestedTables.Count);
            Assert.All(loader.RequestedTables, name => Assert.Equal(TycoonLoaderStub.TableName, name));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(19)]
        [InlineData(23)]
        public void TruncatedOrOversizedPayload_ThrowsInvalidDataException(int payloadLength)
        {
            // 0..3：头部不足 4 字节；5/19/23：声明 0 行却多出字节。
            TycoonLoaderStub loader = TycoonLoaderStub.Missing().AddTable(new byte[payloadLength]);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonTables(loader);
                });
        }

        [Fact]
        public void TableLengthMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 1 行，实际写了 2 行：长度 44，期望 4 + 1 × 20 = 24。
            byte[] payload = TycoonLoaderStub.BuildPayloadDeclaringRows(
                1,
                new TycoonRowFixture(TycoonTestHarness.StallId, TycoonBuildingKind.Stall, 100, 5, 5),
                new TycoonRowFixture(TycoonTestHarness.ShopId, TycoonBuildingKind.Shop, 300, 12, 5));
            TycoonLoaderStub loader = TycoonLoaderStub.Missing().AddTable(payload);

            InvalidDataException fromTables = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonTables(loader);
                });
            Assert.Contains("长度", fromTables.Message);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonHandler(new CqeBus(), loader);
                });
        }

        [Fact]
        public void DuplicateBuildingId_ThrowsInvalidDataException()
        {
            byte[] payload = TycoonLoaderStub.BuildPayload(
                new TycoonRowFixture(7, TycoonBuildingKind.Stall, 100, 5, 5),
                new TycoonRowFixture(7, TycoonBuildingKind.Shop, 300, 12, 5));
            TycoonLoaderStub loader = TycoonLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonTables(loader);
                });
            Assert.Contains("重复", exception.Message);
        }

        [Fact]
        public void KindOutOfRange_ThrowsInvalidDataException()
        {
            byte[] payload = TycoonLoaderStub.BuildPayload(
                new TycoonRowFixture(1, (TycoonBuildingKind)3, 100, 5, 5));
            TycoonLoaderStub loader = TycoonLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonTables(loader);
                });
            Assert.Contains("kind", exception.Message);
        }

        [Fact]
        public void InvalidNumericFields_ThrowInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonTables(TycoonLoaderStub.WithRows(
                        new TycoonRowFixture(1, TycoonBuildingKind.Stall, -1, 5, 5)));
                });

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonTables(TycoonLoaderStub.WithRows(
                        new TycoonRowFixture(1, TycoonBuildingKind.Stall, 100, -1, 5)));
                });

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TycoonTables(TycoonLoaderStub.WithRows(
                        new TycoonRowFixture(1, TycoonBuildingKind.Stall, 100, 5, 0)));
                });
        }

        [Fact]
        public void EmptyTable_IsValidButNothingCanBeBuilt()
        {
            TycoonTestHarness harness = TycoonTestHarness.Create(TycoonLoaderStub.WithRows(), 500);

            Assert.Equal(0, new TycoonTables(TycoonLoaderStub.WithRows()).Count);

            harness.Build(TycoonTestHarness.StallId);

            // 空表不是加载失败：构造成功，但任何建筑都走"未知建筑"拒绝。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Empty(harness.Wallet.Consumes);
            Assert.Equal(0, harness.Handler.BuiltCount);

            harness.Tick(2);

            Assert.Empty(harness.Wallet.Grants);
            Assert.Equal(2, harness.Handler.TickCount);

            TycoonSnapshot snapshot = harness.Snapshot();
            Assert.Equal(2, snapshot.TickCount);
            Assert.Equal(0, snapshot.TotalIncomePerTick);
            Assert.Empty(snapshot.Buildings);
        }

        // ---------- 总线装配 ----------

        [Fact]
        public void RegisterThroughRealBus_ExposesEveryCommandAndSnapshotQuery()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            // 不经过测试辅助方法，直接用真实总线发送，验证处理器确实挂在总线上。
            harness.Bus.Send(new BuildTycoonCommand(TycoonTestHarness.ShopId));
            harness.Bus.Send(new UpgradeTycoonCommand(TycoonTestHarness.ShopId));
            harness.Bus.Send(new HireStaffCommand(TycoonTestHarness.ShopId));
            harness.Bus.Send(new TycoonTickCommand(1));

            TycoonSnapshot snapshot = harness.Bus.Query<TycoonSnapshotQueryRequest, TycoonSnapshot>(
                new TycoonSnapshotQueryRequest());

            Assert.Equal(1, harness.Handler.BuiltCount);
            Assert.Equal(1, harness.Handler.UpgradeCount);
            Assert.Equal(1, harness.Handler.HiredCount);
            Assert.Equal(1, harness.Handler.TickCount);

            Assert.Equal(511, snapshot.Gold);
            Assert.Equal(1, snapshot.TickCount);
            Assert.Equal(36, snapshot.TotalIncomePerTick);
            Assert.Equal(2, snapshot.Buildings[0].Level);
            Assert.Equal(1, snapshot.Buildings[0].StaffCount);
        }

        [Fact]
        public void RegisterTwiceOnSameBus_ThrowsInvalidOperationException()
        {
            TycoonTestHarness harness = TycoonTestHarness.CreateWithDefaultRows(1000);

            Assert.Throws<InvalidOperationException>(
                () =>
                {
                    harness.Handler.Register(harness.Bus);
                });
        }

        [Fact]
        public void ConstructorAndRegister_NullArguments_ThrowArgumentNullException()
        {
            TycoonLoaderStub loader = TycoonLoaderStub.WithRows(TycoonTestHarness.DefaultRows());

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new TycoonHandler(null!, loader);
                });

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new TycoonHandler(new CqeBus(), null!);
                });

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new TycoonTables(null!);
                });

            TycoonHandler handler = new TycoonHandler(new CqeBus(), loader);
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    handler.Register(null!);
                });
        }

        // ---------- 子表访问器 ----------

        [Fact]
        public void Tables_GetAndTryFindExposeParsedRows()
        {
            TycoonTables tables = new TycoonTables(
                TycoonLoaderStub.WithRows(TycoonTestHarness.DefaultRows()));

            Assert.Equal(3, tables.Count);

            TycoonBuildingDef first = tables.Get(0);
            Assert.Equal(TycoonTestHarness.StallId, first.BuildingId);
            Assert.Equal(TycoonBuildingKind.Stall, first.Kind);
            Assert.Equal(100, first.BuildCost);
            Assert.Equal(5, first.BaseIncome);
            Assert.Equal(5, first.MaxLevel);

            TycoonBuildingDef restaurant;
            Assert.True(tables.TryFind(TycoonTestHarness.RestaurantId, out restaurant));
            Assert.Equal(TycoonBuildingKind.Restaurant, restaurant.Kind);
            Assert.Equal(800, restaurant.BuildCost);
            Assert.Equal(30, restaurant.BaseIncome);
            Assert.Equal(3, restaurant.MaxLevel);

            TycoonBuildingDef missing;
            Assert.False(tables.TryFind(4242, out missing));
            Assert.Equal(0, missing.BuildingId);

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(3);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(-1);
                });
        }
    }
}
