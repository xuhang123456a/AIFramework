using System;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Tests.Fakes;
using AIFramework.Features.TowerDefense;
using Xunit;

namespace AIFramework.Features.Tests.TowerDefense
{
    /// <summary>
    /// 塔防 Meta 切片验收：真实 <c>CqeBus</c>（经 <see cref="RecordingBus"/> 记录）+ 本切片自带的桩端口，
    /// 断言只落在"切片发出了什么消息"与"布局快照内容"两处。
    /// <para>
    /// <b>为什么战斗结算不在本文件里断言</b>：塔防切片根本没有战斗逻辑，
    /// 伤害/波次属于 <c>Kernel.Realtime</c>；在这里造一场战斗只会把两个切片的契约揉在一起。
    /// </para>
    /// </summary>
    public sealed class TowerDefenseHandlerTests
    {
        // ---------- 建造正常路径 ----------

        [Fact]
        public void BuildWithEnoughGold_ConsumesGoldThenPlacesTowerThenPublishesEvent()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithWallet(
                TowerLoaderStub.WithRows(TowerDefenseHarness.DefaultRows()));
            harness.Inventory.SetCount(MetaItemIds.Gold, 500);

            harness.Build(3, 4, 1);

            ConsumeItemCommand consume = Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(MetaItemIds.Gold, consume.ItemId);
            Assert.Equal(TowerDefenseHarness.ArrowTowerCost, consume.Count);
            Assert.Equal(6601, consume.ReasonTagId);

            TowerBuiltEvent built = Assert.Single(harness.Bus.PublishedOf<TowerBuiltEvent>());
            Assert.Equal(3, built.X);
            Assert.Equal(4, built.Y);
            Assert.Equal(1, built.TowerId);
            Assert.Equal(TowerDefenseHarness.ArrowTowerCost, built.Cost);

            // 顺序契约：命令本身 → 先扣钱 → 事件在扣款之后广播（落子夹在两者之间，不发消息）。
            Assert.Equal(2, harness.Bus.SentCommands.Count);
            Assert.IsType<BuildTowerCommand>(harness.Bus.SentCommands[0]);
            Assert.IsType<ConsumeItemCommand>(harness.Bus.SentCommands[1]);
            Assert.Single(harness.Bus.PublishedEvents);

            // 只查了金币这一种货币，且只查了一次。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Equal(MetaItemIds.Gold, harness.Inventory.QueriedItemIds[0]);

            Assert.Equal(1, harness.Handler.BuildCount);
            Assert.Equal(500 - TowerDefenseHarness.ArrowTowerCost, harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void BuildWithExactlyEnoughGold_SucceedsBecauseBalanceIsInclusive()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, TowerDefenseHarness.CannonTowerCost);

            harness.Build(0, 0, 2);

            Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Single(harness.Bus.PublishedOf<TowerBuiltEvent>());
            Assert.Equal(1, harness.Handler.BuildCount);
        }

        [Fact]
        public void BuildTwice_PublishesOneEventPerBuildAndKeepsBothCells()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Build(0, 0, 1);
            harness.Build(7, 7, 2);

            Assert.Equal(2, harness.Bus.SentOf<ConsumeItemCommand>().Count);
            Assert.Equal(2, harness.Bus.PublishedOf<TowerBuiltEvent>().Count);
            Assert.Equal(2, harness.Handler.BuildCount);

