#nullable enable
using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 侧埋点实现：**不联网**，把事件收进定长环形缓冲，可选择追加写入本地文件。
    /// <para>
    /// 为什么默认 <see cref="IsAvailable"/> = false：埋点最容易变成"看起来在记、其实丢了一半"的功能。
    /// 没有接真实的采集后端之前，宁可明确报"不可用"，也不要在日志里制造"已经上报"的假象。
    /// 需要联调时用 <c>new UnityAnalyticsService(storage)</c> 打开本地落盘模式（按行文本，便于人工核对）。
    /// </para>
    /// <para>
    /// 真实接入（HTTP 批量上报、重试队列、离线缓存上限、隐私开关）属于适配层后续工作，
    /// 框架只保证 <see cref="IAnalyticsService"/> 的调用点语义不变。
    /// </para>
    /// </summary>
    public sealed class UnityAnalyticsService : IAnalyticsService
    {
        private readonly AnalyticsEventBuffer _buffer;
        private readonly IStoragePort? _storage;
        private readonly string _slotKey;
        private readonly bool _available;
        private readonly AnalyticsEvent[] _drainBuffer = new AnalyticsEvent[16];

        public UnityAnalyticsService(
            IStoragePort? storage = null,
            string slotKey = "analytics/events.log",
            int capacity = AnalyticsEventBuffer.DefaultCapacity,
            bool available = false)
        {
            _storage = storage;
            _slotKey = slotKey;
            _buffer = new AnalyticsEventBuffer(capacity);
            _available = available && storage != null;
        }

        public bool IsAvailable
        {
            get { return _available; }
        }

        public long AcceptedCount
        {
            get { return _buffer.AcceptedCount; }
        }

        public long DroppedCount
        {
            get { return _buffer.DroppedCount + _buffer.OverwrittenCount; }
        }

        public int BufferedCount
        {
            get { return _buffer.Count; }
        }

        public void Track(in AnalyticsEvent analyticsEvent)
        {
            if (!_available)
            {
                return;
            }

            _buffer.TryTrack(in analyticsEvent);
        }

        public void Flush()
        {
            IStoragePort? storage = _storage;
            if (!_available || storage == null || _buffer.Count == 0)
            {
                return;
            }

            var builder = new System.Text.StringBuilder(_buffer.Count * 48);
            Span<AnalyticsEvent> drained = _drainBuffer;
            while (_buffer.Count > 0)
            {
                int taken = _buffer.Drain(drained);
                for (int i = 0; i < taken; i++)
                {
                    AppendLine(builder, in drained[i]);
                }
            }

            // 追加写：读旧内容 + 拼新行（IStoragePort 只有整块字节读写，没有 append 语义）。
            string payload = builder.ToString();
            byte[]? previous = storage.Read(_slotKey);
            byte[] data;
            if (previous == null || previous.Length == 0)
            {
                data = System.Text.Encoding.UTF8.GetBytes(payload);
            }
            else
            {
                byte[] appended = System.Text.Encoding.UTF8.GetBytes(payload);
                data = new byte[previous.Length + appended.Length];
                Buffer.BlockCopy(previous, 0, data, 0, previous.Length);
                Buffer.BlockCopy(appended, 0, data, previous.Length, appended.Length);
            }

            storage.Write(_slotKey, data);
        }

        private static void AppendLine(System.Text.StringBuilder builder, in AnalyticsEvent analyticsEvent)
        {
            builder.Append(analyticsEvent.EventId);
            builder.Append('|');
            builder.Append(analyticsEvent.Value0.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(analyticsEvent.Value1.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(analyticsEvent.Value2.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('|');
            if (analyticsEvent.Label != null)
            {
                builder.Append(analyticsEvent.Label);
            }

            builder.Append('\n');
        }
    }
}
