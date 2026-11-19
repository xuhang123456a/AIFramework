#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.Tactics;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 战棋场景控制器：建宿主 → 直接构造 <see cref="TacticsWorld"/> → 按 0.5 秒节拍替玩家走一步 →
    /// 只读视图推给 <see cref="TacticsHud"/> → 终局结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new TacticsWorld</c>，而其它玩法控制器要走 <c>host.Sessions.Begin</c></b>：
    /// 适配层是组合根（<c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 刻意<b>没有</b> Tactics 值——战棋对主板而言只是"另一个回合制会话"，
    /// 胜负、战利品与流程完全一致；为了一个演示场景去扩枚举与 <c>UnityKernelFactory</c>，会让存档协议与契约
    /// 为一套同构卡带付长期成本。这与 <c>BoardGameController</c> / <c>RhythmController</c> 完全同因：
    /// 直接构造并<b>自己持有这次会话</b>（不登记进 <c>host.Sessions</c>），结算时直接调
    /// <see cref="IGameplaySession.CollectResult"/>。宿主仍然负责总线、日志与背包——本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何战棋规则</b>：能不能走到那格（四邻 BFS + 移动力）、能不能打（阵营 + 攻程）、
    /// 扣多少血、谁先手、敌方怎么走、胜负与平局全部由内核判定；适配层一旦自己算一遍（哪怕是"看起来等价"的复算），
    /// 改内核而不改 UI 时两侧就会静默分叉。本类只做四件事：装配宿主、把"操作/节拍"翻译成
    /// <see cref="ITacticsInput"/> 意图、把只读视图推给 HUD、终局结算并广播事实。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 计时而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次计时器只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>自动演示策略（<c>autoPlay</c>）</b>：每 <c>0.5</c> 秒替玩家推进一步——
    /// 取第一个"本回合还没行动过"的玩家单位；若最近的敌人已在攻程内就 <c>TryAttack</c>，
    /// 否则朝最近敌人走一格（四邻里挑一个能缩短曼哈顿距离且为空的格子）；三个单位都动过就 <c>EndTurn</c>，
    /// 由内核跑完敌方回合并推进回合序号。<b>策略只决定"提交哪个意图"，不预判结果</b>：
    /// 每个意图都要看内核返回值，被拒（被挡、目标已死）就按"这个单位本回合不再动"收尾，
    /// 因此一次卡住不会变成原地死循环——最坏情况交给内核的回合上限（<c>MaxTurns</c>）判平局。
    /// </para>
    /// <para>
    /// <b>为什么 <c>EndTurn</c> 之后要补一次 <c>Step(1/60)</c></b>：回合制内核的 <see cref="IGameplaySession.Step"/>
    /// 只给 <c>ProcessedSteps</c> 计数、不推进任何状态（没有待结算动画队列），补一次是为了让结算报告里的
    /// <c>StepCount</c> 与"推进过多少帧"保持同一口径；<b>它不影响胜负、也不影响回合序号</b>。
    /// 注意由此得到的关系是 <c>StepCount == TurnIndex - 1</c>（最后一回合的 EndTurn 还没发生），
    /// 离线探针已逐条核对过，不要把它当成"回合数"去展示。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TacticsController : MonoBehaviour
    {
        /// <summary>
        /// 场次 ID。<b>为什么不暴露成 <c>[SerializeField]</c></b>：战棋只有一张固定 8×8 盘，
        /// "选关"在本玩法里是个伪概念；它只用于 <see cref="GameplayInitData"/> 与
        /// <see cref="GameFinishedEvent"/> 的场次标识，写成字段只会多一个"场景里没注入"的空洞。
        /// </summary>
        private const int DefaultLevelId = 1;

        /// <summary>玩家阵营编码（与 <see cref="TacticsUnitView.Team"/> 的约定一致：0 = 玩家）。</summary>
        private const int PlayerTeam = 0;

        /// <summary>敌方阵营编码（1 = 敌方）。</summary>
        private const int EnemyTeam = 1;

        /// <summary>"没有单位"的哨兵值（内核统一用 <c>-1</c>：无选中 / 空格 / 找不到）。</summary>
        private const int NoUnitId = -1;

        /// <summary>本回合"已行动过"集合的初始容量（玩家单位最多 3 个，留余量即可，构造期一次分配）。</summary>
        private const int ActedCapacity = 8;

        /// <summary>
        /// 节拍兜底值：<c>autoStepSeconds</c> 被 Inspector 改成 0 或负数时用它。
        /// <b>为什么必须兜底</b>：0 会让自动演示每帧走一步（一局在一个帧内跑完，看起来像"游戏直接结束"），
        /// 负数则永远不到点（看起来像"卡住"）——两种都像 bug 而不是"参数被改坏"。
        /// </summary>
        private const float FallbackAutoStepSeconds = 0.5f;

        /// <summary><see cref="IGameplaySession.Step"/> 的固定增量（1/60 秒）：只用于让 <c>StepCount</c> 计数。</summary>
        private const float SingleStepDeltaSeconds = 1f / 60f;

        /// <summary>战棋皮肤（渲染状态行与 8×8 字符盘，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private TacticsHud hud = null!;

        /// <summary>
        /// 随机种子，原样送进 <see cref="GameplayInitData.RandomSeed"/>。
        /// <b>战棋内核不使用它</b>（Phase 1 没有随机：出生点、数值、敌方 AI 全是确定性的），
        /// 但进门契约要求带上，且"同种子同开局"的日志口径要跟其它玩法一致；离线探针已核对
        /// 种子 1..20 的结局完全相同——<b>想换局面请改 <c>unitCount</c>，不是改种子</b>。
        /// </summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>
        /// 玩家出战单位数（内核把它夹在 1..3；敌方恒为 3 个）。
        /// <b>这是唯一会改变局面的参数</b>：3 个玩家单位能赢，1~2 个会被敌方集火打死。
        /// </summary>
        [SerializeField] private int unitCount = 3;

        /// <summary>
        /// 是否自动演示：为 true 时玩家侧也由本类按类型注释里的策略自动行动（默认 true，场景挂上就能看）。
        /// 为 false 时玩家侧只由 <see cref="SelectUnit"/> / <see cref="MoveTo"/> / <see cref="Attack"/> /
        /// <see cref="EndTurn"/> 驱动，适合手动试玩。
        /// </summary>
        [SerializeField] private bool autoPlay = true;

        /// <summary>自动演示的节拍（秒/步）：0.5 秒足够让人看清"谁动了、打到谁"，再快就看不清走位。</summary>
        [SerializeField] private float autoStepSeconds = 0.5f;

        /// <summary>
        /// 本回合已行动过的玩家单位 ID（<see cref="EndTurn"/> 时清空）。
        /// <b>为什么状态在本类而不在内核</b>："一个单位一回合动一次"是演示策略的约定，不是内核规则
        /// （内核只要求"当前阵营 + 合法目标"）；把它写进内核会让一个 UI 编排细节变成规则。
        /// </summary>
        private readonly List<int> _actedUnitIds = new List<int>(ActedCapacity);

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前对局会话（<see cref="IGameplaySession"/> 视角，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空（会话所有权已交还），重开时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一局的具体类型。<see cref="ITacticsReadView"/> 里没有 <c>SelectedUnitId</c> 与
        /// <c>VictoryTeam</c>，而"选中高亮"和"胜方文案"都要用它们；结算后<b>保留</b>
        /// （HUD 要继续显示最终盘面），重开时替换。
        /// </summary>
        private TacticsWorld? _world;

        /// <summary>只读视图（与 <see cref="_world"/> 同一对象，单独留一份是为了表达"这里只用读面"）。</summary>
        private ITacticsReadView? _readView;

        /// <summary>输入端口（与 <see cref="_world"/> 同一对象）。</summary>
        private ITacticsInput? _input;

        /// <summary>本局是否已结算（<c>CollectResult</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>距上一步自动演示累计的秒数。</summary>
        private float _stepTimer;

        /// <summary>
        /// 本类认为的"当前选中单位"。<b>为什么自己记一份</b>：内核的
        /// <see cref="ITacticsInput.TrySelect"/> 对同一个 ID 是<b>取消选中</b>（不是幂等选中），
        /// 自动演示反复选同一个单位会把它取消掉；先比对再提交才不会互相打架。
        /// </summary>
        private int _selectedUnitId = NoUnitId;

        /// <summary>当前回合序号（从 1 开始）；无会话时为 0。</summary>
        public int TurnIndex
        {
            get
            {
                ITacticsReadView? view = _readView;
                return view != null ? view.TurnIndex : 0;
            }
        }

        /// <summary>
        /// 胜方阵营：<c>-1</c> 未分胜负、0 玩家胜、1 敌方胜。
        /// <b>平局也报 1</b>（内核把"回合上限"记成敌方占优，但 <c>GameplayResult.IsVictory</c> 仍为 false），
        /// 因此判"玩家是否赢了"请看 <see cref="GameplayResult.IsVictory"/> 那条链路（HUD 的终局文案），
        /// 不要拿这个值当胜负开关。
        /// </summary>
        public int VictoryTeam
        {
            get
            {
                TacticsWorld? world = _world;
                return world != null ? world.VictoryTeam : NoUnitId;
            }
        }

        /// <summary>对局是否结束（含"结算已取回"）；无会话时为 false。</summary>
        public bool IsFinished
        {
            get
            {
                TacticsWorld? world = _world;
                return world != null && world.IsFinished;
            }
        }

        /// <summary>
        /// 重开一局：换一个全新的 <see cref="TacticsWorld"/>（等价于"再来一次"）。
        /// <para>
        /// <b>为什么必须重建而不是"清盘"</b>：内核在 <c>CollectResult</c> 之后把会话所有权交还，
        /// <c>TrySelect</c> / <c>TryMove</c> / <c>TryAttack</c> / <c>EndTurn</c> 全部冻结；
        /// 只有重新构造才能真正回到初始局面。旧对象是纯内存的，随字段替换被 GC，没有原生资源要回收。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是 MonoBehaviour），它的
        /// <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"已终局但还没结算"的那一帧，
        /// 直接重建会把这一局的 <c>GameFinishedEvent</c> 与战利品静默吞掉。终局就先走
        /// <see cref="Settle"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// <para>局中调用同样安全：旧局还没结算就直接丢弃，不会出现两个会话同时活着。</para>
        /// </summary>
        public void Restart()
        {
            TacticsWorld? world = _world;
            if (!_collected && world != null && world.IsFinished)
            {
                Settle();
            }

            CreateWorld();
        }

        /// <summary>
        /// 选中一个单位（玩家单位 = 选中/取消选中；敌方单位 = 什么都不发生）。
        /// <b>合法性全交给内核</b>：非当前阵营、已阵亡、对局结束一律由
        /// <see cref="ITacticsInput.TrySelect"/> 返回 false，本类不做第二份校验。
        /// <para>
        /// <b>重复选中同一单位会取消选中</b>——这是内核契约（"再传一次即取消"），HUD 的
        /// <c>OnUnitClicked</c> 因此天然支持"点自己选中的棋子取消高亮"。
        /// </para>
        /// </summary>
        /// <param name="unitId">要选中的单位 ID（玩家单位 1..3）。</param>
        public void SelectUnit(int unitId)
        {
            TacticsWorld? world = _world;
            ITacticsInput? input = _input;
            if (world == null || input == null || _collected || world.IsFinished)
            {
                return;
            }

            if (!input.TrySelect(unitId))
            {
                return;
            }

            _selectedUnitId = world.SelectedUnitId;
            RenderHud();
        }

        /// <summary>
        /// 把 <paramref name="unitId"/> 移动到 (x, y)。<b>不在这里判合法性</b>：界内、空格、移动力、
        /// 四邻路径可达、当前阵营、对局是否结束——一律由 <see cref="ITacticsInput.TryMove"/> 决定，
        /// 被拒时什么都不改。
        /// </summary>
        /// <param name="unitId">要移动的单位 ID。</param>
        /// <param name="x">目标列（0..7，向右）。</param>
        /// <param name="y">目标行（0..7，向上；0 是玩家底线）。</param>
        public void MoveTo(int unitId, int x, int y)
        {
            TacticsWorld? world = _world;
            ITacticsInput? input = _input;
            if (world == null || input == null || _collected || world.IsFinished)
            {
                return;
            }

            if (!input.TryMove(unitId, x, y))
            {
                return;
            }

            // 移动不改变选中（内核注释明确"选中只是 UI 提示"），但仍同步一次：
            // 万一将来内核改成"移动即清选中"，HUD 也不会显示一个过期的 ID。
            _selectedUnitId = world.SelectedUnitId;
            RenderHud();
            SettleIfFinished();
        }

        /// <summary>
        /// 用 <paramref name="attackerId"/> 攻击 <paramref name="targetId"/>。
        /// 攻程、阵营、存活与否全部由内核判定；命中扣血、目标阵亡移除、胜负刷新也都在内核里。
        /// </summary>
        /// <param name="attackerId">攻方单位 ID（必须是当前阵营）。</param>
        /// <param name="targetId">目标单位 ID（必须敌对阵营且在攻程内）。</param>
        public void Attack(int attackerId, int targetId)
        {
            TacticsWorld? world = _world;
            ITacticsInput? input = _input;
            if (world == null || input == null || _collected || world.IsFinished)
            {
                return;
            }

            if (!input.TryAttack(attackerId, targetId))
            {
                return;
            }

            // 内核在"被打死的正好是选中单位"时会自己清掉选中，这里必须重新同步，否则 HUD 会高亮一个死人。
            _selectedUnitId = world.SelectedUnitId;
            RenderHud();
            SettleIfFinished();
        }

        /// <summary>
        /// 结束玩家回合：内核跑完敌方 AI 并推进回合序号（<b>同步完成，不会分帧</b>），
        /// 之后补一次 <see cref="IGameplaySession.Step"/> 计数、清空"本回合已行动"集合并刷新 HUD。
        /// </summary>
        public void EndTurn()
        {
            TacticsWorld? world = _world;
            ITacticsInput? input = _input;
            if (world == null || input == null || _collected || world.IsFinished)
            {
                return;
            }

            input.EndTurn();

            // 回合制 Step 只计数：它既不是"敌方回合的动画"，也不会改变任何状态，纯为结算报告的 StepCount 口径。
            world.Step(SingleStepDeltaSeconds);

            _selectedUnitId = world.SelectedUnitId;
            _actedUnitIds.Clear();
            RenderHud();
            SettleIfFinished();
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
        /// 每帧：累计节拍 → 到点走一步 → 刷新 HUD → 终局则结算一次。
        /// <b>顺序不可换</b>：先推进再渲染，玩家看到的就是这一步之后的真实局面；
        /// 先渲染再推进会让画面永远慢一格（终局也要多等一拍才显示结果）。
        /// </summary>
        private void Update()
        {
            TacticsWorld? world = _world;
            if (world == null || _collected)
            {
                return;
            }

            // 结算不受 autoPlay 影响：手动模式下由点击驱动的终局同样要广播与发奖。
            if (world.IsFinished)
            {
                SettleIfFinished();
                return;
            }

            if (!autoPlay)
            {
                return;
            }

            _stepTimer += Time.deltaTime;
            if (_stepTimer < EffectiveAutoStepSeconds())
            {
                return;
            }

            // 只扣一次节拍（不补跑）：编辑器编译/切窗口产生的长帧不该让棋盘"连跳好几步"，
            // 那会让玩家以为自己漏看了一整回合。
            _stepTimer = 0f;

            AutoStepOnce();
            RenderHud();
            SettleIfFinished();
        }

        /// <summary>退订 HUD 并释放宿主（<c>Dispose</c> 会丢弃未结算的会话引用）。</summary>
        private void OnDestroy()
        {
            UnsubscribeHud();

            _session = null;
            _world = null;
            _readView = null;
            _input = null;
            _actedUnitIds.Clear();
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
        /// 而战棋没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
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
            _readView = null;
            _input = null;
            _collected = false;
            _stepTimer = 0f;
            _selectedUnitId = NoUnitId;
            _actedUnitIds.Clear();

            var init = new GameplayInitData(DefaultLevelId, randomSeed, unitCount);
            var world = new TacticsWorld(in init);

            _session = world;
            _world = world;
            _readView = world;
            _input = world;

            RenderHud();

            host.Log.Info("[Tactics] 开局：场次=" + DefaultLevelId
                + " 种子=" + randomSeed
                + " 玩家单位=" + unitCount + "（内核夹在 1..3）"
                + " 棋盘=" + world.Width + "×" + world.Height
                + " 自动演示=" + autoPlay
                + " 节拍秒=" + EffectiveAutoStepSeconds());
        }

        /// <summary>
        /// 自动演示走一步：优先"打得到的就先打"，否则"朝最近敌人走一格"；
        /// 三个玩家单位都动过就结束回合（由内核跑敌方）。
        /// </summary>
        private void AutoStepOnce()
        {
            TacticsWorld? world = _world;
            if (world == null || world.IsFinished)
            {
                return;
            }

            // 稳态下 EndTurn 返回后阵营恒为玩家；这里挡一下，"敌方回合中"绝不下手。
            if (world.CurrentTeam != PlayerTeam)
            {
                return;
            }

            int unitId = NextIdlePlayerUnit(world);
            if (unitId == NoUnitId)
            {
                EndTurn();
                return;
            }

            ActWith(world, unitId);
        }

        /// <summary>
        /// 取第一个"本回合还没行动过"的存活玩家单位（按 <see cref="ITacticsReadView.GetUnit"/> 的 index 序，
        /// 即单位表的稳定顺序，确定性）；全动过返回 <see cref="NoUnitId"/>。
        /// </summary>
        /// <param name="world">当前对局。</param>
        /// <returns>单位 ID，或 <see cref="NoUnitId"/>。</returns>
        private int NextIdlePlayerUnit(TacticsWorld world)
        {
            for (int i = 0; i < world.UnitCount; i++)
            {
                TacticsUnitView unit = world.GetUnit(i);
                if (unit.Team != PlayerTeam)
                {
                    continue;
                }

                if (_actedUnitIds.Contains(unit.UnitId))
                {
                    continue;
                }

                return unit.UnitId;
            }

            return NoUnitId;
        }

        /// <summary>
        /// 让一个玩家单位行动一次：选中 → 找最近敌人 → 攻程内就打，否则走一格 → 标记"本回合已行动"。
        /// <para>
        /// <b>为什么无论成败都要标记</b>：不标记的话，一个被堵死的单位会让自动演示在同一格上反复重试，
        /// 看起来像卡死；标记后它本回合不再动，回合照常推进，最坏情况由内核的回合上限判平局。
        /// </para>
        /// </summary>
        /// <param name="world">当前对局。</param>
        /// <param name="unitId">要行动的单位 ID。</param>
        private void ActWith(TacticsWorld world, int unitId)
        {
            TacticsUnitView actor;
            if (!TryGetUnit(world, unitId, out actor))
            {
                MarkActed(unitId);
                return;
            }

            // 选中只为让 HUD 高亮"现在轮到谁"；内核的 TrySelect 对同一 ID 是取消选中，故先比对。
            if (_selectedUnitId != unitId)
            {
                world.TrySelect(unitId);
                _selectedUnitId = world.SelectedUnitId;
            }

            TacticsUnitView enemy;
            if (!TryFindNearestEnemy(world, in actor, out enemy))
            {
                // 没有敌人还活着 → 内核应该已经判结束；兜底不抛异常。
                MarkActed(unitId);
                return;
            }

            if (Manhattan(actor.X, actor.Y, enemy.X, enemy.Y) <= actor.AttackRange)
            {
                world.TryAttack(unitId, enemy.UnitId);
                MarkActed(unitId);
                _selectedUnitId = world.SelectedUnitId;
                return;
            }

            TryStepToward(world, in actor, in enemy);
            MarkActed(unitId);
            _selectedUnitId = world.SelectedUnitId;
        }

        /// <summary>
        /// 朝最近敌人走一格：在四邻里挑一个<b>能缩短与目标曼哈顿距离且为空格</b>的格子提交
        /// <see cref="ITacticsInput.TryMove"/>；找不到（被围死 / 越界）就原地不动。
        /// <para>
        /// <b>为什么先试上下再试左右</b>：双方分别出生在 <c>y = 0</c> 与 <c>y = Height-1</c>，
        /// 主战场是纵向对冲；平局时优先纵向能让队形不散开（横向只会绕远）。
        /// </para>
        /// <para>
        /// <b>为什么不自己判"能不能到"</b>：四邻可达性由内核 BFS 判定；本类只提交相邻一格，
        /// 距离 1 一定在移动力内，但"提交并看返回值"才是唯一不会与内核分叉的写法。
        /// </para>
        /// </summary>
        /// <param name="world">当前对局。</param>
        /// <param name="actor">行动单位快照。</param>
        /// <param name="enemy">最近敌人快照。</param>
        /// <returns>是否真的移动成功。</returns>
        private static bool TryStepToward(TacticsWorld world, in TacticsUnitView actor, in TacticsUnitView enemy)
        {
            int bestDistance = Manhattan(actor.X, actor.Y, enemy.X, enemy.Y);
            int bestX = NoUnitId;
            int bestY = NoUnitId;

            ConsiderCell(world, actor.X, actor.Y + 1, enemy.X, enemy.Y, ref bestDistance, ref bestX, ref bestY);
            ConsiderCell(world, actor.X, actor.Y - 1, enemy.X, enemy.Y, ref bestDistance, ref bestX, ref bestY);
            ConsiderCell(world, actor.X + 1, actor.Y, enemy.X, enemy.Y, ref bestDistance, ref bestX, ref bestY);
            ConsiderCell(world, actor.X - 1, actor.Y, enemy.X, enemy.Y, ref bestDistance, ref bestX, ref bestY);

            if (bestX == NoUnitId)
            {
                return false;
            }

            return world.TryMove(actor.UnitId, bestX, bestY);
        }

        /// <summary>
        /// 评估一个候选格：越界 / 被占 / 不比当前最优更近就跳过。
        /// <b>只收严格更近</b>：这样"第一个在平局里出现的格子"胜出，策略因此完全确定（同局面必得同一步）。
        /// </summary>
        /// <param name="world">当前对局。</param>
        /// <param name="x">候选列。</param>
        /// <param name="y">候选行。</param>
        /// <param name="enemyX">目标敌人列。</param>
        /// <param name="enemyY">目标敌人行。</param>
        /// <param name="bestDistance">当前最优距离（会被更新）。</param>
        /// <param name="bestX">当前最优列（会被更新）。</param>
        /// <param name="bestY">当前最优行（会被更新）。</param>
        private static void ConsiderCell(
            TacticsWorld world,
            int x,
            int y,
            int enemyX,
            int enemyY,
            ref int bestDistance,
            ref int bestX,
            ref int bestY)
        {
            if (x < 0 || x >= world.Width || y < 0 || y >= world.Height)
            {
                return;
            }

            if (world.UnitAt(x, y) >= 0)
            {
                return;
            }

            int distance = Manhattan(x, y, enemyX, enemyY);
            if (distance >= bestDistance)
            {
                return;
            }

            bestDistance = distance;
            bestX = x;
            bestY = y;
        }

        /// <summary>按稳定 ID 取单位快照（<see cref="ITacticsReadView.GetUnit"/> 收的是 index，会因阵亡前移）。</summary>
        /// <param name="world">当前对局。</param>
        /// <param name="unitId">单位 ID。</param>
        /// <param name="unit">取到的快照。</param>
        /// <returns>是否找到。</returns>
        private static bool TryGetUnit(TacticsWorld world, int unitId, out TacticsUnitView unit)
        {
            for (int i = 0; i < world.UnitCount; i++)
            {
                TacticsUnitView candidate = world.GetUnit(i);
                if (candidate.UnitId == unitId)
                {
                    unit = candidate;
                    return true;
                }
            }

            unit = default;
            return false;
        }

        /// <summary>
        /// 找离 <paramref name="actor"/> 曼哈顿最近的敌方单位；<b>同距离取 UnitId 更小者</b>
        /// （与内核 <c>TacticsAi</c> "取 index 更小者"同样确定，只是这里换成了稳定 ID）。
        /// </summary>
        /// <param name="world">当前对局。</param>
        /// <param name="actor">行动单位快照。</param>
        /// <param name="enemy">最近的敌人快照。</param>
        /// <returns>是否存在活着的敌人。</returns>
        private static bool TryFindNearestEnemy(TacticsWorld world, in TacticsUnitView actor, out TacticsUnitView enemy)
        {
            bool found = false;
            int bestDistance = int.MaxValue;
            enemy = default;

            for (int i = 0; i < world.UnitCount; i++)
            {
                TacticsUnitView candidate = world.GetUnit(i);
                if (candidate.Team != EnemyTeam)
                {
                    continue;
                }

                int distance = Manhattan(actor.X, actor.Y, candidate.X, candidate.Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    enemy = candidate;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>把单位记进"本回合已行动"集合（容量上限兜底，重复标记幂等）。</summary>
        /// <param name="unitId">单位 ID。</param>
        private void MarkActed(int unitId)
        {
            if (_actedUnitIds.Count >= ActedCapacity || _actedUnitIds.Contains(unitId))
            {
                return;
            }

            _actedUnitIds.Add(unitId);
        }

        /// <summary>两格曼哈顿距离（四邻网格，没有对角线）。</summary>
        /// <param name="x1">起点列。</param>
        /// <param name="y1">起点行。</param>
        /// <param name="x2">终点列。</param>
        /// <param name="y2">终点行。</param>
        /// <returns>距离。</returns>
        private static int Manhattan(int x1, int y1, int x2, int y2)
        {
            int dx = x1 > x2 ? x1 - x2 : x2 - x1;
            int dy = y1 > y2 ? y1 - y2 : y2 - y1;
            return dx + dy;
        }

        /// <summary>
        /// 把 HUD 的"重新开始 / 结束回合 / 单位点击 / 格子点击"接到本类
        /// （<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。
        /// </summary>
        private void SubscribeHud()
        {
            TacticsHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked += HandleRestartClicked;
            view.OnEndTurnClicked += HandleEndTurnClicked;
            view.OnUnitClicked += HandleUnitClicked;
            view.OnCellClicked += HandleCellClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            TacticsHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked -= HandleRestartClicked;
            view.OnEndTurnClicked -= HandleEndTurnClicked;
            view.OnUnitClicked -= HandleUnitClicked;
            view.OnCellClicked -= HandleCellClicked;
        }

        /// <summary>点"重新开始"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>点"结束回合"：与自动演示里那一步完全同一条路径（清空已行动集合、推进、渲染、判结算）。</summary>
        private void HandleEndTurnClicked()
        {
            EndTurn();
        }

        /// <summary>
        /// 点一个单位：玩家单位 = 选中/取消；敌方单位 = 用当前选中单位攻击它（攻程不够时内核拒绝，什么都不发生）。
        /// <para>
        /// <b>为什么敌方走"攻击"而不是"选中"</b>：<see cref="ITacticsInput.TrySelect"/> 只收当前阵营，
        /// 点敌人唯一有意义的解释就是"打它"；这条分支不做任何距离判断，把"够不够得着"留给内核。
        /// </para>
        /// </summary>
        /// <param name="unitId">被点单位的稳定 ID。</param>
        private void HandleUnitClicked(int unitId)
        {
            TacticsWorld? world = _world;
            if (world == null)
            {
                return;
            }

            TacticsUnitView unit;
            if (!TryGetUnit(world, unitId, out unit))
            {
                return;
            }

            if (unit.Team == PlayerTeam)
            {
                SelectUnit(unitId);
                return;
            }

            int attackerId = _selectedUnitId;
            if (attackerId == NoUnitId)
            {
                return;
            }

            Attack(attackerId, unitId);
        }

        /// <summary>点一个空格：把当前选中单位移过去（坐标已由 HUD 换算成内核口径，行序也翻好了）。</summary>
        /// <param name="x">列（0..7）。</param>
        /// <param name="y">行（0..7，向上）。</param>
        private void HandleCellClicked(int x, int y)
        {
            int unitId = _selectedUnitId;
            if (unitId == NoUnitId)
            {
                return;
            }

            MoveTo(unitId, x, y);
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            TacticsHud? view = hud;
            ITacticsReadView? read = _readView;
            if (view != null && read != null)
            {
                view.Render(read, _selectedUnitId);
            }
        }

        /// <summary>终局才结算（未终局直接返回，避免在热路径上多判一次 <c>CollectResult</c> 的前置条件）。</summary>
        private void SettleIfFinished()
        {
            TacticsWorld? world = _world;
            if (world == null || _collected || !world.IsFinished)
            {
                return;
            }

            Settle();
        }

        /// <summary>
        /// 结算：<c>CollectResult</c> → 广播 <see cref="GameFinishedEvent"/> → 战利品入账 → HUD 显示结果。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>为什么广播 <see cref="GameplayKernelType.Turn"/></b>：战棋就是 Turn 卡带的一个会话，
        /// 主板只认"回合制玩法结束了、玩家胜没胜"；为了它新增枚举值会逼存档协议与装配层同步扩容。
        /// </para>
        /// </summary>
        private void Settle()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            TacticsWorld? world = _world;
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
                host.Log.Error("[Tactics] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, DefaultLevelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            TacticsHud? view = hud;
            if (view != null)
            {
                view.ShowResult(result.IsVictory, world.VictoryTeam);
            }

            host.Log.Info("[Tactics] 结算：场次=" + DefaultLevelId
                + " 胜负=" + result.IsVictory
                + " 胜方=" + world.VictoryTeam
                + " 回合=" + world.TurnIndex
                + " 步数=" + result.StepCount
                + " 玩家存活=" + result.RemainingUnits
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>自动演示节拍：字段被改成非法值时退回 <see cref="FallbackAutoStepSeconds"/>。</summary>
        /// <returns>大于 0 的节拍（秒）。</returns>
        private float EffectiveAutoStepSeconds()
        {
            float seconds = autoStepSeconds;
            return seconds > 0f ? seconds : FallbackAutoStepSeconds;
        }
    }
}
