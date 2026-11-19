#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;

namespace AIFramework.Features.Gacha
{
    /// <summary>
    /// 抽卡面板 UI 大脑：把"金币余额 + 最近一次结果"翻译成视图调用，
    /// 并把视图点击转发成 <see cref="DrawGachaCommand"/>。
    /// <para>
    /// 这里不算价、不扣钱、不掷骰子：单抽/十连的费用、余额校验、权重与保底全部由
    /// <see cref="GachaHandler"/> 裁决（被拒绝时它静默返回，这里不做二次判断）。
    /// 唯一持有的业务字段是 <see cref="_gold"/> 这份"最近一次查询到的金币"镜像，绝不在本地加减。
    /// </para>
    /// </summary>
    public sealed class GachaPresenter
    {
        /// <summary>单抽次数（与 <see cref="DrawGachaCommand"/> 的受理值一致）。</summary>
        private const int SingleDrawCount = 1;

        /// <summary>十连次数（与 <see cref="DrawGachaCommand"/> 的受理值一致）。</summary>
        private const int TenDrawCount = 10;

        /// <summary>结果文本前缀。</summary>
        private const string ResultPrefix = "最近结果：";

        /// <summary>从未抽过（或最近一次被拒绝且此前也没抽过）时的占位文案。</summary>
        private const string NoResultText = ResultPrefix + "暂无";

        /// <summary>保底触发后缀。</summary>
        private const string PitySuffix = "（保底）";

        /// <summary>稀有项标记（跟在道具 ID 后面）。</summary>
        private const char RareMark = '★';

        private readonly ICqeBus _bus;
        private readonly GachaHandler _handler;
        private readonly IGachaView _view;
        private int _gold;
        private GachaViewData _lastRendered;

        /// <summary>装配 Presenter 并接管视图的两个抽卡按钮。</summary>
        /// <param name="bus">总线：查询金币、发送抽卡指令。</param>
        /// <param name="handler">抽卡内核：最近结果与保底状态的唯一来源。</param>
        /// <param name="view">视图（Unity 侧由 <c>GachaHud</c> 实现）。</param>
        /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>。</exception>
        public GachaPresenter(ICqeBus bus, GachaHandler handler, IGachaView view)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            _bus = bus;
            _handler = handler;
            _view = view;
            _gold = 0;
            _lastRendered = new GachaViewData(0, NoResultText, 0, false);
            _view.OnDrawOnceClicked += HandleDrawOnceClicked;
            _view.OnDrawTenClicked += HandleDrawTenClicked;
        }

        /// <summary>
        /// 最近一次刷新到的金币（测试/断言用）。它不是本地账本：抽完必须靠重新查询更新。
        /// </summary>
        public int Gold
        {
            get { return _gold; }
        }

        /// <summary>最近一次推给视图的快照（金币 + 结果文本/项数/保底标记）。</summary>
        public GachaViewData LastRendered
        {
            get { return _lastRendered; }
        }

        /// <summary>
        /// 查金币 + 读处理器最近结果，再依次 <see cref="IGachaView.RenderGold"/>、<see cref="IGachaView.RenderLastResult"/>。
        /// <para>
        /// 结果为空（还没抽过、或抽卡被拒绝且此前也没抽过）也必须照常渲染占位文案与余额，
        /// 不能因为"没有结果"就跳过金币——否则玩家会因为一次拒绝而看不到余额。每次刷新允许分配。
        /// </para>
        /// </summary>
        public void Refresh()
        {
            int gold = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold)).Count;
            _gold = gold;

            GachaResult? result = _handler.LastResult;
            string text = FormatLastResult(result);
            int itemCount = result == null ? 0 : result.Items.Length;
            bool pityTriggered = result != null && result.PityTriggered;

            _lastRendered = new GachaViewData(gold, text, itemCount, pityTriggered);

            // 顺序契约：先金币后结果，视图在一次刷新里读到的是同一份余额推导出来的结果。
            _view.RenderGold(gold);
            _view.RenderLastResult(text);
        }

        /// <summary>
        /// 单抽：先发 <see cref="DrawGachaCommand"/>(1)，再无条件刷新一次。
        /// 抽没抽成都要刷新——余额不足时处理器静默返回，UI 必须回到真实状态。
        /// </summary>
        public void DrawOnce()
        {
            _bus.Send(new DrawGachaCommand(SingleDrawCount));
            Refresh();
        }

        /// <summary>
        /// 十连：先发 <see cref="DrawGachaCommand"/>(10)，再无条件刷新一次。
        /// </summary>
        public void DrawTen()
        {
            _bus.Send(new DrawGachaCommand(TenDrawCount));
            Refresh();
        }

        /// <summary>
        /// 结果文本：<c>最近结果：20001 20002 20004★（保底）</c>。
        /// 稀有项带 ★，整次命令由保底触发时追加后缀；未抽过返回占位文案。
        /// </summary>
        private static string FormatLastResult(GachaResult? result)
        {
            if (result == null)
            {
                return NoResultText;
            }

            var text = new StringBuilder(ResultPrefix);
            for (int i = 0; i < result.Items.Length; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }

                GachaResultItem item = result.Items[i];
                text.Append(item.ItemId);
                if (item.IsRare)
                {
                    text.Append(RareMark);
                }
            }

            if (result.PityTriggered)
            {
                text.Append(PitySuffix);
            }

            return text.ToString();
        }

        private void HandleDrawOnceClicked()
        {
            DrawOnce();
        }

        private void HandleDrawTenClicked()
        {
            DrawTen();
        }
    }
}
