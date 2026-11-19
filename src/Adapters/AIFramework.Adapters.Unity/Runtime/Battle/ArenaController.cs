#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Core.Replay;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 实时竞技场场景控制器：<b>三波敌人的推进全部编排在适配层，内核每次只负责一场沙盒</b>。
    /// <para>
    /// <b>为什么"一波一局"而不是"一局三波"</b>：<c>Kernel.Realtime</c> 的 <c>IsFinished</c> 语义是
    /// "任意单位 HP &lt;= 0 即整场结束"（首个阵亡即收场，死掉的槽位不会被回收），而 <c>Step</c> 在结束之后是空操作、
    /// <c>CollectResult</c> 只能取一次。也就是说单场沙盒里观察不到"第二个阵亡"这个事件，
    /// 三波不可能塞进同一场会话。所以波次序列由本类持有：每波
    /// <c>Release</c> 旧会话 → <c>Begin(Realtime, seed + 波次)</c> → 布场 → 逐帧 <c>Step</c>，
    /// 只有<b>终结那波</b>用 <c>EndAndCollect</c> 结算（一场竞技场只广播一次 <c>GameFinishedEvent</c>、只发一次战利品）。
    /// </para>
    /// <para>
    /// <b>为什么只认 Contracts 接口</b>：本类不引用 <c>AIFramework.Kernel.Realtime</c> 的任何类型，
    /// 布场走 <see cref="IRealtimeSandbox"/>，读状态走 <see cref="IUnitWorldReadView"/> /
    /// <see cref="IProjectileWorldReadView"/>（都用 <c>session as</c> 转），
    /// 因此换内核实现不需要改本文件。
    /// </para>
    /// <para>
    /// <b>谁死谁活怎么判</b>：视图里没有阵营字段，主角靠 <see cref="GameplayTags.UnitShieldActive"/>
    /// 标签识别（契约级判定，不认"0 号槽位"这种内核实现细节），其余单位一律按敌人统计。
    /// 判定优先级：主角 HP 归零 → 失败；存活敌人归零 → 推进波次；总帧数触顶 → 兜底判负；
    /// 内核先收场（首杀即结束）→ 本波按清空推进。
    /// </para>
    /// <para>
    /// <b>场景表现与碰撞体</b>：只做数据布场的话，场上没有任何 Unity 碰撞体，而装配层的
    /// <c>UnitySpatialQuery</c> 走 <c>Physics.OverlapSphereNonAlloc</c> + "碰撞体 → 句柄"映射，
    /// 没登记过的物体一律查不到——真机表现就是"飞行物穿过去、既不反射也不掉血"。
    /// 所以布场时每个单位/飞行物都经 <see cref="RealtimeSceneBinder"/> 变成<b>已登记碰撞体</b>的图元，
    /// 每个逻辑步 <c>Step</c> 之后同步一次 Transform；换波前先 <c>Dispose</c> 旧绑定再重建
    /// （新会话的句柄从 0 号槽位重新发放，旧映射留着会把新单位串成旧单位）。
    /// 碰撞体尺寸直接取布场时给内核的半径（胶囊/球体缩放 = 半径 × 2），位置与内核一致地落在
    /// X-Z 平面（y = 0）。
    /// </para>
    /// <para>
    /// <b>物理帧时序（真机需知）</b>：工程设置里 <c>Physics.autoSyncTransforms</c> 是关的，
    /// 所以每次写完 Transform 都必须显式 <c>Physics.SyncTransforms()</c>：逐帧由
    /// <see cref="RealtimeSceneBinder.Sync"/> 负责，布场当帧由 <see cref="DeployWave"/> 末尾兜底。
    /// 少了布场这一次同步，新建碰撞体会停在创建位姿，首帧空间查询会读到"所有碰撞体挤在原点"；
    /// 更关键的是，录制局每波布场后会跨一次帧边界，回放局则在同一帧里连续布场 + <c>Step</c>，
    /// 两边 PhysX 位姿不同就会让回放从换波首帧开始假不一致。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArenaController : MonoBehaviour
    {
        /// <summary>总波次：三波清完即胜利。</summary>
        private const int Waves = 3;

        /// <summary>竞技场总逻辑帧上限（三波共用），到顶即兜底判负，防"一局永远打不完"。</summary>
        private const int MaxTotalFrames = 1200;

        /// <summary>回放帧数硬上限：与 <see cref="ArenaRecorder"/> 默认容量一致，保证一次回放在有限时间内结束。</summary>
        private const int MaxReplayFrames = 2400;

        /// <summary>步进兜底值：字段被 Inspector 改成 0 或负数时用它，避免时间轴停摆。</summary>
        private const float FallbackStepSeconds = 1f / 60f;

        /// <summary>主角判定半径；护盾反射阶段的采样圈是它 + 内核自己的 padding。</summary>
        private const float ProtagonistRadius = 1f;

        /// <summary>主角霸体等级：高到不会被普通的敌方飞行物打断（打断只影响控制位，不影响血量）。</summary>
        private const int ProtagonistArmorLevel = 5;

        /// <summary>主角生命值：刻意给足，撑过"护盾正常反射"的一整场；但在完全没有反射时会被磨死。</summary>
        private const int ProtagonistHp = 60;

        /// <summary>敌人判定半径。</summary>
        private const float EnemyRadius = 0.35f;

        /// <summary>敌人霸体等级。</summary>
        private const int EnemyArmorLevel = 0;

        /// <summary>敌人生命值：低于一发飞行物伤害，被反射回来的一发即阵亡（"清波"要能在一帧内发生）。</summary>
        private const int EnemyHp = 10;

        /// <summary>
        /// 敌人站位圆半径。<b>同一波的所有敌人等距</b>，这是"同时出膛 → 同时进护盾圈 → 同时被反射 →
        /// 同帧阵亡"的前提；只有同帧全灭，"当前波敌人全部死亡"才有机会在第一次收场前被观察到。
        /// </summary>
        private const float EnemyRingDistance = 4f;

        /// <summary>敌方飞行物伤害：够一击打死敌人（14 ≥ 10），也够在没有反射时逐步磨死主角。</summary>
        private const int EnemyProjectileDamage = 14;

        /// <summary>敌方飞行物速度（世界单位/秒）。</summary>
        private const float EnemyProjectileSpeed = 8f;

        /// <summary>出膛点相对敌人中心的外扩距离，避免出生当帧就与自己扫掠相交。</summary>
        private const float EnemyMuzzleOffset = 0.2f;

        /// <summary>
        /// 飞行物的判定半径（世界单位），<b>必须与 <c>Kernel.Realtime</c> 的私有常量
        /// <c>ProjectileRadius</c>（0.1f）保持一致</b>：内核用"单位半径 + 本半径"做精确命中判定，
        /// 场景侧的球体碰撞体用同一个值缩放，两边一旦不一致，物理查询的命中圈就会与内核判定错位。
        /// </summary>
        private const float ProjectileRadius = 0.1f;

        /// <summary>第一波序号（<see cref="CurrentWave"/> 从这个值开始）。</summary>
        private const int FirstWave = 1;

        /// <summary>竞技场皮肤（渲染状态行与重开按钮，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private ArenaHud hud = null!;

        /// <summary>关卡 ID，进门前快照的一部分。</summary>
        [SerializeField] private int levelId = 1;

        /// <summary>随机种子：固定值让"同一波同一布场"可复现。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>每个逻辑步的固定时长（秒）。默认 1/60 与强实时内核的帧长一致，便于对拍。</summary>
        [SerializeField] private float fixedStepSeconds = 1f / 60f;

        /// <summary>宿主（总线/会话/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>当前这一波的会话；换波或结算后置空。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的布场端口；内核未实现该接口时为 <c>null</c>。</summary>
        private IRealtimeSandbox? _sandbox;

        /// <summary>会话强转出来的单位只读视图；内核未实现该接口时为 <c>null</c>。</summary>
        private IUnitWorldReadView? _units;

        /// <summary>会话强转出来的飞行物只读视图；内核未实现该接口时为 <c>null</c>。</summary>
        private IProjectileWorldReadView? _projectiles;

        /// <summary>
        /// 本波的场景表现绑定器：把布场出来的单位/飞行物变成已登记碰撞体的场景对象。
        /// <b>一场一换</b>——它构造期抓住本会话的两个只读视图，且 <c>Dispose</c> 是一次性的，
        /// 换波必须丢弃重建。视图缺失（内核未实现）时为 <c>null</c>，此时退回纯数据布场。
        /// </summary>
        private RealtimeSceneBinder? _binder;

        /// <summary>本波主角句柄（超时兜底时要靠它定位主角）。</summary>
        private UnitHandle _protagonist;

        /// <summary>本局的回放脚本（种子 + 每波布场参数）；开局时生成，重开再生成。</summary>
        private ArenaReplayScript? _script;

        /// <summary>本局的录制器：每帧一个状态指纹；结算后保留，供"回放上一局"使用。</summary>
        private ArenaRecorder? _recorder;

        /// <summary>回放是否正在同步执行（期间禁止 <see cref="Update"/> 再推进真实战局）。</summary>
        private bool _replaying;

        /// <summary>最近一次回放是否逐帧一致（未回放或回放中断时为 <c>false</c>）。</summary>
        private bool _lastReplayMatched;

        /// <summary>最近一次回放已比对的帧数（未回放时为 0）。</summary>
        private int _lastReplayFrames;

        /// <summary>当前波次；结束后归 <see cref="FirstWave"/> 之外的 0。</summary>
        private int _wave;

        /// <summary>三波累计推进的逻辑帧数（兜底上限按它算，而不是按单场帧号）。</summary>
        private int _framesTotal;

        /// <summary>已结束波次累计的反射次数（当前波的实时值另算，见 <see cref="ReflectedCount"/>）。</summary>
        private int _reflectedTotal;

        /// <summary>本场竞技场是否已结算（<c>EndAndCollect</c> 只允许发生一次）。</summary>
        private bool _finished;

        /// <summary>本场竞技场是否胜利（结算后与内核结果对齐）。</summary>
        private bool _isVictory;

        /// <summary>当前波次，1..3；结束（胜利/失败/超时）后为 0。</summary>
        public int CurrentWave
        {
            get { return _wave; }
        }

        /// <summary>总波次，固定 3。</summary>
        public int TotalWaves
        {
            get { return Waves; }
        }

        /// <summary>本场竞技场是否胜利（未结算时为 <c>false</c>）。</summary>
        public bool IsVictory
        {
            get { return _isVictory; }
        }

        /// <summary>最近一次"回放上一局"是否逐帧一致（未回放过或回放中断时为 <c>false</c>）。</summary>
        public bool LastReplayMatched
        {
            get { return _lastReplayMatched; }
        }

        /// <summary>最近一次"回放上一局"已比对的帧数（未回放时为 0）。</summary>
        public int LastReplayFrames
        {
            get { return _lastReplayFrames; }
        }

        /// <summary>
        /// 累计反射次数（已结束波次 + 当前波实时值）。
        /// <b>为什么是 internal 而不是 public</b>：公开面只放"波次/胜负/回放结果"这类场景契约，
        /// 这是给同程序集的 <see cref="ArenaHud"/> 拼状态行用的诊断值，不该出现在场景对外契约里。
        /// </summary>
        internal int ReflectedCount
        {
            get
            {
                IRealtimeSandbox? sandbox = _sandbox;
                return _reflectedTotal + (sandbox != null ? sandbox.ReflectedCount : 0);
            }
        }

        /// <summary>
        /// 建宿主 → 挂 HUD 事件 → 布第一波。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：
        /// 重开按钮的订阅要在玩家可能点下去之前挂好，第一波的布场也要在第一次 <c>Update</c> 之前完成。
        /// </summary>
        private void Awake()
        {
            AIFrameworkUnityHost host;
            try
            {
                host = new AIFrameworkUnityHost();
                host.Initialize();
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is ArgumentException
                || exception is IOException)
            {
                // 宿主装配失败（存档目录不可写等）时不能静默：整场竞技场都没法玩了。
                Debug.LogError("[Arena] 宿主装配失败：" + exception.Message);
                _finished = true;
                return;
            }

            _host = host;

            ArenaHud? view = hud;
            if (view != null)
            {
                view.OnRestartClicked += HandleRestartClicked;
                view.OnReplayClicked += HandleReplayClicked;
            }
            else
            {
                host.Log.Error("[Arena] hud 未注入，竞技场没有任何状态显示与重开入口。");
            }

            StartArena();
        }

        /// <summary>
        /// 每帧推进一个固定逻辑步，然后判定波次/胜负并刷新 HUD。
        /// <b>固定步长而不是 <c>Time.deltaTime</c></b>：内核按固定帧推进，胜负不该随帧率漂移。
        /// <para>
        /// <b>Step 之后必须 Sync</b>：内核这一帧改过位置（飞行物积分、位移仲裁），
        /// 下一次 <c>Step</c> 里的空间查询才会读场景碰撞体，所以同步要落在两次 <c>Step</c> 之间。
        /// </para>
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            if (session == null || _finished || _replaying)
            {
                return;
            }

            session.Step(EffectiveStepSeconds());
            _framesTotal++;

            RecordFrame();

            RealtimeSceneBinder? binder = _binder;
            if (binder != null)
            {
                binder.Sync();
            }

            Tick();

            if (_finished)
            {
                return;
            }

            RenderHud();
        }

        /// <summary>
        /// 一步之后的竞技场裁决，优先级：主角阵亡 → 敌人清空 → 内核先收场 → 总帧数触顶。
        /// <para>
        /// <b>为什么主角阵亡排在最前</b>：主角与最后一个敌人同帧阵亡时必须判负（把"敌人清空"放前面会把失败读成胜利）。
        /// </para>
        /// <para>
        /// <b>为什么"内核先收场"排在"帧数触顶"之前</b>：内核自己也有一条 1200 帧的收场线，
        /// 而它的 <c>Step</c> 在收场后是空操作。若先判帧数触顶，兜底的"把主角打到 0 血"会写进一个已经停摆的世界，
        /// <c>EndAndCollect</c> 反而报出"主角还活着"的胜利。先让内核收场这一波，下一帧在<b>新会话</b>里再兜底判负，
        /// 结算才和竞技场判定一致。
        /// </para>
        /// </summary>
        private void Tick()
        {
            IGameplaySession? session = _session;
            if (session == null)
            {
                return;
            }

            ReadField(out int protagonistHp, out int aliveEnemies);

            if (protagonistHp == 0)
            {
                Finish(false);
                return;
            }

            if (protagonistHp > 0 && aliveEnemies == 0)
            {
                AdvanceWave();
                return;
            }

            if (session.IsFinished)
            {
                // 内核单场已收场（首杀即结束，或本场撞上内核自己的 1200 帧线）：
                // 本波按"清空"推进，并留下一条可排查的日志。
                _host?.Log.Warn("[Arena] 第 " + _wave + " 波由内核提前收场（仍存活敌人=" + aliveEnemies
                    + "，已用帧=" + _framesTotal + "），按清空推进。");
                AdvanceWave();
                return;
            }

            if (_framesTotal >= MaxTotalFrames)
            {
                // 兜底：打不完就判负。先把主角打到 0 血再结算，保证 EndAndCollect 读到的胜负与这里一致。
                ForceDefeat();
                Finish(false);
            }
        }

        /// <summary>
        /// 从只读视图里数出战场快照。主角按护盾标签识别（契约判定，不认槽位下标），
        /// 其余单位按敌人统计且只数存活者。
        /// </summary>
        /// <param name="protagonistHp">主角当前 HP；视图不可读或找不到主角时为 -1。</param>
        /// <param name="aliveEnemies">存活（HP &gt; 0）的敌人数量。</param>
        private void ReadField(out int protagonistHp, out int aliveEnemies)
        {
            protagonistHp = -1;
            aliveEnemies = 0;

            IUnitWorldReadView? units = _units;
            if (units == null)
            {
                // 内核没暴露单位视图：波次判定退化成"内核收场即换波"，HUD 也不会有状态。
                return;
            }

            int count = units.Count;
            for (int i = 0; i < count; i++)
            {
                UnitReadView view = units[i];
                if (view.Tags.Has(GameplayTags.UnitShieldActive))
                {
                    if (protagonistHp < 0)
                    {
                        protagonistHp = view.Hp;
                    }

                    continue;
                }

                if (view.Hp > 0)
                {
                    aliveEnemies++;
                }
            }
        }

        /// <summary>
        /// 推进到下一波；已是最后一波则判胜。
        /// 换波前把本波反射次数折进累计值——旧会话换掉后就再也读不到它了。
        /// </summary>
        private void AdvanceWave()
        {
            IRealtimeSandbox? sandbox = _sandbox;
            if (sandbox != null)
            {
                _reflectedTotal += sandbox.ReflectedCount;
            }

            if (_wave >= Waves)
            {
                Finish(true);
                return;
            }

            _wave++;
            StartWave(_wave);
        }

        /// <summary>
        /// 开一波：交还旧会话 → <c>Begin(Realtime)</c> → 取三个端口 → 布场。
        /// 初始化失败的路径一律把竞技场停在"未开局"，绝不用错的状态继续推演。
        /// </summary>
        /// <param name="wave">要开的波次（1..3）。</param>
        private void StartWave(int wave)
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            // 上一波的场景对象与碰撞体必须先撤：Dispose 会逐个 Unregister 并销毁 GameObject。
            // 放在 Release 之前是刻意的——绑定器只依赖构造期抓住的旧视图，先撤干净再换会话，
            // 就不会出现"旧碰撞体仍挂在 host.Spatial 上、新单位却复用了同一个槽位句柄"的串号。
            DisposeBinder();

            // 上一波已经收场（或本波放弃）：先交还会话，否则 Begin 会抛"已有活跃会话"。
            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            IGameplaySession session;
            WaveSpec spec = WaveSpecFor(wave);
            try
            {
                var init = new GameplayInitData(levelId, randomSeed + wave, spec.EnemyCount + 1);
                session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Arena] 第 " + wave + " 波开局失败（宿主工厂未接 Realtime 分支？）：" + exception.Message);
                _finished = true;
                _wave = 0;
                return;
            }

            IRealtimeSandbox? sandbox = session as IRealtimeSandbox;
            if (sandbox == null)
            {
                // 布不了场就绝不能让 Update 去空转这一场：交还会话并停掉竞技场。
                host.Log.Error("[Arena] 会话未实现 IRealtimeSandbox，无法布场（本场放弃）。");
                host.Sessions.Release();
                _finished = true;
                _wave = 0;
                return;
            }

            _session = session;
            _sandbox = sandbox;
            _units = session as IUnitWorldReadView;
            _projectiles = session as IProjectileWorldReadView;

            if (_units == null || _projectiles == null)
            {
                host.Log.Warn("[Arena] 会话未完整实现只读视图，场景里不会创建任何碰撞体（HUD 也无法显示战场状态）。");
            }

            // 布场前先把绑定器建好：AddUnit/AddProjectile 要在 SpawnUnit/SpawnProjectile 之后立刻调用，
            // 句柄只有在刚生成时才能与内核里的那一份对上。
            _binder = CreateBinder(host);
            _protagonist = DeployWave(sandbox, wave, _binder, in spec);

            host.Log.Info("[Arena] 第 " + wave + "/" + Waves + " 波开战：主角 HP=" + ProtagonistHp
                + "，敌人=" + spec.EnemyCount + "，已用帧=" + _framesTotal);
        }

        /// <summary>
        /// 布场：主角先放，再按等角圆摆这一波的敌人，每个敌人朝主角发射一枚飞行物；
        /// 每生成一个就立刻交给 <see cref="RealtimeSceneBinder"/> 建碰撞体并登记句柄。
        /// <para>
        /// <b>布场顺序即语义</b>：内核约定"首个 <c>SpawnUnit</c> 的单位是主角"，胜负读的就是它，
        /// 所以主角必须先于敌人放入，否则"主角阵亡"会被读成"敌人阵亡"。
        /// </para>
        /// </summary>
        /// <param name="sandbox">会话的布场端口。</param>
        /// <param name="wave">当前波次。</param>
        /// <param name="binder">场景绑定器；视图缺失（内核未实现只读视图）时为 <c>null</c>，此时只做数据布场。</param>
        /// <param name="spec">本波参数（数量与半径来自回放脚本，回放时才能逐位复现）。</param>
        /// <returns>主角句柄；槽位耗尽时为无效句柄。</returns>
        private UnitHandle DeployWave(
            IRealtimeSandbox sandbox,
            int wave,
            RealtimeSceneBinder? binder,
            in WaveSpec spec)
        {
            GameplayTagMask shield = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
            UnitHandle protagonist = sandbox.SpawnUnit(
                Vec2.Zero, ProtagonistRadius, ProtagonistArmorLevel, ProtagonistHp, shield);

            if (!protagonist.IsValid)
            {
                _host?.Log.Error("[Arena] 主角槽位耗尽，第 " + wave + " 波布场失败（本波不会有任何战斗）。");
                return default;
            }

            if (binder != null)
            {
                binder.AddUnit(protagonist, Vec2.Zero, ProtagonistRadius, "Protagonist");
            }

            int enemies = spec.EnemyCount;
            for (int i = 0; i < enemies; i++)
            {
                Vec2 position = EnemyPosition(i, enemies);
                UnitHandle enemy = sandbox.SpawnUnit(
                    position, spec.Radius, EnemyArmorLevel, EnemyHp, GameplayTagMask.Empty);

                if (!enemy.IsValid)
                {
                    // 槽位耗尽：少放一个敌人，本波照样能打（清空判定按存活数算）。
                    continue;
                }

                if (binder != null)
                {
                    binder.AddUnit(enemy, position, spec.Radius, "Enemy_" + wave + "_" + i);
                }

                // 主角在原点，瞄准方向就是指向圆心的方向。
                Vec2 direction = (-position).Normalized;
                if (direction.SqrLength <= 0f)
                {
                    direction = new Vec2(0f, -1f);
                }

                Vec2 muzzle = position + (direction * (spec.Radius + EnemyMuzzleOffset));
                ProjectileHandle projectile = sandbox.SpawnProjectile(
                    enemy,
                    muzzle,
                    direction * EnemyProjectileSpeed,
                    EnemyProjectileDamage,
                    GameplayTagMask.Empty);

                if (binder != null && projectile.IsValid)
                {
                    binder.AddProjectile(projectile, muzzle, ProjectileRadius, "Projectile_" + wave + "_" + i);
                }
            }

            // 布场当帧显式同步物理位姿：新建碰撞体默认停在创建时的位姿，而工程里
            // Physics.autoSyncTransforms 是关的。若不在第一次 Step 前同步，首帧空间查询会读到
            // "所有碰撞体挤在原点"这种假状态；更关键的是，录制局每波布场后会跨一次帧边界，
            // 回放局则在同一帧里连续布场 + Step，两者的 PhysX 位姿会因此分叉，回放必然假不一致。
            Physics.SyncTransforms();

            return protagonist;
        }

        /// <summary>
        /// 为当前会话重建场景绑定器（调用前必须先 <see cref="DisposeBinder"/>）。
        /// <b>为什么必须每波重建</b>：绑定器在构造期抓住本会话的只读视图，换会话后旧视图全部失效；
        /// 且它的 <c>Dispose</c> 会永久置位，不能复用。
        /// </summary>
        /// <param name="host">宿主（绑定器从这里取空间查询端口）。</param>
        /// <returns>绑定器；会话没有同时实现两个只读视图时为 <c>null</c>。</returns>
        private RealtimeSceneBinder? CreateBinder(AIFrameworkUnityHost host)
        {
            IUnitWorldReadView? units = _units;
            IProjectileWorldReadView? projectiles = _projectiles;
            if (units == null || projectiles == null)
            {
                return null;
            }

            // root 用控制器自身节点：生成物挂在 Arena 下面，Hierarchy 里一眼能看出这一波是谁的。
            return new RealtimeSceneBinder(host, units, projectiles, transform);
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
        /// 超时兜底：把主角打到 0 血，让内核结算出"失败"。
        /// <para>
        /// <b>为什么不直接广播失败</b>：<c>PublishGameFinished</c> 只能报 <c>result.IsVictory</c>，
        /// 而超时时主角往往还活着（内核会判胜）。改血量是唯一能让"超时=失败"与内核结算一致的做法，
        /// 且走的是契约里的 <see cref="IIntentWriter"/> 通道（帧内写意图、仲裁器统一结算），没有绕过管线。
        /// </para>
        /// </summary>
        private void ForceDefeat()
        {
            IGameplaySession? session = _session;
            if (session == null)
            {
                return;
            }

            IIntentWriter? writer = session as IIntentWriter;
            if (writer == null || !_protagonist.IsValid)
            {
                // 拿不到写通道就只能认内核的结算结果（Finish 里会以 EndAndCollect 为准）。
                _host?.Log.Error("[Arena] 超时且无法通过意图通道终结对局，胜负以内核结果为准。");
                return;
            }

            var intent = new DamageIntent(_protagonist, _protagonist, ProtagonistHp * 2, 0);
            writer.Submit(in intent);
            session.Step(0f);
        }

        /// <summary>
        /// 结算本场竞技场：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示胜负。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已胜利但金币还没到账"的中间态）。
        /// </para>
        /// </summary>
        /// <param name="isVictory">竞技场判定：三波清空为 <c>true</c>，其余为 <c>false</c>。</param>
        private void Finish(bool isVictory)
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _finished)
            {
                return;
            }

            // 结算前把当前波的反射次数折进累计值：置空 _sandbox 之后就再也读不到了。
            IRealtimeSandbox? sandbox = _sandbox;
            if (sandbox != null)
            {
                _reflectedTotal += sandbox.ReflectedCount;
            }

            // 先置标记再结算：结算抛异常时不允许每帧重试。
            _finished = true;
            _isVictory = isVictory;
            _wave = 0;
            _session = null;
            _sandbox = null;
            _units = null;
            _projectiles = null;

            GameplayResult result;
            try
            {
                result = host.Sessions.EndAndCollect();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Arena] 结算失败：" + exception.Message);
                return;
            }

            if (result.IsVictory != _isVictory)
            {
                // 唯一可能的分歧来源：超时兜底没能把主角打死。此时以结算报告为准，并显式留痕。
                host.Log.Warn("[Arena] 内核判定(" + result.IsVictory + ")与竞技场判定(" + _isVictory
                    + ")不一致，以 EndAndCollect 为准。");
                _isVictory = result.IsVictory;
            }

            host.PublishGameFinished(GameplayKernelType.Realtime, levelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            ArenaHud? view = hud;
            if (view != null)
            {
                view.ShowResult(result.IsVictory);
            }

            host.Log.Info("[Arena] 结算：胜负=" + result.IsVictory
                + "，总帧=" + _framesTotal
                + "，剩余单位=" + result.RemainingUnits
                + "，反射=" + _reflectedTotal
                + "，战利品条目=" + loots.Count
                + "，录制帧=" + RecorderFrameCount());

            ArenaRecorder? recorder = _recorder;
            if (recorder != null && recorder.DroppedFrames)
            {
                // 容量写满只影响回放尾段，不影响本局结算；显式留痕，避免"回放莫名短一截"。
                host.Log.Warn("[Arena] 录像帧容量耗尽，尾部帧已丢弃（回放只能比对已录部分）。");
            }
        }

        /// <summary>退订 HUD 事件并释放宿主（<c>Dispose</c> 会丢弃未结算的会话引用）。</summary>
        private void OnDestroy()
        {
            ArenaHud? view = hud;
            if (view != null)
            {
                view.OnRestartClicked -= HandleRestartClicked;
                view.OnReplayClicked -= HandleReplayClicked;
            }

            _session = null;
            _sandbox = null;
            _units = null;
            _projectiles = null;
            _finished = true;

            // 先撤掉本场生成的对象与碰撞体登记，再释放宿主；两步都不能省，
            // 否则重进场景时上一场的映射会一直挂在宿主级空间查询里。
            DisposeBinder();

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>重开按钮：只有已经收场时才允许重来（打一半点重开会让结算报告对不上）。</summary>
        private void HandleRestartClicked()
        {
            if (!_finished)
            {
                return;
            }

            Restart();
        }

        /// <summary>
        /// 回放按钮：只有收场后且确实录到过帧才允许点。
        /// 回放语义（重开一局、逐帧比对指纹）由控制器决定，HUD 不做规则判断。
        /// </summary>
        private void HandleReplayClicked()
        {
            ArenaRecorder? recorder = _recorder;
            if (!_finished || recorder == null || recorder.FrameCount <= 0)
            {
                return;
            }

            ReplayLastRun();
        }

        /// <summary>把竞技场复位到第一波（计数器、胜负标记、反射累计一起清）。</summary>
        private void Restart()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            _finished = false;
            _isVictory = false;
            _wave = FirstWave;
            _framesTotal = 0;
            _reflectedTotal = 0;
            _protagonist = default;

            // 每局一份脚本 + 一份录制器：重开不能复用上一局的录制，否则指纹会接在旧录像尾部。
            _script = CreateScript(randomSeed);
            _recorder = new ArenaRecorder(_script);

            StartWave(FirstWave);
            RenderHud();

            host.Log.Info("[Arena] 重开：回到第 " + FirstWave + "/" + Waves + " 波。");
        }

        /// <summary>竞技场开局：复位计数并布第一波。</summary>
        private void StartArena()
        {
            _finished = false;
            _isVictory = false;
            _wave = FirstWave;
            _framesTotal = 0;
            _reflectedTotal = 0;
            _protagonist = default;

            // 每局一份脚本 + 一份录制器：脚本在构造期复制波次参数，录制器一次性预分配指纹数组。
            _script = CreateScript(randomSeed);
            _recorder = new ArenaRecorder(_script);

            StartWave(FirstWave);
            RenderHud();
        }

        /// <summary>
        /// 回放上一局：用录制的脚本重开一局、逐帧重推并比对状态指纹。
        /// <para>
        /// <b>为什么同步跑完而不是分帧</b>：回放需要反复建/删碰撞体并重推内核；分帧会把"回放世界"
        /// 与真实战局混进同一个 <see cref="Update"/>，状态机立刻变得不可推理。同步执行把整个回放
        /// 限制在一次调用内（最多 <see cref="MaxReplayFrames"/> 帧），结束后再回到正常状态。
        /// </para>
        /// <para>
        /// <b>局中调用</b>：若当前这一局还没结算，则只回放"已录到的前缀"，回放结束后自动重开一局；
        /// 正常入口（HUD 回放按钮）只在结算后开放。
        /// </para>
        /// </summary>
        /// <returns>回放是否完整且逐帧一致；没有可回放的录制时返回 <c>false</c>。</returns>
        public bool ReplayLastRun()
        {
            AIFrameworkUnityHost? host = _host;
            ArenaRecorder? recorder = _recorder;
            if (host == null || recorder == null || recorder.FrameCount <= 0 || _replaying)
            {
                return false;
            }

            // 回放开始前记下"这一局是否已结算"：决定回放结束后是留在结算态还是重开一局。
            bool wasFinished = _finished;
            _replaying = true;

            // 回放要独占会话宿主：先撤掉正在推进的这一局（会话引用由 finally 兜底释放）。
            // 局中调用时这一局会被丢弃，但它本来就只用于"看回放"，不能同帧并行两个战局。
            DisposeBinder();
            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            _session = null;
            _sandbox = null;
            _units = null;
            _projectiles = null;
            _wave = 0;
            _finished = true;

            ArenaReplayVerifier verifier = new ArenaReplayVerifier(recorder);
            bool completed = false;
            try
            {
                completed = RunReplay(host, recorder.Script, verifier, recorder.FrameCount);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Arena] 回放中断（会话不可用）：" + exception.Message);
            }
            finally
            {
                DisposeBinder();
                if (host.Sessions.HasActiveSession)
                {
                    host.Sessions.Release();
                }

                _session = null;
                _sandbox = null;
                _units = null;
                _projectiles = null;
                _replaying = false;
            }

            _lastReplayFrames = verifier.ComparedFrames;
            _lastReplayMatched = completed && verifier.IsComplete && verifier.IsMatch;

            ArenaHud? view = hud;
            if (view != null)
            {
                view.ShowReplayResult(_lastReplayMatched, verifier.ComparedFrames, verifier.FirstMismatchFrame);
            }

            host.Log.Info("[Arena] 回放" + (_lastReplayMatched ? "一致" : "不一致")
                + "：比对帧=" + verifier.ComparedFrames
                + "，录制帧=" + recorder.FrameCount
                + "，首个不匹配=" + verifier.FirstMismatchFrame);

            if (!wasFinished)
            {
                // 局中回放只是读取已录前缀；结束后重开一局，让竞技场回到正常推进状态。
                StartArena();
            }

            return _lastReplayMatched;
        }

        /// <summary>
        /// 回放主循环：按录制帧数逐帧推进，先推一帧、再比指纹、再同步场景、最后按与
        /// <see cref="Tick"/> 完全相同的优先级决定是否换波。
        /// <para>
        /// <b>为什么要照抄 Tick 的换波判定</b>：录制只存指纹与"连续帧序列"，不存波次边界；
        /// 回放必须靠同一套状态判定在<b>同一帧</b>换波，否则下一帧就会在新旧两个战场上比对。
        /// 判定条件与顺序一旦与 <see cref="Tick"/> 不一致，回放会立刻在换波帧出现假不一致。
        /// </para>
        /// </summary>
        /// <param name="host">宿主（会话与空间查询的唯一入口）。</param>
        /// <param name="script">录制的布场脚本。</param>
        /// <param name="verifier">逐帧比对器。</param>
        /// <param name="recordedFrames">录制帧数（回放最多推进这么多帧）。</param>
        /// <returns>是否完整比对完录制帧数。</returns>
        private bool RunReplay(
            AIFrameworkUnityHost host,
            ArenaReplayScript script,
            ArenaReplayVerifier verifier,
            int recordedFrames)
        {
            if (script.WaveCount <= 0 || recordedFrames <= 0)
            {
                return false;
            }

            int replayLimit = recordedFrames < MaxReplayFrames ? recordedFrames : MaxReplayFrames;
            int wave = FirstWave;
            if (!BeginReplayWave(host, script, wave))
            {
                return false;
            }

            float stepSeconds = EffectiveStepSeconds();
            int framesTotal = 0;

            for (int frame = 0; frame < replayLimit; frame++)
            {
                IGameplaySession? session = _session;
                IUnitWorldReadView? units = _units;
                IProjectileWorldReadView? projectiles = _projectiles;
                if (session == null || units == null || projectiles == null)
                {
                    return false;
                }

                session.Step(stepSeconds);
                framesTotal++;

                verifier.PushFrame(StateFingerprint.Of(units, projectiles));

                RealtimeSceneBinder? binder = _binder;
                if (binder != null)
                {
                    binder.Sync();
                }

                ReadField(out int protagonistHp, out int aliveEnemies);

                // 与 Tick 同序：主角阵亡 → 敌人清空 → 内核先收场 → 总帧数触顶。
                if (protagonistHp == 0)
                {
                    return frame + 1 == recordedFrames && verifier.IsComplete;
                }

                bool waveCleared = protagonistHp > 0 && aliveEnemies == 0;
                if (!waveCleared && !session.IsFinished)
                {
                    if (framesTotal >= MaxTotalFrames)
                    {
                        // 原局在这一帧之后 ForceDefeat + Finish，录制到此为止。
                        return frame + 1 == recordedFrames && verifier.IsComplete;
                    }

                    continue;
                }

                if (wave >= script.WaveCount)
                {
                    // 最后一波已清空（或内核收场）：原局此时结算，录制帧数必须正好到此为止。
                    return frame + 1 == recordedFrames && verifier.IsComplete;
                }

                // 录制前缀正好停在换波这一帧：不需要再开下一波（开了也会立刻被释放）。
                if (frame + 1 >= recordedFrames)
                {
                    return verifier.IsComplete;
                }

                DisposeBinder();
                host.Sessions.Release();
                _session = null;
                _sandbox = null;
                _units = null;
                _projectiles = null;

                wave++;
                if (!BeginReplayWave(host, script, wave))
                {
                    return false;
                }
            }

            return verifier.IsComplete;
        }

        /// <summary>
        /// 为回放开一波：<c>Begin</c> → 取端口 → 建绑定器 → 按脚本布场。
        /// <b>回放也必须建绑定器</b>：护盾反射阶段查的是装配层的空间查询（Physics 碰撞体），
        /// 只做数据布场会让回放局在第 0 帧就与录制局分叉。
        /// </summary>
        /// <param name="host">宿主。</param>
        /// <param name="script">布场脚本。</param>
        /// <param name="wave">要开的波次（1..script.WaveCount）。</param>
        /// <returns>是否成功开波并布场。</returns>
        private bool BeginReplayWave(AIFrameworkUnityHost host, ArenaReplayScript script, int wave)
        {
            int index = wave - FirstWave;
            if (index < 0 || index >= script.WaveCount)
            {
                return false;
            }

            WaveSpec spec = script.Waves[index];
            IGameplaySession session;
            try
            {
                var init = new GameplayInitData(levelId, script.RandomSeed + wave, spec.EnemyCount + 1);
                session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Arena] 回放第 " + wave + " 波开局失败：" + exception.Message);
                return false;
            }

            IRealtimeSandbox? sandbox = session as IRealtimeSandbox;
            IUnitWorldReadView? units = session as IUnitWorldReadView;
            IProjectileWorldReadView? projectiles = session as IProjectileWorldReadView;
            if (sandbox == null || units == null || projectiles == null)
            {
                host.Log.Error("[Arena] 回放所需端口缺失（IRealtimeSandbox / 只读视图），无法逐帧比对。");
                host.Sessions.Release();
                return false;
            }

            _session = session;
            _sandbox = sandbox;
            _units = units;
            _projectiles = projectiles;
            _binder = new RealtimeSceneBinder(host, units, projectiles, transform);
            _protagonist = DeployWave(sandbox, wave, _binder, in spec);

            return _protagonist.IsValid;
        }

        /// <summary>
        /// 记录当前帧的状态指纹。<b>只读视图缺失时跳过</b>：没有视图就没有可比较的状态，
        /// 与其记一个假指纹，不如让录制器保持 0 帧（回放入口会直接返回 <c>false</c>）。
        /// </summary>
        private void RecordFrame()
        {
            IUnitWorldReadView? units = _units;
            IProjectileWorldReadView? projectiles = _projectiles;
            ArenaRecorder? recorder = _recorder;
            if (units == null || projectiles == null || recorder == null)
            {
                return;
            }

            recorder.RecordFrame(StateFingerprint.Of(units, projectiles));
        }

        /// <summary>本局录到的帧数（录制器未就绪时为 0）。</summary>
        private int RecorderFrameCount()
        {
            ArenaRecorder? recorder = _recorder;
            return recorder != null ? recorder.FrameCount : 0;
        }

        /// <summary>
        /// 生成本局的回放脚本：种子 + 每波敌人数量/半径。
        /// <b>位置不放进脚本</b>：它是 <c>(序号, 数量)</c> 的纯函数，同一份代码必然算出同一组坐标。
        /// </summary>
        private static ArenaReplayScript CreateScript(int seed)
        {
            WaveSpec[] waves = new WaveSpec[Waves];
            for (int i = 0; i < Waves; i++)
            {
                int wave = FirstWave + i;
                waves[i] = new WaveSpec(EnemyCountForWave(wave), EnemyRadius);
            }

            return new ArenaReplayScript(seed, Waves, waves);
        }

        /// <summary>取第 <paramref name="wave"/> 波的布场参数；脚本缺失时退回代码内常量。</summary>
        private WaveSpec WaveSpecFor(int wave)
        {
            ArenaReplayScript? script = _script;
            int index = wave - FirstWave;
            if (script != null && index >= 0 && index < script.WaveCount)
            {
                return script.Waves[index];
            }

            return new WaveSpec(EnemyCountForWave(wave), EnemyRadius);
        }

        /// <summary>渲染一次 HUD（会话可能已被置空，故先取本地副本）。</summary>
        private void RenderHud()
        {
            ArenaHud? view = hud;
            if (view != null)
            {
                view.Render(this, _units, _projectiles);
            }
        }

        /// <summary>第 <paramref name="wave"/> 波的敌人数量：首波 2 个，之后 3 个（递增压力，仍在 2~3 之间）。</summary>
        /// <param name="wave">波次（1..3）。</param>
        /// <returns>该波的敌人数量。</returns>
        private static int EnemyCountForWave(int wave)
        {
            return wave >= 2 ? 3 : 2;
        }

        /// <summary>
        /// 第 <paramref name="index"/> 个敌人的站位：以主角为圆心的等距圆上等角分布，从正上方起摆。
        /// 等距是刻意的——见 <see cref="EnemyRingDistance"/>。
        /// </summary>
        /// <param name="index">敌人序号。</param>
        /// <param name="count">本波敌人总数。</param>
        /// <returns>敌人的世界坐标（战斗平面）。</returns>
        private static Vec2 EnemyPosition(int index, int count)
        {
            if (count <= 0)
            {
                return new Vec2(0f, EnemyRingDistance);
            }

            double angle = (Math.PI * 2.0 * index / count) + (Math.PI * 0.5);
            return new Vec2(
                (float)(Math.Cos(angle) * EnemyRingDistance),
                (float)(Math.Sin(angle) * EnemyRingDistance));
        }

        /// <summary>出步间隔：字段被改成非法值时退回 1/60 秒。</summary>
        /// <returns>本帧要推进的逻辑步长（秒）。</returns>
        private float EffectiveStepSeconds()
        {
            float interval = fixedStepSeconds;
            return interval > 0f ? interval : FallbackStepSeconds;
        }
    }
}
