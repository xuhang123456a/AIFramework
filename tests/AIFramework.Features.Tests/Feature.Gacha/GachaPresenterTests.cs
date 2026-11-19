using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Features.Gacha;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.Gacha
{
    /// <summary>
    /// 抽卡 Presenter 验收：真实 <c>CqeBus</c>（包在 <see cref="RecordingBus"/> 里，命令/事件全可见）
    /// + 本切片桩端口与预置随机序列 + 记录调用的抽卡视图。
    /// 断言只落在"视图收到了什么""总线发出了什么""余额变成了什么"三处。
    /// </summary>
    public sealed class GachaPresenterTests
    {
        /// <summary>标准夹具里的稀有项（唯一 <c>isRare = 1</c> 的行）。</summary>
        private const int RareItemId = GachaFixture.RareItemId;

        /// <summary>标准夹具里的第一行（权重最大、非稀有）。</summary>
        private const int CommonItemId = GachaFixture.CommonItemId;

        // ---------- 刷新：金币 + 最近结果 ----------

        [Fact]
        public void Refresh_PushesGoldThenPlaceholderResult()
        {
            GachaPresenterFixture fixture = new GachaPresenterFixture(1000);

            fixture.Presenter.Refresh();

            Assert.Equal(1000, fixture.Presenter.Gold);
            AssertCalls(fixture.View, "gold:1000", "lastResult:最近结果：暂无");

            GachaViewData snapshot = fixture.Presenter.LastRendered;
            Assert.Equal(1000, snapshot.Gold);
            Assert.Equal(0, snapshot.LastItemCount);
            Assert.False(snapshot.PityTriggered);
            Assert.Equal("最近结果：暂无", snapshot.LastResultText);
        }

        [Fact]
        public void Refresh_WithZeroGold_StillPushesBothValues()
        {
            GachaPresenterFixture fixture = new GachaPresenterFixture(0);

            fixture.Presenter.Refresh();

            Assert.Equal(0, fixture.Presenter.Gold);
            AssertCalls(fixture.View, "gold:0", "lastResult:最近结果：暂无");
        }

        // ---------- 单抽 ----------

        [Fact]
        public void DrawOnce_WithEnoughGold_SendsSingleDrawThenRefreshes()
        {
            // 权重 70/20/9/1：roll 0 落在第一行（20001，非稀有）。
            GachaPresenterFixture fixture = new GachaPresenterFixture(1000, 0);

            fixture.Presenter.DrawOnce();

            DrawGachaCommand command = Assert.Single(fixture.Bus.SentOf<DrawGachaCommand>());
            Assert.Equal(1, command.Count);

            // 金币减少来自"重新查询账本"，不是 Presenter 本地减法。
            Assert.Equal(1000 - GachaFixture.SingleDrawCost, fixture.Inventory.GetCount(MetaItemIds.Gold));
            Assert.Equal(1000 - GachaFixture.SingleDrawCost, fixture.Presenter.Gold);
            Assert.Single(fixture.Handler.LastResult!.Items);

            AssertCalls(fixture.View, "gold:900", "lastResult:最近结果：" + CommonItemId);
            Assert.Equal(1, fixture.Presenter.LastRendered.LastItemCount);
        }

        [Fact]
        public void DrawOnce_WithRareItem_MarksStarInResultText()
        {
            // roll 99 落在最后一行（20004，唯一稀有项）。
            GachaPresenterFixture fixture = new GachaPresenterFixture(1000, 99);

            fixture.Presenter.DrawOnce();

            AssertCalls(fixture.View, "gold:900", "lastResult:最近结果：" + RareItemId + "★");
            Assert.False(fixture.Presenter.LastRendered.PityTriggered);
        }

        [Fact]
        public void DrawOnce_WithInsufficientGold_KeepsGoldAndStillRefreshes()
        {
            GachaPresenterFixture fixture = new GachaPresenterFixture(GachaFixture.SingleDrawCost - 1);

            fixture.Presenter.DrawOnce();

            // 意愿发出了，但处理器按余额静默拒绝：不扣费、不发货、不发事件、不抛异常。
            Assert.Single(fixture.Bus.SentOf<DrawGachaCommand>());
            Assert.Empty(fixture.Bus.SentOf<ConsumeItemCommand>());
            Assert.Empty(fixture.Bus.SentOf<GrantItemCommand>());
            Assert.Empty(fixture.Bus.PublishedOf<GachaDrawnEvent>());
            Assert.Null(fixture.Handler.LastResult);

            Assert.Equal(GachaFixture.SingleDrawCost - 1, fixture.Presenter.Gold);

            // 抽没抽成都要刷新：UI 必须回到账本里的真实状态。
            AssertCalls(
                fixture.View,
                "gold:" + (GachaFixture.SingleDrawCost - 1),
                "lastResult:最近结果：暂无");
        }

        // ---------- 十连 ----------

        [Fact]
        public void DrawTen_WithEnoughGold_SendsTenDrawAndPushesTenItems()
        {
            // 第 1 抽自然命中稀有（99），后 9 抽都是第一行：全程 10 个随机数，不触发保底。
            GachaPresenterFixture fixture = new GachaPresenterFixture(1000, 99, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            fixture.Presenter.DrawTen();

            DrawGachaCommand command = Assert.Single(fixture.Bus.SentOf<DrawGachaCommand>());
            Assert.Equal(10, command.Count);

            Assert.Equal(1000 - GachaFixture.TenDrawCost, fixture.Presenter.Gold);
            Assert.Equal(10, fixture.Handler.LastResult!.Items.Length);
            Assert.Equal(10, fixture.Bus.SentOf<GrantItemCommand>().Count);
            Assert.Equal(10, fixture.Bus.PublishedOf<GachaDrawnEvent>().Count);

            AssertCalls(
                fixture.View,
                "gold:100",
                "lastResult:最近结果：" + RareItemId + "★"
                    + " 20001 20001 20001 20001 20001 20001 20001 20001 20001");
            Assert.Equal(10, fixture.Presenter.LastRendered.LastItemCount);
            Assert.False(fixture.Presenter.LastRendered.PityTriggered);
        }

        [Fact]
        public void DrawTen_WithInsufficientGold_KeepsGoldAndStillRefreshes()
        {
            GachaPresenterFixture fixture = new GachaPresenterFixture(100);

            fixture.Presenter.DrawTen();

            // 意愿发出了，但处理器按余额拒绝：不扣钱、不取随机、不发事件。
            Assert.Single(fixture.Bus.SentOf<DrawGachaCommand>());
            Assert.Empty(fixture.Bus.SentOf<ConsumeItemCommand>());
            Assert.Null(fixture.Handler.LastResult);

            Assert.Equal(100, fixture.Presenter.Gold);
            AssertCalls(fixture.View, "gold:100", "lastResult:最近结果：暂无");
        }

        [Fact]
        public void DrawAfterSuccess_WithInsufficientGold_KeepsPreviousResultText()
        {
            // 100 金币只够单抽一次：第二次被拒绝时，最近结果必须保持上一次的内容。
            GachaPresenterFixture fixture = new GachaPresenterFixture(GachaFixture.SingleDrawCost, 0);

            fixture.Presenter.DrawOnce();
            fixture.Presenter.DrawOnce();

            Assert.Equal(0, fixture.Presenter.Gold);
            AssertCalls(
                fixture.View,
                "gold:0",
                "lastResult:最近结果：" + CommonItemId,
                "gold:0",
                "lastResult:最近结果：" + CommonItemId);
            Assert.Equal(1, fixture.Presenter.LastRendered.LastItemCount);
        }

        // ---------- 保底文案 ----------

        [Fact]
        public void DrawOnce_PityTriggered_AppendsPitySuffixAndFlagsSnapshot()
        {
            // 前 9 抽都是非稀有（roll 0）；第 10 抽自然结果仍非稀有 → 追加一次稀有池取整（再一个 0）。
            GachaPresenterFixture fixture = new GachaPresenterFixture(1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            for (int i = 0; i < 10; i++)
            {
                fixture.Presenter.DrawOnce();
            }

            Assert.Equal(0, fixture.Presenter.Gold);
            Assert.True(fixture.Presenter.LastRendered.PityTriggered);
            Assert.Equal(10, fixture.Bus.SentOf<DrawGachaCommand>().Count);

            // 前 9 次渲染都是普通结果，最后一次是保底改判的稀有项。
            Assert.Equal("gold:900", fixture.View.Calls[0]);
            Assert.Equal("lastResult:最近结果：" + CommonItemId, fixture.View.Calls[1]);
            Assert.Equal("gold:100", fixture.View.Calls[16]);
            Assert.Equal("lastResult:最近结果：" + CommonItemId, fixture.View.Calls[17]);
            Assert.Equal("gold:0", fixture.View.Calls[18]);
            Assert.Equal("lastResult:最近结果：" + RareItemId + "★（保底）", fixture.View.Calls[19]);
            Assert.Equal(20, fixture.View.Calls.Count);
        }

        // ---------- 视图点击接线 ----------

        [Fact]
        public void ViewClick_DrawOnce_RoutesThroughPresenter()
        {
            GachaPresenterFixture fixture = new GachaPresenterFixture(1000, 0);

            fixture.View.RaiseDrawOnceClicked();

            DrawGachaCommand command = Assert.Single(fixture.Bus.SentOf<DrawGachaCommand>());
            Assert.Equal(1, command.Count);
            Assert.Equal(900, fixture.Presenter.Gold);
            Assert.Equal(2, fixture.View.Calls.Count);
        }

        [Fact]
        public void ViewClick_DrawTen_RoutesThroughPresenter()
        {
            GachaPresenterFixture fixture = new GachaPresenterFixture(1000, 99, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            fixture.View.RaiseDrawTenClicked();

            DrawGachaCommand command = Assert.Single(fixture.Bus.SentOf<DrawGachaCommand>());
            Assert.Equal(10, command.Count);
            Assert.Equal(100, fixture.Presenter.Gold);
            Assert.Equal(10, fixture.Presenter.LastRendered.LastItemCount);
        }

        // ---------- 构造参数守卫 ----------

        [Fact]
        public void Constructor_WithNullArguments_ThrowsArgumentNullException()
        {
            GachaPresenterFixture fixture = new GachaPresenterFixture(0);

            ArgumentNullException nullBus = Assert.Throws<ArgumentNullException>(
                () => new GachaPresenter(null!, fixture.Handler, fixture.View));
            Assert.Equal("bus", nullBus.ParamName);

            ArgumentNullException nullHandler = Assert.Throws<ArgumentNullException>(
                () => new GachaPresenter(fixture.Bus, null!, fixture.View));
            Assert.Equal("handler", nullHandler.ParamName);

            ArgumentNullException nullView = Assert.Throws<ArgumentNullException>(
                () => new GachaPresenter(fixture.Bus, fixture.Handler, null!));
            Assert.Equal("view", nullView.ParamName);
        }

        // ---------- 断言辅助 ----------

        private static void AssertCalls(RecordingGachaView view, params string[] expected)
        {
            Assert.Equal(expected.Length, view.Calls.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], view.Calls[i]);
            }
        }
    }

    /// <summary>
    /// 一次性的 Presenter 装配：真实 <see cref="GachaHandler"/> + 记录型总线 + 预置随机序列。
    /// <para>
    /// <b>为什么不用共享的 <c>GachaHarness</c></b>：它把真实 <c>CqeBus</c> 直接交给切片，
    /// 记录不到 <see cref="DrawGachaCommand"/>（没有命令拦截点）。Presenter 验收必须能断言
    /// "单抽发的是 1、十连发的是 10"，所以这里用 <see cref="RecordingBus"/> 重铺同一条装配链，
    /// 钱包/背包/事件桩仍复用切片自带的测试替身，不另造业务替身。
    /// </para>
    /// </summary>
    internal sealed class GachaPresenterFixture
    {
        internal GachaPresenterFixture(int gold, params int[] randomValues)
        {
            Bus = new RecordingBus();
            Inventory = new GachaInventoryStub();
            Inventory.SetCount(MetaItemIds.Gold, gold);

            GachaMessageRecorder messages = new GachaMessageRecorder();
            GachaWalletStub wallet = new GachaWalletStub(Inventory, messages);
            Bus.RegisterCommandHandler<ConsumeItemCommand>(wallet);
            Bus.RegisterCommandHandler<GrantItemCommand>(wallet);
            Bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(Inventory);
            Bus.Subscribe<GachaDrawnEvent>(new GachaEventSpy(messages));

            Handler = new GachaHandler(Bus, new SequenceRandomSource(randomValues), GachaFixture.DefaultLoader());
            Handler.Register(Bus);

            View = new RecordingGachaView();
            Presenter = new GachaPresenter(Bus, Handler, View);
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        internal RecordingBus Bus { get; }

        /// <summary>背包/钱包桩（金币余额也在这里）。</summary>
        internal GachaInventoryStub Inventory { get; }

        /// <summary>被测抽卡处理器。</summary>
        internal GachaHandler Handler { get; }

        /// <summary>记录渲染轨迹的视图。</summary>
        internal RecordingGachaView View { get; }

        /// <summary>被测 Presenter。</summary>
        internal GachaPresenter Presenter { get; }
    }

    /// <summary>记录渲染顺序与内容的抽卡视图假实现，并提供"人手点击"的触发入口。</summary>
    internal sealed class RecordingGachaView : IGachaView
    {
        private readonly List<string> _calls;

        internal RecordingGachaView()
        {
            _calls = new List<string>();
        }

        /// <inheritdoc />
        public event Action? OnDrawOnceClicked;

        /// <inheritdoc />
        public event Action? OnDrawTenClicked;

        /// <summary>按调用顺序记录的渲染轨迹（<c>gold:n</c> / <c>lastResult:文本</c>）。</summary>
        internal IReadOnlyList<string> Calls
        {
            get { return _calls; }
        }

        /// <summary>最近一次渲染的金币。</summary>
        internal int LastGold { get; private set; }

        /// <summary>最近一次渲染的结果文本。</summary>
        internal string LastResultText { get; private set; } = string.Empty;

        /// <inheritdoc />
        public void RenderGold(int value)
        {
            LastGold = value;
            _calls.Add("gold:" + value);
        }

        /// <inheritdoc />
        public void RenderLastResult(string text)
        {
            LastResultText = text;
            _calls.Add("lastResult:" + text);
        }

        /// <summary>模拟点击单抽按钮。</summary>
        internal void RaiseDrawOnceClicked()
        {
            OnDrawOnceClicked?.Invoke();
        }

        /// <summary>模拟点击十连按钮。</summary>
        internal void RaiseDrawTenClicked()
        {
            OnDrawTenClicked?.Invoke();
        }
    }
}
