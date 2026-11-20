#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Kernel.Turn.Puzzle;
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
    /// <b>为什么只在"建外部关卡"这一处碰内核具体类型</b>：<see cref="GameplayInitData"/> 只带
    /// <see cref="GameplayInitData.LevelId"/> 与种子，<c>IGameplayKernelFactory</c> 没有"把布局传进去"的参数，
    /// 所以编辑器/配置表导出的关卡只能由本类直接
    /// <c>new PuzzleWorld(init, rows, source)</c> 建出来（<b>唯一的</b>内核具体类型引用）。
    /// 换关、推演、结算、渲染仍只走 <see cref="IGameplaySession"/> / <see cref="IPuzzleReadView"/> /
    /// <see cref="IPuzzleInput"/>。工厂一旦支持透传行数组，这里应改回 <c>host.Sessions.Begin</c>——
    /// 这是本任务的架构债，已在交付报告里写明。
    /// </para>
    /// <para>
    /// <b>外部关卡为什么不进 host.Sessions</b>：宿主会话表只认 <see cref="GameplayInitData"/>，外部关卡没有
    /// 能交给工厂的载荷，因此由本控制器直接持有会话（<c>_directSession</c>）。代价是这段时间
    /// <c>host.Sessions.HasActiveSession == false</c>，结算也必须由本类直接
    /// <see cref="IGameplaySession.CollectResult"/>（<see cref="Finish"/> 已按该标记分流），
    /// 不能走 <c>EndAndCollect</c>——否则会抛"没有活跃会话"，通关奖励与 <c>GameFinishedEvent</c> 全部丢失。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 计时而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。累加器 + <c>Time.deltaTime</c> 只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>关卡 JSON（T5-9）</b>：<see cref="Awake"/> 里先按 <see cref="puzzleDataFile"/>（可配）→
    /// <c>Levels/puzzle-&lt;levelId&gt;</c> → <c>Levels/puzzle-demo</c> 的顺序找
    /// <c>Resources</c> 下的关卡 JSON，解析并校验（格式见 <see cref="PuzzleLevelJson"/>）。
    /// 通过 → <b>直接用该布局建会话</b>（见上）；缺失/非法/内核拒绝 → <c>Log.Warn</c> 并回退内置关卡，
    /// 绝不让玩法崩。
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

        /// <summary>
        /// 外部关卡求解的状态预算（BFS 展开上限）。<b>为什么必须有上限</b>：8×8 上三四个箱子的状态空间是百万级，
        /// 点一次按钮卡死一帧比"解得慢"更糟；超预算就明确告警并跳过自动解法，绝不无限搜。
        /// </summary>
        private const int MaxSolverStates = 8000;

        /// <summary>第 1 关内置解法：上、上、上（0=上 1=右 2=下 3=左），3 步。</summary>
        private static readonly int[] SolutionLevel1 = { 1, 1, 1 };

        /// <summary>
        /// 第 2 关内置解法：需要绕到箱子另一侧再推，9 步。
        /// <b>步序由离屏 BFS 复核过</b>（原值 <c>{2,1,1,0,3,0,3,0,0}</c> 会在第 6 步被内核拒绝、解不开本关）。
        /// </summary>
        private static readonly int[] SolutionLevel2 = { 2, 1, 1, 0, 3, 2, 3, 0, 0 };

        /// <summary>
        /// 第 3 关内置解法：两个箱子两个目标点，9 步。
        /// <b>步序由离屏 BFS 复核过</b>（原值 <c>{1,0,0,0,0,3,3,3,0}</c> 第 5 步就会把箱子推离目标、解不开本关）。
        /// </summary>
        private static readonly int[] SolutionLevel3 = { 1, 0, 0, 0, 2, 3, 3, 3, 0 };

        /// <summary>解谜皮肤（渲染状态行与字符网格，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private PuzzleHud hud = null!;

        /// <summary>入场关卡 ID（1..3，其它值夹到 1）。</summary>
        [SerializeField] private int levelId = 1;

        /// <summary>随机种子：内置关卡是确定的，仍按门禁契约透传，便于将来换成配置表关卡。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>
        /// 关卡 JSON 的 <c>Resources</c> 名（可配）。可写 <c>puzzle-demo</c> 或 <c>Levels/puzzle-demo</c>；
        /// 留空时按关卡 ID 推导 <c>Levels/puzzle-&lt;levelId&gt;</c>，再退到示例 <c>Levels/puzzle-demo</c>。
        /// </summary>
        [SerializeField] private string puzzleDataFile = string.Empty;

        /// <summary>本次从 Resources 读到的关卡 JSON；<c>null</c> 表示没读到或没通过校验（此时走内置关卡）。</summary>
        private PuzzleLevelData? _levelData;

        /// <summary>
        /// 当前会话是否由本控制器**直接**构造（外部 JSON 关卡），而非宿主 <c>Sessions</c> 托管。
        /// <para>宿主会话表只认 <see cref="GameplayInitData"/>，不认"外部布局"，所以外部关卡必须绕开它；
        /// 代价是释放时要按这个标记区分，否则会去释放一个不在表里的会话。</para>
        /// </summary>
        private bool _directSession;

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

            bool external = _directSession;
            int[]? solution;
            if (external)
            {
                // 外部关卡不在"内置三关"的解法表里，用内核自身当裁判现求一条（不复制推箱规则）。
                int explored;
                solution = TrySolveExternalLevel(out explored);
                if (solution == null)
                {
                    host.Log.Warn("[Puzzle] 外部关卡在 " + MaxSolverStates + " 个状态内没解出来，已跳过自动解：关卡="
                        + _currentLevel + "，尺寸=" + _readView.Width + "×" + _readView.Height
                        + "，箱子=" + _readView.BoxCount);
                    StopAutoSolve();
                    return;
                }

                host.Log.Info("[Puzzle] 外部关卡已求出解法：" + solution.Length + " 步（BFS 展开 " + explored + " 个状态）。");
            }
            else
            {
                solution = SolutionFor(_currentLevel);
            }

            _solution = solution;
            _solutionIndex = 0;

            // 先垫满一个间隔：点完立刻在下一帧走第一步，别让玩家对着静止棋盘等 0.35 秒。
            _autoStepAccumulator = EffectiveAutoStepSeconds();
            _autoSolving = true;

            host.Log.Info("[Puzzle] 自动解法开始：关卡=" + _currentLevel + (external ? "（外部关卡）" : string.Empty)
                + " 共 " + _solution.Length + " 步，间隔 " + EffectiveAutoStepSeconds() + "s");
        }

        /// <summary>
        /// 为<b>外部关卡</b>求一条解：BFS，但每一步的合法性都由内核回答
        /// （<see cref="IPuzzleInput.TryMove"/> 返回 false 即非法），<b>适配层不复制任何推箱规则</b>。
        /// <para>
        /// <b>为什么用"重放路径"而不是保存局面快照</b>：<c>PuzzleWorld</c> 没有克隆入口，而
        /// <see cref="IPuzzleReadView"/> 只读。因此每个节点只保存方向序列，展开前 <c>Reset</c> 再重放；
        /// 代价是 O(深度) 次重放，对 8×8 小关卡完全够用，且不需要给内核加新接口。
        /// </para>
        /// <para>
        /// <b>为什么去重键是"玩家位置 + 箱子位图"</b>：这两者完全决定局面（地形是静态的、已归位计数由箱子决定），
        /// 64 格以内正好塞进一个 <see cref="ulong"/>。
        /// </para>
        /// </summary>
        /// <param name="explored">展开的状态数（写日志用）。</param>
        /// <returns>方向序列；预算内无解返回 <c>null</c>。返回前会把棋盘 <c>Reset</c> 回初始局面。</returns>
        private int[]? TrySolveExternalLevel(out int explored)
        {
            explored = 0;
            IPuzzleInput? input = _input;
            IPuzzleReadView? view = _readView;
            if (input == null || view == null)
            {
                return null;
            }

            if (view.IsSolved)
            {
                return Array.Empty<int>();
            }

            var visited = new HashSet<(int Player, ulong Boxes)>();
            var queue = new Queue<int[]>();
            queue.Enqueue(Array.Empty<int>());
            visited.Add(StateOf(view));

            while (queue.Count > 0 && explored < MaxSolverStates)
            {
                int[] path = queue.Dequeue();
                explored++;

                for (int direction = 0; direction < 4; direction++)
                {
                    if (!ReplayAndMove(input, path, direction))
                    {
                        continue;
                    }

                    (int Player, ulong Boxes) state = StateOf(view);
                    if (!visited.Add(state))
                    {
                        continue;
                    }

                    var next = new int[path.Length + 1];
                    Array.Copy(path, next, path.Length);
                    next[path.Length] = direction;

                    if (view.IsSolved)
                    {
                        input.Reset();
                        return next;
                    }

                    queue.Enqueue(next);
                }
            }

            input.Reset();
            return null;
        }

        /// <summary>复位后重放 <paramref name="path"/>，再试走一步；返回该步是否被内核接受。</summary>
        /// <param name="input">输入端口。</param>
        /// <param name="path">从初始局面出发的方向序列。</param>
        /// <param name="direction">本步方向（0=上 1=右 2=下 3=左）。</param>
        /// <returns>该步是否合法。</returns>
        private static bool ReplayAndMove(IPuzzleInput input, int[] path, int direction)
        {
            input.Reset();
            for (int i = 0; i < path.Length; i++)
            {
                if (!input.TryMove(path[i]))
                {
                    return false;
                }
            }

            return input.TryMove(direction);
        }

        /// <summary>只读视图 → 搜索去重键（玩家线性下标 + 箱子位图）。</summary>
        /// <param name="view">解谜只读视图。</param>
        /// <returns>去重键；只用于 BFS，不参与任何规则判定。</returns>
        private static (int Player, ulong Boxes) StateOf(IPuzzleReadView view)
        {
            ulong boxes = 0;
            int index = 0;
            for (int y = 0; y < view.Height; y++)
            {
                for (int x = 0; x < view.Width; x++)
                {
                    if (view.HasBox(x, y))
                    {
                        boxes |= 1UL << index;
                    }

                    index++;
                }
            }

            return ((view.PlayerY * view.Width) + view.PlayerX, boxes);
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的按钮订阅与首帧渲染要在
        /// 玩家可能点下去之前就绪；挂到 <c>Start</c> 会留一个"第一次点击没反应"的空窗。
        /// <para>
        /// 顺序固定为"建宿主 → 订 HUD → 读关卡 JSON → 开局 → 报告 JSON 与内核布局的比对结果"：
        /// JSON 只在这条链上做校验与诊断，开局仍然交给内核；读 JSON 失败不会改变开局流程。
        /// </para>
        /// </summary>
        private void Awake()
        {
            var host = new AIFrameworkUnityHost();
            host.Initialize();
            _host = host;

            SubscribeHud();

            int level = ClampLevel(levelId);
            TryLoadLevelJson(level);
            BeginSession(level);
            ReportLevelJsonUsage();
        }

        /// <summary>
        /// 尝试从 <c>Resources</c> 读关卡 JSON：读到就解析 + 校验，并把摘要写进日志。
        /// <b>任何失败路径都只告警、不抛、不改开局流程</b>——缺一个开发者数据文件不该让玩法停摆。
        /// </summary>
        /// <param name="level">已夹取到 1..3 的关卡 ID（用于推导默认资源名）。</param>
        private void TryLoadLevelJson(int level)
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            string? resourceName = FindLevelResourceName(level);
            if (resourceName == null)
            {
                host.Log.Warn("[Puzzle] 未找到关卡 JSON（" + PuzzleLevelJson.ResourceNameForLevel(level)
                    + " 与 " + PuzzleLevelJson.ResourceNameFor(PuzzleLevelJson.DemoId)
                    + " 都不存在），改用内置关卡 " + level + "。");
                return;
            }

            TextAsset? asset = Resources.Load<TextAsset>(resourceName);
            if (asset == null)
            {
                host.Log.Warn("[Puzzle] 关卡 JSON 无法作为 TextAsset 读取：" + resourceName
                    + "，改用内置关卡 " + level + "。");
                return;
            }

            PuzzleLevelData? data;
            IReadOnlyList<string> errors;
            if (!PuzzleLevelJson.TryParse(asset.text, out data, out errors) || data == null)
            {
                host.Log.Warn("[Puzzle] 关卡 JSON 校验未通过：" + resourceName + "（共 " + errors.Count
                    + " 项），改用内置关卡 " + level + "。");
                for (int i = 0; i < errors.Count; i++)
                {
                    host.Log.Warn("[Puzzle] - " + errors[i]);
                }

                return;
            }

            _levelData = data;
            host.Log.Info("[Puzzle] 关卡 JSON 已载入：" + resourceName + "（id=" + data.Id
                + "，" + data.Width + "×" + data.Height
                + "，玩家=(" + data.PlayerX + "," + data.PlayerY + ")"
                + "，箱子=" + data.BoxCount + "，目标=" + data.GoalCount + "）。");
        }

        /// <summary>
        /// 按优先级找关卡 JSON 的 <c>Resources</c> 名：<see cref="puzzleDataFile"/>（显式配置）→
        /// <c>Levels/puzzle-&lt;levelId&gt;</c>（按关卡编号）→ <c>Levels/puzzle-demo</c>（示例兜底）。
        /// </summary>
        /// <param name="level">已夹取到 1..3 的关卡 ID。</param>
        /// <returns>资源名；一个都不存在时返回 <c>null</c>。</returns>
        private string? FindLevelResourceName(int level)
        {
            string configured = puzzleDataFile == null ? string.Empty : puzzleDataFile.Trim();
            if (configured.Length > 0)
            {
                // 允许两种写法："puzzle-demo" 与 "Levels/puzzle-demo"。
                return configured.IndexOf('/') >= 0 ? configured : PuzzleLevelJson.ResourceNameFor(configured);
            }

            string byLevel = PuzzleLevelJson.ResourceNameForLevel(level);
            if (Resources.Load<TextAsset>(byLevel) != null)
            {
                return byLevel;
            }

            string demo = PuzzleLevelJson.ResourceNameFor(PuzzleLevelJson.DemoId);
            return Resources.Load<TextAsset>(demo) != null ? demo : null;
        }

        /// <summary>
        /// 把"JSON 与内核实际布局"的关系明确写进日志：一致 → Info；不一致 → Warn 并说明
        /// 内核尚未支持外部布局（**不静默**，否则以后没人知道 JSON 没生效）。
        /// </summary>
        private void ReportLevelJsonUsage()
        {
            AIFrameworkUnityHost? host = _host;
            PuzzleLevelData? data = _levelData;
            IPuzzleReadView? view = _readView;
            if (host == null || data == null || view == null)
            {
                return;
            }

            if (data.Width != view.Width || data.Height != view.Height)
            {
                host.Log.Warn("[Puzzle] 关卡 JSON 布局尚未生效：适配层的会话工厂只按 LevelId 建内置关卡的 PuzzleWorld，"
                    + "本次运行内置第 " + _currentLevel + " 关（" + view.Width + "×" + view.Height
                    + "），JSON 是 " + data.Width + "×" + data.Height
                    + "；JSON 仅用于数据校验与编辑器回读。");
                return;
            }

            int mismatches = CountLayoutMismatches(data, view);
            if (mismatches == 0)
            {
                host.Log.Info("[Puzzle] 关卡 JSON 与内核当前布局逐格一致（" + data.Width + "×" + data.Height + "）。");
                return;
            }

            host.Log.Warn("[Puzzle] 关卡 JSON 与内核内置第 " + _currentLevel + " 关有 " + mismatches
                + " 格/项不一致；本次仍以内置关卡为准（JSON 未注入棋盘）。");
        }

        /// <summary>逐格比较 JSON 与只读视图的地形/箱子，并比较玩家位置；返回不一致的格数（玩家算 1 项）。</summary>
        /// <param name="data">已通过校验的关卡 JSON。</param>
        /// <param name="view">内核当前关卡的只读视图。</param>
        /// <returns>不一致计数。</returns>
        private static int CountLayoutMismatches(PuzzleLevelData data, IPuzzleReadView view)
        {
            int mismatches = 0;
            for (int y = 0; y < data.Height; y++)
            {
                for (int x = 0; x < data.Width; x++)
                {
                    char tile = data.GetTile(x, y);
                    PuzzleCell expected = PuzzleLevelJson.IsWall(tile)
                        ? PuzzleCell.Wall
                        : PuzzleLevelJson.IsGoal(tile) ? PuzzleCell.Goal : PuzzleCell.Floor;

                    if (view.GetCell(x, y) != expected || view.HasBox(x, y) != PuzzleLevelJson.IsBox(tile))
                    {
                        mismatches++;
                    }
                }
            }

            if (view.PlayerX != data.PlayerX || view.PlayerY != data.PlayerY)
            {
                mismatches++;
            }

            return mismatches;
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
            _directSession = false;
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

            // 先记下"上一局是不是外部关卡"，再复位标记：顺序反了会让下面的释放分支永远走不到。
            // 外部关卡不在宿主会话表里，直接丢引用即可（PuzzleWorld 不是 IDisposable，也没有原生资源）。
            bool previousWasDirect = _directSession;

            _currentLevel = level;
            _session = null;
            _readView = null;
            _input = null;
            _collected = false;
            _directSession = false;

            if (!previousWasDirect && host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            var init = new GameplayInitData(level, randomSeed, PuzzleUnitCount);

            // 优先用编辑器/配置表导出的关卡 JSON 建局：内核侧的"外部行"构造是 T5-9 的唯一入口。
            // 外部关卡不经过 host.Sessions（宿主会话表只认 GameplayInitData），由本控制器直接持有，
            // 所以释放路径用 _directSession 区分。
            IGameplaySession? external = TryCreateExternalSession(host, in init);
            if (external != null)
            {
                _session = external;
                _directSession = true;
                _readView = external as IPuzzleReadView;
                _input = external as IPuzzleInput;

                if (_readView != null && _input != null)
                {
                    RenderHud();
                    ReportExternalLevelApplied(host, _levelData!);
                    host.Log.Info("[Puzzle] 开局（外部关卡）：尺寸=" + _readView.Width + "×" + _readView.Height
                        + " 箱子=" + _readView.BoxCount + " 自动步长=" + EffectiveAutoStepSeconds() + "s");
                    return;
                }

                // 理论上不可达（PuzzleWorld 同时实现三张契约）；真到了这里也绝不能用错的状态继续跑。
                host.Log.Error("[Puzzle] 外部关卡会话未实现解谜契约，已回退内置关卡。");
                _session = null;
                _readView = null;
                _input = null;
                _directSession = false;
            }

            IGameplaySession session;
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
        /// 尝试用已校验的关卡 JSON 建外部会话：解析通过但内核拒绝（布局非法）时记 Warn 并返回 null，
        /// 由调用方回退内置关卡——缺一个关卡文件绝不能让玩法崩。
        /// </summary>
        private IGameplaySession? TryCreateExternalSession(AIFrameworkUnityHost host, in GameplayInitData init)
        {
            PuzzleLevelData? data = _levelData;
            if (data == null)
            {
                return null;
            }

            try
            {
                return new PuzzleWorld(in init, ToKernelRows(data.GetRows()), data.Id + ".json");
            }
            catch (ArgumentException exception)
            {
                host.Log.Warn("[Puzzle] 关卡 JSON 未被内核接受，已回退内置关卡：" + exception.Message);
                return null;
            }
        }

        /// <summary>
        /// JSON 方言 → 内核方言（**这是两套字符约定，必须显式翻译**）。
        /// <para>
        /// JSON/编辑器用可读方言：<c>.</c>=空地、<c>O</c>=目标、<c>X</c>=箱子；
        /// 内核沿用文本关卡约定：空格=空地、<c>.</c>=目标、<c>$</c>=箱子。
        /// 直接把 JSON 行喂给内核会得到"未知字符"并静默回退内置关卡——这条翻译是数据驱动关卡真生效的关键一步。
        /// </para>
        /// </summary>
        private static string[] ToKernelRows(string[] jsonRows)
        {
            var rows = new string[jsonRows.Length];
            for (int row = 0; row < jsonRows.Length; row++)
            {
                string source = jsonRows[row];
                var chars = new char[source.Length];
                for (int x = 0; x < source.Length; x++)
                {
                    char tile = source[x];
                    switch (tile)
                    {
                        case '.':
                            chars[x] = ' ';   // 空地
                            break;
                        case 'O':
                            chars[x] = '.';   // 目标
                            break;
                        case 'X':
                            chars[x] = '$';   // 箱子
                            break;
                        default:
                            chars[x] = tile;  // # @ * + 两套约定一致
                            break;
                    }
                }

                rows[row] = new string(chars);
            }

            return rows;
        }

        /// <summary>外部关卡生效后的日志：把"数据驱动关卡真的进了棋盘"写在明处（不是只打印一遍 JSON）。</summary>
        private static void ReportExternalLevelApplied(AIFrameworkUnityHost host, PuzzleLevelData data)
        {
            host.Log.Info("[Puzzle] 关卡 JSON 已注入棋盘：" + data.Id + "（" + data.Width + "×" + data.Height
                + "，" + data.BoxCount + " 箱 / " + data.GoalCount + " 目标）。");
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
        /// 结算：外部关卡走 <see cref="IGameplaySession.CollectResult"/>、内置关卡走 <c>EndAndCollect</c>，
        /// 然后广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示通关。
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
            // 外部关卡不在宿主会话表里，只能直接向会话要结算；内置关卡仍走 EndAndCollect。
            IGameplaySession? direct = _directSession ? _session : null;
            _collected = true;
            _session = null;
            _directSession = false;
            StopAutoSolve();

            int moves = Moves;

            GameplayResult result;
            try
            {
                result = direct != null ? direct.CollectResult() : host.Sessions.EndAndCollect();
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
