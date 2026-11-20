#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.VisualNovel;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 视觉小说场景控制器：建宿主 → 直接构造 <see cref="VisualNovelWorld"/>（内置 16 节点演示剧本）→
    /// 把只读视图推给 <see cref="VisualNovelHud"/> → 玩家点"继续/选项"驱动 → 结局结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new VisualNovelWorld</c></b>：适配层是组合根（
    /// <c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 没有"视觉小说"值、<c>UnityKernelFactory</c> 也没有对应分支
    /// （与 <c>BoardGameController</c> / <c>RhythmController</c> 完全同因）：文字冒险对主板而言
    /// 只是另一个 Turn 会话，为一个纯演示场景去扩枚举与工厂，会让存档协议与契约付长期成本。
    /// 因此这里直接构造并<b>自己持有这次会话</b>（不登记进 <c>host.Sessions</c>），
    /// 结算时直接调 <see cref="IGameplaySession.CollectResult"/>。宿主仍然负责总线、日志与背包——
    /// 本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何剧情分支判断</b>：当前是哪个节点、有哪些<b>可用</b>选项（条件是否满足）、
    /// 推进会不会结束、结局 id 是多少，全部由内核读视图回答；本类只做四件事：装配宿主、
    /// 把输入转成 <c>TryAdvance</c>/<c>TryChoose</c>、把视图推给 HUD、结局结算并广播事实。
    /// 一旦适配层自己判一次"有没有钥匙所以该不该显示这个选项"，改内核而不改 UI 时两侧就会静默分叉。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。本类的"时间轴"只有一条：每帧读 <c>Time.deltaTime</c> 累加自动播放计时器。
    /// </para>
    /// <para>
    /// <b>剧本来源</b>：<see cref="DemoScript"/> 是<b>写死的演示内容</b>（见 <see cref="BuildDemoScript"/>
    /// 的节点图注释）。正式项目应由关卡配置/表格导出后注入 <c>VisualNovelWorld</c> 的构造函数，
    /// 内核只消费、不加载。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VisualNovelController : MonoBehaviour
    {
        /// <summary>
        /// 场次 ID：<b>不暴露成 <c>[SerializeField]</c></b>。文字冒险只有一个故事，
        /// "选关"在本玩法里是伪概念；它只作为 <c>GameplayInitData.LevelId</c> 与
        /// <c>PublishGameFinished</c> 的战绩口径，两处必须是同一个值。
        /// </summary>
        private const int DefaultLevelId = 1;

        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：文字冒险是单人玩法，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对（内核用例为了简洁传 0，两者等价）。
        /// </summary>
        private const int SinglePlayerUnitCount = 1;

        /// <summary>
        /// 自动播放的最小间隔（秒）。<b>为什么要有下限</b>：<see cref="autoAdvanceSeconds"/> 是
        /// 场景里的可配字段，写 0 或负数会让"每帧推进一句"看起来像剧情在闪屏；
        /// 夹一次比在内核里加保护更合适——内核根本不该知道"自动播放"这种表现层的存在。
        /// </summary>
        private const float MinAutoAdvanceSeconds = 0.25f;

        /// <summary>
        /// 演示剧本里的"黄铜钥匙"道具 id。<b>它同时是条件选项的标记来源</b>：
        /// 内核把 <c>requiredItemId</c> 解释为 <c>1 &lt;&lt; (id &amp; 31)</c> 的 flag 位，
        /// 发奖节点与条件选项必须写同一个 id，否则选项永远不会出现（HUD 状态行里的
        /// "标记"读数就是给这类错配留的观察口）。
        /// </summary>
        private const int DemoKeyItemId = 1001;

        /// <summary>
        /// 内置演示剧本（16 节点、4 个结局，节点图见 <see cref="BuildDemoScript"/>）。
        /// <para>
        /// <b>为什么是 <c>static readonly</c> 而不是每次重开都重建</b>：内核构造时对节点数组与每个
        /// <c>Choices</c> 数组都做了防御性拷贝（见 <c>VisualNovelScript</c>），共享这份只读剧本
        /// 不会被任何一局改写；重开只付一次世界构造的代价。（仍是演示数据，正式项目从表里来。）
        /// </para>
        /// </summary>
        private static readonly VisualNovelNode[] DemoScript = BuildDemoScript();

        /// <summary>演示剧本里的结局总数（End 节点个数，用于 HUD 的"共 N 个结局"口径）。</summary>
        private static readonly int DemoEndingCount = CountEndings(DemoScript);

        /// <summary>视觉小说皮肤（说话人/正文/状态行/推进按钮/3 个选项按钮，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private VisualNovelHud hud = null!;

        /// <summary>
        /// 随机种子。<b>为什么内核明明"零随机"还留着它</b>：文字冒险的确定性来自剧本本身，
        /// 但进门数据是统一契约；固定值让日志里的"同种子开局"与其它玩法保持同一套记账口径。
        /// </summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>
        /// 是否自动播放：为 true 时 Line 节点按 <see cref="autoAdvanceSeconds"/> 自动推进；
        /// 为 false 时只由 <see cref="Advance"/>（"继续"按钮）驱动。
        /// <para>
        /// <b>自动播放绝不替玩家选选项</b>：Choice 节点是剧情决策面，替人选等于篡改故事；
        /// 因此自动播放到达 Choice 就停下等点击（这也是演示脚本里"三个分支"最需要人看的地方）。
        /// </para>
        /// </summary>
        [SerializeField] private bool autoPlay = false;

        /// <summary>自动播放每句停留的秒数（小于 <see cref="MinAutoAdvanceSeconds"/> 时按该下限执行）。</summary>
        [SerializeField] private float autoAdvanceSeconds = 2f;

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前会话（<see cref="IGameplaySession"/> 视角，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空，表示会话所有权已交还；重开时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一局的具体类型：<c>NodeCount</c> / <c>CaptureState</c> / <c>TryRestoreState</c> 不在
        /// 契约里，只有具体类型能调。结算后<b>保留</b>（HUD 要继续显示结局卡与读数），重开时替换。
        /// </summary>
        private VisualNovelWorld? _world;

        /// <summary>
        /// 本局是否已结算。<b>为什么必须有它</b>：内核的 <c>CollectResult</c> 只允许调用一次
        /// （二次调用按契约抛 <see cref="InvalidOperationException"/>），而结算可能从三条路径同时逼近
        /// （<see cref="Update"/> 的结局分支、输入后的收口、<see cref="Restart"/> 前的补结算），
        /// 必须用标志兜住重入。
        /// </summary>
        private bool _collected;

        /// <summary>自动播放计时器：距上一次自动推进 Line 累计的秒数。</summary>
        private float _autoTimer;

        /// <summary>当前节点 id；无会话时为 <c>-1</c>（与内核"未开局"口径一致）。</summary>
        public int CurrentNodeId
        {
            get
            {
                VisualNovelWorld? world = _world;
                return world != null ? world.CurrentNodeId : -1;
            }
        }

        /// <summary>故事是否结束（含"结算已取回"）；无会话时为 false。</summary>
        public bool IsFinished
        {
            get
            {
                VisualNovelWorld? world = _world;
                return world != null && world.IsFinished;
            }
        }

        /// <summary>结局 id（= End 节点 id）；未结束或悬空收尾时为 <c>-1</c>；无会话时 <c>-1</c>。</summary>
        public int EndingId
        {
            get
            {
                VisualNovelWorld? world = _world;
                return world != null ? world.EndingId : -1;
            }
        }

        /// <summary>
        /// 剧本里的结局总数（End 节点个数，本演示固定 4）。
        /// <b>为什么它不随"已看到几个结局"变化</b>：它是剧本的静态属性，HUD 用它显示"共 N 个结局"；
        /// 已解锁列表属于局外进度（存档/成就），不在本场景的职责里。
        /// </summary>
        public int EndingCount
        {
            get { return DemoEndingCount; }
        }

        /// <summary>
        /// 重开一局：换一个全新的 <see cref="VisualNovelWorld"/>（等价于"从第一句再读一遍"）。
        /// <para>
        /// <b>为什么必须重建而不是"清空"</b>：内核在 <c>CollectResult</c> 之后把会话所有权交还，
        /// <c>TryAdvance</c>/<c>TryChoose</c>/<c>TryRestoreState</c> 全部冻结；只有重新构造才能真正
        /// 回到起始节点（标记位、发奖去重台账、访问计数都会跟着归零）。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是 MonoBehaviour），它的
        /// <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"已终局但还没结算"的那一帧，
        /// 直接重建会把这一局的 <c>GameFinishedEvent</c> 与战利品静默吞掉。终局就先走
        /// <see cref="Settle"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// <para>局中调用同样安全：旧局还没结算就直接丢弃（纯内存对象，没有原生资源）。</para>
        /// </summary>
        public void Restart()
        {
            if (!_collected && _world != null && _world.IsFinished)
            {
                Settle();
            }

            CreateWorld();
        }

        /// <summary>
        /// 推进一句（"继续"按钮 / 自动播放）。
        /// <para>
        /// <b>不在这里判"现在能不能推进"</b>：Choice 节点必须选选项、End 节点与已结束状态都不接受
        /// 推进语义，这些全由内核 <c>TryAdvance</c> 返回 false 表达；本类不做第二份校验，
        /// 也就不可能与内核判出不同结果。返回 false 时状态一点没动，画面保持原样。
        /// </para>
        /// </summary>
        public void Advance()
        {
            VisualNovelWorld? world = _world;
            if (world == null || _collected)
            {
                return;
            }

            if (!world.TryAdvance())
            {
                return;
            }

            AfterStateChanged();
        }

        /// <summary>
        /// 选择第 <paramref name="index"/> 个<b>可用</b>选项（序号与 HUD 按钮下标、
        /// <c>IVisualNovelInput.TryChoose</c> 完全同序）。
        /// <para>
        /// 越界、条件不满足（防御分支）、非 Choice 节点、故事已结束——一律由内核返回 false
        /// 且不改任何状态；本类只负责"没变就不结算、不重绘"。
        /// </para>
        /// </summary>
        /// <param name="index">可用选项序号（0 基）。</param>
        public void Choose(int index)
        {
            VisualNovelWorld? world = _world;
            if (world == null || _collected)
            {
                return;
            }

            if (!world.TryChoose(index))
            {
                return;
            }

            AfterStateChanged();
        }

        /// <summary>
        /// 导出当前进度快照（4 个 int：节点 / 标记位 / 访问计数 / 结局 id）。
        /// <para>
        /// <b>无会话时返回"未开局"快照</b>（节点 0、标记 0、访问 0、结局 -1），而不是抛异常：
        /// 存档调用方常在场景还没装配完成时先探一次，抛异常只会逼它写 try/catch。
        /// 这份快照<b>不能</b>区分"未开局"与"刚开局停在节点 0"——两者对读档方是同一件事。
        /// </para>
        /// <para>
        /// 快照只搬进度四元组：待发奖台账、发奖去重、累计时间、是否已结算都不迁移
        /// （语义边界见内核 <c>TryRestoreState</c> 注释）。
        /// </para>
        /// </summary>
        /// <returns>进度快照。</returns>
        public VisualNovelSaveState CaptureState()
        {
            VisualNovelWorld? world = _world;
            return world != null ? world.CaptureState() : new VisualNovelSaveState(0, 0, 0, -1);
        }

        /// <summary>
        /// 从快照恢复进度。返回 false 表示<b>一个字段都没改</b>：无会话、已结算、
        /// 或内核判定快照非法（节点不存在、访问计数为负、结局 id 与节点不自洽）。
        /// <para>
        /// <b>读档后同样要做"跳转节点自动跳过 + 渲染 + 结算"这一套收口</b>：快照可能正好落在
        /// Jump 节点（画面上没有内容可看），也可能落在 End 节点（等价于"到达结局"）。
        /// 后者会立刻结算一次；由于战利品台账不随快照迁移，<b>不会重复发放</b>读档点之前的物品。
        /// </para>
        /// </summary>
        /// <param name="state">要恢复的快照。</param>
        /// <returns>是否恢复成功。</returns>
        public bool Restore(in VisualNovelSaveState state)
        {
            VisualNovelWorld? world = _world;
            if (world == null || _collected)
            {
                return false;
            }

            if (!world.TryRestoreState(in state))
            {
                return false;
            }

            AfterStateChanged();
            return true;
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c>：HUD 按钮的订阅与首帧渲染要在
        /// 玩家可能点下去之前就绪；挂到 <c>Start</c> 会留一个"第一次点击没反应"的空窗。
        /// </summary>
        private void Awake()
        {
            var host = new AIFrameworkUnityHost();
            host.Initialize();
            _host = host;

            SubscribeHud();
            CreateWorld();
        }

        /// <summary>
        /// 每帧：先跳过 Jump 节点 → 到点自动推进一句 → 刷新 HUD → 结局则结算一次。
        /// <b>顺序不可换</b>：先推进再渲染，玩家看到的就是推进之后的真实节点；
        /// 先渲染再推进会让画面永远慢一步（结局也要多等一拍才显示）。
        /// </summary>
        private void Update()
        {
            VisualNovelWorld? world = _world;
            if (world == null || _collected)
            {
                return;
            }

            // Jump 节点没有内容可看：先跳过去（也顺带兜住"读档正好落在 Jump"的情况）。
            SkipJumps();

            if (!world.IsFinished)
            {
                TickAutoAdvance(world);
            }

            RenderHud();
            SettleIfFinished();
        }

        /// <summary>退订 HUD 并释放宿主（<c>Dispose</c> 会丢弃未结算的会话引用）。</summary>
        private void OnDestroy()
        {
            UnsubscribeHud();

            _session = null;
            _world = null;
            _collected = true;

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 建（或重建）一局：构造内核 → 跳过起始 Jump → 首帧渲染。
        /// <para>
        /// <b>为什么不经过 <c>host.Sessions</c></b>：宿主只按 <see cref="GameplayKernelType"/> 建会话，
        /// 而视觉小说没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
        /// 因此也不需要 <c>Release</c> 上一局——旧对象随字段替换被 GC。
        /// </para>
        /// </summary>
        private void CreateWorld()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            _session = null;
            _world = null;
            _collected = false;
            _autoTimer = 0f;

            var init = new GameplayInitData(DefaultLevelId, randomSeed, SinglePlayerUnitCount);
            var world = new VisualNovelWorld(in init, DemoScript);

            _session = world;
            _world = world;

            // 起始节点若是 Jump（剧本用跳转桩开场的情况），先落到真正的剧情节点再渲染；
            // 起手就是 End 的剧本（极端数据）同样要在这里就完成一次结算，不能等第一帧输入。
            SkipJumps();
            RenderHud();
            SettleIfFinished();

            host.Log.Info("[VisualNovel] 开局：场次=" + DefaultLevelId
                + " 种子=" + randomSeed
                + " 节点数=" + world.NodeCount
                + " 结局数=" + DemoEndingCount
                + " 自动播放=" + autoPlay);
        }

        /// <summary>
        /// 一次成功输入之后的统一收口：清自动播放计时器 → 跳过 Jump → 渲染 → 终局则结算。
        /// <b>为什么只有一个入口</b>：<see cref="Advance"/> / <see cref="Choose"/> / <see cref="Restore"/>
        /// 三条路径都必须走同一套收口，任何一条漏掉都会留下"停在 Jump 上"或"结局不结算"的暗坑。
        /// </summary>
        private void AfterStateChanged()
        {
            _autoTimer = 0f;
            SkipJumps();
            RenderHud();
            SettleIfFinished();
        }

        /// <summary>
        /// 自动播放：只对 Line 节点计时，到点推进一句。
        /// <para>
        /// <b>Choice 节点不计时也不推进</b>：选项是剧情决策，替玩家选等于篡改故事
        /// （见 <see cref="autoPlay"/> 的注释）。计时器在非 Line 节点上归零，
        /// 因此"选完选项后的第一句"总是完整停留 <see cref="autoAdvanceSeconds"/> 秒。
        /// </para>
        /// </summary>
        /// <param name="world">当前会话。</param>
        private void TickAutoAdvance(VisualNovelWorld world)
        {
            if (!autoPlay || world.CurrentKind != VisualNovelNodeKind.Line)
            {
                _autoTimer = 0f;
                return;
            }

            _autoTimer += Time.deltaTime;
            if (_autoTimer < EffectiveAutoAdvanceSeconds())
            {
                return;
            }

            _autoTimer = 0f;
            if (!world.TryAdvance())
            {
                return;
            }

            SkipJumps();
        }

        /// <summary>
        /// 跳过连续的 Jump 节点（Jump 节点本身没有内容可看，<c>TryAdvance</c> 是它唯一的消费方式）。
        /// <para>
        /// <b>为什么要封顶</b>：剧本是外部数据，若若干个 Jump 首尾相接成环，无限循环会把整个编辑器
        /// 卡死在 <c>Update</c> 里。上界取 <c>NodeCount</c>——任何<b>无环</b>跳跃链都不可能比节点数更长。
        /// 真的撞上上界时本类只记一条警告并停在原地（不伪造状态、不强行结束），
        /// 让人从日志里看到"剧本有 Jump 环"这个事实。
        /// </para>
        /// </summary>
        private void SkipJumps()
        {
            VisualNovelWorld? world = _world;
            AIFrameworkUnityHost? host = _host;
            if (world == null)
            {
                return;
            }

            int budget = world.NodeCount;
            while (!world.IsFinished && world.CurrentKind == VisualNovelNodeKind.Jump)
            {
                if (budget-- <= 0)
                {
                    if (host != null)
                    {
                        host.Log.Warn("[VisualNovel] Jump 链超过节点数上限（疑似剧本成环），已停在节点 "
                            + world.CurrentNodeId + "。");
                    }

                    return;
                }

                if (!world.TryAdvance())
                {
                    return;
                }
            }
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            VisualNovelHud? view = hud;
            VisualNovelWorld? world = _world;
            if (view != null && world != null)
            {
                view.Render(world, DemoEndingCount);
            }
        }

        /// <summary>终局才结算（未终局直接返回，避免在热路径上多判一次 <c>CollectResult</c> 的前置条件）。</summary>
        private void SettleIfFinished()
        {
            VisualNovelWorld? world = _world;
            if (world == null || _collected || !world.IsFinished)
            {
                return;
            }

            Settle();
        }

        /// <summary>
        /// 结算：<c>CollectResult</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示结局。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>为什么广播用固定的 <see cref="DefaultLevelId"/></b>：它是这次"场次"的身份
        /// （与 Tetris / BoardGame 的 <c>DefaultLevelId</c> 同义）；文字冒险没有多关卡，场次恒为 1，
        /// 不把"内核回报的什么数"冒充成玩家选过的关卡。
        /// </para>
        /// <para>
        /// <b>为什么逐条发 <see cref="GrantItemCommand"/></b>：战利品清单是内核结算报告里的事实，
        /// 本类只负责翻译成发奖指令；<see cref="MetaReasonTags.GameLoot"/> 是因果日志的追溯标签。
        /// 悬空 Line/Jump 收尾（<c>EndingId = -1</c>）按内核口径报非胜利，但仍然照发已进入节点的奖励。
        /// </para>
        /// </summary>
        private void Settle()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            VisualNovelWorld? world = _world;
            if (host == null || session == null || world == null || _collected)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;

            GameplayResult result;
            try
            {
                result = session.CollectResult();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[VisualNovel] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, DefaultLevelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            VisualNovelHud? view = hud;
            if (view != null)
            {
                view.ShowEnding(world.EndingId, result.IsVictory);
            }

            host.Log.Info("[VisualNovel] 结算：场次=" + DefaultLevelId
                + " 结局=" + world.EndingId
                + " 胜利=" + result.IsVictory
                + " 进入节点=" + world.NodesVisited
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>
        /// 把 HUD 的"继续/重开"与"3 个选项"接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。
        /// </summary>
        private void SubscribeHud()
        {
            VisualNovelHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnAdvanceClicked += HandleAdvanceClicked;
            view.OnChoiceClicked += HandleChoiceClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            VisualNovelHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnAdvanceClicked -= HandleAdvanceClicked;
            view.OnChoiceClicked -= HandleChoiceClicked;
        }

        /// <summary>
        /// 点"继续 / 重新开始"：<b>同一个按钮，两种语义</b>——已结算则重开，否则推进一句。
        /// 结局后不换绑监听器，是为了让"重开"这条路径永远只有一处实现。
        /// </summary>
        private void HandleAdvanceClicked()
        {
            if (_collected || (_world != null && _world.IsFinished))
            {
                Restart();
                return;
            }

            Advance();
        }

        /// <summary>点第 <paramref name="index"/> 个可用选项（序号已由 HUD 对齐到内核口径）。</summary>
        /// <param name="index">可用选项序号。</param>
        private void HandleChoiceClicked(int index)
        {
            Choose(index);
        }

        /// <summary>当前自动播放间隔（已按 <see cref="MinAutoAdvanceSeconds"/> 夹取）。</summary>
        /// <returns>正的秒数。</returns>
        private float EffectiveAutoAdvanceSeconds()
        {
            float seconds = autoAdvanceSeconds;
            if (seconds < MinAutoAdvanceSeconds)
            {
                return MinAutoAdvanceSeconds;
            }

            return seconds;
        }

        /// <summary>数一段剧本里有几个 End 节点（= 结局总数）。</summary>
        /// <param name="script">剧本节点。</param>
        /// <returns>End 节点个数。</returns>
        private static int CountEndings(VisualNovelNode[] script)
        {
            int count = 0;
            for (int i = 0; i < script.Length; i++)
            {
                if (script[i].Kind == VisualNovelNodeKind.End)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// <b>演示内容（写死）</b>：雨夜旧书店的委托，16 个节点、节点 id 连续 0..15、4 个结局。
        /// <para>
        /// <b>节点图</b>（<c>A → B</c> 表示推进到 B，<c>[选项] → B</c> 表示选择后跳到 B）：
        /// <code>
        /// 0 旁白 ─→ 1 店主 ─→ 2 抉择（开局三选一）
        ///                       ├─[接受委托]─→ 3 店主 ─→ 4 店主(发奖 1001×1) ─→ 5 Jump ─┐
        ///                       ├─[讨价还价]─→ 6 店主 ─→ 7 店主 ──────────────→ 8 Jump ─┤
        ///                       └─[离开书店]─→ 9 旁白 ─→ 10 旁白 ─→ 11 End「雨夜离别」│
        ///                                                                            ↓
        /// 12 抉择（汇合点，来自 5 / 8）
        ///   ├─[推开通往里屋的门]─────────────────→ 13 End「里屋的门」
        ///   ├─[用钥匙打开柜台下的抽屉 (需 1001)]─→ 14 End「抽屉里的字条」
        ///   └─[收好钥匙，先回家]─────────────────→ 15 End「带钥匙回家」
        /// </code>
        /// </para>
        /// <para>
        /// <b>两个结局条件</b>：
        /// <list type="number">
        /// <item>结局 11「雨夜离别」只在开局选"离开书店"时可达——它是唯一不经过汇合点的收尾，
        /// 用来证明"最短路径也能形成显式结局"。</item>
        /// <item>结局 14「抽屉里的字条」是<b>条件结局</b>：选项 <c>requiredItemId = 1001</c>，
        /// 内核按 <c>1 &lt;&lt; (1001 &amp; 31)</c> 查标记位，而该位只在节点 4（发奖节点）被置起。
        /// 因此"走讨价还价分支到汇合点"时只有 2 个选项、拿不到这个结局；
        /// 只有"接受委托 → 拿到黄铜钥匙 → 汇合"才会出现第 3 个选项。</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>为什么剧本写在这里而不是资源文件</b>：这是<b>演示内容</b>，与内核用例里的构造式剧本同性质；
        /// 正式项目应由关卡配置/表格导出后注入构造函数，内核不加载、不反序列化。
        /// </para>
        /// </summary>
        /// <returns>长度 16、节点 id 连续 0..15 的剧本数组。</returns>
        private static VisualNovelNode[] BuildDemoScript()
        {
            return new VisualNovelNode[]
            {
                // ── 开场：旁白 → 角色 → 三选一 ───────────────────────────────────────────
                Line(
                    0,
                    "旁白",
                    "雨夜的旧书店没有客人。门楣上的铜铃响了一声，又安静下去。",
                    1),
                Line(
                    1,
                    "店主",
                    "「你终于来了。柜台上那封委托，只有你能拆。」",
                    2),
                Choice(
                    2,
                    "你",
                    "委托就压在台灯底下，信封上没写名字。你要怎么回应？",
                    new VisualNovelChoice("接受委托", 3),
                    new VisualNovelChoice("讨价还价", 6),
                    new VisualNovelChoice("离开书店", 9)),

                // ── 分支 A：接受 → 发奖（1001 黄铜钥匙）→ Jump 汇合 ─────────────────────
                Line(
                    3,
                    "店主",
                    "「痛快。书页里夹着一枚铜钥匙，别弄丢——弄丢了，门就再也开不了。」",
                    4),
                Grant(
                    4,
                    "店主",
                    "「这枚黄铜钥匙归你了。它开的不是店门，是里屋那扇不该开的门。」",
                    5,
                    DemoKeyItemId,
                    1),
                Jump(5, 12),

                // ── 分支 B：讨价还价 → 两句 → Jump 汇合（不给钥匙）──────────────────────
                Line(
                    6,
                    "店主",
                    "「价钱可以谈。但书里的东西不等人——先看货，还是先谈钱？」",
                    7),
                Line(
                    7,
                    "店主",
                    "「我劝你现在就走。留下来的话，接下来的事你得自己担着。」",
                    8),
                Jump(8, 12),

                // ── 分支 C：离开 → 两句旁白 → 结局 11「雨夜离别」────────────────────────
                Line(
                    9,
                    "旁白",
                    "你把委托推回台灯底下，推门走进雨里。",
                    10),
                Line(
                    10,
                    "旁白",
                    "身后的书店熄了灯，铜铃在风里响了两声，像一声没说完的挽留。",
                    11),
                End(
                    11,
                    "旁白",
                    "故事在一场雨里结束：你带走了伞，却没带走任何答案。"),

                // ── 汇合点：无条件 2 选 1 + 需要钥匙的条件选项 ───────────────────────────
                Choice(
                    12,
                    "店主",
                    "「钥匙在你手里（或者不在）。接下来，你自己选。」",
                    new VisualNovelChoice("推开通往里屋的门", 13),
                    new VisualNovelChoice("用钥匙打开柜台下的抽屉", 14, DemoKeyItemId),
                    new VisualNovelChoice("收好钥匙，先回家", 15)),

                // ── 三个结局节点（13 / 14 / 15）──────────────────────────────────────────
                End(
                    13,
                    "旁白",
                    "门后是一整面没有书的书架，每一格都摆着同一封没署名的委托。"),
                End(
                    14,
                    "旁白",
                    "抽屉里只有一张字条：「你已经比他们都走得远了。」"),
                End(
                    15,
                    "旁白",
                    "你把钥匙收进怀里，雨停了。书店的灯在你身后慢慢熄灭。")
            };
        }

        /// <summary>造一个 Line 节点（无发奖、无选项）。</summary>
        /// <param name="nodeId">节点 id。</param>
        /// <param name="speaker">说话人。</param>
        /// <param name="text">台词/旁白。</param>
        /// <param name="nextNodeId">下一节点 id。</param>
        /// <returns>Line 节点。</returns>
        private static VisualNovelNode Line(int nodeId, string speaker, string text, int nextNodeId)
        {
            return new VisualNovelNode(
                nodeId,
                VisualNovelNodeKind.Line,
                speaker,
                text,
                nextNodeId,
                null,
                0,
                0);
        }

        /// <summary>造一个带发奖的 Line 节点（进入即置 flag 并记账，同一节点只发一次）。</summary>
        /// <param name="nodeId">节点 id。</param>
        /// <param name="speaker">说话人。</param>
        /// <param name="text">台词/旁白。</param>
        /// <param name="nextNodeId">下一节点 id。</param>
        /// <param name="grantItemId">发放的道具 id。</param>
        /// <param name="grantCount">发放数量（必须为正）。</param>
        /// <returns>Line 节点。</returns>
        private static VisualNovelNode Grant(
            int nodeId,
            string speaker,
            string text,
            int nextNodeId,
            int grantItemId,
            int grantCount)
        {
            return new VisualNovelNode(
                nodeId,
                VisualNovelNodeKind.Line,
                speaker,
                text,
                nextNodeId,
                null,
                grantItemId,
                grantCount);
        }

        /// <summary>造一个 Choice 节点（<paramref name="choices"/> 不能为空，目标必须存在）。</summary>
        /// <param name="nodeId">节点 id。</param>
        /// <param name="speaker">说话人。</param>
        /// <param name="text">问题文本。</param>
        /// <param name="choices">选项清单。</param>
        /// <returns>Choice 节点。</returns>
        private static VisualNovelNode Choice(
            int nodeId,
            string speaker,
            string text,
            params VisualNovelChoice[] choices)
        {
            return new VisualNovelNode(
                nodeId,
                VisualNovelNodeKind.Choice,
                speaker,
                text,
                -1,
                choices,
                0,
                0);
        }

        /// <summary>造一个 Jump 节点（不显示内容，表现层自动 <c>TryAdvance</c> 跳过）。</summary>
        /// <param name="nodeId">节点 id。</param>
        /// <param name="nextNodeId">跳转目标节点 id。</param>
        /// <returns>Jump 节点。</returns>
        private static VisualNovelNode Jump(int nodeId, int nextNodeId)
        {
            return new VisualNovelNode(
                nodeId,
                VisualNovelNodeKind.Jump,
                string.Empty,
                string.Empty,
                nextNodeId,
                null,
                0,
                0);
        }

        /// <summary>造一个 End 节点（id 同时是结局 id；<c>NextNodeId</c> 用 -1）。</summary>
        /// <param name="nodeId">节点 id。</param>
        /// <param name="speaker">说话人。</param>
        /// <param name="text">结局面文本。</param>
        /// <returns>End 节点。</returns>
        private static VisualNovelNode End(int nodeId, string speaker, string text)
        {
            return new VisualNovelNode(
                nodeId,
                VisualNovelNodeKind.End,
                speaker,
                text,
                -1,
                null,
                0,
                0);
        }
    }
}
