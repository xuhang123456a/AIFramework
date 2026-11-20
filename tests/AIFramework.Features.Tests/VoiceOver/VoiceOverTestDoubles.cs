#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Tests.Fakes;
using AIFramework.Features.VoiceOver;

namespace AIFramework.Features.Tests.VoiceOver
{
    /// <summary>一行语音夹具（字段顺序与 <c>tables/TbVoiceLine.json</c> 列顺序一致）。</summary>
    internal sealed class VoiceLineRowSpec
    {
        internal VoiceLineRowSpec(
            int lineId,
            string speaker,
            string textKey,
            string audioKey,
            int language,
            int startMs,
            int endMs)
        {
            LineId = lineId;
            Speaker = speaker;
            TextKey = textKey;
            AudioKey = audioKey;
            Language = language;
            StartMs = startMs;
            EndMs = endMs;
        }

        internal int LineId { get; }

        internal string Speaker { get; }

        internal string TextKey { get; }

        internal string AudioKey { get; }

        internal int Language { get; }

        internal int StartMs { get; }

        internal int EndMs { get; }

        /// <summary>
        /// 便捷构造：说话人 <c>speaker{lineId}</c>、文案键 <c>vo.line.{lineId}</c>；
        /// 需要断言具体字段的用例走构造函数显式给值。
        /// </summary>
        internal static VoiceLineRowSpec Row(int lineId, int language, int startMs, int endMs, string audioKey)
        {
            return new VoiceLineRowSpec(lineId, "speaker" + lineId, "vo.line." + lineId, audioKey, language, startMs, endMs);
        }
    }

    /// <summary>
    /// <c>TbVoiceLine</c> 字节构造器（布局见切片 <c>CONTRACT.md</c> §3）：
    /// 小端 <c>[int32 rowCount]</c> + 每行
    /// <c>{ int32 lineId; [int32 len]+UTF-8 speaker; …textKey; …audioKey; int32 language; int32 startMs; int32 endMs }</c>。
    /// 合法表走 <see cref="BuildPayload(VoiceLineRowSpec[])"/>；畸形表走 <see cref="BuildRaw"/> + <see cref="Truncate"/>。
    /// </summary>
    internal sealed class VoiceLineTableStub : IBinaryTableLoader
    {
        internal const string TableName = "TbVoiceLine";

        private const int Int32Bytes = 4;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        private VoiceLineTableStub(Dictionary<string, byte[]?> tables)
        {
            _tables = tables;
            _requestedTables = new List<string>();
        }

        /// <summary>被请求过的表名（按调用顺序），用于断言切片确实按名字取子表。</summary>
        internal IReadOnlyList<string> RequestedTables
        {
            get { return _requestedTables; }
        }

        /// <summary>一次都没成表的加载器：任何表都返回 <c>null</c>（表缺失场景）。</summary>
        internal static VoiceLineTableStub WithoutTable()
        {
            return new VoiceLineTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        }

        /// <summary>按给定行顺序造一张合法子表。</summary>
        internal static VoiceLineTableStub WithRows(params VoiceLineRowSpec[] rows)
        {
            return WithPayload(BuildPayload(rows));
        }

        /// <summary>直接挂一份字节（可用于空表 / 畸形字节 / 仓库真实 <c>.bin</c>）。</summary>
        internal static VoiceLineTableStub WithPayload(byte[]? payload)
        {
            var tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            tables[TableName] = payload;
            return new VoiceLineTableStub(tables);
        }

        internal static byte[] BuildPayload(VoiceLineRowSpec[] rows)
        {
            return BuildPayload(rows, rows.Length);
        }

        /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于截断/长度反例）。</summary>
        internal static byte[] BuildPayload(VoiceLineRowSpec[] rows, int declaredRowCount)
        {
            var bytes = new List<byte>(Int32Bytes * (1 + (rows.Length * 7)));
            AppendInt32(bytes, declaredRowCount);
            for (int i = 0; i < rows.Length; i++)
            {
                VoiceLineRowSpec row = rows[i];
                AppendInt32(bytes, row.LineId);
                AppendString(bytes, row.Speaker);
                AppendString(bytes, row.TextKey);
                AppendString(bytes, row.AudioKey);
                AppendInt32(bytes, row.Language);
                AppendInt32(bytes, row.StartMs);
                AppendInt32(bytes, row.EndMs);
            }

            return bytes.ToArray();
        }

        /// <summary>只写一串小端 int32（不写字符串体），用于手工构造"长度前缀本身畸形"的字节流。</summary>
        internal static byte[] BuildRaw(params int[] values)
        {
            var bytes = new List<byte>(values.Length * Int32Bytes);
            for (int i = 0; i < values.Length; i++)
            {
                AppendInt32(bytes, values[i]);
            }

            return bytes.ToArray();
        }

        /// <summary>截断到前 <paramref name="keptBytes"/> 字节。</summary>
        internal static byte[] Truncate(byte[] payload, int keptBytes)
        {
            if (keptBytes < 0 || keptBytes > payload.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(keptBytes), keptBytes, "截断长度必须落在 0..payload.Length。");
            }

            byte[] truncated = new byte[keptBytes];
            Array.Copy(payload, truncated, keptBytes);
            return truncated;
        }

