#nullable enable
using AIFramework.Contracts.Ports;
using System;
using System.Diagnostics;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台时间端口实现。适配层允许直接读系统时间，纯逻辑层不允许。
    /// </summary>
    public sealed class SystemTimeProvider : ITimeProvider
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private DateTime _utcOverride;
        private bool _hasOverride;
        private double _offsetSeconds;

        /// <summary>单调时钟：自创建起经过的秒数，可被 <see cref="Advance"/> 或 <see cref="SetUtcNow"/> 推进。</summary>
        public double ElapsedSeconds
        {
            get { return _offsetSeconds + _stopwatch.Elapsed.TotalSeconds; }
        }

        public DateTime UtcNow
        {
            get { return _hasOverride ? _utcOverride.AddSeconds(_stopwatch.Elapsed.TotalSeconds) : DateTime.UtcNow; }
        }

        /// <summary>把单调时钟向前推，用于演示/测试。</summary>
        public void Advance(double seconds)
        {
            _offsetSeconds += seconds;
        }

        /// <summary>固定一个 UTC 基准时间，后续 <see cref="UtcNow"/> 以它为起点继续走。</summary>
        public void SetUtcNow(DateTime utcNow)
        {
            _utcOverride = utcNow;
            _hasOverride = true;
        }

        /// <summary>把 UTC 时钟整体向前推若干天，用于演示跨天/补签场景。</summary>
        public void AdvanceDays(int days)
        {
            _utcOverride = UtcNow.AddDays(days);
            _hasOverride = true;
        }
    }
}
