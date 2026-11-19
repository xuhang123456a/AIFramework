#nullable enable
namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 一次击打的判定等级。枚举值同时是<b>存档/回放里的稳定数值</b>（不要重排），
    /// 也是 <c>NoteCount</c> 之外的准确率权重来源（Perfect 1 / Great 0.8 / Good 0.5 / Miss 0）。
    /// </summary>
    public enum RhythmJudgement : byte
    {
        /// <summary>完美：<c>|SongTime - HitTime| ≤ 0.045s</c>。</summary>
        Perfect = 0,

        /// <summary>优秀：<c>0.045s &lt; |delta| ≤ 0.09s</c>。</summary>
        Great = 1,

        /// <summary>良好：<c>0.09s &lt; |delta| ≤ 0.14s</c>。</summary>
        Good = 2,

        /// <summary>漏打：<c>|delta| &gt; 0.14s</c> 仍未被击打，由内核在时间越过判定窗后自动结算。</summary>
        Miss = 3
    }

    /// <summary>
    /// 单个音符的只读快照（值类型）：渲染层按帧调用 <see cref="IRhythmReadView.GetNote"/> 拿到它，
    /// 不持有、也不能改写内核里的定长音符数组。
    /// <para>
    /// <b>"未判定"如何表达</b>：契约只给了四个判定值，没有第五个"未判定"枚举成员，而
    /// <see cref="Judgement"/> 又必须同时承载"还没结算"与"四个判定之一"。因此内核在音符尚未被击打、
    /// 也还没漏过判定窗时，把 <see cref="Judgement"/> 填成<b>超出枚举定义范围的哨兵值
    /// <c>(RhythmJudgement)byte.MaxValue</c></b>，<see cref="IsResolved"/> 就定义为
    /// <c>Judgement != (RhythmJudgement)byte.MaxValue</c>。
    /// </para>
    /// <para>
    /// <b>调用方约定</b>：不要自己 <c>switch</c> <see cref="Judgement"/> 猜"未判定"——先问
    /// <see cref="IsResolved"/>；<c>switch</c> 落在 0..3 之外的哨兵值时走 <c>default</c> 分支即可
    /// （就等同于"还没判定"）。构造侧只有内核会填哨兵值，外部正常构造四个判定值即可。
    /// </para>
    /// </summary>
    public readonly struct RhythmNoteView
    {
        /// <summary>"尚未判定"哨兵值：<c>(RhythmJudgement)byte.MaxValue</c>，契约文档里明文写死。</summary>
        private const RhythmJudgement UnresolvedJudgement = (RhythmJudgement)byte.MaxValue;

        public RhythmNoteView(int noteId, int lane, double hitTimeSeconds, RhythmJudgement judgement)
        {
            NoteId = noteId;
            Lane = lane;
            HitTimeSeconds = hitTimeSeconds;
            Judgement = judgement;
        }

        /// <summary>音符序号（0 起、按 <see cref="HitTimeSeconds"/> 升序，等于 <see cref="IRhythmReadView.GetNote"/> 的下标）。</summary>
        public int NoteId { get; }

        /// <summary>轨道下标 <c>0..3</c>（<see cref="IRhythmReadView.LaneCount"/> 固定为 4）。</summary>
        public int Lane { get; }

        /// <summary>该音符"应该被击打"的歌曲时刻（秒）。</summary>
        public double HitTimeSeconds { get; }

        /// <summary>
        /// 判定结果；尚未判定时是哨兵值 <c>(RhythmJudgement)byte.MaxValue</c>（见类型注释）。
        /// </summary>
        public RhythmJudgement Judgement { get; }

        /// <summary>是否已经被结算过（击打命中，或被内核判为漏打）。</summary>
        public bool IsResolved
        {
            get { return Judgement != UnresolvedJudgement; }
        }
    }

    /// <summary>
    /// 音游只读视图：渲染层每帧只读这里，不碰内核内部音符数组。
    /// <para>
    /// <b>时间口径</b>：<see cref="SongTime"/> 是内核自己的歌曲时钟，由
    /// <see cref="IRhythmInput.Tick"/> 推进；它与音符的 <see cref="RhythmNoteView.HitTimeSeconds"/>
    /// 之差就是判定用的 <c>delta</c>。视图不暴露"帧率/节拍"这类表现层参数。
    /// </para>
    /// <para>
    /// <b>统计口径</b>：四个计数只统计<b>已结算</b>的音符，因此
    /// <c>PerfectCount + GreatCount + GoodCount + MissCount</c> 恒等于"已结算音符数"（≤ <see cref="NoteCount"/>）；
    /// <see cref="IsFinished"/> 成立时它等于 <see cref="NoteCount"/>。
    /// 判定窗外的空击只会断连击，不消耗音符、也不改任何计数。
    /// </para>
    /// </summary>
    public interface IRhythmReadView
    {
        /// <summary>当前歌曲时间（秒）。对局开始为 0，只增不减。</summary>
        double SongTime { get; }

        /// <summary>轨道数（固定 4）。</summary>
        int LaneCount { get; }

        /// <summary>本谱面的音符总数（谱面 1 = 30，谱面 2 = 50）。</summary>
        int NoteCount { get; }

        /// <summary>按时间升序取第 <paramref name="index"/> 个音符快照（0 起）。越界抛 <c>ArgumentOutOfRangeException</c>。</summary>
        RhythmNoteView GetNote(int index);

        /// <summary>当前连击数：命中 +1，漏打或空击归零。</summary>
        int Combo { get; }

        /// <summary>本局最高连击（只增不减）。</summary>
        int MaxCombo { get; }

        /// <summary>Perfect 命中数。</summary>
        int PerfectCount { get; }

        /// <summary>Great 命中数。</summary>
        int GreatCount { get; }

        /// <summary>Good 命中数。</summary>
        int GoodCount { get; }

        /// <summary>漏打数（只由内核在时间越过判定窗后自动结算，不含判定窗外的空击）。</summary>
        int MissCount { get; }

        /// <summary>
        /// 准确率 <c>0..1</c>：<c>(Perfect + Great*0.8 + Good*0.5) / 已结算音符数</c>。
        /// <para>分母只算已结算音符（未判定音符不计入分母，规则如此）；还没有任何结算时返回 <c>0</c>。</para>
        /// </summary>
        double Accuracy { get; }

        /// <summary>对局是否结束（歌曲时间越过曲长且所有音符已结算，或结算报告已取回）。</summary>
        bool IsFinished { get; }
    }

    /// <summary>
    /// 音游输入端口：适配层只能通过它提交意图，不能直接改状态。
    /// <para>对局结束后一律返回 <see cref="RhythmJudgement.Miss"/> / 空操作，不抛异常。</para>
    /// </summary>
    public interface IRhythmInput
    {
        /// <summary>时间推进（秒）。由适配层按真实帧时间驱动。</summary>
        void Tick(double deltaSeconds);

        /// <summary>在某条轨道上击打：从该轨道未判定的音符里挑时间最近的；超出判定窗口返回 Miss 且不消耗输入。</summary>
        RhythmJudgement Hit(int lane);
    }
}
