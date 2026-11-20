#nullable enable
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Gm;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Adapters.Console.E2e;
using AIFramework.Core.Cqe;
using AIFramework.Core.Gameplay;
using AIFramework.Contracts.Services;
using AIFramework.Contracts.Testing;
using AIFramework.Core.Meta;
using AIFramework.Core.Replay;
using AIFramework.Core.Save;
using AIFramework.Core.Testing;
using AIFramework.Core.Services;
using AIFramework.Features.Achievement;
using AIFramework.Features.ActivitySchedule;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Gacha;
using AIFramework.Features.Experiment;
using AIFramework.Features.Guide;
using AIFramework.Features.ReplayBrowser;
using AIFramework.Features.RichText;
using AIFramework.Features.LimitedOffer;
using AIFramework.Features.Localization;
using AIFramework.Features.VoiceOver;
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

        private GuideHandler? _guide;
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
            WireErrorReport();
            WireReconcile();
            WireResourceManifest();
            WireLeaderboardPager();
            WireVoiceOver();
            WireSaveMigration();
            WireEconomySim();
            WireMatch3Difficulty();
            WirePlayerMetrics();
            WireGuideDemo();
            WireSaveCompression();
            WireAssetBundles();
            WireReplayBrowser();
            WireMockGateway();
            WireRichText();
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
            WireGm();
            RunE2e();
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
        /// <summary>
        /// 热更资源清单校验：对 DemoTable 目录建清单再逐条校验，演示"全有或全无"的回退结论。
        /// <para>用真实 .bin 文件做校验对象——空跑假数据的测试没有说服力。</para>
        /// </summary>
        private void WireResourceManifest()
        {
            string tablesDirectory = ResolveDemoTablesDirectory();
            string[] files = System.IO.Directory.GetFiles(tablesDirectory, "*.bin");
            var entries = new AssetManifestEntry[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                byte[] data = System.IO.File.ReadAllBytes(files[i]);
                entries[i] = new AssetManifestEntry(
                    System.IO.Path.GetFileName(files[i]),
                    ResourceManifestVerifier.Sha256Hex(data),
                    data.Length);
            }

            var manifest = new AssetManifest("demo-tables", entries);
            var verifier = new ResourceManifestVerifier();
            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, name =>
            {
                string path = System.IO.Path.Combine(tablesDirectory, name);
                return System.IO.File.Exists(path) ? System.IO.File.ReadAllBytes(path) : null;
            });

            int failures = ResourceManifestVerifier.CountFailures(outcomes);
            _writer.WriteLine("[Resource] 清单=" + entries.Length + " 校验=" + outcomes.Length
                + " 失败=" + failures + " 可回退=" + (!ResourceManifestVerifier.AllOk(outcomes) ? "是" : "否"));
        }

        /// <summary>Mock 网关演示：注册成功/超时/服务端错误三种规则，打印响应与调用记录。</summary>
        private void WireMockGateway()
        {
            var gateway = new MockGateway(_time);
            gateway.AddRule(new MockRule("save.write", "slot=1", MockTransportOutcome.Ok, "{\"ok\":true}", 20));
            gateway.AddRule(new MockRule("leaderboard.submit", null, MockTransportOutcome.ServerError, string.Empty, 0));
            gateway.AddRule(new MockRule("cloud.load", null, MockTransportOutcome.Timeout, string.Empty, 3000));

            MockResponse ok = gateway.Invoke("save.write", "slot=1&data=AISV");
            MockResponse error = gateway.Invoke("leaderboard.submit", "score=42");
            MockResponse timeout = gateway.Invoke("cloud.load", "user=10001");

            _writer.WriteLine("[MockGate] 成功=" + ok.Outcome + " 载荷=" + ok.Payload
                + " 错误=" + error.Outcome + " 超时=" + timeout.Outcome + " 调用=" + gateway.Calls.Length);
        }

        /// <summary>富文本安全渲染演示：合法标签保留、非法标签剥离并给诊断。</summary>
        private void WireRichText()
        {
            RichTextDocument document = RichTextParser.Parse(
                "活动开启！<b>限时</b> <color=#FF8800>双倍</color> 掉落<size=24>（仅今日）</size><script>alert(1)</script>");

            _writer.WriteLine("[RichText] 片段=" + document.Spans.Length + " 诊断=" + document.Diagnostics.Length
                + " 纯文本=" + document.PlainText);
        }

        /// <summary>回放列表演示：从真实录像仓库读列表 → 分页/排序 → 删除一条并核对事件。</summary>
        private void WireReplayBrowser()
        {
            var store = new ReplayArchiveStore(_storage, "replay-browser");
            for (int i = 0; i < 12; i++)
            {
                int seed = 100 + i;
                var script = new ReplayScriptData(seed, 0, System.Array.Empty<WaveSpecData>());
                var fingerprints = new long[3 + (i % 4)];
                for (int f = 0; f < fingerprints.Length; f++)
                {
                    fingerprints[f] = (i * 10) + f;
                }

                store.Save(0, 1 + (i % 3), seed, script, fingerprints, 1_800_000_000 + i);
            }

            var browser = new ReplayBrowserHandler(_bus, new ConsoleReplayArchivePort(store));
            ReplayListPage first = browser.Query(new ReplayListQuery(0, 5));
            ReplayListPage byLength = browser.Query(new ReplayListQuery(0, 3, ReplaySortOrder.LongestFirst));
            bool deleted = browser.Delete(first.Entries[0].SlotKey);
            ReplayListPage afterDelete = browser.Query(new ReplayListQuery(0, 5));

            _writer.WriteLine("[ReplayList] " + first.ToReportLine());
            _writer.WriteLine("[ReplayList] 按长度=" + byLength.Entries[0].SlotKey + " 最长帧=" + byLength.Entries[0].FrameCount);
            _writer.WriteLine("[ReplayList] 删除=" + deleted + " 剩余=" + afterDelete.TotalCount);
        }

        /// <summary>分包清单校验演示：核心包/关卡包/赛季包 + 依赖关系，打印体积与问题汇总。</summary>
        private void WireAssetBundles()
        {
            var bundles = new[]
            {
                new AssetBundleSpec("core", new[] { "ui/common.prefab", "audio/common.bnk" }, System.Array.Empty<string>(), 12L * 1024 * 1024),
                new AssetBundleSpec("levels", new[] { "scenes/lobby.unity", "scenes/arena.unity" }, new[] { "core" }, 24L * 1024 * 1024),
                new AssetBundleSpec("season", new[] { "ui/season.prefab", "audio/season.bnk" }, new[] { "core" }, 8L * 1024 * 1024),
            };

            AssetBundleReport report = AssetBundleValidator.Validate(bundles);
            _writer.WriteLine(report.ToReportLine());
        }

        /// <summary>存档压缩演示：对真实 AISV 存档做 deflate，打印体积对比（云存档按字节计费）。</summary>
        private void WireSaveCompression()
        {
            var seeder = new SaveService(_storage, "compress-seed");
            // 用"接近真实"的存档规模（60 种道具）：48 字节的小档压缩后反而更大，
            // 正是 ShouldCompress 阈值存在的理由；演示也要展示真实收益而不是负数。
            var entries = new InventoryEntrySnapshot[60];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new InventoryEntrySnapshot(1001 + (i * 7), 10 + i);
            }

            byte[] payload = InventorySaveCodec.Encode(entries);
            seeder.Save(0, new SaveEnvelope(SaveService.CurrentVersion, payload));
            byte[]? saveBytes = _storage.Read(seeder.Key(0));

            if (saveBytes == null)
            {
                _writer.WriteLine("[Compress] 无存档可压。");
                return;
            }

            if (!SaveCompression.ShouldCompress(saveBytes.Length))
            {
                _writer.WriteLine("[Compress] 存档仅 " + saveBytes.Length + "B，低于压缩阈值，跳过（头开销更大）。");
                return;
            }

            SaveCompressionReport report = SaveCompression.Evaluate(saveBytes);
            _writer.WriteLine(report.ToReportLine());
        }

        /// <summary>引导自动演示：脚本化"触发→推进"并按时间推进，验证演示链路可被数据驱动。</summary>
        private void WireGuideDemo()
        {
            // 复用 WireGuide 里已注册的处理器：同一总线重复注册会抛 InvalidOperationException。
            GuideHandler? guide = _guide;
            if (guide == null)
            {
                _writer.WriteLine("[GuideDemo] 引导未装配，跳过。");
                return;
            }

            var script = new GuideDemoScript("console-demo", new[]
            {
                new GuideDemoStep(kind: 0, value: 0, targetId: 0, delayMs: 0),
                new GuideDemoStep(kind: 1, value: 1, targetId: 0, delayMs: 200),
                new GuideDemoStep(kind: 0, value: 1, targetId: 0, delayMs: 200),
            });

            var player = new GuideDemoPlayer(_bus);
            player.Start(in script);
            player.Tick(1000);

            _writer.WriteLine("[GuideDemo] 脚本=" + player.ScriptName + " 步=" + player.ExecutedSteps
                + "/" + script.Steps.Length + " 完成=" + player.IsFinished);
        }

        /// <summary>玩家数据看板：喂几条埋点事件 → 聚合指标 → 导出单行 CSV（真实事件号）。</summary>
        private void WirePlayerMetrics()
        {
            var metrics = new PlayerMetrics(_time);
            metrics.BeginSession();
            metrics.MarkActive();
            metrics.Track(new AIFramework.Contracts.Services.AnalyticsEvent(
                AIFramework.Contracts.Services.AnalyticsEventIds.BattleStart, 1, 0, 0, null));
            metrics.Track(new AIFramework.Contracts.Services.AnalyticsEvent(
                AIFramework.Contracts.Services.AnalyticsEventIds.BattleEnd, 1, 1200, 0, null));
            metrics.EndSession();

            _writer.WriteLine(metrics.ToCsv().TrimEnd());
        }

        /// <summary>三消难度评估演示：对一张 8×8 示例盘算可行手与难度分（关卡批量导入时用同一口径）。</summary>
        private void WireMatch3Difficulty()
        {
            var cells = new int[64];
            int[] pattern = { 1, 2, 3, 1, 2, 3, 1, 2 };
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = pattern[(i / 8 + i % 8) % pattern.Length];
            }

            Match3DifficultyReport report = Match3DifficultyEvaluator.Evaluate(cells, 8, 8);
            _writer.WriteLine(report.ToReportLine());
        }

        /// <summary>
        /// 经济模拟演示：按"每日 20 局、每局掉落 120 金币、商店日消耗 1500"跑 30 天，看通胀判定。
        /// <para>数字是演示假设，真实数值应来自掉落表/商店表按玩法时长加权。</para>
        /// </summary>
        private void WireEconomySim()
        {
            var days = new EconomyDay[30];
            for (int i = 0; i < days.Length; i++)
            {
                days[i] = new EconomyDay(earnedCurrency: 20 * 120, spentCurrency: 1500, earnedItems: 20, spentItems: 15);
            }

            EconomyVerdict verdict = EconomySimulator.Simulate(days);
            _writer.WriteLine(verdict.ToReportLine());
        }

        /// <summary>
        /// 游客档迁移演示：先造一份"游客档"（备份槽写 AISV 包络），再迁移到账号槽位。
        /// <para>用真实字节（SaveService 写、SaveMigrationService 读）而不是空跑假数据。</para>
        /// </summary>
        private void WireSaveMigration()
        {
            var seeder = new SaveService(_storage, "migrate-seed");
            byte[] payload = InventorySaveCodec.Encode(new[]
            {
                new InventoryEntrySnapshot(1001, 30),
                new InventoryEntrySnapshot(2001, 2),
            });
            seeder.Save(0, new SaveEnvelope(SaveService.CurrentVersion, payload));
            byte[]? guestBytes = _storage.Read(seeder.Key(0));
            if (guestBytes != null)
            {
                _storage.Write("save.guest", guestBytes);
            }

            var migration = new SaveMigrationService(_storage, _time, "save");
            SaveMigrationResult result = migration.MigrateGuestToAccount(new SaveMigrationRequest(
                guestSlotKey: "guest",
                targetSlotKey: "account-10001",
                policy: SaveMergePolicy.UnionBest,
                keepGuestBackup: true));

            _writer.WriteLine("[SaveMigrate] 成功=" + result.Success + " 条目=" + result.MergedEntries
                + " 目标=" + result.TargetSlotKey + " 备份=" + result.BackupSlotKey
                + (result.Success ? string.Empty : " 原因=" + result.Reason));
        }

        /// <summary>语音字幕接线：表驱动语音行 + 语言回退 + 字幕时间轴推进（不真的播放音频）。</summary>
        private void WireVoiceOver()
        {
            var voice = new VoiceOverHandler(_bus, _tables);
            voice.Register(_bus);

            VoiceOverSnapshot snapshot = voice.Snapshot();
            int lineId = snapshot.Lines.Length > 0 ? snapshot.Lines[0].LineId : 0;
            bool played = voice.Play(lineId);
            voice.Tick(120);

            VoiceOverSnapshot after = voice.Snapshot();
            _writer.WriteLine("[VoiceOver] 行数=" + snapshot.Lines.Length
                + " 播放=" + played + " 当前行=" + after.CurrentLineId
                + " 播放中=" + after.IsPlaying + " 字幕=" + (after.Lines.Length > 0 && after.Lines[0].HasSubtitle));
        }

        /// <summary>排行榜分页接线：假服务端排序 → pager 归一化 → 取自己所在页。</summary>
        private void WireLeaderboardPager()
        {
            var server = new ConsoleLeaderboardServer();
            server.TrySubmit(1, 10001, 1200);
            server.TrySubmit(1, 10002, 1500);
            server.TrySubmit(1, 10003, 1200);
            server.TrySubmit(1, 10004, 900);

            var pager = new LeaderboardPager(server);
            var request = new LeaderboardPageRequest(1, LeaderboardSeason.Current, offset: 0, limit: 2, playerId: 10003);
            LeaderboardPage page = pager.Fetch(in request);
            LeaderboardPage self = pager.FetchAroundSelf(1, 10003, radius: 1);

            _writer.WriteLine("[Leaderboard] 总数=" + page.TotalCount + " 本页=" + page.Rows.Length
                + " 首位=" + (page.Rows.Length > 0 ? page.Rows[0].PlayerId + "/" + page.Rows[0].Score : "空")
                + " 我的名次=" + (page.SelfRow.HasValue ? page.SelfRow.Value.Rank : 0)
                + " 自己页=" + self.Rows.Length);
        }

        /// <summary>掉单对账接线：拿一批"商店已成交"的收据对账，Missing 的补发一次。</summary>
        private void WireReconcile()
        {
            var analytics = new ConsoleAnalyticsService(new System.IO.StringWriter());
            var flow = new ConsoleStorePurchaseFlow(_bus, analytics);
            var reconciler = new PurchaseReconciler(new IapPurchaseLedgerAdapter(flow.Ledger), _bus);

            var receipts = new[]
            {
                new PurchaseReceipt("console-receipt-gold60", ConsoleIapService.ProductGold60,
                    ConsoleIapService.GoldItemId, 60, 0, "granted"),
                new PurchaseReceipt("console-receipt-no-ads", ConsoleIapService.ProductNoAds,
                    2001, 1, 0, "refunded"),
            };

            ReconcileAction[] actions = reconciler.Reconcile(receipts);
            int granted = 0;
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i].State == ReconcileState.Missing && reconciler.TryGrantMissing(actions[i].ReceiptId))
                {
                    granted++;
                }
            }

            _writer.WriteLine("[Reconcile] 动作=" + actions.Length + " 补发=" + granted
                + " 掉单=" + reconciler.MissingCount + " 已发=" + reconciler.GrantedCount
                + " 退款=" + reconciler.RefundedCount);
        }

        /// <summary>错误上报接线：聚合去重 → 批量上传（假通道），致命错误立即上报。</summary>
        private void WireErrorReport()
        {
            var uploader = new ConsoleErrorUploader(_writer);
            var reporter = new ErrorReportAggregator(_time, _bus, uploader);

            reporter.Report(ErrorReportLevel.Warning, "network.timeout", "请求超时");
            reporter.Report(ErrorReportLevel.Warning, "network.timeout", "请求超时");
            reporter.Report(ErrorReportLevel.Error, "save.corrupt", "存档校验失败");
            bool flushed = reporter.TryFlush();

            _writer.WriteLine("[ErrorReport] 待发=" + reporter.PendingCount
                + " 上报次数=" + reporter.ReportedCount
                + " 立即上报=" + flushed
                + " 成功批=" + uploader.Uploaded.Count);
        }

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
            _guide = guide;

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
        /// GM 控制台演示：把已有能力（发货 / 统计 / 引导跳过）包成 GM 命令，用合法权限跑 3 条，
        /// 再用 Player 权限跑 1 条 Admin 命令（必须被拒且留痕），最后打印审计条数与首条记录。
        /// <para>审计是 GM 的追责底线：越权尝试也要有一条 <c>Success=false</c> 的记录。</para>
        /// </summary>
        private void WireGm()
        {
            var hooks = new GmConsoleHooks
            {
                ItemCountById = itemId => _inventory.GetCount(itemId),
                GuideStepId = () => QueryGuide().StepId,
                GuideCompletedCount = () => QueryGuide().CompletedCount,
                GuideTotalCount = () => QueryGuide().TotalCount,
                AdvanceTime = seconds => _time.Advance(seconds),
                ExtraStats = () => "items=" + DescribeInventory(),
            };

            var adapter = new GmConsoleAdapter(_bus, _time, hooks);
            IGmConsole gm = adapter.Console;

            GmCommandResult give = gm.Execute("gm.give", new[] { "1001", "100" }, GmPermission.Admin, "console-operator");
            _writer.WriteLine("[GM] gm.give 1001 100 成功=" + give.Success + " 输出=" + give.Output);

            GmCommandResult stats = gm.Execute("gm.stats", Array.Empty<string>(), GmPermission.Player, "console-operator");
            _writer.WriteLine("[GM] gm.stats 成功=" + stats.Success + " 输出=" + stats.Output);

            GmCommandResult reset = gm.Execute("gm.reset.guide", Array.Empty<string>(), GmPermission.Support, "console-operator");
            _writer.WriteLine("[GM] gm.reset.guide 成功=" + reset.Success + " 输出=" + reset.Output);

            // 越权验证：同一个命令，Player 权限必须被拒（reason 固定为 permission denied）。
            GmCommandResult denied = gm.Execute("gm.give", new[] { "1001", "1" }, GmPermission.Player, "console-player");
            _writer.WriteLine("[GM] gm.give(Player) 成功=" + denied.Success + " 原因=" + denied.Reason);

            GmAuditEntry[] audit = gm.AuditLog;
            GmAuditEntry first = audit.Length > 0 ? audit[0] : default;
            _writer.WriteLine("[GM] 审计=" + audit.Length + " 条 首条=" + first.Command
                + " args=[" + first.Arguments + "] op=" + first.OperatorId
                + " 权限=" + first.Permission + " 成功=" + first.Success
                + " 时间=" + first.ExecutedAtUnixSeconds);
        }

        /// <summary>
        /// 端到端冒烟（T9-1）：把"登录 → 购买 → 战斗 → 结算 → 存档 → 对账"串成一条可断言场景并实跑。
        /// <para>
        /// 装配侧只做三件事：建一条商店闭环（它自带两份台账）、给存档开一个场景独占前缀
        /// （<c>e2e</c>，不碰业务槽位）、把已有依赖原样交给 <see cref="ConsoleE2eScenarios.MainFlow"/>。
        /// 断言与编排全部在场景里，这里不写任何业务判定。
        /// </para>
        /// <para>
        /// 失败只体现在报告里（<c>结论=失败</c>），<b>不打断后续冒烟步骤</b>：冒烟是"一次跑完看全貌"，
        /// 一条链路红灯不该让 GM/大厅等无关段落全部消失。
        /// </para>
        /// </summary>
        private void RunE2e()
        {
            var purchaseFlow = new ConsoleStorePurchaseFlow(
                _bus, new ConsoleAnalyticsService(new System.IO.StringWriter()));
            var save = new SaveService(_storage, "e2e");
            E2eScenario scenario = ConsoleE2eScenarios.MainFlow(_time, _bus, purchaseFlow, _inventory, _host, save);
            E2eReport report = new E2eRunner(_time).Run(in scenario);

            _writer.WriteLine(report.ToReportLine());
            for (int i = 0; i < report.Results.Length; i++)
            {
                E2eStepResult step = report.Results[i];
                string state = !step.Executed ? "未执行" : (step.Passed ? "通过" : "失败");
                _writer.WriteLine("  [E2E] " + (i + 1) + ". " + step.StepName + " → " + state
                    + " " + step.ElapsedMilliseconds + "ms"
                    + (step.Message.Length > 0 ? "（" + step.Message + "）" : string.Empty));
            }
        }

        /// <summary>引导快照查询（GM 统计与引导演示共用同一口径）。</summary>
        private GuideSnapshot QueryGuide()
        {
            return _bus.Query<GuideQueryRequest, GuideSnapshot>(new GuideQueryRequest());
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
