#nullable enable
using System;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.VoiceOver
{
    /// <summary>
    /// 一条台词在**当前语种**下的展示状态：配置列 + 播放时刻折算出的字幕/配音可用性。
    /// <para>
    /// <see cref="Language"/> 回报的是**实际选中行**的语种，不是当前设置的语种：
    /// 两者不同就说明发生了回退（例如当前日语、该行缺日语 → 用简中兜底），
    /// UI 可据此提示"该语音缺失/请切换语言"，不需要自己再查表。
    /// </para>
    /// <para>
    /// <see cref="HasAudio"/> = 选中行的 <see cref="AudioKey"/> 非空；回退到"任意语种"兜底层时恒为 false
    /// ——串语种播配音比不播更糟（玩家会听到听不懂的语言）。
    /// </para>
    /// <para>
    /// <see cref="HasSubtitle"/> 只在"这条台词就是当前播放行、且进度落在 <c>[startMs, endMs)</c>"时为 true，
    /// 其它台词恒为 false（时间轴只对正在播的那一行有意义）。
    /// </para>
    /// </summary>
    public readonly struct VoiceLineState
    {
        public VoiceLineState(
            int lineId,
            string speaker,
            string textKey,
            string audioKey,
            int language,
            bool hasSubtitle,
            bool hasAudio)
        {
            LineId = lineId;
            Speaker = speaker;
            TextKey = textKey;
            AudioKey = audioKey ?? string.Empty;
            Language = language;
            HasSubtitle = hasSubtitle;
            HasAudio = hasAudio;
        }

        /// <summary>台词 id（配置主键）。</summary>
        public int LineId { get; }

        /// <summary>说话人显示名（取自选中行，允许按语种本地化）。</summary>
        public string Speaker { get; }

        /// <summary>字幕文案键：交给本地化切片解析，本切片只透出键。</summary>
        public string TextKey { get; }

        /// <summary>该语种的配音资源键；空串表示只有字幕没有配音。</summary>
        public string AudioKey { get; }

        /// <summary>实际选中行的语种（0=简中 / 1=英语 / 2=日语）。</summary>
        public int Language { get; }

        /// <summary>此刻是否该显示字幕（仅当前播放行、且进度落在左闭右开区间内）。</summary>
        public bool HasSubtitle { get; }

        /// <summary>此刻是否有配音可播（<see cref="AudioKey"/> 非空，且未落到串语种兜底层）。</summary>
        public bool HasAudio { get; }
    }

    /// <summary>
    /// 语音/字幕全量只读快照：<see cref="Lines"/> 长度 = 去重后的台词数，顺序 = 配置里首次出现顺序。
    /// <para>
    /// <see cref="CurrentLineId"/> 为 <c>0</c>（<see cref="VoiceOverHandler.NoLine"/>）表示还没播过任何台词；
    /// 播完后 <see cref="IsPlaying"/> 变为 false，但 <see cref="CurrentLineId"/> 保留最后一行（UI 需要知道是谁说完了）。
    /// </para>
    /// <para>
    /// <see cref="ElapsedMs"/> 是相对当前行播放起点的毫秒进度：未播放过为 0，
    /// 播放完成后夹到该行的 <c>endMs</c>（不会超过字幕区间右界）。
    /// </para>
    /// </summary>
    public sealed class VoiceOverSnapshot
    {
        public VoiceOverSnapshot(VoiceLineState[] lines, int currentLineId, bool isPlaying, int elapsedMs)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            Lines = lines;
            CurrentLineId = currentLineId;
            IsPlaying = isPlaying;
            ElapsedMs = elapsedMs;
        }

        /// <summary>全部台词的当前语种状态，顺序 = 配置首次出现顺序；空表为空数组。</summary>
        public VoiceLineState[] Lines { get; }

        /// <summary>当前（或最后）播放的台词 id；<c>0</c> 表示还没播过。</summary>
        public int CurrentLineId { get; }

        /// <summary>是否正在播放（完成事件发出后为 false）。</summary>
        public bool IsPlaying { get; }

        /// <summary>相对当前行播放起点的毫秒进度（0 .. 当前行 endMs）。</summary>
        public int ElapsedMs { get; }
    }

    /// <summary>语音/字幕全量查询请求：无参数，返回按当前语种折算的全量快照；只读，不推进进度。</summary>
    public readonly struct VoiceLineQueryRequest : IQuery<VoiceOverSnapshot>
    {
    }
}
