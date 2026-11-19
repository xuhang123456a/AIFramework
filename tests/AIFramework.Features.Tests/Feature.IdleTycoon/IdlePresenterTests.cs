using System;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.IdleTycoon;
using Xunit;

namespace AIFramework.Features.Tests.IdleTycoon
{
    /// <summary>
    /// 放置 Presenter 验收：真实 <c>CqeBus</c>（经 <see cref="IdleTycoonHarness"/> 的记录型总线）
    /// + 本切片桩端口 + 一个记录调用的放置视图，断言只落在"视图收到了什么"与"账本/等级变成了什么"两处。
    /// </summary>
    public sealed class IdlePresenterTests
    {
        // ---------- 刷新：金币 + 收入 + 建筑列表 ----------

        [Fact]
        public void Refresh_PushesGoldThenIncomeThenBuildings()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(1000);

            fixture.Presenter.Refresh();

            Assert.Equal(1000, fixture.Presenter.Gold);

            // 顺序契约：金币 → 收入 → 列表，一次刷新只查一次余额。
            AssertCalls(
                fixture.View,
                "gold:1000",
                "income:收入 0/秒",
                "buildings:3");

            BuildingViewData[] buildings = fixture.View.LastBuildings;
            Assert.Equal(3, buildings.Length);
            Assert.Equal(new[] { IdleTycoonHarness.FactoryId, IdleTycoonHarness.FarmId, IdleTycoonHarness.MineId }, BuildingIds(buildings));
            Assert.Equal(new[] { 0, 0, 0 }, Levels(buildings));
            Assert.Equal(new[] { 3, 2, 1 }, MaxLevels(buildings));
            Assert.Equal(
                new[]
                {
                    IdleTycoonHarness.FactoryUpgradeCost,
                    IdleTycoonHarness.FarmUpgradeCost,
                    40,
                },
                UpgradeCosts(buildings));

            // 等级全 0：每栋的每秒产出都是 0，全场收入也是 0。
            Assert.Equal(new[] { 0, 0, 0 }, Incomes(buildings));
            Assert.Equal(new[] { true, true, true }, Affordables(buildings));
        }

        [Fact]
        public void Refresh_AffordableFollowsUpgradeCostBoundary()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(99);

            fixture.Presenter.Refresh();

            // 差 1：99 金币升不起 100 金币的工厂，但仍升得起 40 金币的矿场。
            Assert.Equal(99, fixture.Presenter.Gold);
            Assert.Equal(new[] { false, false, true }, Affordables(fixture.View.LastBuildings));

            // 恰好够：余额等于升级费用即可升（与 IdleTycoonHandler 的含边界判定一致）。
            fixture.Harness.Inventory.SetCount(MetaItemIds.Gold, 100);
            fixture.Presenter.Refresh();

