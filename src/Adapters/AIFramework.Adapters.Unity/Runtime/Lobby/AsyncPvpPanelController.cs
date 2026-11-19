#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.AsyncPvp;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 异步 PVP 场景控制器：建宿主 → 注册 <see cref="AsyncPvpHandler"/> → 查询档案与对手池推给
    /// <see cref="AsyncPvpHud"/>，并把 HUD 的三路点击翻译成 CQE 命令后立刻刷新。
    /// <para>
    /// <b>本切片不需要子表</b>：对手池与算分全在处理器里用纯算术生成。表加载端口仍然要建，
    /// 因为宿主会用它装配商店 / 任务 / 邮件 / 签到 / 本地化——那几片缺表只会各自关闭，不影响本面板。
    /// </para>
    /// <para>
    /// <b>为什么"挑战"用手上这份对手池的第 1 个</b>：玩家看到的就是这份池子，"挑战"必须打的正是屏幕上第 1 行，
    /// 否则"点刷新换对手"这件操作在面板上毫无意义。打完场次推进后池子会变，面板<b>不</b>自动换——
    /// 换对手的入口只有『刷新』，这是本演示要展示的异步 PVP 基本节奏。
    /// </para>
    /// <para>
    /// <b>为什么"重新开始"要重建宿主</b>：<c>CqeBus</c> 没有反注册接口（同一命令类型注册第二次直接抛异常），
    /// 要回到"初始段位 + 空档案"只能换一个全新的宿主与全新的处理器实例。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AsyncPvpPanelController : MonoBehaviour
    {
        /// <summary>演示子表目录（与 <c>AIFrameworkUnityHost</c> 的约定一致，相对 StreamingAssets）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>
        /// 进攻方战力：固定值，决定"每次挑战的结果可复现"。
        /// 合法区间是 0..1000（越界由切片静默拒绝），100 落在中段，演示里既能赢也能输。
        /// </summary>
        private const int AttackerPower = 100;

        /// <summary>对手池从未查过时的占位池（<c>Render</c> 不接受 null，面板必须一直有一份可画的值）。</summary>
        private static readonly PvpOpponentList EmptyPool = new PvpOpponentList(Array.Empty<DefenseSnapshot>());

        /// <summary>异步 PVP 面板皮肤（本控制器的视图）。</summary>
        [SerializeField] private AsyncPvpHud hud = null!;

        /// <summary>子表加载端口；重开时复用同一实例（本身无状态）。</summary>
        private IBinaryTableLoader? _tables;

        /// <summary>宿主（总线 / 时间源 / 日志 / 背包的唯一入口）；销毁与重开时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>宿主的消息总线；重开时随宿主一起换。</summary>
        private ICqeBus? _bus;

        /// <summary>异步 PVP 切片处理器；装配失败时为 <c>null</c>（面板保持占位文案，不崩）。</summary>
        private AsyncPvpHandler? _handler;

        /// <summary>手上这份对手池：『挑战』打它的第 1 个，『刷新』才换它。</summary>
        private PvpOpponentList? _opponents;

        private void Awake()
        {
            _tables = new UnityStreamingAssetsTableLoader(new UnityAssetLoader(), TableRelativeDirectory);

            // 先订阅再建面板：HUD 的按钮监听在它自己的 Start 里挂，但事件订阅越早越不会漏掉首帧点击。
            SubscribeHud();
            CreatePanel();
        }

        private void Start()
        {
            // 对手池必须先于首帧渲染查一次，否则面板会先画一帧"对手池为空"。
            ReloadOpponents();
            Refresh();
        }

        private void OnDestroy()
        {
            UnsubscribeHud();

            _handler = null;
            _opponents = null;
            _bus = null;
            _host?.Dispose();
            _host = null;
        }

        /// <summary>
        /// 建宿主 + 处理器并注册；装配失败只关掉本面板，错误必须能在 Console 里看到。
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
                host.Log.Error("[AsyncPvp] 控制器未挂 AsyncPvpHud，异步 PVP 面板不装配。");
                return;
            }

            try
            {
                var handler = new AsyncPvpHandler(host.Bus);
                handler.Register(host.Bus);
                _handler = handler;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is InvalidDataException
                || exception is IOException
                || exception is ArgumentException)
            {
                // 重复注册等装配错误同样只关掉本面板，不让整个场景停摆。
                _handler = null;
                host.Log.Error("[AsyncPvp] 异步 PVP 切片装配失败，面板已禁用：" + exception.Message);
            }
        }

        /// <summary>
        /// 重新查一次对手池并记在手上；切片未装配（总线为空或处理器注册失败）时把池子清空，
        /// 面板会画"对手池为空"。
        /// <para>
        /// <b>为什么必须连处理器一起判</b>：对手池查询处理器是随切片注册的，
        /// 装配失败时总线还在、查询却没人接——只判总线会在这里抛"未注册处理器"，
        /// 把"面板已禁用"变成每个 Start 一条异常。
        /// </para>
        /// </summary>
        private void ReloadOpponents()
        {
            ICqeBus? bus = _bus;
            if (bus == null || _handler == null)
            {
                _opponents = null;
                return;
            }

            _opponents = bus.Query<PvpOpponentsQueryRequest, PvpOpponentList>(new PvpOpponentsQueryRequest());
        }

        /// <summary>
        /// 按当前档案 / 手上对手池 / 最近一场结果刷新 HUD。
        /// 面板未装配成功时什么都不做，保持占位文案。
        /// </summary>
        private void Refresh()
        {
            ICqeBus? bus = _bus;
            AsyncPvpHandler? handler = _handler;
            AsyncPvpHud? view = hud;
            if (bus == null || handler == null || view == null)
            {
                return;
            }

            PvpProfileSnapshot profile = bus.Query<PvpProfileQueryRequest, PvpProfileSnapshot>(
                new PvpProfileQueryRequest());

            // 一次都没打过时 LastResult 是 default（全 0、判负），必须用场次把它挡在渲染之外。
            AsyncDuelResult? lastResult = handler.DuelCount == 0 ? (AsyncDuelResult?)null : handler.LastResult;

            view.Render(profile, _opponents ?? EmptyPool, lastResult);
        }

        /// <summary>把 HUD 的三路点击接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            AsyncPvpHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnFightClicked += HandleFightClicked;
            view.OnRefreshClicked += HandleRefreshClicked;
            view.OnRestartClicked += HandleRestartClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击发两条命令）。</summary>
        private void UnsubscribeHud()
        {
            AsyncPvpHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnFightClicked -= HandleFightClicked;
            view.OnRefreshClicked -= HandleRefreshClicked;
            view.OnRestartClicked -= HandleRestartClicked;
        }

        /// <summary>
        /// 点"挑战"：用手上对手池第 1 个 + 固定战力发一条 <see cref="AsyncDuelCommand"/>。
        /// 池子为空（或面板未装配）就什么都不做——切片对非法输入同样是静默口径。
        /// </summary>
        private void HandleFightClicked()
        {
            ICqeBus? bus = _bus;
            AsyncPvpHandler? handler = _handler;
            if (bus == null || handler == null)
            {
                return;
            }

            PvpOpponentList pool = _opponents ?? EmptyPool;
            if (pool.Opponents.Length == 0)
            {
                return;
            }

            bus.Send(new AsyncDuelCommand(pool.Opponents[0], AttackerPower));
            Refresh();
        }

        /// <summary>点"刷新"：重新查对手池（场次推进后池子会换一批）。</summary>
        private void HandleRefreshClicked()
        {
            ReloadOpponents();
            Refresh();
        }

        /// <summary>点"重新开始"。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>
        /// 重开：丢弃旧宿主（总线随宿主一起废弃）→ 建新宿主 → 重新注册切片 → 重新查池 → 刷新。
        /// <para>
        /// <b>为什么先置空再 Dispose</b>：<c>Dispose</c> 之后再有人调到 <see cref="Refresh"/> 会拿到已释放的宿主，
        /// 先断引用就能让中途的任何刷新变成空操作。
        /// </para>
        /// </summary>
        private void Restart()
        {
            _handler = null;
            _opponents = null;
            _bus = null;
            _host?.Dispose();
            _host = null;

            CreatePanel();
            ReloadOpponents();
            Refresh();
        }
    }
}
