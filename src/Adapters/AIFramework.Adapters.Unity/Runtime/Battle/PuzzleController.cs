#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 推箱子（解谜）场景控制器：建宿主 → <c>Begin(Turn)</c> → 按节奏把内置解法喂给内核 → 解出后结算一次。
    /// <para>
    /// <b>为什么这里没有任何推箱子规则</b>：能不能走、能不能推、箱子是否归位，全部由内核判定；
    /// 适配层一旦自己算一遍规则，两侧就会各自演化，改内核而不改 UI 时行为静默分叉。
    /// 因此本类只做五件事：装配宿主、把"方向"翻译成 <see cref="IPuzzleInput"/> 意图、
    /// 按 <see cref="autoStepSeconds"/> 间隔驱动内置解法、结算并广播事实、把只读视图推给 HUD。
    /// </para>
    /// <para>
    /// <b>为什么只认 Contracts 接口</b>：本类<b>不</b>引用 <c>AIFramework.Kernel.Turn</c> 的
    /// <c>PuzzleWorld</c>，只用 <see cref="IPuzzleReadView"/> / <see cref="IPuzzleInput"/> 与
    /// <see cref="IGameplaySession"/>；内核实现与宿主工厂分支由装配层接，本类无需改动。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 计时而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。累加器 + <c>Time.deltaTime</c> 只有一条可读的时间线。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PuzzleController : MonoBehaviour
    {
        /// <summary>自动解法间隔的兜底值：字段被 Inspector 改成 0 或负数时用它，避免每帧连推（画面瞬移）。</summary>
        private const float FallbackAutoStepSeconds = 0.35f;

        /// <summary>最小关卡 ID。<b>为什么写死 1..3</b>：内核把越界 ID 夹到 1，适配层夹同一份规则，避免"场景显示第 9 关、内核其实在第 1 关"。</summary>
        private const int MinLevel = 1;

        /// <summary>内置关卡数量上限（与 <c>PuzzleLevel.LevelCount</c> 对齐；不引用内核类型，故此处独立定义）。</summary>
        private const int MaxLevel = 3;

        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：推箱子只有一个玩家，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对。
        /// </summary>
        private const int PuzzleUnitCount = 1;

        /// <summary>第 1 关内置解法：上、上、上（0=上 1=右 2=下 3=左），3 步。</summary>
        private static readonly int[] SolutionLevel1 = { 1, 1, 1 };

        /// <summary>第 2 关内置解法：需要绕到箱子另一侧再推，9 步。</summary>
        private static readonly int[] SolutionLevel2 = { 2, 1, 1, 0, 3, 0, 3, 0, 0 };

        /// <summary>第 3 关内置解法：两个箱子两个目标点，9 步。</summary>
        private static readonly int[] SolutionLevel3 = { 1, 0, 0, 0, 0, 3, 3, 3, 0 };

        /// <summary>解谜皮肤（渲染状态行与字符网格，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private PuzzleHud hud = null!;

        /// <summary>入场关卡 ID（1..3，其它值夹到 1）。</summary>
        [SerializeField] private int levelId = 1;

        /// <summary>随机种子：内置关卡是确定的，仍按门禁契约透传，便于将来换成配置表关卡。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>自动解法每步之间的间隔（秒）。默认 0.35 秒，肉眼能看清"推一格"而不是瞬移。</summary>
        [SerializeField] private float autoStepSeconds = 0.35f;

        /// <summary>宿主（总线/会话/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>当前对局会话；结算或开局失败后置空。<b>只有它还非空时才允许 <c>EndAndCollect</c></b>。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的只读视图；结算后仍保留（HUD 要继续显示最终局面），换关时替换。</summary>
        private IPuzzleReadView? _readView;

        /// <summary>会话强转出来的输入端口；开局失败时为空（此时只能看不能推）。</summary>
        private IPuzzleInput? _input;

        /// <summary>当前关卡 ID（夹取后）。公开面 <see cref="CurrentLevel"/> 读它。</summary>
        private int _currentLevel = MinLevel;

        /// <summary>当前关卡的内置解法（指向静态数组，<b>只读不写</b>）。</summary>
        private int[]? _solution;

        /// <summary>解法执行到第几步（下标）。</summary>
        private int _solutionIndex;

        /// <summary>自动解法的间隔累加器：攒够 <see cref="autoStepSeconds"/> 才推一步。</summary>
        private float _autoStepAccumulator;

        /// <summary>是否正在按内置解法自动推进。</summary>
        private bool _autoSolving;

        /// <summary>本关是否已结算（<c>EndAndCollect</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>当前关卡 ID（1..3，夹取后的真实值）。</summary>
        public int CurrentLevel
        {
            get { return _currentLevel; }
        }

        /// <summary>当前已走步数（只统计成功的移动/推箱；结算后仍可读）。</summary>
        public int Moves
        {
            get
            {
                IPuzzleReadView? view = _readView;
                return view != null ? view.Moves : 0;
            }
        }

        /// <summary>本关是否所有箱子都已归位。</summary>
        public bool IsSolved
        {
            get
            {
                IPuzzleReadView? view = _readView;
                return view != null && view.IsSolved;
            }
        }

        /// <summary>
        /// 载入（重建）某一关：夹取 ID → 停掉自动解法 → 交还上一局 → 重新 <c>Begin</c>。
        /// <para>
        /// <b>为什么要"重建"而不是只改字段</b>：内核的关卡在构造期就固化进地形/箱位数组，
        /// 会话本身没有"换关"入口；同一宿主可以复用，但必须走 <c>Release</c> + <c>Begin</c>。
        /// </para>
        /// </summary>
        /// <param name="levelId">关卡 ID（1..3，其它值夹到 1）。</param>
        public void LoadLevel(int levelId)
        {
            BeginSession(ClampLevel(levelId));
        }

        /// <summary>
        /// 回到当前关卡的初始局面。
        /// <para>
        /// <b>为什么结算后要走重建</b>：内核在 <c>CollectResult</c> 之后把 <c>Reset</c> 变成空操作
        /// （会话所有权已交还宿主），此时只有重新 <c>Begin</c> 才能真正回到初始局面。
        /// </para>
        /// </summary>
        public void ResetLevel()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            IPuzzleInput? input = _input;
            if (!_collected && _session != null && input != null)
            {
                StopAutoSolve();
                input.Reset();
                RenderHud();
                host.Log.Info("[Puzzle] 重置：关卡=" + _currentLevel);
                return;
            }

            BeginSession(_currentLevel);
        }

        /// <summary>
        /// 按内置解法自动走完当前关（每 <see cref="autoStepSeconds"/> 一步，由 <see cref="Update"/> 驱动）。
        /// <para>
        /// <b>为什么重复点击直接返回</b>：走到一半重新从头播放会让"局面"和"解法下标"错位，
        /// 之后每一步都会被内核拒绝；要么等它走完，要么先重置/换关。
        /// </para>
        /// <para>
        /// <b>为什么已解出/已结算时先重建</b>：内核解出后冻结输入（<see cref="IPuzzleInput.TryMove"/> 返回 false），
        /// 结算后连 <see cref="IPuzzleInput.Reset"/> 都失效；不重建的话这个按钮会变成"点了没反应"。
        /// </para>
        /// </summary>
        public void SolveDemo()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            if (_autoSolving)
            {
                return;
            }

            if (_collected || _session == null || _readView == null || _input == null || IsSolved)
            {
                BeginSession(_currentLevel);
            }

            if (_session == null || _readView == null || _input == null)
            {
                // 开局不可用（例如工厂还没接推箱子分支）：错误已经在 BeginSession 里记过，这里不再重复刷屏。
                return;
            }

            _solution = SolutionFor(_currentLevel);
            _solutionIndex = 0;

            // 先垫满一个间隔：点完立刻在下一帧走第一步，别让玩家对着静止棋盘等 0.35 秒。
            _autoStepAccumulator = EffectiveAutoStepSeconds();
            _autoSolving = true;

            host.Log.Info("[Puzzle] 自动解法开始：关卡=" + _currentLevel + " 共 " + _solution.Length + " 步，间隔 " + EffectiveAutoStepSeconds() + "s");
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
            BeginSession(ClampLevel(levelId));
        }

        /// <summary>
        /// 每帧：按累加器推进一步 → 刷新 HUD → 解出则结算一次。
        /// <b>顺序不可换</b>：最后一步必须先上屏再结算，否则玩家看到的是"还没推完就通关"。
        /// </summary>
        private void Update()
        {
            if (_host == null)
            {
                return;
            }

            AdvanceAutoSolve();

            if (!_collected)
            {
                RenderHud();
            }

            if (!_collected && _session != null && IsSolved)
            {
                Finish();
            }
        }

        /// <summary>退订 HUD、停掉自动解法并释放宿主（<c>Dispose</c> 会丢弃未结算的对局引用）。</summary>
        private void OnDestroy()
        {
            UnsubscribeHud();

            StopAutoSolve();
            _session = null;
            _readView = null;
            _input = null;
            _collected = true;

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 建（或重建）一关的会话：交还旧会话 → <c>Begin(Turn)</c> → 强转契约 → 首帧渲染。
        /// <para>
        /// <b>为什么每次都先 <c>Release</c></b>：宿主只允许一个活跃会话，上一局（本场景重进或换关）
        /// 若还挂着，<c>Begin</c> 会直接抛"已有活跃会话"。
        /// </para>
        /// </summary>
        /// <param name="level">已夹取到 1..3 的关卡 ID。</param>
        private void BeginSession(int level)
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            StopAutoSolve();

            _currentLevel = level;
            _session = null;
            _readView = null;
            _input = null;
            _collected = false;

            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            IGameplaySession session;
            var init = new GameplayInitData(level, randomSeed, PuzzleUnitCount);
            try
            {
                session = host.Sessions.Begin(GameplayKernelType.Puzzle, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Puzzle] 开局失败（宿主已有未结算会话？）：" + exception.Message);
                RenderHud();
                return;
            }

            _session = session;
            _readView = session as IPuzzleReadView;
            _input = session as IPuzzleInput;

            if (_readView == null || _input == null)
            {
                // 会话没暴露解谜契约：说明工厂还没接推箱子分支（Turn 现在落回 TurnWorld）。
                // 此时绝不能用错的卡带继续推演，也不能让 HUD 显示一份假棋盘——立刻交还会话并关掉 HUD。
                host.Log.Error("[Puzzle] 会话未实现 IPuzzleReadView/IPuzzleInput（UnityKernelFactory 还没接 Puzzle 分支？），"
                    + "本关已放弃，HUD 已禁用。");

                _session = null;
                _readView = null;
                _input = null;
                _collected = true;
                host.Sessions.Release();

                if (hud != null)
                {
                    hud.enabled = false;
                }

                return;
            }

            RenderHud();

            host.Log.Info("[Puzzle] 开局：关卡=" + _currentLevel + " 种子=" + randomSeed
                + " 尺寸=" + _readView.Width + "×" + _readView.Height
                + " 箱子=" + _readView.BoxCount + " 自动步长=" + EffectiveAutoStepSeconds() + "s");
        }

        /// <summary>
        /// 按累加器推进一步内置解法。
        /// <para>
        /// <b>不追帧</b>：掉帧后只补一步，剩下的累加值清零——否则编辑器断点回来后会在几帧内把整关瞬移完。
        /// </para>
        /// <para>
        /// <b>为什么先 <c>Step</c> 再 <c>TryMove</c></b>：<see cref="IGameplaySession.Step"/> 对内核算术无影响，
        /// 只累加"推演步数"（进 <see cref="GameplayResult.StepCount"/> 上屏与对拍）；放在移动之前，
        /// 最后一步也能被计入（解出后内核会把 <c>Step</c> 变成空操作）。
        /// </para>
        /// </summary>
        private void AdvanceAutoSolve()
        {
            if (!_autoSolving)
            {
                return;
            }

            IPuzzleInput? input = _input;
            IGameplaySession? session = _session;
            int[]? solution = _solution;
            if (input == null || session == null || solution == null || _collected)
            {
                StopAutoSolve();
                return;
            }

            if (_solutionIndex < 0 || _solutionIndex >= solution.Length)
            {
                StopAutoSolve();
                return;
            }

            float interval = EffectiveAutoStepSeconds();
            _autoStepAccumulator += Time.deltaTime;
            if (_autoStepAccumulator < interval)
            {
                return;
            }

            _autoStepAccumulator -= interval;
            if (_autoStepAccumulator > interval)
            {
                _autoStepAccumulator = 0f;
            }

            int direction = solution[_solutionIndex];
            _solutionIndex++;

            // 先记一步推演再提交意图：内核在解出后会把 Step 变成空操作，放在后面会让最后一步漏记。
            session.Step(interval);

            if (!input.TryMove(direction))
            {
                // 解法与局面错位（关卡数据被改过 / 有人在自动走的过程中动过局面）：
                // 立刻停下，绝不把剩下的步骤继续喂进去——那只会让每一步都被拒，看起来像"按钮坏了"。
                AIFrameworkUnityHost? host = _host;
                if (host != null)
                {
                    host.Log.Warn("[Puzzle] 内置解法第 " + _solutionIndex + " 步被内核拒绝（方向=" + direction
                        + "），自动解法已停止：关卡=" + _currentLevel);
                }

                StopAutoSolve();
                return;
            }

            if (_solutionIndex >= solution.Length)
            {
                StopAutoSolve();
            }
        }

        /// <summary>停掉自动解法并清空进度（不改棋盘）。</summary>
        private void StopAutoSolve()
        {
            _autoSolving = false;
            _solution = null;
            _solutionIndex = 0;
            _autoStepAccumulator = 0f;
        }

        /// <summary>
        /// 结算：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示通关。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已胜利但物品还没到账"的中间态）。
        /// </para>
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _collected)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未解出/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;
            StopAutoSolve();

            int moves = Moves;

            GameplayResult result;
            try
            {
                result = host.Sessions.EndAndCollect();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Puzzle] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Puzzle, _currentLevel, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            if (hud != null)
            {
                hud.ShowSolved(moves);
            }

            host.Log.Info("[Puzzle] 结算：关卡=" + _currentLevel
                + " 胜负=" + result.IsVictory
                + " 步数=" + result.RemainingUnits
                + " 推演步数=" + result.StepCount
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>把 HUD 的三个点击事件接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            PuzzleHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnAutoSolveClicked += HandleAutoSolveClicked;
            view.OnResetClicked += HandleResetClicked;
            view.OnNextLevelClicked += HandleNextLevelClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            PuzzleHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnAutoSolveClicked -= HandleAutoSolveClicked;
            view.OnResetClicked -= HandleResetClicked;
            view.OnNextLevelClicked -= HandleNextLevelClicked;
        }

        /// <summary>点"自动解一关"。</summary>
        private void HandleAutoSolveClicked()
        {
            SolveDemo();
        }

        /// <summary>点"重置本关"。</summary>
        private void HandleResetClicked()
        {
            ResetLevel();
        }

        /// <summary>点"下一关"：1 → 2 → 3 → 1（只有内置三关，最后一关之后回到第一关）。</summary>
        private void HandleNextLevelClicked()
        {
            int next = _currentLevel >= MaxLevel ? MinLevel : _currentLevel + 1;
            LoadLevel(next);
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            PuzzleHud? view = hud;
            IPuzzleReadView? read = _readView;
            if (view != null && read != null)
            {
                view.Render(read);
            }
        }

        /// <summary>把任意 ID 夹到内置关卡区间：越界一律回到第 1 关（与内核的夹取规则一致）。</summary>
        /// <param name="level">外部传入的关卡 ID。</param>
        /// <returns>1..3 之间的关卡 ID。</returns>
        private static int ClampLevel(int level)
        {
            if (level < MinLevel || level > MaxLevel)
            {
                return MinLevel;
            }

            return level;
        }

        /// <summary>取某关的内置解法。<b>返回的是静态数组，调用方只读不写。</b></summary>
        /// <param name="level">已夹取到 1..3 的关卡 ID。</param>
        /// <returns>方向序列（0=上 1=右 2=下 3=左）。</returns>
        private static int[] SolutionFor(int level)
        {
            switch (level)
            {
                case 2:
                    return SolutionLevel2;
                case 3:
                    return SolutionLevel3;
                default:
                    return SolutionLevel1;
            }
        }

        /// <summary>自动解法间隔：字段被改成非法值时退回 <see cref="FallbackAutoStepSeconds"/>。</summary>
        /// <returns>大于 0 的间隔秒数。</returns>
        private float EffectiveAutoStepSeconds()
        {
            float interval = autoStepSeconds;
            return interval > 0f ? interval : FallbackAutoStepSeconds;
        }
    }
}
