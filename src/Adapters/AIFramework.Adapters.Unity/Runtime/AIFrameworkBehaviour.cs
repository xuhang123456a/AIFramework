#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Features.DailySignIn;
using AIFramework.Kernel.Realtime;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 场景入口：挂在任意 GameObject 上即可把整个框架跑起来。
    /// <para>
    /// <b>它只做三件事</b>：<c>Awake</c> 装配 <see cref="AIFrameworkUnityHost"/>；<c>Start</c> 跑一次签到演示；
    /// <c>Update</c> 按 <c>Time.deltaTime</c> 推进当前实时会话。<b>业务规则一律不写在这里</b>——
    /// 这里是引擎生命周期与纯逻辑世界之间的唯一缝隙，写得越薄，换引擎/换 UI 时越不用动。
    /// </para>
    /// <para>
    /// <b>Update 的零分配纪律</b>：<c>Update</c> 只读字段、只调 <c>Step</c>，不 <c>new</c>、不 LINQ、不拼字符串；
    /// 会话结束后的结算日志（需要拼字符串）刻意推迟到 <c>LateUpdate</c> 做一次。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AIFrameworkBehaviour : MonoBehaviour
    {
        /// <summary>演示关卡 ID。</summary>
        private const int DemoLevelId = 1;

        /// <summary>演示随机种子（局内随机由内核用它构造自己的随机流）。</summary>
        private const int DemoRandomSeed = 20261118;

        /// <summary>演示单位判定半径（世界单位）。</summary>
        private const float DemoUnitRadius = 1f;

        /// <summary>演示单位霸体等级。</summary>
        private const int DemoUnitArmorLevel = 5;

        /// <summary>演示单位血量。</summary>
        private const int DemoUnitHp = 100;

        /// <summary>
        /// 是否在 <c>Start</c> 自动开一局实时会话。<b>为什么要有这个开关</b>：演示场景需要一个
        /// "挂上就动"的默认行为；正式项目由自己的流程（选关、匹配结果）决定何时开战，那时应关掉它。
        /// </summary>
        [SerializeField]
        private bool _autoBeginRealtimeOnStart = true;

        /// <summary>
        /// 场景里当作战场单位的碰撞体（Inspector 里拖入）。<b>为什么用序列化数组而不是自动扫描</b>：
        /// 自动 <c>FindObjectsOfType&lt;Collider&gt;</c> 会把地面、装饰物一起绑成单位，且顺序不稳定——
        /// 而 <c>BattleWorld</c> 以"第一个生成的单位"为主角，顺序必须由人显式给定。
        /// </summary>
        [SerializeField]
        private Collider[] _sceneUnits = new Collider[0];

        /// <summary>
        /// 是否给第一个场景单位挂护盾标签。<b>为什么默认开</b>：演示场景要能看到
        /// <c>ShieldReflectSkillStep</c> 的反射链路，护盾必须落在主角（首个生成的单位）身上。
        /// </summary>
        [SerializeField]
        private bool _firstUnitHasShield = true;

        /// <summary>装配好的框架实例；装配失败时为 <c>null</c>（见 <c>Awake</c> 的异常分支）。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前活跃的实时会话。<b>为什么缓存成字段</b>：<c>Update</c> 每帧都要用，
        /// 走 <c>Sessions</c> 属性只会多一层间接；更重要的是它明确了"本组件只推 Realtime 卡带"。
        /// </summary>
        private IGameplaySession? _activeSession;

        /// <summary>当前实时战局布场入口（Contracts 接口，不依赖内核具体类型）；回合制会话时为 <c>null</c>。</summary>
        private IRealtimeSandbox? _activeSandbox;

        /// <summary>当前场次 ID：结算时随 <c>GameFinishedEvent</c> 一起广播，任务/活动切片据此区分关卡。</summary>
        private int _activeLevelId;

        /// <summary>
        /// 固定步长（秒）。<b>为什么要累加器</b>：<c>BattleWorld.Step</c> 按秒推进，若直接把可变的
        /// <c>Time.deltaTime</c> 传进去，帧率抖动会改变推演结果，回放/断线重连就复现不出来。
        /// 传 0 表示退化为"每帧一步、步长取 deltaTime"（只用于纯表现向的场合）。
        /// </summary>
        [SerializeField]
        private float _fixedStepSeconds = 1f / 60f;

        /// <summary>单帧最多补几步：防止卡顿后一次性追帧把整帧拖死（追上不的部分直接丢弃）。</summary>
        [SerializeField]
        private int _maxStepsPerFrame = 4;

        /// <summary>固定步长累加器；只在 <c>Update</c> 里读写，不做持久化。</summary>
        private float _stepAccumulator;

        /// <summary>
        /// 会话刚结束、结果还没取。<b>为什么需要这个标记</b>：<c>EndAndCollect</c> 只能调用一次，
        /// 而"发现结束"发生在 <c>Update</c>（不许拼字符串），"取结果并打日志"发生在 <c>LateUpdate</c>，
        /// 两个生命周期回调之间必须有个状态传递。
        /// </summary>
        private bool _resultPending;

        /// <summary>装配是否成功；失败时 <c>Start</c>/<c>Update</c> 全部跳过，避免每帧抛异常刷屏。</summary>
        private bool _ready;

        /// <summary>装配好的框架实例；未装配成功时为 <c>null</c>。</summary>
        public AIFrameworkUnityHost? Host
        {
            get { return _host; }
        }

        /// <summary>空间查询端口：需要手动 <c>RegisterCollider</c> 时用它；未装配成功时为 <c>null</c>。</summary>
        public UnitySpatialQuery? Spatial
        {
            get
            {
                AIFrameworkUnityHost? host = _host;
                return host != null ? host.Spatial : null;
            }
        }

        /// <summary>当前实时战局布场入口；没有进行中的 Realtime 对局时为 <c>null</c>。</summary>
        public IRealtimeSandbox? Realtime
        {
            get { return _activeSandbox; }
        }

        /// <summary>装配框架。异常在这里被拦下并打日志：一个数据缺失不该让整个场景失效。</summary>
        private void Awake()
        {
            try
            {
                var host = new AIFrameworkUnityHost();
                host.Initialize();
                _host = host;
                _ready = true;
            }
            catch (Exception exception)
            {
                _host = null;
                _ready = false;
                Debug.LogError(
                    "[AIFramework] 装配失败，本组件已停用。请检查 StreamingAssets 子表与 persistentDataPath 写权限。");
                Debug.LogException(exception);
            }
        }

        /// <summary>跑一次签到演示；按配置决定是否顺带开一局实时战斗。</summary>
        private void Start()
        {
            AIFrameworkUnityHost? host = _host;
            if (!_ready || host == null)
            {
                return;
            }

            LogSignInSlots(host);

            if (!_autoBeginRealtimeOnStart)
            {
                return;
            }

            if (_sceneUnits == null || _sceneUnits.Length == 0)
            {
                // 空战场推进没有任何演示价值，只会白烧 CPU，因此连会话都不开。
                host.Log.Info("未配置场景单位（Scene Units 为空），跳过 Realtime 演示会话。");
                return;
            }

            if (!BeginRealtime(DemoLevelId, DemoRandomSeed, _sceneUnits.Length))
            {
                return;
            }

            BindSceneUnits(host);
        }

        /// <summary>
        /// 每帧推进实时会话。<b>只允许读字段与调用 <c>Step</c></b>：任何 <c>new</c>、LINQ、字符串拼接
        /// 都会变成每帧 GC 压力，而实时内核的验收线是"预热后 1000 帧增量分配为 0"。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _activeSession;
            if (session == null)
            {
                return;
            }

            if (session.IsFinished)
            {
                _activeSession = null;
                _resultPending = true;
                return;
            }

            if (_fixedStepSeconds <= 0f)
            {
                session.Step(Time.deltaTime);
                return;
            }

            _stepAccumulator += Time.deltaTime;

            int steps = 0;
            while (_stepAccumulator >= _fixedStepSeconds && steps < _maxStepsPerFrame)
            {
                _stepAccumulator -= _fixedStepSeconds;
                steps++;
                session.Step(_fixedStepSeconds);

                if (session.IsFinished)
                {
                    _activeSession = null;
                    _resultPending = true;
                    break;
                }
            }

            // 追不上就把余量清掉：否则卡顿一次之后会长期积压，越追越慢。
            if (steps >= _maxStepsPerFrame)
            {
                _stepAccumulator = 0f;
            }
        }

        /// <summary>会话结束时的一次性收尾：取结果、打日志（允许拼字符串，因为只发生一次）。</summary>
        private void LateUpdate()
        {
            if (!_resultPending)
            {
                return;
            }

            _resultPending = false;
            CollectAndLogResult();
        }

        /// <summary>卸载时释放宿主：解绑视图事件、丢弃未结算会话引用。</summary>
        private void OnDestroy()
        {
            _activeSession = null;
            _activeSandbox = null;
            _stepAccumulator = 0f;
            _resultPending = false;
            _ready = false;

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 开一局实时会话。已有会话未结算时返回 <c>false</c> 并打警告（不抛异常：场景脚本不该被流程问题炸掉）。
        /// </summary>
        /// <param name="levelId">关卡 ID。</param>
        /// <param name="randomSeed">局内随机种子（内核自己持有，回放可复现）。</param>
        /// <param name="unitCount">本局预计单位数，内核据此一次性分配槽位容量。</param>
        /// <returns>是否成功创建实时战局。</returns>
        public bool BeginRealtime(int levelId, int randomSeed, int unitCount)
        {
            _activeLevelId = levelId;
            AIFrameworkUnityHost? host = _host;
            if (!_ready || host == null)
            {
                return false;
            }

            if (host.Sessions.HasActiveSession)
            {
                host.Log.Warn("已有活跃会话未结算，先 EndAndCollect 再开新局。");
                return false;
            }

            // 新一局的句柄从 0 号槽位重新发放，旧映射必须清空，否则上一局的碰撞体会把查询结果串到新单位身上。
            host.Spatial.Clear();

            var init = new GameplayInitData(levelId, randomSeed, unitCount);
            _stepAccumulator = 0f;
            _activeSession = host.Sessions.Begin(GameplayKernelType.Realtime, in init);

            // 只按 Contracts 接口拿布场能力：适配层不再依赖 BattleWorld 具体类型。
            _activeSandbox = _activeSession as IRealtimeSandbox;
            if (_activeSandbox == null)
            {
                host.Log.Error("Realtime 内核未实现 IRealtimeSandbox，无法布场。");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 把一个场景碰撞体绑成战场单位：<c>BattleWorld.SpawnUnit</c> 生成句柄，
        /// 再用 <c>UnitySpatialQuery.RegisterCollider</c> 建立"物理体 → 句柄"映射。
        /// <para>位置取碰撞体世界坐标的 X-Z 平面（<c>Vec2.Y</c> 对应世界 Z，见 <see cref="UnitySpatialQuery"/>）。</para>
        /// </summary>
        /// <param name="collider">代表该单位的碰撞体。</param>
        /// <param name="radius">判定半径（世界单位）。</param>
        /// <param name="armorLevel">霸体等级。</param>
        /// <param name="hp">血量。</param>
        /// <param name="tags">初始标签掩码。</param>
        /// <returns>生成的单位句柄；没有活跃会话或槽位耗尽时返回无效句柄。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="collider"/> 为 <c>null</c>。</exception>
        public UnitHandle BindUnit(Collider collider, float radius, int armorLevel, int hp, GameplayTagMask tags)
        {
            if (collider == null)
            {
                throw new ArgumentNullException(nameof(collider));
            }

            AIFrameworkUnityHost? host = _host;
            IRealtimeSandbox? sandbox = _activeSandbox;
            if (host == null || sandbox == null)
            {
                if (host != null)
                {
                    host.Log.Warn("尚无活跃 Realtime 会话，先调用 BeginRealtime 再绑定单位。");
                }

                return default;
            }

            Vector3 position = collider.transform.position;
            UnitHandle handle = sandbox.SpawnUnit(
                new Vec2(position.x, position.z), radius, armorLevel, hp, tags);

            if (!handle.IsValid)
            {
                host.Log.Error("单位槽位耗尽，绑定失败：" + collider.name);
                return handle;
            }

            host.Spatial.RegisterCollider(collider, handle);
            return handle;
        }

        /// <summary>
        /// 把一个场景碰撞体绑成飞行物：<c>BattleWorld.SpawnProjectile</c> 生成句柄，
        /// 再登记到空间查询的飞行物表，护盾反射阶段才查得到它。
        /// </summary>
        /// <param name="collider">代表该飞行物的碰撞体。</param>
        /// <param name="owner">归属单位（反射后由内核改写）。</param>
        /// <param name="velocity">速度（每秒世界单位，战斗平面 X-Z）。</param>
        /// <param name="damage">伤害值。</param>
        /// <param name="tags">初始标签掩码。</param>
        /// <returns>生成的飞行物句柄；没有活跃会话或槽位耗尽时返回无效句柄。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="collider"/> 为 <c>null</c>。</exception>
        public ProjectileHandle BindProjectile(
            Collider collider,
            UnitHandle owner,
            Vec2 velocity,
            int damage,
            GameplayTagMask tags)
        {
            if (collider == null)
            {
                throw new ArgumentNullException(nameof(collider));
            }

            AIFrameworkUnityHost? host = _host;
            IRealtimeSandbox? sandbox = _activeSandbox;
            if (host == null || sandbox == null)
            {
                if (host != null)
                {
                    host.Log.Warn("尚无活跃 Realtime 会话，先调用 BeginRealtime 再绑定飞行物。");
                }

                return default;
            }

            Vector3 position = collider.transform.position;
            ProjectileHandle handle = sandbox.SpawnProjectile(
                owner, new Vec2(position.x, position.z), velocity, damage, tags);

            if (!handle.IsValid)
            {
                host.Log.Error("飞行物槽位耗尽，绑定失败：" + collider.name);
                return handle;
            }

            host.Spatial.RegisterProjectileCollider(collider, handle);
            return handle;
        }

        /// <summary>按 Inspector 配置逐个生成并绑定场景单位；第一个（可选）挂护盾标签。</summary>
        private void BindSceneUnits(AIFrameworkUnityHost host)
        {
            Collider[] units = _sceneUnits;
            for (int i = 0; i < units.Length; i++)
            {
                Collider collider = units[i];
                if (collider == null)
                {
                    host.Log.Warn("Scene Units 第 " + i + " 项为空，已跳过。");
                    continue;
                }

                GameplayTagMask tags = GameplayTagMask.Empty;
                if (i == 0 && _firstUnitHasShield)
                {
                    tags = tags.Add(GameplayTags.UnitShieldActive);
                }

                BindUnit(collider, DemoUnitRadius, DemoUnitArmorLevel, DemoUnitHp, tags);
            }
        }

        /// <summary>打印当天签到状态；只读 Presenter 组好的 7 格，不在适配层重写判格规则。</summary>
        private static void LogSignInSlots(AIFrameworkUnityHost host)
        {
            if (!host.SignInAvailable)
            {
                host.Log.Error("签到切片不可用（子表缺失或损坏），跳过签到演示。");
                return;
            }

            // Refresh 会重组 7 格并推给视图；视图是空实现，只记条数并留快照。
            host.SignInPresenter.Refresh();

            SignInHandler signIn = host.SignIn;
            var builder = new StringBuilder(128);
            builder.Append("[AIFramework] 今天天序号=");
            builder.Append(signIn.TodayDayIndex);
            builder.Append("，周期第 1 天=");
            builder.Append(signIn.Snapshot.CycleStartDayIndex);
            builder.Append("，本周期已领=");
            builder.Append(signIn.Snapshot.ClaimedCount);
            host.Log.Info(builder.ToString());

            IReadOnlyList<SignInSlotViewData> slots = host.SignInSlots;
            builder.Length = 0;
            builder.Append("[AIFramework] 签到 7 格：");
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append('D');
                builder.Append(slots[i].DayIndex);
                builder.Append('=');
                builder.Append(slots[i].State);
            }

            host.Log.Info(builder.ToString());
        }

        /// <summary>结算并打印一次对局结果；只在 <c>LateUpdate</c> 里被调用。</summary>
        private void CollectAndLogResult()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || !host.Sessions.HasActiveSession)
            {
                return;
            }

            GameplayResult result = host.Sessions.EndAndCollect();

            // 一局结束是"事实"，广播给任务/活动/成就类切片；它们不需要知道这是哪个内核。
            host.PublishGameFinished(GameplayKernelType.Realtime, _activeLevelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            IRealtimeSandbox? sandbox = _activeSandbox;
            int reflected = sandbox != null ? sandbox.ReflectedCount : 0;
            int interrupted = sandbox != null ? sandbox.InterruptedCount : 0;
            _activeSandbox = null;

            host.Log.Info("[Realtime] 结算：胜负=" + result.IsVictory
                + "，推演帧数=" + result.StepCount
                + "，存活单位=" + result.RemainingUnits
                + "，反射次数=" + reflected
                + "，打断次数=" + interrupted);
        }
    }
}
