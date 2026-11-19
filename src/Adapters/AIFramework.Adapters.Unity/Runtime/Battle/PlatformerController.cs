#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.Platformer;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 平台跳跃场景控制器：建宿主 → 直接构造 <see cref="PlatformerWorld"/> → 每帧把真实时间喂给
    /// <see cref="IPlatformerInput.Tick"/> → 只读视图推给 HUD → 到达终点/掉出世界时结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new PlatformerWorld</c>，而战棋/三消控制器要走 <c>host.Sessions</c></b>：
    /// 适配层是组合根（<c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 没有 Platformer 值（平台跳跃对主板而言只是"又一个回合制会话"，
    /// 结算仍按 <see cref="GameplayKernelType.Turn"/>）。为一个纯演示场景去扩枚举与工厂，会让存档协议与契约
    /// 为一套"对主板完全同构"的卡带付长期成本；因此这里直接构造并<b>自己持有这次会话</b>
    /// （不登记进 <c>host.Sessions</c>），结算时直接调 <see cref="IGameplaySession.CollectResult"/>。
    /// 宿主仍然负责总线、日志与背包——本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何平台跳跃规则</b>：重力、跳跃初速、单向平台落地、收集品判定、终点与胜负
    /// 全部由内核决定。适配层一旦自己算一遍（哪怕是"看起来等价"的复算），改内核而不改 UI 时两侧就会
    /// 静默分叉。本类只做五件事：装配宿主、把帧时间翻译成 <see cref="IPlatformerInput.Tick"/>、
    /// 把按钮/自动演示翻译成移动与跳跃意图、把只读视图推给 HUD、结束时结算并广播事实。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次 <c>Tick</c> 只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>AutoPlay 的演示契约</b>：内核按 <c>0.05 s</c> 上限切物理子步，这里把单帧增量也夹到
    /// <see cref="MaxFrameDeltaSeconds"/>（0.05 s），让"一帧 = 一个子步"，自动演示的决策粒度与物理粒度
    /// 对齐；即使编辑器编译/切窗口产生一次长帧，也不会因为一帧跨过平台边缘而把整段跳跃弧线跳掉。
    /// 夹取造成的时间轴略微落后于墙钟在演示里不可见。
    /// </para>
    /// <para>
    /// <b>自动演示为什么必须"能走就别跳"</b>：第 2 关的浮台 <c>[8,16]</c> 右沿正对着下方的地面
    /// <c>[16,24]</c>，而从浮台起跳会落到 <c>[18,22]</c> 那块更高的浮台上；站在 <c>[18,22]</c> 上再往前走，
    /// 4 米宽的坑（<c>24..28</c>）就再也跨不过去了——滞空 0.727 s 的横移是 4.36 米，而从上往下落回地面
    /// 会多花 1.2 米的高度差，落点正好落在坑里。所以自动演示只在"前方同高度断掉、且正前方也没有更低平台"
    /// 时才起跳，其余情况下宁可走下去（掉到低一级的平台上再继续）。这不是保守，是通关的必要条件。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlatformerController : MonoBehaviour
    {
        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：平台跳跃是单人玩法，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对。
        /// </summary>
        private const int SinglePlayerUnitCount = 1;

        /// <summary>
        /// 单帧喂给内核的上限（秒）。<b>为什么是 0.05</b>：内核的 <c>Tick</c> 按 0.05 s 切子步，
        /// 夹到同一个值就等于"一帧一个子步"，自动演示每帧看到的都是最新且完整的物理结果；
        /// 0.05 s × 6 m/s = 0.3 米也远小于 3 米宽的最窄平台，长帧不会让玩家整帧穿过平台。
        /// </summary>
        private const double MaxFrameDeltaSeconds = 0.05;

        /// <summary>
        /// 自动演示的探针距离（米）：从玩家左沿向前 0.4 米。<b>为什么不是玩家 AABB 宽 0.8</b>：
        /// 视图不暴露玩家尺寸，而 AI 只需要"还站在平台上时看到前方断掉"——一帧最多走
        /// <c>0.05 s × 6 m/s = 0.3 米</c>，探针比它宽 0.1 米就保证每帧至少有一次决策落在
        /// <c>(右沿 - 0.4, 右沿]</c> 这段窗口里；同时起跳点尽量贴近边缘，4 米宽的坑
        /// （第 2/3 关）才有落点余量（滞空 0.727 s × 6 m/s ≈ 4.36 米）。
        /// </summary>
        private const float EdgeProbeMeters = 0.4f;

        /// <summary>
        /// "同一高度"的容差（米）：只用于自动演示判断探针下面有没有同高度的落脚点，
        /// 不参与任何碰撞或落地判定（那些只在内核里发生）。容差比内核的站地容差大两个数量级，
        /// 是因为探针读的是<b>平台数据</b>而不是玩家状态，不存在亚毫米级抖动。
        /// </summary>
        private const float FootToleranceMeters = 0.01f;

        /// <summary>平台跳跃皮肤（渲染状态行与 20×10 字符简图，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private PlatformerHud hud = null!;

        /// <summary>
        /// 关卡 ID：交给 <see cref="GameplayInitData"/>，并作为结算广播的场次标识。
        /// 内核只认 2/3 命中另外两关、其余一律第 1 关；夹取属于内核规则，本类不复制，
        /// 但会把内核的结论回写进本字段（见 <see cref="CreateWorld"/>），避免 HUD/广播与内核各说一套。
        /// </summary>
        [SerializeField] private int levelId = 1;

        /// <summary>
        /// 随机种子：固定值让同一关的物理推进与自动演示完全可复现
        /// （内核当前的关卡是确定性的，仍按进出门契约透传，便于将来换成配置表关卡）。
        /// </summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>
        /// 是否自动演示（默认开：场景一进来就能看到角色一路向右、在坑边起跳并通关）。
        /// 关掉后玩家用 HUD 的"左移/右移/跳跃"按钮手动操作，判定链路完全一样。
        /// </summary>
        [SerializeField] private bool autoPlay = true;

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前对局会话（<see cref="IGameplaySession"/> 视角，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空，表示会话所有权已交还；重开/换关时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一局的具体类型：<see cref="PlatformerWorld.LevelId"/> / <see cref="PlatformerWorld.GoalX"/>
        /// 不在只读视图里，只有具体类型能读；同时它本身就是 <see cref="IPlatformerReadView"/> 与
        /// <see cref="IPlatformerInput"/>。结算后<b>保留</b>（HUD 要继续显示终局字符简图与结果），重开时替换。
        /// </summary>
        private PlatformerWorld? _world;

        /// <summary>本局是否已结算（<c>CollectResult</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>当前分数（结算后仍可读；无会话时为 0）。</summary>
        public int Score
        {
            get
            {
                PlatformerWorld? world = _world;
                return world != null ? world.Score : 0;
            }
        }

        /// <summary>结束方式是否为胜利（未结束或掉出世界时为 false；结算后仍可读）。</summary>
        public bool IsVictory
        {
            get
            {
                PlatformerWorld? world = _world;
                return world != null && world.IsVictory;
            }
        }

        /// <summary>
        /// 重开本局：换一个全新的 <see cref="PlatformerWorld"/>（等价于"再来一次"）。
        /// <para>
        /// <b>为什么必须重建而不是"清空"</b>：内核在 <c>CollectResult</c> 之后把会话所有权交还，
        /// <c>Tick</c> 与输入全部冻结（连 <c>Restart</c> 本身都是空操作）；只有重新构造才能真正回到初始局面。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是 MonoBehaviour），它的
        /// <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"已结束但还没结算"的那一帧，
        /// 直接重建会把这一局的 <c>GameFinishedEvent</c> 与战利品静默吞掉。判定已结束就先走
        /// <see cref="Finish"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// </summary>
        public void Restart()
        {
            FlushPendingResult();
            CreateWorld(levelId);
        }

        /// <summary>
        /// 换到指定关卡：与 <see cref="Restart"/> 走同一条重建路径，只是换一个进场参数。
        /// <para>
        /// 越界关卡（0、负数、4 以上）由内核夹到第 1 关，本类不复制这份规则；夹取结果会回写进
        /// <c>levelId</c>，所以 HUD 显示、结算广播与内核实际跑的关卡永远一致。
        /// </para>
        /// </summary>
        /// <param name="levelId">关卡 ID（1..3，其它值由内核夹到 1）。</param>
        public void LoadLevel(int levelId)
        {
            FlushPendingResult();
            CreateWorld(levelId);
        }

        /// <summary>
        /// 设置左右移动意图（手动操作，<c>autoPlay=false</c> 时用）。
        /// <para>
        /// 语义与 <see cref="IPlatformerInput.SetMove"/> 一致：<b>按住式</b>——调用一次之后方向会一直保持，
        /// 直到下一次调用覆盖（HUD 只提供"左移/右移"两个按钮，没有"松开"事件，所以点一次就持续朝那个方向走）。
        /// 超出 <c>-1..1</c> 的值与 <c>NaN</c> 由内核夹取，本类不做校验。
        /// </para>
        /// <para>
        /// 自动演示开着时本调用仍会转发，但 <see cref="Update"/> 每帧都会把方向重新压成 <c>+1</c>，
        /// 所以手动方向只在关掉 <c>autoPlay</c> 后才有意义。
        /// </para>
        /// </summary>
        /// <param name="direction">-1 全速向左、+1 全速向右、0 松手。</param>
        public void Move(float direction)
        {
            PlatformerWorld? world = _world;
            if (world != null)
            {
                world.SetMove(direction);
            }
        }

        /// <summary>
        /// 起跳。空中再次调用是安全的：内核按"已离地"拒绝（返回 false），本类不复制这条判定。
        /// </summary>
        public void Jump()
        {
            PlatformerWorld? world = _world;
            if (world != null)
            {
                world.TryJump();
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
            CreateWorld(levelId);
        }

        /// <summary>
        /// 每帧：推动物理 → 自动演示决策 → 刷新 HUD → 结束则结算一次。
        /// <b>顺序不可换</b>：先 Tick 再渲染，玩家看到的就是这一步之后的真实位置；
        /// 先渲染再 Tick 会让画面永远慢一格（终点/掉落也要多等一帧才显示结果）。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            PlatformerWorld? world = _world;
            if (session == null || world == null)
            {
                return;
            }

            world.Tick(EffectiveDeltaSeconds());

            if (autoPlay)
            {
                DriveAutoPlay(world, world);
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
        /// 建（或重建）一局：构造内核 → 回写夹取后的关卡号 → 首帧渲染 → 打一条开局日志。
        /// <para>
        /// <b>为什么不经过 <c>host.Sessions</c></b>：宿主只按 <see cref="GameplayKernelType"/> 建会话，
        /// 而平台跳跃没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
        /// 因此也不需要 <c>Release</c> 上一局——旧对象随字段替换被 GC。
        /// </para>
        /// </summary>
        /// <param name="requestedLevelId">请求的关卡 ID（未经夹取）。</param>
        private void CreateWorld(int requestedLevelId)
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            _session = null;
            _world = null;
            _collected = false;

            var init = new GameplayInitData(requestedLevelId, randomSeed, SinglePlayerUnitCount);
            var world = new PlatformerWorld(in init);

            _session = world;
            _world = world;

            // 关卡号的夹取口径完全在内核（只有 2/3 命中其它两关，其余一律第 1 关）；
            // 这里只把内核的结论回写进配置字段，避免"HUD 显示第 9 关、实际跑第 1 关"。
            levelId = world.LevelId;

            RenderHud();

            host.Log.Info("[Platformer] 开局：关卡=" + world.LevelId
                + " 平台=" + world.PlatformCount
                + " 收集品=" + world.CollectibleCount
                + " 终点x=" + world.GoalX.ToString("F1")
                + " 种子=" + randomSeed
                + " 自动演示=" + autoPlay);
        }

        /// <summary>
        /// 自动演示：一直按住右；只有"前方同高度没有地面、且正前方也没有更低的平台"时才起跳。
        /// <para>
        /// <b>为什么只吃两个端口</b>：<paramref name="view"/> / <paramref name="input"/> 就是
        /// <see cref="IPlatformerReadView"/> 与 <see cref="IPlatformerInput"/>，因此这段决策不依赖
        /// <see cref="MonoBehaviour"/>、不读 <c>Time</c>，将来搬进无头测试可以直接喂桩。
        /// </para>
        /// <para>
        /// <b>为什么不记"已经跳过的坑"</b>：内核的输入本身是幂等的——空中 <c>TryJump</c> 返回 false，
        /// 站在地面上重复提交只是再来一次起跳；把去重放进内核以外的地方等于把同一份规则抄两遍。
        /// 这里每帧照常提交意图，由内核裁决。
        /// </para>
        /// <para>
        /// <b>为什么"前方更低"要让路不走</b>：见类型注释里第 2 关浮台 <c>[8,16]</c> 的反例——
        /// 在那里起跳会落到更高的 <c>[18,22]</c> 上，从此跨不过 <c>24..28</c> 的坑；
        /// 直接走下去则落到 <c>[16,24]</c> 的地面，坑就还在起跳距离之内。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图（玩家位置与平台表）。</param>
        /// <param name="input">输入端口（移动与跳跃意图）。</param>
        private static void DriveAutoPlay(IPlatformerReadView view, IPlatformerInput input)
        {
            if (view.IsFinished)
            {
                return;
            }

            // "向右侧移动"的全部内容：没有任何速度/加速度口径，满舵由内核决定。
            input.SetMove(1f);

            if (!view.IsOnGround)
            {
                return;
            }

            float feetY = view.PlayerY;
            float probeX = view.PlayerX + EdgeProbeMeters;

            FootingKind footing = ProbeFooting(view, probeX, feetY);
            if (footing == FootingKind.None)
            {
                input.TryJump();
            }
        }

        /// <summary>
        /// 探针结果：探针脚下有什么。
        /// </summary>
        private enum FootingKind
        {
            /// <summary>什么都没有（悬空）：再不跳就会掉下去。</summary>
            None = 0,

            /// <summary>同高度的平台：前方还是连续地面，继续走。</summary>
            SameLevel = 1,

            /// <summary>更低的平台：走下去能接住，不必跳（跳反而可能落到更高的浮台上）。</summary>
            Lower = 2
        }

        /// <summary>
        /// 在探针位置 <paramref name="probeX"/> 上找脚下有什么：
        /// 与 <paramref name="feetY"/> 齐平的平台算 <see cref="FootingKind.SameLevel"/>，
        /// 顶面低于脚底的平台算 <see cref="FootingKind.Lower"/>，都没有则是 <see cref="FootingKind.None"/>。
        /// <para>
        /// <b>区间为什么是闭区间</b>：探针恰好落在平台右沿时仍算"还有地面"。这跟内核的半开区间碰撞
        /// 不是同一件事——内核管"是否还站着"，探针管"要不要现在起跳"；边界上多给一帧容错，
        /// 才不会在 <c>右沿 - 0.4</c> 那一帧把"能走"误判成"悬空"。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <param name="probeX">探针的横坐标（米）。</param>
        /// <param name="feetY">玩家脚底高度（米）。</param>
        /// <returns>探针脚下的落脚情况。</returns>
        private static FootingKind ProbeFooting(IPlatformerReadView view, float probeX, float feetY)
        {
            FootingKind result = FootingKind.None;
            int platformCount = view.PlatformCount;

            for (int i = 0; i < platformCount; i++)
            {
                PlatformerView platform = view.GetPlatform(i);

                if (probeX < platform.X || probeX > platform.X + platform.Width)
                {
                    continue;
                }

                float difference = feetY - (platform.Y + platform.Height);

                if (difference >= -FootToleranceMeters && difference <= FootToleranceMeters)
                {
                    return FootingKind.SameLevel;
                }

                if (difference > FootToleranceMeters)
                {
                    result = FootingKind.Lower;
                }
            }

            return result;
        }

        /// <summary>
        /// 若本局已结束但还没结算，先补一次 <see cref="Finish"/>（重开/换关前调用）。
        /// <para>
        /// 按钮回调（<c>EventSystem</c>）的 <c>Update</c> 可能排在本类之前，若不补这一刀，
        /// "结束那一帧点重开"会把本局的广播与战利品静默吞掉。<see cref="Finish"/> 自己有
        /// <c>_collected</c> 兜底，重复调用不会结算两次。
        /// </para>
        /// </summary>
        private void FlushPendingResult()
        {
            PlatformerWorld? world = _world;
            if (!_collected && world != null && world.IsFinished)
            {
                Finish();
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
        /// 之后再读内核字段属于"借用已交还的对象"；抄一份既避免这个语义问题，也让日志与 HUD
        /// 用的是同一瞬间的快照。内核也把本局得分镜像进了 <see cref="GameplayResult.RemainingUnits"/>
        /// （平台跳跃没有兵力，复用该字段承载得分），这里用视图里的分数与之同源同值。
        /// </para>
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            PlatformerWorld? world = _world;
            if (host == null || session == null || world == null || _collected)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;

            int score = world.Score;
            int collectedCount = world.CollectedCount;
            int collectibleCount = world.CollectibleCount;

            GameplayResult result;
            try
            {
                result = session.CollectResult();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Platformer] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, levelId, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            PlatformerHud? view = hud;
            if (view != null)
            {
                view.ShowResult(result.IsVictory, score);
            }

            host.Log.Info("[Platformer] 结算：关卡=" + levelId
                + " 胜负=" + result.IsVictory
                + " 分数=" + score
                + " 收集=" + collectedCount + "/" + collectibleCount
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>把 HUD 的"重新开始 / 下一关 / 左移 / 右移 / 跳跃"接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            PlatformerHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked += HandleRestartClicked;
            view.OnNextLevelClicked += HandleNextLevelClicked;
            view.OnLeftPressed += HandleLeftPressed;
            view.OnRightPressed += HandleRightPressed;
            view.OnJumpPressed += HandleJumpPressed;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            PlatformerHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked -= HandleRestartClicked;
            view.OnNextLevelClicked -= HandleNextLevelClicked;
            view.OnLeftPressed -= HandleLeftPressed;
            view.OnRightPressed -= HandleRightPressed;
            view.OnJumpPressed -= HandleJumpPressed;
        }

        /// <summary>点"重新开始"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>
        /// 点"下一关"：直接 <c>levelId + 1</c>。<b>为什么不在这里判"最后一关"</b>：
        /// 第 3 关之后传 4，内核会夹回第 1 关——关卡总数只有内核知道，适配层再存一份就会各说一套。
        /// </summary>
        private void HandleNextLevelClicked()
        {
            LoadLevel(levelId + 1);
        }

        /// <summary>点"左移"：满舵向左（按住式，下一次点击才会改方向）。</summary>
        private void HandleLeftPressed()
        {
            Move(-1f);
        }

        /// <summary>点"右移"：满舵向右。</summary>
        private void HandleRightPressed()
        {
            Move(1f);
        }

        /// <summary>点"跳跃"：直接转发给内核，能不能跳由内核决定（空中会被拒）。</summary>
        private void HandleJumpPressed()
        {
            Jump();
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            PlatformerHud? view = hud;
            PlatformerWorld? world = _world;
            if (view != null && world != null)
            {
                view.Render(world);
            }
        }

        /// <summary>
        /// 本帧要喂给内核的时间（秒）：<see cref="Time.deltaTime"/> 夹到 <see cref="MaxFrameDeltaSeconds"/>。
        /// <para>
        /// <b>为什么不夹到 0</b>：暂停/首帧送来 0 是合法状态，内核自己会忽略非正数增量；
        /// 这里只负责"长帧不要一次跨过整个平台边缘"。
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
