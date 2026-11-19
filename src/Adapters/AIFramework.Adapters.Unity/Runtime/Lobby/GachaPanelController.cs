#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Gacha;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 抽卡场景控制器：建宿主 → 注册抽卡切片 → 用 <see cref="GachaHud"/> 当视图装配 Presenter。
    /// <para>
    /// 随机源一律用宿主注入的 <see cref="AIFrameworkUnityHost.Random"/>（内部是按固定种子初始化的可复现实现），
    /// 保证"同一场景重进、同一次点击序列"可复现；绝不在场景脚本里 new 随机。
    /// </para>
    /// <para>
    /// 背包处理器已由宿主注册，这里<b>绝不再注册</b>；本场景只额外注册 <c>DrawGachaCommand</c>。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GachaPanelController : MonoBehaviour
    {
        /// <summary>演示子表目录（与 <c>AIFrameworkUnityHost</c> 的约定一致，相对 StreamingAssets）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>抽卡面板皮肤（Presenter 的视图）。</summary>
        [SerializeField] private GachaHud hud = null!;

        /// <summary>宿主（总线/随机源/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>面板大脑；子表缺失时为 <c>null</c>（面板保持占位文案，不崩）。</summary>
        private GachaPresenter? _presenter;

        private void Awake()
        {
            var assets = new UnityAssetLoader();
            IBinaryTableLoader tables = new UnityStreamingAssetsTableLoader(assets, TableRelativeDirectory);

            var host = new AIFrameworkUnityHost(tables);
            host.Initialize();
            _host = host;

            if (hud == null)
            {
                host.Log.Error("[Gacha] 控制器未挂 GachaHud，抽卡面板不装配。");
                return;
            }

            try
            {
                // 顺序：先建处理器与 Presenter，全成功后才注册——绝不把"半装配"的处理器挂上总线。
                var handler = new GachaHandler(host.Bus, host.Random, tables);
                GachaPresenter presenter = new GachaPresenter(host.Bus, handler, hud);
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
                host.Log.Error("[Gacha] 抽卡切片装配失败（子表 TbGachaPool 缺失或损坏），面板已禁用：" + exception.Message);
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
