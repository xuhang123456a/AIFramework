using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Features.Merge;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.Merge
{
    /// <summary>
    /// 合成切片验收：真实 <c>CqeBus</c>（经 <see cref="RecordingBus"/> 记录）+ 本切片私有的子表/钱包桩。
    /// 断言只落在三处可观察结果上：棋盘查询快照、切片发出的消息、处理器自己的计数。
    /// </summary>
    public sealed class MergeHandlerTests
    {
        // ---------- 合成正常路径 ----------

        [Fact]
        public void MergeAdjacentSameLevel_UpgradesTargetClearsSourceAndAwardsGold()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 1);
            harness.Spawn(1, 0, 1);

            harness.Merge(0, 0, 1, 0);

            MergeGridSnapshot grid = harness.Query();
            Assert.Equal(6, grid.Width);
            Assert.Equal(6, grid.Height);
            Assert.Equal(0, grid.LevelAt(0, 0));
            Assert.Equal(2, grid.LevelAt(1, 0));
            Assert.Equal(1, grid.UsedCellCount);
            Assert.Equal(1, harness.Handler.MergeCount);
            Assert.Equal(2, harness.Handler.SpawnCount);

            MergePerformedEvent performed = Assert.Single(harness.Bus.PublishedOf<MergePerformedEvent>());
            Assert.Equal(2, performed.NewLevel);
            Assert.Equal(1, performed.GridX);
            Assert.Equal(0, performed.GridY);
            Assert.Equal(MergeTestHarness.Level2SellGold, performed.GoldAwarded);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(MetaItemIds.Gold, grant.ItemId);
            Assert.Equal(MergeTestHarness.Level2SellGold, grant.Count);
            Assert.Equal(6701, grant.ReasonTagId);

            // 金币真的入账（不是只发了一条指令）。
            Assert.Equal(MergeTestHarness.Level2SellGold, harness.Wallet.GetCount(MetaItemIds.Gold));
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(-1, 0)]
        [InlineData(0, 1)]
        [InlineData(0, -1)]
        public void MergeInEveryOrthogonalDirection_Succeeds(int deltaX, int deltaY)
        {
            // 一对同等级物品放在棋盘正中：四个正交方向都在盘内，可用于逐方向验收。
            const int centerX = 3;
            const int centerY = 3;
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(centerX, centerY, 1);
            harness.Spawn(centerX + deltaX, centerY + deltaY, 1);

            harness.Merge(centerX, centerY, centerX + deltaX, centerY + deltaY);

            MergeGridSnapshot grid = harness.Query();
            Assert.Equal(0, grid.LevelAt(centerX, centerY));
            Assert.Equal(2, grid.LevelAt(centerX + deltaX, centerY + deltaY));
            Assert.Equal(1, grid.UsedCellCount);
            Assert.Equal(1, harness.Handler.MergeCount);
        }

        [Fact]
        public void MergeUsesGoldOfNewLevel_NotOfMergedLevel()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 2);
            harness.Spawn(1, 0, 2);

            harness.Merge(0, 0, 1, 0);

            Assert.Equal(3, harness.Query().LevelAt(1, 0));

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(MergeTestHarness.Level3SellGold, grant.Count);
            Assert.NotEqual(MergeTestHarness.Level2SellGold, grant.Count);
            Assert.Equal(MergeTestHarness.Level3SellGold, harness.Wallet.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void MergeToMaxLevel_AwardsMaxLevelSellGold()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, MergeTestHarness.MaxLevel - 1);
            harness.Spawn(1, 0, MergeTestHarness.MaxLevel - 1);

            harness.Merge(0, 0, 1, 0);

            Assert.Equal(MergeTestHarness.MaxLevel, harness.Query().LevelAt(1, 0));

            MergePerformedEvent performed = Assert.Single(harness.Bus.PublishedOf<MergePerformedEvent>());
            Assert.Equal(MergeTestHarness.MaxLevel, performed.NewLevel);
            Assert.Equal(MergeTestHarness.Level4SellGold, performed.GoldAwarded);
            Assert.Equal(MergeTestHarness.Level4SellGold, harness.Wallet.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void MergeWithZeroSellGold_PublishesEventWithoutGrantCommand()
        {
            // 2 级售价配成 0：合成成功、事件照发，但不该再发一条数量为 0 的金币指令。
            MergeRowFixture[] rows =
            {
                new MergeRowFixture(1, MergeTestHarness.Level1ItemId, MergeTestHarness.Level1SellGold),
                new MergeRowFixture(2, MergeTestHarness.Level1ItemId + 1, 0),
            };
            MergeTestHarness harness = MergeTestHarness.Create(MergeLoaderStub.WithRows(rows));
            harness.Spawn(0, 0, 1);
            harness.Spawn(1, 0, 1);

            harness.Merge(0, 0, 1, 0);

            MergePerformedEvent performed = Assert.Single(harness.Bus.PublishedOf<MergePerformedEvent>());
            Assert.Equal(2, performed.NewLevel);
            Assert.Equal(0, performed.GoldAwarded);
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(0, harness.Wallet.GetCount(MetaItemIds.Gold));
            Assert.Equal(1, harness.Handler.MergeCount);
        }

        [Fact]
        public void ConsecutiveMerges_ChainOnePlusOneThenTwoPlusTwo()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 1);
            harness.Spawn(1, 0, 1);

            harness.Merge(0, 0, 1, 0);

            Assert.Equal(2, harness.Query().LevelAt(1, 0));
            Assert.Equal(1, harness.Handler.MergeCount);

            // 再补一个 2 级物品，与刚才合成的 2 级继续合成。
            harness.Spawn(0, 0, 2);
            harness.Merge(0, 0, 1, 0);

            MergeGridSnapshot grid = harness.Query();
            Assert.Equal(3, grid.LevelAt(1, 0));
            Assert.Equal(0, grid.LevelAt(0, 0));
            Assert.Equal(1, grid.UsedCellCount);
            Assert.Equal(2, harness.Handler.MergeCount);

            List<MergePerformedEvent> events = harness.Bus.PublishedOf<MergePerformedEvent>();
            Assert.Equal(2, events.Count);
            Assert.Equal(2, events[0].NewLevel);
            Assert.Equal(3, events[1].NewLevel);
            Assert.Equal(1, events[0].GridX);
            Assert.Equal(1, events[1].GridX);

            int expectedGold = MergeTestHarness.Level2SellGold + MergeTestHarness.Level3SellGold;
            Assert.Equal(expectedGold, harness.Wallet.GetCount(MetaItemIds.Gold));
        }

        // ---------- 合成静默拒绝 ----------

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 0)]
        [InlineData(0, 0)]
        public void MergeNonAdjacentSameLevelCells_AreSilentlyRejected(int toX, int toY)
        {
            // 目标格提前放好同等级物品：拒绝只能来自"不相邻"（对角 / 隔一格 / 同一格）。
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 1);
            if (toX != 0 || toY != 0)
            {
                harness.Spawn(toX, toY, 1);
            }

            harness.Merge(0, 0, toX, toY);

            AssertSilentlyRejected(harness);
            Assert.Equal(1, harness.Query().LevelAt(0, 0));
            Assert.Equal(1, harness.Query().LevelAt(toX, toY));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void MergeWithEmptyOrDifferentLevelTarget_IsSilentlyRejected(int targetLevel)
        {
            // 0 = 目标格为空（空 + 物品不构成合成）；2 = 目标格被不同等级占位。
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 1);
            if (targetLevel != 0)
            {
                harness.Spawn(1, 0, targetLevel);
            }

            harness.Merge(0, 0, 1, 0);

            AssertSilentlyRejected(harness);
            Assert.Equal(1, harness.Query().LevelAt(0, 0));
            Assert.Equal(targetLevel, harness.Query().LevelAt(1, 0));
        }

        [Fact]
        public void MergeFromEmptySource_IsSilentlyRejected()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(1, 0, 1);

            harness.Merge(0, 0, 1, 0);

            AssertSilentlyRejected(harness);
            Assert.Equal(1, harness.Query().LevelAt(1, 0));
            Assert.Equal(1, harness.Query().UsedCellCount);
        }

        [Fact]
        public void MergeAtMaxLevel_IsSilentlyRejected()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, MergeTestHarness.MaxLevel);
            harness.Spawn(1, 0, MergeTestHarness.MaxLevel);

            harness.Merge(0, 0, 1, 0);

            // 满级不可再合并：不产出 MaxLevel + 1 这种表里不存在的等级。
            AssertSilentlyRejected(harness);
            Assert.Equal(MergeTestHarness.MaxLevel, harness.Query().LevelAt(0, 0));
            Assert.Equal(MergeTestHarness.MaxLevel, harness.Query().LevelAt(1, 0));
            Assert.Equal(2, harness.Query().UsedCellCount);
        }

        [Theory]
        [InlineData(-1, 0, 0, 0)]
        [InlineData(0, -1, 0, 0)]
        [InlineData(0, 0, 6, 0)]
        [InlineData(0, 0, 0, 6)]
        public void MergeOutOfBounds_IsSilentlyRejected(int fromX, int fromY, int toX, int toY)
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 1);
            harness.Spawn(1, 0, 1);

            harness.Merge(fromX, fromY, toX, toY);

            AssertSilentlyRejected(harness);
            Assert.Equal(1, harness.Query().LevelAt(0, 0));
            Assert.Equal(1, harness.Query().LevelAt(1, 0));
        }

        // ---------- 生成 ----------

        [Fact]
        public void SpawnIntoEmptyCell_PlacesItemAndCountsWithoutBroadcasting()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();

            harness.Spawn(4, 2, 3);

            Assert.Equal(3, harness.Query().LevelAt(4, 2));
            Assert.Equal(1, harness.Handler.SpawnCount);
            Assert.Empty(harness.Bus.PublishedEvents);
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
        }

        [Theory]
        [InlineData(6, 0, 1)]
        [InlineData(0, 6, 1)]
        [InlineData(-1, 0, 1)]
        [InlineData(0, -1, 1)]
        [InlineData(0, 0, 0)]
        [InlineData(0, 0, -1)]
        [InlineData(0, 0, MergeTestHarness.MaxLevel + 1)]
        public void SpawnWithIllegalArguments_IsSilentlyRejected(int x, int y, int level)
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();

            harness.Spawn(x, y, level);

            Assert.Equal(0, harness.Handler.SpawnCount);
            Assert.Equal(0, harness.Query().UsedCellCount);
            Assert.Empty(harness.Bus.PublishedEvents);
        }

        [Fact]
        public void SpawnIntoOccupiedCell_IsSilentlyRejected()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 1);

            harness.Spawn(0, 0, 2);

            // 已有物品不被覆盖，计数也不增加。
            Assert.Equal(1, harness.Query().LevelAt(0, 0));
            Assert.Equal(1, harness.Handler.SpawnCount);
        }

        // ---------- 棋盘查询 ----------

        [Fact]
        public void GridQuery_IsRowMajorAndReportsUsedCellCount()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();

            MergeGridSnapshot emptyGrid = harness.Query();
            Assert.Equal(6, emptyGrid.Width);
            Assert.Equal(6, emptyGrid.Height);
            Assert.Equal(0, emptyGrid.UsedCellCount);

            harness.Spawn(0, 0, 1);
            harness.Spawn(5, 5, 3);
            harness.Spawn(2, 4, 2);

            MergeGridSnapshot grid = harness.Query();
            Assert.Equal(1, grid.LevelAt(0, 0));
            Assert.Equal(3, grid.LevelAt(5, 5));
            Assert.Equal(2, grid.LevelAt(2, 4));
            Assert.Equal(0, grid.LevelAt(2, 2));
            Assert.Equal(3, grid.UsedCellCount);
            Assert.Equal(-1, grid.LevelAt(6, 0));
            Assert.Equal(-1, grid.LevelAt(0, -1));
        }

        [Fact]
        public void GridQuery_SnapshotIsIndependentOfLaterMerges()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();
            harness.Spawn(0, 0, 1);
            harness.Spawn(1, 0, 1);

            MergeGridSnapshot before = harness.Query();
            harness.Merge(0, 0, 1, 0);

            // 查询之后棋盘变了，已发出的快照仍是当时的状态。
            Assert.Equal(1, before.LevelAt(0, 0));
            Assert.Equal(1, before.LevelAt(1, 0));
            Assert.Equal(2, before.UsedCellCount);

            MergeGridSnapshot after = harness.Query();
            Assert.Equal(0, after.LevelAt(0, 0));
            Assert.Equal(2, after.LevelAt(1, 0));
            Assert.Equal(1, after.UsedCellCount);
        }

        [Fact]
        public void Snapshot_LevelAtIsRowMajorAndBoundsSafe()
        {
            // 3 列 × 4 行：先按行优先铺满（值 = y * 10 + x + 1，保证全非 0），再把两格抹成 0（空格）。
            int[] levels = new int[12];
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 3; x++)
                {
                    levels[(y * 3) + x] = (y * 10) + x + 1;
                }
            }

            levels[(2 * 3) + 1] = 0;
            levels[(3 * 3) + 2] = 0;

            MergeGridSnapshot snapshot = new MergeGridSnapshot(3, 4, levels);

            Assert.Equal(3, snapshot.Width);
            Assert.Equal(4, snapshot.Height);
            Assert.Equal(1, snapshot.LevelAt(0, 0));
            Assert.Equal(3, snapshot.LevelAt(2, 0));
            Assert.Equal(11, snapshot.LevelAt(0, 1));
            Assert.Equal(32, snapshot.LevelAt(1, 3));
            Assert.Equal(0, snapshot.LevelAt(1, 2));
            Assert.Equal(10, snapshot.UsedCellCount);

            // 越界返回 -1，与"空格 0"严格区分。
            Assert.Equal(-1, snapshot.LevelAt(-1, 0));
            Assert.Equal(-1, snapshot.LevelAt(0, -1));
            Assert.Equal(-1, snapshot.LevelAt(3, 0));
            Assert.Equal(-1, snapshot.LevelAt(0, 4));

            // 构造后改原数组不影响已建好的快照。
            levels[0] = 99;
            Assert.Equal(1, snapshot.LevelAt(0, 0));
        }

        [Fact]
        public void Snapshot_RejectsMalformedConstruction()
        {
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new MergeGridSnapshot(2, 2, null!);
                });
            Assert.Throws<ArgumentException>(
                () =>
                {
                    new MergeGridSnapshot(2, 2, new int[3]);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    new MergeGridSnapshot(0, 2, new int[0]);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    new MergeGridSnapshot(2, 0, new int[0]);
                });
        }

        // ---------- 子表加载与校验 ----------

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            MergeLoaderStub loader = MergeLoaderStub.Missing();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new MergeTables(loader);
                });
            Assert.Contains(MergeLoaderStub.TableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new MergeHandler(new RecordingBus(), loader);
                });
            Assert.Contains(MergeLoaderStub.TableName, fromHandler.Message);

            // 两次构造都按名字取同一张子表，没有退化成"随便找一张表"。
            Assert.Equal(2, loader.RequestedTables.Count);
            Assert.All(loader.RequestedTables, name => Assert.Equal(MergeLoaderStub.TableName, name));
        }

        [Fact]
        public void TableLengthMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 1 行，实际写了 2 行：长度 28，期望 4 + 1 * 12 = 16。
            byte[] payload = MergeLoaderStub.BuildPayloadDeclaringRows(
                1,
                new MergeRowFixture(1, MergeTestHarness.Level1ItemId, MergeTestHarness.Level1SellGold),
                new MergeRowFixture(2, MergeTestHarness.Level1ItemId + 1, MergeTestHarness.Level2SellGold));
            MergeLoaderStub loader = MergeLoaderStub.Missing().AddTable(payload);

            InvalidDataException fromTables = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MergeTables(loader);
                });
            Assert.Contains("长度", fromTables.Message);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MergeHandler(new RecordingBus(), loader);
                });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(5)]
        public void MalformedHeader_ThrowsInvalidDataException(int payloadLength)
        {
            // 0/2 字节：头部不足 4 字节；5 字节：声明 0 行却多出 1 字节。
            MergeLoaderStub loader = MergeLoaderStub.Missing().AddTable(new byte[payloadLength]);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MergeTables(loader);
                });
        }

        [Fact]
        public void DuplicateLevel_ThrowsInvalidDataException()
        {
            byte[] payload = MergeLoaderStub.BuildPayload(
                new MergeRowFixture(1, MergeTestHarness.Level1ItemId, MergeTestHarness.Level1SellGold),
                new MergeRowFixture(1, MergeTestHarness.Level1ItemId + 1, MergeTestHarness.Level2SellGold));
            MergeLoaderStub loader = MergeLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MergeTables(loader);
                });
            Assert.Contains("重复", exception.Message);
        }

        [Theory]
        [InlineData(new[] { 1, 3 })]
        [InlineData(new[] { 2, 3 })]
        [InlineData(new[] { 1, 2, 4 })]
        public void NonContiguousLevels_ThrowInvalidDataException(int[] levels)
        {
            MergeRowFixture[] rows = new MergeRowFixture[levels.Length];
            for (int i = 0; i < levels.Length; i++)
            {
                rows[i] = new MergeRowFixture(
                    levels[i],
                    MergeTestHarness.Level1ItemId + i,
                    MergeTestHarness.Level1SellGold * levels[i]);
            }

            MergeLoaderStub loader = MergeLoaderStub.Missing().AddTable(MergeLoaderStub.BuildPayload(rows));

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MergeTables(loader);
                });
            Assert.Contains("连续", exception.Message);
        }

        [Theory]
        [InlineData(0, MergeTestHarness.Level1ItemId)]
        [InlineData(-1, MergeTestHarness.Level1ItemId)]
        [InlineData(1, 0)]
        [InlineData(1, -1)]
        public void NonPositiveLevelOrItemId_ThrowInvalidDataException(int level, int itemId)
        {
            MergeLoaderStub loader = MergeLoaderStub.Missing().AddTable(
                MergeLoaderStub.BuildPayload(new MergeRowFixture(level, itemId, MergeTestHarness.Level1SellGold)));

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new MergeTables(loader);
                });
            Assert.Contains(level <= 0 ? "level" : "itemId", exception.Message);
        }

        [Fact]
        public void EmptyTable_IsValidButAcceptsNothing()
        {
            // 0 行是合法配置（没有人往盘上铺东西），不是加载失败。
            MergeTestHarness harness = MergeTestHarness.Create(MergeLoaderStub.WithRows());

            Assert.Equal(0, harness.Handler.MergeCount);
            Assert.Equal(0, harness.Handler.SpawnCount);
            Assert.Equal(0, harness.Query().UsedCellCount);

            harness.Spawn(0, 0, 1);

            // 空等级链没有任何合法等级：生成同样被拒绝。
            Assert.Equal(0, harness.Handler.SpawnCount);
            Assert.Equal(0, harness.Query().UsedCellCount);
        }

        [Fact]
        public void Tables_ExposeCountMaxLevelAndLevelLookups()
        {
            MergeTables tables = new MergeTables(MergeLoaderStub.WithRows(MergeTestHarness.DefaultRows()));

            Assert.Equal(MergeTestHarness.MaxLevel, tables.Count);
            Assert.Equal(MergeTestHarness.MaxLevel, tables.MaxLevel);

            MergeItemDef first = tables.Get(0);
            Assert.Equal(1, first.Level);
            Assert.Equal(MergeTestHarness.Level1ItemId, first.ItemId);
            Assert.Equal(MergeTestHarness.Level1SellGold, first.SellGold);

            // 物理行序被打乱时 GetByLevel 仍按等级取值，不能退化成"行序猜等级"。
            MergeRowFixture[] disordered =
            {
                new MergeRowFixture(2, MergeTestHarness.Level1ItemId + 1, MergeTestHarness.Level2SellGold),
                new MergeRowFixture(1, MergeTestHarness.Level1ItemId, MergeTestHarness.Level1SellGold),
            };
            MergeTables shuffled = new MergeTables(MergeLoaderStub.WithRows(disordered));
            Assert.Equal(2, shuffled.Get(0).Level);
            Assert.Equal(1, shuffled.GetByLevel(1).Level);
            Assert.Equal(MergeTestHarness.Level1ItemId, shuffled.GetByLevel(1).ItemId);
            Assert.Equal(MergeTestHarness.Level2SellGold, shuffled.GetByLevel(2).SellGold);

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    shuffled.Get(MergeTestHarness.MaxLevel);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    shuffled.Get(-1);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    shuffled.GetByLevel(0);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    shuffled.GetByLevel(MergeTestHarness.MaxLevel + 1);
                });
        }

        // ---------- 构造与注册守卫 ----------

        [Fact]
        public void NullArguments_ThrowArgumentNullException()
        {
            MergeLoaderStub loader = MergeLoaderStub.WithRows(MergeTestHarness.DefaultRows());

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new MergeHandler(null!, loader);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new MergeHandler(new RecordingBus(), null!);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new MergeTables(null!);
                });

            MergeHandler handler = new MergeHandler(new RecordingBus(), loader);
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    handler.Register(null!);
                });
        }

        [Fact]
        public void Register_MakesHandlerReachableThroughTheRealBus()
        {
            MergeTestHarness harness = MergeTestHarness.CreateWithDefaultRows();

            // 绕过记录包装，直接打真实 CqeBus：证明确实在总线上注册过，而不是只有测试替身在转发。
            harness.Bus.Inner.Send(new SpawnItemCommand(0, 0, 1));
            harness.Bus.Inner.Send(new SpawnItemCommand(1, 0, 1));
            harness.Bus.Inner.Send(new MergeCellsCommand(0, 0, 1, 0));

            MergeGridSnapshot grid = harness.Bus.Inner.Query<MergeGridQueryRequest, MergeGridSnapshot>(
                new MergeGridQueryRequest());

            Assert.Equal(2, grid.LevelAt(1, 0));
            Assert.Equal(0, grid.LevelAt(0, 0));
            Assert.Equal(1, harness.Handler.MergeCount);

            // 真实总线上的发奖处理器也收到了金币。
            Assert.Equal(MergeTestHarness.Level2SellGold, harness.Wallet.GetCount(MetaItemIds.Gold));
        }

        /// <summary>静默拒绝的统一断言：不改计数、不发金币指令、不发事件。</summary>
        private static void AssertSilentlyRejected(MergeTestHarness harness)
        {
            Assert.Equal(0, harness.Handler.MergeCount);
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedEvents);
        }
    }
}
