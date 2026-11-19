#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.Rhythm;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 音游场景控制器：建宿主 → 直接构造 <see cref="RhythmWorld"/> → 每帧把真实时间喂给歌曲时钟 →
    /// 只读视图推给 HUD → 曲终结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new RhythmWorld</c>，而其它玩法控制器要走 <c>host.Sessions.Begin</c></b>：
    /// 适配层是组合根（<c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 没有 Rhythm 值、<c>UnityKernelFactory</c> 也没有对应分支。
    /// 为了一个纯演示场景去扩枚举与工厂，会让存档协议/契约为一套"对主板完全同构"的卡带付长期成本；
    /// 因此这里直接构造并<b>自己持有这次会话</b>（不登记进 <c>host.Sessions</c>），结算时直接调
    /// <see cref="IGameplaySession.CollectResult"/>。宿主仍然负责总线、日志与背包——本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何音游规则</b>：判定窗口、漏打扫描、连击、准确率、胜负门槛全部由内核决定；
    /// 适配层一旦自己算一遍（哪怕是"看起来等价"的复算），改内核而不改 UI 时两侧就会静默分叉。
    /// 本类只做四件事：装配宿主、把帧时间翻译成 <see cref="IRhythmInput.Tick"/>、把只读视图推给 HUD、
    /// 曲终结算并广播事实。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次 <c>Tick</c> 只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>AutoPlay 的演示契约</b>：内核在 <c>Tick</c> 里把已进入 ±0.045s 完美窗的音符自动打掉，
    /// 前提是适配层每一步都 &lt; 0.09s。这里把单帧增量夹到 <see cref="MaxFrameDeltaSeconds"/>（0.05s），
    /// 即使编辑器编译/切窗口产生一次长帧，也不会让音符整窗跳过（那会变成一串 Miss，与"自动演示全 Perfect"
    /// 的承诺相矛盾）。本场景没有音频，因此夹取造成的时间轴略微落后于墙钟是不可见的。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RhythmController : MonoBehaviour
    {
        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：音游是单人玩法，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对。
        /// </summary>
        private const int SinglePlayerUnitCount = 1;

        /// <summary>
        /// 单帧喂给歌曲时钟的上限（秒）。<b>为什么是 0.05</b>：内核的 AutoPlay 要求单步不超过
        /// 完美窗的整个宽度（±0.045s 合计 0.09s），否则一个音符可能被整窗跳过；0.05 留了余量。
        /// 再大就会在编辑器长帧后出现"自动演奏漏打"，让演示看起来像坏了。
        /// </summary>
        private const double MaxFrameDeltaSeconds = 0.05;

        /// <summary>音游皮肤（渲染状态行与 4 行轨道文本，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private RhythmHud hud = null!;

        /// <summary>
        /// 场次/谱面 ID：交给 <see cref="GameplayInitData"/>，并作为结算广播的场次标识。
        /// 内核只认 2（谱面 2），其它值一律夹到谱面 1（夹取属于内核规则，本类不复制）。
        /// </summary>
        [SerializeField] private int levelId = 1;

        /// <summary>随机种子：固定值让同一谱面的 ±0.012s 抖动与轨道偏移可复现。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>是否自动完美演奏（默认开：场景一进来就能看到连击增长与全 Perfect）。</summary>
        [SerializeField] private bool autoPlay = true;

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前对局会话（<see cref="IGameplaySession"/> 视角，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空，表示会话所有权已交还；重开时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一局的具体类型：<c>AutoPlay</c> 开关与四个判定计数不在契约里，只有具体类型能读。
        /// 结算后<b>保留</b>（HUD 要继续显示最终判定与准确率），重开时替换。
        /// </summary>
        private RhythmWorld? _world;

        /// <summary>本局是否已结算（<c>CollectResult</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>当前歌曲时间（秒；结算后仍可读，无会话时为 0）。</summary>
        public double SongTime
        {
            get
            {
                RhythmWorld? world = _world;
                return world != null ? world.SongTime : 0.0;
            }
        }

        /// <summary>当前连击（结算后仍可读，无会话时为 0）。</summary>
        public int Combo
        {
            get
            {
                RhythmWorld? world = _world;
                return world != null ? world.Combo : 0;
            }
        }

        /// <summary>当前准确率 <c>0..1</c>（结算后仍可读，无会话时为 0）。</summary>
        public double Accuracy
        {
            get
            {
                RhythmWorld? world = _world;
                return world != null ? world.Accuracy : 0.0;
            }
        }

        /// <summary>
        /// 重开一局：换一个全新的 <see cref="RhythmWorld"/>（等价于"再来一次"）。
        /// <para>
        /// <b>为什么必须重建而不是"清空"</b>：内核在 <c>CollectResult</c> 之后把会话所有权交还，
        /// <c>Tick</c>/<c>Hit</c> 全部冻结；只有重新构造才能真正回到初始局面。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是 MonoBehaviour），它的
        /// <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"曲终但还没结算"的那一帧，
        /// 直接重建会把这一局的 <c>GameFinishedEvent</c> 与战利品静默吞掉。判定已结束就先走
        /// <see cref="Finish"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// <para>局中调用同样安全：旧局还没结算就直接丢弃（纯内存对象，没有原生资源）。</para>
        /// </summary>
        public void Restart()
        {
            if (!_collected && _world != null && _world.IsFinished)
            {
                Finish();
            }

            CreateWorld();
        }

        /// <summary>
        /// 在 0..3 号轨道上击打一次。返回的判定不在这里消费：HUD 每帧从只读视图里读真实判定，
        /// 适配层再存一份就会变成"第二套账本"。
        /// <para>
        /// 越界轨道、空轨道、对局结束后调用都是安全的：内核按"空击"处理（只断连击，不消耗音符），
        /// 本类不做额外校验，避免把同一份规则抄两遍。
        /// </para>
        /// </summary>
        /// <param name="lane">轨道下标（0..3）。</param>
        public void HitLane(int lane)
        {
            RhythmWorld? world = _world;
            if (world != null)
            {
                world.Hit(lane);
            }
        }

        /// <summary>
        /// 切换自动演奏：只翻 <see cref="RhythmWorld.AutoPlay"/>，不重建对局（内核允许运行中切换）。
        /// <para>
        /// <b>为什么不需要重启</b>：AutoPlay 只是"下一次 <c>Tick</c> 要不要替你打点"的开关，
        /// 判定链路、计数与连击都走同一条路径；关掉之后玩家点轨道按钮就是手动演奏。
        /// </para>
        /// </summary>
        public void ToggleAutoPlay()
        {
            autoPlay = !autoPlay;

            RhythmWorld? world = _world;
            if (world != null)
            {
                world.AutoPlay = autoPlay;
            }

            AIFrameworkUnityHost? host = _host;
            if (host != null)
            {
                host.Log.Info("[Rhythm] 自动演奏=" + autoPlay);
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
        /// 每帧：推动歌曲时钟 → 刷新 HUD → 曲终则结算一次。
        /// <b>顺序不可换</b>：先 Tick 再渲染，玩家看到的就是这一步之后的真实连击/判定；
        /// 先渲染再 Tick 会让画面永远慢一格（曲终也要多等一帧才显示结果）。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            RhythmWorld? world = _world;
            if (session == null || world == null)
            {
                return;
            }

            world.Tick(EffectiveDeltaSeconds());

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
        /// 建（或重建）一局：构造内核 → 应用 <c>autoPlay</c> → 首帧渲染。
        /// <para>
        /// <b>为什么不经过 <c>host.Sessions</c></b>：宿主只按 <see cref="GameplayKernelType"/> 建会话，
        /// 而 Rhythm 没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
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

            var init = new GameplayInitData(levelId, randomSeed, SinglePlayerUnitCount);
            var world = new RhythmWorld(in init);
            world.AutoPlay = autoPlay;

            _session = world;
            _world = world;

            RenderHud();

            host.Log.Info("[Rhythm] 开局：场次=" + levelId
                + " 谱面=" + world.LevelId
                + " 音符=" + world.NoteCount
                + " 曲长=" + world.SongLengthSeconds + "s"
                + " 种子=" + randomSeed
                + " 自动演奏=" + world.AutoPlay);
        }

        /// <summary>
        /// 结算：<c>CollectResult</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示结果。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>为什么广播用配置的 <c>levelId</c></b>：它是这次"场次"的身份（与 Tetris 的
        /// <c>DefaultLevelId</c> 同义）；内核夹取后的谱面 ID 只进日志，不冒充玩家选的关卡。
        /// </para>
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            RhythmWorld? world = _world;
            if (host == null || session == null || world == null || _collected)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;

            RhythmJudgement best = BestJudgement(world);
            double accuracy = world.Accuracy;
            int chartId = world.LevelId;
            int maxCombo = world.MaxCombo;

            GameplayResult result;
            try
            {
                result = session.CollectResult();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Rhythm] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, levelId, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            RhythmHud? view = hud;
            if (view != null)
            {
                view.ShowResult(best, accuracy);
            }

            host.Log.Info("[Rhythm] 结算：场次=" + levelId
                + " 谱面=" + chartId
                + " 胜负=" + result.IsVictory
                + " 准确率=" + accuracy.ToString("P1")
                + " 最佳判定=" + best
                + " 最高连击=" + maxCombo
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>把 HUD 的"重新开始 / 4 个轨道按钮"接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            RhythmHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked += HandleRestartClicked;
            view.OnLaneClicked += HandleLaneClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            RhythmHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked -= HandleRestartClicked;
            view.OnLaneClicked -= HandleLaneClicked;
        }

        /// <summary>点"重新开始"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>点某条轨道：直接转发给内核判定，命中与否由内核决定。</summary>
        /// <param name="lane">轨道下标（0..3）。</param>
        private void HandleLaneClicked(int lane)
        {
            HitLane(lane);
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            RhythmHud? view = hud;
            RhythmWorld? world = _world;
            if (view != null && world != null)
            {
                view.Render(world);
            }
        }

        /// <summary>
        /// 本局"最佳判定"：四个计数里数值最小的那个判定（Perfect &lt; Great &lt; Good &lt; Miss）。
        /// <para>
        /// <b>为什么由适配层算而不是内核报</b>：它是"本局出现过的最好判定"这种纯展示口径，
        /// 不进 <see cref="GameplayResult"/>；内核只提供四个计数，谁展示谁决定怎么解读。
        /// 四种判定一个都没有时（理论上只会在 0 音符谱面出现）报 Miss，避免把空局说成 Perfect。
        /// </para>
        /// </summary>
        /// <param name="world">已结束的对局。</param>
        /// <returns>本局最佳判定。</returns>
        private static RhythmJudgement BestJudgement(RhythmWorld world)
        {
            if (world.PerfectCount > 0)
            {
                return RhythmJudgement.Perfect;
            }

            if (world.GreatCount > 0)
            {
                return RhythmJudgement.Great;
            }

            if (world.GoodCount > 0)
            {
                return RhythmJudgement.Good;
            }

            return RhythmJudgement.Miss;
        }

        /// <summary>
        /// 本帧要喂给内核的时间（秒）：<see cref="Time.deltaTime"/> 夹到 <see cref="MaxFrameDeltaSeconds"/>。
        /// <para>
        /// <b>为什么不夹到 0</b>：暂停/首帧送来 0 是合法状态，内核自己会忽略非正数增量；
        /// 这里只负责"长帧不要一次跨过整个完美窗"。
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
