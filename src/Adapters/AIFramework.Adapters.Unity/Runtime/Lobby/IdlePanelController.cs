#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Ports;
using AIFramework.Features.IdleTycoon;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 放置场景控制器：建宿主 → 注册放置切片 → 用 <see cref="IdleHud"/> 当视图装配 Presenter。
    /// <para>
    /// <b>为什么这里自己注册切片</b>：<see cref="AIFrameworkUnityHost"/> 只装配大厅的商店/任务/邮件/签到，
    /// 放置与抽卡切片由各自场景的控制器按需装配——一个场景只付它真正用到的切片的代价。
    /// 背包处理器已由宿主注册，这里<b>绝不再注册</b>（重复注册会直接抛异常）。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="UnityStreamingAssetsTableLoader"/></b>：宿主没有暴露它内部的表加载端口，
    /// 而本类与宿主同属一个适配层程序集，直接复用同一实现并<b>把它注入宿主</b>，
    /// 保证整场景只有一套"表名 → StreamingAssets/*.bin"约定。正式项目应换成 Luban 导出目录的实现。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IdlePanelController : MonoBehaviour
    {
        /// <summary>演示子表目录（与 <c>AIFrameworkUnityHost</c> 的约定一致，相对 StreamingAssets）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>放置面板皮肤（Presenter 的视图）。</summary>
        [SerializeField] private IdleHud hud = null!;

        /// <summary>宿主（总线/时间源/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>面板大脑；子表缺失时为 <c>null</c>（面板保持占位文案，不崩）。</summary>
        private IdlePresenter? _presenter;

        private void Awake()
        {
            var assets = new UnityAssetLoader();
            IBinaryTableLoader tables = new UnityStreamingAssetsTableLoader(assets, TableRelativeDirectory);

            var host = new AIFrameworkUnityHost(tables);
            host.Initialize();
            _host = host;

            if (hud == null)
            {
                host.Log.Error("[Idle] 控制器未挂 IdleHud，放置面板不装配。");
                return;
            }

            try
            {
                // 顺序：先建处理器与 Presenter，全成功后才注册——绝不把"半装配"的处理器挂上总线。
                var handler = new IdleTycoonHandler(host.Bus, host.Time, tables);
                IdlePresenter presenter = new IdlePresenter(host.Bus, host.Time, handler, hud, tables);
                handler.Register(host.Bus);
                _presenter = presenter;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is InvalidDataException
                || exception is IOException
                || exception is ArgumentException)
            {
                // 缺表/坏表是部署问题，不该让整个场景停摆：只关掉本面板，错误必须能在 Console 里看到。
                _presenter = null;
                host.Log.Error("[Idle] 放置切片装配失败（子表 TbBuilding 缺失或损坏），面板已禁用：" + exception.Message);
            }
        }

        private void Start()
        {
            _presenter?.Refresh();
        }

        private void OnDestroy()
        {
            // Presenter 不持有非托管资源，解引用即可；宿主必须 Dispose（释放会话与签到视图事件）。
            _presenter = null;
            _host?.Dispose();
            _host = null;
        }
    }
}
