using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleVfxService"/> 的行为契约验收。
    /// <para>
    /// 这些用例之所以有意义，是因为无头实现与 Unity 实现被要求<b>语义一致</b>：
    /// 什么算非法请求、池满/日志满怎么淘汰、<c>Clear</c> 之后"正在播"读作几——任何一条在这里改了
    /// 而 <c>UnityVfxService</c> 没改，就是真机上"特效不消失/数量对不上"的回归，而这在无头环境里根本看不见。
    /// </para>
    /// </summary>
    public sealed class ConsoleVfxServiceTests
    {
        [Fact]
        public void Play_RecordsFormattedEntryInDrainLog()
        {
            var vfx = new ConsoleVfxService();

            vfx.Play(new VfxRequest(1001, 1.5f, 2.25f, 1.5f, 0.5f));

            IReadOnlyList<string> log = vfx.DrainLog();

            Assert.Single(log);
            // 固定不变文化 + 最多两位小数：日志能在任何区域设置下与 CI 基线逐字比对。
            Assert.Equal("vfx#1001 @(1.5,2.25) x1.5 0.5s", log[0]);
        }

        [Fact]
        public void Play_InvalidEffectId_IsRejectedWithoutLogOrActiveCount()
        {
            var vfx = new ConsoleVfxService();

            vfx.Play(new VfxRequest(0, 0f, 0f, 1f, 1f));
            vfx.Play(new VfxRequest(-7, 0f, 0f, 1f, 1f));

            Assert.Empty(vfx.DrainLog());
            Assert.Equal(0, vfx.ActiveCount);
            Assert.Equal(2, vfx.RejectedCount);
        }

        [Fact]
        public void Play_NonPositiveDurationOrScale_IsRejected()
        {
            var vfx = new ConsoleVfxService();

            vfx.Play(new VfxRequest(1001, 0f, 0f, 1f, 0f));   // 时长为 0：永远不会到期，等同驻留，必须拒
            vfx.Play(new VfxRequest(1001, 0f, 0f, 1f, -1f));
            vfx.Play(new VfxRequest(1001, 0f, 0f, 0f, 1f));   // 缩放 0：不可见，拒绝
            vfx.Play(new VfxRequest(1001, 0f, 0f, -3f, 1f));

            Assert.Empty(vfx.DrainLog());
            Assert.Equal(0, vfx.ActiveCount);
            Assert.Equal(4, vfx.RejectedCount);
        }

        [Fact]
        public void Play_NaNCoordinate_IsRejected()
        {
            var vfx = new ConsoleVfxService();

            vfx.Play(new VfxRequest(1001, float.NaN, 0f, 1f, 1f));

            Assert.Empty(vfx.DrainLog());
            Assert.Equal(1, vfx.RejectedCount);
        }

        [Fact]
        public void ActiveCount_WithoutTimeProvider_CountsEveryAcceptedPlay()
        {
            var vfx = new ConsoleVfxService();

            vfx.Play(new VfxRequest(1001, 0f, 0f, 1f, 0.1f));
            vfx.Play(new VfxRequest(1002, 0f, 0f, 1f, 0.1f));

            // 没有时钟就没有"到期"：口径退化为"自上次 Clear 起的累计播放数"，只增不减。
            // 这是刻意的保守上界，而不是假装知道特效什么时候播完（AIF003 禁止读系统时间）。
            Assert.Equal(2, vfx.ActiveCount);

            vfx.Play(new VfxRequest(1003, 0f, 0f, 1f, 0.1f));
            Assert.Equal(3, vfx.ActiveCount);
        }

        [Fact]
        public void ActiveCount_WithTimeProvider_ExpiresExactlyAtRequestedDuration()
        {
            var time = new FakeTimeProvider();
            var vfx = new ConsoleVfxService(time);

            vfx.Play(new VfxRequest(1001, 0f, 0f, 1f, 0.5f));
            vfx.Play(new VfxRequest(1002, 0f, 0f, 1f, 5f));
            Assert.Equal(2, vfx.ActiveCount);

            // 满 0.5 秒：短特效正好到期（边界取"不再算活跃"），长特效仍算在播。
            time.Advance(0.5);
            Assert.Equal(1, vfx.ActiveCount);

            time.Advance(4.5);
            Assert.Equal(0, vfx.ActiveCount);
        }

        [Fact]
        public void Clear_StopsCountingAndIsIdempotent()
        {
            var vfx = new ConsoleVfxService();
            vfx.Play(new VfxRequest(1001, 0f, 0f, 1f, 1f));

            vfx.Clear();

            Assert.Equal(0, vfx.ActiveCount);

            IReadOnlyList<string> log = vfx.DrainLog();
            Assert.Equal(2, log.Count);
            Assert.Equal(ConsoleVfxService.ClearLogEntry, log[1]);

            // 幂等：已经没有活跃特效时再清一次不写日志，避免把 64 条环形日志刷满噪声。
            vfx.Clear();
            Assert.Empty(vfx.DrainLog());
        }

        [Fact]
        public void DrainLog_KeepsOnlyLatestEntries_AndClearsAfterDrain()
        {
            var vfx = new ConsoleVfxService();

            for (int i = 0; i < 70; i++)
            {
                vfx.Play(new VfxRequest(100 + i, 0f, 0f, 1f, 1f));
            }

            IReadOnlyList<string> log = vfx.DrainLog();

            Assert.Equal(ConsoleVfxService.LogCapacity, log.Count);
            Assert.Equal("vfx#106 @(0,0) x1 1s", log[0]); // 70 - 64 = 6，前 6 条已被环形覆盖
            Assert.Equal("vfx#169 @(0,0) x1 1s", log[log.Count - 1]);

            // 排空语义：第二次取出必须为空，避免同一批事件被重复打印。
            Assert.Empty(vfx.DrainLog());
        }

        [Fact]
        public void RejectedRequests_DoNotConsumeLogCapacity()
        {
            var vfx = new ConsoleVfxService();

            for (int i = 1; i <= ConsoleVfxService.LogCapacity; i++)
            {
                vfx.Play(new VfxRequest(i, 0f, 0f, 1f, 1f));
            }

            for (int i = 0; i < 5; i++)
            {
                vfx.Play(VfxRequestWithInvalidEffectId());
            }

            IReadOnlyList<string> log = vfx.DrainLog();

            // 非法请求不进日志：64 条容量必须全是真实播放记录（否则有效诊断信息会被噪声挤掉）。
            Assert.Equal(ConsoleVfxService.LogCapacity, log.Count);
            Assert.Equal("vfx#1 @(0,0) x1 1s", log[0]);
            Assert.Equal("vfx#64 @(0,0) x1 1s", log[log.Count - 1]);
            Assert.Equal(5, vfx.RejectedCount);
        }

        [Fact]
        public void RejectedCount_IsLifetimeDiagnostic_AndSurvivesClear()
        {
            var vfx = new ConsoleVfxService();
            vfx.Play(new VfxRequest(0, 0f, 0f, 1f, 1f));
            vfx.Play(new VfxRequest(1001, 0f, 0f, 1f, 1f));

            vfx.Clear();

            Assert.Equal(1, vfx.RejectedCount);
            Assert.Equal(0, vfx.ActiveCount);
        }

        /// <summary>造一条 effectId 非法的请求（用于"非法请求不应占用资源"的批次用例）。</summary>
        private static VfxRequest VfxRequestWithInvalidEffectId()
        {
            return new VfxRequest(0, 0f, 0f, 1f, 1f);
        }

        /// <summary>可快进的假时间源：无头测试的唯一时间入口（逻辑层禁止直接读系统时钟）。</summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            public void Advance(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }
        }
    }
}
