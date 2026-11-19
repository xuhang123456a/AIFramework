#nullable enable
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Diagnostics
{
    /// <summary>
    /// 帧耗时 HUD：把 <see cref="FrameProfiler"/> 的窗口统计写成一行 uGUI 文本，形如
    /// <c>FPS 59.8 · avg 16.7ms · peak 24.1ms · 帧分配 10B · 采样 240</c>。
    /// <para>
    /// <b>为什么要节流</b>：uGUI 每次给 <c>Text.text</c> 赋新值都会触发
    /// <c>GenerateText</c>（重新排版 + 重建网格），那是每帧几毫秒级的开销。
    /// 这里把重绘压到 4Hz（0.25s 一次）：足够看清趋势，又不会让"测量工具"变成"帧耗时主要来源"。
    /// </para>
    /// <para>
    /// <b>为什么文本自身还是会分配</b>：数字转字符串 + 拼一行文本必然产生临时堆对象（每 0.25s 一次，几十~几百字节）。
    /// 采样路径（<see cref="FrameProfiler.Update"/>）与这里完全隔离，因此"采样零分配"成立；
    /// 而 HUD 自身的分配会落在下一帧的分配样本里，所以 <c>帧分配</c> 一栏在真实场景里是**小的非零值**而不是 0——
    /// 这恰好也是它诚实的地方：要单看采样路径是否零分配，读 <see cref="FrameProfiler.AllocatedSinceLastReset"/>
    /// 并配合 Editor Profiler 的 <c>GC.Alloc</c> 列，别指望 HUD 数字能掉到 0。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PerformanceHud : MonoBehaviour
    {
        /// <summary>重绘间隔（秒）：4Hz。低于这个频率信息更新不及时，高于它就是在烧 UI 重建。</summary>
        private const float RenderIntervalSeconds = 0.25f;

        /// <summary>字段分隔符（与设计稿一致）。</summary>
        private const string Separator = " · ";

        /// <summary>状态行控件（Inspector/建场景脚本注入）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>数据源（Inspector/建场景脚本注入）；没注入时 HUD 只显示占位文案，不抛异常。</summary>
        [SerializeField] private FrameProfiler profiler = null!;

        /// <summary>
        /// 复用的字符串缓冲：每 0.25s 清空重填一次，避免每次 <c>Render</c> 都新建 <see cref="StringBuilder"/>。
        /// <b>它不是零分配方案</b>（<c>Append(double)</c> 与 <c>ToString()</c> 仍会分配），只是把稳定态垃圾压到最小。
        /// </summary>
        private readonly StringBuilder _builder = new StringBuilder(160);

        /// <summary>距上次重绘累计的秒数（只用 unscaled 时间：暂停时也要能看到最后一次数据）。</summary>
        private float _sinceLastRender = RenderIntervalSeconds;

        /// <summary>按 4Hz 触发一次 <see cref="Render"/>。</summary>
        private void Update()
        {
            _sinceLastRender += Time.unscaledDeltaTime;
            if (_sinceLastRender < RenderIntervalSeconds)
            {
                return;
            }

            _sinceLastRender = 0f;
            Render();
        }

        /// <summary>
        /// 立刻重绘一次（<see cref="Update"/> 内部按 4Hz 调它；测试/调试器也可以直接调）。
        /// <para>
        /// 缺控件、缺 profiler、profiler 还没采到帧，三种退化情况都只写一行占位文案，绝不抛异常——
        /// 诊断组件把游戏打挂，比没有诊断更糟。
        /// </para>
        /// </summary>
        public void Render()
        {
            Text? target = statusText;
            if (target == null)
            {
                return;
            }

            FrameProfiler? source = profiler;
            if (source == null)
            {
                target.text = "FPS --" + Separator + "未挂 FrameProfiler";
                return;
            }

            int samples = source.SampleCount;
            if (samples == 0)
            {
                target.text = "采样中…" + Separator + "等待首帧";
                return;
            }

            double average = source.AverageMilliseconds;
            double peak = source.PeakMilliseconds;
            double fps = average > 0d ? 1000d / average : 0d;

            // 帧均分配 = 自上次 ResetCounters 以来的分配 ÷ 同区间帧数。
            // 不用 SampleCount 当除数：它被环形缓冲封顶（写满 240 后不再增长），会把比值越算越大。
            long frames = source.FramesSinceReset;
            long bytesPerFrame = frames > 0L ? source.AllocatedSinceLastReset / frames : 0L;

            StringBuilder builder = _builder;
            builder.Length = 0;
            builder.Append("FPS ");
            AppendFixed1(builder, fps);
            builder.Append(Separator);
            builder.Append("avg ");
            AppendFixed1(builder, average);
            builder.Append("ms");
            builder.Append(Separator);
            builder.Append("peak ");
            AppendFixed1(builder, peak);
            builder.Append("ms");
            builder.Append(Separator);
            builder.Append("帧分配 ");
            builder.Append(bytesPerFrame);
            builder.Append('B');
            builder.Append(Separator);
            builder.Append("采样 ");
            builder.Append(samples);

            target.text = builder.ToString();
        }

        /// <summary>
        /// 追加"一位小数"的数字。用 <see cref="CultureInfo.InvariantCulture"/> 而不是当前文化：
        /// 某些区域（德/法）小数点是逗号，用当前文化会让 HUD 在不同机器上长得不一样，也难做字符串比对。
        /// </summary>
        private static void AppendFixed1(StringBuilder builder, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                builder.Append("--");
                return;
            }

            builder.Append(value.ToString("F1", CultureInfo.InvariantCulture));
        }
    }
}
