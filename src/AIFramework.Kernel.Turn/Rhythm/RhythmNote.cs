#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Rhythm
{
    /// <summary>
    /// 内核内部的音符状态（可变，定长数组的元素类型）：把只读契约
    /// <see cref="RhythmNoteView"/> 与"判定是否已落账"拆开，避免每次判定都要额外查一张表。
    /// <para>
    /// <b>为什么是可变结构体</b>：谱面在构造期一次填满，之后每判定一个音符只改两个字段
    /// （<see cref="Judgement"/> / <see cref="Resolved"/>），没有任何增删；用结构体数组可以让
    /// <see cref="RhythmWorld.Tick"/> 的漏打扫描保持零分配、且对缓存友好。
    /// </para>
    /// <para>
    /// <b>未判定的哨兵值</b>：与契约里 <see cref="RhythmNoteView"/> 的约定一致，未判定时
    /// <see cref="Judgement"/> 是 <c>(RhythmJudgement)byte.MaxValue</c>。
    /// <see cref="Resolved"/> 这个布尔量是内核内部真值（不依赖对哨兵值的比较），
    /// 对外构造视图时才折算成哨兵值。
    /// </para>
    /// </summary>
    internal struct RhythmNote
    {
        /// <summary>"尚未判定"哨兵值，必须与 <see cref="RhythmNoteView"/> 的私有常量保持一致。</summary>
        internal const RhythmJudgement UnresolvedJudgement = (RhythmJudgement)byte.MaxValue;

        internal RhythmNote(int noteId, int lane, double hitTimeSeconds)
        {
            NoteId = noteId;
            Lane = lane;
            HitTimeSeconds = hitTimeSeconds;
            Judgement = UnresolvedJudgement;
            Resolved = false;
        }

        /// <summary>音符序号（0 起、按时间升序）。</summary>
        internal int NoteId;

        /// <summary>轨道下标 <c>0..3</c>。</summary>
        internal int Lane;

        /// <summary>应该被击打的歌曲时刻（秒）。</summary>
        internal double HitTimeSeconds;

        /// <summary>判定结果；未判定时是 <see cref="UnresolvedJudgement"/>。</summary>
        internal RhythmJudgement Judgement;

        /// <summary>是否已结算（命中或漏打）。内核内部真值，判定只写一次。</summary>
        internal bool Resolved;
    }
}
