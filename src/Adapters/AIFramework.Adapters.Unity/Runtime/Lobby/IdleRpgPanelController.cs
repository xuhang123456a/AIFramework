#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.IdleRpg;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 挂机 RPG 场景控制器：建宿主 → 注册 <see cref="IdleRpgHandler"/> → 查询快照推给 <see cref="IdleRpgHud"/>，
    /// 并把 HUD 的点击翻译成 CQE 命令后立刻刷新。
    /// <para>
    /// <b>为什么这里自己注册切片</b>：<see cref="AIFrameworkUnityHost"/> 只装配大厅的商店 / 任务 / 邮件 / 签到 / 本地化，
    /// 挂机 RPG 由本场景按需装配。金币走宿主的背包切片（已注册），这里<b>不再注册</b>背包；
    /// 本切片只发 <c>GrantItemCommand</c>，从不需要自己查余额。
    /// </para>
    /// <para>
    /// <b>为什么不驱动时间</b>：战斗推进完全由"打 10 tick"的显式命令驱动，本控制器不在 <c>Update</c> 里自动结算——
    /// 自动挂机是产品决策，Phase 1 先保证每一步都是玩家点出来的确定性推进。
    /// </para>
    /// <para>
    /// <b>为什么"重新开始"要重建宿主</b>：<c>CqeBus</c> 没有反注册接口（同一命令类型注册第二次直接抛异常），
    /// 要回到"第 1 关、1 级、0 经验"只能换一个全新的宿主与全新的处理器实例。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IdleRpgPanelController : MonoBehaviour
    {
        /// <summary>演示子表目录（与 <c>AIFrameworkUnityHost</c> 的约定一致，相对 StreamingAssets）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>点一次"打 10 tick"推进的战斗 tick 数（构造期会夹到 1..10）。</summary>
        private const int TicksPerClick = 10;

        /// <summary>挂机 RPG 面板皮肤（本控制器的视图）。</summary>
        [SerializeField] private IdleRpgHud hud = null!;

        /// <summary>子表加载端口；重开时复用同一实例（本身无状态）。</summary>
        private IBinaryTableLoader? _tables;

        /// <summary>宿主（总线 / 时间源 / 日志 / 背包的唯一入口）；销毁与重开时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>宿主的消息总线；重开时随宿主一起换。</summary>
        private ICqeBus? _bus;

        /// <summary>面板大脑；子表缺失时为 <c>null</c>（面板保持占位文案，不崩）。</summary>
        private IdleRpgHandler? _handler;

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
                host.Log.Error("[IdleRpg] 控制器未挂 IdleRpgHud，挂机面板不装配。");
                return;
            }

            try
            {
                // initialHeroLevel 用切片的默认值（1 级）：重开等于"新档"，不继承上一轮等级。
                var handler = new IdleRpgHandler(host.Bus, tables);
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
                host.Log.Error("[IdleRpg] 挂机 RPG 切片装配失败（子表 TbIdleStage 缺失或损坏），面板已禁用：" + exception.Message);
            }
        }

        /// <summary>按当前快照刷新 HUD；面板未装配成功（缺表）时保持占位文案。</summary>
        private void Refresh()
        {
            ICqeBus? bus = _bus;
            IdleRpgHandler? handler = _handler;
            IdleRpgHud? view = hud;
            if (bus == null || handler == null || view == null)
            {
                return;
            }

            IdleRpgSnapshot snapshot = bus.Query<IdleRpgSnapshotQueryRequest, IdleRpgSnapshot>(new IdleRpgSnapshotQueryRequest());
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

        /// <summary>把 HUD 的四路点击接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            IdleRpgHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnBattleTickClicked += HandleBattleTickClicked;
            view.OnLevelUpClicked += HandleLevelUpClicked;
            view.OnAdvanceStageClicked += HandleAdvanceStageClicked;
            view.OnRestartClicked += HandleRestartClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击发两条命令）。</summary>
        private void UnsubscribeHud()
        {
            IdleRpgHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnBattleTickClicked -= HandleBattleTickClicked;
            view.OnLevelUpClicked -= HandleLevelUpClicked;
            view.OnAdvanceStageClicked -= HandleAdvanceStageClicked;
            view.OnRestartClicked -= HandleRestartClicked;
        }

        /// <summary>点"打 10 tick"：这一场打不打得死敌人、通不通关由处理器结算。</summary>
        private void HandleBattleTickClicked()
        {
            Send(new IdleBattleTickCommand(TicksPerClick));
        }

        /// <summary>点"升一级"：经验是否够由处理器判定。</summary>
        private void HandleLevelUpClicked()
        {
            Send(new LevelUpHeroCommand());
        }

        /// <summary>点"进下一关"：当前关是否已通关（或首关尚未开打）由处理器判定。</summary>
        private void HandleAdvanceStageClicked()
        {
            Send(new AdvanceStageCommand());
        }

        /// <summary>点"重新开始"：换一套全新的宿主与切片，回到第 1 关、1 级。</summary>
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
