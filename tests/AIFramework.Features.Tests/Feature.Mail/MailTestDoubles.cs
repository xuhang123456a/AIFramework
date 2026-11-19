using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.Mail;

namespace AIFramework.Features.Tests.Mail
{
    /// <summary>
    /// 邮件子表夹具：默认三封邮件，<b>物理行顺序故意不是 mailId 升序</b>，
    /// 用来验证查询结果依赖排序而不是依赖表行序。
    /// </summary>
    internal static class MailFixture
    {
        /// <summary>子表名（与切片内常量一致）。</summary>
        public const string TableName = "TbMail";

        /// <summary>单行字节数。</summary>
        public const int RowBytes = 16;

        /// <summary>头部字节数。</summary>
        public const int HeaderBytes = 4;

        /// <summary>号最小的邮件：1 天前发（最旧，排序后第一位）。</summary>
        public const int OldestMailId = 1001;

        public const int OldestRewardItemId = 8201;

        public const int OldestRewardCount = 1;

        /// <summary>今天发的邮件：0 天偏移（有效期的基准行）。</summary>
        public const int TodayMailId = 1002;

        public const int TodayRewardItemId = 8202;

        public const int TodayRewardCount = 2;

        /// <summary>2 天前发的邮件。</summary>
        public const int RecentMailId = 1003;

        public const int RecentRewardItemId = 8203;

        public const int RecentRewardCount = 3;

        /// <summary>默认夹具的物理行：1002 → 1001 → 1003（故意乱序）。</summary>
        public static byte[] DefaultPayload()
        {
            MailPayloadBuilder builder = new MailPayloadBuilder();
            builder.Add(TodayMailId, TodayRewardItemId, TodayRewardCount, 0);
            builder.Add(OldestMailId, OldestRewardItemId, OldestRewardCount, 1);
            builder.Add(RecentMailId, RecentRewardItemId, RecentRewardCount, 2);
            return builder.Build();
        }
    }

    /// <summary>小端子表字节构造器：<c>[int32 rowCount]</c> + <c>rowCount × 16 字节</c>。</summary>
    internal sealed class MailPayloadBuilder
    {
        private readonly List<MailDef> _rows = new List<MailDef>();

        public MailPayloadBuilder Add(int mailId, int rewardItemId, int rewardCount, int sendDayOffset)
        {
            _rows.Add(new MailDef(mailId, rewardItemId, rewardCount, sendDayOffset));
            return this;
        }

        /// <summary>头部行数与实际写入行数一致。</summary>
        public byte[] Build()
        {
            return BuildDeclaringRows(_rows.Count);
        }

        /// <summary>头部声明 <paramref name="declaredRowCount"/> 行，实际仍写满所有已添加行（制造长度不符）。</summary>
        public byte[] BuildDeclaringRows(int declaredRowCount)
        {
            byte[] payload = new byte[MailFixture.HeaderBytes + (_rows.Count * MailFixture.RowBytes)];
            WriteInt32LittleEndian(payload, 0, declaredRowCount);
            for (int i = 0; i < _rows.Count; i++)
            {
                MailDef row = _rows[i];
                int offset = MailFixture.HeaderBytes + (i * MailFixture.RowBytes);
                WriteInt32LittleEndian(payload, offset, row.MailId);
                WriteInt32LittleEndian(payload, offset + 4, row.RewardItemId);
                WriteInt32LittleEndian(payload, offset + 8, row.RewardCount);
                WriteInt32LittleEndian(payload, offset + 12, row.SendDayOffset);
            }

            return payload;
        }

        private static void WriteInt32LittleEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }

    /// <summary>二进制表加载桩：按表名返回夹具字节，未登记的表返回 <c>null</c>（表缺失场景）。</summary>
    internal sealed class FakeMailTableLoader : IBinaryTableLoader
    {
        private readonly Dictionary<string, byte[]?> _tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        private readonly List<string> _requestedTables = new List<string>();

        /// <summary>一张表都没有的加载器。</summary>
        public static FakeMailTableLoader WithoutTables()
        {
            return new FakeMailTableLoader();
        }

        /// <summary>标准三封邮件夹具。</summary>
        public static FakeMailTableLoader WithDefaultMailTable()
        {
            return new FakeMailTableLoader().AddTable(MailFixture.TableName, MailFixture.DefaultPayload());
        }

        /// <summary>登记（或覆盖）一张表；<paramref name="payload"/> 传 null 表示该表缺失。</summary>
        public FakeMailTableLoader AddTable(string tableName, byte[]? payload)
        {
            if (tableName == null)
            {
                throw new ArgumentNullException(nameof(tableName));
            }

            _tables[tableName] = payload;
            return this;
        }

        /// <summary>被请求过的表名（按调用顺序）。</summary>
        public IReadOnlyList<string> RequestedTables
        {
            get { return _requestedTables; }
        }

        public byte[]? Load(string tableName)
        {
            _requestedTables.Add(tableName);

            byte[]? payload;
            if (_tables.TryGetValue(tableName, out payload))
            {
                return payload;
            }

            return null;
        }
    }

    /// <summary>本目录自带的假时间源（不改共享 Fakes/）：<see cref="AdvanceDays"/> 同时推进墙钟与秒数。</summary>
    internal sealed class FakeMailTimeProvider : ITimeProvider
    {
        public FakeMailTimeProvider(DateTime utcNow)
        {
            UtcNow = utcNow.Kind == DateTimeKind.Utc
                ? utcNow
                : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            ElapsedSeconds = 0d;
        }

