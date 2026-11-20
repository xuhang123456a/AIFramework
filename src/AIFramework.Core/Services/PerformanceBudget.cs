using System;
using System.Collections.Generic;
using System.Globalization;

namespace AIFramework.Core.Services
{
    /// <summary>性能预算阈值（毫秒/字节/帧数）。</summary>
    public readonly struct PerformanceBudgetOptions
    {
        /// <summary>构造预算：平均帧耗时上限、P95 上限、托管分配上限、最少样本帧数。</summary>
        public PerformanceBudgetOptions(double maxAverageMilliseconds, double maxP95Milliseconds, long maxAllocatedBytes, int minFrames)
        {
            MaxAverageMilliseconds = maxAverageMilliseconds < 0 ? 0 : maxAverageMilliseconds;
            MaxP95Milliseconds = maxP95Milliseconds < 0 ? 0 : maxP95Milliseconds;
            MaxAllocatedBytes = maxAllocatedBytes < 0 ? 0 : maxAllocatedBytes;
            MinFrames = minFrames < 0 ? 0 : minFrames;
        }

        /// <summary>平均帧耗时上限。</summary>
        public double MaxAverageMilliseconds { get; }

        /// <summary>P95 帧耗时上限。</summary>
        public double MaxP95Milliseconds { get; }

        /// <summary>托管分配上限。</summary>
        public long MaxAllocatedBytes { get; }

        /// <summary>有效样本所需最少帧数（不够则结论=样本不足）。</summary>
        public int MinFrames { get; }

        /// <summary>默认预算：60fps 目标下留 20% 余量（16.7→20ms），P95 给 33ms，分配 8MB。</summary>
        public static PerformanceBudgetOptions Default
        {
            get { return new PerformanceBudgetOptions(20d, 33d, 8L * 1024L * 1024L, 60); }
        }

        /// <summary>宽松预算（CI/编辑器用：帧率与 GC 抖动大，只拦"明显异常"）。</summary>
        public static PerformanceBudgetOptions Relaxed
        {
            get { return new PerformanceBudgetOptions(50d, 200d, 64L * 1024L * 1024L, 30); }
        }
    }

    /// <summary>一次预算评估结论。</summary>
    public readonly struct PerformanceVerdict
    {
        /// <summary>构造结论。</summary>
        /// <param name="passed">是否通过。</param>
        /// <param name="sampleCount">参与评估的帧数。</param>
        /// <param name="averageMilliseconds">平均帧耗时。</param>
        /// <param name="p95Milliseconds">P95 帧耗时。</param>
        /// <param name="allocatedBytes">托管分配字节数。</param>
        /// <param name="reasons">不通过原因（通过时为空数组）。</param>
        public PerformanceVerdict(bool passed, int sampleCount, double averageMilliseconds, double p95Milliseconds,
                                  long allocatedBytes, string[] reasons)
        {
            Passed = passed;
            SampleCount = sampleCount;
            AverageMilliseconds = averageMilliseconds;
            P95Milliseconds = p95Milliseconds;
            AllocatedBytes = allocatedBytes;
            Reasons = reasons ?? new string[0];
        }

        /// <summary>是否通过。</summary>
        public bool Passed { get; }

        /// <summary>参与评估的帧数。</summary>
        public int SampleCount { get; }

        /// <summary>平均帧耗时（毫秒）。</summary>
        public double AverageMilliseconds { get; }

        /// <summary>P95 帧耗时（毫秒）。</summary>
        public double P95Milliseconds { get; }

        /// <summary>托管分配字节数。</summary>
        public long AllocatedBytes { get; }

        /// <summary>不通过原因。</summary>
        public string[] Reasons { get; }

        /// <summary>一行可粘贴到 CI 日志/报告的字符串（口径固定，便于横向比较）。</summary>
        public string ToReportLine()
        {
            return "[Perf] 帧=" + SampleCount.ToString(CultureInfo.InvariantCulture)
                + " 平均=" + AverageMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + "ms"
                + " P95=" + P95Milliseconds.ToString("F2", CultureInfo.InvariantCulture) + "ms"
                + " 分配=" + (AllocatedBytes / 1024L).ToString(CultureInfo.InvariantCulture) + "KB"
                + " 结论=" + (Passed ? "通过" : "不通过（" + string.Join("；", Reasons) + "）");
        }
    }

