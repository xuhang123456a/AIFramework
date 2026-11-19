using System;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Features.Shop;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.Shop
{
    /// <summary>
    /// 商店业务规则验收：真实 <c>CqeBus</c>（经 <see cref="RecordingBus"/> 记录）+ 本切片自带的桩端口，
    /// 断言只落在"切片发出了什么消息"与"目录快照内容"两处。
    /// </summary>
    public sealed class ShopHandlerTests
    {
        // ---------- 购买正常路径 ----------

        [Fact]
        public void BuyWithEnoughGold_ConsumesPriceThenGrantsItemThenPublishesEvent()
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 500);

            harness.Buy(ShopItemIds.HealthPotion, 1);

            ConsumeItemCommand consume = Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(MetaItemIds.Gold, consume.ItemId);
            Assert.Equal(ShopTestHarness.HealthPotionPrice, consume.Count);
            Assert.Equal(6101, consume.ReasonTagId);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(ShopItemIds.HealthPotion, grant.ItemId);
            Assert.Equal(1, grant.Count);
            Assert.Equal(6102, grant.ReasonTagId);

            ShopItemPurchasedEvent purchased = Assert.Single(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());
            Assert.Equal(ShopItemIds.HealthPotion, purchased.ItemId);
            Assert.Equal(1, purchased.Count);
            Assert.Equal(MetaItemIds.Gold, purchased.PriceItemId);
            Assert.Equal(ShopTestHarness.HealthPotionPrice, purchased.PriceCount);

            // 顺序契约：命令本身 → 先扣钱 → 后给货；事件在两条指令之后广播。
            Assert.Equal(3, harness.Bus.SentCommands.Count);
            Assert.IsType<BuyShopItemCommand>(harness.Bus.SentCommands[0]);
            Assert.IsType<ConsumeItemCommand>(harness.Bus.SentCommands[1]);
            Assert.IsType<GrantItemCommand>(harness.Bus.SentCommands[2]);
            Assert.Single(harness.Bus.PublishedEvents);

            // 只查了这一次余额，且查的是该行声明的计价货币。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Equal(MetaItemIds.Gold, harness.Inventory.QueriedItemIds[0]);
        }

        [Fact]
        public void BuyWithExactlyEnoughGold_SucceedsBecauseBalanceIsInclusive()
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, ShopTestHarness.HealthPotionPrice);

            harness.Buy(ShopItemIds.HealthPotion, 1);

            Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Single(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());
        }

        [Fact]
        public void BuyDifferentItem_ChargesThatRowsPriceNotTheFirstRows()
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 1000);

            harness.Buy(ShopItemIds.ReviveToken, 1);

            ConsumeItemCommand consume = Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Equal(ShopTestHarness.ReviveTokenPrice, consume.Count);

            GrantItemCommand grant = Assert.Single(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Equal(ShopItemIds.ReviveToken, grant.ItemId);
        }

        // ---------- 静默拒绝 ----------

        [Fact]
        public void BuyWithInsufficientGold_IsSilentlyRejected()
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, ShopTestHarness.HealthPotionPrice - 1);

            harness.Buy(ShopItemIds.HealthPotion, 1);

            // 余额不足是"查过之后才发现"：查询发生一次，但没有任何指令/事件。
            Assert.Equal(1, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.IsType<BuyShopItemCommand>(harness.Bus.SentCommands[0]);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());
        }

        [Fact]
        public void BuyUnknownItem_IsSilentlyRejectedWithoutTouchingInventory()
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);

            harness.Buy(4242, 1);

            // 未知商品连背包都不该查。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(-1)]
        public void BuyWithCountOtherThanOne_IsSilentlyRejected(int count)
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithDefaultRows();
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);

            harness.Buy(ShopItemIds.HealthPotion, count);

            // Phase 1 只支持单价购买：其余数量一律拒绝，且不查背包。
            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());
        }

        // ---------- 子表加载校验 ----------

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            ShopLoaderStub loader = ShopLoaderStub.Missing();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new ShopTables(loader);
                });
            Assert.Contains(ShopLoaderStub.TableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new ShopHandler(new RecordingBus(), loader);
                });
            Assert.Contains(ShopLoaderStub.TableName, fromHandler.Message);

            // 两次构造都按名字取同一张子表，没有退化成"随便找一张表"。
            Assert.Equal(2, loader.RequestedTables.Count);
            Assert.All(loader.RequestedTables, name => Assert.Equal(ShopLoaderStub.TableName, name));
        }

        [Fact]
        public void TableLengthMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 1 行，实际写了 2 行：长度 28，期望 4 + 1 * 12 = 16。
            byte[] payload = ShopLoaderStub.BuildPayloadDeclaringRows(
                1,
                new ShopRowFixture(ShopItemIds.HealthPotion, MetaItemIds.Gold, ShopTestHarness.HealthPotionPrice),
                new ShopRowFixture(ShopItemIds.ManaPotion, MetaItemIds.Gold, ShopTestHarness.ManaPotionPrice));
            ShopLoaderStub loader = ShopLoaderStub.Missing().AddTable(payload);

            InvalidDataException fromTables = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new ShopTables(loader);
                });
            Assert.Contains("长度", fromTables.Message);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new ShopHandler(new RecordingBus(), loader);
                });
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(5)]
        public void MalformedHeader_ThrowsInvalidDataException(int payloadLength)
        {
            // 0/2 字节：头部不足 4 字节；5 字节：声明 0 行却多出 1 字节。
            ShopLoaderStub loader = ShopLoaderStub.Missing().AddTable(new byte[payloadLength]);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new ShopTables(loader);
                });
        }

        [Fact]
        public void DuplicateItemId_ThrowsInvalidDataException()
        {
            byte[] payload = ShopLoaderStub.BuildPayload(
                new ShopRowFixture(ShopItemIds.HealthPotion, MetaItemIds.Gold, ShopTestHarness.HealthPotionPrice),
                new ShopRowFixture(ShopItemIds.HealthPotion, MetaItemIds.Gold, ShopTestHarness.HealthPotionPrice + 1));
            ShopLoaderStub loader = ShopLoaderStub.Missing().AddTable(payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new ShopTables(loader);
                });
            Assert.Contains("重复", exception.Message);
        }

        [Fact]
        public void TablesGet_OutOfRangeIndex_ThrowsArgumentOutOfRangeException()
        {
            ShopTables tables = new ShopTables(ShopLoaderStub.WithRows(ShopTestHarness.DefaultRows()));

            Assert.Equal(3, tables.Count);
            Assert.Equal(ShopItemIds.HealthPotion, tables.Get(0).ItemId);
            Assert.Equal(MetaItemIds.Gold, tables.Get(0).PriceItemId);
            Assert.Equal(ShopTestHarness.HealthPotionPrice, tables.Get(0).PriceCount);

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

        [Fact]
        public void EmptyTable_IsValidButEveryPurchaseIsRejected()
        {
            ShopTestHarness harness = ShopTestHarness.Create(ShopLoaderStub.WithRows());
            harness.Inventory.SetCount(MetaItemIds.Gold, 9999);

            Assert.Empty(harness.QueryCatalog().Items);

            harness.Buy(ShopItemIds.HealthPotion, 1);

            Assert.Equal(0, harness.Inventory.QueryCount);
            Assert.Single(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());
        }

        // ---------- 目录查询 ----------

        [Fact]
        public void CatalogQuery_ReturnsEveryRowWithItsOwnPrice()
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithDefaultRows();

            ShopCatalogSnapshot snapshot = harness.QueryCatalog();

            Assert.Equal(3, snapshot.Items.Count);
            Assert.Equal(
                new[] { ShopItemIds.HealthPotion, ShopItemIds.ManaPotion, ShopItemIds.ReviveToken },
                ItemIds(snapshot));
            Assert.Equal(
                new[] { MetaItemIds.Gold, MetaItemIds.Gold, MetaItemIds.Gold },
                PriceItemIds(snapshot));
            Assert.Equal(
                new[]
                {
                    ShopTestHarness.HealthPotionPrice,
                    ShopTestHarness.ManaPotionPrice,
                    ShopTestHarness.ReviveTokenPrice,
                },
                PriceCounts(snapshot));

            // 目录查询是只读的：不得顺手发任何指令或事件。
            Assert.Empty(harness.Bus.SentCommands);
            Assert.Empty(harness.Bus.PublishedEvents);
        }

        [Fact]
        public void CatalogQuery_IsStableAcrossCallsAndKeepsPhysicalRowOrder()
        {
            // 行序故意与 itemId 升序相反：若实现"顺手按 itemId 排序"，这里会立刻变红。
            ShopRowFixture[] rows =
            {
                new ShopRowFixture(ShopItemIds.ReviveToken, MetaItemIds.Gold, ShopTestHarness.ReviveTokenPrice),
                new ShopRowFixture(ShopItemIds.HealthPotion, MetaItemIds.Gold, ShopTestHarness.HealthPotionPrice),
            };
            ShopTestHarness harness = ShopTestHarness.Create(ShopLoaderStub.WithRows(rows));

            int[] firstCall = ItemIds(harness.QueryCatalog());
            int[] secondCall = ItemIds(harness.QueryCatalog());

            Assert.Equal(new[] { ShopItemIds.ReviveToken, ShopItemIds.HealthPotion }, firstCall);
            Assert.Equal(firstCall, secondCall);
        }

        // ---------- 构造参数守卫 ----------

        [Fact]
        public void NullBusOrLoader_ThrowsArgumentNullException()
        {
            ShopLoaderStub loader = ShopLoaderStub.WithRows(ShopTestHarness.DefaultRows());

            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new ShopHandler(null!, loader);
                });
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new ShopHandler(new RecordingBus(), null!);
                });

            ShopHandler handler = new ShopHandler(new RecordingBus(), loader);
            Assert.Throws<ArgumentNullException>(
                () =>
                {
                    handler.Register(null!);
                });
        }

        private static int[] ItemIds(ShopCatalogSnapshot snapshot)
        {
            int[] ids = new int[snapshot.Items.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = snapshot.Items[i].ItemId;
            }

            return ids;
        }

        private static int[] PriceItemIds(ShopCatalogSnapshot snapshot)
        {
            int[] ids = new int[snapshot.Items.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = snapshot.Items[i].PriceItemId;
            }

            return ids;
        }

        private static int[] PriceCounts(ShopCatalogSnapshot snapshot)
        {
            int[] prices = new int[snapshot.Items.Count];
            for (int i = 0; i < prices.Length; i++)
            {
                prices[i] = snapshot.Items[i].PriceCount;
            }

            return prices;
        }
    }
}
