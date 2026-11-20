#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ai;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Ai;
using AIFramework.Core.Replay;
using AIFramework.Core.Services;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 实时竞技场场景控制器：<b>逐波敌人的推进全部编排在适配层，内核每次只负责一场沙盒</b>。
    /// <para>
    /// <b>波次从哪来</b>：开局前先读 <c>TbArenaWave</c>（<see cref="LoadWaveTable"/>），行数就是总波数、
    /// 每行的数量/血量/速度决定这一波的布场参数；表缺失或非法时打 Warn 并退回内置 3 波。
    /// 波次曲线的合法性由工具侧 <c>tools/wavecheck</c> 在构建期把关，运行期只做"布场能用"的最小校验。
    /// </para>
    /// <para>
    /// <b>为什么"一波一局"而不是"一局多波"</b>：<c>Kernel.Realtime</c> 的 <c>IsFinished</c> 语义是
    /// "任意单位 HP &lt;= 0 即整场结束"（首个阵亡即收场，死掉的槽位不会被回收），而 <c>Step</c> 在结束之后是空操作、
    /// <c>CollectResult</c> 只能取一次。也就是说单场沙盒里观察不到"第二个阵亡"这个事件，
    /// 多波不可能塞进同一场会话。所以波次序列由本类持有：每波
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
        /// <summary>
        /// 内置回退波数：<c>TbArenaWave</c> 缺失或非法时仍按老的三波跑。
        /// <para>
        /// <b>为什么保留常量而不是"没表就不开局"</b>：竞技场是演示场景，表没打进包时应当仍然能玩；
        /// 但"表没配"和"表读坏了"必须能区分开，所以回退时一律打 Warn（见 <see cref="LoadWaveTable"/>）。
        /// </para>
        /// </summary>
        private const int BuiltInWaves = 3;

        /// <summary>波次表所在目录（相对 StreamingAssets，与宿主/塔防控制器同一约定）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>竞技场总逻辑帧上限（整场共用，不随波数变化），到顶即兜底判负，防"一局永远打不完"。</summary>
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

        /// <summary>
        /// 敌人 AI 追击速度（世界单位/秒）。<b>刻意远低于飞行物速度（8）</b>：
        /// 位移只改变"敌人站在射线上的哪一段"，不改变"反射弹沿原射线飞回"这一命中几何。
        /// </summary>
        private const float EnemyChaseSpeed = 0.6f;

        /// <summary>
        /// 追击停止半径。<b>必须大于护盾反射查询半径（主角半径 1 + padding 0.5 = 1.5）</b>：
        /// 敌人停在这个半径外，反射弹从护盾圈向外飞时必然还会经过它；一旦让它挤进护盾圈内侧，
        /// 自己的反射弹就可能"从它背后飞走"，本波永远清不掉，胜负会被 AI 位移改写。
        /// </summary>
        private const float EnemyChaseStopRadius = 2f;

        /// <summary>
        /// 撤退最远半径。<b>必须远小于飞行物出界回收半径（40）</b>：反射弹速度 8 远高于撤退速度 0.6，
        /// 敌人退到 5.5 之前就会被追上；再远则可能先出界，波次同样清不掉。
        /// </summary>
        private const float EnemyRetreatMaxRadius = 5.5f;

        /// <summary>敌人 AI 的撤退血量百分比（黑板 0..100 口径）。</summary>
        private const int EnemyRetreatHpPercent = 30;

        /// <summary>敌人 AI 诊断日志间隔（逻辑帧）：60 fps 下约每半秒一条，不参与任何战斗判定。</summary>
        private const int EnemyAiLogIntervalFrames = 30;

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

        /// <summary>会话强转出来的意图写通道；内核未实现时为 <c>null</c>（此时只做决策，不落位移）。</summary>
        private IIntentWriter? _intents;

        /// <summary>
        /// 敌人 AI 决策器（纯 C#，包一棵无状态行为树）。<b>全体敌人共用一个实例</b>：
        /// 树本身不存跨帧状态，每帧每个敌人各喂一次黑板；诊断字段保留"最后一个被求值的敌人"。
        /// </summary>
        private ArenaEnemyAi? _enemyAi;

        /// <summary>敌人 AI 诊断日志的帧计数（每 <see cref="EnemyAiLogIntervalFrames"/> 帧输出一条）。</summary>
        private int _enemyAiLogCounter;

        /// <summary>
        /// 本波的场景表现绑定器：把布场出来的单位/飞行物变成已登记碰撞体的场景对象。
        /// <b>一场一换</b>——它构造期抓住本会话的两个只读视图，且 <c>Dispose</c> 是一次性的，
        /// 换波必须丢弃重建。视图缺失（内核未实现）时为 <c>null</c>，此时退回纯数据布场。
        /// </summary>
        private RealtimeSceneBinder? _binder;

        /// <summary>
        /// 本场景的音效服务。<b>宿主暂未公开 <c>Audio</c> 属性</b>，所以这里用宿主已有的
        /// 资源/日志端口自建一条（音源节点挂在控制器下，随场景销毁）；宿主将来补上 <c>Audio</c> 后
        /// 只需要把这一行换成 <c>host.Audio</c>，桥与选择器一行都不用改。
        /// </summary>
        private UnityAudioService? _audio;

        /// <summary>
        /// 音效选择器（纯逻辑，在 Core）。控制器持有它只为把"本局请求了多少条 cue"暴露成
        /// <see cref="AudioCueCount"/>；规则本身一个字都不在这里。
        /// </summary>
        private BattleAudioCueSelector? _audioSelector;

        /// <summary>音效桥：每帧把帧差翻成 cue 再交给 <see cref="_audio"/>。</summary>
        private BattleAudioBridge? _audioBridge;

        /// <summary>
        /// 帧快照采样器：把本波的两个只读视图 + 反射计数拼成 <c>BattleAudioFrame</c>。
        /// <b>换波不用换它</b>：差值口径已把"新会话基线"钳成 0（见 <c>BattleAudioFrameSampler</c> 注释）。
        /// </summary>
        private BattleAudioFrameSampler? _audioSampler;

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

        /// <summary>
        /// 本场总波数：优先取 <c>TbArenaWave</c> 的行数，缺表/非法时为 <see cref="BuiltInWaves"/>。
        /// <b>它只在开局前变一次</b>（<see cref="LoadWaveTable"/>），之后整局只读，回放脚本与它同源。
        /// </summary>
        private int _totalWaves = BuiltInWaves;

        /// <summary>
        /// 表驱动的每波参数；<c>null</c> = 没读到表（回退内置三波）。
        /// <para>
        /// <b>为什么只存"表里写了什么"而不是换算后的 <see cref="WaveSpec"/></b>：布场的唯一真源是回放脚本，
        /// 表在这里只是"生成脚本的原料"。把换算结果也缓存一份，回放局就一定会在"表被改过"时与录制局分叉。
        /// </para>
        /// </summary>
        private ArenaWaveRow[]? _tableWaves;

        /// <summary>整场累计推进的逻辑帧数（兜底上限按它算，而不是按单场帧号）。</summary>
        private int _framesTotal;

        /// <summary>已结束波次累计的反射次数（当前波的实时值另算，见 <see cref="ReflectedCount"/>）。</summary>
        private int _reflectedTotal;

        /// <summary>本场竞技场是否已结算（<c>EndAndCollect</c> 只允许发生一次）。</summary>
        private bool _finished;

        /// <summary>本场竞技场是否胜利（结算后与内核结果对齐）。</summary>
        private bool _isVictory;

        /// <summary>当前波次，1..<see cref="TotalWaves"/>；结束（胜利/失败/超时）后为 0。</summary>
        public int CurrentWave
        {
            get { return _wave; }
        }

        /// <summary>总波次：波次表（<c>TbArenaWave</c>）的行数；缺表/非法时为内置的 3 波。</summary>
        public int TotalWaves
        {
            get { return _totalWaves; }
        }

        /// <summary>本场竞技场是否胜利（未结算时为 <c>false</c>）。</summary>
        public bool IsVictory
        {
            get { return _isVictory; }
        }

        /// <summary>
        /// 最近一次敌人 AI 求值输出的动作（诊断；无敌人或尚未开局为 <see cref="BehaviorAction.None"/>）。
        /// <b>它是"最后一个被求值的敌人"的动作</b>，不是全队聚合结果；只用于观感排查，不参与胜负判定。
        /// </summary>
        public BehaviorAction LastEnemyAction
        {
            get
            {
                ArenaEnemyAi? ai = _enemyAi;
                return ai != null ? ai.LastAction : BehaviorAction.None;
            }
        }

        /// <summary>最近一次敌人 AI 求值访问的节点数（诊断，口径见 <c>BehaviorTree.EvaluationsUsed</c>）。</summary>
        public int LastEnemyEvaluationCount
        {
            get
            {
                ArenaEnemyAi? ai = _enemyAi;
                return ai != null ? ai.LastEvaluationCount : 0;
            }
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
        /// 本局音效桥累计请求播放的 cue 条数（生命周期累计，重开不清）。
        /// <para>
        /// <b>它数的是"请求"而不是"真的听见"</b>：PlayMode 里没有音频资源、也没有声卡，
        /// 能断言的只有"桥算出了多少条该播的 cue"；真机是否出声取决于 <c>Resources/Audio/&lt;id&gt;</c>
        /// 是否存在（缺失只会 Warn 一次并静默跳过，见 <c>UnityAudioService</c>）。
        /// </para>
        /// </summary>
        public int AudioCueCount
        {
            get
            {
                BattleAudioCueSelector? selector = _audioSelector;
                return selector != null ? selector.EmittedCount : 0;
            }
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

            // 波次表必须在第一波布场之前读完：总波数与每波参数都会被开局路径（CreateScript/StartWave）读到。
            // 读失败只 Warn 并回退内置三波——竞技场是演示场景，"表没配"不该让整场玩不了。
            LoadWaveTable(host);

            // T5-7 音效桥：宿主目前没有 Audio 属性，用它的资源/日志端口自建一条音效服务
            // （音源节点挂在本控制器下，随场景卸载一起销毁），再把纯逻辑选择器接进去。
            _audio = host.Audio;
            _audioSelector = new BattleAudioCueSelector();
            _audioBridge = new BattleAudioBridge(_audio, _audioSelector);
            _audioSampler = new BattleAudioFrameSampler();

            // T8-1 敌人 AI：树在开局前编译一次（配置校验也发生在这一刻），之后每波复用。
            // 决策是纯函数（只读视图 + 黑板），所以回放局能逐帧复现同一串动作。
            _enemyAi = new ArenaEnemyAi(BehaviorTreeLibrary.CreateArenaEnemyTree());
            _enemyAiLogCounter = 0;

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

            TickAudio();

            RecordFrame();

            RealtimeSceneBinder? binder = _binder;
            if (binder != null)
            {
                binder.Sync();
            }

            // T8-1 钩子：Step 之后、换波裁决之前读一次现场。AI 只做两件事——
            // 把 MoveToward/MoveAway 翻成一次有界的径向 MoveIntent（下一帧才落位），
            // 其余动作只记诊断。伤害/命中/胜负仍全部由内核管线结算。
            TickEnemyAi(_wave, log: true);

            Tick();

            if (_finished)
            {
                return;
            }

            RenderHud();
        }

        /// <summary>
        /// 帧内音效推进：采样只读视图 → 选择器算 cue → <c>PlaySfx</c>。
        /// <para>
        /// <b>为什么紧跟 <c>Step</c> 之后、<see cref="Tick"/> 之前</b>：本帧的伤害/阵亡/反射都发生在这一次
        /// <c>Step</c> 里，而 <see cref="Tick"/> 在换波时会释放会话（<c>_units</c> 置空），
        /// 晚一步就再也读不到"这一帧死了人"的现场。
        /// </para>
        /// <para>
        /// <b>时间用逻辑帧长而不是 <c>Time.deltaTime</c></b>：节流窗口要对齐内核的固定步长，
        /// 否则高刷屏会让同一串连击响得更密（同一份回放在 30/144 fps 下听感不同）。
        /// </para>
        /// </summary>
        private void TickAudio()
        {
            BattleAudioBridge? bridge = _audioBridge;
            BattleAudioFrameSampler? sampler = _audioSampler;
            UnityAudioService? audio = _audio;
            if (bridge == null || sampler == null || audio == null)
            {
                return;
            }

            BattleAudioFrame frame = sampler.Capture(_units, _projectiles, _sandbox);
            bridge.Tick(audio, in frame, EffectiveStepSeconds());
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
        /// T8-1 敌人 AI 每帧决策：对每个存活敌人构造一次黑板 → <see cref="ArenaEnemyAi.Tick"/> →
        /// 把 MoveToward/MoveAway 翻成一次<b>有界径向</b> <see cref="MoveIntent"/>（下一帧 Phase 5 才落位）。
        /// <para>
        /// <b>为什么不直接改坐标</b>：本类从不写内核状态，位移也走 <see cref="IIntentWriter"/>，
        /// 由管线统一仲裁。这样 AI 只是"多提交一条意图"，伤害/命中/胜负仍由内核结算。
        /// </para>
        /// <para>
        /// <b>为什么位移必须保持径向且有内外半径</b>：本关的敌人是"朝原点发射一枚弹，护盾把弹沿原射线反射回来"。
        /// 敌人沿射线接近/远离时，反射弹的返回路径仍然穿过它，命中与否不变；一旦允许切向移动，
        /// 反射弹就可能擦过，波次清不掉、胜负会被 AI 改写。追到 <see cref="EnemyChaseStopRadius"/> 就停、
        /// 退到 <see cref="EnemyRetreatMaxRadius"/> 就停，两个边界都用注释写清了与护盾/回收半径的关系。
        /// </para>
        /// <para>
        /// <b>Attack / Wait 为什么只记诊断</b>：内核没有"敌人攻击冷却/重新出膛"的端口——敌人的弹在布场时
        /// 一次性生成，之后没有按单位补弹的 API。强行用 DamageIntent 补伤害就会改战斗规则，所以这两类动作
        /// 只落在 <see cref="LastEnemyAction"/> 与诊断日志里，等内核将来暴露攻击端口再接。
        /// </para>
        /// </summary>
        /// <param name="wave">当前波次（回放局用回放循环的局部波次，保证与录制局同一黑板）。</param>
        /// <param name="log">是否输出节流诊断日志（回放局关闭，避免刷屏且不影响状态）。</param>
        private void TickEnemyAi(int wave, bool log)
        {
            ArenaEnemyAi? ai = _enemyAi;
            IUnitWorldReadView? units = _units;
            if (ai == null || units == null || !_protagonist.IsValid)
            {
                return;
            }

            if (!units.TryGetUnit(_protagonist, out UnitReadView protagonist))
            {
                return;
            }

            int count = units.Count;
            int aliveEnemies = 0;
            for (int i = 0; i < count; i++)
            {
                UnitReadView view = units[i];
                if (view.Tags.Has(GameplayTags.UnitShieldActive))
                {
                    continue;
                }

                if (view.Hp > 0)
                {
                    aliveEnemies++;
                }
            }

            if (aliveEnemies <= 0)
            {
                return;
            }

            IProjectileWorldReadView? projectiles = _projectiles;
            IIntentWriter? intents = _intents;
            float stepSeconds = EffectiveStepSeconds();
            BehaviorAction lastAction = BehaviorAction.None;
            int lastEvaluationCount = 0;
            int movedCount = 0;

            for (int i = 0; i < count; i++)
            {
                UnitReadView enemy = units[i];
                if (enemy.Tags.Has(GameplayTags.UnitShieldActive) || enemy.Hp <= 0)
                {
                    continue;
                }

                // 黑板口径（全部取"这一帧能读到的近似值"，不引入额外状态）：
                // HP 百分比按满血常量归一；距离是到主角的世界距离四舍五入；
                // 数量取本波存活敌人总数（场景级近似，不区分"谁是谁的敌人"）；
                // 冷却就绪 = 这个敌人名下已经没有在飞的弹（弹被反射后 Owner 会改成主角）。
                int hpPercent = enemy.Hp >= EnemyHp ? 100 : enemy.Hp * 100 / EnemyHp;
                float distance = Vec2.Distance(enemy.Position, protagonist.Position);
                int enemyDistance = (int)(distance + 0.5f);
                bool cooldownReady = !HasProjectileOwnedBy(projectiles, enemy.Handle);

                var blackboard = new Blackboard(
                    hpPercent,
                    enemyDistance,
                    aliveEnemies,
                    cooldownReady,
                    wave,
                    EnemyRetreatHpPercent);

                BehaviorAction action = ai.Tick(in blackboard);
                lastAction = action;
                lastEvaluationCount = ai.LastEvaluationCount;

                if (intents != null)
                {
                    movedCount += ApplyEnemyMotion(
                        intents, in enemy, in protagonist, action, stepSeconds);
                }
            }

            _enemyAiLogCounter++;
            if (!log || _enemyAiLogCounter < EnemyAiLogIntervalFrames)
            {
                return;
            }

            _enemyAiLogCounter = 0;
            AIFrameworkUnityHost? host = _host;
            if (host != null)
            {
                // 只在节流点拼字符串：逐帧日志会制造每帧分配，也会把真机日志刷爆。
                host.Log.Info("[Arena][AI] 波=" + wave + "，存活敌=" + aliveEnemies
                    + "，动作=" + lastAction + "，求值节点=" + lastEvaluationCount
                    + "，本帧位移敌=" + movedCount);
            }
        }

        /// <summary>
        /// 把 AI 动作翻成一次径向位移意图。只有 MoveToward/MoveAway 会写世界；
        /// 位移量按固定帧长限速，且被夹在 [<see cref="EnemyChaseStopRadius"/>,
        /// <see cref="EnemyRetreatMaxRadius"/>] 内，避免破坏反射弹的命中几何。
        /// </summary>
        /// <returns>是否提交了位移意图（0/1，供诊断计数）。</returns>
        private static int ApplyEnemyMotion(
            IIntentWriter writer,
            in UnitReadView enemy,
            in UnitReadView protagonist,
            BehaviorAction action,
            float stepSeconds)
        {
            if (action != BehaviorAction.MoveToward && action != BehaviorAction.MoveAway)
            {
                return 0;
            }

            Vec2 toTarget = protagonist.Position - enemy.Position;
            float distance = toTarget.Length;
            if (distance <= 0.0001f)
            {
                return 0;
            }

            Vec2 direction = toTarget / distance;
            float travel;
            float maxTravel = EnemyChaseSpeed * stepSeconds;

            if (action == BehaviorAction.MoveToward)
            {
                if (distance <= EnemyChaseStopRadius)
                {
                    return 0;
                }

                travel = distance - EnemyChaseStopRadius;
                if (travel > maxTravel)
                {
                    travel = maxTravel;
                }
            }
            else
            {
                if (distance >= EnemyRetreatMaxRadius)
                {
                    return 0;
                }

                travel = EnemyRetreatMaxRadius - distance;
                if (travel > maxTravel)
                {
                    travel = maxTravel;
                }

                direction = -direction;
            }

            if (travel <= 0f)
            {
                return 0;
            }

            writer.Submit(new MoveIntent(enemy.Handle, direction * travel));
            return 1;
        }

        /// <summary>
        /// 这个敌人名下是否还有在飞的弹。弹被护盾反射后 <c>Owner</c> 改成主角，
        /// 因此"没有自己的弹"= 这一发已经打出去了，可以用来近似"冷却就绪"。
        /// </summary>
        private static bool HasProjectileOwnedBy(
            IProjectileWorldReadView? projectiles, UnitHandle owner)
        {
            if (projectiles == null)
            {
                return false;
            }

            int count = projectiles.Count;
            for (int i = 0; i < count; i++)
            {
                if (projectiles[i].Owner == owner)
                {
                    return true;
                }
            }

            return false;
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

            if (_wave >= _totalWaves)
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
        /// <param name="wave">要开的波次（1..<see cref="TotalWaves"/>）。</param>
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
            _intents = session as IIntentWriter;

            if (_units == null || _projectiles == null)
            {
                host.Log.Warn("[Arena] 会话未完整实现只读视图，场景里不会创建任何碰撞体（HUD 也无法显示战场状态）。");
            }

            // 新会话的音效基线必须重新起：上一波残弹数与本波开场弹数恰好相同时，
            // "飞行物变多"的差分会把这一波的齐射整段漏掉（这一行只影响音效，不影响任何战斗状态）。
            _audioSampler?.Reset();

            // 布场前先把绑定器建好：AddUnit/AddProjectile 要在 SpawnUnit/SpawnProjectile 之后立刻调用，
            // 句柄只有在刚生成时才能与内核里的那一份对上。
            _binder = CreateBinder(host);
            _protagonist = DeployWave(sandbox, wave, _binder, in spec);

            host.Log.Info("[Arena] 第 " + wave + "/" + _totalWaves + " 波开战：主角 HP=" + ProtagonistHp
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
            _intents = null;

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
            _intents = null;
            _enemyAi = null;
            _finished = true;

            // 音效桥先摘引用再释放音源：桥的 Tick 只在 Update 里跑，摘引用保证卸载过程中不会再有
            // 逐帧调用摸到已释放的 AudioSource；音源节点是本控制器的子节点，Dispose 只是提前销毁。
            UnityAudioService? audio = _audio;
            _audio = null;
            _audioBridge = null;
            _audioSampler = null;
            _audioSelector = null;
            if (audio != null)
            {
                audio.Dispose();
            }

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

            host.Log.Info("[Arena] 重开：回到第 " + FirstWave + "/" + _totalWaves + " 波。");
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
            _intents = null;
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
                _intents = null;
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

                // 回放局必须与录制局执行同一份 AI 决策：位移意图在第 5 相位落位，
                // 所以只能在 Step 之后、下一次 Step 之前提交；日志在回放中关闭（不影响状态）。
                TickEnemyAi(wave, log: false);

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
                _intents = null;

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
            _intents = session as IIntentWriter;
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
        /// 读 <c>TbArenaWave</c> 决定本场波次：成功则用表里的行数与每波参数，失败则 Warn + 回退内置 3 波。
        /// <para>
        /// <b>为什么加载侧还要再校验一遍</b>：<c>tools/wavecheck</c> 只在构建期跑，打包后的 <c>.bin</c> 可能来自旧版本、
        /// 也可能被替换；运行期需要一条底线——"行号连续、数量/血量/速度/间隔在区间内、Boss 只在最后一行"，
        /// 保证布场不会越界。至于难度曲线好不好玩（回落、节奏突变），那是设计问题，不该让整场退化成 3 波。
        /// </para>
        /// <para>
        /// <b>为什么失败不是静默回退</b>：静默回退会让"表没打进包"和"表被配坏了"在真机上完全一样（都只是少打几波），
        /// 排查成本极高；这里一律 Warn 并带上原因。
        /// </para>
        /// </summary>
        /// <param name="host">宿主（资源端口与日志的唯一入口）。</param>
        private void LoadWaveTable(AIFrameworkUnityHost host)
        {
            _tableWaves = null;
            _totalWaves = BuiltInWaves;

            try
            {
                IBinaryTableLoader loader = new UnityStreamingAssetsTableLoader(host.Assets, TableRelativeDirectory);
                byte[]? payload = loader.Load(ArenaWaveTableReader.TableName);
                if (payload == null)
                {
                    host.Log.Warn("[Arena] 没有找到波次表 " + ArenaWaveTableReader.TableName + ".bin（StreamingAssets/"
                        + TableRelativeDirectory + "），回退内置 " + BuiltInWaves + " 波。");
                    return;
                }

                ArenaWaveRow[] rows = ArenaWaveTableReader.Parse(payload);
                _tableWaves = rows;
                _totalWaves = rows.Length;

                int minEnemies = rows[0].EnemyCount;
                int maxEnemies = rows[0].EnemyCount;
                int bossWave = 0;
                for (int i = 0; i < rows.Length; i++)
                {
                    if (rows[i].EnemyCount < minEnemies)
                    {
                        minEnemies = rows[i].EnemyCount;
                    }

                    if (rows[i].EnemyCount > maxEnemies)
                    {
                        maxEnemies = rows[i].EnemyCount;
                    }

                    if (rows[i].IsBoss)
                    {
                        bossWave = rows[i].WaveIndex;
                    }
                }

                host.Log.Info("[Arena] 波次表已加载：" + rows.Length + " 波，每波敌人 " + minEnemies + ".." + maxEnemies
                    + "，Boss 波=" + (bossWave > 0 ? bossWave.ToString() : "无")
                    + "（难度曲线由 tools/wavecheck 在构建期校验）。");

                if (rows.Length != BuiltInWaves)
                {
                    // 帧预算不随波数放大（MaxTotalFrames 是内核/兜底的常量，改它等于改战斗规则），
                    // 波数越多每波可用帧越少：这条 Warn 是给真机调参的人看的。
                    host.Log.Warn("[Arena] 波数从内置 " + BuiltInWaves + " 变为 " + rows.Length
                        + "，但单场帧上限仍是 " + MaxTotalFrames + " 帧（约 "
                        + (int)(MaxTotalFrames * EffectiveStepSeconds()) + " 秒）：每波可用帧被摊薄，节奏需真机验证。");
                }
            }
            catch (Exception exception) when (exception is InvalidDataException
                || exception is InvalidOperationException
                || exception is IOException
                || exception is ArgumentException)
            {
                _tableWaves = null;
                _totalWaves = BuiltInWaves;
                host.Log.Warn("[Arena] 波次表 " + ArenaWaveTableReader.TableName + " 读取失败，回退内置 "
                    + BuiltInWaves + " 波：" + exception.Message);
            }
        }

        /// <summary>
        /// 生成本局的回放脚本：种子 + 每波敌人数量/半径（波数取 <see cref="_totalWaves"/>）。
        /// <b>位置不放进脚本</b>：它是 <c>(序号, 数量)</c> 的纯函数，同一份代码必然算出同一组坐标。
        /// <b>波次参数只从"表或内置常量"取</b>（<see cref="SpecFromData"/>），绝不回读上一局的脚本——
        /// 否则重开会在表被改过时把旧参数带进新一局，录制/回放的口径就再也对不上了。
        /// </summary>
        private ArenaReplayScript CreateScript(int seed)
        {
            int waveCount = _totalWaves > 0 ? _totalWaves : BuiltInWaves;
            WaveSpec[] waves = new WaveSpec[waveCount];
            for (int i = 0; i < waveCount; i++)
            {
                waves[i] = SpecFromData(FirstWave + i);
            }

            return new ArenaReplayScript(seed, waveCount, waves);
        }

        /// <summary>
        /// 取第 <paramref name="wave"/> 波的布场参数：<b>脚本优先</b>（回放局必须逐位复现录制局的布场），
        /// 脚本没覆盖到时退回表/内置常量。
        /// </summary>
        private WaveSpec WaveSpecFor(int wave)
        {
            ArenaReplayScript? script = _script;
            int index = wave - FirstWave;
            if (script != null && index >= 0 && index < script.WaveCount)
            {
                return script.Waves[index];
            }

            return SpecFromData(wave);
        }

        /// <summary>按"表优先、内置兜底"把第 <paramref name="wave"/> 波换成布场参数。</summary>
        private WaveSpec SpecFromData(int wave)
        {
            int index = wave - FirstWave;
            ArenaWaveRow[]? tableWaves = _tableWaves;
            if (tableWaves != null && index >= 0 && index < tableWaves.Length)
            {
                return ToWaveSpec(in tableWaves[index]);
            }

            return new WaveSpec(EnemyCountForWave(wave), EnemyRadius);
        }

        /// <summary>
        /// 把一行波次表换算成 <see cref="WaveSpec"/>：<b>只有"数量"能精确表达，血量/速度只能近似到判定半径</b>。
        /// <para>
        /// <b>为什么不把 hp/speed 直接喂给布场</b>：<see cref="WaveSpec"/> 只有 <c>EnemyCount</c> 与 <c>Radius</c>，
        /// 而敌人血量（<see cref="EnemyHp"/>）与追击速度（<see cref="EnemyChaseSpeed"/>）是编译期常量。
        /// 改这两个常量等于改战斗规则（血量 &gt; 14 时一发反射弹打不死，本波只能靠超时收场），
        /// 超出"工具侧只加加载逻辑"的权限，所以这里只做<b>可表达维度上的近似</b>：
        /// </para>
        /// <list type="bullet">
        /// <item><description>血量：相对内置基准血量按比例放大判定圈（越肉越容易被反射弹命中，近似"多挨几下"），夹在 0.75~1.6 倍；</description></item>
        /// <item><description>速度：越快判定圈越小（越容易擦过，近似"更难打中"），夹在 0.7~1.25 倍。</description></item>
        /// </list>
        /// <para>
        /// 半径只影响"擦过还是命中"的余量：反射弹沿原射线飞回，必然穿过敌人中心，所以夹在这个区间内
        /// 既不会让本波清不掉，也不会改变"同帧全灭"的节奏。表的 <c>spawnIntervalMs</c> / <c>rewardGold</c> /
        /// <c>bossFlag</c> 在竞技场<b>没有对应表达</b>（本体一次布场全放、战利品由内核结算），
        /// 只作为诊断与 <c>tools/wavecheck</c> 的校验输入存在。
        /// </para>
        /// </summary>
        /// <param name="row">表里的一行。</param>
        /// <returns>本波的布场参数。</returns>
        private static WaveSpec ToWaveSpec(in ArenaWaveRow row)
        {
            float hpScale = row.EnemyHp / (float)EnemyHp;
            if (hpScale < 0.75f)
            {
                hpScale = 0.75f;
            }
            else if (hpScale > 1.6f)
            {
                hpScale = 1.6f;
            }

            float speedScale = 1000f / (1000f + row.EnemySpeedPermille);
            if (speedScale < 0.7f)
            {
                speedScale = 0.7f;
            }
            else if (speedScale > 1.25f)
            {
                speedScale = 1.25f;
            }

            float radius = EnemyRadius * hpScale * speedScale;
            if (radius < 0.2f)
            {
                radius = 0.2f;
            }
            else if (radius > 0.6f)
            {
                radius = 0.6f;
            }

            return new WaveSpec(row.EnemyCount, radius);
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

        /// <summary>内置兜底的第 <paramref name="wave"/> 波敌人数量：首波 2 个，之后 3 个（表缺失时才会走到这里）。</summary>
        /// <param name="wave">波次（1..<see cref="BuiltInWaves"/>）。</param>
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

    /// <summary>
    /// <c>TbArenaWave</c> 的一行：纯数据，列序与 <c>tables/TbArenaWave.json</c> 一致。
    /// <para>
    /// <b>为什么和 <see cref="ArenaController"/> 挤在同一个文件</b>：本轮改动范围只允许动这一个适配层文件，
    /// 而 Unity 程序集不能引用 <c>tools/wavecheck</c>（net10.0 工具工程），运行期读取器只能与控制器同文件；
    /// 构建期规则仍只在 <c>tools/wavecheck</c>，两边刻意不共享代码（工具侧多查"设计味道"，运行期只保底线）。
    /// </para>
    /// </summary>
    internal readonly struct ArenaWaveRow
    {
        /// <summary>构造一行。</summary>
        /// <param name="waveIndex">波次序号（1 起）。</param>
        /// <param name="enemyCount">本波敌人数量。</param>
        /// <param name="enemyHp">单个敌人血量（本关不可逐波表达，只参与半径近似）。</param>
        /// <param name="enemySpeedPermille">敌人速度千分比（同上）。</param>
        /// <param name="spawnIntervalMs">出怪间隔（本关一次布场全放，只作诊断）。</param>
        /// <param name="rewardGold">通关金币（战利品由内核结算，只作诊断）。</param>
        /// <param name="bossFlag">Boss 标记（0 / 1）。</param>
        internal ArenaWaveRow(
            int waveIndex,
            int enemyCount,
            int enemyHp,
            int enemySpeedPermille,
            int spawnIntervalMs,
            int rewardGold,
            int bossFlag)
        {
            WaveIndex = waveIndex;
            EnemyCount = enemyCount;
            EnemyHp = enemyHp;
            EnemySpeedPermille = enemySpeedPermille;
            SpawnIntervalMs = spawnIntervalMs;
            RewardGold = rewardGold;
            BossFlag = bossFlag;
        }

        /// <summary>波次序号（1 起）。</summary>
        internal int WaveIndex { get; }

        /// <summary>本波敌人数量。</summary>
        internal int EnemyCount { get; }

        /// <summary>单个敌人血量。</summary>
        internal int EnemyHp { get; }

        /// <summary>敌人速度（千分比）。</summary>
        internal int EnemySpeedPermille { get; }

        /// <summary>出怪间隔（毫秒）。</summary>
        internal int SpawnIntervalMs { get; }

        /// <summary>通关奖励金币。</summary>
        internal int RewardGold { get; }

        /// <summary>Boss 标记（0 / 1）。</summary>
        internal int BossFlag { get; }

        /// <summary>本行是否 Boss 波。</summary>
        internal bool IsBoss
        {
            get { return BossFlag == 1; }
        }
    }

    /// <summary>
    /// <c>TbArenaWave.bin</c> 的读取器：布局 = <c>[int32 行数]</c> + 每行 7 个 int32（列序即解析顺序）。
    /// <para>
    /// <b>为什么一处不合法就整表失败</b>：波次表是"整条时间轴"，少一行会让后面的波次编号全部前移；
    /// 悄悄跳过坏行等于把"第 7 波少一个 0"变成"第 7 波还在、只是参数变了"，真机上只会表现为"这波怎么这么弱"。
    /// 失败后由 <see cref="ArenaController"/> 统一 Warn 并回退内置波次，至少行为是可解释的。
    /// </para>
    /// </summary>
    internal static class ArenaWaveTableReader
    {
        /// <summary>表名（与 <c>tables/TbArenaWave.json</c> 的 <c>table</c> 字段一致）。</summary>
        internal const string TableName = "TbArenaWave";

        /// <summary>固定列数：waveIndex / enemyCount / enemyHp / speed / spawnInterval / rewardGold / bossFlag。</summary>
        private const int ColumnCount = 7;

        /// <summary>表头：行数 int32。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单行字节数（7 × int32）。</summary>
        private const int RowBytes = ColumnCount * 4;

        /// <summary><c>enemyCount</c> 允许区间。</summary>
        private const int MinEnemyCount = 1;

        /// <summary><c>enemyCount</c> 允许区间。</summary>
        private const int MaxEnemyCount = 30;

        /// <summary><c>enemyHp</c> 允许区间。</summary>
        private const int MinEnemyHp = 1;

        /// <summary><c>enemyHp</c> 允许区间。</summary>
        private const int MaxEnemyHp = 10000;

        /// <summary><c>enemySpeedPermille</c> 允许区间。</summary>
        private const int MinEnemySpeedPermille = 0;

        /// <summary><c>enemySpeedPermille</c> 允许区间。</summary>
        private const int MaxEnemySpeedPermille = 3000;

        /// <summary><c>spawnIntervalMs</c> 允许区间。</summary>
        private const int MinSpawnIntervalMs = 50;

        /// <summary><c>spawnIntervalMs</c> 允许区间。</summary>
        private const int MaxSpawnIntervalMs = 10000;

        /// <summary>解析整张表；任何一行不合法都抛 <see cref="InvalidDataException"/>。</summary>
        /// <param name="payload">表的字节流。</param>
        /// <returns>按行序排列的波次。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="payload"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidDataException">长度、行号、列值或 Boss 位置不合法。</exception>
        internal static ArenaWaveRow[] Parse(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("波次表头部长度不足 " + HeaderBytes + " 字节。");
            }

            int rowCount = ReadInt32(payload, 0);
            if (rowCount <= 0)
            {
                throw new InvalidDataException("波次表行数为 " + rowCount + "，至少要有 1 波。");
            }

            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "波次表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            var rows = new ArenaWaveRow[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int waveIndex = ReadInt32(payload, offset);
                int enemyCount = ReadInt32(payload, offset + 4);
                int enemyHp = ReadInt32(payload, offset + 8);
                int enemySpeed = ReadInt32(payload, offset + 12);
                int spawnInterval = ReadInt32(payload, offset + 16);
                int rewardGold = ReadInt32(payload, offset + 20);
                int bossFlag = ReadInt32(payload, offset + 24);

                if (waveIndex != i + 1)
                {
                    throw new InvalidDataException(
                        "波次表第 " + (i + 1) + " 行的 waveIndex 为 " + waveIndex + "，要求从 1 开始连续递增。");
                }

                EnsureRange(waveIndex, "enemyCount", enemyCount, MinEnemyCount, MaxEnemyCount);
                EnsureRange(waveIndex, "enemyHp", enemyHp, MinEnemyHp, MaxEnemyHp);
                EnsureRange(waveIndex, "enemySpeedPermille", enemySpeed, MinEnemySpeedPermille, MaxEnemySpeedPermille);
                EnsureRange(waveIndex, "spawnIntervalMs", spawnInterval, MinSpawnIntervalMs, MaxSpawnIntervalMs);

                if (rewardGold < 0)
                {
                    throw new InvalidDataException(
                        "波次表 waveIndex=" + waveIndex + " 的 rewardGold 为负数：" + rewardGold + "。");
                }

                if (bossFlag != 0 && bossFlag != 1)
                {
                    throw new InvalidDataException(
                        "波次表 waveIndex=" + waveIndex + " 的 bossFlag 只能是 0 或 1，实际 " + bossFlag + "。");
                }

                if (bossFlag == 1 && i != rowCount - 1)
                {
                    throw new InvalidDataException(
                        "波次表 waveIndex=" + waveIndex + " 标了 bossFlag=1 但不是最后一行（最后一行 waveIndex="
                        + rowCount + "）：Boss 必须且只能是最终波。");
                }

                rows[i] = new ArenaWaveRow(
                    waveIndex, enemyCount, enemyHp, enemySpeed, spawnInterval, rewardGold, bossFlag);
            }

            return rows;
        }

        private static void EnsureRange(int waveIndex, string column, int value, int min, int max)
        {
            if (value < min || value > max)
            {
                throw new InvalidDataException(
                    "波次表 waveIndex=" + waveIndex + " 的 " + column + "=" + value
                    + " 越界（允许 " + min + ".." + max + "）。");
            }
        }

        private static int ReadInt32(byte[] payload, int offset)
        {
            return payload[offset]
                | (payload[offset + 1] << 8)
                | (payload[offset + 2] << 16)
                | (payload[offset + 3] << 24);
        }
    }
}