            TowerLayoutSnapshot layout = harness.QueryLayout();
            Assert.Equal(1, layout.TowerIdAt(0, 0));
            Assert.Equal(2, layout.TowerIdAt(7, 7));
            Assert.Equal(2, layout.TowerCount);
        }

        // ---------- 静默拒绝 ----------

        [Fact]
        public void BuildWithInsufficientGold_IsSilentlyRejected()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, TowerDefenseHarness.ArrowTowerCost - 1);

            harness.Build(1, 1, 1);

            // 余额不足是"查过之后才发现"：查询发生一次，但没有任何扣款/事件/落子。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.IsType<BuildTowerCommand>(harness.Bus.SentCommands[0]);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<TowerBuiltEvent>());
            Assert.Equal(0, harness.Handler.BuildCount);
            Assert.Equal(0, harness.QueryLayout().TowerCount);
        }

        [Fact]
        public void BuildUnknownTower_IsSilentlyRejectedWithoutTouchingInventory()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);

            harness.Build(2, 2, 4242);

            // 未知塔连金币都不该查。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<TowerBuiltEvent>());
            Assert.Equal(0, harness.Handler.BuildCount);
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(0, -1)]
        [InlineData(TowerDefenseHandler.Width, 0)]
        [InlineData(0, TowerDefenseHandler.Height)]
        [InlineData(int.MaxValue, int.MaxValue)]
        public void BuildOutOfBounds_IsSilentlyRejected(int x, int y)
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);

            harness.Build(x, y, 1);

            // 越界坐标连表与背包都不该碰。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<TowerBuiltEvent>());
            Assert.Equal(0, harness.Handler.BuildCount);
        }

        [Fact]
        public void BuildOnOccupiedCell_IsSilentlyRejectedWithoutSecondCharge()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithWallet(
                TowerLoaderStub.WithRows(TowerDefenseHarness.DefaultRows()));
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Build(5, 5, 1);
            harness.Build(5, 5, 2);

            // 第二次点击同一格：只扣了一次钱，格子仍是第一次建的塔。
            Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Single(harness.Bus.PublishedOf<TowerBuiltEvent>());
            Assert.Equal(1, harness.Handler.BuildCount);
            Assert.Equal(1, harness.QueryLayout().TowerIdAt(5, 5));
            Assert.Equal(1000 - TowerDefenseHarness.ArrowTowerCost, harness.Inventory.GetCount(MetaItemIds.Gold));
        }

        [Fact]
        public void RejectedBuildAfterSuccessfulOne_DoesNotEmitAnyRedDotCommand()
        {
            // 契约里明确写了"不发任何红点"：用反射不到的间接方式断言——发送队列里只允许出现扣款命令。
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Build(0, 1, 1);
            harness.Build(0, 1, 1);
            harness.Build(-3, 0, 1);
            harness.Build(0, 2, 99);

            // 4 次点击里只有 1 次成功：发送队列里只允许出现那一条扣款命令。
            Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<RedDotDeltaCommand>());
            Assert.Empty(harness.Bus.SentOf<RedDotSetCommand>());
            for (int i = 0; i < harness.Bus.SentCommands.Count; i++)
            {
                Assert.IsNotType<RedDotDeltaCommand>(harness.Bus.SentCommands[i]);
                Assert.IsNotType<RedDotSetCommand>(harness.Bus.SentCommands[i]);
            }
        }

        // ---------- 查询快照 ----------

        [Fact]
        public void LayoutQuery_ReturnsRowMajorGridWithSnapshotSize()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Build(2, 3, 1);
            harness.Build(6, 0, 2);

            TowerLayoutSnapshot layout = harness.QueryLayout();

            Assert.Equal(8, layout.Width);
            Assert.Equal(8, layout.Height);
            Assert.Equal(2, layout.TowerCount);

            // 行优先：下标 = y * 8 + x，与 HUD 的 GridLayoutGroup 排布顺序一致。
            for (int y = 0; y < layout.Height; y++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    int expected = 0;
                    if (x == 2 && y == 3)
                    {
                        expected = 1;
                    }
                    else if (x == 6 && y == 0)
                    {
                        expected = 2;
                    }

                    Assert.Equal(expected, layout.TowerIdAt(x, y));
                }
            }
        }

        [Theory]
        [InlineData(-1, 3)]
        [InlineData(3, -1)]
        [InlineData(8, 3)]
        [InlineData(3, 8)]
        [InlineData(int.MinValue, int.MinValue)]
        public void LayoutSnapshot_OutOfRangeCellReturnsZero(int x, int y)
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Build(3, 3, 1);

            TowerLayoutSnapshot layout = harness.QueryLayout();

            // 越界读返回 0（空格语义），不抛异常。
            Assert.Equal(0, layout.TowerIdAt(x, y));
        }

        [Fact]
        public void LayoutSnapshot_CopiesSourceArraySoLaterWritesDoNotLeak()
        {
            int[] cells = new int[8 * 8];
            cells[(4 * 8) + 5] = 7;

            var snapshot = new TowerLayoutSnapshot(8, 8, cells);

            // 构造后改源数组：快照必须还是建它时的内容。
            cells[(5 * 8) + 5] = 9;

            Assert.Equal(7, snapshot.TowerIdAt(5, 4));
            Assert.Equal(0, snapshot.TowerIdAt(5, 5));
            Assert.Equal(1, snapshot.TowerCount);
        }

        [Fact]
        public void LayoutQuery_IsReadOnlyAndDoesNotEmitMessages()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);
            harness.Build(1, 2, 1);

            int sentBefore = harness.Bus.SentCommands.Count;
            int publishedBefore = harness.Bus.PublishedEvents.Count;

            TowerLayoutSnapshot first = harness.QueryLayout();
            TowerLayoutSnapshot second = harness.QueryLayout();

            Assert.Equal(first.TowerIdAt(1, 2), second.TowerIdAt(1, 2));
            Assert.Equal(sentBefore, harness.Bus.SentCommands.Count);
            Assert.Equal(publishedBefore, harness.Bus.PublishedEvents.Count);

            // 两次查询被真实总线记录，但没有多查一次金币、也没有多发任何消息。
            Assert.Equal(2, harness.Bus.CountQueried<TowerLayoutQueryRequest>());
            Assert.Equal(1, harness.Inventory.QueryCount);
        }

        // ---------- 子表加载校验 ----------

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            TowerLoaderStub loader = TowerLoaderStub.Missing();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new TowerTables(loader);
                });
            Assert.Contains(TowerLoaderStub.TableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new TowerDefenseHandler(new RecordingBus(), loader);
                });
            Assert.Contains(TowerLoaderStub.TableName, fromHandler.Message);

            // 两次构造都按名字取同一张子表，没有退化成"随便找一张表"。
            Assert.Equal(2, loader.RequestedTables.Count);
            for (int i = 0; i < loader.RequestedTables.Count; i++)
            {
                Assert.Equal(TowerLoaderStub.TableName, loader.RequestedTables[i]);
            }
        }

        [Fact]
        public void TableLengthMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 1 行，实际写了 2 行：长度 36，期望 4 + 1 * 16 = 20。
            byte[] payload = TowerLoaderStub.BuildPayloadDeclaringRows(
                1,
                new TowerRowFixture(1, 100, 10, 2),
                new TowerRowFixture(2, 250, 24, 3));
            TowerLoaderStub loader = TowerLoaderStub.Missing().AddTable(payload);

            InvalidDataException fromTables = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TowerTables(loader);
                });
            Assert.Contains("长度", fromTables.Message);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TowerDefenseHandler(new RecordingBus(), loader);
                });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(19)]
        public void MalformedPayload_ThrowsInvalidDataException(int payloadLength)
        {
            // 0/3 字节：头部不足 4 字节；19 字节：声明 0 行却多出 15 字节。
            TowerLoaderStub loader = TowerLoaderStub.Missing().AddTable(new byte[payloadLength]);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TowerTables(loader);
                });
        }

        [Fact]
        public void DuplicateTowerId_ThrowsInvalidDataException()
        {
            byte[] payload = TowerLoaderStub.BuildPayload(
                new TowerRowFixture(1, 100, 10, 2),
                new TowerRowFixture(1, 250, 24, 3));
            TowerLoaderStub loader = TowerLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TowerTables(loader);
                });
            Assert.Contains("重复", exception.Message);
        }

        [Theory]
        [InlineData(-1, 10, 2)]
        [InlineData(100, 0, 2)]
        [InlineData(100, -5, 2)]
        [InlineData(100, 10, 0)]
        [InlineData(100, 10, -1)]
        public void IllegalRowValues_ThrowInvalidDataException(int cost, int damage, int range)
        {
            TowerLoaderStub loader = TowerLoaderStub.WithRows(new TowerRowFixture(1, cost, damage, range));

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new TowerTables(loader);
                });

            // 报错必须带上是哪一行，否则一张几十行的表根本没法定位。
            Assert.Contains("towerId=1", exception.Message);
        }

        [Fact]
        public void TablesGetAndGetById_OutOfRangeOrMissingThrow()
        {
            TowerTables tables = new TowerTables(TowerLoaderStub.WithRows(TowerDefenseHarness.DefaultRows()));

            Assert.Equal(2, tables.Count);
            Assert.Equal(1, tables.Get(0).TowerId);
            Assert.Equal(TowerDefenseHarness.ArrowTowerCost, tables.Get(0).Cost);
            Assert.Equal(TowerDefenseHarness.ArrowTowerDamage, tables.Get(0).Damage);
            Assert.Equal(TowerDefenseHarness.ArrowTowerRange, tables.Get(0).Range);
            Assert.Equal(TowerDefenseHarness.CannonTowerRange, tables.GetById(2).Range);

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(2);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(-1);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.GetById(4242);
                });

            // 静默分支只能走 TryFind，不抛异常。
            TowerDef unused;
            Assert.False(tables.TryFind(4242, out unused));
        }

        [Fact]
        public void EmptyTable_IsValidButEveryBuildIsRejected()
        {
            TowerDefenseHarness harness = TowerDefenseHarness.Create(TowerLoaderStub.WithRows());
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);

            Assert.Equal(0, harness.QueryLayout().TowerCount);

            harness.Build(0, 0, 1);

            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<TowerBuiltEvent>());
            Assert.Equal(0, harness.Handler.BuildCount);
        }

        // ---------- 总线注册与构造守卫 ----------

        [Fact]
        public void Register_MakesBothEndpointsUsableThroughRealBus()
        {
            var bus = new RecordingBus();
            TowerInventoryStub inventory = new TowerInventoryStub();
            inventory.SetCount(MetaItemIds.Gold, 1000);
            bus.RegisterCommandHandler<ConsumeItemCommand>(new NoOpCommandHandler<ConsumeItemCommand>());
            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(inventory);

            var handler = new TowerDefenseHandler(bus, TowerLoaderStub.WithRows(TowerDefenseHarness.DefaultRows()));

            // 未注册：真实总线必须抛"未注册处理器"，证明注册是显式步骤而不是构造副作用。
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    handler.Register(null!);
                });
            Assert.Throws<InvalidOperationException>(
                () =>
                {
                    bus.Query<TowerLayoutQueryRequest, TowerLayoutSnapshot>(new TowerLayoutQueryRequest());
                });

            handler.Register(bus);

            bus.Send(new BuildTowerCommand(4, 4, 1));

            TowerLayoutSnapshot layout = bus.Query<TowerLayoutQueryRequest, TowerLayoutSnapshot>(
                new TowerLayoutQueryRequest());
            Assert.Equal(1, layout.TowerIdAt(4, 4));
            Assert.Equal(1, handler.BuildCount);

            // 同一总线重复注册必须抛（真实 CqeBus 的行为，切片不得自带去重掩盖它）。
            Assert.Throws<InvalidOperationException>(
                () =>
                {
                    handler.Register(bus);
                });
        }

        [Fact]
        public void NullBusOrLoader_ThrowsArgumentNullException()
        {
            IBinaryTableLoader loader = TowerLoaderStub.WithRows(TowerDefenseHarness.DefaultRows());

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new TowerDefenseHandler(null!, loader);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new TowerDefenseHandler(new RecordingBus(), null!);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new TowerTables(null!);
                });
        }

        [Fact]
        public void Snapshot_RejectsNullArrayAndNegativeSize()
        {
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new TowerLayoutSnapshot(8, 8, null!);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    new TowerLayoutSnapshot(-1, 8, new int[8]);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    new TowerLayoutSnapshot(8, -1, new int[8]);
                });
        }
    }
}
