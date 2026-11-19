#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Kernel.Turn.Roguelike;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 肉鸽卡牌场景控制器：建宿主 → 直接构造 <see cref="RoguelikeWorld"/> → 每帧把只读视图推给 HUD →
    /// run 结束结算一次。
    /// <para>
    /// <b>为什么本类可以直接 <c>new RoguelikeWorld</c>，而其它玩法控制器要走 <c>host.Sessions.Begin</c></b>：
    /// 适配层是组合根（<c>AIFramework.Adapters.Unity</c> 的 asmdef 已引用 <c>AIFramework.Kernel.Turn</c>），
    /// 而 <see cref="GameplayKernelType"/> 没有 Roguelike 值、<c>UnityKernelFactory</c> 也没有对应分支。
    /// 肉鸽对主板而言只是"另一个回合制会话"（胜负、战利品、进出门与既有 Turn 内容完全一致），
    /// 为一个纯演示场景扩枚举与工厂会让存档协议/契约长期付成本；因此这里直接构造并<b>自己持有这次会话</b>
    /// （不登记进 <c>host.Sessions</c>），结算时直接调 <see cref="IGameplaySession.CollectResult"/>。
    /// 宿主仍然负责总线、日志与背包——本类只借它广播与发奖。
    /// </para>
    /// <para>
    /// <b>为什么这里没有任何肉鸽规则</b>：地图形状、节点种类、能量/费用、伤害与格挡、商店与营地、
    /// 胜负门槛全部由内核决定；适配层一旦自己算一遍（哪怕是"看起来等价"的复算），
    /// 改内核而不改 UI 时两侧就会静默分叉。本类只做四件事：装配宿主、把帧时间喂给 <c>Step</c>、
    /// 把只读视图推给 HUD、run 结束结算并广播事实。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次 <c>Step</c> 只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>自动演示的节拍契约</b>：<see cref="RoguelikeWorld"/> 是回合制内核，<c>Step</c> 只累计推演步数
    /// （<c>deltaSeconds</c> 被忽略），所以"一帧刷完一整轮 run"在技术上完全可能。本类因此用累计时间
    /// <see cref="AutoPlayStepSeconds"/> 把自动操作限速成"每 0.4 秒一个动作"：玩家能看清每一步的地图前进、
    /// 出牌与敌人反击，而不是按 Play 之后立刻看到结算。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoguelikeController : MonoBehaviour
    {
        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：肉鸽是单人玩法，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对。
        /// </summary>
        private const int SinglePlayerUnitCount = 1;

        /// <summary>
        /// 场次 ID：交给 <see cref="GameplayInitData"/>，并作为结算广播里的场次标识。
        /// <b>为什么是常量而不是 <c>[SerializeField]</c></b>：本场景只有"一轮 run"，没有选关语义；
        /// 公开面锁定为"只暴露控制台需要的三个字段"，多一个可调项只会让场景注入多一处可能漏配的地方。
        /// </summary>
        private const int DefaultLevelId = 1;

        /// <summary>
        /// 自动演示里两个动作之间的最小间隔（秒）。<b>为什么是 0.4</b>：一帧一个动作会让整轮 run
        /// 在一秒内结束（HUD 只来得及画最后一帧）；0.4 秒足够看清"进入节点 → 出牌 → 敌人反击"的因果，
        /// 又不会慢到让自动演示显得卡住。
        /// </summary>
        private const float AutoPlayStepSeconds = 0.4f;

        /// <summary>肉鸽皮肤（渲染状态行/地图行/手牌，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private RoguelikeHud hud = null!;

        /// <summary>
        /// 随机种子：同时决定地图形状、洗牌顺序与奖励抽卡。固定值让"同一次演示"可复现
        /// （同一张地图、同一套奖励序列）。
        /// </summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>是否自动演示（默认开：场景一进来就能看到地图推进与战斗）。</summary>
        [SerializeField] private bool autoPlay = true;

        /// <summary>宿主（总线/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>
        /// 当前 run 的会话视角（<see cref="IGameplaySession"/>，与 <see cref="_world"/> 是<b>同一个对象</b>）。
        /// 结算后置空，表示会话所有权已交还；重开时与 <see cref="_world"/> 一起替换。
        /// </summary>
        private IGameplaySession? _session;

        /// <summary>
        /// 同一轮 run 的具体类型：<c>BattlesWon</c> 这类累计值不在契约里，只有具体类型能读。
        /// 结算后<b>保留</b>（HUD 与公开属性要继续显示最终读数），重开时替换。
        /// </summary>
        private RoguelikeWorld? _world;

        /// <summary>本局是否已结算（<c>CollectResult</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>自动演示的累计时间（秒）：越过 <see cref="AutoPlayStepSeconds"/> 才执行下一个动作。</summary>
        private float _autoPlayTimer;

        /// <summary>玩家当前 HP（结算后仍可读；无会话时为 0）。</summary>
        public int PlayerHp
        {
            get
            {
                RoguelikeWorld? world = _world;
                return world != null ? world.PlayerHp : 0;
            }
        }

        /// <summary>当前敌人 HP（战斗外与结算后为 0）。</summary>
        public int EnemyHp
        {
            get
            {
                RoguelikeWorld? world = _world;
                return world != null ? world.EnemyHp : 0;
            }
        }

        /// <summary>本轮 run 是否已结束（阵亡或通关；无会话时为 <c>false</c>）。</summary>
        public bool IsRunOver
        {
            get
            {
                RoguelikeWorld? world = _world;
                return world != null && world.IsRunOver;
            }
        }

        /// <summary>
        /// 重开一轮：换一个全新的 <see cref="RoguelikeWorld"/>（等价于"再来一轮"）。
        /// <para>
        /// <b>为什么必须重建而不是调 <c>world.Restart()</c></b>：内核在 <c>CollectResult</c> 之后把会话
        /// 所有权交还，<c>Restart</c> 从此为空操作（见 <see cref="RoguelikeWorld.Restart"/> 注释），
        /// 只有重新构造才能真正回到初始局面；重建同时把 <c>BattlesWon</c> 这类累计诊断值归零，
        /// 让结算文案里的"胜场"始终是<b>本轮</b>的胜场。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是 MonoBehaviour），它的
        /// <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"run 结束但还没结算"的那一帧，
        /// 直接重建会把这一轮的 <c>GameFinishedEvent</c> 与战利品静默吞掉。已结束就先走
        /// <see cref="Finish"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// <para>局中调用同样安全：旧局还没结算就直接丢弃（纯内存对象，没有原生资源）。</para>
        /// </summary>
        public void Restart()
        {
            RoguelikeWorld? world = _world;
            if (!_collected && world != null && world.IsRunOver)
            {
                Finish();
            }

            CreateWorld();
        }

        /// <summary>
        /// 前进到下一层。能否前进（run 已结束 / 正在战斗 / 已到最后一层）由内核判定并返回 <c>false</c>，
        /// 本类不做额外校验——否则就是第二套规则。
        /// </summary>
        public void EnterNextNode()
        {
            RoguelikeWorld? world = _world;
            if (world != null)
            {
                world.TryEnterNextNode();
            }
        }

        /// <summary>
        /// 打出第 <paramref name="handIndex"/> 张手牌。越界索引、能量不足、战斗外、run 结束后调用都安全：
        /// 内核返回 <c>false</c> 且不改任何状态。
        /// </summary>
        /// <param name="handIndex">手牌下标（0..HandCount-1）。</param>
        public void PlayCard(int handIndex)
        {
            RoguelikeWorld? world = _world;
            if (world != null)
            {
                world.TryPlayCard(handIndex);
            }
        }

        /// <summary>
        /// 结束本回合：敌人攻击（先由格挡吸收）→ 清空手牌 → 重抽 → 能量重置，全部由内核结算。
        /// 战斗外或 run 结束后调用是空操作。
        /// </summary>
        public void EndTurn()
        {
            RoguelikeWorld? world = _world;
            if (world != null)
            {
                world.EndBattleTurn();
            }
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的按钮事件订阅与首帧渲染
        /// 要在玩家可能点下去之前就绪；挂到 <c>Start</c> 会留一个"第一次点击没反应"的空窗。
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
        /// 每帧：驱动内核 →（自动演示时）执行下一个动作 → 刷新 HUD → run 结束则结算一次。
        /// <b>顺序不可换</b>：先推进再渲染，玩家看到的就是这一步之后的真实读数；
        /// 先渲染再推进会让画面永远慢一格（结算也要多等一帧才显示结果）。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            RoguelikeWorld? world = _world;
            if (session == null || world == null)
            {
                return;
            }

            // 回合制内核忽略 deltaSeconds，只累计推演步数；喂帧时间是为了让 StepCount 读起来像"跑了多久"。
            session.Step(Time.deltaTime);

            if (!_collected && autoPlay && !world.IsRunOver)
            {
                _autoPlayTimer += Time.deltaTime;
                if (_autoPlayTimer >= AutoPlayStepSeconds)
                {
                    _autoPlayTimer = 0f;
                    RunAutoPlayStep(world);
                }
            }

            RenderHud();

            if (!_collected && session.IsFinished)
            {
                Finish();
            }
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
        /// 建（或重建）一轮 run：构造内核 → 首帧渲染。
        /// <para>
        /// <b>为什么不经过 <c>host.Sessions</c></b>：宿主只按 <see cref="GameplayKernelType"/> 建会话，
        /// 而肉鸽没有枚举值（见类型注释）；这里直接构造，会话所有权从一开始就在本类手里，
        /// 因此也不需要 <c>Release</c> 上一轮——旧对象随字段替换被 GC。
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
            _autoPlayTimer = 0f;

            var init = new GameplayInitData(DefaultLevelId, randomSeed, SinglePlayerUnitCount);
            var world = new RoguelikeWorld(in init);

            _session = world;
            _world = world;

            RenderHud();

            host.Log.Info("[Roguelike] 开局：场次=" + DefaultLevelId
                + " 种子=" + randomSeed
                + " 节点数=" + world.NodeCount
                + " 初始卡组=" + world.DeckSize
                + " 自动演示=" + autoPlay);
        }

        /// <summary>
        /// 自动演示的一个动作（每 <see cref="AutoPlayStepSeconds"/> 秒一次，绝不在一帧里刷完）：
        /// 不在战斗就前进一层；战斗中打出"付得起的最高费用牌"；一张都打不出就结束回合。
        /// <para>
        /// <b>为什么"最贵"而不是"伤害最高"</b>：费用是内核给的强度口径（2 费的重击/铁壁强于 1 费的打击/防御），
        /// 用费用排序不需要在适配层复算任何牌面规则；同费用时依次比伤害、格挡，最后取小下标，保证可复现。
        /// </para>
        /// <para>
        /// <b>为什么会终止</b>：每次动作要么消耗能量（手牌减少）、要么清空手牌重抽、要么推进地图，
        /// 内核对这些动作都有硬上限（手牌 5 张、能量 3、地图 8 层）；真正卡住的唯一形态是
        /// "活下来的僵持"，那是内核数值问题，不该由 UI 层编一个回合上限去掩盖。
        /// </para>
        /// </summary>
        /// <param name="world">当前 run。</param>
        private static void RunAutoPlayStep(RoguelikeWorld world)
        {
            if (world.IsRunOver)
            {
                return;
            }

            if (!world.InBattle)
            {
                world.TryEnterNextNode();
                return;
            }

            int cardIndex = FindMostExpensivePlayableCard(world);
            if (cardIndex >= 0)
            {
                world.TryPlayCard(cardIndex);
                return;
            }

            world.EndBattleTurn();
        }

        /// <summary>
        /// 挑出"付得起的最高费用牌"；同费用依次比伤害、格挡，最后取小下标。
        /// 费用为负或超过剩余能量的牌内核会拒绝打出，这里提前跳过（否则永远选不出一张能打的牌）。
        /// </summary>
        /// <param name="world">当前 run（调用方保证在战斗中）。</param>
        /// <returns>手牌下标；一张都付不起时返回 <c>-1</c>。</returns>
        private static int FindMostExpensivePlayableCard(RoguelikeWorld world)
        {
            int best = -1;
            int bestCost = 0;
            int bestDamage = 0;
            int bestBlock = 0;

            int handCount = world.HandCount;
            int energy = world.Energy;

            for (int i = 0; i < handCount; i++)
            {
                RoguelikeCardView card = world.GetHandCard(i);
                if (card.Cost < 0 || card.Cost > energy)
                {
                    continue;
                }

                bool better = best < 0
                    || card.Cost > bestCost
                    || (card.Cost == bestCost && card.Damage > bestDamage)
                    || (card.Cost == bestCost && card.Damage == bestDamage && card.Block > bestBlock);

                if (better)
                {
                    best = i;
                    bestCost = card.Cost;
                    bestDamage = card.Damage;
                    bestBlock = card.Block;
                }
            }

            return best;
        }

        /// <summary>
        /// 结算：<c>CollectResult</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示结果。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这轮已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// <para>
        /// <b>为什么广播用 <see cref="GameplayKernelType.Turn"/></b>：肉鸽没有独立内核类型，
        /// 它对主板就是一个回合制会话；借用 Turn 才能让既有的任务/成就切片直接认账。
        /// </para>
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            IGameplaySession? session = _session;
            RoguelikeWorld? world = _world;
            if (host == null || session == null || world == null || _collected)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;

            int battlesWon = world.BattlesWon;
            int depth = world.Depth;

            GameplayResult result;
            try
            {
                result = session.CollectResult();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Roguelike] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Turn, DefaultLevelId, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            RoguelikeHud? view = hud;
            if (view != null)
            {
                view.ShowResult(result.IsVictory, battlesWon);
            }

            host.Log.Info("[Roguelike] 结算：场次=" + DefaultLevelId
                + " 胜负=" + result.IsVictory
                + " 到达层=" + depth
                + " 胜场=" + battlesWon
                + " 步数=" + result.StepCount
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>
        /// 把 HUD 的"重开 / 前进 / 结束回合 / 第 N 张牌"接到本类
        /// （<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。
        /// </summary>
        private void SubscribeHud()
        {
            RoguelikeHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked += HandleRestartClicked;
            view.OnEnterNodeClicked += HandleEnterNodeClicked;
            view.OnEndTurnClicked += HandleEndTurnClicked;
            view.OnCardClicked += HandleCardClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            RoguelikeHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked -= HandleRestartClicked;
            view.OnEnterNodeClicked -= HandleEnterNodeClicked;
            view.OnEndTurnClicked -= HandleEndTurnClicked;
            view.OnCardClicked -= HandleCardClicked;
        }

        /// <summary>点"重开"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>点"前进"：能否前进由内核判定。</summary>
        private void HandleEnterNodeClicked()
        {
            EnterNextNode();
        }

        /// <summary>点"结束回合"：敌人行动与抽牌由内核结算。</summary>
        private void HandleEndTurnClicked()
        {
            EndTurn();
        }

        /// <summary>点第 <paramref name="handIndex"/> 张牌：直接转发给内核，能不能打由内核决定。</summary>
        /// <param name="handIndex">手牌下标（0..4）。</param>
        private void HandleCardClicked(int handIndex)
        {
            PlayCard(handIndex);
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            RoguelikeHud? view = hud;
            RoguelikeWorld? world = _world;
            if (view != null && world != null)
            {
                view.Render(world);
            }
        }
    }
}
