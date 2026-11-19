using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Features.IdleTycoon;
using Xunit;

namespace AIFramework.Features.Tests.IdleTycoon
{
    /// <summary>
    /// 放置/经营玩法内核验收：真实 <c>CqeBus</c>（经 <c>RecordingBus</c> 记录）+ 本切片自带的桩端口。
    /// 断言只落在"切片发出了什么消息"、"等级/每秒产出"、"金币余额"三处；
    /// 时间一律通过 <see cref="IdleTimeStub"/> 快进，绝不等待真实时间。
    /// </summary>
    public sealed class IdleTycoonHandlerTests
    {
        // ---------- 初始状态 ----------

        [Fact]
        public void NewHandler_StartsAtLevelZeroAndZeroIncomeWithoutTouchingTheBus()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();

            Assert.Equal(0, harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(0, harness.Handler.GetLevel(IdleTycoonHarness.FarmId));
            Assert.Equal(0, harness.Handler.GetLevel(IdleTycoonHarness.MineId));
            Assert.Equal(0, harness.Handler.IncomePerSecond);
            Assert.Equal(0, harness.Handler.GetTotalUpgradesPurchased);

            // 构造期只读表：不发指令、不发事件、不查背包。
            Assert.Empty(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.PublishedEvents);
            Assert.Empty(harness.Bus.Queries);
        }

