#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Contracts.Ui;
using AIFramework.Core.Cqe;
using AIFramework.Core.Gameplay;
using AIFramework.Core.Meta;
using AIFramework.Core.Save;
using AIFramework.Core.Ui;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Inventory;
using AIFramework.Features.Localization;
using AIFramework.Features.Mail;
using AIFramework.Features.Quest;
using AIFramework.Features.Shop;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 侧唯一装配入口（对应 Console 侧的 <c>ConsoleAppBootstrap</c>）：
    /// 把总线、因果日志、五个端口实现、会话宿主与签到切片一次性装好，然后交给场景里的
    /// <see cref="AIFrameworkBehaviour"/> 驱动。
    /// <para>
    /// <b>为什么是普通类而不是 MonoBehaviour</b>：装配逻辑必须能在编辑器工具、单元测试、
    /// 甚至无场景的启动脚本里跑；一旦绑上 MonoBehaviour 生命周期，就再也拿不到"不依赖场景"的测试形态。
    /// 场景入口只是它的一个调用方。
    /// </para>
    /// <para>
    /// <b>调用顺序</b>：构造（只建对象，不注册）→ <see cref="Initialize"/>（注册处理器、装配签到切片）→ 使用。
    /// 构造期不抛业务异常，签到切片依赖的外部数据（子表）缺失时只关掉签到，不影响总线与战斗会话。
    /// </para>
    /// </summary>
    public sealed class AIFrameworkUnityHost : IDisposable
    {
        /// <summary>因果日志环形容量：256 条足够覆盖一次装配 + 一个活动周期的排查窗口，且定长零扩容。</summary>
        private const int CausalLogCapacity = 256;

        /// <summary>存档子目录名：不直接写 <c>persistentDataPath</c> 根，避免和工程其它文件混在一起。</summary>
        private const string DefaultStorageSubDirectory = "AIFramework";

        /// <summary>
        /// 演示子表所在目录（相对 StreamingAssets）。<b>为什么写死</b>：Phase 1 只有这一张签到子表，
        /// 路径与 <c>unity/README</c> 的约定一致；正式项目应注入自己的 <see cref="IBinaryTableLoader"/>（Luban 导出目录）。
        /// </summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>适配层自身的因果日志来源标签名。</summary>
        private const string AdapterTagName = "adapter.unity";

        /// <summary>装配完成事件的因果日志标签名。</summary>
        private const string InitTagName = "adapter.unity.host.init";

        /// <summary>真实总线实例。<b>为什么是具体类型而不是 <c>ICqeBus</c></b>：装配层需要能读到它，
        /// 而且 Phase 1 没有装饰器需求（因果日志用显式调用记录，不做总线包装）。</summary>
        private readonly CqeBus _bus;

        /// <summary>定长环形因果日志，热路径只写数值。</summary>
        private readonly RingCausalLog _causalLog;

        /// <summary>数值 Tag 与可读名的映射，仅装配/导出阶段使用。</summary>
        private readonly CausalTagRegistry _tagRegistry;

        /// <summary>本适配层的来源 Tag ID，写因果日志时带上，便于区分"谁发的消息"。</summary>
        private readonly int _sourceTagId;

        /// <summary>时间源。<b>为什么由宿主持有</b>：编辑器要能 <c>SetUtcOverride</c> 钉住日期验证跨天逻辑。</summary>
        private readonly UnityTimeProvider _time;

        /// <summary>局外随机源（局内随机必须用内核自己的种子，见 <see cref="UnityRandomSource"/> 注释）。</summary>
        private readonly UnityRandomSource _random;

        /// <summary>日志端口；所有切片与适配层自身的诊断都走它，禁止直接 <c>Debug.Log</c>。</summary>
        private readonly UnityLogger _logger;

        /// <summary>存档端口（persistentDataPath 下的子目录）。</summary>
        private readonly UnityStoragePort _storage;

        /// <summary>资源端口（StreamingAssets，开发期通道）。</summary>
        private readonly UnityAssetLoader _assets;

        /// <summary>空间查询端口；场景侧的 <c>RegisterCollider</c> 也走它。</summary>
        private readonly UnitySpatialQuery _spatial;

        private readonly UnityVfxService _vfx;

        /// <summary>内核工厂；宿主通过它 <c>Begin</c> 会话，再用 <see cref="UnityKernelFactory.LastRealtime"/> 拿战局引用。</summary>
        private readonly UnityKernelFactory _factory;

        /// <summary>玩法会话宿主（主板）。</summary>
        private readonly GameplaySessionHost _sessions;

        /// <summary>背包切片（真实业务账本，已替换 Phase 1 的适配层占位实现）。</summary>
        private readonly InventoryHandler _inventory;

        /// <summary>红点树（主板底座）：各切片只发增量/设定值指令，不互相引用。</summary>
        private readonly RedDotTree _redDots;

        private readonly PopupQueue _popups;

        /// <summary>商店切片；子表缺失时为 <c>null</c>（见 <see cref="ShopAvailable"/>）。</summary>
        private ShopHandler? _shop;

        /// <summary>任务切片；子表缺失时为 <c>null</c>。</summary>
        private QuestHandler? _quests;

        /// <summary>邮件切片；子表缺失时为 <c>null</c>。</summary>
        private MailHandler? _mails;

        private LocalizationHandler? _localization;

        /// <summary>大厅存档服务（背包/任务/邮件状态落盘）。</summary>
        private SaveService? _lobbySave;

        /// <summary>签到视图：无头实现，只把格子数量打进日志并留一份最近快照。</summary>
        private readonly NullSignInView _signInView;

        /// <summary>子表加载端口：默认从 StreamingAssets 读 <c>.bin</c>，可整体注入替换。</summary>
        private readonly IBinaryTableLoader _tableLoader;

        /// <summary>签到业务处理器；子表缺失时为 <c>null</c>（见 <see cref="SignInAvailable"/>）。</summary>
        private SignInHandler? _signIn;

        /// <summary>签到 UI 大脑；与 <see cref="_signIn"/> 同生共死。</summary>
        private SignInPresenter? _presenter;

        /// <summary>背包处理器是否已注册。<b>为什么需要这个标记</b>：<see cref="Initialize"/> 允许重试
        /// （比如先补上子表再调一次），而总线的重复注册会抛异常，所以注册步骤必须只做一次。</summary>
        private bool _busWired;

        /// <summary>是否已完成一次 <see cref="Initialize"/>。</summary>
        private bool _initialized;

        /// <summary>是否已释放（防止场景卸载后仍被调用）。</summary>
        private bool _disposed;

        /// <summary>
        /// 装配全部端口与切片；所有参数都可省略，默认值面向"挂上就能跑"的演示形态。
        /// </summary>
        /// <param name="tableLoader">子表加载端口；传 <c>null</c> 表示用 StreamingAssets/&lt;演示目录&gt;/*.bin。</param>
        /// <param name="storageSubDirectory">存档子目录；传 <c>null</c> 表示直接用 <c>persistentDataPath</c> 根。</param>
        /// <param name="randomSeed">局外随机种子；固定值保证同一串随机可复现。</param>
        /// <param name="spatialBufferCapacity">空间查询命中缓冲容量，即单次查询最多接收的碰撞体数量。</param>
        /// <param name="spatialLayerMask">空间查询层遮罩，默认全部层。</param>
        public AIFrameworkUnityHost(
            IBinaryTableLoader? tableLoader = null,
            string? storageSubDirectory = DefaultStorageSubDirectory,
            int randomSeed = UnityRandomSource.DefaultSeed,
            int spatialBufferCapacity = UnitySpatialQuery.DefaultBufferCapacity,
            int spatialLayerMask = UnitySpatialQuery.AllLayers)
        {
            _tagRegistry = new CausalTagRegistry();
            _sourceTagId = _tagRegistry.Register(AdapterTagName);
            _causalLog = new RingCausalLog(CausalLogCapacity);
            _logger = new UnityLogger();
            _time = new UnityTimeProvider();
            _random = new UnityRandomSource(randomSeed);
            _storage = new UnityStoragePort(storageSubDirectory);
            _assets = new UnityAssetLoader();
            _spatial = new UnitySpatialQuery(spatialBufferCapacity, spatialLayerMask);
            _vfx = new UnityVfxService(_logger);
            _tableLoader = tableLoader ?? new UnityStreamingAssetsTableLoader(_assets, TableRelativeDirectory);

            // 工厂要拿表加载器组卡牌，所以它必须在表加载器之后构造。
            _factory = new UnityKernelFactory(_spatial, UnityKernelFactory.CreateDefaultPhases(), _tableLoader);
            _sessions = new GameplaySessionHost(_factory);
            _bus = new CqeBus();
            _inventory = new InventoryHandler();
            _redDots = new RedDotTree();
            _popups = new PopupQueue();
            _signInView = new NullSignInView(_logger);
        }

        /// <summary>总线：场景脚本可以自己 <c>Send/Query/Publish</c>，也可注册自己的处理器。</summary>
        public CqeBus Bus
        {
            get { return _bus; }
        }

        /// <summary>会话宿主：<c>Begin / EndAndCollect / Release</c> 的唯一入口。</summary>
        public GameplaySessionHost Sessions
        {
            get { return _sessions; }
        }

        /// <summary>
        /// 签到切片处理器。
        /// <b>未装配成功时访问会抛 <see cref="InvalidOperationException"/></b>，调用前先看
        /// <see cref="SignInAvailable"/>——比返回 <c>null</c> 让调用方到处判空更安全。
        /// </summary>
        public SignInHandler SignIn
        {
            get
            {
                SignInHandler? current = _signIn;
                if (current == null)
                {
                    throw new InvalidOperationException("签到切片尚未装配成功，请先检查子表与 Initialize 的日志。");
                }

                return current;
            }
        }

        /// <summary>签到切片是否可用（子表存在且校验通过）。</summary>
        public bool SignInAvailable
        {
            get { return _signIn != null; }
        }

        /// <summary>签到 UI 大脑；每次 <c>Refresh()</c> 会重组 7 格并推到视图（非帧内路径，允许分配）。</summary>
        public SignInPresenter SignInPresenter
        {
            get
            {
                SignInPresenter? current = _presenter;
                if (current == null)
                {
                    throw new InvalidOperationException("签到切片尚未装配成功，Presenter 不存在。");
                }

                return current;
            }
        }

        /// <summary>签到视图（无头实现）；<see cref="NullSignInView.LastRendered"/> 是最近一次渲染的快照。</summary>
        public NullSignInView SignInView
        {
            get { return _signInView; }
        }

        /// <summary>最近一次签到的 7 格展示数据；从未渲染过时为空列表。</summary>
        public IReadOnlyList<SignInSlotViewData> SignInSlots
        {
            get { return _signInView.LastRendered; }
        }

        /// <summary>空间查询端口：场景对象要靠它把 <c>Collider</c> 绑成战场句柄。</summary>
        /// <summary>特效服务端口：业务只发"播什么"，池/资源/回收由适配层决定。</summary>
        public UnityVfxService Vfx
        {
            get { return _vfx; }
        }

        public UnitySpatialQuery Spatial
        {
            get { return _spatial; }
        }

        /// <summary>内核工厂：<c>LastRealtime</c> 是当前实时战局的唯一入口。</summary>
        public UnityKernelFactory Factory
        {
            get { return _factory; }
        }

        /// <summary>时间源；编辑器里用它 <c>SetUtcOverride</c> 做跨天验证。</summary>
        public UnityTimeProvider Time
        {
            get { return _time; }
        }

        /// <summary>局外随机源。</summary>
        public IRandomSource Random
        {
            get { return _random; }
        }

        /// <summary>存档端口。</summary>
        public IStoragePort Storage
        {
            get { return _storage; }
        }

        /// <summary>资源端口。</summary>
        public IAssetLoader Assets
        {
            get { return _assets; }
        }

        /// <summary>日志端口。</summary>
        public ILogger Log
        {
            get { return _logger; }
        }

        /// <summary>因果日志（导出/断言用，不要进热路径）。</summary>
        public RingCausalLog CausalLog
        {
            get { return _causalLog; }
        }

        /// <summary>Tag 注册表：把数值 Tag 翻成可读名，只用于导出与断言。</summary>
        public CausalTagRegistry CausalTags
        {
            get { return _tagRegistry; }
        }

        /// <summary>是否已完成装配。</summary>
        public bool IsInitialized
        {
            get { return _initialized; }
        }

        /// <summary>
        /// 注册背包处理器并装配签到切片。可重复调用：已注册的部分跳过，签到切片失败会再尝试一次。
        /// </summary>
        /// <exception cref="ObjectDisposedException">宿主已释放。</exception>
        public void Initialize()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AIFrameworkUnityHost));
            }

            WireInventory();
            TryWireMeta();
            TryWireSignIn();

            _causalLog.Record(_tagRegistry.Register(InitTagName), _sourceTagId, CausalOutcome.Success);
            _initialized = true;
        }

        /// <summary>
        /// 释放：解绑签到视图事件并释放未结算的会话引用。场景卸载时由
        /// <see cref="AIFrameworkBehaviour"/> 调用，可重复调用。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            SignInPresenter? presenter = _presenter;
            if (presenter != null)
            {
                presenter.Dispose();
                _presenter = null;
            }

            // 场景卸载时未结算的对局直接丢弃引用：BattleWorld 是纯内存对象，没有原生资源需要释放。
            if (_sessions.HasActiveSession)
            {
                _vfx.Dispose();
            _sessions.Release();
            }
        }

        /// <summary>
        /// 注册背包三件套。<b>为什么装配层必须代做</b>：签到切片在领取时会 <c>Send(GrantItemCommand)</c>、
        /// 在补签时会 <c>Query&lt;InventoryQueryRequest, InventorySnapshot&gt;</c>，只要缺一个处理器，
        /// 总线就会在玩家第一次点领取时抛"未注册处理器"——这是 Phase 1 缺独立背包切片导致的真实缺口。
        /// </summary>
        private void WireInventory()
        {
            if (_busWired)
            {
                return;
            }

            _inventory.Register(_bus);
            _redDots.Register(_bus);

            // 演示用初始金币：正式项目应由存档/服务器下发，这里只保证"挂上就能点商店"。
            _inventory.Seed(MetaItemIds.Gold, 1000);

            _busWired = true;
        }

        /// <summary>
        /// 装配大厅 Meta 切片（商店 / 任务 / 邮件）。任一切片因缺表失败只关掉它自己，
        /// 不牵连总线与其它切片——缺数据是部署问题，不该让整个场景停摆。
        /// </summary>
        private void TryWireMeta()
        {
            _lobbySave = new SaveService(_storage, "lobby");

            if (_shop == null)
            {
                try
                {
                    var shop = new ShopHandler(_bus, _tableLoader);
                    shop.Register(_bus);
                    _shop = shop;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    || exception is InvalidDataException
                    || exception is IOException
                    || exception is ArgumentException)
                {
                    _shop = null;
                    _logger.Error("商店切片装配失败（子表缺失或损坏），已跳过：" + exception.Message);
                }
            }

            if (_quests == null)
            {
                try
                {
                    var quests = new QuestHandler(_bus, _tableLoader);
                    quests.Register(_bus);
                    _quests = quests;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    || exception is InvalidDataException
                    || exception is IOException
                    || exception is ArgumentException)
                {
                    _quests = null;
                    _logger.Error("任务切片装配失败（子表缺失或损坏），已跳过：" + exception.Message);
                }
            }

            if (_mails == null)
            {
                try
                {
                    var mails = new MailHandler(_bus, _time, _tableLoader);
                    mails.Register(_bus);
                    mails.SeedRedDot(_bus);
                    _mails = mails;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    || exception is InvalidDataException
                    || exception is IOException
                    || exception is ArgumentException)
                {
                    _mails = null;
                    _logger.Error("邮件切片装配失败（子表缺失或损坏），已跳过：" + exception.Message);
                }
            }

            if (_localization == null)
            {
                try
                {
                    var localization = new LocalizationHandler(_bus, _tableLoader);
                    localization.Register(_bus);
                    _localization = localization;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    || exception is InvalidDataException
                    || exception is IOException
                    || exception is ArgumentException)
                {
                    _localization = null;
                    _logger.Error("本地化切片装配失败（子表缺失或损坏），已跳过：" + exception.Message);
                }
            }
        }

        /// <summary>
        /// 把一局结果翻成 <see cref="GameFinishedEvent"/> 广播出去：任务/活动/成就类切片只依赖这个事实，
        /// 不需要知道打的是哪个内核、更不需要引用内核类型。
        /// </summary>
        /// <param name="kernelType">产生结果的玩法内核。</param>
        /// <param name="levelId">关卡/场次 ID。</param>
        /// <param name="isVictory">是否胜利。</param>
        public void PublishGameFinished(GameplayKernelType kernelType, int levelId, bool isVictory)
        {
            var finished = new GameFinishedEvent(kernelType, levelId, isVictory);
            _bus.Publish(in finished);
        }

        /// <summary>背包切片处理器（真实账本）。</summary>
        public InventoryHandler Inventory
        {
            get { return _inventory; }
        }

        /// <summary>红点树。</summary>
        /// <summary>弹窗队列端口（UI 表现层用；业务切片只发"要弹哪个"，不关心栈与动效）。</summary>
        public IPopupQueue Popups
        {
            get { return _popups; }
        }

        public RedDotTree RedDots
        {
            get { return _redDots; }
        }

        /// <summary>商店切片；<see cref="ShopAvailable"/> 为 false 时访问会抛异常。</summary>
        public ShopHandler Shop
        {
            get
            {
                ShopHandler? current = _shop;
                if (current == null)
                {
                    throw new InvalidOperationException("商店切片尚未装配成功（子表缺失？）。");
                }

                return current;
            }
        }

        /// <summary>商店是否可用。</summary>
        public bool ShopAvailable
        {
            get { return _shop != null; }
        }

        /// <summary>任务切片；<see cref="QuestAvailable"/> 为 false 时访问会抛异常。</summary>
        public QuestHandler Quests
        {
            get
            {
                QuestHandler? current = _quests;
                if (current == null)
                {
                    throw new InvalidOperationException("任务切片尚未装配成功（子表缺失？）。");
                }

                return current;
            }
        }

        /// <summary>任务是否可用。</summary>
        public bool QuestAvailable
        {
            get { return _quests != null; }
        }

        /// <summary>邮件切片；<see cref="MailAvailable"/> 为 false 时访问会抛异常。</summary>
        public MailHandler Mails
        {
            get
            {
                MailHandler? current = _mails;
                if (current == null)
                {
                    throw new InvalidOperationException("邮件切片尚未装配成功（子表缺失？）。");
                }

                return current;
            }
        }

        /// <summary>邮件是否可用。</summary>
        /// <summary>本地化切片；<see cref="LocalizationAvailable"/> 为 false 时访问会抛异常。</summary>
        public LocalizationHandler Localization
        {
            get
            {
                LocalizationHandler? current = _localization;
                if (current == null)
                {
                    throw new InvalidOperationException("本地化切片未装配（TbLocalization.bin 缺失）。请先跑 tools/fixtures 生成夹具。");
                }

                return current;
            }
        }

        /// <summary>本地化子表是否装配成功。</summary>
        public bool LocalizationAvailable
        {
            get { return _localization != null; }
        }

        public bool MailAvailable
        {
            get { return _mails != null; }
        }

        /// <summary>大厅存档服务；未 Initialize 前为 null。</summary>
        public SaveService? LobbySave
        {
            get { return _lobbySave; }
        }

        /// <summary>
        /// 装配签到切片。失败只关闭签到，不牵连总线与战斗沙盒：
        /// 子表缺失/校验不过属于"数据与部署问题"，不应该让整个框架在场景里停摆；
        /// 错误一律走 <c>Log.Error</c>，必须能在 Console 里看到。
        /// </summary>
        private void TryWireSignIn()
        {
            if (_signIn != null)
            {
                return;
            }

            try
            {
                var signIn = new SignInHandler(_bus, _time, _tableLoader);
                signIn.Register(_bus);
                _presenter = new SignInPresenter(signIn, _signInView);
                _signIn = signIn;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is InvalidDataException
                || exception is IOException
                || exception is ArgumentException)
            {
                _signIn = null;
                _presenter = null;
                _logger.Error("签到切片装配失败（子表缺失或损坏），已跳过：" + exception.Message);
            }
        }
    }

    /// <summary>
    /// 无头签到视图：只把"渲染了多少格"记进日志，并把最近一次渲染结果留成快照，
    /// 供控制台打印与自动化断言使用。<b>它不是 UI 实现</b>——真机 UI 由 <c>ISignInView</c> 的
    /// 生成式实现（<c>SignInViewBinder</c>）接，本类只在没有 UI 的场景里占位。
    /// </summary>
    public sealed class NullSignInView : ISignInView
    {
        /// <summary>日志端口；视图自己不打 <c>Debug.Log</c>，一律走注入的端口，保持可替换。</summary>
        private readonly ILogger _logger;

        /// <summary>最近一次渲染结果；初值为空列表，调用方无需判空。</summary>
        private IReadOnlyList<SignInSlotViewData> _lastRendered = Array.Empty<SignInSlotViewData>();

        /// <summary>以日志端口构造。</summary>
        /// <param name="logger">日志端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="logger"/> 为 <c>null</c>。</exception>
        public NullSignInView(ILogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _logger = logger;
        }

        /// <inheritdoc />
        public event Action? OnClaimClicked;

        /// <inheritdoc />
        public event Action<int>? OnRetroactiveClicked;

        /// <summary>最近一次渲染的快照。<b>为什么留一份</b>：无头视图没有界面可看，
        /// 演示与断言只能从视图侧取回实际渲染出来的数据（Console 侧同名实现也是这么做的）。</summary>
        public IReadOnlyList<SignInSlotViewData> LastRendered
        {
            get { return _lastRendered; }
        }

        /// <inheritdoc />
        /// <remarks>不判空、不改数据：Presenter 每次 <c>Refresh</c> 都新建列表且之后不再改动它，因此直接存引用是安全的。</remarks>
        /// <exception cref="ArgumentNullException"><paramref name="slots"/> 为 <c>null</c>。</exception>
        public void RenderSlots(IReadOnlyList<SignInSlotViewData> slots)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            _lastRendered = slots;
            _logger.Info("[SignIn] 渲染格子数=" + slots.Count);
        }

        /// <summary>模拟点击"领取"（无头场景没有按钮，演示与测试靠它驱动）。</summary>
        public void SimulateClaimClicked()
        {
            Action? handler = OnClaimClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>模拟点击某一格的"补签"。参数为 1..7 的格子序号，Presenter 会换算成绝对天序号。</summary>
        /// <param name="dayIndexOrSlotIndex">格子序号（1..7）。</param>
        public void SimulateRetroactiveClicked(int dayIndexOrSlotIndex)
        {
            Action<int>? handler = OnRetroactiveClicked;
            if (handler != null)
            {
                handler(dayIndexOrSlotIndex);
            }
        }
    }

    /// <summary>
    /// StreamingAssets 子表加载器：把表名映射成 <c>&lt;relativeDirectory&gt;/&lt;tableName&gt;.bin</c> 再交给资源端口。
    /// <para>
    /// <b>缺表返回 <c>null</c> 而不是抛异常</b>：这是 <see cref="IBinaryTableLoader"/> 的契约——
    /// 缺表由切片显式判定失败（签到切片会抛 <c>InvalidOperationException</c>），
    /// 适配层既不能替它决定"用空表继续"，也不能自己造一张假表。
    /// </para>
    /// </summary>
    internal sealed class UnityStreamingAssetsTableLoader : IBinaryTableLoader
    {
        /// <summary>子表文件后缀（约定 <c>.bin</c>，与 Luban cs-bin 导出一致）。</summary>
        private const string TableFileExtension = ".bin";

        /// <summary>资源端口。</summary>
        private readonly IAssetLoader _assets;

        /// <summary>子目录前缀（不含首尾斜杠）；空串表示直接放在 StreamingAssets 根。</summary>
        private readonly string _relativeDirectory;

        /// <summary>注入资源端口与子目录。</summary>
        /// <param name="assets">资源端口。</param>
        /// <param name="relativeDirectory">子目录，可为空串。</param>
        /// <exception cref="ArgumentNullException"><paramref name="assets"/> 为 <c>null</c>。</exception>
        public UnityStreamingAssetsTableLoader(IAssetLoader assets, string relativeDirectory)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }

            _assets = assets;
            _relativeDirectory = relativeDirectory ?? string.Empty;
        }

        /// <inheritdoc />
        public byte[]? Load(string tableName)
        {
            if (tableName == null)
            {
                throw new ArgumentNullException(nameof(tableName));
            }

            string fileName = tableName + TableFileExtension;
            string key = _relativeDirectory.Length == 0
                ? fileName
                : _relativeDirectory + "/" + fileName;

            if (!_assets.Exists(key))
            {
                return null;
            }

            return _assets.LoadBytes(key);
        }
    }

}
