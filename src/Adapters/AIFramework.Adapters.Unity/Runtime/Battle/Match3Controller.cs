#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 三消场景控制器：建宿主 → <c>Begin(Match3)</c> → 按固定节奏 <c>Step</c> → 把只读视图推给 HUD。
    /// <para>
    /// <b>为什么必须按节奏 Step 而不是每帧 Step</b>：三消的"交换 → 消除 → 下落 → 级联"是同一个逻辑步里
    /// 连续结算的，若每帧都 Step，级联会在不到 0.1 秒内全部结完，玩家看到的是一次瞬移；按
    /// <see cref="stepSeconds"/> 出步，级联才有可看的过程。
    /// </para>
    /// <para>
    /// <b>为什么只认 Contracts 接口</b>：本类<b>不</b>引用 <c>AIFramework.Kernel.Turn</c> 的任何类型，
    /// 只用 <see cref="IMatch3ReadView"/> / <see cref="IMatch3Input"/> 与 <see cref="IGameplaySession"/>。
    /// 内核（<c>Match3World</c>）与宿主工厂分支由装配层另行接，本类无需改动。
    /// </para>
    /// <para>
    /// <b>操作</b>：点第一格记锚点，点第二格调用 <see cref="IMatch3Input.TrySwap"/>；交换被内核拒绝
    /// （不相邻/换完不消除）时把锚点移到新格，玩家不必重新点两下。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Match3Controller : MonoBehaviour
    {
        /// <summary>步进间隔的兜底值：字段被 Inspector 改成 0 或负数时用它，避免除零/死循环。</summary>
        private const float FallbackStepSeconds = 0.25f;

        /// <summary>单帧最多结算的步数：机器卡顿后不允许在一帧里追赶出一大段级联（防"追帧爆炸"）。</summary>
        private const int MaxStepsPerFrame = 8;

        /// <summary>未选中任何格子的哨兵值。</summary>
        private const int NoSelection = -1;

        /// <summary>棋盘皮肤（渲染与点击都经它，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private Match3Hud hud = null!;

        /// <summary>关卡 ID，进门前快照的一部分（战利品/难度由内核按它决定）。</summary>
        [SerializeField] private int levelId = 1;

        /// <summary>随机种子：固定值让同一场局面可复现。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>每次 <c>Step</c> 的节奏（秒）；用于把级联动画拉开成可看的过程。</summary>
        [SerializeField] private float stepSeconds = 0.25f;

        /// <summary>宿主（总线/会话/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>当前对局会话；结算或失败后置空。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的只读视图；内核尚未实现该接口时为 <c>null</c>。</summary>
        private IMatch3ReadView? _readView;

        /// <summary>会话强转出来的输入端口；内核尚未实现该接口时为 <c>null</c>（此时只能看不能动）。</summary>
        private IMatch3Input? _input;

        /// <summary>步进累加器：攒够 <see cref="stepSeconds"/> 才走一步。</summary>
        private float _accumulator;

        /// <summary>本局是否已结算（结算只允许发生一次）。</summary>
        private bool _finished;

        /// <summary>当前锚点格子的 X；<see cref="NoSelection"/> 表示无锚点。</summary>
        private int _selectedX = NoSelection;

        /// <summary>当前锚点格子的 Y。</summary>
        private int _selectedY = NoSelection;

        /// <summary>只读视图（供外部诊断/测试读取；局中可能为 <c>null</c>）。</summary>
        public IMatch3ReadView? ReadView
        {
            get { return _readView; }
        }

        /// <summary>输入端口（供外部诊断/测试驱动；局中可能为 <c>null</c>）。</summary>
        public IMatch3Input? Input
        {
            get { return _input; }
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的点击订阅要在第一帧
        /// 玩家可能点下去之前就挂好；<c>Start</c> 晚于 <c>OnEnable</c>，留窗口没有意义。
        /// </summary>
        private void Awake()
        {
            var host = new AIFrameworkUnityHost();
            host.Initialize();
            _host = host;

            // 上一局（或上个场景留下的）未结算会话必须先释放，否则 Begin 会抛"已有活跃会话"。
            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            IGameplaySession session;
            try
            {
                var init = new GameplayInitData(levelId, randomSeed, 0);
                session = host.Sessions.Begin(GameplayKernelType.Match3, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Match3] 开局失败（宿主工厂尚未接三消分支？）：" + exception.Message);
                return;
            }

            _session = session;
            _readView = session as IMatch3ReadView;
            _input = session as IMatch3Input;

            if (_readView == null || _input == null)
            {
                host.Log.Error("[Match3] 会话未实现 IMatch3ReadView/IMatch3Input，棋盘无法渲染与操作。");
            }

            if (hud != null)
            {
                hud.OnCellClicked += HandleCellClicked;
                hud.Render(_readView);
            }

            float interval = EffectiveStepSeconds();
            host.Log.Info("[Match3] 开局：关卡=" + levelId + " 种子=" + randomSeed + " 出步间隔=" + interval + "s");
        }

        /// <summary>
        /// 按累加器推进逻辑步，每步后刷新一次 HUD。
        /// <b>刻意不用协程</b>（宪法禁止业务逻辑挂协程），也不每帧 Step（那会吃掉级联过程）。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            if (session == null || _finished)
            {
                return;
            }

            if (session.IsFinished)
            {
                Finish();
                return;
            }

            float interval = EffectiveStepSeconds();
            _accumulator += Time.deltaTime;

            int steps = 0;
            while (_accumulator >= interval && steps < MaxStepsPerFrame)
            {
                _accumulator -= interval;
                steps++;
                session.Step(interval);
                RenderHud();

                if (session.IsFinished)
                {
                    break;
                }
            }

            // 严重掉帧（比如编辑器卡在断点）后不要把积压的步全补上，只留一步的余量。
            if (_accumulator > interval)
            {
                _accumulator = interval;
            }

            if (session.IsFinished)
            {
                Finish();
            }
        }

        /// <summary>退订 HUD 并释放宿主（宿主 Dispose 会丢弃未结算的对局引用）。</summary>
        private void OnDestroy()
        {
            Match3Hud? view = hud;
            if (view != null)
            {
                view.OnCellClicked -= HandleCellClicked;
            }

            _session = null;
            _readView = null;
            _input = null;
            _finished = true;

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 把 HUD 的点击翻译成一次交换意图。
        /// <para>
        /// 相邻性/有效性一律交给内核判定（<see cref="IMatch3Input.TrySwap"/> 返回 <c>false</c> 即不算数），
        /// 适配层<b>不复制</b>消除规则，避免两套规则各自演化后行为分叉。
        /// </para>
        /// </summary>
        private void HandleCellClicked(int x, int y)
        {
            if (_finished)
            {
                return;
            }

            IMatch3Input? input = _input;
            if (input == null)
            {
                return;
            }

            if (_selectedX == NoSelection || _selectedY == NoSelection)
            {
                _selectedX = x;
                _selectedY = y;
                return;
            }

            if (_selectedX == x && _selectedY == y)
            {
                ClearSelection();
                return;
            }

            bool swapped = input.TrySwap(_selectedX, _selectedY, x, y);
            if (swapped)
            {
                ClearSelection();
                return;
            }

            // 这次不成：把锚点挪到刚点的格子上，玩家下一次点击即可与它配对。
            _selectedX = x;
            _selectedY = y;
        }

        /// <summary>渲染一次棋盘（会话可能已被销毁，故先取本地副本）。</summary>
        private void RenderHud()
        {
            Match3Hud? view = hud;
            if (view != null)
            {
                view.Render(_readView);
            }
        }

        /// <summary>
        /// 结算：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示胜负。
        /// <b>顺序不可换</b>：先广播事实再发奖，保证任务/成就类切片看到的是"这局已结束"时的账本状态。
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _finished)
            {
                return;
            }

            _finished = true;
            _session = null;
            ClearSelection();

            GameplayResult result;
            try
            {
                result = host.Sessions.EndAndCollect();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Match3] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Match3, levelId, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            Match3Hud? view = hud;
            if (view != null)
            {
                view.ShowResult(result);
            }

            host.Log.Info("[Match3] 结算：胜负=" + result.IsVictory
                + "，推演步数=" + result.StepCount
                + "，战利品条目=" + loots.Count);
        }

        /// <summary>消掉锚点。</summary>
        private void ClearSelection()
        {
            _selectedX = NoSelection;
            _selectedY = NoSelection;
        }

        /// <summary>出步间隔：字段被改成非法值时退回 0.25 秒。</summary>
        private float EffectiveStepSeconds()
        {
            float interval = stepSeconds;
            return interval > 0f ? interval : FallbackStepSeconds;
        }
    }
}
