using System;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>
    /// 可快进的假时间源：<see cref="UtcNow"/> 固定在构造给定的 UTC 时刻，
    /// <see cref="AdvanceDays"/> 同时推进墙钟与 <see cref="ElapsedSeconds"/>，保证两者不脱节。
    /// </summary>
    public sealed class FakeTimeProvider : ITimeProvider
    {
        public FakeTimeProvider(DateTime utcNow)
        {
            UtcNow = utcNow.Kind == DateTimeKind.Utc
                ? utcNow
                : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            ElapsedSeconds = 0d;
        }

        /// <summary>当前 UTC 时间，只由 Advance 系列方法推进。</summary>
        public DateTime UtcNow { get; private set; }

        /// <summary>单调递增秒数。</summary>
        public double ElapsedSeconds { get; private set; }

        /// <summary>推进整数天：签到跨天与跨周期用例的唯一时间入口。</summary>
        public void AdvanceDays(int days)
        {
            if (days < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(days), days, "时间只能向前推进。");
            }

            AdvanceSeconds(days * 86400d);
        }

        /// <summary>推进任意秒数：用于验证"同一天内推进不到 24 小时不算跨天"。</summary>
        public void AdvanceSeconds(double seconds)
        {
            if (seconds < 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "时间只能向前推进。");
            }

            ElapsedSeconds += seconds;
            UtcNow = UtcNow.AddSeconds(seconds);
        }
    }
}
