#nullable enable
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 卡牌战斗场景控制器：建宿主 → <c>Begin(CardBattle)</c> → 每帧固定步推进 → 把只读视图推给 HUD。
    /// <para>
    /// <b>为什么这里没有任何卡牌规则</b>：能量、费用、抽牌、伤害与胜负判定全部在 <c>CardBattleWorld</c> 内核里。
    /// 适配层一旦复制一份规则，两侧就会各自演化，改内核而不改 UI 时行为静默分叉——这是最贵的 bug 形态。
    /// 因此本类只做四件事：装配宿主、按固定步 <c>Step</c>、把点击翻译成 <see cref="ICardBattleInput"/> 意图、
    /// 结算后广播事实并派发战利品。
    /// </para>
    /// <para>
    /// <b>为什么只认 Contracts 接口</b>：本类<b>不</b>引用 <c>AIFramework.Kernel.Turn</c> 的任何类型，
    /// 只依赖 <see cref="IGameplaySession"/>、<see cref="ICardBattleReadView"/> 与 <see cref="ICardBattleInput"/>；
    /// 内核实现与宿主工厂分支由装配层另行接入，本类无需改动。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CardBattleController : MonoBehaviour
    {
        /// <summary>卡牌战斗皮肤（渲染状态行与手牌，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private CardBattleHud hud = null!;

        /// <summary>关卡 ID，进门前快照的一部分（难度/战利品由内核按它决定）。</summary>
        [SerializeField] private int levelId = 1;

        /// <summary>随机种子：固定值让同一场牌序可复现。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>敌人单位数量；卡牌内核按它摆敌方阵容。</summary>
        [SerializeField] private int enemyUnitCount = 2;

        /// <summary>每个逻辑步的固定时长（秒）。默认 1/60 与强实时内核的帧长保持一致，便于对拍。</summary>
        [SerializeField] private float fixedStepSeconds = 1f / 60f;

        /// <summary>宿主（总线/会话/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>当前对局会话；结算或开局失败后置空。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的只读视图；内核未实现该接口时为 <c>null</c>。</summary>
        private ICardBattleReadView? _readView;

        /// <summary>会话强转出来的输入端口；内核未实现该接口时为 <c>null</c>（此时只能看不能打）。</summary>
        private ICardBattleInput? _input;

        /// <summary>本局是否已结算：<c>EndAndCollect</c> 只允许发生一次，用它兜住重入。</summary>
        private bool _finished;

        /// <summary>只读视图（供 HUD 与外部诊断/测试读取；开局失败时为 <c>null</c>）。</summary>
        public ICardBattleReadView? ReadView
        {
            get { return _readView; }
        }

        /// <summary>输入端口（供 HUD 与外部诊断/测试驱动；开局失败时为 <c>null</c>）。</summary>
        public ICardBattleInput? Input
        {
            get { return _input; }
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的点击订阅要在第一个
        /// <c>Update</c> 与玩家第一次点击之前就挂好；挂晚了会出现"第一次点牌没反应"的空窗。
        /// </summary>
        private void Awake()
        {
            var host = new AIFrameworkUnityHost();
            host.Initialize();
            _host = host;

            SubscribeHud();

            // 防御：宿主是场景内的新建实例，正常不会有活跃会话；先交还再 Begin，避免 Begin 抛"已有活跃会话"。
            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            var init = new GameplayInitData(levelId, randomSeed, enemyUnitCount);
            IGameplaySession session = host.Sessions.Begin(GameplayKernelType.CardBattle, in init);
            _session = session;
            _readView = session as ICardBattleReadView;
            _input = session as ICardBattleInput;

            if (_readView == null || _input == null)
            {
                // 会话没有暴露卡牌契约：说明工厂还没接 CardBattle 分支（或接了别的卡带）。
                // 此时绝不能用错的卡带继续推演，也不能让 HUD 显示一份假状态——立刻交还会话并关掉 HUD。
                host.Log.Error("[CardBattle] 会话未实现 ICardBattleReadView/ICardBattleInput（工厂分支未接入？），"
                    + "本局已放弃，HUD 已禁用。");
                _session = null;
                _readView = null;
                _input = null;
                _finished = true;
                host.Sessions.Release();
                if (hud != null)
                {
                    hud.enabled = false;
                }

                return;
            }

            if (hud != null)
            {
                hud.Render(_readView);
            }

            host.Log.Info("[CardBattle] 开局：关卡=" + levelId + " 种子=" + randomSeed
                + " 敌人数=" + enemyUnitCount + " 固定步长=" + fixedStepSeconds + "s");
        }

        /// <summary>
        /// 每帧推进一个固定逻辑步并刷新 HUD；结束时结算一次。
        /// <b>固定步长而不是 <c>Time.deltaTime</c></b>：卡牌是离散回合制，胜负不该随帧率漂移；
        /// 固定步也让"同一种子同一操作序列必得同一结果"这条可复现性成立。
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

            session.Step(fixedStepSeconds);

            if (hud != null)
            {
                hud.Render(_readView);
            }
        }

        /// <summary>
        /// 结算：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示胜负。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已胜利但金币还没到账"的中间态）。
        /// </para>
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _finished)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _finished = true;
            _session = null;

            GameplayResult result = host.Sessions.EndAndCollect();

            host.PublishGameFinished(GameplayKernelType.CardBattle, levelId, result.IsVictory);

            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            if (hud != null)
            {
                hud.ShowResult(result);
            }

            host.Log.Info("[CardBattle] 结算：胜负=" + result.IsVictory
                + "，步数=" + result.StepCount
                + "，剩余单位=" + result.RemainingUnits
                + "，战利品条目=" + loots.Count);
        }

        /// <summary>退订 HUD 并释放宿主（<c>Dispose</c> 会丢弃未结算的对局引用）。</summary>
        private void OnDestroy()
        {
            if (hud != null)
            {
                hud.OnCardClicked -= HandleCardClicked;
                hud.OnEndTurnClicked -= HandleEndTurnClicked;
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

        /// <summary>把 HUD 的点击事件接到本类的方法上（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            if (hud != null)
            {
                hud.OnCardClicked += HandleCardClicked;
                hud.OnEndTurnClicked += HandleEndTurnClicked;
            }
        }

        /// <summary>
        /// 把"点了第 index 张牌"翻译成一次打牌意图。
        /// 能量不足/索引越界/已结束一律由内核返回 <c>false</c>，本类不看牌面数据、不做任何规则判定。
        /// </summary>
        private void HandleCardClicked(int handIndex)
        {
            Input?.TryPlayCard(handIndex);
        }

        /// <summary>把"结束回合"翻译成意图；敌人行动与抽牌由内核在下一 <c>Step</c> 里结算。</summary>
        private void HandleEndTurnClicked()
        {
            Input?.EndTurn();
        }
    }
}
