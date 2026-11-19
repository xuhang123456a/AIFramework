using System.Collections.Generic;
using AIFramework.Adapters.Unity;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Localization;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Localization;
using AIFramework.Features.Mail;
using AIFramework.Features.Quest;
using AIFramework.Features.Shop;
using NUnit.Framework;

namespace AIFramework.Tests.EditMode
{
    /// <summary>
    /// Unity 侧 EditMode 测试：在真实引擎环境里验证"装配 → 切片 → 内核"链路。
    /// <para>
    /// 这些用例跑在 <c>Unity -batchmode -runTests -testPlatform EditMode</c> 下，
    /// 覆盖的是纯 .NET 单测覆盖不到的部分：<c>StreamingAssets</c> 表加载、Unity 时间/随机端口、
    /// <c>persistentDataPath</c> 存档、以及 <c>Prefab/MonoBehaviour</c> 之外的真实运行环境。
    /// </para>
    /// </summary>
    public sealed class HostSmokeTests
    {
        private const string TestStorageDir = "aiframework-editmode-tests";

        private static AIFrameworkUnityHost CreateHost()
        {
            var host = new AIFrameworkUnityHost(storageSubDirectory: TestStorageDir);
            host.Initialize();
            return host;
        }

        [Test]
        public void Host_Initializes_AndLoadsAllSlices()
        {
            using AIFrameworkUnityHost host = CreateHost();

            Assert.IsTrue(host.IsInitialized, "宿主未完成装配。");
            Assert.IsTrue(host.SignInAvailable, "签到切片缺表：TbSignInReward.bin 应位于 StreamingAssets/AIFrameworkDemo/。");
            Assert.IsTrue(host.ShopAvailable, "商店切片缺表：TbShopItem.bin。");
            Assert.IsTrue(host.QuestAvailable, "任务切片缺表：TbQuest.bin。");
            Assert.IsTrue(host.MailAvailable, "邮件切片缺表：TbMail.bin。");
            Assert.IsNotNull(host.LobbySave, "大厅存档服务未装配。");
        }

        [Test]
        public void SignIn_Claim_GrantsRewardIntoLedger()
        {
            using AIFrameworkUnityHost host = CreateHost();

            // 夹具第 1 天的奖励是 SignInRewardBase + 1（=1001），见 tools/fixtures。
            int rewardItemId = MetaItemIds.SignInRewardBase + 1;
            int before = host.Inventory.GetCount(rewardItemId);
            var presenter = new SignInPresenter(host.SignIn, host.SignInView);

            presenter.Refresh();
            Assert.AreEqual(7, host.SignInSlots.Count, "签到应渲染 7 格。");

            // 通过视图事件模拟"点领取"，走的是真实 Presenter → Handler → 总线链路。
            host.SignInView.SimulateClaimClicked();
            presenter.Refresh();

            int after = host.Inventory.GetCount(rewardItemId);
            Assert.Greater(after, before, "领取后道具数量没有增加。");

            presenter.Dispose();
        }

        [Test]
        public void Shop_Purchase_ConsumesGoldAndGrantsItem()
        {
            using AIFrameworkUnityHost host = CreateHost();

            var catalogQuery = new ShopCatalogQueryRequest();
            ShopCatalogSnapshot catalog = host.Bus.Query<ShopCatalogQueryRequest, ShopCatalogSnapshot>(in catalogQuery);
            Assert.Greater(catalog.Items.Count, 0, "商店目录为空。");

            ShopItemDef first = catalog.Items[0];
            int goldBefore = host.Inventory.GetCount(MetaItemIds.Gold);
            Assert.GreaterOrEqual(goldBefore, first.PriceCount, "演示初始金币应足够买第一件商品。");

            var buy = new BuyShopItemCommand(first.ItemId, 1);
            host.Bus.Send(in buy);

            Assert.AreEqual(goldBefore - first.PriceCount, host.Inventory.GetCount(MetaItemIds.Gold), "扣款金额不对。");
            Assert.AreEqual(1, host.Inventory.GetCount(first.ItemId), "商品未入账。");
        }

        [Test]
        public void Quest_Progresses_FromGameFinishedEvent()
        {
            using AIFrameworkUnityHost host = CreateHost();

            var before = new QuestListQueryRequest();
            QuestListSnapshot snapshot = host.Bus.Query<QuestListQueryRequest, QuestListSnapshot>(in before);
            Assert.Greater(snapshot.Quests.Count, 0, "任务表为空。");

            int progressBefore = snapshot.Quests[0].Progress;

            host.PublishGameFinished(GameplayKernelType.CardBattle, 1, isVictory: true);

            var after = new QuestListQueryRequest();
            QuestListSnapshot updated = host.Bus.Query<QuestListQueryRequest, QuestListSnapshot>(in after);
            Assert.AreEqual(progressBefore + 1, updated.Quests[0].Progress, "任务进度没有因战斗结束而推进。");
        }

