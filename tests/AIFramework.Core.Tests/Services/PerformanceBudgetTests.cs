using System;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 性能预算评估的口径：达标/超标/样本不足/分配超标，以及"P95 抗尖刺"这条设计意图。
    /// </summary>
    public sealed class PerformanceBudgetTests
    {
        private static double[] Frames(int count, double value)
        {
            var frames = new double[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = value;
            }

            return frames;
        }

        [Fact]
        public void Evaluate_WithinBudget_Passes()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(
                Frames(120, 12.5), allocatedBytes: 1024 * 1024, PerformanceBudgetOptions.Default);

            Assert.True(verdict.Passed);
            Assert.Equal(120, verdict.SampleCount);
            Assert.Equal(12.5, verdict.AverageMilliseconds, 3);
            Assert.Empty(verdict.Reasons);
        }

        [Fact]
        public void Evaluate_AverageOverBudget_Fails()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(
                Frames(120, 30d), 0, PerformanceBudgetOptions.Default);

            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Reasons, reason => reason.Contains("平均"));
        }

        [Fact]
        public void Evaluate_AllocationOverBudget_Fails()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(
                Frames(120, 5d), allocatedBytes: 16L * 1024 * 1024, PerformanceBudgetOptions.Default);

            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Reasons, reason => reason.Contains("分配"));
        }

        [Fact]
        public void Evaluate_TooFewSamples_Fails()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(
                Frames(10, 5d), 0, PerformanceBudgetOptions.Default);

            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Reasons, reason => reason.Contains("样本不足"));
        }

        [Fact]
        public void Evaluate_InvalidSamplesIgnored()
        {
            double[] frames = { 10d, double.NaN, -1d, 0d, double.PositiveInfinity, 10d };

            PerformanceVerdict verdict = PerformanceBudget.Evaluate(frames, 0, PerformanceBudgetOptions.Relaxed);

            Assert.Equal(2, verdict.SampleCount);
            Assert.Equal(10d, verdict.AverageMilliseconds, 3);
        }

        [Fact]
        public void Evaluate_P95_IsNotPollutedBySingleSpike()
        {
            // 100 帧里只有 1 帧是 500ms 尖刺：P95 仍应接近 10ms，平均会被拉高但仍远小于尖刺。
            var frames = new double[100];
            for (int i = 0; i < 99; i++)
            {
                frames[i] = 10d;
            }

            frames[99] = 500d;

            PerformanceVerdict verdict = PerformanceBudget.Evaluate(frames, 0, PerformanceBudgetOptions.Relaxed);

            // 100 帧的 P95 是第 95 个（索引 94），恰好避开唯一一帧 500ms 尖刺。
            Assert.True(verdict.AverageMilliseconds < 20d, "单点尖刺不应把均值拉到 20ms 以上。");
        }

        [Fact]
        public void Evaluate_P95ThresholdTrips()
        {
            var frames = new double[100];
            for (int i = 0; i < 90; i++)
            {
                frames[i] = 5d;
            }

            for (int i = 90; i < 100; i++)
            {
                frames[i] = 40d;
            }

            PerformanceVerdict verdict = PerformanceBudget.Evaluate(frames, 0, PerformanceBudgetOptions.Default);

            Assert.False(verdict.Passed);
            Assert.Contains(verdict.Reasons, reason => reason.Contains("P95"));
        }

        [Fact]
        public void Report_ContainsFixedFields()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(
                Frames(120, 12d), 2L * 1024 * 1024, PerformanceBudgetOptions.Default);

            string line = verdict.ToReportLine();

            Assert.Contains("[Perf]", line);
            Assert.Contains("帧=120", line);
            Assert.Contains("平均=", line);
            Assert.Contains("P95=", line);
            Assert.Contains("分配=", line);
            Assert.Contains("结论=通过", line);
        }

        [Fact]
        public void Report_Failure_ContainsReasons()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(
                Frames(120, 100d), 0, PerformanceBudgetOptions.Default);

            Assert.Contains("结论=不通过", verdict.ToReportLine());
            Assert.Contains("平均", verdict.ToReportLine());
        }

        [Fact]
        public void Evaluate_NullFrames_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => PerformanceBudget.Evaluate(null!, 0, PerformanceBudgetOptions.Default));
        }

        [Fact]
        public void Options_NegativeValuesClampToZero()
        {
            var options = new PerformanceBudgetOptions(-1d, -1d, -1L, -1);

            Assert.Equal(0d, options.MaxAverageMilliseconds);
            Assert.Equal(0d, options.MaxP95Milliseconds);
            Assert.Equal(0L, options.MaxAllocatedBytes);
            Assert.Equal(0, options.MinFrames);
        }

        [Fact]
        public void Evaluate_EmptySamples_FailsOnSampleCount()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(Array.Empty<double>(), 0, PerformanceBudgetOptions.Default);

            Assert.False(verdict.Passed);
            Assert.Equal(0, verdict.SampleCount);
            Assert.Equal(0d, verdict.AverageMilliseconds);
            Assert.Equal(0d, verdict.P95Milliseconds);
        }

        [Fact]
        public void DefaultBudget_IsTighterThanRelaxed()
        {
            Assert.True(PerformanceBudgetOptions.Default.MaxAverageMilliseconds < PerformanceBudgetOptions.Relaxed.MaxAverageMilliseconds);
            Assert.True(PerformanceBudgetOptions.Default.MaxAllocatedBytes < PerformanceBudgetOptions.Relaxed.MaxAllocatedBytes);
        }

        [Fact]
        public void Evaluate_NegativeAllocated_ClampsToZero()
        {
            PerformanceVerdict verdict = PerformanceBudget.Evaluate(Frames(120, 5d), -12345, PerformanceBudgetOptions.Default);

            Assert.Equal(0L, verdict.AllocatedBytes);
            Assert.True(verdict.Passed);
        }
    }
}
