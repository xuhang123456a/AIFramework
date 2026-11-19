#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Mail;
using AIFramework.Features.Quest;
using AIFramework.Features.Shop;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 大厅场景控制器：创建宿主 → 用四个 HUD 当视图装配 Presenter → 把按钮点击翻译成意图。
    /// <para>
    /// <b>这里为什么允许出现 "new 具体实现"</b>：它属于适配层（装配入口），宪法只禁止切片之间互引；
    /// 场景脚本就是最外层的最后一段胶水。它不含任何业务判断。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LobbyController : MonoBehaviour
    {
        /// <summary>签到面板皮肤。</summary>
        [SerializeField] private SignInHud signInHud = null!;

        /// <summary>商店面板皮肤。</summary>
        [SerializeField] private ShopHud shopHud = null!;

        /// <summary>任务面板皮肤。</summary>
        [SerializeField] private QuestHud questHud = null!;

        /// <summary>邮件面板皮肤。</summary>
        [SerializeField] private MailHud mailHud = null!;


        /// <summary>打一场的按钮（跑一轮 Realtime 内核后把结果广播给任务切片）。</summary>
        [SerializeField] private Button battleButton = null!;

        /// <summary>状态行文本。</summary>
        [SerializeField] private Text statusText = null!;

        private AIFrameworkUnityHost? _host;
        private SignInPresenter? _signIn;
        private ShopPresenter? _shop;
        private QuestPresenter? _quests;
        private MailPresenter? _mails;

        private void Awake()
        {
            var host = new AIFrameworkUnityHost();
            host.Initialize();
            _host = host;

            if (signInHud != null && host.SignInAvailable)
            {
                _signIn = new SignInPresenter(host.SignIn, signInHud);
            }

            if (shopHud != null && host.ShopAvailable)
            {
                _shop = new ShopPresenter(host.Bus, shopHud);
            }

            if (questHud != null && host.QuestAvailable)
            {
                _quests = new QuestPresenter(host.Bus, questHud);
            }

            if (mailHud != null && host.MailAvailable)
            {
                _mails = new MailPresenter(host.Bus, mailHud);
            }


            if (battleButton != null)
            {
                battleButton.onClick.AddListener(OnBattleClicked);
            }

            SetStatus("装载完成：" + DescribeAvailability(host));
        }

        private void Start()
        {
            RefreshAll();
        }

        private void OnDestroy()
        {

            if (battleButton != null)
            {
                battleButton.onClick.RemoveListener(OnBattleClicked);
            }

            _signIn?.Dispose();
            _signIn = null;

            _host?.Dispose();
            _host = null;
        }

        /// <summary>刷新所有可用面板。</summary>
        public void RefreshAll()
        {
            _signIn?.Refresh();
            _shop?.Refresh();
            _quests?.Refresh();
            _mails?.Refresh();
        }

        /// <summary>签到相关的事件由 <see cref="SignInHud"/> 直接抛给 Presenter；控制器只负责整体刷新。</summary>
        public void RefreshAfterClaim()
        {
            RefreshAll();
            SetStatus("领取结果已刷新");
        }

        /// <summary>
        /// 跑一场无渲染的实时战斗：内核在纯逻辑里推进，结果通过 <c>GameFinishedEvent</c> 广播给任务切片。
        /// <b>这不是"游戏玩法"</b>，而是证明"一个场景里能跑内核并把事实喂给 Meta 系统"的最小闭环。
        /// </summary>
        private void OnBattleClicked()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            var init = new GameplayInitData(1, 20261120, 2);
            IGameplaySession session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);

            IRealtimeSandbox? sandbox = host.Sessions.Current as IRealtimeSandbox;
            if (sandbox != null)
            {
                GameplayTagMask shield = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
                sandbox.SpawnUnit(new Vec2(0f, 0f), 1f, 5, 100, shield);
                UnitHandle attacker = sandbox.SpawnUnit(new Vec2(0f, 3f), 1f, 1, 5, GameplayTagMask.Empty);
                sandbox.SpawnProjectile(attacker, new Vec2(0f, 1.4f), new Vec2(0f, -6f), 10, GameplayTagMask.Empty);
            }

            int frames = 0;
            while (!session.IsFinished && frames < 1200)
            {
                session.Step(1f / 60f);
                frames++;
            }

            GameplayResult result = host.Sessions.EndAndCollect();
            host.PublishGameFinished(GameplayKernelType.Realtime, 1, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            RefreshAll();
            SetStatus("战斗结束：帧=" + frames + " 胜负=" + result.IsVictory + " 反射="
                + (sandbox != null ? sandbox.ReflectedCount : 0));
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }

            _host?.Log.Info("[Lobby] " + message);
        }

        private static string DescribeAvailability(AIFrameworkUnityHost host)
        {
            return "签到=" + (host.SignInAvailable ? "OK" : "缺表")
                + " 商店=" + (host.ShopAvailable ? "OK" : "缺表")
                + " 任务=" + (host.QuestAvailable ? "OK" : "缺表")
                + " 邮件=" + (host.MailAvailable ? "OK" : "缺表");
        }
    }
}
