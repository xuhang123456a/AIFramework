#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Save;
using AIFramework.Adapters.Unity.Scene;
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

        /// <summary>场景跳转器（懒创建，不参与序列化）。</summary>
        private UnitySceneDirector? _sceneDirector;

        /// <summary>进入卡牌战斗场景。</summary>
        [SerializeField] private Button? cardBattleButton = null;

        /// <summary>进入三消场景。</summary>
        [SerializeField] private Button? match3Button = null;

        [Tooltip("弹窗队列演示（可选）：挂上后可在界面上推三个不同优先级的弹窗并逐个关闭。")]
        [SerializeField] private PopupHud? popupHud = null;

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

            TryLoadInventory();
            SetStatus("装载完成：" + DescribeAvailability(host));
           
            if (cardBattleButton != null)
            {
                cardBattleButton.onClick.AddListener(GoCardBattle);
            }

            if (match3Button != null)
            {
                match3Button.onClick.AddListener(GoMatch3);
            }

            if (popupHud != null)
            {
                popupHud.Bind(host.Popups);
            }
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

            SaveInventory();

            if (cardBattleButton != null)
            {
                cardBattleButton.onClick.RemoveListener(GoCardBattle);
            }

            if (match3Button != null)
            {
                match3Button.onClick.RemoveListener(GoMatch3);
            }

            popupHud = null;

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
            SaveInventory();
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
            SaveInventory();
            SetStatus("战斗结束：帧=" + frames + " 胜负=" + result.IsVictory + " 反射="
                + (sandbox != null ? sandbox.ReflectedCount : 0));
        }

        /// <summary>
        /// 读档：把上次落盘的背包灌回账本。<b>为什么不存整个宿主状态</b>：
        /// 任务/邮件可由子表重建，只有玩家资产需要持久化；存得越少，版本迁移越省心。
        /// </summary>
        private void TryLoadInventory()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            SaveService? save = host.LobbySave;
            if (save == null)
            {
                return;
            }

            if (!save.TryLoad(0, out SaveEnvelope envelope))
            {
                host.Log.Info("[Lobby] 无可用存档，按新档开始。");
                return;
            }

            if (!InventorySaveCodec.TryDecode(envelope.Payload, out InventoryEntrySnapshot[] entries))
            {
                host.Log.Error("[Lobby] 背包存档损坏，已按新档处理（旧档保留原样，便于排查）。");
                return;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                host.Inventory.Seed(entries[i].ItemId, entries[i].Count);
            }

            host.Log.Info("[Lobby] 已读档：v" + envelope.Version + "，" + entries.Length + " 种道具。");
        }

        /// <summary>存档：只把背包快照写盘，走 <see cref="SaveService"/> 的 CRC 包络。</summary>
        private void SaveInventory()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            SaveService? save = host.LobbySave;
            if (save == null)
            {
                return;
            }

            var query = new InventoryListQueryRequest();
            InventoryListSnapshot snapshot = host.Bus.Query<InventoryListQueryRequest, InventoryListSnapshot>(in query);
            save.Save(0, new SaveEnvelope(1, InventorySaveCodec.Encode(snapshot.Items)));
        }

        /// <summary>进卡牌战斗场景。宿主随旧场景销毁自然 Dispose，这里不需要手动处理。</summary>
        private void GoCardBattle()
        {
            LoadScene("CardBattleScene");
        }

        /// <summary>进三消场景。</summary>
        private void GoMatch3()
        {
            LoadScene("Match3Scene");
        }

        private void LoadScene(string sceneName)
        {
            SaveInventory();
            _sceneDirector ??= new UnitySceneDirector(new UnityLogger("[AIFramework/Lobby] "));
            if (!_sceneDirector.Load(sceneName))
            {
                SetStatus("场景加载失败：" + sceneName + "（检查 Build Settings）");
            }
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
