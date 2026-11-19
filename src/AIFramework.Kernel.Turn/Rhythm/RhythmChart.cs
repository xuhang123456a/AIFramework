#nullable enable
namespace AIFramework.Kernel.Turn.Rhythm
{
    /// <summary>
    /// 内置谱面表：把"歌曲有多长、有几个音符、每个音符在哪条轨道、落在哪一刻"收在一处。
    /// <para>
    /// <b>谱面后续由配置表 / Luban 提供</b>：这里的两首歌是 Phase 1 的硬编码样例，
    /// 目的是让内核在没有资源管线、没有音频文件、没有引擎时钟的情况下也能跑通
    /// "时间轴 + 判定窗口 + 连击/准确率"。接配置表时只需要把
    /// <see cref="Build"/> 换成"读 <c>RhythmChartRow[]</c> 并写入 <see cref="RhythmNote"/> 缓冲"，
    /// 内核其余部分（时间推进、判定、连击、结算）一行都不用改。
    /// </para>
    /// <para>
    /// <b>为什么"固定 + 种子微调"而不是纯随机</b>：纯随机会让同一个 <c>LevelId</c> 在不同种子下
    /// 长出完全不同的密度，既不可读也不可断言；纯固定又让 <c>GameplayInitData.RandomSeed</c>
    /// 变成摆设。折中方案是——节拍骨架（间隔、轨道步进、曲长、音符数）硬编码，只有
    /// <b>每个间隔 ±0.012s 的抖动</b>（3 步 × 0.004s）与 <b>轨道偏移 0/1</b> 由种子决定：
    /// 同种子逐位可复现，异种子肉眼可见不同，且任意两个相邻音符的间距恒 ≥ 0.42s，
    /// 远大于判定窗（0.14s）的两倍，因此"同一轨道最近音符"永远唯一。
    /// </para>
    /// <para>
    /// <b>时长口径</b>：曲长（15s / 25s）是硬编码常量而不是"最后一个音符 + 尾巴"，
    /// 这样谱面重排不会悄悄改变 <see cref="RhythmWorld.IsFinished"/> 的时间点。
    /// 抖动的最坏情况已验算：谱面 1 末音符 ≤ 13.848s、谱面 2 末音符 ≤ 24.588s，
    /// 相对曲长仍留下 &gt; 0.14s 的尾巴，保证"曲终 ⇒ 所有音符都已结算"。
    /// </para>
    /// <para>
    /// <b>零分配</b>：本类只有静态方法，<see cref="Build"/> 只写调用方预先分配的定长数组，
    /// 自身不申请托管堆内存。
    /// </para>
    /// </summary>
    internal static class RhythmChart
    {
        /// <summary>轨道数（固定 4，与 <c>IRhythmReadView.LaneCount</c> 同源）。</summary>
        internal const int LaneCount = 4;

        /// <summary>内置谱面数量（1/2 号，其它 <c>LevelId</c> 一律夹到 1）。</summary>
        internal const int ChartCount = 2;

        /// <summary>所有内置谱面里最大的音符数（内核据此分配定长数组）。</summary>
        internal const int MaxNoteCount = 50;

        /// <summary>谱面 1 的音符数。</summary>
        private const int ChartOneNoteCount = 30;

        /// <summary>谱面 2 的音符数。</summary>
        private const int ChartTwoNoteCount = 50;

        /// <summary>谱面 1 的曲长（秒）。</summary>
        private const double ChartOneSongLengthSeconds = 15.0;

        /// <summary>谱面 2 的曲长（秒）。</summary>
        private const double ChartTwoSongLengthSeconds = 25.0;

        /// <summary>谱面 1 的基准音符间隔（秒）：慢歌，0.45s 一拍。</summary>
        private const double ChartOneBeatSeconds = 0.45;

        /// <summary>谱面 2 的基准音符间隔（秒）：快歌，0.48s 一拍但更密集。</summary>
        private const double ChartTwoBeatSeconds = 0.48;

        /// <summary>谱面 1 的轨道步进（模 4 循环走 0/3/2/1，视觉上左右横跳）。</summary>
        private const int ChartOneLaneStride = 3;

        /// <summary>谱面 2 的轨道步进（模 4 循环走 0/1/2/3，视觉上顺时针爬升）。</summary>
        private const int ChartTwoLaneStride = 1;

        /// <summary>间隔抖动的最小步长（秒）。</summary>
        private const double JitterStepSeconds = 0.004;

        /// <summary>间隔抖动的步数范围：<c>-3..+3</c>，即 ±0.012s。</summary>
        private const int JitterSteps = 3;

        /// <summary>轨道微调的取值数：偏移 <c>0</c> 或 <c>1</c>。</summary>
        private const int LaneJitterRange = 2;

        /// <summary>把任意外部 <c>LevelId</c> 夹到内置谱面：只有 <c>2</c> 选谱面 2，其余（含 0/9/负数）一律谱面 1。</summary>
        internal static int NormalizeChartId(int levelId)
        {
            return levelId == 2 ? 2 : 1;
        }

        /// <summary>谱面的音符数。</summary>
        internal static int NoteCount(int chartId)
        {
            return chartId == 2 ? ChartTwoNoteCount : ChartOneNoteCount;
        }

        /// <summary>谱面的曲长（秒）。</summary>
        internal static double SongLengthSeconds(int chartId)
        {
            return chartId == 2 ? ChartTwoSongLengthSeconds : ChartOneSongLengthSeconds;
        }

        /// <summary>
        /// 生成谱面写入 <paramref name="buffer"/>（容量必须 ≥ <see cref="NoteCount"/>），返回音符数。
        /// <para>
        /// 时间轴：<c>t[i] = t[i-1] + beat + jitter[i]</c>（<c>t[0] = beat + jitter[0]</c>），
        /// 轨道：<c>lane[i] = (i * stride + laneJitter[i]) &amp; 3</c>。
        /// 每个音符固定消耗两次随机（间隔抖动、轨道微调），取样节奏与 <paramref name="chartId"/> 无关，
        /// 因此谱面 1/2 用同一种子时前者的随机数使用方式也不会被后者影响。
        /// </para>
        /// </summary>
        internal static int Build(int chartId, int seed, RhythmNote[] buffer)
        {
            int count = NoteCount(chartId);
            double beat = chartId == 2 ? ChartTwoBeatSeconds : ChartOneBeatSeconds;
            int laneStride = chartId == 2 ? ChartTwoLaneStride : ChartOneLaneStride;
            RhythmRandom random = new RhythmRandom(seed);

            double time = 0.0;
            for (int i = 0; i < count; i++)
            {
                int jitterSteps = random.NextIndex((JitterSteps * 2) + 1) - JitterSteps;
                time += beat + (jitterSteps * JitterStepSeconds);

                int laneJitter = random.NextIndex(LaneJitterRange);
                int lane = ((i * laneStride) + laneJitter) & (LaneCount - 1);

                buffer[i] = new RhythmNote(i, lane, time);
            }

            return count;
        }
    }
}
