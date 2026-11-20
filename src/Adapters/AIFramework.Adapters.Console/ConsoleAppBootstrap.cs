#nullable enable
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Core.Cqe;
using AIFramework.Core.Gameplay;
using AIFramework.Contracts.Services;
using AIFramework.Core.Meta;
using AIFramework.Core.Replay;
using AIFramework.Core.Services;
using AIFramework.Features.Achievement;
using AIFramework.Features.ActivitySchedule;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Gacha;
using AIFramework.Features.Experiment;
using AIFramework.Features.Guide;
using AIFramework.Features.LimitedOffer;
using AIFramework.Features.Localization;
using AIFramework.Features.Inventory;
using AIFramework.Features.Quest;
using AIFramework.Features.SeasonPass;
using AIFramework.Kernel.Realtime;
using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 无头装配入口（本仓唯一允许 <c>new</c> 具体实现的地方）：
    /// 组装总线 / 因果日志 / 端口实现 / 切片 / 内核，然后串行跑通一次完整冒烟流程。
    /// 这个类里不写业务规则，只做装配与流程编排。
    /// </summary>
    public sealed class ConsoleAppBootstrap
    {
        private const int LootReasonTagId = 6001;

        /// <summary>存档冒烟用的键名（3 个 int = 12 字节）。</summary>
        private const string SignInStateKey = "signin.state";

        private readonly IIOWriter _writer;
        private readonly CausalTagRegistry _registry;
        private readonly RingCausalLog _log;
        private readonly SystemTimeProvider _time;
        private readonly DirectoryBinaryTableLoader _tables;
        private readonly InventoryHandler _inventory;
        private readonly RedDotTree _redDots;
        private readonly MockSpatialWorld _spatial;
        private readonly string _storageDirectory;
        private readonly FileSystemStoragePort _storage;
        private readonly CqeBus _innerBus;
        private readonly LoggingCqeBus _bus;
        private readonly DemoKernelFactory _factory;
        private readonly GameplaySessionHost _host;
        private ConsoleSignInView? _signInView;

        public ConsoleAppBootstrap(IIOWriter writer)
        {
            _writer = writer;
            _registry = new CausalTagRegistry();
            _log = new RingCausalLog(256);
            _time = new SystemTimeProvider();
            _tables = new DirectoryBinaryTableLoader(ResolveDemoTablesDirectory());
            _inventory = new InventoryHandler();
            _redDots = new RedDotTree();
            _spatial = new MockSpatialWorld();
            _storageDirectory = Path.Combine(
                Path.GetTempPath(), "aiframework-smoke", Guid.NewGuid().ToString("N"));
            _storage = new FileSystemStoragePort(_storageDirectory);
            _innerBus = new CqeBus();

            int adapterTagId = _registry.Register("adapter.console");
            _bus = new LoggingCqeBus(_innerBus, _log, _registry, adapterTagId);

            _factory = new DemoKernelFactory(_spatial, DemoKernelFactory.DefaultPhases(), _tables);
            _host = new GameplaySessionHost(_factory);
        }

        public void Run()
        {
            _writer.WriteLine("=== AIFramework Phase 1 冒烟 ===");

            // 固定时间基准，保证天序号与输出可复现；真机由服务器时间或系统时钟提供。
            _time.SetUtcNow(new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));

            WireInventory();
            WireAchievement();
            WireSeasonPass();
            WireGuide();
            WireActivitySchedule();
            WireErrorToast();
            WireAccount();
            WireExperiment();
            WireLimitedOffers();
            WireSocial();
            WirePush();
            RunReplayArchiveSmoke();
            RunStorePurchaseSmoke();
            SignInPresenter presenter = WireDailySignIn(out SignInHandler signIn);

            _writer.WriteLine("今天天序号=" + signIn.TodayDayIndex + "，初始道具=" + DescribeInventory());

            // 领取 → 防重 → 跨天 → 补签卡不足拒绝 → 有卡补签 → 领取今天
            presenter.Refresh();
            SimulateClaim(presenter, signIn, "第一次领取");
            SimulateClaim(presenter, signIn, "重复领取（应被拒绝）");

            // 推进两天：昨天（周期第 2 格）成为唯一可补签的"过去且未领"天。
            _time.AdvanceDays(2);
            presenter.Refresh();
            _inventory.Seed(9001, 0);
            Retroactive(presenter, signIn, signIn.TodayDayIndex - 1, "补签卡不足（应被拒绝）");
            _inventory.Seed(9001, 2);
            Retroactive(presenter, signIn, signIn.TodayDayIndex - 1, "有补签卡后补签昨天");
            SimulateClaim(presenter, signIn, "领取今天");

            RunTurnSandbox();
            RunCardBattleSandbox();
            RunMatch3Sandbox();
            int realtimeFrames = RunRealtimeSandbox();

            ExportCausalLog();
            RunStorageSmoke(signIn);

            RunLobbyDemo();
            CartridgeShowcase.Run(_writer);
            ConsoleServices.RunSelfCheck(_writer);
            PublishBattleFacts();
            CleanupStorage();

            _writer.WriteLine("Realtime 推进帧数=" + realtimeFrames + "，最终道具=" + DescribeInventory());
            _writer.WriteLine("=== 冒烟结束 ===");
        }

        /// <summary>
        /// 装配背包切片：注册到**日志总线**（<c>_bus</c>）而不是裸总线，
        /// 这样冒烟时因果日志能记录每一条发放/消耗，切片本身并不知道日志存在。
        /// </summary>
        private void WireInventory()
        {
            _inventory.Register(_bus);
            _redDots.Register(_bus);
            _inventory.Seed(MetaItemIds.RetroactiveCard, 0);
        }

        private SignInPresenter WireDailySignIn(out SignInHandler signIn)
        {
            signIn = new SignInHandler(_bus, _time, _tables);
            signIn.Register(_bus);

            _signInView = new ConsoleSignInView(_writer);
            return new SignInPresenter(signIn, _signInView);
        }

        private void SimulateClaim(SignInPresenter presenter, SignInHandler signIn, string label)
        {
            presenter.Refresh();
            View.SimulateClaimClick();
            _writer.WriteLine(label + " → 已领天数=" + signIn.Snapshot.ClaimedCount + "，道具=" + DescribeInventory());
        }

        private void Retroactive(SignInPresenter presenter, SignInHandler signIn, int dayIndex, string label)
        {
            presenter.Refresh();
            View.SimulateRetroactiveClick(dayIndex);
            _writer.WriteLine(label + " → 已领天数=" + signIn.Snapshot.ClaimedCount
                + "，补签卡=" + _inventory.GetCount(9001) + "，道具=" + DescribeInventory());
        }

        private ConsoleSignInView View
        {
            get
            {
                return _signInView
                    ?? throw new InvalidOperationException("签到视图尚未装配，装配顺序有误。");
            }
        }

        private void RunTurnSandbox()
        {
            var init = new GameplayInitData(1, 20261118, 3);
            IGameplaySession session = _host.Begin(GameplayKernelType.Turn, in init);
            for (int i = 0; i < 12 && !session.IsFinished; i++)
            {
                session.Step(1f / 60f);
            }

            GameplayResult result = _host.EndAndCollect();
            _writer.WriteLine("[Turn] 胜负=" + result.IsVictory + "，推演步数=" + result.StepCount
                + "，存活=" + result.RemainingUnits + "，战利品=" + DescribeLoots(result));
            GrantLoots(result);
        }

        /// <summary>
        /// 卡牌战斗冒烟：只通过 <see cref="ICardBattleInput"/> 出牌/结束回合，读取
        /// <see cref="ICardBattleReadView"/> 做决策——和 UI 拿到的是同一组接口。
        /// </summary>
        private void RunCardBattleSandbox()
        {
            var init = new GameplayInitData(1, 20261120, 2);
            IGameplaySession session = _host.Begin(GameplayKernelType.CardBattle, in init);

            var view = (ICardBattleReadView)session;
            var input = (ICardBattleInput)session;
            int guard = 0;

            while (!session.IsFinished && guard++ < 400)
            {
                bool played = false;
                for (int i = 0; i < view.HandCount; i++)
                {
                    if (input.TryPlayCard(i))
                    {
                        played = true;
                        break;
                    }
                }

                if (!played)
                {
                    input.EndTurn();
                }

                session.Step(1f / 60f);
            }

            GameplayResult result = _host.EndAndCollect();
            _writer.WriteLine("[CardBattle] 回合=" + view.TurnIndex + " 胜负=" + result.IsVictory
                + " 敌HP=" + view.EnemyHp + " 我HP=" + view.PlayerHp
                + " 手牌=" + view.HandCount + " 抽/" + view.DrawPileCount + " 弃/" + view.DiscardPileCount
                + " 步数=" + result.StepCount + " 战利品=" + DescribeLoots(result));
            GrantLoots(result);
        }

        /// <summary>三消冒烟：按固定顺序尝试相邻交换，读取 <see cref="IMatch3ReadView"/> 打印棋盘状态。</summary>
        private void RunMatch3Sandbox()
        {
            var init = new GameplayInitData(1, 20261120, 0);
            IGameplaySession session = _host.Begin(GameplayKernelType.Match3, in init);

            var view = (IMatch3ReadView)session;
            var input = (IMatch3Input)session;
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
                    // 没有可消的相邻交换：走步进让内核推进级联，直到结束。
                    session.Step(1f / 60f);
                }
            }

            GameplayResult result = _host.EndAndCollect();
            _writer.WriteLine("[Match3] 分数=" + view.Score + "/" + view.TargetScore
                + " 步数=" + view.MovesLeft + " 胜负=" + result.IsVictory
                + " 战斗步数=" + result.StepCount + " 战利品=" + DescribeLoots(result));
            GrantLoots(result);
        }

        private int RunRealtimeSandbox()
        {
            var init = new GameplayInitData(2, 777, 2);
            IGameplaySession session = _host.Begin(GameplayKernelType.Realtime, in init);

            // 只走 Contracts：会话按 IRealtimeSandbox 布场，只读视图按接口绑定空间查询，
            // 适配层不需要知道内核具体类型（也就不会把 BattleWorld 引脚泄漏出去）。
            IRealtimeSandbox sandbox = _host.Current as IRealtimeSandbox
                ?? throw new InvalidOperationException("Realtime 内核未实现 IRealtimeSandbox。");
            IUnitWorldReadView units = session as IUnitWorldReadView
                ?? throw new InvalidOperationException("Realtime 内核未实现 IUnitWorldReadView。");
            IProjectileWorldReadView projectiles = session as IProjectileWorldReadView
                ?? throw new InvalidOperationException("Realtime 内核未实现 IProjectileWorldReadView。");
            _spatial.Bind(units, projectiles);

            GameplayTagMask shieldTags = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
            sandbox.SpawnUnit(new Vec2(0f, 0f), 1f, 5, 100, shieldTags);
            UnitHandle attacker = sandbox.SpawnUnit(new Vec2(0f, 3f), 1f, 1, 5, GameplayTagMask.Empty);
            sandbox.SpawnProjectile(attacker, new Vec2(0f, 1.4f), new Vec2(0f, -6f), 10, GameplayTagMask.Empty);

            int frames = 0;
            for (int i = 0; i < 120 && !session.IsFinished; i++)
            {
                session.Step(1f / 60f);
                frames++;
            }

            GameplayResult result = _host.EndAndCollect();
            _writer.WriteLine("[Realtime] 推进帧=" + frames + "，反射次数=" + sandbox.ReflectedCount
                + "，打断次数=" + sandbox.InterruptedCount + "，胜负=" + result.IsVictory
                + "，结算步数=" + result.StepCount + "，触发标签数=" + result.TriggeredTags.Count
                + "，战利品=" + DescribeLoots(result));
            GrantLoots(result);
            return frames;
        }

        private void GrantLoots(GameplayResult result)
        {
            IReadOnlyList<LootItem> loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                _bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, LootReasonTagId));
            }
        }

        /// <summary>
        /// 存档端口冒烟：把签到状态的 3 个 int（12 字节）写进临时目录，重开一个端口实例读回校验，
        /// 最后无条件递归删除临时目录。非帧内代码，允许分配。
        /// </summary>
        /// <summary>推送接线：权限 → token → 每日提醒排期（假推送后端，不联网、不弹系统框）。</summary>
        private void WirePush()
        {
            var push = new ConsolePushService(_time);
            var scheduler = new PushScheduler(push, _time, maxScheduled: 8);

            PushPermissionState permission = push.RequestPermission();
            PushRegisterResult registered = scheduler.EnsureRegistered("console-demo-token-0001");
            bool scheduled = scheduler.ScheduleDaily(1, "体力已满", "回来看看吧", 9, 0);
            PushNotification[] plan = push.GetScheduled();

            _writer.WriteLine("[Push] 权限=" + permission + " 登记=" + registered
                + " 排期=" + scheduled + " 计划=" + plan.Length
                + " 下次触发=" + (plan.Length > 0 ? plan[0].FireAtUnixSeconds : 0));
        }

        /// <summary>社交接线：赠送/领取体力，跨日重置由时间驱动（假社交后端，不联网）。</summary>
        private void WireSocial()
        {
            var social = new ConsoleSocialService(_time, friendCount: 3);
            var friends = new FriendService(social, _time, maxGiftPerDay: 2);

            FriendOperationOutcome gift = friends.GiftStamina("friend-1");
            int claimed = friends.ClaimAllStamina();
            FriendOperationOutcome duplicate = friends.GiftStamina("friend-1");

            _writer.WriteLine("[Social] 好友=" + social.FriendCount
                + " 赠送=" + gift.Success + " 领取=" + claimed
                + " 重复赠送=" + duplicate.Result
                + " 今日赠送剩余=" + friends.DailyGiftRemaining);
        }

        /// <summary>限时礼包接线：窗口/折扣/限购由切片裁决，成交后经 GrantItemCommand 发货。</summary>
        private void WireLimitedOffers()
        {
            var offers = new LimitedOfferHandler(_bus, _time, _tables);
            offers.Register(_bus);

            LimitedOfferSnapshot snapshot = _bus.Query<LimitedOfferQueryRequest, LimitedOfferSnapshot>(new LimitedOfferQueryRequest());

            // 演示选"当前可买"的第一单：表里刻意同时配了已结束/进行中/未开始三种窗口，
            // 直接取第 0 行会拿到已结束的礼包，看到 NotOpen 反而像功能坏了。
            int firstId = 0;
            for (int i = 0; i < snapshot.Offers.Length; i++)
            {
                if (snapshot.Offers[i].IsOpen && snapshot.Offers[i].TotalRemaining > 0)
                {
                    firstId = snapshot.Offers[i].OfferId;
                    break;
                }
            }

            LimitedOfferPurchaseOutcome bought = offers.Purchase(firstId);
            LimitedOfferPurchaseOutcome again = offers.Purchase(firstId);

            _writer.WriteLine("[LimitedOffer] 可买=" + snapshot.OpenCount + "/" + snapshot.Offers.Length
                + " 首单=" + bought.Result + " 价=" + bought.FinalPrice + "x" + bought.GrantCount
                + " 再买=" + again.Result);
        }

        /// <summary>账号接线：游客静默登录 + 会话 TTL 过期演示（假后端，不联网）。</summary>
        private void WireAccount()
        {
            var accounts = new ConsoleAccountService(_time);
            var session = new AccountSessionManager(accounts, _time);
            AccountLoginOutcome signIn = session.EnsureSignedIn();
            session.TryGetAccount(out AccountInfo info);
            AccountLoginOutcome again = session.EnsureSignedIn();

            _writer.WriteLine("[Account] 登录=" + signIn.IsSuccess + " 玩家=" + info.PlayerId
                + " 类型=" + info.Kind + " 重复登录=" + again.Result + " 状态=" + session.State);
        }

        /// <summary>A/B 实验接线：按玩家 id 确定性分桶并曝光（同一玩家只曝光一次）。</summary>
        private void WireExperiment()
        {
            var experiment = new ExperimentHandler(_bus, _tables);
            experiment.Register(_bus);
            _bus.Send(new ExperimentEvaluateCommand(10001));

            ExperimentSnapshot first = _bus.Query<ExperimentQueryRequest, ExperimentSnapshot>(new ExperimentQueryRequest());
            _bus.Send(new ExperimentEvaluateCommand(10001));
            ExperimentSnapshot second = _bus.Query<ExperimentQueryRequest, ExperimentSnapshot>(new ExperimentQueryRequest());

            string sample = first.Assignments.Count > 0
                ? first.Assignments[0].Key + "=" + first.Assignments[0].Variant
                : "空表";
            _writer.WriteLine("[Experiment] 分桶=" + first.Assignments.Count + " 示例=" + sample
                + " 曝光=" + first.ExposedCount + " 重复曝光后=" + second.ExposedCount);
        }

        /// <summary>
        /// 错误码 → 本地化 → Toast 统一链路：业务只上报错误码，文案与级别由目录决定。
        /// 这里顺带把本地化处理器装上，验证"有翻译用翻译、缺翻译回退兜底"两条路径都真实生效。
        /// </summary>
        private void WireErrorToast()
        {
            var localization = new LocalizationHandler(_bus, _tables);
            localization.Register(_bus);

            var presenter = new ConsoleToastPresenter(_writer);
            var router = new ErrorToastRouter(_bus, presenter);

            string balance = router.Route(3004);
            string network = router.Route(1001);
            string claimed = router.Route(5003);
            string unknown = router.Route(999999);

            _writer.WriteLine("[Toast] 共=" + presenter.ShownCount + " 条，兜底=" + router.FallbackCount
                + " 未知码=" + router.UnknownCodeCount + "；示例=" + balance + " / " + network + " / " + claimed);
        }

        /// <summary>活动轮换接线：排期由表驱动，tick 由时间源推动（这里只跑一次演示）。</summary>
        private void WireActivitySchedule()
        {
            var schedule = new ActivityScheduleHandler(_bus, _time, _tables);
            schedule.Register(_bus);
            _bus.Send(new ActivityTickCommand());

            ActivityScheduleSnapshot snapshot = _bus.Query<ActivityQueryRequest, ActivityScheduleSnapshot>(new ActivityQueryRequest());
            _writer.WriteLine("[Activity] 开启=" + snapshot.OpenCount + "/" + snapshot.Activities.Count
                + " 首个=" + (snapshot.Activities.Count > 0 ? snapshot.Activities[0].Name : "无")
                + " 剩余=" + (snapshot.Activities.Count > 0 ? snapshot.Activities[0].SecondsRemaining : 0) + "s");
        }

        /// <summary>回放落盘冒烟：编码 → 存储 → 列表 → 读回帧指纹 → 删除。</summary>
        private void RunReplayArchiveSmoke()
        {
            var store = new ReplayArchiveStore(_storage, "demo-replays");
            var wave1 = new WaveSpecData(2, 0.35f);
            var wave2 = new WaveSpecData(3, 0.35f);
            var script = new ReplayScriptData(randomSeed: 20261120, waveCount: 2, new[] { wave1, wave2 });
            long now = 1764000000L;
            string key = store.Save(kernelType: 1, levelId: 1, randomSeed: 20261120, script, new long[] { 11L, 22L, 33L }, now);
            ReplayArchiveInfo[] list = store.List();
            bool loaded = store.TryLoadFingerprints(key, out long[] fingerprints, out string reason);
            int overlong = store.Save(kernelType: 1, levelId: 2, randomSeed: 20261120, script, new long[] { 1L }, now + 1).Length > 0 ? 1 : 0;
            bool deleted = store.Delete(key);

            string shareText = ReplaySharing.Export(store.List().Length > 0 ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 9 });
            bool imported = ReplaySharing.TryImport(shareText, out byte[]? sharedBack, out string shareReason);

            _writer.WriteLine("[Replay] 保存后列表=" + list.Length + " 读回=" + loaded
                + " 帧数=" + (loaded ? fingerprints.Length : 0)
                + " 再存=" + overlong + " 删除=" + deleted);
            _writer.WriteLine("[ReplayShare] 导出=" + shareText.Length + " 字符 导入=" + imported
                + " 字节=" + (sharedBack != null ? sharedBack.Length : 0)
                + (imported ? string.Empty : " 原因=" + shareReason));
        }

        /// <summary>新手引导接线：表驱动步骤机；外部事实（进大厅/开商店/完成战斗）由适配层翻译成 trigger。</summary>
        private void WireGuide()
        {
            var guide = new GuideHandler(_bus, _tables);
            guide.Register(_bus);

            // 演示四个触发点：启动 → 进大厅 → 打开商店 → 完成一场战斗。
            _bus.Send(new GuideTriggerCommand(0, 0));
            _bus.Send(new GuideAdvanceCommand(1));
            _bus.Send(new GuideTriggerCommand(1, 0));
            _bus.Send(new GuideAdvanceCommand(2));
            _bus.Send(new GuideTriggerCommand(2, 0));
            _bus.Send(new GuideAdvanceCommand(3));

            // 完成一场战斗由事件桥接驱动（切片间不互相引用）。
            _bus.Subscribe(new BattleGuideBridge(_bus));
            _bus.Publish(new GameFinishedEvent(GameplayKernelType.Turn, 1, isVictory: true));

            GuideSnapshot snapshot = _bus.Query<GuideQueryRequest, GuideSnapshot>(new GuideQueryRequest());
            _writer.WriteLine("[Guide] 当前=" + snapshot.StepId + "(" + snapshot.Title + ")"
                + " 已到条件=" + snapshot.Reached
                + " 进度=" + snapshot.CompletedCount + "/" + snapshot.TotalCount
                + " 完成=" + snapshot.IsCompleted);
        }

        /// <summary>商店 → IAP → 发货 → 埋点闭环冒烟（假商店，不发真实货币）。</summary>
        private void RunStorePurchaseSmoke()
        {
            var analytics = new ConsoleAnalyticsService(new System.IO.StringWriter());
            var flow = new ConsoleStorePurchaseFlow(_bus, analytics);

            StorePurchaseOutcome gold = flow.Buy(ConsoleIapService.ProductGold60);
            StorePurchaseOutcome goldAgain = flow.Buy(ConsoleIapService.ProductGold60);
            StorePurchaseOutcome noAds = flow.Buy(ConsoleIapService.ProductNoAds);
            StorePurchaseOutcome noAdsAgain = flow.Buy(ConsoleIapService.ProductNoAds);
            int restored = flow.Restore();

            _writer.WriteLine(
                "[Shop/IAP] 金币=" + gold.Granted + "(" + gold.ItemId + "x" + gold.Count + ")"
                + " 再买=" + goldAgain.Granted + "(" + goldAgain.Reason + ")"
                + " 去广告=" + noAds.Granted + " 重复=" + noAdsAgain.Granted + "(" + noAdsAgain.Reason + ")"
                + " 恢复=" + restored + " 埋点=" + analytics.AcceptedCount);
        }

        /// <summary>赛季通行证接线：战斗结算经验由适配层桥接注入（切片间不互相引用）。</summary>
        private void WireSeasonPass()
        {
            // 赛季终点本应服务端下发；演示固定一个 UTC 时刻（晚于时间源锚点）。
            long seasonEndUnix = new DateTimeOffset(new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var seasonPass = new SeasonPassHandler(_bus, _time, _tables, seasonEndUnix);
            seasonPass.Register(_bus);
            _bus.Subscribe(new BattleSeasonPassBridge(_bus));

            _bus.Send(new AddSeasonXpCommand(120));
            SeasonPassSnapshot snapshot = _bus.Query<SeasonPassQueryRequest, SeasonPassSnapshot>(new SeasonPassQueryRequest());
            _writer.WriteLine("[Season] Lv." + snapshot.Level + " xp=" + snapshot.CurrentXp + "/" + snapshot.NextLevelXp
                + " 可领=" + snapshot.ClaimableCount + " 剩余=" + snapshot.RemainingSeconds + "s");
        }

        private sealed class BattleGuideBridge : IEventHandler<GameFinishedEvent>
        {
            private readonly ICqeBus _bus;

            public BattleGuideBridge(ICqeBus bus)
            {
                _bus = bus;
            }

            public void Handle(in GameFinishedEvent evt)
            {
                _bus.Send(new GuideTriggerCommand(GuideTriggers.BattleFinished, evt.LevelId));
            }
        }

        private sealed class BattleSeasonPassBridge : IEventHandler<GameFinishedEvent>
        {
            private readonly ICqeBus _bus;

            public BattleSeasonPassBridge(ICqeBus bus)
            {
                _bus = bus;
            }

            public void Handle(in GameFinishedEvent evt)
            {
                _bus.Send(new AddSeasonXpCommand(evt.IsVictory ? 120 : 40));
            }
        }

        /// <summary>成就切片接线：注册处理器 + 把"已发生的事实"翻译成计数命令。</summary>
        private void WireAchievement()
        {
            var achievements = new AchievementHandler(_bus, _tables);
            achievements.Register(_bus);

            // 适配层做桥：切片之间不允许互相引用（AIF001），所以"签到/抽卡/战斗/金币"这些事实
            // 必须由装配层翻译成 AchievementRecordCommand。
            _bus.Subscribe(new SignInAchievementBridge(_bus));
            _bus.Subscribe(new GachaAchievementBridge(_bus));
            _bus.Subscribe(new BattleAchievementBridge(_bus));
            _bus.Subscribe(new GoldAchievementBridge(_bus));
        }

        private sealed class SignInAchievementBridge : IEventHandler<DailySignInClaimedEvent>
        {
            private readonly ICqeBus _bus;

            public SignInAchievementBridge(ICqeBus bus)
            {
                _bus = bus;
            }

            public void Handle(in DailySignInClaimedEvent evt)
            {
                _bus.Send(new AchievementRecordCommand(AchievementMetrics.SignIn, 1));
            }
        }

        private sealed class GachaAchievementBridge : IEventHandler<GachaDrawnEvent>
        {
            private readonly ICqeBus _bus;

            public GachaAchievementBridge(ICqeBus bus)
            {
                _bus = bus;
            }

            public void Handle(in GachaDrawnEvent evt)
            {
                _bus.Send(new AchievementRecordCommand(AchievementMetrics.Gacha, 1));
            }
        }

        private sealed class BattleAchievementBridge : IEventHandler<GameFinishedEvent>
        {
            private readonly ICqeBus _bus;

            public BattleAchievementBridge(ICqeBus bus)
            {
                _bus = bus;
            }

            public void Handle(in GameFinishedEvent evt)
            {
                if (evt.IsVictory)
                {
                    _bus.Send(new AchievementRecordCommand(AchievementMetrics.Clear, 1));
                }
            }
        }

        private sealed class GoldAchievementBridge : IEventHandler<ItemGrantedEvent>
        {
            private readonly ICqeBus _bus;

            public GoldAchievementBridge(ICqeBus bus)
            {
                _bus = bus;
            }

            public void Handle(in ItemGrantedEvent evt)
            {
                if (evt.ItemId == MetaItemIds.Gold)
                {
                    _bus.Send(new AchievementRecordCommand(AchievementMetrics.Gold, evt.Count));
                }
            }
        }

        private void RunStorageSmoke(SignInHandler signIn)
        {
            string directory = Path.Combine(
                Path.GetTempPath(), "aiframework-smoke", Guid.NewGuid().ToString("N"));
            try
            {
                var storage = new FileSystemStoragePort(directory);
                SignInState state = signIn.Snapshot;

                byte[] payload;
                using (var buffer = new MemoryStream(12))
                {
                    using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
                    {
                        writer.Write(state.CycleStartDayIndex);
                        writer.Write(state.ClaimedCount);
                        writer.Write(state.LastClaimDayIndex);
                    }

                    payload = buffer.ToArray();
                }

                storage.Write(SignInStateKey, payload);

                // 重新 new 一个端口实例：确认落盘字节真的过了磁盘，而不是内存缓存。
                var reopened = new FileSystemStoragePort(directory);
                byte[] raw = reopened.Read(SignInStateKey)
                    ?? throw new InvalidOperationException("存档往返失败：键 " + SignInStateKey + " 读回为空。");

                int cycle;
                int claimed;
                int last;
                using (var reader = new BinaryReader(new MemoryStream(raw, writable: false)))
                {
                    cycle = reader.ReadInt32();
                    claimed = reader.ReadInt32();
                    last = reader.ReadInt32();
                }

                _writer.WriteLine("[Storage] 存档往返: cycle=" + cycle + " claimed=" + claimed
                    + " last=" + last + " keys=[" + string.Join(", ", reopened.ListKeys()) + "]");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        private void ExportCausalLog()
        {
            int visible = _log.Count < _log.Capacity ? _log.Count : _log.Capacity;
            _writer.WriteLine("--- 因果日志（累计 " + _log.Count + " 条，展示最近 " + visible + " 条）---");
            for (int i = 0; i < visible; i++)
            {
                CausalRecord record = _log[i];
                _writer.WriteLine("#" + record.SequenceId
                    + " msg=" + Resolve(record.MessageTagId)
                    + " src=" + Resolve(record.SourceTagId)
                    + " outcome=" + record.OutcomeCode);
            }
        }

        private string Resolve(int tagId)
        {
            string? name = _registry.Resolve(tagId);
            return name ?? ("tag#" + tagId);
        }

        private string DescribeInventory()
        {
            IReadOnlyList<InventoryEntry> snapshot = _inventory.Snapshot();
            if (snapshot.Count == 0)
            {
                return "空";
            }

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(snapshot[i].ItemId).Append('=').Append(snapshot[i].Count);
            }

            return builder.ToString();
        }

        /// <summary>跑一遍大厅链路（商店/任务/邮件/红点/弹窗/存档），验证 Meta 切片真的能协作。</summary>
        private void RunLobbyDemo()
        {
            var lobby = new LobbyDemo(_writer, _bus, _time, _tables, _storage, _inventory, _redDots);
            lobby.Wire();
            lobby.Run();
        }

        /// <summary>
        /// 把两场内核对战结果翻成 <see cref="GameFinishedEvent"/> 广播出去：
        /// 任务/活动类切片只依赖这个事实，不需要知道打的是哪个内核。
        /// </summary>
        private void PublishBattleFacts()
        {
            var turn = new GameFinishedEvent(GameplayKernelType.Turn, 1, isVictory: true);
            _bus.Publish(in turn);
            var realtime = new GameFinishedEvent(GameplayKernelType.Realtime, 2, isVictory: false);
            _bus.Publish(in realtime);

            var query = new QuestListQueryRequest();
            QuestListSnapshot quests = _bus.Query<QuestListQueryRequest, QuestListSnapshot>(in query);
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < quests.Quests.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                QuestViewData quest = quests.Quests[i];
                builder.Append('Q').Append(quest.QuestId).Append(' ')
                    .Append(quest.Progress).Append('/').Append(quest.TargetCount);
            }

            _writer.WriteLine("[Quest] 两场战斗后进度：" + builder);
        }

        /// <summary>
        /// 定位配置表夹具目录：优先输出目录下的 <c>DemoTables</c>（csproj 会拷贝），
        /// 其次回退到源码目录（直接用 dotnet run 从仓根启动时）。
        /// </summary>
        private static string ResolveDemoTablesDirectory()
        {
            string outputLocal = Path.Combine(AppContext.BaseDirectory, "DemoTables");
            if (Directory.Exists(outputLocal))
            {
                return outputLocal;
            }

            // 从 bin/Debug/net10.0 回溯到工程目录：…/bin/<cfg>/<tfm> → 上三级
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 4 && directory != null; i++)
            {
                directory = directory.Parent;
                if (directory == null)
                {
                    break;
                }

                string candidate = Path.Combine(directory.FullName, "DemoTables");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 最后兜底：仓根的 unity StreamingAssets（与 Unity 侧读同一份夹具）
            return Path.Combine("unity", "Assets", "StreamingAssets", "AIFrameworkDemo");
        }

        /// <summary>清理本次冒烟创建的临时存档目录（幂等）。</summary>
        private void CleanupStorage()
        {
            if (Directory.Exists(_storageDirectory))
            {
                Directory.Delete(_storageDirectory, recursive: true);
            }
        }        private static string DescribeLoots(GameplayResult result)
        {
            IReadOnlyList<LootItem> loots = result.Loots;
            if (loots.Count == 0)
            {
                return "无";
            }

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < loots.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(loots[i].ItemId).Append('×').Append(loots[i].Count);
            }

            return builder.ToString();
        }
    }
}
