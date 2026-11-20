using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Ports;
using AIFramework.Features.ActivitySchedule;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.ActivitySchedule
{
    /// <summary>一条排期行的夹具定义（字段顺序与 <c>tables/TbActivitySchedule.json</c> 列顺序一致）。</summary>
    internal readonly struct ActivityScheduleRowSpec
    {
        internal ActivityScheduleRowSpec(
            int activityId,
            string name,
            int startUnixSeconds,
            int endUnixSeconds,
            int dailyResetHourUtc,
            int rotationGroup,
            int priority)
        {
            ActivityId = activityId;
            Name = name;
            StartUnixSeconds = startUnixSeconds;
            EndUnixSeconds = endUnixSeconds;
            DailyResetHourUtc = dailyResetHourUtc;
            RotationGroup = rotationGroup;
            Priority = priority;
        }

        internal int ActivityId { get; }

        internal string Name { get; }

        internal int StartUnixSeconds { get; }

        internal int EndUnixSeconds { get; }

        internal int DailyResetHourUtc { get; }

        internal int RotationGroup { get; }

        internal int Priority { get; }
    }

    /// <summary>
    /// 活动排期子表加载桩：严格按 <c>tools/tablegen</c> 的布局造字节
    /// （小端 <c>[int32 rowCount]</c> + 每行 <c>{ int32 activityId; [int32 len]+UTF-8 name; int32 × 5 }</c>），
    /// 只在本测试目录内使用，不改共享 <c>Fakes/</c>。
    /// </summary>
    internal sealed class ActivityScheduleTableStub : IBinaryTableLoader
    {
        internal const string TableName = "TbActivitySchedule";

        private const int HeaderBytes = 4;

        private readonly Dictionary<string, byte[]?> _tables;

        private ActivityScheduleTableStub(Dictionary<string, byte[]?> tables)
        {
            _tables = tables;
        }

        /// <summary>一次都没成表的加载器：任何表都返回 <c>null</c>（表缺失场景）。</summary>
        internal static ActivityScheduleTableStub WithoutTable()
        {
            return new ActivityScheduleTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        }

        /// <summary>按给定行顺序造一张合法子表。</summary>
        internal static ActivityScheduleTableStub WithRows(params ActivityScheduleRowSpec[] rows)
        {
            return WithPayload(BuildPayload(rows));
        }

        /// <summary>直接挂一份字节（可用于空表 / 畸形字节 / 仓库真实 <c>.bin</c>）。</summary>
        internal static ActivityScheduleTableStub WithPayload(byte[]? payload)
        {
            var tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            tables[TableName] = payload;
            return new ActivityScheduleTableStub(tables);
        }

        internal static byte[] BuildPayload(ActivityScheduleRowSpec[] rows)
        {
            return BuildPayload(rows, rows.Length);
        }

        /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于长度/截断反例）。</summary>
        internal static byte[] BuildPayload(ActivityScheduleRowSpec[] rows, int declaredRowCount)
        {
            var bytes = new List<byte>(HeaderBytes + (rows.Length * 48));
            AppendInt32(bytes, declaredRowCount);
            for (int i = 0; i < rows.Length; i++)
            {
                ActivityScheduleRowSpec row = rows[i];
                AppendInt32(bytes, row.ActivityId);
                byte[] name = Encoding.UTF8.GetBytes(row.Name);
                AppendInt32(bytes, name.Length);
                bytes.AddRange(name);
                AppendInt32(bytes, row.StartUnixSeconds);
                AppendInt32(bytes, row.EndUnixSeconds);
                AppendInt32(bytes, row.DailyResetHourUtc);
                AppendInt32(bytes, row.RotationGroup);
                AppendInt32(bytes, row.Priority);
            }

            return bytes.ToArray();
        }

        /// <summary>只写一串小端 int32（不写字符串体），用于手工构造"长度前缀本身畸形"的字节流。</summary>
        internal static byte[] BuildRaw(params int[] values)
        {
            var bytes = new List<byte>(values.Length * 4);
            for (int i = 0; i < values.Length; i++)
            {
                AppendInt32(bytes, values[i]);
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

    /// <summary>
    /// 装配：记录型总线（<see cref="RecordingBus"/>）+ 可快进时间源 + 切片
    /// <see cref="ActivityScheduleHandler"/>。默认时间锚点固定，保证所有窗口/跨日断言可复现。
    /// </summary>
    internal sealed class ActivityScheduleHarness
    {
        /// <summary>默认时间锚点：2026-11-20T08:00:00Z（UTC 08 点，默认已过 04:00 的重置整点）。</summary>
        internal static readonly DateTime DefaultNow = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private ActivityScheduleHarness(RecordingBus bus, ActivityScheduleHandler handler, FakeTimeProvider time)
        {
            Bus = bus;
            Handler = handler;
            Time = time;
        }

        internal RecordingBus Bus { get; }

        internal ActivityScheduleHandler Handler { get; }

        internal FakeTimeProvider Time { get; }

        internal static ActivityScheduleHarness Create(ActivityScheduleTableStub loader)
        {
            return Create(loader, DefaultNow);
        }

        internal static ActivityScheduleHarness Create(ActivityScheduleTableStub loader, DateTime now)
        {
            var bus = new RecordingBus();
            var time = new FakeTimeProvider(now);
            var handler = new ActivityScheduleHandler(bus, time, loader);
            handler.Register(bus);
            return new ActivityScheduleHarness(bus, handler, time);
        }

        /// <summary>UTC 时刻 → Unix 秒（测试侧独立实现，避免"用被测代码算期望值"）。</summary>
        internal static long ToUnixSeconds(DateTime utc)
        {
            DateTime normalized = utc.Kind == DateTimeKind.Utc
                ? utc
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return (long)(normalized - UnixEpoch).TotalSeconds;
        }

        /// <summary>UTC 时刻 → int32 Unix 秒（表内时间列是 int32，2038 年前够用）。</summary>
        internal static int ToUnixSecondsInt(DateTime utc)
        {
            return (int)ToUnixSeconds(utc);
        }

        /// <summary>锚点偏移 <paramref name="seconds"/> 秒后的 int32 Unix 秒。</summary>
        internal static int OffsetSeconds(int seconds)
        {
            return ToUnixSecondsInt(DefaultNow.AddSeconds(seconds));
        }

        internal void AdvanceSeconds(double seconds)
        {
            Time.AdvanceSeconds(seconds);
        }

        internal void Tick()
        {
            Bus.Send(new ActivityTickCommand());
        }

        internal ActivityScheduleSnapshot Query()
        {
            return Bus.Query<ActivityQueryRequest, ActivityScheduleSnapshot>(new ActivityQueryRequest());
        }

        internal ActivityScheduleSnapshot TickAndQuery()
        {
            Tick();
            return Query();
        }

        /// <summary>按 id 取快照里的活动状态；找不到直接抛（说明快照漏行）。</summary>
        internal static ActivityState StateOf(ActivityScheduleSnapshot snapshot, int activityId)
        {
            for (int i = 0; i < snapshot.Activities.Count; i++)
            {
                if (snapshot.Activities[i].ActivityId == activityId)
                {
                    return snapshot.Activities[i];
                }
            }

            throw new InvalidOperationException("快照里没有 activityId " + activityId + "。");
        }

        internal List<int> OpenedIds()
        {
            List<ActivityOpenedEvent> events = Bus.PublishedOf<ActivityOpenedEvent>();
            var ids = new List<int>(events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                ids.Add(events[i].ActivityId);
            }

            return ids;
        }

        internal List<int> ClosedIds()
        {
            List<ActivityClosedEvent> events = Bus.PublishedOf<ActivityClosedEvent>();
            var ids = new List<int>(events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                ids.Add(events[i].ActivityId);
            }

            return ids;
        }
    }
}
