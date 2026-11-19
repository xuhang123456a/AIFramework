using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台埋点实现：把事件收进定长环形缓冲，<see cref="Flush"/> 时映射成可读行打印。
    /// <para>
    /// 它同时是"事件号 → 可读名"的唯一映射点：真实项目里这张表属于后台看板，
    /// 客户端只在离线调试/回放时用；因此映射表放在适配层而不是 Contracts。
    /// </para>
    /// </summary>
    public sealed class ConsoleAnalyticsService : IAnalyticsService
    {
        private readonly AnalyticsEventBuffer _buffer;
        private readonly List<string> _flushed = new List<string>(64);
        private readonly AnalyticsEvent[] _drainBuffer = new AnalyticsEvent[64];
        private readonly System.IO.TextWriter _writer;
        private readonly bool _available;

        public ConsoleAnalyticsService(
            System.IO.TextWriter? writer = null,
            int capacity = AnalyticsEventBuffer.DefaultCapacity,
            bool available = true)
        {
            _writer = writer ?? System.Console.Out;
            _buffer = new AnalyticsEventBuffer(capacity);
            _available = available;
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

        /// <summary>缓冲满时被挤掉的条数（诊断：看板缺口的来源）。</summary>
        public long OverwrittenCount
        {
            get { return _buffer.OverwrittenCount; }
        }

        /// <summary>当前缓存条数。</summary>
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
            if (_buffer.Count == 0)
            {
                return;
            }

            Span<AnalyticsEvent> drained = _drainBuffer;
            while (_buffer.Count > 0)
            {
                int taken = _buffer.Drain(drained);
                for (int i = 0; i < taken; i++)
                {
                    string line = Format(in drained[i]);
                    _flushed.Add(line);
                    _writer.WriteLine("[Track] " + line);
                }
            }
        }

        /// <summary>取走自上次调用以来的已打印行（测试/冒烟断言用）。</summary>
        public IReadOnlyList<string> DrainFlushed()
        {
            var copy = _flushed.ToArray();
            _flushed.Clear();
            return copy;
        }

        /// <summary>事件号到可读名的映射；未知号保持 <c>#号</c>，不抛。</summary>
        public static string NameOf(int eventId)
        {
            switch (eventId)
            {
                case AnalyticsEventIds.AppLaunch: return "app_launch";
                case AnalyticsEventIds.SceneEnter: return "scene_enter";
                case AnalyticsEventIds.BattleStart: return "battle_start";
                case AnalyticsEventIds.BattleEnd: return "battle_end";
                case AnalyticsEventIds.ItemGranted: return "item_granted";
                case AnalyticsEventIds.Purchase: return "purchase";
                case AnalyticsEventIds.Gacha: return "gacha";
                case AnalyticsEventIds.AdCompleted: return "ad_completed";
                case AnalyticsEventIds.IapPurchased: return "iap_purchased";
                default: return "#" + eventId;
            }
        }

        private static string Format(in AnalyticsEvent analyticsEvent)
        {
            var builder = new StringBuilder(64);
            builder.Append(NameOf(analyticsEvent.EventId));
            builder.Append(" v0=").Append(analyticsEvent.Value0.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" v1=").Append(analyticsEvent.Value1.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(" v2=").Append(analyticsEvent.Value2.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            if (analyticsEvent.Label != null)
            {
                builder.Append(" label=").Append(analyticsEvent.Label);
            }

            return builder.ToString();
        }
    }
}
