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
    /// <b>真机注意（已知缺口，未在本场修复）</b>：本场景是纯数据布场，场上没有 Unity 碰撞体；
    /// 而装配层的 <c>UnitySpatialQuery</c> 走 <c>Physics.OverlapSphereNonAlloc</c>，
    /// 只有被 <c>RegisterCollider</c> 登记过的碰撞体才认得出单位和飞行物。
    /// 因此编辑器里通常表现为"飞行物穿过彼此、既不反射也不掉血"，最终靠 1200 帧兜底收场。
    /// 要让反射/击杀真的发生，需要像 <c>AIFrameworkBehaviour.BindUnit/BindProjectile</c> 那样
    /// 给每个单位与飞行物建碰撞体并登记句柄（本任务的可写范围里没有这项，故留作后续）。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArenaController : MonoBehaviour
    {
        /// <summary>总波次：三波清完即胜利。</summary>
        private const int Waves = 3;

        /// <summary>竞技场总逻辑帧上限（三波共用），到顶即兜底判负，防"一局永远打不完"。</summary>
        private const int MaxTotalFrames = 1200;

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

        /// <summary>本波主角句柄（超时兜底时要靠它定位主角）。</summary>
        private UnitHandle _protagonist;

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

        /// <summary>
        /// 累计反射次数（已结束波次 + 当前波实时值）。
        /// <b>为什么是 internal 而不是 public</b>：公开面锁定为三个只读属性，这是给同程序集的
        /// <see cref="ArenaHud"/> 拼状态行用的诊断值，不该出现在场景对外契约里。
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
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            if (session == null || _finished)
            {
                return;
            }

            session.Step(EffectiveStepSeconds());
            _framesTotal++;

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

            // 上一波已经收场（或本波放弃）：先交还会话，否则 Begin 会抛"已有活跃会话"。
            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            IGameplaySession session;
            try
            {
                var init = new GameplayInitData(levelId, randomSeed + wave, EnemyCountForWave(wave) + 1);
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
                host.Log.Warn("[Arena] 会话未完整实现只读视图，HUD 无法显示战场状态（波次判定退化）。");
            }

            _protagonist = DeployWave(sandbox, wave);

            host.Log.Info("[Arena] 第 " + wave + "/" + Waves + " 波开战：主角 HP=" + ProtagonistHp
                + "，敌人=" + EnemyCountForWave(wave) + "，已用帧=" + _framesTotal);
        }

        /// <summary>
        /// 布场：主角先放，再按等角圆摆这一波的敌人，每个敌人朝主角发射一枚飞行物。
        /// <para>
        /// <b>布场顺序即语义</b>：内核约定"首个 <c>SpawnUnit</c> 的单位是主角"，胜负读的就是它，
        /// 所以主角必须先于敌人放入，否则"主角阵亡"会被读成"敌人阵亡"。
        /// </para>
        /// </summary>
        /// <param name="sandbox">会话的布场端口。</param>
        /// <param name="wave">当前波次。</param>
        /// <returns>主角句柄；槽位耗尽时为无效句柄。</returns>
        private UnitHandle DeployWave(IRealtimeSandbox sandbox, int wave)
        {
            GameplayTagMask shield = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
            UnitHandle protagonist = sandbox.SpawnUnit(
                Vec2.Zero, ProtagonistRadius, ProtagonistArmorLevel, ProtagonistHp, shield);

            if (!protagonist.IsValid)
            {
                _host?.Log.Error("[Arena] 主角槽位耗尽，第 " + wave + " 波布场失败（本波不会有任何战斗）。");
                return default;
            }

            int enemies = EnemyCountForWave(wave);
            for (int i = 0; i < enemies; i++)
            {
                Vec2 position = EnemyPosition(i, enemies);
                UnitHandle enemy = sandbox.SpawnUnit(
                    position, EnemyRadius, EnemyArmorLevel, EnemyHp, GameplayTagMask.Empty);

                if (!enemy.IsValid)
                {
                    // 槽位耗尽：少放一个敌人，本波照样能打（清空判定按存活数算）。
                    continue;
                }

                // 主角在原点，瞄准方向就是指向圆心的方向。
                Vec2 direction = (-position).Normalized;
                if (direction.SqrLength <= 0f)
                {
                    direction = new Vec2(0f, -1f);
                }

                sandbox.SpawnProjectile(
                    enemy,
                    position + (direction * (EnemyRadius + EnemyMuzzleOffset)),
                    direction * EnemyProjectileSpeed,
                    EnemyProjectileDamage,
                    GameplayTagMask.Empty);
            }

            return protagonist;
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
                + "，战利品条目=" + loots.Count);
        }

        /// <summary>退订 HUD 事件并释放宿主（<c>Dispose</c> 会丢弃未结算的会话引用）。</summary>
        private void OnDestroy()
        {
            ArenaHud? view = hud;
            if (view != null)
            {
                view.OnRestartClicked -= HandleRestartClicked;
            }

            _session = null;
            _sandbox = null;
            _units = null;
            _projectiles = null;
            _finished = true;

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

            StartWave(FirstWave);
            RenderHud();
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
