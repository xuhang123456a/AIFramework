using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 错误聚合上报器：去重窗口内合并同类错误、达到阈值或致命错误时立即上报、失败保留重试。
    /// <para>
    /// <b>为什么必须聚合</b>：网络抖动一次重试失败就能产生几十条同码错误，逐条上报会把通道打满、
    /// 让真正的罕见错误淹没在噪音里。聚合后带 <see cref="ErrorReportEntry.RepeatCount"/>，服务端仍知道"发生了 N 次"。
    /// </para>
    /// <para>
    /// <b>为什么要去重窗口</b>：没有窗口时"第一次是 Error、第二次是 Warning"会被拆成两条；有窗口后
    /// 同码同摘要只保留首条并累加次数，级别取更严重者（升级不降级）。
    /// </para>
    /// <para>
    /// <b>为什么失败不丢</b>：上报失败（离线/通道不可用）时批次留在待发队列里，等下次 flush 再发；
    /// 队列有容量上限（<see cref="Capacity"/>），超出后丢最旧的 Warning（Fatal 永不丢）。
    /// </para>
    /// </summary>
    public sealed class ErrorReportAggregator : IErrorReporter
    {
        /// <summary>默认去重窗口（秒）：窗口内的同码同摘要合并计数。</summary>
        public const int DefaultDedupWindowSeconds = 60;

        /// <summary>默认容量：超过后挤出最旧的 Warning（Fatal/Error 优先保留）。</summary>
        public const int DefaultCapacity = 64;

        private readonly ICqeBus? _bus;
        private readonly IErrorUploader? _uploader;
        private readonly ITimeProvider _time;
        private readonly int _dedupWindowSeconds;
        private readonly List<MutableEntry> _pending;
        private long _attemptCount;
        private long _successCount;
        private long _droppedCount;

        /// <summary>构造聚合器。</summary>
        /// <param name="time">时间源（禁 DateTime.Now）。</param>
        /// <param name="bus">可选总线：每条错误广播 <see cref="ErrorReportedEvent"/>（降级面板用）。</param>
        /// <param name="uploader">可选上报通道；为 null 时只聚合不发送。</param>
        /// <param name="dedupWindowSeconds">去重窗口（秒，&lt;0 视为 0 = 不去重）。</param>
        /// <param name="capacity">待发队列容量（&lt;1 按 1 处理）。</param>
        public ErrorReportAggregator(
            ITimeProvider time,
            ICqeBus? bus = null,
            IErrorUploader? uploader = null,
            int dedupWindowSeconds = DefaultDedupWindowSeconds,
            int capacity = DefaultCapacity)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _bus = bus;
            _uploader = uploader;
            _dedupWindowSeconds = dedupWindowSeconds < 0 ? 0 : dedupWindowSeconds;
            Capacity = capacity < 1 ? 1 : capacity;
            _pending = new List<MutableEntry>(Capacity);
        }

        /// <summary>待发队列容量。</summary>
        public int Capacity { get; }

        /// <summary>待上报（去重后）条目数。</summary>
        public int PendingCount
        {
            get { return _pending.Count; }
        }

        /// <summary>累计上报次数（不论通道是否可用）。</summary>
        public int ReportedCount { get; private set; }

        /// <summary>累计尝试 flush 次数。</summary>
        public long AttemptCount
        {
            get { return _attemptCount; }
        }

        /// <summary>累计成功上报次数。</summary>
        public long SuccessCount
        {
            get { return _successCount; }
        }

        /// <summary>因容量上限被丢弃的条目数（只会丢 Warning）。</summary>
        public long DroppedCount
        {
            get { return _droppedCount; }
        }

        /// <summary>最近一次错误码（诊断用）。</summary>
        public string LastCode { get; private set; } = string.Empty;

        /// <summary>上报一条错误：去重合并 → 必要时挤出旧 Warning → 广播事件 → 致命错误立即尝试上报。</summary>
        /// <param name="level">级别。</param>
        /// <param name="code">稳定错误码；null/空按 <c>"unknown"</c>。</param>
        /// <param name="message">摘要。</param>
        public void Report(ErrorReportLevel level, string code, string message)
        {
            string normalizedCode = string.IsNullOrEmpty(code) ? "unknown" : code!;
            string normalizedMessage = message ?? string.Empty;
            long now = _time.ElapsedSeconds > long.MaxValue ? long.MaxValue : (long)_time.ElapsedSeconds;

            LastCode = normalizedCode;
            ReportedCount++;

            for (int i = 0; i < _pending.Count; i++)
            {
                MutableEntry existing = _pending[i];
                if (string.Equals(existing.Code, normalizedCode, StringComparison.Ordinal)
                    && string.Equals(existing.Message, normalizedMessage, StringComparison.Ordinal)
                    && now - existing.LastSeenUnixSeconds <= _dedupWindowSeconds)
                {
                    existing.RepeatCount++;
                    existing.LastSeenUnixSeconds = now;
                    if (level > existing.Level)
                    {
                        existing.Level = level;
                    }

                    _pending[i] = existing;
                    Broadcast(existing.ToEntry());
                    if (level == ErrorReportLevel.Fatal)
                    {
                        TryFlush();
                    }

                    return;
                }
            }

            var entry = new MutableEntry
            {
                Code = normalizedCode,
                Message = normalizedMessage,
                Level = level,
                FirstSeenUnixSeconds = now,
                LastSeenUnixSeconds = now,
                RepeatCount = 1,
            };

            EvictIfNeeded();
            _pending.Add(entry);
            Broadcast(entry.ToEntry());

            if (level == ErrorReportLevel.Fatal)
            {
                TryFlush();
            }
        }

        /// <summary>
        /// 尝试立即上报全部待发条目：通道不可用/失败时保留（不丢），成功才清空。
        /// </summary>
        /// <returns>真的发出返回 <c>true</c>；队列为空或未配置通道返回 <c>false</c>。</returns>
        public bool TryFlush()
        {
            _attemptCount++;

            IErrorUploader? uploader = _uploader;
            if (uploader == null || _pending.Count == 0)
            {
                return false;
            }

            if (!uploader.IsAvailable)
            {
                return false;
            }

            var entries = new ErrorReportEntry[_pending.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = _pending[i].ToEntry();
            }

            bool uploaded;
            try
            {
                var batch = new ErrorReportBatch(entries);
                uploaded = uploader.TryUpload(in batch);
            }
            catch (Exception)
            {
                // 上报通道自身异常绝不能再抛给业务（错误处理链路上的二次失败最致命）。
                uploaded = false;
            }

            if (!uploaded)
            {
                return false;
            }

            _pending.Clear();
            _successCount++;
            return true;
        }

        private void EvictIfNeeded()
        {
            while (_pending.Count >= Capacity)
            {
                int victim = FindOldestNonFatal();
                if (victim < 0)
                {
                    // 全是 Fatal：宁可超出容量也不丢致命错误（容量是软上限）。
                    return;
                }

                _pending.RemoveAt(victim);
                _droppedCount++;
            }
        }

        private int FindOldestNonFatal()
        {
            int victim = -1;
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Level == ErrorReportLevel.Fatal)
                {
                    continue;
                }

                if (victim < 0 || _pending[i].FirstSeenUnixSeconds < _pending[victim].FirstSeenUnixSeconds)
                {
                    victim = i;
                }
            }

            return victim;
        }

        private void Broadcast(in ErrorReportEntry entry)
        {
            ICqeBus? bus = _bus;
            if (bus == null)
            {
                return;
            }

            var evt = new ErrorReportedEvent(in entry);
            try
            {
                bus.Publish(in evt);
            }
            catch (Exception)
            {
                // 降级面板/订阅者自身出错不能反过来打死上报链路（错误处理链路上的二次失败最致命）。
            }
        }

        /// <summary>待发队列的可变条目（结构体 + 列表索引修改，避免每帧 new class）。</summary>
        private struct MutableEntry
        {
            public string Code;
            public string Message;
            public ErrorReportLevel Level;
            public long FirstSeenUnixSeconds;
            public long LastSeenUnixSeconds;
            public int RepeatCount;

            public ErrorReportEntry ToEntry()
            {
                return new ErrorReportEntry(Code, Message, Level, FirstSeenUnixSeconds, RepeatCount);
            }
        }
    }
}
