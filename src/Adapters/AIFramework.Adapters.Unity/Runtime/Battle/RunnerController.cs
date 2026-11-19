#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.Runner;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 跑酷场景控制器：建宿主 → 直接构造 <see cref="RunnerWorld"/> → 每帧把真实时间喂给
    /// <see cref="IRunnerInput.Tick"/> → 只读视图推给 HUD → 撞上障碍后结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new RunnerWorld</c>，而战棋/三消控制器要走 <c>host.Sessions</c></b>：
    /// 适配层是组合根（<c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 没有 Runner 值（跑酷对主板而言只是"又一个回合制会话"，
    /// 结算仍按 <see cref="GameplayKernelType.Turn"/>）。为一个纯演示场景去扩枚举与工厂，会让存档协议
    /// 与契约为一套"对主板完全同构"的卡带付长期成本；因此这里直接构造并<b>自己持有这次会话</b>
    /// （不登记进 <c>host.Sessions</c>），结算时直接调 <see cref="IGameplaySession.CollectResult"/>。
    /// 宿主仍然负责总线、日志与背包——本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何跑酷规则</b>：速度曲线、分数、生成节奏、碰撞窗、清障高度全部由内核决定；
    /// 适配层一旦自己算一遍（哪怕是"看起来等价"的复算），改内核而不改 UI 时两侧就会静默分叉。
    /// 本类只做四件事：装配宿主、把帧时间翻译成 <see cref="IRunnerInput.Tick"/>、把只读视图推给 HUD、
    /// 撞上后结算并广播事实。连"最近障碍是哪个"这种判断也只在 <see cref="autoPlay"/> 的测试替身里出现。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次 <c>Tick</c> 只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>场次 ID 为什么是常量而不是 <c>[SerializeField]</c></b>：需求把本类的公开面锁成四个属性 +
    /// 四个方法，Inspector 面只留 <c>hud / randomSeed / autoPlay</c> 三个字段；跑酷只有一张"地图"，
    /// 场次身份固定为 1（与 Console 侧 <c>CartridgeShowcase</c> 的口径一致）。它同时进
    /// <see cref="GameplayInitData"/> 与 <c>PublishGameFinished</c>，两处必须是同一个值。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RunnerController : MonoBehaviour
    {
        /// <summary>
        /// 场次/关卡 ID。<b>为什么是 1</b>：跑酷内核不读 <see cref="GameplayInitData.LevelId"/>
        /// （规则全是常量），这个值纯粹是"这一局叫什么"的身份标签，用于结算广播；固定 1 与
        /// Console 侧演示保持一致。
        /// </summary>
        private const int DefaultLevelId = 1;

        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：跑酷是单人玩法，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对。
        /// </summary>
        private const int SinglePlayerUnitCount = 1;

        /// <summary>
        /// 单帧喂给内核的上限（秒）。<b>为什么是 0.05</b>：内核的碰撞是"逐帧点采样"——
        /// 位移超过 1.2 米（碰撞窗宽度）就可能整帧穿过障碍而不判定。0.05 s × 12 m/s = 0.6 米，
        /// 半步宽，编辑器编译/切窗口产生的长帧不会让玩家"穿墙"。夹取造成的时间轴略微落后于墙钟
        /// 在演示里不可见，但"必躲的跳跃忽然撞死"是肉眼可见的事故。
        /// </summary>
        private const double MaxFrameDeltaSeconds = 0.05;

        /// <summary>
        /// 下蹲时长（秒）：<see cref="IRunnerInput.TrySlide"/> 的实参，按钮与自动演示共用一份。
        /// 内核允许 0.2~3.0 秒，0.5 秒足够覆盖一个 High 障碍的通过时间（12 m/s 下只需 0.1 秒）。
        /// </summary>
        private const double SlideSeconds = 0.5;

        /// <summary>
        /// 自动演示的"视野"（米）：障碍进入这个距离内才开始按种类决策。<b>它只是上界，不是触发点</b>
        /// ——真正的触发距离由 <see cref="JumpLeadSeconds"/> / <see cref="SlideLeadSeconds"/> 乘当前速度算，
        /// 原因见这两个常量的注释（"进 6 米就跳"在 8 m/s 下必撞）。
        /// </summary>
        private const float DecisionHorizon = 6f;

        /// <summary>
        /// 碰撞窗半宽（米）：与内核的 <c>|x| ≤ 0.6</c> 同一口径，这里只用来判断"这个障碍是否已经被甩到身后"。
        /// 它不参与任何命中判定（命中只在内核里发生）。
        /// </summary>
        private const float CollisionHalfWidth = 0.6f;

        /// <summary>
        /// Low（地面）障碍的起跳提前量（秒）＝ 跳跃顶点时刻＝ <c>6.5 / 20 = 0.325</c>。
        /// <para>
        /// <b>算术必须写下来，否则调数值时一定踩</b>：起跳垂直初速 6.5 m/s、重力 20 m/s² →
        /// 顶点 <c>6.5² / 40 = 1.056 m</c> 出现在 0.325 s，落地在 0.65 s；清障线 0.6 m 对应的
        /// <b>安全滞空窗</b>是解 <c>6.5t − 10t² = 0.6</c> 得到的 <c>t ∈ [0.111, 0.539]</c>（宽 0.428 s）。
        /// 也就是说"必须在起跳后 0.111~0.539 s 之间穿过 <c>|x| ≤ 0.6</c>"。
        /// 8 m/s 时从 x=6 m 起跳要 0.675 s 才到 <c>x = 0.6</c>，那时玩家已经落地 0.025 s ——
        /// 一帧不差地撞死在第一个 Low 上。所以提前距离必须由顶点时刻反推：
        /// <c>起跳距离 = 速度 × 0.325</c>（8 m/s → 2.6 m，12 m/s → 3.9 m），
        /// 这样障碍进窗的时刻正好落在顶点两侧，窗头窗尾各留 <c>0.6 / speed</c> 秒余量。
        /// </para>
        /// </summary>
        private const float JumpLeadSeconds = 0.325f;

        /// <summary>
        /// High（空中）障碍的下蹲提前量（秒）＝ 下蹲时长的一半（0.5 / 2 = 0.25）。
        /// <para>
        /// 下蹲从触发瞬间开始计时 0.5 秒，把碰撞窗放在下蹲时段的<b>正中间</b>最稳：
        /// 触发距离 <c>= 速度 × 0.25</c>（8 m/s → 2.0 m，12 m/s → 3.0 m）时，
        /// 进窗时刻是 <c>0.25 − 0.6/speed</c>、出窗是 <c>0.25 + 0.6/speed</c>，
        /// 两端都落在 <c>(0, 0.5)</c> 内且各留 ≈0.19 秒余量，50 ms 的卡帧也翻不了车。
        /// </para>
        /// </summary>
        private const float SlideLeadSeconds = 0.25f;

        /// <summary>跑酷皮肤（渲染状态行与 30 列字符跑道，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private RunnerHud hud = null!;

        /// <summary>
        /// 随机种子：固定值让"第几个障碍是 Low、第几个是 High"可复现——
        /// 自动演示能稳定跑出同一条障碍序列，改一个数字就能换一段节奏。
        /// </summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>
        /// 是否自动演示（默认开：场景一进来就能看到角色跳过 Low、蹲过 High）。
        /// 关掉后玩家用 HUD 的"跳跃/下蹲"按钮手动操作，判定链路完全一样。
        /// </summary>
        [SerializeField] private bool autoPlay = true;

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前对局会话（<see cref="IGameplaySession"/> 视角，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空，表示会话所有权已交还；重开时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一局的具体类型：<c>Speed</c> / <c>BestScore</c> / <c>TryJump</c> / <c>TrySlide</c>
        /// 都不在 <see cref="IRunnerReadView"/> 里，只有具体类型能读。结算后<b>保留</b>
        /// （HUD 要继续显示最后一段跑道），重开时替换。
        /// </summary>
        private RunnerWorld? _world;

        /// <summary>本局是否已结算（<c>CollectResult</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>已跑距离（米；无会话时为 0）。</summary>
        public float Distance
        {
            get
            {
                RunnerWorld? world = _world;
                return world != null ? world.Distance : 0f;
            }
        }

        /// <summary>当前分数（<c>floor(Distance) * 10</c>；无会话时为 0）。</summary>
        public int Score
        {
            get
            {
                RunnerWorld? world = _world;
                return world != null ? world.Score : 0;
            }
        }

        /// <summary>是否已撞上障碍（结算后仍为 true；无会话时为 false）。</summary>
        public bool IsGameOver
        {
            get
            {
                RunnerWorld? world = _world;
                return world != null && world.IsGameOver;
            }
        }

        /// <summary>
        /// 重开一局：换一个全新的 <see cref="RunnerWorld"/>（等价于"再来一次"）。
        /// <para>
        /// <b>为什么必须重建而不是 <c>world.Restart()</c></b>：内核在 <c>CollectResult</c> 之后把会话
        /// 所有权交还，此时 <c>Tick</c> / <c>TryJump</c> / 连 <c>Restart</c> 本身都是空操作；
        /// 只有重新构造才能真正回到初始局面。所有权的唯一口径就是"这个对象还能不能被驱动"。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是 MonoBehaviour），它的
        /// <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"已撞上但还没结算"的那一帧，
        /// 直接重建会把这一局的 <c>GameFinishedEvent</c> 与战利品静默吞掉。判定已结束就先走
        /// <see cref="Finish"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// <para>
        /// <b>种子不换</b>：同一 <see cref="randomSeed"/> ⇒ 同一串障碍，演示可复现；想要不同节奏就在
        /// Inspector 里改种子。局中调用同样安全：旧局还没结算就直接丢弃（纯内存对象，没有原生资源）。
        /// </para>
        /// </summary>
        public void Restart()
        {
            IGameplaySession? session = _session;
            if (!_collected && session != null && session.IsFinished)
            {
                Finish();
            }

            CreateWorld();
        }

        /// <summary>
        /// 起跳。空中再次调用是安全的：内核按"已离地"拒绝（返回 false），本类不复制这条判定。
        /// </summary>
        public void Jump()
        {
            IRunnerInput? input = _world;
            if (input != null)
            {
                input.TryJump();
            }
        }

        /// <summary>
        /// 下蹲 <see cref="SlideSeconds"/> 秒。空中调用同样由内核拒绝，本类不做额外校验。
        /// </summary>
        public void Slide()
        {
            IRunnerInput? input = _world;
            if (input != null)
            {
                input.TrySlide(SlideSeconds);
            }
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的按钮订阅与首帧渲染要在
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
        /// 每帧：推动世界 → 自动演示决策 → 刷新 HUD → 撞上则结算一次。
        /// <b>顺序不可换</b>：先 Tick 再渲染，玩家看到的就是这一步之后的真实姿态；
        /// 先渲染再 Tick 会让画面永远慢一格（撞上也得多等一帧才显示结果）。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            RunnerWorld? world = _world;
            if (session == null || world == null)
            {
                return;
            }

            world.Tick(EffectiveDeltaSeconds());

            if (autoPlay)
            {
                DriveAutoPlay(world);
            }

            RenderHud();

            if (!_collected && session.IsFinished)
            {
                Finish();
            }
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
        /// 建（或重建）一局：构造内核 → 首帧渲染 → 打一条开局日志。
        /// <para>
        /// <b>为什么不经过 <c>host.Sessions</c></b>：宿主只按 <see cref="GameplayKernelType"/> 建会话，
        /// 而跑酷没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
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

            var init = new GameplayInitData(DefaultLevelId, randomSeed, SinglePlayerUnitCount);
            var world = new RunnerWorld(in init);

            _session = world;
            _world = world;

            RenderHud();

            host.Log.Info("[Runner] 开局：场次=" + DefaultLevelId
                + " 种子=" + randomSeed
                + " 自动演示=" + autoPlay);
        }

        /// <summary>
        /// 自动演示：找"最近的、还没被甩到身后的"障碍，按种类决定跳还是蹲。
        /// <para>
        /// <b>为什么先取最近障碍的 x 而不是按索引取 0 号</b>：内核的索引是"生成顺序"，0 号确实最靠近玩家，
        /// 但它在被回收前会一路走到 <c>x &lt; -2</c>；若直接取 0 号，刚越过一个障碍后 AI 会盯着身后那个
        /// 已经没威胁的障碍发呆，直到它被回收——这段空窗恰好会错过下一个障碍的起跳点。因此按 x 取最小，
        /// 并且<b>跳过 <c>x ≤ -0.6</c> 那些已经穿过碰撞窗的</b>（它们的 <c>Hit</c> 仍是 false，不能靠它过滤）。
        /// </para>
        /// <para>
        /// <b>两段式判定</b>：先看是否进入 <see cref="DecisionHorizon"/>（6 米），再看是否到了按速度算出的
        /// 触发距离（<c>速度 × 提前量</c>）。为什么要第二段，见 <see cref="JumpLeadSeconds"/> 的算术。
        /// </para>
        /// <para>
        /// <b>为什么不记"已经反应过的障碍编号"</b>：内核的输入本身是幂等的——空中 <c>TryJump</c> 返回 false，
        /// 重复 <c>TrySlide</c> 只是刷新计时。把去重放进内核以外的地方等于把同一份规则抄两遍；
        /// 这里每帧照常提交意图，由内核裁决。
        /// </para>
        /// </summary>
        /// <param name="world">当前对局（未结束）。</param>
        private void DriveAutoPlay(RunnerWorld world)
        {
            if (world.IsGameOver)
            {
                return;
            }

            int count = world.ObstacleCount;
            RunnerObstacleView nearest = default;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                RunnerObstacleView candidate = world.GetObstacle(i);

                // ObstacleId == 0 是"没有这个障碍"（越界/空槽位的默认值），不是合法障碍。
                if (candidate.ObstacleId == 0 || candidate.Hit)
                {
                    continue;
                }

                if (candidate.X <= -CollisionHalfWidth)
                {
                    continue;
                }

                if (!found || candidate.X < nearest.X)
                {
                    nearest = candidate;
                    found = true;
                }
            }

            if (!found)
            {
                return;
            }

            float x = nearest.X;
            if (x > DecisionHorizon)
            {
                return;
            }

            float speed = world.Speed;
            float lead = speed * (nearest.Kind == RunnerObstacleKind.Low ? JumpLeadSeconds : SlideLeadSeconds);

            if (x > lead)
            {
                return;
            }

            if (nearest.Kind == RunnerObstacleKind.Low)
            {
                world.TryJump();
            }
            else
            {
                world.TrySlide(SlideSeconds);
            }
        }

        /// <summary>
        /// 结算：<c>CollectResult</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示结果。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>为什么先把读数抄成本地变量</b>：<c>CollectResult</c> 一调用就把会话所有权交还，
        /// 之后再读内核字段属于"借用已交还的对象"；抄一份既避免这个语义问题，也让日志与广播
        /// 用的是同一瞬间的快照。
        /// </para>
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            RunnerWorld? world = _world;
            if (host == null || session == null || world == null || _collected)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;

            int score = world.Score;
            float distance = world.Distance;
            int bestScore = world.BestScore;

            GameplayResult result;
            try
            {
                result = session.CollectResult();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Runner] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, DefaultLevelId, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            RunnerHud? view = hud;
            if (view != null)
            {
                view.ShowResult(result.IsVictory, score);
            }

            host.Log.Info("[Runner] 结算：场次=" + DefaultLevelId
                + " 胜负=" + result.IsVictory
                + " 距离=" + distance.ToString("F1") + "m"
                + " 分数=" + score
                + " 最好=" + bestScore
                + " 步数=" + result.StepCount
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>把 HUD 的"重新开始 / 跳跃 / 下蹲"接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            RunnerHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked += HandleRestartClicked;
            view.OnJumpPressed += HandleJumpPressed;
            view.OnSlidePressed += HandleSlidePressed;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            RunnerHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked -= HandleRestartClicked;
            view.OnJumpPressed -= HandleJumpPressed;
            view.OnSlidePressed -= HandleSlidePressed;
        }

        /// <summary>点"重新开始"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>点"跳跃"：直接转发给内核，能不能跳由内核决定（空中会被拒）。</summary>
        private void HandleJumpPressed()
        {
            Jump();
        }

        /// <summary>点"下蹲"：直接转发给内核，能不能蹲由内核决定（空中会被拒）。</summary>
        private void HandleSlidePressed()
        {
            Slide();
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            RunnerHud? view = hud;
            RunnerWorld? world = _world;
            if (view != null && world != null)
            {
                view.Render(world, world.Speed);
            }
        }

        /// <summary>
        /// 本帧要喂给内核的时间（秒）：<see cref="Time.deltaTime"/> 夹到 <see cref="MaxFrameDeltaSeconds"/>。
        /// <para>
        /// <b>为什么不夹到 0</b>：暂停/首帧送来 0 是合法状态，内核自己会忽略非正数增量；
        /// 这里只负责"长帧不要一步跨过整个碰撞窗"。
        /// </para>
        /// </summary>
        /// <returns>0..<see cref="MaxFrameDeltaSeconds"/> 之间的帧增量。</returns>
        private static double EffectiveDeltaSeconds()
        {
            double delta = Time.deltaTime;
            return delta > MaxFrameDeltaSeconds ? MaxFrameDeltaSeconds : delta;
        }
    }
}
