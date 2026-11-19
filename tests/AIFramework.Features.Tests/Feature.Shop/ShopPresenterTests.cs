using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Features.Shop;
using Xunit;

namespace AIFramework.Features.Tests.Shop
{
    /// <summary>
    /// Presenter 验收：真实 <c>CqeBus</c>（经 <see cref="ShopTestHarness"/> 的记录型总线）
    /// + 本切片桩端口 + 一个记录调用的商店视图，断言只落在"视图收到了什么"与"账本变成了什么"两处。
    /// </summary>
    public sealed class ShopPresenterTests
    {
        // ---------- 刷新：金币 + 商品列表 ----------

        [Fact]
        public void Refresh_PushesGoldThenItemsWithAffordableFlags()
        {
            ShopTestHarness harness = CreateHarness(1000);
            RecordingShopView view = new RecordingShopView();
            ShopPresenter presenter = new ShopPresenter(harness.Bus, view);

            presenter.Refresh();

            Assert.Equal(1000, presenter.Gold);
            Assert.Equal(1, view.RenderGoldCount);
            Assert.Equal(1, view.RenderItemsCount);

            // 顺序契约：先金币后列表，且在商品列表之前就把余额推给视图。
            Assert.Equal(2, view.Calls.Count);
            Assert.Equal("gold:1000", view.Calls[0]);
            Assert.Equal("items:3", view.Calls[1]);

            ShopItemViewData[] items = view.LastItems;
            Assert.Equal(3, items.Length);
            Assert.Equal(
                new[] { ShopItemIds.HealthPotion, ShopItemIds.ManaPotion, ShopItemIds.ReviveToken },
                ItemIds(items));
            Assert.Equal(
                new[] { MetaItemIds.Gold, MetaItemIds.Gold, MetaItemIds.Gold },
                PriceItemIds(items));
            Assert.Equal(
                new[]
                {
                    ShopTestHarness.HealthPotionPrice,
                    ShopTestHarness.ManaPotionPrice,
                    ShopTestHarness.ReviveTokenPrice,
                },
                Prices(items));
            Assert.Equal(new[] { true, true, true }, Affordables(items));
        }

        [Fact]
        public void Refresh_AffordableFollowsGoldBoundary()
        {
            // 差 1：99 金币买不起 100 金币的生命药水。
            ShopTestHarness harness = CreateHarness(ShopTestHarness.HealthPotionPrice - 1);
            RecordingShopView view = new RecordingShopView();
            ShopPresenter presenter = new ShopPresenter(harness.Bus, view);

            presenter.Refresh();

            Assert.Equal(ShopTestHarness.HealthPotionPrice - 1, presenter.Gold);
            Assert.Equal(new[] { false, false, false }, Affordables(view.LastItems));

            // 恰好够：余额等于单价即可买（与 ShopHandler 的含边界判定一致）。
            harness.Inventory.SetCount(MetaItemIds.Gold, ShopTestHarness.HealthPotionPrice);
            presenter.Refresh();

            Assert.Equal(ShopTestHarness.HealthPotionPrice, presenter.Gold);
            Assert.Equal(new[] { true, false, false }, Affordables(view.LastItems));
        }

        [Fact]
        public void Refresh_WithEmptyCatalog_StillRendersGold()
        {
            // rowCount = 0 是合法配置（空商店）：金币仍要渲染，不能因为没商品就不查余额。
            ShopTestHarness harness = ShopTestHarness.CreateWithWallet(ShopLoaderStub.WithRows());
            harness.Inventory.SetCount(MetaItemIds.Gold, 7);
            RecordingShopView view = new RecordingShopView();
            ShopPresenter presenter = new ShopPresenter(harness.Bus, view);

            presenter.Refresh();

            Assert.Equal(7, presenter.Gold);
            Assert.Equal(1, view.RenderGoldCount);
            Assert.Equal(1, view.RenderItemsCount);
            Assert.Equal("gold:7", view.Calls[0]);
            Assert.Equal("items:0", view.Calls[1]);
            Assert.Empty(view.LastItems);
        }

        // ---------- 购买：成功路径 ----------