        [Test]
        public void Mail_Inbox_LoadsFromSubTable()
        {
            using AIFrameworkUnityHost host = CreateHost();

            var query = new MailListQueryRequest();
            MailListSnapshot inbox = host.Bus.Query<MailListQueryRequest, MailListSnapshot>(in query);

            Assert.Greater(inbox.Mails.Count, 0, "邮件表为空。");
            Assert.AreEqual(MailState.Unread, inbox.Mails[0].State, "首封邮件应是未读状态。");
        }

        [Test]
        public void Localization_SwitchesLanguage_AndFallsBackToKey()
        {
            using AIFrameworkUnityHost host = CreateHost();

            Assert.IsTrue(host.LocalizationAvailable, "本地化切片缺表：TbLocalization.bin。");
            Assert.AreEqual(GameLanguage.ChineseSimplified, host.Localization.CurrentLanguage, "默认语言应是简体中文。");

            var play = new LocalizationQueryRequest("ui.play");
            LocalizedText chinese = host.Bus.Query<LocalizationQueryRequest, LocalizedText>(in play);
            Assert.IsTrue(chinese.Found, "ui.play 应在夹具表里。");
            Assert.AreEqual("开始游戏", chinese.Text);

            var switchCommand = new SetLanguageCommand(GameLanguage.English);
            host.Bus.Send(in switchCommand);
            Assert.AreEqual(GameLanguage.English, host.Localization.CurrentLanguage);

            LocalizedText english = host.Bus.Query<LocalizationQueryRequest, LocalizedText>(in play);
            Assert.AreEqual("Play", english.Text, "切到英文后 ui.play 应变英文。");

            var missing = new LocalizationQueryRequest("ui.not-exist");
            LocalizedText fallback = host.Bus.Query<LocalizationQueryRequest, LocalizedText>(in missing);
            Assert.IsFalse(fallback.Found, "不存在的 key 应标记 Found=false。");
            Assert.AreEqual("ui.not-exist", fallback.Text, "缺 key 时文案回退成 key 本身。");
        }

        [Test]
        public void Realtime_Session_RunsToCompletion_AndProducesResult()
        {
            using AIFrameworkUnityHost host = CreateHost();

            var init = new GameplayInitData(1, 20261120, 2);
            IGameplaySession session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);

            IRealtimeSandbox sandbox = host.Sessions.Current as IRealtimeSandbox;
            Assert.IsNotNull(sandbox, "Realtime 会话未实现 IRealtimeSandbox。");

            GameplayTagMask shield = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
            sandbox.SpawnUnit(new Vec2(0f, 0f), 1f, 5, 100, shield);
            UnitHandle attacker = sandbox.SpawnUnit(new Vec2(0f, 3f), 1f, 1, 5, GameplayTagMask.Empty);
            sandbox.SpawnProjectile(attacker, new Vec2(0f, 1.4f), new Vec2(0f, -6f), 10, GameplayTagMask.Empty);

            int frames = 0;
            while (!session.IsFinished && frames < 1200)
            {
                session.Step(1f / 60f);
                frames++;
            }

            Assert.IsTrue(session.IsFinished, "Realtime 会话在 1200 帧内没有结束。");
            Assert.Greater(frames, 0, "一次 Step 都没执行。");

            GameplayResult result = host.Sessions.EndAndCollect();
            Assert.IsTrue(result.IsVictory, "该布场下应判定胜利。");

            // 护盾反射依赖真实 Collider（UnitySpatialQuery 走 Physics.OverlapSphereNonAlloc），
            // EditMode 测试里没有场景碰撞体，所以不断言 ReflectedCount——
            // 反射行为由 DemoScene 的 Play 验收负责，纯逻辑侧由 Adapters.Tests 的 MockSpatialWorld 覆盖。
        }