        /// <summary>尾部追加 <paramref name="extraBytes"/> 个 0 字节，用于造"尾部残留"。</summary>
        internal static byte[] AppendPadding(byte[] payload, int extraBytes)
        {
            if (extraBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(extraBytes), extraBytes, "追加字节数不能为负。");
            }

            byte[] padded = new byte[payload.Length + extraBytes];
            Array.Copy(payload, padded, payload.Length);
            return padded;
        }

        /// <inheritdoc />
        public byte[]? Load(string tableName)
        {
            _requestedTables.Add(tableName);

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

        private static void AppendString(List<byte> target, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            AppendInt32(target, bytes.Length);
            target.AddRange(bytes);
        }
    }

    /// <summary>
    /// 播放事件记录器：同时订阅"开始"与"完成"，按**广播顺序**记录到一个列表里，
    /// 因此既能量条数，也能断言"Changed 一定先于 Completed"。
    /// </summary>
    internal sealed class VoiceLineEventRecorder
        : IEventHandler<VoiceLineChangedEvent>, IEventHandler<VoiceLineCompletedEvent>
    {
        private readonly List<object> _events;
        private readonly List<VoiceLineChangedEvent> _changed;
        private readonly List<VoiceLineCompletedEvent> _completed;

        internal VoiceLineEventRecorder()
        {
            _events = new List<object>();
            _changed = new List<VoiceLineChangedEvent>();
            _completed = new List<VoiceLineCompletedEvent>();
        }

        /// <summary>按广播顺序记录的全部事件（跨事件类型）。</summary>
        internal IReadOnlyList<object> Events
        {
            get { return _events; }
        }

        /// <summary>按广播顺序记录的开始事件。</summary>
        internal IReadOnlyList<VoiceLineChangedEvent> Changed
        {
            get { return _changed; }
        }

        /// <summary>按广播顺序记录的完成事件。</summary>
        internal IReadOnlyList<VoiceLineCompletedEvent> Completed
        {
            get { return _completed; }
        }

        /// <inheritdoc />
        public void Handle(in VoiceLineChangedEvent evt)
        {
            _events.Add(evt);
            _changed.Add(evt);
        }

        /// <inheritdoc />
        public void Handle(in VoiceLineCompletedEvent evt)
        {
            _events.Add(evt);
            _completed.Add(evt);
        }
    }

    /// <summary>
    /// 切片一次性装配：真实 <see cref="VoiceOverHandler"/> + 真实 <c>CqeBus</c>（经共享 <see cref="RecordingBus"/>
    /// 记录）+ 本目录私有桩端口；查询与命令默认走真实总线，验证 <c>Register</c> 真的接上了。
    /// </summary>
    internal sealed class VoiceOverTestHarness
    {
        private VoiceOverTestHarness(RecordingBus bus, VoiceLineEventRecorder events, VoiceOverHandler handler)
        {
            Bus = bus;
            Events = events;
            Handler = handler;
        }

        /// <summary>记录型总线（内部是真实 <c>CqeBus</c>）。</summary>
        internal RecordingBus Bus { get; }

        /// <summary>播放事件记录器。</summary>
        internal VoiceLineEventRecorder Events { get; }

        /// <summary>被测处理器。</summary>
        internal VoiceOverHandler Handler { get; }

        /// <summary>简体中文语种 + 给定行。</summary>
        internal static VoiceOverTestHarness Create(params VoiceLineRowSpec[] rows)
        {
            return Create(0, rows);
        }

        /// <summary>指定语种 + 给定行。</summary>
        internal static VoiceOverTestHarness Create(int language, params VoiceLineRowSpec[] rows)
        {
            return CreateWithPayload(language, VoiceLineTableStub.BuildPayload(rows));
        }

        /// <summary>指定语种 + 原始字节（空表 / 畸形表 / 仓库真实 .bin）。</summary>
        internal static VoiceOverTestHarness CreateWithPayload(int language, byte[]? payload)
        {
            return CreateWithLoader(language, VoiceLineTableStub.WithPayload(payload));
        }

        /// <summary>指定语种 + 自定义加载器。</summary>
        internal static VoiceOverTestHarness CreateWithLoader(int language, IBinaryTableLoader loader)
        {
            RecordingBus bus = new RecordingBus();
            VoiceLineEventRecorder events = new VoiceLineEventRecorder();
            bus.Subscribe<VoiceLineChangedEvent>(events);
            bus.Subscribe<VoiceLineCompletedEvent>(events);

            VoiceOverHandler handler = new VoiceOverHandler(bus, loader, language);
            handler.Register(bus);

            return new VoiceOverTestHarness(bus, events, handler);
        }

        /// <summary>走真实总线查快照。</summary>
        internal VoiceOverSnapshot Snapshot()
        {
            return Bus.Query<VoiceLineQueryRequest, VoiceOverSnapshot>(new VoiceLineQueryRequest());
        }

        /// <summary>走真实总线发播放命令。</summary>
        internal void Play(int lineId)
        {
            Bus.Send(new VoiceLinePlayCommand(lineId));
        }

        /// <summary>取快照里某条台词的状态（找不到即用例失败）。</summary>
        internal VoiceLineState Line(VoiceOverSnapshot snapshot, int lineId)
        {
            for (int i = 0; i < snapshot.Lines.Length; i++)
            {
                if (snapshot.Lines[i].LineId == lineId)
                {
                    return snapshot.Lines[i];
                }
            }

            throw new InvalidOperationException("快照里没有 lineId " + lineId + "。");
        }
    }
}
