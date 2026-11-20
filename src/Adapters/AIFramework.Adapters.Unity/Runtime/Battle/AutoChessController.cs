#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ai;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Ai;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 自走棋场景控制器：建宿主 → <c>Begin(AutoChess)</c> → 把三个按钮翻译成
    /// <see cref="IAutoChessInput"/> 意图 → 战斗/回合推进后把只读视图推给 HUD。
    /// <para>
    /// <b>为什么这里没有任何自走棋规则</b>：能不能上场、落哪一格、谁赢谁输、扣多少基地血、
    /// 什么时候算对局结束，全部由内核判定。适配层一旦自己算一遍，两侧就会各自演化，
    /// 改内核而不改 UI 时行为静默分叉。因此本类只做四件事：装配宿主、把点击翻译成意图、
    /// 结束时分发战利品与事实广播、把只读视图推给 HUD。
    /// </para>
    /// <para>
    /// <b>为什么只认 Contracts 接口</b>：本类<b>不</b>引用 <c>AIFramework.Kernel.Turn</c> 的
    /// <c>AutoChessWorld</c>，只用 <see cref="IAutoChessReadView"/> / <see cref="IAutoChessInput"/> 与
    /// <see cref="IGameplaySession"/>；内核实现与 <c>UnityKernelFactory</c> 的 <c>AutoChess</c> 分支
    /// 由装配层接，本类无需改动。
    /// </para>
    /// <para>
    /// <b>为什么没有 <c>Update</c> 里的计时器</b>：自走棋的战斗是 <see cref="IAutoChessInput.StartBattle"/>
    /// 同步推完的（内核一次跑完 30 个 tick），没有"每秒推进多少"的节奏可供适配层决定；
    /// <see cref="Update"/> 只做一件事：发现对局结束就收口一次。
    /// </para>
    /// <para>
    /// <b>AI 接管开关</b>：<c>aiTakesOverRounds</c> 打开后，"开战"会先让 Core 的 <see cref="AutoChessAi"/>
    /// 把备战席交出去，"下一回合"结算后会立刻接手下一轮；AI 的每条决策仍然被翻译成
    /// <see cref="IAutoChessInput"/> 意图，本类不新增任何玩法规则（详见 <see cref="PlaceWithAi"/>）。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AutoChessController : MonoBehaviour
    {
        /// <summary>
        /// 进场关卡 ID。自走棋 Phase 1 没有关卡表（敌方阵容由内核按回合固定生成），
        /// 但 <see cref="GameplayInitData"/> 要求带上它，故固定传 1 并在日志里如实打印。
        /// </summary>
        private const int LevelId = 1;

        /// <summary>玩家半场的前几行算"前排"：只用于喂给 AI 的前排标记，不参与任何规则判定。</summary>
        private const int AutoChessFrontlineRows = 2;

        /// <summary>自走棋皮肤（渲染状态行与字符网格，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private AutoChessHud hud = null!;

        /// <summary>随机种子：固定值让同一套布阵在同一个种子下可复现（内核的走位随机流由它派生）。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>开局玩家单位数（备战席人数）。<b>越界值不在这里夹</b>：内核自己夹到 1..3，适配层不复制这份规则。</summary>
        [SerializeField] private int initialUnitCount = 3;

        /// <summary>
        /// AI 接管回合：打开后"开战"会先让 <see cref="AutoChessAi"/> 完成本轮布阵，
        /// 而"下一回合"在结算后会立刻接手下一轮（同样先布阵再开战）。
        /// <para>
        /// <b>为什么默认关闭</b>：自走棋场景首先是"人类可操作"的演示，AI 接管只是验证策略接线的开关；
        /// 关掉它，三个按钮的行为与本功能合入前完全一致，也不构造任何 AI 实例。
        /// </para>
        /// </summary>
        [SerializeField] private bool aiTakesOverRounds = false;

        /// <summary>AI 决策器：开关打开后首次需要时构造，关闭时保持 <c>null</c>。</summary>
        private AutoChessAi? _ai;

        /// <summary>宿主（总线/会话/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>当前对局会话；结算或开局失败后置空。<b>只有它还非空时才允许 <c>EndAndCollect</c></b>。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的只读视图；结算后仍保留（HUD 要继续显示终局画面），重开时替换。</summary>
        private IAutoChessReadView? _readView;

        /// <summary>会话强转出来的输入端口；开局失败时为空（此时只能看不能打）。</summary>
        private IAutoChessInput? _input;

        /// <summary>本局是否已结算（<c>EndAndCollect</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>当前回合序号（内核从 0 开始计数）；会话未就绪时报 0。</summary>
        public int RoundIndex
        {
            get
            {
                IAutoChessReadView? view = _readView;
                return view != null ? view.RoundIndex : 0;
            }
        }

        /// <summary>玩家基地血量；会话未就绪时报 0。</summary>
        public int PlayerHp
        {
            get
            {
                IAutoChessReadView? view = _readView;
                return view != null ? view.PlayerHp : 0;
            }
        }

        /// <summary>敌方基地血量；会话未就绪时报 0。</summary>
        public int EnemyHp
        {
            get
            {
                IAutoChessReadView? view = _readView;
                return view != null ? view.EnemyHp : 0;
            }
        }

        /// <summary>本轮战斗是否已结算（结算后到 <see cref="NextRound"/> 之前为 <c>true</c>）。</summary>
        public bool IsBattleResolved
        {
            get
            {
                IAutoChessReadView? view = _readView;
                return view != null && view.IsBattleResolved;
            }
        }

        /// <summary>AI 是否接管回合（Inspector 开关的只读镜像，供 HUD/调试确认当前模式）。</summary>
        public bool AiTakesOverRounds
        {
            get { return aiTakesOverRounds; }
        }

        /// <summary>
        /// 把备战席上的玩家单位全部放上己方半场。
        /// <para>
        /// <b>为什么逐个 <c>TryPlace</c> 而不是直接摆坐标</b>：落点由内核按"己方半场第一个空格"决定，
        /// 适配层若自己挑格子，就要复制半场边界与占位规则；这里只负责"把还在席上的单位挨个交出去"。
        /// 槽位在 <c>TryPlace</c> 前后不重排（只有出售会压缩槽位），因此边遍历边放置是安全的。
        /// </para>
        /// </summary>
        public void PlaceAll()
        {
            IAutoChessInput? input = _input;
            IAutoChessReadView? view = _readView;
            if (input == null || view == null || _collected || view.IsFinished)
            {
                return;
            }

            int placed = 0;
            int playerSlots = view.PlayerUnitCount;
            for (int slot = 0; slot < playerSlots; slot++)
            {
                ChessUnitView unit = view.GetUnit(slot);

                // 备战席判据是"坐标 -1"：阵亡单位会保留阵亡时的坐标，所以不能只看 Alive。
                if (!unit.Alive || unit.X >= 0)
                {
                    continue;
                }

                if (input.TryPlace(unit.UnitId))
                {
                    placed++;
                }
            }

            RenderHud();

            AIFrameworkUnityHost? host = _host;
            if (host != null)
            {
                host.Log.Info("[AutoChess] 布阵：本轮上场 " + placed + " 个，回合=" + RoundIndex);
            }
        }

        /// <summary>
        /// AI 接管时的布阵：把只读视图投影成 <see cref="AutoChessState"/>，逐条取决策，
        /// 把"上场"类决策翻译成 <see cref="IAutoChessInput.TryPlace"/>，直到 AI 不再要求上场。
        /// <para>
        /// <b>为什么投影里没有经济和商店</b>：Phase 1 的内核只有"棋子与格子"，没有金币/等级/刷新。
        /// 因此备战席上的棋子以 <c>IsOwned = true</c> 的商店槽位投影（这正是契约里"已拥有"的表达方式），
        /// 金币固定 0、经济下限设为 0——否则"攒钱"规则会把布阵永远挡在 Wait 上。
        /// 于是 AI 在本场景里只会走"摆放"分支；买 / 卖 / 升人口 / 刷新保留给内核接上经济系统的那一天。
        /// </para>
        /// <para>
        /// <b>为什么不用决策里的落点坐标</b>：内核的 <c>TryPlace</c> 自己按"己方半场第一个空格"扫描，
        /// 顺序与 AI 的落点扫描（y 0→3、x 0→7）完全一致；适配层再挪一次只会多出一条可能与内核打架的规则。
        /// 坐标先留在决策里，等内核开放"指定落点"再用。
        /// </para>
        /// </summary>
        private void PlaceWithAi()
        {
            IAutoChessInput? input = _input;
            IAutoChessReadView? view = _readView;
            if (input == null || view == null || _collected || view.IsFinished)
            {
                return;
            }

            AutoChessAi ai = EnsureAi();
            int placed = 0;
            int playerSlots = view.PlayerUnitCount;

            // 上界 = 槽位数：每次成功上场至少消耗一个备战席单位，失败立即 break，循环必然收敛。
            for (int i = 0; i < playerSlots; i++)
            {
                AutoChessState state = BuildAiState(view);
                AutoChessDecision decision = ai.Decide(in state);

                if (decision.Action != AutoChessAiAction.Buy)
                {
                    break;
                }

                if (!input.TryPlace(decision.TargetUnitId))
                {
                    break;
                }

                placed++;
            }

            if (placed > 0)
            {
                RenderHud();

                AIFrameworkUnityHost? host = _host;
                if (host != null)
                {
                    host.Log.Info("[AutoChess] AI 布阵：本轮上场 " + placed + " 个，回合=" + RoundIndex
                        + "，累计决策=" + ai.DecisionsMade);
                }
            }
        }

        /// <summary>
        /// 把内核只读视图投影成 AI 状态：已上场的棋子进 <see cref="AutoChessBoardUnit"/>，
        /// 备战席棋子进 <see cref="AutoChessShopSlot"/>（<c>IsOwned = true</c>）。
        /// <para>
        /// <b>为什么成本填 0</b>：内核没有价格概念，填 0 表示"不涉及经济"；<c>IsOwned = true</c>
        /// 让"补满棋盘"不会把这些棋子当成新牌，"差两张升星"让凑星规则也不会误触发。
        /// </para>
        /// </summary>
        private static AutoChessState BuildAiState(IAutoChessReadView view)
        {
            int playerSlots = view.PlayerUnitCount;
            var board = new List<AutoChessBoardUnit>(playerSlots);
            var bench = new List<AutoChessShopSlot>(playerSlots);

            for (int slot = 0; slot < playerSlots; slot++)
            {
                ChessUnitView unit = view.GetUnit(slot);
                if (!unit.Alive)
                {
                    continue;
                }

                if (unit.X >= 0)
                {
                    // 前排口径 = 玩家半场的前两行（y = 0..1），只喂给 AI 的"超编卖谁"取舍。
                    board.Add(new AutoChessBoardUnit(
                        unit.UnitId, 0, 1, unit.X, unit.Y, unit.Y < AutoChessFrontlineRows));
                }
                else
                {
                    bench.Add(new AutoChessShopSlot(unit.UnitId, 0, true, 2));
                }
            }

            return new AutoChessState(
                gold: 0,
                level: 1,
                round: view.RoundIndex,
                hp: view.PlayerHp,
                boardCapacity: playerSlots,
                board: board.ToArray(),
                shop: bench.ToArray(),
                streak: 0);
        }

        /// <summary>
        /// 取 AI 实例（懒构造）。<b>经济下限 0</b>：Phase 1 适配层没有经济系统，
        /// 攒钱规则不该拦住布阵（详见 <see cref="PlaceWithAi"/>）。
        /// </summary>
        private AutoChessAi EnsureAi()
        {
            AutoChessAi? ai = _ai;
            if (ai == null)
            {
                ai = new AutoChessAi(economyFloor: 0);
                _ai = ai;
            }

            return ai;
        }

        /// <summary>
        /// 开战：同步推完本轮战斗，返回时双方基地血量与胜负已经落定。
        /// <para>
        /// <b>为什么先 <c>Step</c> 再 <c>StartBattle</c></b>：<see cref="IGameplaySession.Step"/> 对内核算术无影响，
        /// 只累加"推演步数"（进 <see cref="GameplayResult.StepCount"/> 上屏与对拍）；放在开战之前，
        /// 每次开战正好记一步，结算报告里的步数就等于实际开战次数。
        /// </para>
        /// </summary>
        public void StartBattle()
        {
            IAutoChessInput? input = _input;
            IAutoChessReadView? view = _readView;
            if (input == null || view == null || _collected || view.IsFinished)
            {
                return;
            }

            if (view.IsBattleResolved)
            {
                // 内核重复开战是空操作；直接不调，免得日志里刷出一串"开了但没变"。
                return;
            }

            if (aiTakesOverRounds)
            {
                // 先由 AI 把备战席交出去，再开战；人类已经手动布过阵时这一步自然空转。
                PlaceWithAi();
            }

            _session?.Step(0f);
            input.StartBattle();
            RenderHud();

            AIFrameworkUnityHost? host = _host;
            if (host != null && _readView != null)
            {
                host.Log.Info("[AutoChess] 战斗结算：回合=" + _readView.RoundIndex
                    + " 我方HP=" + _readView.PlayerHp
                    + " 敌方HP=" + _readView.EnemyHp
                    + " 槽位=" + _readView.PlayerUnitCount + "/" + _readView.EnemyUnitCount);
            }
        }

        /// <summary>
        /// 推进到下一回合；对局已结算时改为"重新开始"（重建会话）。
        /// <para>
        /// <b>为什么要处理"已结束但还没收口"这一态</b>：正常由 <see cref="Update"/> 抢先结算，
        /// 但玩家可能在同一帧内点到按钮；此时若直接 <c>EndRound</c>（内核空操作）就会表现为"点了没反应"。
        /// 因此这里先收口一次并立刻返回——本次点击只负责"把结果上屏"，重开留给下一次点击。
        /// </para>
        /// </summary>
        public void NextRound()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            IAutoChessReadView? view = _readView;
            IAutoChessInput? input = _input;

            if (_collected || _session == null || view == null || input == null)
            {
                // 已结算 / 开局失败 / 契约缺失：同一个按钮表示"重新开始"。
                BeginSession();
                return;
            }

            if (view.IsFinished)
            {
                Finish();
                return;
            }

            if (!view.IsBattleResolved)
            {
                // 还没打过本轮的仗：按钮本该是灰的，走到这里说明有人直接调了 API。
                host.Log.Warn("[AutoChess] 回合尚未结算，忽略『下一回合』：回合=" + view.RoundIndex);
                return;
            }

            input.EndRound();
            RenderHud();

            host.Log.Info("[AutoChess] 进入下一回合：回合=" + RoundIndex
                + " 我方HP=" + PlayerHp + " 敌方HP=" + EnemyHp);

            if (aiTakesOverRounds)
            {
                // AI 接管：本轮布阵 + 开战一次做完，玩家只负责按节奏点"下一回合"。
                StartBattle();
            }
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的点击订阅与首帧渲染要在
        /// 玩家可能点下去之前就绪；挂到 <c>Start</c> 会留一个"第一次点击没反应"的空窗。
        /// </summary>
        private void Awake()
        {
            var host = new AIFrameworkUnityHost();
            host.Initialize();
            _host = host;

            SubscribeHud();
            BeginSession();
        }

        /// <summary>
        /// 每帧：发现对局结束就收口一次，随后把只读视图推给 HUD。
        /// <b>顺序不可换</b>：结算必须在渲染之前完成，否则终局那一帧显示的还是"布阵中"，
        /// 玩家会看到一格"结束前"的画面卡在屏幕上。
        /// </summary>
        private void Update()
        {
            if (_host == null)
            {
                return;
            }

            if (!_collected && _session != null && _readView != null && _readView.IsFinished)
            {
                Finish();
            }

            // 结算后仍要渲染：终局画面、双方血量与"重新开始"文案都靠这一条。
            RenderHud();
        }

        /// <summary>退订 HUD 并释放宿主（<c>Dispose</c> 会丢弃未结算的对局引用）。</summary>
        private void OnDestroy()
        {
            UnsubscribeHud();

            _session = null;
            _readView = null;
            _input = null;
            _ai = null;
            _collected = true;

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 建（或重建）一局：交还旧会话 → <c>Begin(AutoChess)</c> → 强转契约 → 首帧渲染。
        /// <para>
        /// <b>为什么每次都先 <c>Release</c></b>：宿主只允许一个活跃会话，上一局（本场景重开）若还挂着，
        /// <c>Begin</c> 会直接抛"已有活跃会话"。
        /// </para>
        /// </summary>
        private void BeginSession()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            _session = null;
            _readView = null;
            _input = null;
            _collected = false;

            // 新一局：AI 的策略本身无状态，只把诊断计数清零（开关关闭时 _ai 为 null，空条件调用）。
            _ai?.Reset();

            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            IGameplaySession session;
            var init = new GameplayInitData(LevelId, randomSeed, initialUnitCount);
            try
            {
                session = host.Sessions.Begin(GameplayKernelType.AutoChess, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[AutoChess] 开局失败（宿主已有未结算会话？）：" + exception.Message);
                _collected = true;
                return;
            }

            _session = session;
            _readView = session as IAutoChessReadView;
            _input = session as IAutoChessInput;

            if (_readView == null || _input == null)
            {
                // 会话没暴露自走棋契约：说明工厂没接 AutoChess 分支（落回 TurnWorld）。
                // 此时绝不能用错的卡带继续推演，也不能让 HUD 显示一份假棋盘——立刻交还会话并关掉 HUD。
                host.Log.Error("[AutoChess] 会话未实现 IAutoChessReadView/IAutoChessInput（UnityKernelFactory 还没接 AutoChess 分支？），"
                    + "本局已放弃，HUD 已禁用。");

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

            host.Log.Info("[AutoChess] 开局：关卡=" + LevelId + " 种子=" + randomSeed
                + " 单位数=" + initialUnitCount
                + " 棋盘=" + _readView.Width + "×" + _readView.Height);
        }

        /// <summary>
        /// 结算：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → 重绘终局画面。
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

            // 先置标记再结算：CollectResult 抛异常（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;

            GameplayResult result;
            try
            {
                result = host.Sessions.EndAndCollect();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[AutoChess] 结算失败：" + exception.Message);
                RenderHud();
                return;
            }

            host.PublishGameFinished(GameplayKernelType.AutoChess, LevelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            // 结算后 Update 仍会渲染，但这条渲染保证"直接从 NextRound 走到结算"时终局画面立刻上屏。
            RenderHud();

            host.Log.Info("[AutoChess] 结算：胜负=" + result.IsVictory
                + " 回合=" + RoundIndex
                + " 我方HP=" + PlayerHp
                + " 敌方HP=" + EnemyHp
                + " 剩余单位=" + result.RemainingUnits
                + " 推演步数=" + result.StepCount
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>把 HUD 的三个点击事件接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            AutoChessHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnPlaceClicked += HandlePlaceClicked;
            view.OnStartBattleClicked += HandleStartBattleClicked;
            view.OnNextRoundClicked += HandleNextRoundClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            AutoChessHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnPlaceClicked -= HandlePlaceClicked;
            view.OnStartBattleClicked -= HandleStartBattleClicked;
            view.OnNextRoundClicked -= HandleNextRoundClicked;
        }

        /// <summary>点"全部上场"。</summary>
        private void HandlePlaceClicked()
        {
            PlaceAll();
        }

        /// <summary>点"开战"。</summary>
        private void HandleStartBattleClicked()
        {
            StartBattle();
        }

        /// <summary>点"下一回合 / 重新开始"。</summary>
        private void HandleNextRoundClicked()
        {
            NextRound();
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时直接跳过）。</summary>
        private void RenderHud()
        {
            AutoChessHud? view = hud;
            IAutoChessReadView? read = _readView;
            if (view != null && read != null)
            {
                view.Render(read);
            }
        }
    }
}
