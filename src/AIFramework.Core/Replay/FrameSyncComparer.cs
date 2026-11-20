#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace AIFramework.Core.Replay
{
    /// <summary>
    /// 两路对拍结论：比对了多少帧、首个分歧落在哪一帧、该帧两路各自的指纹。
    /// <para>
    /// <b>帧号口径是 1 基</b>：面向人读的日志里"第 1 帧"就是第一帧；程序内部下标换算为
    /// <c>FirstDivergenceFrame - 1</c>。无分歧时 <see cref="FirstDivergenceFrame"/> 固定为 <c>-1</c>
    /// 而不是 <c>0</c>——<c>0</c> 与"第 0 帧"无法区分，容易把"完全一致"误读成"第 0 帧就分歧"。
    /// </para>
    /// <para>
    /// <b>缺失侧指纹记 0</b>：一路比另一路短时，短的那一路在"缺失帧"上没有指纹可给，用 <c>0</c> 占位，
    /// 并由 <see cref="FrameSyncComparer.DescribeDivergence"/> 明确标注"（缺失）"；
    /// 真实指纹恰好等于 <c>0</c> 的情况靠帧号与上下文文本区分，不会与"缺失"混淆。
    /// </para>
    /// <para>
    /// <b><c>default</c> 实例不代表一致</b>：未经构造的 <c>default(FrameSyncReport)</c> 帧号字段为 <c>0</c>，
    /// <see cref="IsMatch"/> 为 <c>false</c>。宁可把"未比对"报成分歧，也不能让它冒充"已验证一致"。
    /// </para>
    /// </summary>
    public readonly struct FrameSyncReport
    {
        /// <summary>构造对拍结论。</summary>
        /// <param name="comparedFrames">真正成对比对过的帧数；负数按 0 处理。</param>
        /// <param name="firstDivergenceFrame">首个分歧帧号（1 基）；任意负数统一归一为 <c>-1</c>（无分歧）。</param>
        /// <param name="expectedFingerprint">分歧帧上"期望（原始会话）"一路的指纹；该侧无对应帧时为 <c>0</c>。</param>
        /// <param name="actualFingerprint">分歧帧上"实际（回放重放）"一路的指纹；该侧无对应帧时为 <c>0</c>。</param>
        public FrameSyncReport(int comparedFrames, int firstDivergenceFrame, long expectedFingerprint, long actualFingerprint)
        {
            ComparedFrames = comparedFrames < 0 ? 0 : comparedFrames;
            FirstDivergenceFrame = firstDivergenceFrame < 0 ? -1 : firstDivergenceFrame;
            ExpectedFingerprint = expectedFingerprint;
            ActualFingerprint = actualFingerprint;
        }

        /// <summary>
        /// 真正成对比对过的帧数：只统计"两路都有指纹"的位置，因此一路比另一路短时它等于较短长度。
        /// 出现数值分歧时包含分歧帧本身（该帧确实做了比较）。
        /// </summary>
        public int ComparedFrames { get; }

        /// <summary>首个分歧帧号（1 基）；无分歧为 <c>-1</c>。</summary>
        public int FirstDivergenceFrame { get; }

        /// <summary>分歧帧上"期望（原始会话）"一路的指纹；该侧无对应帧时为 <c>0</c>；无分歧时也为 <c>0</c>。</summary>
        public long ExpectedFingerprint { get; }

        /// <summary>分歧帧上"实际（回放重放）"一路的指纹；该侧无对应帧时为 <c>0</c>；无分歧时也为 <c>0</c>。</summary>
        public long ActualFingerprint { get; }

        /// <summary>是否逐帧一致（等价于 <see cref="FirstDivergenceFrame"/> 小于 0）。</summary>
        public bool IsMatch
        {
            get { return FirstDivergenceFrame < 0; }
        }

        /// <summary>
        /// 一行可粘贴到 CI 日志/缺陷单的报告（口径固定，便于横向比较）。
        /// 固定形如 <c>[FrameSync] 比对= 结论= 首个分歧= 期望= 实际=</c>，数字一律按
        /// <see cref="CultureInfo.InvariantCulture"/> 输出，避免不同区域设置下负号/分隔符漂移。
        /// </summary>
        public string ToReportLine()
        {
            return "[FrameSync] 比对=" + ComparedFrames.ToString(CultureInfo.InvariantCulture)
                + " 结论=" + (IsMatch ? "一致" : "分歧")
                + " 首个分歧=" + FirstDivergenceFrame.ToString(CultureInfo.InvariantCulture)
                + " 期望=" + ExpectedFingerprint.ToString(CultureInfo.InvariantCulture)
                + " 实际=" + ActualFingerprint.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 帧同步对拍器：拿同一份输入序列跑出的两路逐帧状态指纹，定位首个分歧帧。
    /// <para>
    /// <b>定位边界</b>：本类只吃 <see cref="long"/> 指纹数组，不引用会话、内核或适配层——
    /// 采集指纹是宿主的事（强实时局每帧用 <see cref="StateFingerprint.Of"/> 把可观测状态折成一个
    /// <see cref="long"/>），对拍器只负责"逐帧比较 + 首个分歧定位 + 可粘贴的报告"。
    /// 因此本类放在 <c>Core</c>（netstandard2.1、零引擎引用），任何宿主都能用。
    /// </para>
    /// <para>
    /// <b>为什么批量对拍之外还要有流式验证</b>：适配层的竞技场验证器是"跑一帧推一帧"的流式兜底，
    /// 适合运行期立刻止损；离线/CI 场景两路数据已经完整落下来，批量比较才能给出"相对期望数组第几帧"
    /// 的可复现定位，也便于把两路指纹数组先存档、之后反复重跑同一份证据。
    /// </para>
    /// <para>
    /// <b>只负责定位，不负责解释</b>：真实分歧的常见根因都是非确定性来源（AI 随机数、
    /// 帧内系统执行顺序、浮点求和顺序、容器遍历顺序）；本类只回答"从第几帧开始不一样"，
    /// 根因需要结合录像头（种子/关卡）与当帧输入另行排查。
    /// </para>
    /// <para>
    /// <b>线程与分配</b>：全部静态无状态、全同步，无锁、无静态可变字段，可在任意线程安全调用；
    /// <see cref="Compare"/> 不建中间集合、不装箱，纯遍历零托管分配；
    /// <see cref="DescribeDivergence"/> 只分配返回文本本身。不读时钟、不用随机源。
    /// </para>
    /// </summary>
    public static class FrameSyncComparer
    {
        /// <summary>分歧定位默认展示的前后上下文帧数。</summary>
        private const int DefaultContextFrames = 3;

        /// <summary>
        /// 逐帧比对两路指纹，返回首个分歧帧与两路指纹。
        /// <list type="number">
        /// <item>公共前缀逐帧比较，第一个不等的帧即分歧帧（1 基）。</item>
        /// <item>长度不同且公共前缀全等：分歧帧 = 较短长度 + 1（"多出/缺少的第一帧"），
        /// 缺失那一路的指纹记 <c>0</c>，另一路给该帧真实指纹。</item>
        /// <item>双空 → 一致（比对 0 帧）；一空一非空 → 第 1 帧分歧。</item>
        /// <item>任一入参为 <c>null</c> → <see cref="ArgumentNullException"/>（不把"没传数据"当成"空局一致"）。</item>
        /// </list>
        /// </summary>
        /// <param name="expected">期望一路的逐帧指纹（原始会话采集）。</param>
        /// <param name="actual">实际一路的逐帧指纹（回放重放采集）。</param>
        /// <returns>对拍结论；<see cref="FrameSyncReport.IsMatch"/> 为 <c>true</c> 表示逐帧一致。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="expected"/> 或 <paramref name="actual"/> 为 <c>null</c>。</exception>
        public static FrameSyncReport Compare(long[] expected, long[] actual)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }

            int commonLength = expected.Length < actual.Length ? expected.Length : actual.Length;

            // 直接按公共前缀遍历：不拷贝、不切分、不建中间集合。
            for (int index = 0; index < commonLength; index++)
            {
                if (expected[index] != actual[index])
                {
                    // 分歧帧本身确实做了比较，因此 ComparedFrames 包含它。
                    return new FrameSyncReport(index + 1, index + 1, expected[index], actual[index]);
                }
            }

            if (expected.Length == actual.Length)
            {
                return new FrameSyncReport(commonLength, -1, 0L, 0L);
            }

            // 公共前缀全等、长度不同：分歧在"较短一路结束后的第一帧"，缺失侧指纹记 0。
            if (expected.Length > commonLength)
            {
                return new FrameSyncReport(commonLength, commonLength + 1, expected[commonLength], 0L);
            }

            return new FrameSyncReport(commonLength, commonLength + 1, 0L, actual[commonLength]);
        }

        /// <summary>
        /// 生成"分歧定位"多行文本：以分歧帧为中心，前后各展示 <paramref name="contextFrames"/> 帧，
        /// 便于直接贴进日志或缺陷单。
        /// <para>
        /// 首行是头部（分歧帧号、两路总帧数、上下文宽度）；之后每帧一行，格式
        /// <c>帧 N: 期望=X 实际=Y</c>，分歧行以 <c>&gt; </c> 开头，缺失侧写作 <c>0（缺失）</c>。
        /// </para>
        /// <para>无分歧（含双空一致）返回固定文本 <c>"无分歧"</c>，不返回空串。</para>
        /// </summary>
        /// <param name="expected">期望一路的逐帧指纹。</param>
        /// <param name="actual">实际一路的逐帧指纹。</param>
        /// <param name="contextFrames">分歧帧前后各展示的帧数；负数按 0 处理（只展示分歧帧本身）。</param>
        /// <returns>多行定位文本；无分歧时为 <c>"无分歧"</c>。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="expected"/> 或 <paramref name="actual"/> 为 <c>null</c>。</exception>
        public static string DescribeDivergence(long[] expected, long[] actual, int contextFrames = DefaultContextFrames)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            if (actual == null)
            {
                throw new ArgumentNullException(nameof(actual));
            }

            // 复用同一套分歧判定，避免 Compare 与 Describe 对"第几帧"给出两套口径。
            FrameSyncReport report = Compare(expected, actual);
            if (report.IsMatch)
            {
                return "无分歧";
            }

            int divergenceIndex = report.FirstDivergenceFrame - 1;
            int longestLength = expected.Length > actual.Length ? expected.Length : actual.Length;

            // 上下文先收敛到数组长度再算下标：避免调用方传 int.MaxValue 时 divergenceIndex + context
            // 溢出成负数（那会让 StringBuilder 收到负容量并抛异常，而不是展示全部帧）。
            int context = contextFrames < 0 ? 0 : contextFrames;
            if (context > longestLength)
            {
                context = longestLength;
            }

            int firstIndex = (int)Math.Max(0L, (long)divergenceIndex - context);
            int lastIndex = (int)Math.Min((long)longestLength - 1L, (long)divergenceIndex + context);

            int lineCount = lastIndex - firstIndex + 2;

            // 容量提示封顶：正常战局只有个位数帧上下文，这里只防"超大数组 + 超大上下文"
            // 把 lineCount * 48 乘到溢出（负容量会直接抛异常）。
            int capacityHint = lineCount > 4096 ? 4096 * 48 : lineCount * 48;
            var builder = new StringBuilder(capacityHint);
            builder.Append("[FrameSync] 首个分歧帧=")
                .Append(report.FirstDivergenceFrame.ToString(CultureInfo.InvariantCulture))
                .Append("（1 基） 期望帧数=")
                .Append(expected.Length.ToString(CultureInfo.InvariantCulture))
                .Append(" 实际帧数=")
                .Append(actual.Length.ToString(CultureInfo.InvariantCulture))
                .Append(" 上下文=")
                .Append(context.ToString(CultureInfo.InvariantCulture));

            for (int index = firstIndex; index <= lastIndex; index++)
            {
                builder.Append('\n');
                builder.Append(index == divergenceIndex ? '>' : ' ');
                builder.Append(" 帧 ")
                    .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(": 期望=");

                if (index < expected.Length)
                {
                    builder.Append(expected[index].ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("0（缺失）");
                }

                builder.Append(" 实际=");

                if (index < actual.Length)
                {
                    builder.Append(actual[index].ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append("0（缺失）");
                }
            }

            return builder.ToString();
        }
    }
}
