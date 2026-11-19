using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.Achievement;

namespace AIFramework.Features.Tests.Achievement
{
    /// <summary>一条成就行的夹具定义（字段顺序与 <c>tables/TbAchievement.json</c> 列顺序一致）。</summary>
    internal readonly struct AchievementRowSpec
    {
        internal AchievementRowSpec(
            int achievementId,
            string name,
            int metric,
            int target,
            int rewardItemId,
            int rewardCount)
        {
            AchievementId = achievementId;
            Name = name;
            Metric = metric;
            Target = target;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        internal int AchievementId { get; }

        internal string Name { get; }

        internal int Metric { get; }

        internal int Target { get; }

        internal int RewardItemId { get; }

        internal int RewardCount { get; }
    }

    /// <summary>
    /// 成就子表加载桩：严格按 <c>tools/tablegen</c> 的布局造字节
    /// （小端 <c>[int32 rowCount]</c> + 每行 <c>id / [len]UTF-8 name / metric / target / rewardItemId / rewardCount</c>），
    /// 只在本测试目录内使用，不改共享 <c>Fakes/</c>。
    /// </summary>
    internal sealed class AchievementTableStub : IBinaryTableLoader
    {
        internal const string TableName = "TbAchievement";

        private readonly Dictionary<string, byte[]?> _tables;

        private AchievementTableStub(Dictionary<string, byte[]?> tables)
        {
            _tables = tables;
        }

        /// <summary>一次都没成表的加载器：任何表都返回 <c>null</c>（表缺失场景）。</summary>
        internal static AchievementTableStub WithoutTable()
        {
            return new AchievementTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        }

        /// <summary>按给定行顺序造一张合法子表。</summary>
        internal static AchievementTableStub WithRows(params AchievementRowSpec[] rows)
        {
            return WithPayload(BuildPayload(rows));
        }

        /// <summary>直接挂一份字节（可用于空表 / 畸形字节 / 仓库真实 .bin）。</summary>
        internal static AchievementTableStub WithPayload(byte[]? payload)
        {
            var tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            tables[TableName] = payload;
            return new AchievementTableStub(tables);
        }

        internal static byte[] BuildPayload(AchievementRowSpec[] rows)
        {
            return BuildPayload(rows, rows.Length);
        }

        /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于长度/截断反例）。</summary>
        internal static byte[] BuildPayload(AchievementRowSpec[] rows, int declaredRowCount)
        {
            var bytes = new List<byte>(4 + (rows.Length * 32));
            AppendInt32(bytes, declaredRowCount);
            for (int i = 0; i < rows.Length; i++)
            {
                AppendInt32(bytes, rows[i].AchievementId);
                AppendUtf8(bytes, rows[i].Name);
                AppendInt32(bytes, rows[i].Metric);
                AppendInt32(bytes, rows[i].Target);
                AppendInt32(bytes, rows[i].RewardItemId);
                AppendInt32(bytes, rows[i].RewardCount);
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

        private static void AppendUtf8(List<byte> target, string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            AppendInt32(target, raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                target.Add(raw[i]);
            }
        }
    }

    /// <summary>按发生顺序记录命令与事件的消息流水，用于断言"切片真实发出了什么、顺序如何"。</summary>
    internal sealed class AchievementMessageRecorder
    {
        private readonly List<object> _entries;

        internal AchievementMessageRecorder()
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
    internal sealed class AchievementCommandSpy<TCommand> : ICommandHandler<TCommand>
        where TCommand : struct, ICommand
    {
        private readonly AchievementMessageRecorder _recorder;

        internal AchievementCommandSpy(AchievementMessageRecorder recorder)
        {
            _recorder = recorder;
        }

        public void Handle(in TCommand command)
        {
            _recorder.Record(command);
        }
    }

    /// <summary>事件桩：走真实 <see cref="CqeBus"/> 派发，只记录。</summary>
    internal sealed class AchievementEventSpy<TEvent> : IEventHandler<TEvent>
        where TEvent : struct, IEvent
    {
        private readonly AchievementMessageRecorder _recorder;

        internal AchievementEventSpy(AchievementMessageRecorder recorder)
        {
            _recorder = recorder;
        }

        public void Handle(in TEvent evt)
        {
            _recorder.Record(evt);
        }
    }

    /// <summary>装配：真实 <see cref="CqeBus"/> + 记录桩 + 切片 <see cref="AchievementHandler"/>。</summary>
    internal sealed class AchievementHarness
    {
        private readonly AchievementMessageRecorder _messages;

        private AchievementHarness(CqeBus bus, AchievementMessageRecorder messages, AchievementHandler handler)
        {
            Bus = bus;
            _messages = messages;
            Handler = handler;
        }

        internal CqeBus Bus { get; }

        internal AchievementHandler Handler { get; }

        internal IReadOnlyList<object> Messages
        {
            get { return _messages.Entries; }
        }

        internal static AchievementHarness Create(AchievementTableStub loader)
        {
            var bus = new CqeBus();
            var messages = new AchievementMessageRecorder();

            // 切片解锁时会发 GrantItemCommand，真实总线要求该命令必须有处理器；桩在这里兼作接收端。
            bus.RegisterCommandHandler<GrantItemCommand>(new AchievementCommandSpy<GrantItemCommand>(messages));
            bus.Subscribe<AchievementUnlockedEvent>(new AchievementEventSpy<AchievementUnlockedEvent>(messages));

            var handler = new AchievementHandler(bus, loader);
            handler.Register(bus);
            return new AchievementHarness(bus, messages, handler);
        }

        internal void Record(int metric, int amount)
        {
            Bus.Send(new AchievementRecordCommand(metric, amount));
        }

        internal AchievementSnapshot Query()
        {
            return Bus.Query<AchievementQueryRequest, AchievementSnapshot>(new AchievementQueryRequest());
        }

        internal List<T> MessagesOf<T>()
            where T : struct
        {
            return _messages.Of<T>();
        }
    }
}