            Assert.Equal(100, fixture.Presenter.Gold);
            Assert.Equal(new[] { true, false, true }, Affordables(fixture.View.LastBuildings));
        }

        [Fact]
        public void Refresh_AtLevelCapIsNotAffordableEvenWithEnoughGold()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(1000);
            fixture.Presenter.Refresh();

            // 矿场上限 1 级：升满后即便金币充足也不能再升（处理器会静默拒绝）。
            fixture.Presenter.Upgrade(IdleTycoonHarness.MineId);

            BuildingViewData mine = fixture.View.LastBuildings[2];
            Assert.Equal(1, mine.Level);
            Assert.Equal(1, mine.MaxLevel);
            Assert.Equal(1, mine.IncomePerSecond);
            Assert.False(mine.Affordable);

            // 其余建筑照常可升，收入是"全场合计"而不是单栋产出。
            Assert.Equal(new[] { true, true, false }, Affordables(fixture.View.LastBuildings));
            Assert.Equal("income:收入 1/秒", fixture.View.Calls[fixture.View.Calls.Count - 2]);
        }

        [Fact]
        public void Refresh_WithEmptyBuildingTable_StillRendersGoldAndIncome()
        {
            // rowCount = 0 是合法配置（空建筑表）：余额与收入照样要渲染，不能因为没建筑就连金币都不查。
            IdlePresenterFixture fixture = new IdlePresenterFixture(7, IdleLoaderStub.WithRows());

            fixture.Presenter.Refresh();

            Assert.Equal(7, fixture.Presenter.Gold);
            AssertCalls(fixture.View, "gold:7", "income:收入 0/秒", "buildings:0");
            Assert.Empty(fixture.View.LastBuildings);
        }

        [Fact]
        public void Refresh_TimestampsFromInjectedTimeProvider()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(0);

            // 构造期先对齐一次基线；之后每次刷新都取注入时间源的当前秒数。
            Assert.Equal(0d, fixture.Presenter.LastRefreshElapsedSeconds);

            fixture.Harness.Time.AdvanceSeconds(30d);
            fixture.Presenter.Refresh();

            Assert.Equal(30d, fixture.Presenter.LastRefreshElapsedSeconds);
        }

        // ---------- 升级：成功路径 ----------

        [Fact]
        public void Upgrade_WithEnoughGold_SendsCommandThenRefreshes()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(1000);
            fixture.Presenter.Refresh();

            fixture.Presenter.Upgrade(IdleTycoonHarness.FactoryId);

            // 意图真的发给了处理器。
            UpgradeBuildingCommand command = Assert.Single(fixture.Harness.Bus.SentOf<UpgradeBuildingCommand>());
            Assert.Equal(IdleTycoonHarness.FactoryId, command.BuildingId);

            // 金币减少与等级上涨都来自"重新查询/重新读内核"，不是 Presenter 本地加减。
            Assert.Equal(1000 - IdleTycoonHarness.FactoryUpgradeCost, fixture.Harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(1000 - IdleTycoonHarness.FactoryUpgradeCost, fixture.Presenter.Gold);
            Assert.Equal(1, fixture.Harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));

            AssertCalls(
                fixture.View,
                "gold:1000",
                "income:收入 0/秒",
                "buildings:3",
                "gold:900",
                "income:收入 5/秒",
                "buildings:3");

            BuildingViewData factory = fixture.View.LastBuildings[0];
            Assert.Equal(1, factory.Level);
            Assert.Equal(IdleTycoonHarness.FactoryBaseIncome, factory.IncomePerSecond);
        }

        // ---------- 升级：拒绝路径 ----------

        [Fact]
        public void Upgrade_WithInsufficientGold_KeepsStateAndStillRefreshes()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(0);
            fixture.Presenter.Refresh();

            fixture.Presenter.Upgrade(IdleTycoonHarness.FactoryId);

            // 处理器静默拒绝：等级不变、金币不变、也不抛异常。
            Assert.Equal(0, fixture.Harness.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(0, fixture.Harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(0, fixture.Presenter.Gold);

            // 升没升成都要刷新：UI 必须回到内核里的真实状态。
            Assert.Equal(6, fixture.View.Calls.Count);
            AssertCalls(
                fixture.View,
                "gold:0",
                "income:收入 0/秒",
                "buildings:3",
                "gold:0",
                "income:收入 0/秒",
                "buildings:3");
            Assert.Equal(new[] { false, false, false }, Affordables(fixture.View.LastBuildings));
        }

        [Fact]
        public void Upgrade_WithUnknownBuilding_DoesNotThrowAndStillRefreshes()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(1000);
            fixture.Presenter.Refresh();

            // 未知建筑只由处理器静默拒绝；Presenter 不得因为 GetLevel 抛异常而炸掉面板。
            Exception? thrown = Record.Exception(() => fixture.Presenter.Upgrade(4242));

            Assert.Null(thrown);
            Assert.Equal(1000, fixture.Presenter.Gold);
            Assert.Equal(0, fixture.Harness.Handler.GetLevel(IdleTycoonHarness.FactoryId));
            Assert.Equal(6, fixture.View.Calls.Count);
            Assert.Equal("gold:1000", fixture.View.Calls[3]);
        }

        // ---------- 视图点击接线 ----------

        [Fact]
        public void ViewClick_Upgrade_RoutesThroughPresenterAndRefreshes()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(500);
            fixture.Presenter.Refresh();

            fixture.View.RaiseUpgradeClicked(IdleTycoonHarness.FarmId);

            UpgradeBuildingCommand command = Assert.Single(fixture.Harness.Bus.SentOf<UpgradeBuildingCommand>());
            Assert.Equal(IdleTycoonHarness.FarmId, command.BuildingId);
            Assert.Equal(1, fixture.Harness.Handler.GetLevel(IdleTycoonHarness.FarmId));
            Assert.Equal(500 - IdleTycoonHarness.FarmUpgradeCost, fixture.Presenter.Gold);
            Assert.Equal(6, fixture.View.Calls.Count);
            Assert.Equal("income:收入 12/秒", fixture.View.Calls[4]);
        }

        // ---------- 构造参数守卫 ----------

        [Fact]
        public void Constructor_WithNullArguments_ThrowsArgumentNullException()
        {
            IdlePresenterFixture fixture = new IdlePresenterFixture(0);
            IBinaryTableLoader tables = IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows());

            ArgumentNullException nullBus = Assert.Throws<ArgumentNullException>(
                () => new IdlePresenter(null!, fixture.Harness.Time, fixture.Harness.Handler, fixture.View, tables));
            Assert.Equal("bus", nullBus.ParamName);

            ArgumentNullException nullTime = Assert.Throws<ArgumentNullException>(
                () => new IdlePresenter(fixture.Harness.Bus, null!, fixture.Harness.Handler, fixture.View, tables));
            Assert.Equal("time", nullTime.ParamName);

            ArgumentNullException nullHandler = Assert.Throws<ArgumentNullException>(
                () => new IdlePresenter(fixture.Harness.Bus, fixture.Harness.Time, null!, fixture.View, tables));
            Assert.Equal("handler", nullHandler.ParamName);

            ArgumentNullException nullView = Assert.Throws<ArgumentNullException>(
                () => new IdlePresenter(fixture.Harness.Bus, fixture.Harness.Time, fixture.Harness.Handler, null!, tables));
            Assert.Equal("view", nullView.ParamName);

            ArgumentNullException nullTables = Assert.Throws<ArgumentNullException>(
                () => new IdlePresenter(fixture.Harness.Bus, fixture.Harness.Time, fixture.Harness.Handler, fixture.View, null!));
            Assert.Equal("tables", nullTables.ParamName);
        }

        // ---------- 断言辅助 ----------

        private static void AssertCalls(RecordingIdleView view, params string[] expected)
        {
            Assert.Equal(expected.Length, view.Calls.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], view.Calls[i]);
            }
        }

        private static int[] BuildingIds(BuildingViewData[] buildings)
        {
            int[] values = new int[buildings.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = buildings[i].BuildingId;
            }

            return values;
        }

        private static int[] Levels(BuildingViewData[] buildings)
        {
            int[] values = new int[buildings.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = buildings[i].Level;
            }

            return values;
        }

        private static int[] MaxLevels(BuildingViewData[] buildings)
        {
            int[] values = new int[buildings.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = buildings[i].MaxLevel;
            }

            return values;
        }

        private static int[] UpgradeCosts(BuildingViewData[] buildings)
        {
            int[] values = new int[buildings.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = buildings[i].UpgradeCost;
            }

            return values;
        }

        private static int[] Incomes(BuildingViewData[] buildings)
        {
            int[] values = new int[buildings.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = buildings[i].IncomePerSecond;
            }

            return values;
        }

        private static bool[] Affordables(BuildingViewData[] buildings)
        {
            bool[] values = new bool[buildings.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = buildings[i].Affordable;
            }

            return values;
        }
    }

    /// <summary>
    /// 一次性的 Presenter 装配：真实 <see cref="IdleTycoonHandler"/> + 真实总线（经记录）+ 可控时间源，
    /// 并让处理器与 Presenter 从<b>同一个</b>加载器读同一张 <c>TbBuilding</c>。
    /// </summary>
    internal sealed class IdlePresenterFixture
    {
        internal IdlePresenterFixture(int gold)
            : this(gold, IdleLoaderStub.WithRows(IdleTycoonHarness.DefaultRows()))
        {
        }

        internal IdlePresenterFixture(int gold, IdleLoaderStub loader)
        {
            Loader = loader;
            Harness = IdleTycoonHarness.Create(loader);
            Harness.Inventory.SetCount(MetaItemIds.Gold, gold);
            View = new RecordingIdleView();
            Presenter = new IdlePresenter(Harness.Bus, Harness.Time, Harness.Handler, View, loader);
        }

        /// <summary>子表加载桩（处理器与 Presenter 共用）。</summary>
        internal IdleLoaderStub Loader { get; }

        /// <summary>已注册的手处理器与桩端口。</summary>
        internal IdleTycoonHarness Harness { get; }

        /// <summary>记录渲染轨迹的视图。</summary>
        internal RecordingIdleView View { get; }

        /// <summary>被测 Presenter。</summary>
        internal IdlePresenter Presenter { get; }
    }

    /// <summary>记录渲染顺序与内容的放置视图假实现，并提供"人手点击升级"的触发入口。</summary>
    internal sealed class RecordingIdleView : IIdleView
    {
        private readonly List<string> _calls;
        private BuildingViewData[] _lastBuildings;

        internal RecordingIdleView()
        {
            _calls = new List<string>();
            _lastBuildings = Array.Empty<BuildingViewData>();
        }

        /// <inheritdoc />
        public event Action<int>? OnUpgradeClicked;

        /// <summary>按调用顺序记录的渲染轨迹（<c>gold:n</c> / <c>income:文本</c> / <c>buildings:n</c>）。</summary>
        internal IReadOnlyList<string> Calls
        {
            get { return _calls; }
        }

        /// <summary>最近一次渲染的金币。</summary>
        internal int LastGold { get; private set; }

        /// <summary>最近一次渲染的收入文本。</summary>
        internal string LastIncomeText { get; private set; } = string.Empty;

        /// <summary>最近一次渲染的建筑快照（拷贝副本，视图外不可回写）。</summary>
        internal BuildingViewData[] LastBuildings
        {
            get { return _lastBuildings; }
        }

        /// <inheritdoc />
        public void RenderGold(int value)
        {
            LastGold = value;
            _calls.Add("gold:" + value);
        }

        /// <inheritdoc />
        public void RenderIncome(string text)
        {
            LastIncomeText = text;
            _calls.Add("income:" + text);
        }

        /// <inheritdoc />
        public void RenderBuildings(IReadOnlyList<BuildingViewData> buildings)
        {
            if (buildings == null)
            {
                throw new ArgumentNullException(nameof(buildings));
            }

            BuildingViewData[] copy = new BuildingViewData[buildings.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = buildings[i];
            }

            _lastBuildings = copy;
            _calls.Add("buildings:" + copy.Length);
        }

        /// <summary>模拟点击某栋建筑的升级按钮。</summary>
        internal void RaiseUpgradeClicked(int buildingId)
        {
            OnUpgradeClicked?.Invoke(buildingId);
        }
    }
}
