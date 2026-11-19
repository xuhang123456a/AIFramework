#nullable enable
using System;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Diagnostics
{
    /// <summary>
    /// 轻量帧耗时采样器（T3-11 帧耗时基线）：每帧把一次帧时长写进定长环形缓冲，
    /// 顺带记账"本线程分配了多少字节"，供 <see cref="PerformanceHud"/> 显示与回归对比。
    /// <para>
    /// <b>为什么不用 Unity Profiler / Recorder</b>：那套 API 在 Player 里开销可观，而且会把自身开销算进样本；
    /// 这里要的是"每帧几十纳秒、0 字节、可以长期开着"的常驻表计，用来当帧耗时与帧分配的**回归基线**，
    /// 不是做深度剖析（深度剖析请用 Editor 的 Profiler 窗口）。
    /// </para>
    /// <para>
    /// <b>采样路径零分配的三条约束</b>（改这个文件时任何一条被破坏，"0 分配"基线就失效）：
    /// 1) 缓冲在 <c>Awake</c> 一次性分配 <c>double[capacity]</c>，<c>Update</c> 只写下标，**绝不扩容**；
    /// 2) <c>Update</c> 不碰 <c>string</c>、不装箱、不 LINQ、不调 <c>Debug.Log</c>（日志格式化本身就分配）；
    /// 3) 平均/峰值**按需现算**（O(n)，n ≤ 240 次 double 加法），不维护滚动和——滚动和必须在样本淘汰时重算，
    ///    维护成本与出错面都大于收益，而读取方（HUD）最高 4Hz，一次遍历完全不够看。
    /// </para>
    /// <para>
    /// <b>分配是怎么量的</b>：<c>GC.GetAllocatedBytesForCurrentThread()</c> 是"当前线程累计托管分配字节数"的单调计数器，
    /// 两次采样相减即该帧该线程的分配增量。局限：只覆盖**托管堆**，只覆盖**调用它的那个线程**
    /// （渲染/音频/Unity 内部 Job 线程不在这条线上）。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FrameProfiler : MonoBehaviour
    {
        /// <summary>默认采样容量：240 帧 ≈ 4 秒 @ 60fps。</summary>
        private const int DefaultSampleCapacity = 240;

        /// <summary>
        /// 环形缓冲容量。只在 <c>Awake</c> 读一次：运行中在 Inspector 改它不会生效（缓冲已按旧容量分配），
        /// 要改容量请改完再进 Play，或调用方重建组件。
        /// </summary>
        [SerializeField] private int sampleCapacity = DefaultSampleCapacity;

        /// <summary>帧时长环形缓冲（毫秒），长度 == 容量。<c>Awake</c> 之后再不变。</summary>
        private double[] _milliseconds = Array.Empty<double>();

        /// <summary>下一个写入槽位；写满一圈后回到 0 覆盖最旧样本。</summary>
        private int _nextSlot;

        /// <summary>已写入的样本数（上限 = 容量；写满后不再增长）。</summary>
        private int _sampleCount;

        /// <summary>最近一次采样的帧时长（毫秒）。</summary>
        private double _lastMilliseconds;

        /// <summary>组件启动时（<c>Awake</c>）当前线程的累计分配字节数，作为 <see cref="TotalAllocatedBytes"/> 的零点。</summary>
        private long _baselineAllocatedBytes;

        /// <summary>上一次采样时读到的当前线程累计分配字节数（下一帧做差用）。</summary>
        private long _lastThreadAllocatedBytes;

        /// <summary>自最近一次 <see cref="ResetCounters"/> 以来的本线程累计分配增量。</summary>
        private long _allocatedSinceReset;

        /// <summary>自最近一次 <see cref="ResetCounters"/> 以来的采样帧数（<see cref="AllocatedSinceLastReset"/> 的除数）。</summary>
        private long _framesSinceReset;

        /// <summary>环形缓冲里的有效样本数（≤ 容量）。</summary>
        public int SampleCount
        {
            get { return _sampleCount; }
        }

        /// <summary>
        /// 窗口内平均帧耗时（毫秒）；还没有样本时返回 0。
        /// <para>
        /// 每次读都遍历一次缓冲（O(n)，n ≤ 240）。<b>不缓存</b>的理由见类注释第 3 条：
        /// 缓存滚动和就必须在样本被覆盖时做增量修正，而"平均/峰值随便读"是一秒几次的事，算得起。
        /// </para>
        /// </summary>
        public double AverageMilliseconds
        {
            get
            {
                int count = _sampleCount;
                if (count == 0)
                {
                    return 0d;
                }

                double[] samples = _milliseconds;
                if (count > samples.Length)
                {
                    count = samples.Length;
                }

                double sum = 0d;
                for (int i = 0; i < count; i++)
                {
                    sum += samples[i];
                }

                return sum / count;
            }
        }

        /// <summary>窗口内峰值帧耗时（毫秒）；还没有样本时返回 0。与 <see cref="AverageMilliseconds"/> 取自同一批样本。</summary>
        public double PeakMilliseconds
        {
            get
            {
                int count = _sampleCount;
                if (count == 0)
                {
                    return 0d;
                }

                double[] samples = _milliseconds;
                if (count > samples.Length)
                {
                    count = samples.Length;
                }

                double peak = 0d;
                for (int i = 0; i < count; i++)
                {
                    double value = samples[i];
                    if (value > peak)
                    {
                        peak = value;
                    }
                }

                return peak;
            }
        }

        /// <summary>最近一帧的耗时（毫秒）；还没有采样时为 0。</summary>
        public double LastMilliseconds
        {
            get { return _lastMilliseconds; }
        }

        /// <summary>
        /// 自本组件 <c>Awake</c> 以来当前线程的累计托管分配增量（诊断用）。<see cref="ResetCounters"/> **不会**把它清零。
        /// <para>它量的是"这个组件挂上之后主线程一共分配了多少"，包含 UI 文本、日志、宿主装配等一切，不只采样路径。</para>
        /// </summary>
        public long TotalAllocatedBytes
        {
            get { return _lastThreadAllocatedBytes - _baselineAllocatedBytes; }
        }

        /// <summary>自最近一次 <see cref="ResetCounters"/> 以来的当前线程累计分配增量（诊断用）。</summary>
        public long AllocatedSinceLastReset
        {
            get { return _allocatedSinceReset; }
        }

        /// <summary>
        /// 自最近一次 <see cref="ResetCounters"/> 以来的采样帧数。
        /// 用它当 <see cref="AllocatedSinceLastReset"/> 的除数即可得"帧均分配字节"；
        /// 不用 <see cref="SampleCount"/> 当除数，是因为后者被环形缓冲封顶（写满 240 后不再增长）。
        /// </summary>
        public long FramesSinceReset
        {
            get { return _framesSinceReset; }
        }

        /// <summary>是否已经采到至少一帧（HUD 用它决定显示"采样中"还是真实数字）。</summary>
        public bool HasSamples
        {
            get { return _sampleCount > 0; }
        }

        /// <summary>
        /// 装配期一次性分配环形缓冲并记下分配零点。<b>这是本类唯一允许分配的地方</b>；
        /// <c>Update</c> 一旦要扩容，帧分配基线就不再是 0。
        /// </summary>
        private void Awake()
        {
            int capacity = sampleCapacity;
            if (capacity < 1)
            {
                // Inspector 被手改成 0/负数时退化为"只留最近一帧"，而不是抛异常或每帧越界。
                capacity = 1;
            }

            _milliseconds = new double[capacity];
            _nextSlot = 0;
            _sampleCount = 0;
            _lastMilliseconds = 0d;

            long allocated = GC.GetAllocatedBytesForCurrentThread();
            _baselineAllocatedBytes = allocated;
            _lastThreadAllocatedBytes = allocated;
            _allocatedSinceReset = 0L;
            _framesSinceReset = 0L;
        }

        /// <summary>
        /// 每帧采样一次：写一个 <c>double</c>、做一次整数减法，全程不分配。
        /// <para>
        /// 分配增量取的是"上一次采样到现在"的窗口，所以 HUD 的文本重组、日志等**其它组件**的分配
        /// 也会落进某一帧的样本里——这是本表计的固有口径，不是漏算。
        /// </para>
        /// </summary>
        private void Update()
        {
            double[] samples = _milliseconds;
            if (samples.Length == 0)
            {
                // Awake 被异常打断时缓冲仍是空的：Unity 在 Awake 抛异常后**仍会**继续调 Update，
                // 这里直接退出，宁可不采样，也不要每帧抛一次越界把日志刷满。
                return;
            }

            long allocatedNow = GC.GetAllocatedBytesForCurrentThread();
            long delta = allocatedNow - _lastThreadAllocatedBytes;
            if (delta < 0L)
            {
                // 计数器理论上单调；万一平台实现异常/线程切换导致回退，按 0 记，
                // 否则"累计分配"会出现负数，把基线算歪。
                delta = 0L;
            }

            _lastThreadAllocatedBytes = allocatedNow;
            _allocatedSinceReset += delta;
            _framesSinceReset++;

            // 用 unscaledDeltaTime：Time.timeScale = 0 时 deltaTime 会变 0，
            // 那会把"暂停"洗成"0ms 完美帧"，污染基线。
            double milliseconds = Time.unscaledDeltaTime * 1000d;
            _lastMilliseconds = milliseconds;

            int slot = _nextSlot;
            samples[slot] = milliseconds;

            slot++;
            if (slot >= samples.Length)
            {
                slot = 0;
            }

            _nextSlot = slot;

            if (_sampleCount < samples.Length)
            {
                _sampleCount++;
            }
        }

        /// <summary>
        /// 清零**计数**（分配增量与帧计数），**不动采样**：
        /// 窗口里的历史帧时长保留，<see cref="AverageMilliseconds"/>/<see cref="PeakMilliseconds"/> 不受影响。
        /// <para>
        /// 典型用法：跑一段固定负载前调一次，之后只读 <see cref="AllocatedSinceLastReset"/> 判断"这段有没有分配"；
        /// 不重采样是为了让耗时曲线连续，方便对照"分配从哪一帧开始出现"。
        /// </para>
        /// </summary>
        public void ResetCounters()
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            _allocatedSinceReset = 0L;
            _framesSinceReset = 0L;
            _lastThreadAllocatedBytes = allocated;

            // 刻意不改 _baselineAllocatedBytes：TotalAllocatedBytes 的语义是"自启动以来"，不应被局部清零带偏。
        }
    }
}