    /// <summary>
    /// 帧耗时/分配预算评估：把采样数组翻成"过不过 + 为什么"。
    /// <para>
    /// <b>为什么预算判定要放在 Core 而不是测试里</b>：阈值与口径（平均/P95/样本数）是产品决策，
    /// 写在测试里会导致"游戏内 HUD 看到的结论"和"CI 判定"两套算法漂移；放 Core 后两边共用一份。
    /// </para>
    /// <para>
    /// <b>为什么用 P95 而不是最大帧</b>：最大值永远会被编辑器断点、GC、场景加载尖刺污染；
    /// P95 能表达"95% 的帧有多差"，既抗单点尖刺又能抓住真实掉帧。
    /// </para>
    /// <para>
    /// <b>为什么不在这里 Sleep/等待信号</b>：本类只做纯计算，采样由调用方（适配层/测试）负责——保持可在无头环境测试。
    /// </para>
    /// </summary>
    public static class PerformanceBudget
    {
        /// <summary>评估一批帧耗时样本。</summary>
        /// <param name="frameMilliseconds">帧耗时（毫秒）；非正/NaN 会被忽略。</param>
        /// <param name="allocatedBytes">该窗口内托管分配字节数（&lt;0 按 0）。</param>
        /// <param name="options">预算阈值。</param>
        /// <returns>评估结论。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frameMilliseconds"/> 为 null。</exception>
        public static PerformanceVerdict Evaluate(double[] frameMilliseconds, long allocatedBytes, in PerformanceBudgetOptions options)
        {
            if (frameMilliseconds == null)
            {
                throw new ArgumentNullException(nameof(frameMilliseconds));
            }

            var samples = new List<double>(frameMilliseconds.Length);
            for (int i = 0; i < frameMilliseconds.Length; i++)
            {
                double value = frameMilliseconds[i];
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                {
                    continue;
                }

                samples.Add(value);
            }

            var reasons = new List<string>(3);
            if (samples.Count < options.MinFrames)
            {
                reasons.Add("样本不足（" + samples.Count + " < " + options.MinFrames + "）");
            }

            double average = 0d;
            double p95 = 0d;
            if (samples.Count > 0)
            {
                double sum = 0d;
                for (int i = 0; i < samples.Count; i++)
                {
                    sum += samples[i];
                }

                average = sum / samples.Count;

                var sorted = new List<double>(samples);
                sorted.Sort();
                int index = (int)Math.Ceiling(sorted.Count * 0.95d) - 1;
                if (index < 0)
                {
                    index = 0;
                }

                p95 = sorted[index];

                if (average > options.MaxAverageMilliseconds)
                {
                    reasons.Add("平均 " + average.ToString("F2", CultureInfo.InvariantCulture)
                        + "ms > " + options.MaxAverageMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + "ms");
                }

                if (p95 > options.MaxP95Milliseconds)
                {
                    reasons.Add("P95 " + p95.ToString("F2", CultureInfo.InvariantCulture)
                        + "ms > " + options.MaxP95Milliseconds.ToString("F2", CultureInfo.InvariantCulture) + "ms");
                }
            }

            long allocated = allocatedBytes < 0 ? 0 : allocatedBytes;
            if (allocated > options.MaxAllocatedBytes)
            {
                reasons.Add("分配 " + (allocated / 1024L) + "KB > " + (options.MaxAllocatedBytes / 1024L) + "KB");
            }

            return new PerformanceVerdict(reasons.Count == 0, samples.Count, average, p95, allocated, reasons.ToArray());
        }
    }
}
