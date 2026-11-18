using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Core.Cqe;
using AIFramework.Core.Gameplay;
using AIFramework.Features.DailySignIn;
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
        private readonly MockBinaryTableLoader _tables;
        private readonly MockInventory _inventory;
        private readonly MockSpatialWorld _spatial;
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
            _tables = new MockBinaryTableLoader();
            _inventory = new MockInventory();
            _spatial = new MockSpatialWorld();
            _innerBus = new CqeBus();

            int adapterTagId = _registry.Register("adapter.console");
            _bus = new LoggingCqeBus(_innerBus, _log, _registry, adapterTagId);

            _factory = new DemoKernelFactory(_spatial, DemoKernelFactory.DefaultPhases());
            _host = new GameplaySessionHost(_factory);
        }

        public void Run()
        {
            _writer.WriteLine("=== AIFramework Phase 1 冒烟 ===");

            // 固定时间基准，保证天序号与输出可复现；真机由服务器时间或系统时钟提供。
            _time.SetUtcNow(new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));

            WireInventory();
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
            int realtimeFrames = RunRealtimeSandbox();

            ExportCausalLog();
            RunStorageSmoke(signIn);
            _writer.WriteLine("Realtime 推进帧数=" + realtimeFrames + "，最终道具=" + DescribeInventory());
            _writer.WriteLine("=== 冒烟结束 ===");
        }

        private void WireInventory()
        {
            _innerBus.RegisterCommandHandler<GrantItemCommand>(_inventory);
            _innerBus.RegisterCommandHandler<ConsumeItemCommand>(_inventory);
            _innerBus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(
                new MockInventoryQueryHandler(_inventory));
            _inventory.Seed(9001, 0);
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
            IReadOnlyList<KeyValuePair<int, int>> snapshot = _inventory.Snapshot();
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

                builder.Append(snapshot[i].Key).Append('=').Append(snapshot[i].Value);
            }

            return builder.ToString();
        }

        private static string DescribeLoots(GameplayResult result)
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