        [Fact]
        public void GetLevel_UnknownBuildingId_ThrowsArgumentOutOfRange()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();

            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Handler.GetLevel(4242));
        }

        // ---------- 升级正常路径 ----------

        [Fact]
        public void Upgrade_WithEnoughGold_ConsumesGoldThenRaisesLevelAndIncome()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Upgrade(IdleTycoonHarness.FactoryId);

            Assert.Equal(1, harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(IdleTycoonHarness.FactoryBaseIncome, harness.Handler.IncomePerSecond);
            Assert.Equal(1, harness.Handler.GetTotalUpgradesPurchased);
            Assert.Equal(1000 - IdleTycoonHarness.FactoryUpgradeCost, harness.Inventory.GetCount(MetaItemIds.Gold));

            ConsumeItemCommand consume = Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(MetaItemIds.Gold, consume.ItemId);
            Assert.Equal(IdleTycoonHarness.FactoryUpgradeCost, consume.Count);
            Assert.Equal(6401, consume.ReasonTagId);

            // 命令本身 + 扣款指令，顺序固定；升级成功不发任何事件。
            Assert.Equal(2, harness.Bus.SentCommands.Count);
            Assert.IsType<UpgradeBuildingCommand>(harness.Bus.SentCommands[0]);
            Assert.IsType<ConsumeItemCommand>(harness.Bus.SentCommands[1]);
            Assert.Empty(harness.Bus.PublishedEvents);

            // 只查一次余额，且查的是金币。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Equal(MetaItemIds.Gold, harness.Inventory.QueriedItemIds[0]);
        }

        [Fact]
        public void Upgrade_WithExactlyEnoughGold_SucceedsBecauseBalanceIsInclusive()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, IdleTycoonHarness.FactoryUpgradeCost);

            harness.Upgrade(IdleTycoonHarness.FactoryId);

            Assert.Equal(1, harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(0, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
        }

        [Fact]
        public void Upgrade_DifferentBuilding_UsesItsOwnCostAndBaseIncome()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Upgrade(IdleTycoonHarness.FarmId);

            Assert.Equal(1, harness.Handler.GetLevel(IdleTycoonHarness.FarmId));
            Assert.Equal(IdleTycoonHarness.FarmBaseIncome, harness.Handler.IncomePerSecond);
            Assert.Equal(1000 - IdleTycoonHarness.FarmUpgradeCost, harness.Inventory.GetCount(MetaItemIds.Gold));

            ConsumeItemCommand consume = Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(IdleTycoonHarness.FarmUpgradeCost, consume.Count);
        }

        [Fact]
        public void Upgrade_Repeatedly_ScalesIncomeByBaseIncomeTimesLevel()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Upgrade(IdleTycoonHarness.FactoryId);

            Assert.Equal(2, harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(2 * IdleTycoonHarness.FactoryBaseIncome, harness.Handler.IncomePerSecond);
            Assert.Equal(2, harness.Handler.GetTotalUpgradesPurchased);
            Assert.Equal(1000 - (2 * IdleTycoonHarness.FactoryUpgradeCost), harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(2, harness.Bus.CountSent<ConsumeItemCommand>());
        }

        [Fact]
        public void IncomePerSecond_SumsAllBuildingsByLevelTimesBaseIncome()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Upgrade(IdleTycoonHarness.FarmId);
            harness.Upgrade(IdleTycoonHarness.MineId);

            Assert.Equal(
                IdleTycoonHarness.FactoryBaseIncome + IdleTycoonHarness.FarmBaseIncome + 1,
                harness.Handler.IncomePerSecond);
            Assert.Equal(1000 - (100 + 250 + 40), harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        // ---------- 静默拒绝 ----------

        [Fact]
        public void Upgrade_WithoutEnoughGold_IsSilentlyRejectedAndKeepsLevel()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, IdleTycoonHarness.FactoryUpgradeCost - 1);

            harness.Upgrade(IdleTycoonHarness.FactoryId);

            Assert.Equal(0, harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(0, harness.Handler.IncomePerSecond);
            Assert.Equal(0, harness.Handler.GetTotalUpgradesPurchased);
            Assert.Equal(IdleTycoonHarness.FactoryUpgradeCost - 1, harness.Inventory.GetCount(MetaItemIds.Gold));

            // 余额是"查了才知道"的：只有查询发生，没有任何扣款与事件。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.PublishedEvents);
        }

        [Fact]
        public void Upgrade_UnknownBuilding_IsSilentlyRejectedWithoutTouchingInventory()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);

            harness.Upgrade(4242);

            // 未知建筑连背包都不该查。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.PublishedEvents);
            Assert.Equal(9999, harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void Upgrade_AtMaxLevel_IsSilentlyRejectedWithoutQueryingInventory()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Upgrade(IdleTycoonHarness.FactoryId);

            Assert.Equal(3, harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(3 * IdleTycoonHarness.FactoryBaseIncome, harness.Handler.IncomePerSecond);
            Assert.Equal(700, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(3, harness.Inventory.QueryCount);

            // 第 4 次：已满级，静默拒绝，连余额都不查。
            harness.Upgrade(IdleTycoonHarness.FactoryId);

            Assert.Equal(3, harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(3, harness.Handler.GetTotalUpgradesPurchased);
            Assert.Equal(3, harness.Inventory.QueryCount);
            Assert.Equal(700, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(3, harness.Bus.CountSent<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.PublishedEvents);
        }

        // ---------- 离线收益 ----------

        [Fact]
        public void SettleOfflineIncome_TenSeconds_GrantsGoldAndPublishesOneEventPerLeveledBuilding()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Upgrade(IdleTycoonHarness.FarmId);
            Assert.Equal(650, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(17, harness.Handler.IncomePerSecond);

            harness.Time.AdvanceSeconds(10d);
            int amount = harness.Settle();

            // 10 秒 × 17/秒 = 170，向下取整。
            Assert.Equal(170, amount);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(MetaItemIds.Gold, grant.ItemId);
            Assert.Equal(170, grant.Count);
            Assert.Equal(6402, grant.ReasonTagId);
            Assert.Equal(650 + 170, harness.Inventory.GetCount(MetaItemIds.Gold));

            // 只有 2 栋有等级的建筑各发一条；0 级的矿场不发。
            List<IdleIncomeCollectedEvent> collected = harness.Bus.PublishedOf<IdleIncomeCollectedEvent>();
            Assert.Equal(2, collected.Count);
            Assert.Equal(IdleTycoonHarness.FactoryId, collected[0].BuildingId);
            Assert.Equal(50, collected[0].Amount);
            Assert.Equal(IdleTycoonHarness.FarmId, collected[1].BuildingId);
            Assert.Equal(120, collected[1].Amount);
            Assert.Equal(amount, collected[0].Amount + collected[1].Amount);
        }

        [Fact]
        public void SettleOfflineIncome_FractionalSeconds_EventAmountsSumToGrantedTotal()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Upgrade(IdleTycoonHarness.MineId);
            Assert.Equal(6, harness.Handler.IncomePerSecond);

            harness.Time.AdvanceSeconds(3.5d);
            int amount = harness.Settle();

            // floor(3.5 × 6) = 21；逐栋向下取整会得到 17 + 3 = 20，差 1 金币。
            Assert.Equal(21, amount);

            List<IdleIncomeCollectedEvent> collected = harness.Bus.PublishedOf<IdleIncomeCollectedEvent>();
            Assert.Equal(2, collected.Count);
            Assert.Equal(17, collected[0].Amount);
            Assert.Equal(4, collected[1].Amount);
            Assert.Equal(amount, collected[0].Amount + collected[1].Amount);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(21, grant.Count);
        }

        [Fact]
        public void SettleOfflineIncome_CalledTwiceImmediately_SecondReturnsZeroAndGrantsNothing()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Time.AdvanceSeconds(10d);

            Assert.Equal(50, harness.Settle());
            Assert.Equal(0, harness.Settle());

            Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Single(harness.Bus.PublishedOf<IdleIncomeCollectedEvent>());
            Assert.Equal(1000 - 100 + 50, harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void SettleOfflineIncome_Beyond24Hours_IsCappedAt24Hours()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Time.AdvanceHours(48d);

            int amount = harness.Settle();

            // 48 小时只能按 24 小时结算：86400 × 5 = 432000。
            Assert.Equal(86400 * IdleTycoonHarness.FactoryBaseIncome, amount);
            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(432000, grant.Count);
        }

        [Fact]
        public void SettleOfflineIncome_AfterCappedSettlement_NextSettlementCountsFromNow()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Upgrade(IdleTycoonHarness.FactoryId);
            harness.Time.AdvanceHours(48d);

            Assert.Equal(432000, harness.Settle());

            // 封顶后基线已对齐到"现在"，再离线 10 秒就是 50，不会把被丢弃的 24 小时找回。
            harness.Time.AdvanceSeconds(10d);
            Assert.Equal(50, harness.Settle());
            Assert.Equal(2, harness.Bus.CountSent<GrantItemCommand>());
        }

        [Fact]
        public void SettleOfflineIncome_WithoutAnyLeveledBuilding_ReturnsZeroAndSendsNothing()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            harness.Time.AdvanceSeconds(10d);

            Assert.Equal(0, harness.Settle());

            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedEvents);
            Assert.Equal(0, harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void SettleOfflineIncome_ClockRollback_ReturnsZeroAndResetsBaselineToNow()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.Create(
                IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows()),
                100d);
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Upgrade(IdleTycoonHarness.FactoryId);

            // 时钟回拨（now 50 < 构造基线 100）按 0 结算，不发金币。
            harness.Time.SetElapsedSeconds(50d);
            Assert.Equal(0, harness.Settle());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());

            // 回拨后的基线是"现在"（50 秒），再走到 60 秒正好 10 秒产出。
            harness.Time.SetElapsedSeconds(60d);
            Assert.Equal(50, harness.Settle());
        }

        // ---------- 时间源可替换性 ----------

        [Fact]
        public void SettleOfflineIncome_BaselineComesFromTimeProviderAtConstructionNotFromZero()
        {
            IdleTycoonHarness harness = IdleTycoonHarness.Create(
                IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows()),
                3600d);
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Upgrade(IdleTycoonHarness.FactoryId);

            harness.Time.AdvanceSeconds(10d);

            // 起点是 3600 秒也没关系：只算构造之后的 10 秒，不把 1 小时误算成离线收益。
            Assert.Equal(50, harness.Settle());
        }

        [Fact]
        public void SettleOfflineIncome_SwappingTheTimeSourceDoesNotChangeTheOutcome()
        {
            IdleTycoonHarness early = IdleTycoonHarness.Create(
                IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows()),
                0d);
            IdleTycoonHarness late = IdleTycoonHarness.Create(
                IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows()),
                1000000d);

            early.Inventory.SetCount(MetaItemIds.Gold, 1000);
            late.Inventory.SetCount(MetaItemIds.Gold, 1000);
            early.Upgrade(IdleTycoonHarness.FactoryId);
            late.Upgrade(IdleTycoonHarness.FactoryId);
            early.Time.AdvanceSeconds(10d);
            late.Time.AdvanceSeconds(10d);

            Assert.Equal(50, early.Settle());
            Assert.Equal(50, late.Settle());
            Assert.Equal(early.Bus.PublishedOf<IdleIncomeCollectedEvent>()[0].Amount, late.Bus.PublishedOf<IdleIncomeCollectedEvent>()[0].Amount);
        }

        // ---------- 子表解析 ----------

        [Fact]
        public void Tables_ValidRows_AreParsedInTableOrder()
        {
            IdleLoaderStub loader = IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows());

            IdleTables tables = new IdleTables(loader);

            Assert.Equal(3, tables.Count);
            Assert.Equal(IdleLoaderStub.TableName, loader.RequestedTables[0]);

            BuildingDef factory = tables.Get(0);
            Assert.Equal(IdleTycoonHarness.FactoryId, factory.BuildingId);
            Assert.Equal(IdleTycoonHarness.FactoryBaseIncome, factory.BaseIncome);
            Assert.Equal(IdleTycoonHarness.FactoryUpgradeCost, factory.UpgradeCost);
            Assert.Equal(3, factory.MaxLevel);

            BuildingDef farm = tables.Get(1);
            Assert.Equal(IdleTycoonHarness.FarmId, farm.BuildingId);
            Assert.Equal(IdleTycoonHarness.FarmBaseIncome, farm.BaseIncome);
            Assert.Equal(IdleTycoonHarness.FarmUpgradeCost, farm.UpgradeCost);
            Assert.Equal(2, farm.MaxLevel);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        public void Tables_GetOutOfRange_ThrowsArgumentOutOfRange(int index)
        {
            IdleTables tables = new IdleTables(IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows()));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(index));
        }

        [Fact]
        public void Tables_ZeroRows_IsLegalEmptyTableNotALoadFailure()
        {
            IdleTables tables = new IdleTables(IdleLoaderStub.WithRows());

            Assert.Equal(0, tables.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(0));

            // 空表也能装配处理器：任何升级都走"未知建筑"静默拒绝。
            IdleTycoonHarness harness = IdleTycoonHarness.Create(IdleLoaderStub.WithRows());
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);
            harness.Upgrade(IdleTycoonHarness.FactoryId);

            Assert.Equal(0, harness.Handler.IncomePerSecond);
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
        }

        [Fact]
        public void Tables_MissingTable_ThrowsInvalidOperationAndSliceRefusesToEnable()
        {
            Assert.Throws<InvalidOperationException>(() => { _ = new IdleTables(IdleLoaderStub.Missing()); });

            // 装配层拿到缺失子表时构造即失败：切片显式拒绝启用，而不是带着空表继续跑。
            IdleTycoonHarness harness = IdleTycoonHarness.CreateWithDefaultRows();
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = new IdleTycoonHandler(harness.Bus, harness.Time, IdleLoaderStub.Missing());
            });
        }

        [Fact]
        public void Tables_DeclaredRowCountMismatch_ThrowsInvalidData()
        {
            // 头部声明 2 行，实际只写了 1 行。
            byte[] payload = IdleLoaderStub.BuildPayloadDeclaringRows(
                2,
                new IdleBuildingRow(IdleTycoonHarness.FactoryId, 5, 100, 3));

            Assert.Throws<InvalidDataException>(() => { _ = new IdleTables(new IdleLoaderStub().AddTable(payload)); });
        }

        [Fact]
        public void Tables_TruncatedHeader_ThrowsInvalidData()
        {
            byte[] payload = new byte[] { 1, 0 };

            Assert.Throws<InvalidDataException>(() => { _ = new IdleTables(new IdleLoaderStub().AddTable(payload)); });
        }

        [Fact]
        public void Tables_TrailingBytes_ThrowsInvalidData()
        {
            // 声明 0 行但实际给了 20 字节：长度不符，不允许"多出来的字节当没事"。
            byte[] payload = new byte[20];

            Assert.Throws<InvalidDataException>(() => { _ = new IdleTables(new IdleLoaderStub().AddTable(payload)); });
        }

        [Fact]
        public void Tables_DuplicateBuildingId_ThrowsInvalidData()
        {
            IdleLoaderStub loader = IdleLoaderStub.WithRows(
                new IdleBuildingRow(7, 5, 100, 3),
                new IdleBuildingRow(7, 9, 200, 2));

            Assert.Throws<InvalidDataException>(() => { _ = new IdleTables(loader); });
        }

        [Theory]
        [InlineData(0, 100, 3)]
        [InlineData(-5, 100, 3)]
        [InlineData(5, -1, 3)]
        [InlineData(5, 100, 0)]
        [InlineData(5, 100, -2)]
        public void Tables_IllegalRowValues_ThrowInvalidData(int baseIncome, int upgradeCost, int maxLevel)
        {
            IdleLoaderStub loader = IdleLoaderStub.WithRows(
                new IdleBuildingRow(1, baseIncome, upgradeCost, maxLevel));

            Assert.Throws<InvalidDataException>(() => { _ = new IdleTables(loader); });
        }
    }
}