        [Test]
        public void CardBattle_Session_HonorsReadAndInputPorts()
        {
            using AIFrameworkUnityHost host = CreateHost();

            var init = new GameplayInitData(1, 20261120, 2);
            IGameplaySession session = host.Sessions.Begin(GameplayKernelType.CardBattle, in init);

            var view = session as Contracts.Gameplay.Turn.ICardBattleReadView;
            var input = session as Contracts.Gameplay.Turn.ICardBattleInput;
            Assert.IsNotNull(view, "CardBattle 未实现 ICardBattleReadView。");
            Assert.IsNotNull(input, "CardBattle 未实现 ICardBattleInput。");

            Assert.AreEqual(5, view.HandCount, "开局应抽 5 张手牌。");
            Assert.AreEqual(3, view.Energy, "开局能量应为 3。");

            bool played = input.TryPlayCard(0);
            Assert.IsTrue(played, "第一张手牌应能打出。");
            Assert.Less(view.Energy, 3, "出牌后能量应减少。");

            // 一直打到结束，确认端口能让一局走完。
            int guard = 0;
            while (!session.IsFinished && guard++ < 400)
            {
                bool any = false;
                for (int i = 0; i < view.HandCount; i++)
                {
                    if (input.TryPlayCard(i))
                    {
                        any = true;
                        break;
                    }
                }

                if (!any)
                {
                    input.EndTurn();
                }
            }

            Assert.IsTrue(session.IsFinished, "卡牌战斗没有结束。");
            host.Sessions.EndAndCollect();
        }

        [Test]
        public void Match3_Session_EnforcesGridAndMoveRules()
        {
            using AIFrameworkUnityHost host = CreateHost();

            var init = new GameplayInitData(1, 20261120, 0);
            IGameplaySession session = host.Sessions.Begin(GameplayKernelType.Match3, in init);

            var view = session as Contracts.Gameplay.Turn.IMatch3ReadView;
            var input = session as Contracts.Gameplay.Turn.IMatch3Input;
            Assert.IsNotNull(view, "Match3 未实现 IMatch3ReadView。");
            Assert.IsNotNull(input, "Match3 未实现 IMatch3Input。");

            Assert.AreEqual(8, view.Width, "棋盘宽度应为 8。");
            Assert.AreEqual(8, view.Height, "棋盘高度应为 8。");
            Assert.AreEqual(0, view.GetGem(-1, -1), "越界坐标应返回 0。");

            bool rejectedDiagonal = input.TrySwap(0, 0, 1, 1);
            Assert.IsFalse(rejectedDiagonal, "对角交换应被拒绝。");

            // 扫一遍找到一次有效交换；找不到就让内核走完步数（防御性，避免死循环）。
            int guard = 0;
            while (!session.IsFinished && guard++ < 200)
            {
                bool swapped = false;
                for (int y = 0; y < view.Height && !swapped; y++)
                {
                    for (int x = 0; x + 1 < view.Width && !swapped; x++)
                    {
                        swapped = input.TrySwap(x, y, x + 1, y);
                    }
                }

                if (!swapped)
                {
                    session.Step(1f / 60f);
                }
            }

            Assert.IsTrue(session.IsFinished, "三消没有结束。");
            host.Sessions.EndAndCollect();
        }
    }

    /// <summary>场景资产守卫：Build Settings 必须按启动顺序包含全部演示场景。</summary>
    public sealed class SceneAssetTests
    {
        private static readonly string[] ExpectedScenes =
        {
            "Assets/AIFrameworkDemo/Scenes/BootScene.unity",
            "Assets/AIFrameworkDemo/Scenes/LobbyScene.unity",
            "Assets/AIFrameworkDemo/Scenes/CardBattleScene.unity",
            "Assets/AIFrameworkDemo/Scenes/Match3Scene.unity",
            "Assets/AIFrameworkDemo/Scenes/TowerDefenseScene.unity",
            "Assets/AIFrameworkDemo/Scenes/ArenaScene.unity",
            "Assets/AIFrameworkDemo/Scenes/IdleScene.unity",
            "Assets/AIFrameworkDemo/Scenes/GachaScene.unity",
            "Assets/AIFrameworkDemo/Scenes/PuzzleScene.unity",
            "Assets/AIFrameworkDemo/Scenes/TetrisScene.unity",
            "Assets/AIFrameworkDemo/Scenes/AutoChessScene.unity",
            "Assets/AIFrameworkDemo/Scenes/DemoScene.unity"
        };

        [Test]
        public void BuildSettings_ContainsAllDemoScenes_InOrder()
        {
            var paths = new List<string>();
            UnityEditor.EditorBuildSettingsScene[] scenes = UnityEditor.EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].enabled)
                {
                    paths.Add(scenes[i].path);
                }
            }

            Assert.GreaterOrEqual(paths.Count, ExpectedScenes.Length, "Build Settings 场景数不足。");

            for (int i = 0; i < ExpectedScenes.Length; i++)
            {
                Assert.AreEqual(ExpectedScenes[i], paths[i], "Build Settings[" + i + "] 不符合预期启动顺序。");
            }
        }

        [Test]
        public void SceneFiles_Exist()
        {
            string[] expected = ExpectedScenes;
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.IsTrue(
                    System.IO.File.Exists(expected[i]),
                    "场景文件缺失：" + expected[i] + "（执行 pwsh unity/tools/generate-scenes.ps1）");
            }
        }
    }
}
