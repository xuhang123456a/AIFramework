using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.SeasonPass;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.SeasonPass
{
    /// <summary>一条通行证行的夹具定义（字段顺序与 <c>tables/TbSeasonPass.json</c> 列顺序一致）。</summary>
    internal readonly struct SeasonPassRowSpec
    {
        internal SeasonPassRowSpec(
            int level,
            int requiredXp,
            int freeItemId,
            int freeCount,
            int paidItemId,
            int paidCount)
        {
            Level = level;
            RequiredXp = requiredXp;
            FreeItemId = freeItemId;
            FreeCount = freeCount;
            PaidItemId = paidItemId;
            PaidCount = paidCount;
        }

        internal int Level { get; }

        internal int RequiredXp { get; }

        internal int FreeItemId { get; }

        internal int FreeCount { get; }

        internal int PaidItemId { get; }

        internal int PaidCount { get; }
    }

    /// <summary>
    /// 通行证子表加载桩：严格按 <c>tools/tablegen</c> 的布局造字节
    /// （小端 <c>[int32 rowCount]</c> + 每行 6 个 int32，单行 24 字节），只在本测试目录内使用，不改共享 <c>Fakes/</c>。
    /// </summary>
    internal sealed class SeasonPassTableStub : IBinaryTableLoader
    {
        internal const string TableName = "TbSeasonPass";

        private const int HeaderBytes = 4;
        private const int RowBytes = 24;

        private readonly Dictionary<string, byte[]?> _tables;

        private SeasonPassTableStub(Dictionary<string, byte[]?> tables)
        {
            _tables = tables;
        }

        /// <summary>一次都没成表的加载器：任何表都返回 <c>null</c>（表缺失场景）。</summary>
        internal static SeasonPassTableStub WithoutTable()
        {
            return new SeasonPassTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        }

        /// <summary>按给定行顺序造一张合法子表。</summary>
        internal static SeasonPassTableStub WithRows(params SeasonPassRowSpec[] rows)
        {
            return WithPayload(BuildPayload(rows));
        }

        /// <summary>直接挂一份字节（可用于空表 / 畸形字节 / 仓库真实 <c>.bin</c>）。</summary>
        internal static SeasonPassTableStub WithPayload(byte[]? payload)
        {
            var tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            tables[TableName] = payload;
            return new SeasonPassTableStub(tables);
        }

        /// <summary>
        /// 标准 5 级夹具：等级门槛 0/100/250/450/700；免费轨每级 7501（数量 10 起每级 +10）；
        /// 付费轨只在 1/3/5 级有奖励（2/4 级付费列整对为 0，覆盖"无付费奖励"分支）。
        /// </summary>
        internal static SeasonPassRowSpec[] StandardRows()
        {
            return new[]
            {
                new SeasonPassRowSpec(1, 0, 7501, 10, 7502, 1),
                new SeasonPassRowSpec(2, 100, 7501, 20, 0, 0),
                new SeasonPassRowSpec(3, 250, 7501, 30, 7502, 1),
                new SeasonPassRowSpec(4, 450, 7501, 40, 0, 0),
                new SeasonPassRowSpec(5, 700, 7501, 50, 7502, 1),
            };
        }

        internal static SeasonPassTableStub WithStandardRows()
        {
            return WithRows(StandardRows());
        }

        internal static byte[] BuildPayload(SeasonPassRowSpec[] rows)
        {
            return BuildPayload(rows, rows.Length);
        }

        /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于长度/截断反例）。</summary>
        internal static byte[] BuildPayload(SeasonPassRowSpec[] rows, int declaredRowCount)
        {
            var bytes = new List<byte>(HeaderBytes + (rows.Length * RowBytes));
            AppendInt32(bytes, declaredRowCount);
            for (int i = 0; i < rows.Length; i++)
            {
                AppendInt32(bytes, rows[i].Level);
                AppendInt32(bytes, rows[i].RequiredXp);
                AppendInt32(bytes, rows[i].FreeItemId);
                AppendInt32(bytes, rows[i].FreeCount);
                AppendInt32(bytes, rows[i].PaidItemId);
                AppendInt32(bytes, rows[i].PaidCount);
            }

            return bytes.ToArray();
        }

        /// <summary>在合法字节尾部追加垃圾字节（模拟"声明行数与字节流不符"的尾部残留）。</summary>
        internal static byte[] AppendTrailingBytes(byte[] payload, int count)
        {
            var bytes = new List<byte>(payload.Length + count);
            bytes.AddRange(payload);
            for (int i = 0; i < count; i++)
            {
                bytes.Add(0x7F);
            }

            return bytes.ToArray();
        }

        public byte[]? Load(string tableName)
        {
            byte[]? payload;
            return _tables.TryGetValue(tableName, out payload) ? payload : null;
        }

        private static void AppendInt32(List<byte> target, int value)
        {
            target.Add((byte)(value & 0xFF));
            target.Add((byte)((value >> 8) & 0xFF));
            target.Add((byte)((value >> 16) & 0xFF));
            target.Add((byte)((value >> 24) & 0xFF));
        }
    }

    /// <summary>按发生顺序记录命令与事件的消息流水，用于断言"切片真实发出了什么、顺序如何"。</summary>
    internal sealed class SeasonPassMessageRecorder
    {
        private readonly List<object> _entries;

        internal SeasonPassMessageRecorder()
        {
            _entries = new List<object>();
        }

        internal IReadOnlyList<object> Entries
        {
            get { return _entries; }
        }

        internal void Record(object message)
        {
            _entries.Add(message);
        }

        internal List<T> Of<T>()
            where T : struct
        {
            var result = new List<T>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i] is T typed)
                {
                    result.Add(typed);
                }
            }

            return result;
        }
    }

    /// <summary>命令桩：走真实 <see cref="CqeBus"/> 派发，只记录不改状态。</summary>
    internal sealed class SeasonPassCommandSpy<TCommand> : ICommandHandler<TCommand>
        where TCommand : struct, ICommand
    {
        private readonly SeasonPassMessageRecorder _recorder;

        internal SeasonPassCommandSpy(SeasonPassMessageRecorder recorder)
        {
            _recorder = recorder;
        }

        public void Handle(in TCommand command)
        {
            _recorder.Record(command);
        }
    }

    /// <summary>事件桩：走真实 <see cref="CqeBus"/> 派发，只记录。</summary>
    internal sealed class SeasonPassEventSpy<TEvent> : IEventHandler<TEvent>
        where TEvent : struct, IEvent
    {
        private readonly SeasonPassMessageRecorder _recorder;

        internal SeasonPassEventSpy(SeasonPassMessageRecorder recorder)
        {
            _recorder = recorder;
        }

        public void Handle(in TEvent evt)
        {
            _recorder.Record(evt);
        }
    }

    /// <summary>
    /// 装配：真实 <see cref="CqeBus"/> + 可快进时间源 + 记录桩 + 切片 <see cref="SeasonPassHandler"/>。
    /// 默认时间锚点与赛季终点固定，保证剩余秒数断言可复现。
    /// </summary>
    internal sealed class SeasonPassHarness
    {
        /// <summary>默认时间锚点：2026-11-20T08:00:00Z。</summary>
        internal static readonly DateTime DefaultNow = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

        /// <summary>默认赛季时长：7 天（604800 秒）。</summary>
        internal const int DefaultSeasonSeconds = 7 * 86400;

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly SeasonPassMessageRecorder _messages;

        private SeasonPassHarness(
            CqeBus bus,
            SeasonPassHandler handler,
            FakeTimeProvider time,
            SeasonPassMessageRecorder messages)
        {
            Bus = bus;
            Handler = handler;
            Time = time;
            _messages = messages;
        }

        internal CqeBus Bus { get; }

        internal SeasonPassHandler Handler { get; }

        internal FakeTimeProvider Time { get; }

        internal IReadOnlyList<object> Messages
        {
            get { return _messages.Entries; }
        }

        /// <summary>默认锚点 + 7 天赛季。</summary>
        internal static SeasonPassHarness Create(SeasonPassTableStub loader)
        {
            return Create(loader, DefaultNow, DefaultNow.AddSeconds(DefaultSeasonSeconds));
        }

        /// <summary>指定时间锚点与赛季终点。</summary>
        internal static SeasonPassHarness Create(SeasonPassTableStub loader, DateTime now, DateTime seasonEnd)
        {
            var bus = new CqeBus();
            var messages = new SeasonPassMessageRecorder();
            var time = new FakeTimeProvider(now);

            // 切片领取时会发 GrantItemCommand，真实总线要求该命令必须有处理器；桩在这里兼作接收端。
            bus.RegisterCommandHandler<GrantItemCommand>(new SeasonPassCommandSpy<GrantItemCommand>(messages));
            bus.Subscribe<SeasonLevelUpEvent>(new SeasonPassEventSpy<SeasonLevelUpEvent>(messages));
            bus.Subscribe<SeasonRewardClaimedEvent>(new SeasonPassEventSpy<SeasonRewardClaimedEvent>(messages));

            var handler = new SeasonPassHandler(bus, time, loader, ToUnixSeconds(seasonEnd));
            handler.Register(bus);
            return new SeasonPassHarness(bus, handler, time, messages);
        }

        /// <summary>已过期的赛季：终点比锚点早 1 秒。</summary>
        internal static SeasonPassHarness CreateExpired(SeasonPassTableStub loader)
        {
            return Create(loader, DefaultNow, DefaultNow.AddSeconds(-1));
        }

        /// <summary>UTC 时刻 → Unix 秒（测试侧与切片内算法独立实现，避免"用被测代码算期望值"）。</summary>
        internal static long ToUnixSeconds(DateTime utc)
        {
            DateTime normalized = utc.Kind == DateTimeKind.Utc
                ? utc
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return (long)(normalized - UnixEpoch).TotalSeconds;
        }

        internal void AddXp(int amount)
        {
            Bus.Send(new AddSeasonXpCommand(amount));
        }

        internal void Claim(int level, bool paidTrack)
        {
            Bus.Send(new ClaimSeasonRewardCommand(level, paidTrack));
        }

        internal void UnlockPaid()
        {
            Bus.Send(new UnlockPaidTrackCommand());
        }

        internal SeasonPassSnapshot Query()
        {
            return Bus.Query<SeasonPassQueryRequest, SeasonPassSnapshot>(new SeasonPassQueryRequest());
        }

        internal List<T> MessagesOf<T>()
            where T : struct
        {
            return _messages.Of<T>();
        }

        internal int GrantCount()
        {
            return _messages.Of<GrantItemCommand>().Count;
        }

        internal int LevelUpCount()
        {
            return _messages.Of<SeasonLevelUpEvent>().Count;
        }

        internal int ClaimedEventCount()
        {
            return _messages.Of<SeasonRewardClaimedEvent>().Count;
        }
    }
}
