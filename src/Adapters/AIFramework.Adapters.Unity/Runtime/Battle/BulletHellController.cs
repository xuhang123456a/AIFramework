#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 弹幕射击场景控制器（T2-5 Bullet Hell）：把 <c>Kernel.Realtime</c> 当"弹幕发射器"用——
    /// 每 <c>spawnIntervalSeconds</c> 秒从战场上方撒一组扇形弹幕，玩家由本类自动走位躲避，全程无键盘输入。
    /// <para>
    /// <b>它是压力演示而不是玩法切片</b>：压力来自"屏幕上的飞行物数量 × 每帧空间查询次数 ×
    /// 已登记碰撞体数量"，用来验证 <c>Kernel.Realtime</c> 在高密度飞行物下仍保持固定帧推进，
    /// 以及 <see cref="RealtimeSceneBinder"/> 能把上百个飞行物逐帧同步进 PhysX。
    /// 离线实测（同一套数值跑满 1200 帧，4±1 发/组、种子 20261120）：累计发射 ≈530 发、
    /// 玩家挨 ≈9 发、结算 HP 55、结束帧场景里挂着 ≈500 个飞行物碰撞体。
    /// </para>
    /// <para><b>六个刻意的设计决定（改之前先读）</b>：</para>
    /// <list type="number">
    /// <item>玩家<b>不带</b> <see cref="GameplayTags.UnitShieldActive"/>：带护盾标签会让
    /// <c>ShieldReflectSkillStep</c> 把飞到脸上的弹幕反射回去，演示里就永远掉不了血。</item>
    /// <item>弹幕的 <c>owner</c> 传<b>无效句柄</b>：内核的命中判定会跳过"飞行物归属者"，
    /// 若把 owner 记成玩家，弹幕永远打不到玩家；而伪造一个炮台单位，它自己又会变成可被命中的
    /// 有效目标（任一单位 HP≤0 即整场结束，炮台被打死会提前收场）。弹幕在这里是"环境产物"，没有归属单位。</item>
    /// <item>玩家必须<b>第一个</b> <c>SpawnUnit</c>：内核约定首个单位的存活与否就是胜负。</item>
    /// <item><see cref="GameplayInitData.UnitCount"/> 申报 256：这是唯一能撑大内核飞行物槽位池的旋钮
    /// （单位池 = max(64, 申报×2)，飞行物池 = 单位池×2，即 512 / 1024）。1200 帧 ÷ 9 帧一组 × 4 发 ≈ 533 发，
    /// 且<b>出界飞行物内核不会回收</b>（契约里没有 despawn 通道），所以池子必须一次给够，否则后半程"哑火"。</item>
    /// <item>固定 1/60 秒步长而不是 <c>Time.deltaTime</c>：弹幕密度与胜负不该随渲染帧率漂移。</item>
    /// <item>只认 Contracts 接口（<see cref="IRealtimeSandbox"/> / <see cref="IUnitWorldReadView"/> /
    /// <see cref="IProjectileWorldReadView"/> / <see cref="IIntentWriter"/> 全部靠 <c>as</c> 转换），
    /// 不引用 <c>AIFramework.Kernel.Realtime</c> 的任何类型，换内核实现不用改本文件。</item>
    /// </list>
    /// <para>
    /// <b>每帧顺序（改动的红线）</b>：布弹 → 写位移意图 → <c>Step</c> → <c>binder.Sync()</c> → 读快照/判结束 → 刷 HUD。
    /// 意图必须在 <c>Step</c> 之前进缓冲（Phase 1 暂存、Phase 5 落位）；
    /// <c>Sync</c> 必须在 <c>Step</c> 之后，因为下一次 <c>Step</c> 的 Phase 4 是按 Unity 碰撞体的当前位姿做空间查询的。
    /// </para>
    /// <para>
    /// <b>结束语义</b>：玩家 HP 归零 → 判负；活到内核的 1200 帧线 → 判胜（"生存成功"）。
    /// 结算走 <c>EndAndCollect</c>（只允许一次）→ 广播 <c>GameFinishedEvent</c> → 战利品入账。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BulletHellController : MonoBehaviour
    {
        /// <summary>关卡 ID（进门前快照的一部分，也是 <c>GameFinishedEvent</c> 的场次标识）。</summary>
        private const int LevelId = 1;

        /// <summary>
        /// 逻辑帧上限，<b>必须与 <c>BattleWorld</c> 的私有常量 <c>MaxFrames</c>（1200）一致</b>：
        /// 本类只负责"到了就收场"，真正的结束判据仍是内核的 <c>IsFinished</c>；两者一旦分叉，
        /// <see cref="Finish"/> 里的兜底分支会把本类的线当成失败处理（并留一条 Warn 日志）。
        /// </summary>
        private const int MaxFrames = 1200;

        /// <summary>
        /// 每个渲染帧推进的固定逻辑步长。<b>为什么是常量而不是 <c>[SerializeField]</c></b>：
        /// 本类的 Inspector 面被需求锁死为四个字段（hud / seed / interval / volley），
        /// 帧长固定 1/60 与内核一致，不需要现场调。
        /// </summary>
        private const float FixedStepSeconds = 1f / 60f;

        /// <summary>玩家初始/最大 HP。</summary>
        private const int PlayerHpMax = 100;

        /// <summary>玩家判定半径（世界单位）。</summary>
        private const float PlayerRadius = 0.4f;

        /// <summary>
        /// 玩家霸体等级：<b>刻意高于弹幕的打断力</b>（弹幕伤害同时充当打断力，见
        /// <c>BattleWorld.ApplyDamage(owner, slot, damage, damage)</c>），这样每发命中都不会触发打断计数器。
        /// 打断只影响控制位、不影响位移结算（Phase 5 明确不阻断），所以这不是玩法选择，只是不去污染诊断计数。
        /// </summary>
        private const int PlayerArmorLevel = 20;

        /// <summary>玩家自动走位速度（世界单位/秒）：必须快过弹幕的横向包夹，慢过弹幕本身。</summary>
        private const float PlayerSpeed = 7f;

        /// <summary>
        /// 申报给内核的单位数。<b>不是真实单位数</b>（场上只有 1 个玩家），只用来撑大槽位池：
        /// 单位池 = max(64, 256×2) = 512，飞行物池 = 512×2 = 1024。见类注释第 4 条。
        /// </summary>
        private const int DeclaredUnitCount = 256;

        /// <summary>战场半宽（X 方向），与 <c>BulletHellSceneBuilder</c> 的边框保持一致。</summary>
        private const float FieldHalfWidth = 6f;

        /// <summary>战场半深（Vec2 的 Y 轴 = Unity 的 Z 轴），与场景边框一致。</summary>
        private const float FieldHalfDepth = 4.5f;

        /// <summary>玩家可达的 X 边界：贴住战场边缘再留一点余量，避免半个身子出框。</summary>
        private const float MoveBoundX = FieldHalfWidth - PlayerRadius - 0.1f;

        /// <summary>玩家可达的 Y 边界。</summary>
        private const float MoveBoundY = FieldHalfDepth - PlayerRadius - 0.1f;

        /// <summary>弹幕生成线：战场上方一点，让弹丸"飞进来"而不是凭空出现在框内。</summary>
        private const float SpawnLineY = FieldHalfDepth + 0.7f;

        /// <summary>生成点横向散布半径。</summary>
        private const float SpawnHalfWidth = FieldHalfWidth - 0.5f;

        /// <summary>弹幕速度（世界单位/秒）：跨越整个战场约 1.1 秒，给自动走位留出穿插时间。</summary>
        private const float BulletSpeed = 8f;

        /// <summary>单发弹幕伤害：5 点 —— 100 HP 要挨 20 发才死，配合下面的走位参数刚好演出"擦弹式生存"。</summary>
        private const int BulletDamage = 5;

        /// <summary>
        /// 弹幕判定半径，<b>必须与 <c>BattleWorld</c> 的私有常量 <c>ProjectileRadius</c>（0.1f）一致</b>：
        /// 内核按"单位半径 + 本半径"判命中，场景侧球体碰撞体用同一个值缩放，两边不一致会让物理查询的
        /// 命中圈与内核判定错位。
        /// </summary>
        private const float ProjectileRadius = 0.1f;

        /// <summary>
        /// 同一组弹幕里相邻两发的角度间隔（弧度）：约 13.7°。
        /// <b>为什么不能更窄</b>：0.14 弧度时 4 发的"危险带"在玩家所在平面上会互相重叠、没有缝可钻，
        /// 玩家只能整组绕开，命中率会从"偶尔擦到"恶化到"每 1.5 秒挨一下"。
        /// </summary>
        private const float FanStepRadians = 0.24f;

        /// <summary>每组弹幕在模板发数上下浮动的幅度（默认 4 发 → 实际 3~5 发，对应需求的"3~5 枚"）。</summary>
        private const int VolleyJitter = 1;

        /// <summary>单组弹幕数量上限（扇形铺得再宽也没意义，同时压住每帧 SpawnProjectile 的次数）。</summary>
        private const int MaxVolleySize = 8;

        /// <summary>生成间隔下限：Inspector 里被改成 0 或负数时用它兜底，防止一帧生成无限组。</summary>
        private const float MinSpawnIntervalSeconds = 0.03f;

        /// <summary>
        /// 自动走位的"威胁圈"半径：只把圈内的弹幕纳入预测。
        /// <b>为什么是 7 而不是 5</b>：弹幕在生成线（Y=5.2）上离场中心的玩家有 5~9 个单位，
        /// 圈太小会导致"刚看见就已经来不及"，实测把圈放到 7 才给得出足够的提前量。
        /// </summary>
        private const float DangerRadius = 7f;

        /// <summary>单次躲避查询最多取多少颗弹幕（<c>stackalloc</c> 定长缓冲，同时是宿主空间查询缓冲的容量）。</summary>
        private const int MaxNearbyProjectiles = 128;

        /// <summary>走位采样方向数：24 个方向 ≈ 每 15° 一个候选（16 个方向实测容易卡在窄缝里）。</summary>
        private const int DodgeDirections = 24;

        /// <summary>预测时间窗（秒）：一个决策要"看"多久的未来。</summary>
        private const float LookaheadSeconds = 0.35f;

        /// <summary>预测时间窗内的采样点数：<b>只看终点会漏掉"半路穿过"的弹幕</b>，必须逐片采样。</summary>
        private const int LookaheadSamples = 6;

        /// <summary>回中偏好的权重：让玩家不贴着边框赖着（贴边一旦被包夹就没有退路）。</summary>
        private const float CenterBiasWeight = 0.35f;

        /// <summary>回中偏好的归一化半径 ≈ |(4.0, 2.8)|，超过它就完全不再往中间拉。</summary>
        private const float CenterBiasRadius = 4.88f;

        /// <summary>方向记忆权重：轻微偏好上一帧的方向，避免两个等价方向来回抖。</summary>
        private const float DirectionMemoryWeight = 0.10f;

        /// <summary>完全没有威胁时给一个"开阔"分数，免得分母为空时选不出方向。</summary>
        private const float OpenSpaceScore = 10f;

        /// <summary>退化距离/合力门限：小于它视为没有位移。</summary>
        private const float MinForceSqr = 1e-6f;

        /// <summary>随机种子为 0 时的替代值（xorshift 的状态不能为 0）。</summary>
        private const uint FallbackSeed = 0x9E3779B9u;

        /// <summary>场景皮肤（状态行与重开按钮，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private BulletHellHud hud = null!;

        /// <summary>随机种子：固定值让"同一帧同一组弹幕"可复现（弹幕用本类自带的 xorshift，不碰 Unity 的全局随机）。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>两组弹幕的间隔（秒）。默认 0.15 → 1/60 步长下每 9 帧一组。</summary>
        [SerializeField] private float spawnIntervalSeconds = 0.15f;

        /// <summary>每组弹幕的模板发数（实际会 ±1 抖动）；会被夹到 1..<see cref="MaxVolleySize"/>。</summary>
        [SerializeField] private int projectilesPerVolley = 4;

        /// <summary>宿主（总线/会话/日志/空间查询的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>当前对局会话；结算或重开前置空。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的布场端口；内核未实现该接口时为 <c>null</c>。</summary>
        private IRealtimeSandbox? _sandbox;

        /// <summary>会话强转出来的单位只读视图；内核未实现该接口时为 <c>null</c>。</summary>
        private IUnitWorldReadView? _units;

        /// <summary>会话强转出来的飞行物只读视图；内核未实现该接口时为 <c>null</c>。</summary>
        private IProjectileWorldReadView? _projectiles;

        /// <summary>会话强转出来的意图写入通道（自动走位的唯一出口）；内核未实现时为 <c>null</c>。</summary>
        private IIntentWriter? _intents;

        /// <summary>
        /// 场景表现绑定器：把单位与每一颗弹幕变成<b>已登记碰撞体</b>的场景图元，并逐帧同步 Transform。
        /// 它构造期抓住本会话的两个只读视图、<c>Dispose</c> 一次性，因此<b>一局一换</b>。
        /// </summary>
        private RealtimeSceneBinder? _binder;

        /// <summary>玩家句柄（本局第一个 <c>SpawnUnit</c> 的单位）。</summary>
        private UnitHandle _player;

        /// <summary>玩家位姿缓存：每帧 <c>Step</c> 之后从视图刷新，弹幕生成与走位都读它。</summary>
        private Vec2 _playerPosition;

        /// <summary>上一帧选中的走位方向（方向记忆的输入，避免抖动）。</summary>
        private Vec2 _lastDodgeDirection = new Vec2(0f, -1f);

        /// <summary>玩家 HP 缓存（<see cref="PlayerHp"/> 的数据源，视图不可读时保留最后已知值）。</summary>
        private int _playerHp;

        /// <summary>存活弹幕缓存（<see cref="AliveProjectiles"/> 的数据源）。</summary>
        private int _aliveProjectiles;

        /// <summary>已推进的逻辑帧数（与内核的 <c>FrameId</c> 一一对应）。</summary>
        private int _frameCount;

        /// <summary>本局是否已结束（含"宿主装配失败"这种一帧都没跑的情况）。</summary>
        private bool _finished;

        /// <summary>本局是否已结算（<c>EndAndCollect</c> 只允许发生一次）。</summary>
        private bool _settled;

        /// <summary>是否允许自动走位（<see cref="StopAutoPlay"/> 关掉后玩家原地挨打）。</summary>
        private bool _autoPlay = true;

        /// <summary>飞行物槽位是否已经打过一次"耗尽"警告（避免每帧刷日志）。</summary>
        private bool _poolWarned;

        /// <summary>距离下一组弹幕的累计时间（秒）。</summary>
        private float _spawnTimer;

        /// <summary>xorshift32 的状态。<b>为什么不用 <c>UnityEngine.Random</c></b>：它是全局状态，
        /// 会被场景里任何一处 <c>Random.value</c> 影响，弹幕就没法复现；本类的种子只归本类。</summary>
        private uint _randomState = FallbackSeed;

        /// <summary>玩家当前 HP（HP 归零即本局失败）。</summary>
        public int PlayerHp
        {
            get { return _playerHp; }
        }

        /// <summary>内核里当前存活（未被命中回收）的飞行物数量。</summary>
        public int AliveProjectiles
        {
            get { return _aliveProjectiles; }
        }

        /// <summary>已推进的逻辑帧数。</summary>
        public int FrameCount
        {
            get { return _frameCount; }
        }

        /// <summary>本局是否已结束（结束后 <see cref="Restart"/> 才重新开局）。</summary>
        public bool IsFinished
        {
            get { return _finished; }
        }

        /// <summary>
        /// 建宿主 → 挂 HUD 事件 → 开第一局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：
        /// 重开按钮的订阅必须早于玩家可能点下去的时刻，第一局也要赶在第一次 <c>Update</c> 之前布好。
        /// <para>
        /// <b>为什么把空间查询缓冲调到 128</b>：走位每帧要用一次球查询收集威胁圈内的弹幕，
        /// 默认 64 条命中缓冲在弹幕密集时会被截断（截断顺序由物理宽相决定，等于随机丢掉最近的威胁）。
        /// </para>
        /// </summary>
        private void Awake()
        {
            AIFrameworkUnityHost host;
            try
            {
                host = new AIFrameworkUnityHost(spatialBufferCapacity: MaxNearbyProjectiles);
                host.Initialize();
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is ArgumentException
                || exception is IOException)
            {
                // 宿主装配失败（存档目录不可写等）时不能静默：整场演示都没法跑。
                Debug.LogError("[BulletHell] 宿主装配失败：" + exception.Message);
                _finished = true;
                return;
            }

            _host = host;

            BulletHellHud? view = hud;
            if (view != null)
            {
                view.OnRestartClicked += HandleRestartClicked;
            }
            else
            {
                host.Log.Error("[BulletHell] hud 未注入，场景里没有任何状态显示与重开入口。");
            }

            BeginRun();
        }

        /// <summary>
        /// 每帧推进一个固定逻辑步：布弹 → 写位移意图 → <c>Step</c> → <c>Sync</c> → 判定/刷新。
        /// 顺序见类注释的"每帧顺序"。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            if (session == null || _finished)
            {
                return;
            }

            // 1) 到点补一组弹幕。布场只允许发生在 Step 之外（内核按预分配假设，帧内布场会破坏它）。
            SpawnDueVolleys();

            // 2) 用"上一次 Step 之后读到的位姿"算走位方向并写位移意图：
            //    意图要赶在 Step 之前进缓冲，才能被本次 Step 的 Phase 1 暂存、Phase 5 落位。
            SubmitDodgeIntent();

            // 3) 固定步长推进一帧。
            session.Step(FixedStepSeconds);
            _frameCount++;

            // 4) 把 Step 之后的位姿写回 Transform 并让 PhysX 同步：
            //    下一次 Step 的 Phase 4 是按 Unity 碰撞体位姿做空间查询的，同步必须落在两次 Step 之间。
            SyncScene();

            // 5) 读快照 → 判结束 → 刷 HUD。
            ReadField();
            if (TryFinish(session))
            {
                return;
            }

            RenderHud();
        }

        /// <summary>
        /// 重开一局：撤掉旧场景对象 → 丢弃旧会话 → 重新 <c>Begin(Realtime)</c> 并布场。
        /// <para>
        /// <b>为什么未结算的会话用 <c>Release</c> 而不是 <c>EndAndCollect</c></b>：后者要求内核已经收场
        /// （玩家死了或帧数触顶），打一半点重开时调它必然抛 <c>InvalidOperationException</c>；
        /// 而"这一局作废"本来就是演示想要的结果，不需要（也不该）编一份结算报告出来。
        /// </para>
        /// </summary>
        public void Restart()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            // 先撤旧绑定再换会话：新会话的句柄从 0 号槽位重新发放，
            // 旧碰撞体若还挂在宿主级空间查询上，会被当成新单位/新飞行物串号。
            DisposeBinder();

            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            _session = null;
            _sandbox = null;
            _units = null;
            _projectiles = null;
            _intents = null;
            _settled = false;

            BeginRun();

            host.Log.Info("[BulletHell] 重开：弹幕计时、帧数与走位状态全部复位。");
        }

        /// <summary>
        /// 停止自动走位：玩家不再提交任何位移意图，原地承受弹幕直到 HP 归零（演示失败路径）。
        /// <para>
        /// <b>为什么用 <c>[ContextMenu]</c></b>：Inspector 面被需求锁死为四个字段，加不了第二个按钮；
        /// 右键组件菜单是在编辑器里现场触发失败演示、又不动公开契约的最小手段。
        /// </para>
        /// <para>停止后只能靠 <see cref="Restart"/> 恢复（重开会把自动走位重新打开）。</para>
        /// </summary>
        [ContextMenu("停止自动走位（演示失败）")]
        public void StopAutoPlay()
        {
            _autoPlay = false;
            _host?.Log.Info("[BulletHell] 自动走位已停止：玩家原地不动，等弹幕把 HP 清空。");
        }

        /// <summary>
        /// 开一局：<c>Begin(Realtime)</c> → 取四个端口 → 布玩家 → 建绑定器 → 复位计数。
        /// 任何一步失败都把本局停在"已结束"，绝不用半套状态继续推演。
        /// </summary>
        private void BeginRun()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            _frameCount = 0;
            _playerHp = PlayerHpMax;
            _aliveProjectiles = 0;
            _playerPosition = Vec2.Zero;
            _lastDodgeDirection = new Vec2(0f, -1f);
            _player = default;
            _finished = false;
            _settled = false;
            _autoPlay = true;
            _poolWarned = false;
            _spawnTimer = 0f;
            _randomState = randomSeed != 0 ? (uint)randomSeed : FallbackSeed;

            IGameplaySession session;
            try
            {
                var init = new GameplayInitData(LevelId, randomSeed, DeclaredUnitCount);
                session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[BulletHell] 开局失败（宿主工厂未接 Realtime 分支？）：" + exception.Message);
                _finished = true;
                return;
            }

            IRealtimeSandbox? sandbox = session as IRealtimeSandbox;
            if (sandbox == null)
            {
                // 布不了场就绝不能让 Update 空转这一局：交还会话并停掉演示。
                host.Log.Error("[BulletHell] 会话未实现 IRealtimeSandbox，无法布场（本场放弃）。");
                host.Sessions.Release();
                _finished = true;
                return;
            }

            _session = session;
            _sandbox = sandbox;
            _units = session as IUnitWorldReadView;
            _projectiles = session as IProjectileWorldReadView;
            _intents = session as IIntentWriter;

            if (_units == null || _projectiles == null)
            {
                host.Log.Warn("[BulletHell] 会话未完整实现只读视图：场景里不会创建任何碰撞体，HUD 也不会有状态。");
            }

            if (_intents == null)
            {
                // 没有意图通道 → 自动走位失效（玩家站桩），但弹幕与结算仍然成立，所以只降级不中止。
                host.Log.Warn("[BulletHell] 会话未实现 IIntentWriter：自动走位不可用，玩家将原地挨打。");
            }

            // 玩家不带护盾标签（否则弹幕会被反射回去），且必须是第一个 SpawnUnit 的单位。
            UnitHandle player = sandbox.SpawnUnit(
                Vec2.Zero, PlayerRadius, PlayerArmorLevel, PlayerHpMax, GameplayTagMask.Empty);

            if (!player.IsValid)
            {
                host.Log.Error("[BulletHell] 玩家槽位耗尽，本局布场失败。");
                host.Sessions.Release();
                _session = null;
                _sandbox = null;
                _units = null;
                _projectiles = null;
                _intents = null;
                _finished = true;
                return;
            }

            _player = player;
            _binder = CreateBinder(host);
            RealtimeSceneBinder? binder = _binder;
            if (binder != null)
            {
                binder.AddUnit(player, Vec2.Zero, PlayerRadius, "Player");
            }

            host.Log.Info("[BulletHell] 开局：玩家 HP=" + PlayerHpMax
                + "，弹幕间隔=" + spawnIntervalSeconds + "s × " + projectilesPerVolley + "±" + VolleyJitter + " 发"
                + "，帧上限=" + MaxFrames + "。");

            RenderHud();
        }

        /// <summary>
        /// 为当前会话建场景绑定器（调用前必须先 <see cref="DisposeBinder"/>）。
        /// <b>为什么一局一建</b>：绑定器构造期抓住本会话的两个只读视图，换会话后旧视图全部失效，
        /// 且它的 <c>Dispose</c> 会永久置位，不能复用。
        /// </summary>
        /// <param name="host">宿主（绑定器从这里取空间查询端口）。</param>
        /// <returns>绑定器；两个只读视图缺任一则为 <c>null</c>（退回纯数据布场）。</returns>
        private RealtimeSceneBinder? CreateBinder(AIFrameworkUnityHost host)
        {
            IUnitWorldReadView? units = _units;
            IProjectileWorldReadView? projectiles = _projectiles;
            if (units == null || projectiles == null)
            {
                return null;
            }

            // root 用控制器自身节点：生成物挂在 BulletHell 下面，Hierarchy 里一眼能看出这一局是谁的。
            return new RealtimeSceneBinder(host, units, projectiles, transform);
        }

        /// <summary>把 Step 之后的位姿同步进场景与 PhysX；绑定器缺失（纯数据布场）时静默跳过。</summary>
        private void SyncScene()
        {
            RealtimeSceneBinder? binder = _binder;
            if (binder != null)
            {
                binder.Sync();
            }
        }

        /// <summary>撤掉当前绑定器：逐个注销碰撞体并销毁场景对象；可重复调用。</summary>
        private void DisposeBinder()
        {
            RealtimeSceneBinder? binder = _binder;
            _binder = null;
            if (binder != null)
            {
                binder.Dispose();
            }
        }

        /// <summary>
        /// 到点就补一组弹幕。<b>用累加器而不是按帧号取模</b>：间隔不是帧长的整数倍时
        /// （比如 Inspector 里填 0.1），取模会累积漂移，累加器不会。
        /// </summary>
        private void SpawnDueVolleys()
        {
            if (!_player.IsValid)
            {
                return;
            }

            float interval = spawnIntervalSeconds > MinSpawnIntervalSeconds
                ? spawnIntervalSeconds
                : MinSpawnIntervalSeconds;

            _spawnTimer += FixedStepSeconds;
            while (_spawnTimer >= interval)
            {
                _spawnTimer -= interval;
                SpawnVolley();
            }
        }

        /// <summary>
        /// 生成一组扇形弹幕：生成点在上边界随机取横向位置，整组以"生成点 → 玩家当前位置"为轴
        /// 按 <see cref="FanStepRadians"/> 铺开，玩家无论站哪都会被罩住，只能靠穿插躲。
        /// <para>
        /// <b>owner 传 <c>default</c> 不是笔误</b>：见类注释第 2 条——归属者会被内核的命中判定跳过。
        /// </para>
        /// </summary>
        private void SpawnVolley()
        {
            IRealtimeSandbox? sandbox = _sandbox;
            if (sandbox == null)
            {
                return;
            }

            int count = projectilesPerVolley;
            if (count < 1)
            {
                count = 1;
            }
            else if (count > MaxVolleySize)
            {
                count = MaxVolleySize;
            }

            // 模板发数 ±1：需求要的是"3~5 枚"，同时让弹幕疏密不均，视觉上不像节拍器。
            count += RandomSign();
            if (count < 1)
            {
                count = 1;
            }
            else if (count > MaxVolleySize)
            {
                count = MaxVolleySize;
            }

            Vec2 origin = new Vec2(RandomRange(-SpawnHalfWidth, SpawnHalfWidth), SpawnLineY);

            Vec2 toPlayer = _playerPosition - origin;
            if (toPlayer.SqrLength <= MinForceSqr)
            {
                // 玩家正好站在生成线上（理论不可达，玩家可达上界是 4.1）：退化成正下方射击。
                toPlayer = new Vec2(0f, -1f);
            }

            float baseAngle = (float)Math.Atan2(toPlayer.Y, toPlayer.X);
            float fanCenter = (count - 1) * 0.5f;

            for (int i = 0; i < count; i++)
            {
                float angle = baseAngle + ((i - fanCenter) * FanStepRadians);
                Vec2 direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));

                ProjectileHandle handle = sandbox.SpawnProjectile(
                    default,
                    origin,
                    direction * BulletSpeed,
                    BulletDamage,
                    GameplayTagMask.Empty);

                if (!handle.IsValid)
                {
                    // 槽位耗尽（不扩容、不抛异常，内核的约定）：本组剩余弹丸直接丢弃。
                    // 默认参数下不会发生（1200 帧最多 ≈533 发 / 1024 槽位），但 volley=8 时会撞上。
                    if (!_poolWarned)
                    {
                        _poolWarned = true;
                        _host?.Log.Warn("[BulletHell] 飞行物槽位已耗尽，后续弹幕会被丢弃"
                            + "（把 projectilesPerVolley 调小，或调大 DeclaredUnitCount）。");
                    }

                    continue;
                }

                RealtimeSceneBinder? binder = _binder;
                if (binder != null)
                {
                    binder.AddProjectile(handle, origin, ProjectileRadius, "Bullet_" + _frameCount + "_" + i);
                }
            }
        }

        /// <summary>
        /// 自动走位：在 <b>24 个候选方向</b>里挑"未来 0.35 秒内离所有弹幕最远"的那个，
        /// 折算成一帧的<b>位移增量</b>，用 <see cref="MoveIntent"/> 提交给内核的仲裁器（本类绝不直接改坐标）。
        /// <para>
        /// <b>为什么是"采样预测"而不是"斥力求和"</b>：斥力模型在多颗弹幕对称包夹时合矢量会互相抵消，
        /// 玩家会停在原地挨打（离线实测 100 HP 只能撑 337 帧）。改成逐时间片预测后同一套参数能撑满 1200 帧。
        /// </para>
        /// <para>
        /// <b>为什么只看端点不够</b>：一帧内玩家移动 0.117、弹幕移动 0.133，命中判定发生在两者当前位姿上；
        /// 若只比较"0.35 秒后"的两点，会漏掉正从身边穿过的那一颗。所以时间窗切成
        /// <see cref="LookaheadSamples"/> 片，每一片都算一次间距，取最小值当该方向的分数。
        /// </para>
        /// <para>
        /// <b>为什么读 <c>host.Spatial</c> 而不是遍历飞行物视图</b>：视图的 <c>this[int]</c> 是"稠密下标 →
        /// 槽位"的线性扫描，遍历 N 颗弹幕是 O(N²)；而 <c>QueryProjectilesCircle</c> 是一次物理球查询，
        /// 复杂度只与"威胁圈内的弹幕数"有关。这是每帧都要跑的路径，差别是数量级的。
        /// </para>
        /// </summary>
        private void SubmitDodgeIntent()
        {
            if (!_autoPlay)
            {
                return;
            }

            IIntentWriter? writer = _intents;
            AIFrameworkUnityHost? host = _host;
            if (writer == null || host == null || !_player.IsValid)
            {
                return;
            }

            Vec2 position = _playerPosition;

            // 栈上定长缓冲：这条路径每帧都跑，不能碰托管堆。
            Span<ProjectileHandle> nearby = stackalloc ProjectileHandle[MaxNearbyProjectiles];
            int found = host.Spatial.QueryProjectilesCircle(position, DangerRadius, nearby);
            int limit = found < nearby.Length ? found : nearby.Length;

            Span<Vec2> start = stackalloc Vec2[MaxNearbyProjectiles];
            Span<Vec2> velocity = stackalloc Vec2[MaxNearbyProjectiles];
            int count = 0;

            IProjectileWorldReadView? projectiles = _projectiles;
            if (projectiles != null)
            {
                for (int i = 0; i < limit; i++)
                {
                    if (!projectiles.TryGetProjectile(nearby[i], out ProjectileReadView view))
                    {
                        continue;
                    }

                    start[count] = view.Position;
                    velocity[count] = view.Velocity;
                    count++;
                }
            }

            float bestScore = float.MinValue;
            Vec2 bestDirection = Vec2.Zero;
            float sampleSeconds = LookaheadSeconds / LookaheadSamples;

            for (int k = 0; k < DodgeDirections; k++)
            {
                double angle = (Math.PI * 2.0 * k) / DodgeDirections;
                Vec2 direction = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));

                // 逐时间片预测：玩家沿 direction 匀速走、弹幕沿自身速度匀速飞，取整段时间窗内的最小间距。
                float worst = count > 0 ? float.MaxValue : OpenSpaceScore;
                for (int s = 1; s <= LookaheadSamples; s++)
                {
                    float t = s * sampleSeconds;
                    Vec2 self = ClampToField(position + (direction * (PlayerSpeed * t)));

                    for (int i = 0; i < count; i++)
                    {
                        Vec2 bullet = start[i] + (velocity[i] * t);
                        float distance = Vec2.Distance(in self, in bullet);
                        if (distance < worst)
                        {
                            worst = distance;
                        }
                    }
                }

                Vec2 candidate = ClampToField(position + (direction * (PlayerSpeed * LookaheadSeconds)));

                // 回中偏好 + 方向记忆：前者防止贴着边框被包夹，后者防止两个等价方向来回抖。
                float centerBias = CenterBiasWeight * (1f - (candidate.Length / CenterBiasRadius));
                float score = worst
                    + centerBias
                    + (DirectionMemoryWeight * Vec2.Dot(in direction, in _lastDodgeDirection));

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = direction;
                }
            }

            if (bestDirection.SqrLength <= MinForceSqr)
            {
                return;
            }

            _lastDodgeDirection = bestDirection;

            Vec2 next = ClampToField(position + (bestDirection * (PlayerSpeed * FixedStepSeconds)));
            Vec2 delta = next - position;
            if (delta.SqrLength <= MinForceSqr)
            {
                // 已经被顶在边界上：这一帧没有可走的空间，不提交空意图。
                return;
            }

            MoveIntent intent = new MoveIntent(_player, delta);
            writer.Submit(in intent);
        }

        /// <summary>
        /// 把位置夹进玩家可达框内。<b>为什么位置必须自己夹</b>：内核没有边界概念，
        /// 越界不会报错，只会把玩家送出镜头之外（`MoveIntent` 是无条件位移增量）。
        /// </summary>
        /// <param name="position">候选位置。</param>
        /// <returns>夹好之后的位置。</returns>
        private static Vec2 ClampToField(in Vec2 position)
        {
            float x = position.X;
            if (x > MoveBoundX)
            {
                x = MoveBoundX;
            }
            else if (x < -MoveBoundX)
            {
                x = -MoveBoundX;
            }

            float y = position.Y;
            if (y > MoveBoundY)
            {
                y = MoveBoundY;
            }
            else if (y < -MoveBoundY)
            {
                y = -MoveBoundY;
            }

            return new Vec2(x, y);
        }

        /// <summary>
        /// 从只读视图刷新本帧缓存。视图不可读（内核没实现）时保留上一次的值，
        /// 而不是把 HP 显示成 0——"读不到"和"死了"必须区分开。
        /// </summary>
        private void ReadField()
        {
            IUnitWorldReadView? units = _units;
            if (units != null && units.TryGetUnit(_player, out UnitReadView playerView))
            {
                _playerPosition = playerView.Position;
                _playerHp = playerView.Hp;
            }

            IProjectileWorldReadView? projectiles = _projectiles;
            _aliveProjectiles = projectiles != null ? projectiles.Count : 0;
        }

        /// <summary>
        /// 一步之后的收场判定，优先级：玩家阵亡 → 内核已收场（生存成功）→ 帧数兜底。
        /// <para>
        /// <b>为什么"玩家阵亡"排最前</b>：玩家死的那一帧内核也 <c>IsFinished</c>，
        /// 若先判内核收场会把"弹幕打死玩家"读成"生存成功"。
        /// </para>
        /// </summary>
        /// <param name="session">当前会话。</param>
        /// <returns>本帧是否已收场。</returns>
        private bool TryFinish(IGameplaySession session)
        {
            if (_playerHp <= 0)
            {
                Finish(false);
                return true;
            }

            if (session.IsFinished)
            {
                // 玩家还活着而内核对局已结束，只可能是撞上了它内部的 1200 帧线 → 生存成功。
                Finish(true);
                return true;
            }

            if (_frameCount >= MaxFrames)
            {
                // 兜底：本类的帧上限与内核的常量一旦分叉，这一条保证演示不会永远打下去。
                _host?.Log.Warn("[BulletHell] 帧数触顶但内核仍未收场（内核 MaxFrames 与本类常量不一致？）。");
                Finish(_playerHp > 0);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 结算本局：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示终局。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局结束了"这个事实再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已胜利但金币还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>不透支绑定器</b>：结束时<b>不</b> <c>Dispose</c> 绑定器——冻结最后一帧的弹幕与玩家，
        /// 玩家能看到"我是被哪一片弹幕打死的"；下一次 <see cref="Restart"/> 与 <c>OnDestroy</c> 会撤掉它。
        /// </para>
        /// </summary>
        /// <param name="isVictory">演示侧判定：活到帧上限为 <c>true</c>，HP 归零为 <c>false</c>。</param>
        private void Finish(bool isVictory)
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _settled)
            {
                return;
            }

            // 先置标记再结算：结算抛异常时不允许每帧重试（Update 也会因此直接返回）。
            _settled = true;
            _finished = true;

            IGameplaySession? session = _session;
            if (session != null && !session.IsFinished)
            {
                // 本类判了结束而内核还没收场（帧线上限分叉）：先把玩家打到 0 血，
                // 保证 EndAndCollect 读到的结果与调用方传进来的判定一致。
                ForceDefeat(session);
            }

            _session = null;
            _sandbox = null;
            _units = null;
            _projectiles = null;
            _intents = null;

            GameplayResult result;
            try
            {
                result = host.Sessions.EndAndCollect();
            }
            catch (InvalidOperationException exception)
            {
                // 只有"没有活跃会话"或"内核仍未收场"会走到这里：留痕，且不再重试。
                host.Log.Error("[BulletHell] 结算失败：" + exception.Message);
                RenderHud();
                return;
            }

            if (result.IsVictory != isVictory)
            {
                host.Log.Warn("[BulletHell] 内核判定(" + result.IsVictory + ")与演示判定(" + isVictory
                    + ")不一致，以 EndAndCollect 为准。");
            }

            host.PublishGameFinished(GameplayKernelType.Realtime, LevelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            host.Log.Info("[BulletHell] 结算：胜负=" + result.IsVictory
                + "，帧=" + _frameCount
                + "，剩余 HP=" + _playerHp
                + "，存活弹幕=" + _aliveProjectiles
                + "，战利品条目=" + loots.Count);

            RenderHud();
        }

        /// <summary>
        /// 兜底判负：通过契约里的意图通道提交一次必杀伤害，再空步一次让仲裁器结算。
        /// <b>为什么不直接广播失败</b>：<c>PublishGameFinished</c> 只能报 <c>result.IsVictory</c>，
        /// 改血量是唯一能让"演示判负"与内核结算一致的做法，而且没有绕过管线（Phase 4 统一仲裁）。
        /// </summary>
        /// <param name="session">当前会话（必须仍未收场）。</param>
        private void ForceDefeat(IGameplaySession session)
        {
            IIntentWriter? writer = _intents;
            if (writer == null || !_player.IsValid)
            {
                _host?.Log.Error("[BulletHell] 无法通过意图通道终结对局，胜负以内核结果为准。");
                return;
            }

            // breakPower 传 0：这是"演示收场"而不是战斗伤害，不该触发打断计数。
            DamageIntent intent = new DamageIntent(_player, _player, PlayerHpMax * 2, 0);
            writer.Submit(in intent);
            session.Step(0f);

            // 空步之后缓存是旧的，但 HP 已在内核里归零；对齐一次视图，避免日志撒谎。
            IUnitWorldReadView? units = _units;
            if (units != null && units.TryGetUnit(_player, out UnitReadView view))
            {
                _playerHp = view.Hp;
            }
        }

        /// <summary>退订 HUD 事件、撤掉本局场景对象并释放宿主（顺序不可换）。</summary>
        private void OnDestroy()
        {
            BulletHellHud? view = hud;
            if (view != null)
            {
                view.OnRestartClicked -= HandleRestartClicked;
            }

            _session = null;
            _sandbox = null;
            _units = null;
            _projectiles = null;
            _intents = null;
            _finished = true;

            // 先撤掉本局生成的对象与碰撞体登记，再释放宿主：两步都不能省，
            // 否则重进场景时上一局的映射会一直挂在宿主级空间查询里。
            DisposeBinder();

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 重开按钮的落点。<b>为什么不在这里判"已结束才允许重开"</b>：
        /// 皮肤已经把按钮锁成只有 <c>isFinished</c> 时才可点，重开语义本身由 <see cref="Restart"/> 承担。
        /// </summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>
        /// 用本帧结果渲染一次 HUD。会话可能已被置空，所以只传值拷贝（HUD 不持有任何战斗状态）。
        /// </summary>
        private void RenderHud()
        {
            BulletHellHud? view = hud;
            if (view != null)
            {
                view.Render(_playerHp, _aliveProjectiles, _frameCount, _finished);
            }
        }

        /// <summary>在 [<paramref name="min"/>, <paramref name="max"/>) 上取一个可复现的随机数。</summary>
        /// <param name="min">下界（含）。</param>
        /// <param name="max">上界（不含）。</param>
        /// <returns>随机值。</returns>
        private float RandomRange(float min, float max)
        {
            return min + ((max - min) * NextFloat01());
        }

        /// <summary>取 -1 / 0 / +1 之一（等概率），用于每组弹幕的发数抖动。</summary>
        /// <returns>-1、0 或 1。</returns>
        private int RandomSign()
        {
            int roll = (int)(NextFloat01() * 3f);
            if (roll <= 0)
            {
                return -VolleyJitter;
            }

            return roll >= 2 ? VolleyJitter : 0;
        }

        /// <summary>
        /// xorshift32：一次异或移位即可，无分配、无引擎依赖、状态只归本类，
        /// 因此"同种子同弹幕序列"在任何平台都成立（<c>UnityEngine.Random</c> 做不到）。
        /// </summary>
        /// <returns>[0, 1) 之间的值。</returns>
        private float NextFloat01()
        {
            uint x = _randomState;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _randomState = x;

            // 取低 24 位再归一化：浮点尾数精度足够，且不会被符号位污染。
            return (x & 0xFFFFFFu) / 16777216f;
        }
    }
}