        public DateTime UtcNow { get; private set; }

        public double ElapsedSeconds { get; private set; }

        public void AdvanceDays(int days)
        {
            if (days < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(days), days, "时间只能向前推进。");
            }

            ElapsedSeconds += days * 86400d;
            UtcNow = UtcNow.AddDays(days);
        }
    }

    /// <summary>命令记录桩：挂到真实总线槽位上，记录切片实际发出的指令（含顺序）。</summary>
    internal sealed class RecordingCommandHandler<TCommand> : ICommandHandler<TCommand>
        where TCommand : struct, ICommand
    {
        private readonly List<TCommand> _items;
        private readonly List<object> _journal;

        public RecordingCommandHandler(List<object> journal)
        {
            _items = new List<TCommand>();
            _journal = journal;
        }

        public IReadOnlyList<TCommand> Items
        {
            get { return _items; }
        }

        public int Count
        {
            get { return _items.Count; }
        }

        public void Handle(in TCommand command)
        {
            _items.Add(command);
            _journal.Add(command);
        }
    }

    /// <summary>事件记录桩：订阅真实总线，记录切片广播的事实（含顺序）。</summary>
    internal sealed class RecordingEventHandler<TEvent> : IEventHandler<TEvent>
        where TEvent : struct, IEvent
    {
        private readonly List<TEvent> _items;
        private readonly List<object> _journal;

        public RecordingEventHandler(List<object> journal)
        {
            _items = new List<TEvent>();
            _journal = journal;
        }

        public IReadOnlyList<TEvent> Items
        {
            get { return _items; }
        }

        public int Count
        {
            get { return _items.Count; }
        }

        public void Handle(in TEvent evt)
        {
            _items.Add(evt);
            _journal.Add(evt);
        }
    }

    /// <summary>
    /// 邮件切片的一次性装配：真实 <see cref="MailHandler"/> + 真实 <see cref="CqeBus"/>
    /// + 假时间源 + 表加载桩 + 发奖/红点记录桩。命令与事件一律经总线往返，不直接调用处理器。
    /// </summary>
    internal sealed class MailHarness
    {
        private MailHarness(
            FakeMailTimeProvider time,
            CqeBus bus,
            RecordingCommandHandler<GrantItemCommand> grants,
            RecordingCommandHandler<RedDotDeltaCommand> redDots,
            RecordingEventHandler<MailClaimedEvent> claimedEvents,
            List<object> journal,
            MailHandler handler)
        {
            Time = time;
            Bus = bus;
            Grants = grants;
            RedDots = redDots;
            ClaimedEvents = claimedEvents;
            Journal = journal;
            Handler = handler;
        }

        public FakeMailTimeProvider Time { get; }

        /// <summary>真实总线（不是 RecordingBus 装饰器）。</summary>
        public CqeBus Bus { get; }

        public RecordingCommandHandler<GrantItemCommand> Grants { get; }

        public RecordingCommandHandler<RedDotDeltaCommand> RedDots { get; }

        public RecordingEventHandler<MailClaimedEvent> ClaimedEvents { get; }

        /// <summary>命令与事件的统一时序日志，用于断言"先发奖、再广播、最后红点"。</summary>
        public List<object> Journal { get; }

        public MailHandler Handler { get; }

        /// <summary>按给定 UTC 起点与子表字节装配；子表缺失时构造阶段就会抛异常。</summary>
        public static MailHarness Create(DateTime startUtc, IBinaryTableLoader loader)
        {
            FakeMailTimeProvider time = new FakeMailTimeProvider(startUtc);
            CqeBus bus = new CqeBus();
            List<object> journal = new List<object>();
            RecordingCommandHandler<GrantItemCommand> grants = new RecordingCommandHandler<GrantItemCommand>(journal);
            RecordingCommandHandler<RedDotDeltaCommand> redDots = new RecordingCommandHandler<RedDotDeltaCommand>(journal);
            RecordingEventHandler<MailClaimedEvent> claimedEvents = new RecordingEventHandler<MailClaimedEvent>(journal);

            bus.RegisterCommandHandler<GrantItemCommand>(grants);
            bus.RegisterCommandHandler<RedDotDeltaCommand>(redDots);
            bus.Subscribe<MailClaimedEvent>(claimedEvents);

            MailHandler handler = new MailHandler(bus, time, loader);
            handler.Register(bus);

            return new MailHarness(time, bus, grants, redDots, claimedEvents, journal, handler);
        }

        /// <summary>标准三封邮件夹具。</summary>
        public static MailHarness CreateWithDefaultTable(DateTime startUtc)
        {
            return Create(startUtc, FakeMailTableLoader.WithDefaultMailTable());
        }

        public void SeedRedDot()
        {
            Handler.SeedRedDot(Bus);
        }

        public void SendClaim(int mailId)
        {
            Bus.Send(new ClaimMailCommand(mailId));
        }

        public void SendDelete(int mailId)
        {
            Bus.Send(new DeleteMailCommand(mailId));
        }

        public MailListSnapshot QueryList()
        {
            return Bus.Query<MailListQueryRequest, MailListSnapshot>(new MailListQueryRequest());
        }
    }
}
