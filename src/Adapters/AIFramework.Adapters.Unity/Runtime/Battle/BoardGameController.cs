#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.BoardGame;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 黑白棋场景控制器：建宿主 → 直接构造 <see cref="BoardGameWorld"/> → 按 0.5 秒节拍推进一步 →
    /// 只读视图推给 <see cref="BoardGameHud"/> → 终局结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new BoardGameWorld</c>，而其它玩法控制器要走 <c>host.Sessions.Begin</c></b>：
    /// 适配层是组合根（<c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 没有 BoardGame 值、<c>UnityKernelFactory</c> 也没有对应分支
    /// （与 <c>RhythmController</c> 完全同因）。为了一个纯演示场景去扩枚举与工厂，会让存档协议与契约
    /// 为一套"对主板完全同构"的卡带付长期成本；因此这里直接构造并<b>自己持有这次会话</b>
    /// （不登记进 <c>host.Sessions</c>），结算时直接调 <see cref="IGameplaySession.CollectResult"/>。
    /// 宿主仍然负责总线、日志与背包——本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何棋类规则</b>：合法性（夹住对方棋子）、翻转、自动跳过、胜负与 AI 搜索
    /// 全部由内核决定；本类只做四件事：装配宿主、把帧时间翻译成"该谁走一步"、把只读视图推给 HUD、
    /// 终局结算并广播事实。适配层一旦自己算一遍翻转数（哪怕是"看起来等价"的复算），
    /// 改内核而不改 UI 时两侧就会静默分叉。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次计时器只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>黑方（本地玩家）的 <c>autoPlay</c> 只能"探测"合法点，不能"搜索"</b>：内核的 AI
    /// （<c>BoardGameAi</c> / <c>BoardGameRules</c>）是 <c>internal</c>，公开面只有
    /// <see cref="IBoardGameInput.TryPlace"/>，且它只收黑方、白方必须走
    /// <see cref="IBoardGameInput.PlayAiTurn"/>。因此自动演示时黑方只能按<b>探测序</b>逐个调
    /// <c>TryPlace</c>、用返回值筛出第一个合法点——<b>绝不复制翻转规则</b>，也不假装黑方也有一个搜索 AI。
    /// 难度只影响这条探测序（见 <see cref="BuildProbeOrder"/>），白方的强弱仍由内核
    /// <see cref="BoardGameDifficulty"/> 决定。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BoardGameController : MonoBehaviour
    {
        /// <summary>
        /// 场次 ID：<b>不暴露成 <c>[SerializeField]</c></b>。黑白棋只有一张固定 8×8 盘，
        /// "选关"在本玩法里是个伪概念；把它做成可配字段只会让人以为换个数就换了局面。
        /// </summary>
        private const int DefaultLevelId = 1;

        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：棋类是单人玩法，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对（内核用例为了简洁传 0，两者等价）。
        /// </summary>
        private const int SinglePlayerUnitCount = 1;

        /// <summary>
        /// 自动推进的节拍（秒）：<b>每 0.5 秒最多走一步</b>。再快看不出翻转过程，
        /// 再慢演示一局要等十几秒；0.5 秒也正好让"落子 → 翻转"的视觉变化被人眼接住。
        /// </summary>
        private const float AutoStepIntervalSeconds = 0.5f;

        /// <summary>棋盘边长（内核固定 8；这里只用于把格下标换算成 (x, y)）。</summary>
        private const int BoardSize = 8;

        /// <summary>难度上界（<see cref="BoardGameDifficulty.Hard"/> = 2）。</summary>
        private const int MaxDifficulty = (int)BoardGameDifficulty.Hard;

        /// <summary>
        /// 黑方探测序（三档难度各一条，类型初始化时算一次）：值 = <c>y * Size + x</c>。
        /// <b>为什么不按难度写三套搜索</b>：见类型注释——黑方搜索不在公开面里，
        /// 这里只是"先试哪些格子"的顺序，强弱差异仅来自四个角/四条边的优先权。
        /// </summary>
        private static readonly int[][] ProbeOrders =
        {
            BuildProbeOrder(BoardGameDifficulty.Easy),
            BuildProbeOrder(BoardGameDifficulty.Normal),
            BuildProbeOrder(BoardGameDifficulty.Hard)
        };

        /// <summary>黑白棋皮肤（渲染状态行与 8×8 字符盘，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private BoardGameHud hud = null!;

        /// <summary>随机种子：固定值让 Easy 难度的随机落子与"同种子同开局"可复现。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>白方 AI 难度：0 Easy / 1 Normal / 2 Hard（越界按 Normal 处理，夹取属于内核规则）。</summary>
        [SerializeField] private int difficulty = 2;

        /// <summary>
        /// 是否自动演示：为 true 时黑方回合也自动落子（见类型注释的探测序），
        /// 为 false 时黑方只由 <see cref="Place"/> 驱动，白方仍在 <see cref="Update"/> 里自动应手。
        /// </summary>
        [SerializeField] private bool autoPlay = true;

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前对局会话（<see cref="IGameplaySession"/> 视角，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空，表示会话所有权已交还；重开时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一局的具体类型：四个计数与探测序不在契约里，只有具体类型能读。
        /// 结算后<b>保留</b>（HUD 要继续显示最终盘面与子数），重开时替换。
        /// </summary>
        private BoardGameWorld? _world;

        /// <summary>本局是否已结算（<c>CollectResult</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>距上一步（自动落子 / 白方应手 / 玩家手动落子）累计的秒数。</summary>
        private float _stepTimer;

        /// <summary>黑子数（无会话时为 0）。</summary>
        public int BlackCount
        {
            get
            {
                BoardGameWorld? world = _world;
                return world != null ? world.BlackCount : 0;
            }
        }

        /// <summary>白子数（无会话时为 0）。</summary>
        public int WhiteCount
        {
            get
            {
                BoardGameWorld? world = _world;
                return world != null ? world.WhiteCount : 0;
            }
        }

        /// <summary>对局是否结束（含"结算已取回"）；无会话时为 false。</summary>
        public bool IsFinished
        {
            get
            {
                BoardGameWorld? world = _world;
                return world != null && world.IsFinished;
            }
        }

        /// <summary>
        /// 重开一局：换一个全新的 <see cref="BoardGameWorld"/>（等价于"再来一次"）。
        /// <para>
        /// <b>为什么必须重建而不是"清空"</b>：内核在 <c>CollectResult</c> 之后把会话所有权交还，
        /// <c>TryPlace</c>/<c>PlayAiTurn</c>/<c>Restart</c> 全部冻结；只有重新构造才能真正回到初始局面。
        /// 同种子重建与内核 <c>Restart</c> 等价（随机流都拨回进门种子），差异只有会话级
        /// <c>PassCount</c> 的累计口径——它不进任何结算与展示，重建不会丢失可见状态。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是 MonoBehaviour），它的
        /// <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"已终局但还没结算"的那一帧，
        /// 直接重建会把这一局的 <c>GameFinishedEvent</c> 与战利品静默吞掉。终局就先走
        /// <see cref="Settle"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// <para>局中调用同样安全：旧局还没结算就直接丢弃（纯内存对象，没有原生资源）。</para>
        /// </summary>
        public void Restart()
        {
            if (!_collected && _world != null && _world.IsFinished)
            {
                Settle();
            }

            CreateWorld();
        }

        /// <summary>
        /// 在 (x, y) 落黑子。<b>不在这里判合法性</b>：越界、已有子、夹不住对方棋子、白方回合、
        /// 对局结束——一律由内核 <see cref="IBoardGameInput.TryPlace"/> 返回 false，本类不做第二份校验。
        /// <para>
        /// 落子成功后把计时器清零，白方 AI 在下一次 <see cref="Update"/> 节拍（0.5 秒后）应手，
        /// 让"我下一手 → 对手想一下 → 再下一手"的节奏可读。
        /// </para>
        /// </summary>
        /// <param name="x">列（0..7，向右）。</param>
        /// <param name="y">行（0..7，向下）。</param>
        public void Place(int x, int y)
        {
            BoardGameWorld? world = _world;
            if (world == null || _collected)
            {
                return;
            }

            if (!world.TryPlace(x, y))
            {
                return;
            }

            _stepTimer = 0f;
            RenderHud();
            SettleIfFinished();
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c>：HUD 按钮的订阅与首帧渲染要在
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
        /// <b>顺序不可换</b>：先推进再渲染，玩家看到的就是这一步之后的真实盘面与子数；
        /// 先渲染再推进会让画面永远慢一步（终局也要多等一拍才显示结果）。
        /// </summary>
        private void Update()
        {
            BoardGameWorld? world = _world;
            if (world == null || _collected || world.IsFinished)
            {
                return;
            }

            _stepTimer += Time.deltaTime;
            if (_stepTimer < AutoStepIntervalSeconds)
            {
                return;
            }

            // 只扣一次节拍（不补跑）：编辑器编译/切窗口产生的长帧不该让棋盘"连跳好几步"，
            // 那会让玩家以为自己漏看了一整段对局。
            _stepTimer = 0f;

            StepOnce();
            RenderHud();
            SettleIfFinished();
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
        /// 建（或重建）一局：构造内核 → 首帧渲染。
        /// <para>
        /// <b>为什么不经过 <c>host.Sessions</c></b>：宿主只按 <see cref="GameplayKernelType"/> 建会话，
        /// 而 BoardGame 没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
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
            _stepTimer = 0f;

            var init = new GameplayInitData(DefaultLevelId, randomSeed, SinglePlayerUnitCount);
            var world = new BoardGameWorld(in init);

            _session = world;
            _world = world;

            RenderHud();

            host.Log.Info("[BoardGame] 开局：场次=" + DefaultLevelId
                + " 种子=" + randomSeed
                + " 难度=" + EffectiveDifficulty()
                + " 自动演示=" + autoPlay);
        }

        /// <summary>
        /// 走一步：白方回合交给内核 AI；黑方回合只在 <see cref="autoPlay"/> 打开时按探测序落子。
        /// <para>
        /// <b><c>autoPlay = false</c> 时黑方不自动走</b>：那一侧是玩家的操作面，这里替他落子会让人
        /// 分不清"我点的"和"它自己下的"。但白方仍然自动应手——否则关掉自动演示后棋盘会停在白方回合死等。
        /// </para>
        /// </summary>
        private void StepOnce()
        {
            BoardGameWorld? world = _world;
            if (world == null || world.IsFinished)
            {
                return;
            }

            if (world.CurrentPlayer == (int)BoardCell.White)
            {
                world.PlayAiTurn(EffectiveDifficulty());
                return;
            }

            if (!autoPlay)
            {
                return;
            }

            AutoPlaceBlack(world);
        }

        /// <summary>
        /// 黑方自动落子：按 <see cref="ProbeOrders"/> 里当前难度的顺序逐个调 <c>TryPlace</c>，
        /// 第一个返回 true 的格子就是这一步。
        /// <para>
        /// <b>为什么用"试"而不是"算"</b>：合法性（八向夹子）是内核规则，适配层重算一遍就等于把同一份
        /// 规则写两遍；而 <c>TryPlace</c> 本身就是"提交并告诉你成不成"的公开入口，用它探测既不需要
        /// 任何 <c>internal</c> 类型，也不可能与内核判出不同结果。
        /// </para>
        /// <para>
        /// <b>为什么一定会成功</b>：只要 <see cref="IBoardGameReadView.IsFinished"/> 为 false 且
        /// <see cref="IBoardGameReadView.CurrentPlayer"/> 是黑，内核保证黑方至少有一步合法手
        /// （无手会自动跳过）。真的走到"一个都不成"时本类不做任何补救——不伪造落子、不跳过回合。
        /// </para>
        /// </summary>
        /// <param name="world">当前对局。</param>
        /// <returns>是否成功落下黑子。</returns>
        private bool AutoPlaceBlack(BoardGameWorld world)
        {
            int[] order = ProbeOrders[DifficultyIndex()];
            for (int i = 0; i < order.Length; i++)
            {
                int cell = order[i];
                if (world.TryPlace(cell % BoardSize, cell / BoardSize))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 三档难度的探测序：<b>Easy</b> 行主序（第一个合法点，最"瞎"）；
        /// <b>Normal</b> 先试四角再行主序；<b>Hard</b> 先四角、再四边、最后行主序。
        /// <para>
        /// 顺序只决定"先试哪个格子"，不改变任何合法性判定；角优先是黑白棋的通用先验
        /// （角上的子永远翻不掉），因此 Hard 的黑方看起来会稍微像个对手。
        /// </para>
        /// </summary>
        /// <param name="difficulty">目标难度。</param>
        /// <returns>长度 64 的格下标数组（<c>y * Size + x</c>）。</returns>
        private static int[] BuildProbeOrder(BoardGameDifficulty difficulty)
        {
            var order = new List<int>(BoardSize * BoardSize);
            var taken = new bool[BoardSize * BoardSize];

            if (difficulty != BoardGameDifficulty.Easy)
            {
                int[] corners = { 0, (BoardSize - 1), (BoardSize * (BoardSize - 1)), (BoardSize * BoardSize) - 1 };
                for (int i = 0; i < corners.Length; i++)
                {
                    AppendCell(order, taken, corners[i]);
                }

                if (difficulty == BoardGameDifficulty.Hard)
                {
                    for (int x = 0; x < BoardSize; x++)
                    {
                        AppendCell(order, taken, x);
                        AppendCell(order, taken, ((BoardSize - 1) * BoardSize) + x);
                    }

                    for (int y = 0; y < BoardSize; y++)
                    {
                        AppendCell(order, taken, y * BoardSize);
                        AppendCell(order, taken, (y * BoardSize) + BoardSize - 1);
                    }
                }
            }

            for (int cell = 0; cell < BoardSize * BoardSize; cell++)
            {
                AppendCell(order, taken, cell);
            }

            return order.ToArray();
        }

        /// <summary>把格下标追加进探测序（越界或重复则忽略）。</summary>
        /// <param name="order">探测序。</param>
        /// <param name="taken">已加入标记（与棋盘同长）。</param>
        /// <param name="cell">格下标（<c>y * Size + x</c>）。</param>
        private static void AppendCell(List<int> order, bool[] taken, int cell)
        {
            if (cell < 0 || cell >= taken.Length || taken[cell])
            {
                return;
            }

            taken[cell] = true;
            order.Add(cell);
        }

        /// <summary>
        /// 把 HUD 的"重新开始 / 64 个格子按钮"接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。
        /// </summary>
        private void SubscribeHud()
        {
            BoardGameHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked += HandleRestartClicked;
            view.OnCellClicked += HandleCellClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            BoardGameHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked -= HandleRestartClicked;
            view.OnCellClicked -= HandleCellClicked;
        }

        /// <summary>点"重新开始"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>点某个格子：坐标已由 HUD 换算成内核口径，这里直接提交给内核。</summary>
        /// <param name="x">列（0..7）。</param>
        /// <param name="y">行（0..7）。</param>
        private void HandleCellClicked(int x, int y)
        {
            Place(x, y);
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            BoardGameHud? view = hud;
            BoardGameWorld? world = _world;
            if (view != null && world != null)
            {
                view.Render(world);
            }
        }

        /// <summary>终局才结算（未终局直接返回，避免在热路径上多判一次 <c>CollectResult</c> 的前置条件）。</summary>
        private void SettleIfFinished()
        {
            BoardGameWorld? world = _world;
            if (world == null || _collected || !world.IsFinished)
            {
                return;
            }

            Settle();
        }

        /// <summary>
        /// 结算：<c>CollectResult</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示结果。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>为什么广播用固定的 <see cref="DefaultLevelId"/></b>：它是这次"场次"的身份
        /// （与 Tetris 的 <c>DefaultLevelId</c> 同义）；黑白棋没有多关卡，场次恒为 1，
        /// 不把"内核回报的什么数"冒充成玩家选过的关卡。
        /// </para>
        /// </summary>
        private void Settle()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            BoardGameWorld? world = _world;
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
                host.Log.Error("[BoardGame] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, DefaultLevelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            BoardGameHud? view = hud;
            if (view != null)
            {
                view.ShowResult(world.Winner, world.BlackCount, world.WhiteCount);
            }

            host.Log.Info("[BoardGame] 结算：场次=" + DefaultLevelId
                + " 胜负=" + result.IsVictory
                + " 胜方=" + world.Winner
                + " 黑=" + world.BlackCount
                + " 白=" + world.WhiteCount
                + " 手数=" + world.MoveCount
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>
        /// 当前难度夹到 0..<see cref="MaxDifficulty"/> 后的下标。
        /// <para>
        /// <b>为什么这里自己夹一次</b>：内核会把非法难度按 Normal 处理，但那是"白方 AI 的兜底"；
        /// 本类要拿难度当 <see cref="ProbeOrders"/> 的数组下标，必须先落进合法区间，
        /// 否则一个写错的配置值就能把演示场景打崩（越界访问）。
        /// </para>
        /// <para>负数按 Easy 夹到 0（不交给内核落到贪心分支），过大夹到 Hard。</para>
        /// </summary>
        /// <returns>0 / 1 / 2。</returns>
        private int DifficultyIndex()
        {
            int value = difficulty;
            if (value < 0)
            {
                return 0;
            }

            return value > MaxDifficulty ? MaxDifficulty : value;
        }

        /// <summary>当前要交给内核的白方难度（<see cref="DifficultyIndex"/> 的枚举形态）。</summary>
        /// <returns>合法难度。</returns>
        private BoardGameDifficulty EffectiveDifficulty()
        {
            return (BoardGameDifficulty)DifficultyIndex();
        }
    }
}
