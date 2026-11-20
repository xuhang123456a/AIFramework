#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.Monopoly;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 大富翁场景控制器：建宿主 → 直接构造 <see cref="MonopolyWorld"/> → 按节拍调一次
    /// <c>IMonopolyInput.TryRoll</c> → 只读视图每帧推给 <see cref="MonopolyHud"/> → 终局结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new MonopolyWorld</c>，而其它玩法控制器要走 <c>host.Sessions.Begin</c></b>：
    /// 适配层是组合根（<c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 没有 Monopoly 值、<c>UnityKernelFactory</c> 也没有对应分支
    /// （与 <c>BoardGameController</c> 完全同因：为一个纯演示场景去扩枚举与工厂，会让存档协议与契约
    /// 为一套"对主板完全同构"的卡带付长期成本）。因此这里直接构造并<b>自己持有这次会话</b>
    /// （不登记进 <c>host.Sessions</c>），结算时直接调 <see cref="IGameplaySession.CollectResult"/>。
    /// 宿主仍然负责总线、日志与背包——本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何大富翁规则</b>：掷骰、移动、买地、租金、升级、税、奖励、机会卡、监狱传送、
    /// 破产淘汰、40 回合封顶与胜负全部由内核决定；本类只做四件事：装配宿主、把帧时间翻译成"该掷骰了"、
    /// 把只读视图推给 HUD、终局结算并广播事实。适配层一旦自己算一遍"经过起点发 200"之类的规则
    /// （哪怕是"看起来等价"的复算），改内核而不改 UI 时两侧就会静默分叉。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次计时器只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>同步结算</b>：<c>TryRoll()</c> 返回时本回合已经全部结算完（契约明确），所以本类不需要
    /// "先播动画再落账"的两段式状态机，也不存在"掷了骰但还没走"的中间态；画面永远是账本的真实投影。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MonopolyController : MonoBehaviour
    {
        /// <summary>
        /// 场次 ID：<b>不暴露成 <c>[SerializeField]</c></b>。大富翁只有一张固定 20 格棋盘，
        /// "选关"在本玩法里是伪概念；把它做成可配字段只会让人以为换个数就换了棋盘
        /// （内核的 <c>GameplayInitData.LevelId</c> 对本卡带确实不参与任何运算）。
        /// </summary>
        private const int DefaultLevelId = 1;

        /// <summary>
        /// 自动演示的节拍（秒）：<b>每 0.6 秒最多掷一次</b>。再快看不清现金与等级的变化，
        /// 再慢演示一局（最坏 80 手）要等一分多钟。
        /// </summary>
        private const float DefaultAutoStepSeconds = 0.6f;

        /// <summary>
        /// 节拍下限（秒）：防止把 <see cref="autoStepSeconds"/> 配成 0 或负数时"每帧掷一次"，
        /// 那样一帧内会跑完整局、HUD 只剩终局画面。夹取只影响表现节奏，不影响任何规则。
        /// </summary>
        private const float MinAutoStepSeconds = 0.05f;

        /// <summary>大富翁皮肤（渲染状态行、4×5 字符盘与 20 个格子按钮，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private MonopolyHud hud = null!;

        /// <summary>
        /// 随机种子：固定值让"同种子同开局"可复现（内核所有随机——骰子与机会卡——
        /// 全走这条种子驱动的 xorshift32，不碰 <c>UnityEngine.Random</c>）。
        /// </summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>
        /// 参战玩家数：内核按 1..4 夹取（≤ 0 → 默认 2、&gt; 4 → 4），本类不再复制一遍夹取规则，
        /// 开局日志与展示一律读内核回报的 <c>MonopolyWorld.PlayerCount</c>。玩家 0 = 本地玩家。
        /// </summary>
        [SerializeField] private int playerCount = 2;

        /// <summary>
        /// 是否自动演示：为 true 时按 <see cref="autoStepSeconds"/> 自动掷骰；
        /// 为 false 时只由 <see cref="Roll"/>（掷骰按钮 / 任意格子按钮）驱动。
        /// <para>
        /// <b>为什么关掉自动演示后不"替玩家掷"</b>：这一侧是玩家的操作面，控制器替他掷骰会让人
        /// 分不清"我点的"和"它自己掷的"。没有 AI 对手是本卡带的已知边界（内核不做内置 AI，
        /// 每个存活玩家都由外部按 <c>TryRoll</c> 驱动）。
        /// </para>
        /// </summary>
        [SerializeField] private bool autoPlay = true;

        /// <summary>自动演示的间隔秒数（&lt; <see cref="MinAutoStepSeconds"/> 时按该下限执行）。</summary>
        [SerializeField] private float autoStepSeconds = DefaultAutoStepSeconds;

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前对局会话（<see cref="IGameplaySession"/> 视角，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空，表示会话所有权已交还；重开时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一局的具体类型：<c>TurnIndex</c> 等读数在契约里也有，但"构造 + TryRoll"必须落在具体类型上。
        /// 结算后<b>保留</b>（HUD 要继续显示最终盘面与胜者），重开时替换。
        /// </summary>
        private MonopolyWorld? _world;

        /// <summary>
        /// 本局是否已结算。<b>为什么必须有它</b>：内核的 <c>CollectResult</c> 只允许调用一次
        /// （二次调用按契约抛 <see cref="InvalidOperationException"/>），而结算可能从三条路径同时逼近
        /// （Update 的终局分支、Roll 后的收口、Restart 前的补结算），必须用标志兜住重入。
        /// </summary>
        private bool _collected;

        /// <summary>距上一次掷骰（自动或手动）累计的秒数。</summary>
        private float _stepTimer;

        /// <summary>已执行的掷骰回合数（= 内核 <c>TurnIndex</c> / 结算报告的 <c>StepCount</c>）；无会话时为 0。</summary>
        public int TurnIndex
        {
            get
            {
                MonopolyWorld? world = _world;
                return world != null ? world.TurnIndex : 0;
            }
        }

        /// <summary>
        /// 名义回合数（1 基，= 内核 <c>TurnIndex / PlayerCount + 1</c>）；无会话时为 0。
        /// <b>注意</b>：封顶瞬间它是 <c>41</c>（见 <see cref="MonopolyHud"/> 里"封顶瞬间是 41"的口径），
        /// HUD 显示时会裁到 40，这里的属性如实转发内核读数，不做裁剪。
        /// </summary>
        public int RoundNumber
        {
            get
            {
                MonopolyWorld? world = _world;
                return world != null ? world.RoundNumber : 0;
            }
        }

        /// <summary>对局是否结束（含"结算已取回"）；无会话时为 false。</summary>
        public bool IsFinished
        {
            get
            {
                MonopolyWorld? world = _world;
                return world != null && world.IsFinished;
            }
        }

        /// <summary>胜者玩家索引（0 基）；未结束或无会话时为 <c>-1</c>（与契约同值）。</summary>
        public int WinnerPlayer
        {
            get
            {
                MonopolyWorld? world = _world;
                return world != null ? world.WinnerPlayer : -1;
            }
        }

        /// <summary>
        /// 重开一局：换一个全新的 <see cref="MonopolyWorld"/>（等价于"再来一次"）。
        /// <para>
        /// <b>为什么必须重建而不是"清空"</b>：内核在 <c>CollectResult</c> 之后把会话所有权交还，
        /// <c>TryRoll</c> 全部冻结；只有重新构造才能真正回到初始局面（同种子重建的随机流也会拨回进门种子）。
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
        /// 掷一次骰（转发内核 <c>IMonopolyInput.TryRoll</c>）。
        /// <para>
        /// <b>返回 false 的两种情况</b>：① 本局已结束/已结算（内核同样返回 false 且不抛异常）；
        /// ② 控制器还没有会话（宿主没装配成功）。其余情况每次调用都必然完成一个完整回合
        /// ——不存在"同一玩家连掷两次"，掷完立刻换手。
        /// </para>
        /// <para>
        /// <b>为什么这里不判"该谁掷"</b>：破产玩家的回合由内核自动跳过，调用方不需要自己判存活；
        /// 适配层再判一遍就等于把回合规则写了两份。
        /// </para>
        /// </summary>
        /// <returns>本次掷骰是否被接受。</returns>
        public bool Roll()
        {
            MonopolyWorld? world = _world;
            if (world == null || _collected)
            {
                return false;
            }

            // 具体类型的 TryRoll() 就是 IMonopolyInput.TryRoll() 的实现（同一个方法），
            // 不必再存一份接口引用，也不会因为接口/实现分叉而漏调。
            if (!world.TryRoll())
            {
                return false;
            }

            _stepTimer = 0f;

            // 顺序不可换：先渲染这一步之后的真实状态，再判收口（终局画面要显示最终盘面）。
            RenderHud();
            SettleIfFinished();
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
        /// 每帧：终局只结算一次并保持最终画面；否则累计节拍、到点掷一次骰；每帧渲染一次 HUD。
        /// <b>顺序不可换</b>：先推进再渲染，玩家看到的就是这一步之后的真实状态；
        /// 先渲染再推进会让画面永远慢一步（终局也要多等一拍才显示结果）。
        /// </summary>
        private void Update()
        {
            MonopolyWorld? world = _world;
            if (world == null)
            {
                return;
            }

            if (_collected || world.IsFinished)
            {
                // 终局分支：SettleIfFinished 幂等（IsFinished 只结算一次由 _collected 兜住），
                // RenderHud 让"最后一拍之后的画面"稳定停在屏幕上。
                SettleIfFinished();
                RenderHud();
                return;
            }

            if (autoPlay)
            {
                _stepTimer += Time.deltaTime;
                if (_stepTimer >= EffectiveAutoStepSeconds())
                {
                    // 只扣一次节拍（不补跑）：编辑器编译/切窗口产生的长帧不该让棋盘"连跳好几步"，
                    // 那会让玩家以为自己漏看了一整段对局。Roll 内部已渲染并结算。
                    _stepTimer = 0f;
                    Roll();
                    return;
                }
            }

            RenderHud();
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
        /// 建（或重建）一局：构造内核 → 首帧渲染。
        /// <para>
        /// <b>为什么不经过 <c>host.Sessions</c></b>：宿主只按 <see cref="GameplayKernelType"/> 建会话，
        /// Monopoly 没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
        /// 因此也不需要 <c>Release</c> 上一局——旧对象随字段替换被 GC。
        /// </para>
        /// <para>
        /// <b>为什么开局日志读 <c>world.PlayerCount</c> 而不是字段</b>：玩家数由内核按 1..4 夹取
        /// （≤ 0 → 2、&gt; 4 → 4），把字段原样打进日志会让"配了 9 看到 9"误导排查。
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
            _stepTimer = 0f;

            var init = new GameplayInitData(DefaultLevelId, randomSeed, playerCount);
            var world = new MonopolyWorld(in init);

            _session = world;
            _world = world;

            RenderHud();

            host.Log.Info("[Monopoly] 开局：场次=" + DefaultLevelId
                + " 种子=" + randomSeed
                + " 玩家数=" + world.PlayerCount
                + " 自动演示=" + autoPlay
                + " 节拍=" + EffectiveAutoStepSeconds() + "s");
        }

        /// <summary>
        /// 把 HUD 的"掷骰 / 重新开始 / 20 个格子按钮"接到本类
        /// （<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。
        /// </summary>
        private void SubscribeHud()
        {
            MonopolyHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRollClicked += HandleRollClicked;
            view.OnRestartClicked += HandleRestartClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            MonopolyHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRollClicked -= HandleRollClicked;
            view.OnRestartClicked -= HandleRestartClicked;
        }

        /// <summary>点"掷骰子"或任意格子按钮：都走同一条掷骰路径（未被接受时静默忽略）。</summary>
        private void HandleRollClicked()
        {
            Roll();
        }

        /// <summary>点"重新开始"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>渲染一次：把只读视图与"要高亮的玩家 0 站位"推给 HUD。</summary>
        private void RenderHud()
        {
            MonopolyHud? view = hud;
            MonopolyWorld? world = _world;
            if (view != null && world != null)
            {
                view.Render(world, SelectedTile(world));
            }
        }

        /// <summary>
        /// 要高亮的格子 = 玩家 0（本地玩家）的站位。
        /// <para>
        /// 契约保证 <c>Position</c> 恒在 0..19（破产后停在最后一次落点、不再移动），
        /// 所以这里不需要额外判界；<c>PlayerCount</c> 最小是 1（≤ 0 会夹成 2），
        /// <c>GetPlayer(0)</c> 必然存在，不会抛越界异常。
        /// </para>
        /// </summary>
        /// <param name="world">当前对局。</param>
        /// <returns>要高亮的格索引。</returns>
        private static int SelectedTile(MonopolyWorld world)
        {
            return world.GetPlayer(0).Position;
        }

        /// <summary>终局才结算（未终局直接返回，避免在热路径上多判一次 <c>CollectResult</c> 的前置条件）。</summary>
        private void SettleIfFinished()
        {
            MonopolyWorld? world = _world;
            if (world == null || _collected || !world.IsFinished)
            {
                return;
            }

            Settle();
        }

        /// <summary>
        /// 结算：<c>CollectResult</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示结果。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>战利品为什么逐条发 <see cref="GrantItemCommand"/></b>：内核出门在
        /// <see cref="GameplayResult.Loots"/> 里给的是<b>清单</b>（胜/负决定条数），
        /// 主板只按清单派发、不解释业务含义——这里不把"胜利发 1003×1"的规则抄进适配层。
        /// </para>
        /// <para>
        /// <b>为什么广播用固定的 <see cref="DefaultLevelId"/></b>：它是这次"场次"的身份
        /// （与 <c>BoardGameController</c> 的 <c>DefaultLevelId</c> 同义）；大富翁没有多关卡，
        /// 场次恒为 1，不把"内核回报的什么数"冒充成玩家选过的关卡。
        /// </para>
        /// </summary>
        private void Settle()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            MonopolyWorld? world = _world;
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
                host.Log.Error("[Monopoly] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, DefaultLevelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            MonopolyHud? view = hud;
            if (view != null)
            {
                view.ShowResult(result.IsVictory, world.WinnerPlayer);
            }

            host.Log.Info("[Monopoly] 结算：场次=" + DefaultLevelId
                + " 胜负=" + result.IsVictory
                + " 胜者=" + world.WinnerPlayer
                + " 回合=" + world.TurnIndex
                + " P1现金=" + world.GetPlayer(0).Cash
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>
        /// 实际生效的自动节拍：<see cref="autoStepSeconds"/> 夹到
        /// [<see cref="MinAutoStepSeconds"/>, ∞)。
        /// <para>
        /// <b>为什么这里自己夹一次</b>：字段来自 Inspector，一个 0 或负数就能让 Update 每帧掷一次骰，
        /// 一帧之内跑完整局——真机上表现为"进场景直接看到结算画面"。夹取只影响表现节奏，不改规则。
        /// </para>
        /// </summary>
        /// <returns>正数节拍（秒）。</returns>
        private float EffectiveAutoStepSeconds()
        {
            float seconds = autoStepSeconds;
            return seconds < MinAutoStepSeconds ? MinAutoStepSeconds : seconds;
        }
    }
}
