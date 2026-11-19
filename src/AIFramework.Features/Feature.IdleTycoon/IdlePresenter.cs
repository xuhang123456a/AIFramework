#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.IdleTycoon
{
    /// <summary>
    /// 放置面板 UI 大脑：把"金币余额 + 各建筑等级 + 每秒产出"翻译成 <see cref="BuildingViewData"/> 推给视图，
    /// 并把视图点击转发成 <see cref="UpgradeBuildingCommand"/>。
    /// <para>
    /// 这里不算价、不扣钱、不改等级：<see cref="BuildingViewData.Affordable"/> 只是展示标记，
    /// 能否升级永远由 <see cref="IdleTycoonHandler"/> 裁决（被拒绝时它静默返回，这里不做二次判断）。
    /// 唯一持有的业务字段是 <see cref="_gold"/> 这份"最近一次查询到的金币"镜像，绝不在本地加减。
    /// </para>
    /// <para>
    /// <b>为什么要再读一次子表</b>：<see cref="IdleTycoonHandler"/> 只暴露等级（<c>GetLevel</c>）与总产出，
    /// 不暴露建筑静态定义（<c>maxLevel</c> / <c>upgradeCost</c> / 基础产出），而面板必须显示它们。
    /// 因此 Presenter 用同一个 <see cref="IBinaryTableLoader"/> 读同一张 <c>TbBuilding</c> 的<b>只读副本</b>，
    /// 与处理器之间没有任何共享可变状态（表是只读配置，读两遍不改变任何语义）。
    /// </para>
    /// </summary>
    public sealed class IdlePresenter
    {
        private readonly ICqeBus _bus;
        private readonly ITimeProvider _time;
        private readonly IdleTycoonHandler _handler;
        private readonly IIdleView _view;
        private readonly IdleTables _tables;
        private int _gold;
        private double _lastRefreshSeconds;

        /// <summary>装配 Presenter 并接管视图的升级点击。</summary>
        /// <param name="bus">总线：查询金币、发送升级指令。</param>
        /// <param name="time">时间源：只用于给"最近一次刷新"打单调时刻戳（见 <see cref="LastRefreshElapsedSeconds"/>）。</param>
        /// <param name="handler">放置玩法内核：等级与总产出的唯一来源。</param>
        /// <param name="view">视图（Unity 侧由 <c>IdleHud</c> 实现）。</param>
        /// <param name="tables">子表加载端口：读与处理器同名同布局的 <c>TbBuilding</c> 只读副本。</param>
        /// <exception cref="ArgumentNullException">任一参数为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">子表布局/取值非法。</exception>
        public IdlePresenter(
            ICqeBus bus,
            ITimeProvider time,
            IdleTycoonHandler handler,
            IIdleView view,
            IBinaryTableLoader tables)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            if (tables == null)
            {
                throw new ArgumentNullException(nameof(tables));
            }

            _bus = bus;
            _time = time;
            _handler = handler;
            _view = view;
            _tables = new IdleTables(tables);
            _gold = 0;
            _lastRefreshSeconds = time.ElapsedSeconds;
            _view.OnUpgradeClicked += HandleUpgradeClicked;
        }

        /// <summary>
        /// 最近一次刷新到的金币（测试/断言用）。它不是本地账本：升级后必须靠重新查询更新，
        /// 视图点击与 <see cref="Upgrade"/> 都不在这里做加减。
        /// </summary>
        public int Gold
        {
            get { return _gold; }
        }

        /// <summary>
        /// 最近一次 <see cref="Refresh"/> 的 <see cref="ITimeProvider.ElapsedSeconds"/> 时间戳（诊断/断言用）。
        /// <para>
        /// <b>收益结算不在这里计时</b>：离线收益的基线由 <see cref="IdleTycoonHandler"/> 自持，
        /// Presenter 只把"面板何时刷新"记在注入的时间源上，供后续做离线收益倒计时/埋点时复用同一时钟。
        /// </para>
        /// </summary>
        public double LastRefreshElapsedSeconds
        {
            get { return _lastRefreshSeconds; }
        }

        /// <summary>
        /// 查金币 + 逐栋取等级，再依次 <see cref="IIdleView.RenderGold"/>、<see cref="IIdleView.RenderIncome"/>、
        /// <see cref="IIdleView.RenderBuildings"/>。
        /// <para>
        /// 金币只查一次且与建筑行数无关：空建筑表（子表 0 行）是合法配置，也必须照常渲染余额与收入。
        /// 每次刷新允许分配（非帧内热路径）。
        /// </para>
        /// </summary>
        public void Refresh()
        {
            int gold = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold)).Count;
            _gold = gold;
            _lastRefreshSeconds = _time.ElapsedSeconds;

            int count = _tables.Count;
            List<BuildingViewData> buildings = new List<BuildingViewData>(count);
            for (int i = 0; i < count; i++)
            {
                BuildingDef def = _tables.Get(i);
                int level = _handler.GetLevel(def.BuildingId);

                // 与处理器受理条件逐条对齐：未满级 且 金币 >= 升级费用（恰好够也算可升）。
                bool affordable = level < def.MaxLevel && gold >= def.UpgradeCost;
                buildings.Add(new BuildingViewData(
                    def.BuildingId,
                    level,
                    def.MaxLevel,
                    def.UpgradeCost,
                    level * def.BaseIncome,
                    affordable));
            }

            // 顺序契约：先金币、再收入、最后列表，视图在一次刷新里读到的是同一份余额推导出来的结果。
            _view.RenderGold(gold);
            _view.RenderIncome(FormatIncome(_handler.IncomePerSecond));
            _view.RenderBuildings(buildings);
        }

        /// <summary>
        /// 升级指定建筑：先发 <see cref="UpgradeBuildingCommand"/>，再无条件刷新一次。
        /// <para>
        /// 升没升成都要刷新——未知建筑 / 已满级 / 金币不足时处理器静默返回，
        /// UI 必须回到内核里的真实状态，而不是停留在"看起来升了"的假象上。
        /// </para>
        /// </summary>
        /// <param name="buildingId">目标建筑 ID；未知 ID 由处理器静默拒绝（本方法不抛异常）。</param>
        public void Upgrade(int buildingId)
        {
            _bus.Send(new UpgradeBuildingCommand(buildingId));
            Refresh();
        }

        /// <summary>每秒收入文本：数值只来自处理器，视图不再做格式化。</summary>
        private static string FormatIncome(int incomePerSecond)
        {
            return "收入 " + incomePerSecond + "/秒";
        }

        private void HandleUpgradeClicked(int buildingId)
        {
            Upgrade(buildingId);
        }
    }
}