        [Fact]
        public void Buy_WithEnoughGold_DeductsGoldAndRepushesBoth()
        {
            ShopTestHarness harness = CreateHarness(500);
            RecordingShopView view = new RecordingShopView();
            ShopPresenter presenter = new ShopPresenter(harness.Bus, view);
            presenter.Refresh();

            presenter.Buy(ShopItemIds.HealthPotion);

            // 意图真的发给了处理器：单价购买 + 扣款 + 发货 + 购买事实。
            BuyShopItemCommand buy = Assert.Single(harness.Bus.SentOf<BuyShopItemCommand>());
            Assert.Equal(ShopItemIds.HealthPotion, buy.ItemId);
            Assert.Equal(1, buy.Count);
            Assert.Single(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Single(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());

            // 金币减少来自"重新查询账本"，不是 Presenter 本地减法。
            Assert.Equal(400, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(400, presenter.Gold);

            Assert.Equal(2, view.RenderGoldCount);
            Assert.Equal(2, view.RenderItemsCount);
            Assert.Equal(4, view.Calls.Count);
            Assert.Equal("gold:400", view.Calls[2]);
            Assert.Equal("items:3", view.Calls[3]);

            // 500 -> 400：生命药水/魔力药水买得起，复活币（500）买不起了。
            Assert.Equal(new[] { true, true, false }, Affordables(view.LastItems));
        }

        // ---------- 购买：拒绝路径 ----------

        [Fact]
        public void Buy_WithInsufficientGold_KeepsGoldAndStillRefreshes()
        {
            ShopTestHarness harness = CreateHarness(ShopTestHarness.HealthPotionPrice - 1);
            RecordingShopView view = new RecordingShopView();
            ShopPresenter presenter = new ShopPresenter(harness.Bus, view);
            presenter.Refresh();

            presenter.Buy(ShopItemIds.HealthPotion);

            // 处理器静默拒绝：没有任何扣款/发货/事件，也不抛异常。
            Assert.Empty(harness.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(harness.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(harness.Bus.PublishedOf<ShopItemPurchasedEvent>());

            Assert.Equal(ShopTestHarness.HealthPotionPrice - 1, harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(ShopTestHarness.HealthPotionPrice - 1, presenter.Gold);

            // 买没买成都要刷新：UI 必须回到账本里的真实状态。
            Assert.Equal(2, view.RenderGoldCount);
            Assert.Equal(2, view.RenderItemsCount);
            Assert.Equal(new[] { false, false, false }, Affordables(view.LastItems));
        }

        [Fact]
        public void Buy_WithViewThatNeverWiresClickEvent_DoesNotThrow()
        {
            ShopTestHarness harness = CreateHarness(500);
            UnwiredShopView view = new UnwiredShopView();
            ShopPresenter presenter = new ShopPresenter(harness.Bus, view);
            presenter.Refresh();

            // 视图的事件订阅是空实现（永远是 null）：不点按钮、直接调用也必须能跑通。
            Exception? thrown = Record.Exception(
                () =>
                {
                    presenter.Buy(ShopItemIds.HealthPotion);
                });

            Assert.Null(thrown);
            Assert.Equal(400, presenter.Gold);
            Assert.Equal(4, view.RenderCount);
        }

        // ---------- 视图点击接线 ----------

        [Fact]
        public void ViewClick_BuysThroughPresenterAndRefreshes()
        {
            ShopTestHarness harness = CreateHarness(500);
            RecordingShopView view = new RecordingShopView();
            ShopPresenter presenter = new ShopPresenter(harness.Bus, view);
            presenter.Refresh();

            view.RaiseBuyClicked(ShopItemIds.HealthPotion);

            Assert.Single(harness.Bus.SentOf<BuyShopItemCommand>());
            Assert.Equal(400, presenter.Gold);
            Assert.Equal(2, view.RenderGoldCount);
            Assert.Equal(2, view.RenderItemsCount);
        }

        // ---------- 构造参数守卫 ----------

        [Fact]
        public void Constructor_WithNullArguments_ThrowsArgumentNullException()
        {
            ShopTestHarness harness = CreateHarness(0);
            RecordingShopView view = new RecordingShopView();

            ArgumentNullException nullView = Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new ShopPresenter(harness.Bus, null!);
                });
            Assert.Equal("view", nullView.ParamName);

            ArgumentNullException nullBus = Assert.Throws<ArgumentNullException>(
                () =>
                {
                    new ShopPresenter(null!, view);
                });
            Assert.Equal("bus", nullBus.ParamName);
        }

        private static ShopTestHarness CreateHarness(int gold)
        {
            ShopTestHarness harness = ShopTestHarness.CreateWithWallet(
                ShopLoaderStub.WithRows(ShopTestHarness.DefaultRows()));
            harness.Inventory.SetCount(MetaItemIds.Gold, gold);
            return harness;
        }

        private static int[] ItemIds(ShopItemViewData[] items)
        {
            int[] ids = new int[items.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = items[i].ItemId;
            }

            return ids;
        }

        private static int[] PriceItemIds(ShopItemViewData[] items)
        {
            int[] ids = new int[items.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = items[i].PriceItemId;
            }

            return ids;
        }

        private static int[] Prices(ShopItemViewData[] items)
        {
            int[] prices = new int[items.Length];
            for (int i = 0; i < prices.Length; i++)
            {
                prices[i] = items[i].PriceCount;
            }

            return prices;
        }

        private static bool[] Affordables(ShopItemViewData[] items)
        {
            bool[] flags = new bool[items.Length];
            for (int i = 0; i < flags.Length; i++)
            {
                flags[i] = items[i].Affordable;
            }

            return flags;
        }
    }

    /// <summary>记录渲染顺序与内容的商店视图假实现，并提供"人手点击"的触发入口。</summary>
    internal sealed class RecordingShopView : IShopView
    {
        private readonly List<string> _calls;
        private ShopItemViewData[] _lastItems;

        public RecordingShopView()
        {
            _calls = new List<string>();
            _lastItems = Array.Empty<ShopItemViewData>();
        }

        /// <inheritdoc />
        public event Action<int>? OnBuyClicked;

        /// <summary>按调用顺序记录的渲染轨迹（<c>gold:数量</c> / <c>items:条数</c>）。</summary>
        public IReadOnlyList<string> Calls
        {
            get { return _calls; }
        }

        /// <summary><see cref="RenderGold"/> 的调用次数。</summary>
        public int RenderGoldCount { get; private set; }

        /// <summary><see cref="RenderItems"/> 的调用次数。</summary>
        public int RenderItemsCount { get; private set; }

        /// <summary>最近一次渲染的金币。</summary>
        public int LastGold { get; private set; }

        /// <summary>最近一次渲染的商品快照（拷贝副本，视图外不可回写）。</summary>
        public ShopItemViewData[] LastItems
        {
            get { return _lastItems; }
        }

        /// <inheritdoc />
        public void RenderGold(int gold)
        {
            RenderGoldCount++;
            LastGold = gold;
            _calls.Add("gold:" + gold);
        }

        /// <inheritdoc />
        public void RenderItems(IReadOnlyList<ShopItemViewData> items)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            ShopItemViewData[] copy = new ShopItemViewData[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                copy[i] = items[i];
            }

            RenderItemsCount++;
            _lastItems = copy;
            _calls.Add("items:" + copy.Length);
        }

        /// <summary>模拟点击某件商品的购买按钮。</summary>
        public void RaiseBuyClicked(int itemId)
        {
            OnBuyClicked?.Invoke(itemId);
        }
    }

    /// <summary>彻底没接线的商店视图：事件的加/退订都是空实现，订阅者永远是 null。</summary>
    internal sealed class UnwiredShopView : IShopView
    {
        /// <inheritdoc />
        public event Action<int>? OnBuyClicked
        {
            add { }
            remove { }
        }

        /// <summary>渲染总次数（金币与列表各记一次）。</summary>
        public int RenderCount { get; private set; }

        /// <inheritdoc />
        public void RenderGold(int gold)
        {
            RenderCount++;
        }

        /// <inheritdoc />
        public void RenderItems(IReadOnlyList<ShopItemViewData> items)
        {
            RenderCount++;
        }
    }
}
