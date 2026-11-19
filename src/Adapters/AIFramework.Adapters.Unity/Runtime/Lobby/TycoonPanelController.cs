#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Tycoon;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 模拟经营场景控制器：建宿主 → 注册 <see cref="TycoonHandler"/> → 查询快照推给 <see cref="TycoonHud"/>，
    /// 并把 HUD 的点击翻译成 CQE 命令后立刻刷新。
    /// <para>
    /// <b>为什么这里自己注册切片</b>：<see cref="AIFrameworkUnityHost"/> 只装配大厅的商店 / 任务 / 邮件 / 签到 / 本地化，
    /// 模拟经营由本场景按需装配——一个场景只付它真正用到的切片的代价。
    /// 背包处理器（金币账本）已由宿主注册，这里<b>绝不再注册</b>（重复注册会直接抛异常）。
    /// </para>
    /// <para>
    /// <b>为什么用同一程序集里的 <c>UnityStreamingAssetsTableLoader</c></b>：宿主没暴露它内部的表加载端口，
    /// 而本类与宿主同属一个适配层程序集，直接复用同一实现，保证整场景只有一套
    /// "表名 → StreamingAssets/AIFrameworkDemo/*.bin" 约定。
    /// </para>
    /// <para>
    /// <b>为什么"重新开始"要重建宿主</b>：<c>CqeBus</c> 没有反注册接口（同一命令类型注册第二次直接抛异常），
    /// 要回到"初始金币 + 空场地"只能换一个全新的宿主与全新的处理器实例。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TycoonPanelController : MonoBehaviour
    {
        /// <summary>演示子表目录（与 <c>AIFrameworkUnityHost</c> 的约定一致，相对 StreamingAssets）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>点一次"结算一次"结算的 tick 数（处理器会把 1..5 之外的值夹回区间）。</summary>
        private const int TicksPerClick = 1;

        /// <summary>模拟经营面板皮肤（本控制器的视图）。</summary>
        [SerializeField] private TycoonHud hud = null!;

        /// <summary>子表加载端口；重开时复用同一实例（本身无状态）。</summary>
        private IBinaryTableLoader? _tables;

        /// <summary>宿主（总线 / 时间源 / 日志 / 背包的唯一入口）；销毁与重开时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>宿主的消息总线；重开时随宿主一起换。</summary>
        private ICqeBus? _bus;

        /// <summary>面板大脑；子表缺失时为 <c>null</c>（面板保持占位文案，不崩）。</summary>
        private TycoonHandler? _handler;

        private void Awake()
        {
            _tables = new UnityStreamingAssetsTableLoader(new UnityAssetLoader(), TableRelativeDirectory);

            // 先订阅再建面板：HUD 的按钮监听在它自己的 Start 里挂，但事件订阅越早越不会漏掉首帧点击。
            SubscribeHud();
            CreatePanel();
        }

        private void Start()
        {
            Refresh();
        }

        private void OnDestroy()
        {
            UnsubscribeHud();

            _handler = null;
            _bus = null;
            _host?.Dispose();
            _host = null;
        }

        /// <summary>
        /// 建宿主 + 处理器并注册；缺表 / 坏表只关掉本面板，错误必须能在 Console 里看到。
        /// 顺序是"先建处理器，全成功后才注册"——绝不把半装配的处理器挂上总线。
        /// </summary>
        private void CreatePanel()
        {
            IBinaryTableLoader? tables = _tables;
            if (tables == null)
            {
                return;
            }

            var host = new AIFrameworkUnityHost(tables);
            host.Initialize();
            _host = host;
            _bus = host.Bus;

            if (hud == null)
            {
                host.Log.Error("[Tycoon] 控制器未挂 TycoonHud，经营面板不装配。");
                return;
            }

            try
            {
                var handler = new TycoonHandler(host.Bus, tables);
                handler.Register(host.Bus);
                _handler = handler;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is InvalidDataException
                || exception is IOException
                || exception is ArgumentException)
            {
                // 缺表/坏表是部署问题，不该让整个场景停摆：只关掉本面板。
                _handler = null;
                host.Log.Error("[Tycoon] 模拟经营切片装配失败（子表 TbTycoonBuilding 缺失或损坏），面板已禁用：" + exception.Message);
            }
        }

        /// <summary>按当前快照刷新 HUD；面板未装配成功（缺表）时保持占位文案。</summary>
        private void Refresh()
        {
            ICqeBus? bus = _bus;
            TycoonHandler? handler = _handler;
            TycoonHud? view = hud;
            if (bus == null || handler == null || view == null)
            {
                return;
            }

            TycoonSnapshot snapshot = bus.Query<TycoonSnapshotQueryRequest, TycoonSnapshot>(new TycoonSnapshotQueryRequest());
            view.Render(snapshot);
        }

        /// <summary>
        /// 发一条命令并立刻刷新。<b>为什么"发完就刷"而不是等事件</b>：本切片对拒绝是静默的
        /// （不抛异常、不发事件），刷新是玩家唯一能看出"点了有没有生效"的途径。
        /// </summary>
        /// <typeparam name="TCommand">命令类型。</typeparam>
        /// <param name="command">命令体。</param>
        private void Send<TCommand>(TCommand command)
            where TCommand : struct, ICommand
        {
            ICqeBus? bus = _bus;
            if (bus != null)
            {
                bus.Send(in command);
            }

            Refresh();
        }

        /// <summary>把 HUD 的五路点击接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            TycoonHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnBuildClicked += HandleBuildClicked;
            view.OnUpgradeClicked += HandleUpgradeClicked;
            view.OnHireClicked += HandleHireClicked;
            view.OnTickClicked += HandleTickClicked;
            view.OnRestartClicked += HandleRestartClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击发两条命令）。</summary>
        private void UnsubscribeHud()
        {
            TycoonHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnBuildClicked -= HandleBuildClicked;
            view.OnUpgradeClicked -= HandleUpgradeClicked;
            view.OnHireClicked -= HandleHireClicked;
            view.OnTickClicked -= HandleTickClicked;
            view.OnRestartClicked -= HandleRestartClicked;
        }

        /// <summary>点某一栋的"建造"：能不能建（未知 id / 已建过 / 金币不足）由处理器判定。</summary>
        /// <param name="buildingId">建筑 id。</param>
        private void HandleBuildClicked(int buildingId)
        {
            Send(new BuildTycoonCommand(buildingId));
        }

        /// <summary>点"升级"：未建造 / 已满级 / 金币不足由处理器静默拒绝。</summary>
        /// <param name="buildingId">目标建筑 id。</param>
        private void HandleUpgradeClicked(int buildingId)
        {
            Send(new UpgradeTycoonCommand(buildingId));
        }

        /// <summary>点"雇员工"：未建造 / 员工位已满 / 金币不足由处理器静默拒绝。</summary>
        /// <param name="buildingId">目标建筑 id。</param>
        private void HandleHireClicked(int buildingId)
        {
            Send(new HireStaffCommand(buildingId));
        }

        /// <summary>点"结算一次"：每次 tick 按当前总收入发金币。</summary>
        private void HandleTickClicked()
        {
            Send(new TycoonTickCommand(TicksPerClick));
        }

        /// <summary>点"重新开始"：换一套全新的宿主与切片，回到初始金币与空场地。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>
        /// 重开：丢弃旧宿主（总线随宿主一起废弃）→ 建新宿主 → 重新注册切片 → 刷新。
        /// <para>
        /// <b>为什么先置空再 Dispose</b>：<c>Dispose</c> 之后再有人调到 <see cref="Refresh"/> 会拿到已释放的宿主，
        /// 先断引用就能让中途的任何刷新变成空操作。
        /// </para>
        /// </summary>
        private void Restart()
        {
            _handler = null;
            _bus = null;
            _host?.Dispose();
            _host = null;

            CreatePanel();
            Refresh();
        }
    }
}
