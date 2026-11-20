#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.VoiceOver
{
    /// <summary>
    /// 请求播放某一行台词（显式意图）。
    /// <para>
    /// 处理器只做"选中语种行 + 重置进度 + 广播 <see cref="VoiceLineChangedEvent"/> 三件事"：
    /// 不加载音频、不发 <c>IAudioService</c> 调用（见 <c>CONTRACT.md</c> §4），
    /// 以免切片持有"是否静音 / 走哪条音频通道"这类全局策略。
    /// </para>
    /// <para>
    /// 未知 <see cref="LineId"/> 与空表都是静默忽略（不改状态、不发事件）：
    /// 台词 id 来自剧情脚本，配错一行不该让整段演出崩掉；需要逐次结果时用
    /// <see cref="VoiceOverHandler.Play"/> 的返回值。
    /// </para>
    /// </summary>
    public readonly struct VoiceLinePlayCommand : ICommand
    {
        /// <summary>请求播放指定台词。</summary>
        /// <param name="lineId">台词 id（配置主键）。</param>
        public VoiceLinePlayCommand(int lineId)
        {
            LineId = lineId;
        }

        /// <summary>要播放的台词 id。</summary>
        public int LineId { get; }
    }

    /// <summary>
    /// 台词开始播放事实：每次 <c>Play</c>（含重播、切行）广播一次。
    /// <para>
    /// 字段取自**实际选中的那一行**（回退后的语种行），因此 <see cref="Speaker"/> 已经是该语种的名字；
    /// <see cref="StartMs"/> 就是这一行的字幕起点，UI 可以据此先把字幕藏到起点再显示。
    /// </para>
    /// <para>
    /// 事件不携带 audioKey：适配层收到本事件后查询 <see cref="VoiceOverSnapshot"/> 取
    /// <see cref="VoiceLineState.AudioKey"/> 与 <see cref="VoiceLineState.HasAudio"/>，
    /// 再决定调 <c>IAudioService</c> 还是静默跳过——"该播什么"与"能不能播"是两件事。
    /// </para>
    /// </summary>
    public readonly struct VoiceLineChangedEvent : IEvent
    {
        /// <summary>广播一次"开始播放某台词"。</summary>
        /// <param name="lineId">台词 id。</param>
        /// <param name="speaker">选中语种行的说话人名。</param>
        /// <param name="textKey">字幕文案键。</param>
        /// <param name="startMs">该行字幕起点（相对播放起点的毫秒）。</param>
        public VoiceLineChangedEvent(int lineId, string speaker, string textKey, int startMs)
        {
            LineId = lineId;
            Speaker = speaker ?? string.Empty;
            TextKey = textKey ?? string.Empty;
            StartMs = startMs;
        }

        /// <summary>台词 id。</summary>
        public int LineId { get; }

        /// <summary>选中语种行的说话人名。</summary>
        public string Speaker { get; }

        /// <summary>字幕文案键（交给本地化切片解析成显示文本）。</summary>
        public string TextKey { get; }

        /// <summary>该行字幕起点（毫秒，相对播放起点）。</summary>
        public int StartMs { get; }
    }

    /// <summary>
    /// 台词播放完成事实：一行台词的生命周期内**只广播一次**（进度到达 <c>endMs</c> 的那一拍）。
    /// <para>
    /// 广播前 <see cref="VoiceOverSnapshot.IsPlaying"/> 已置 false、<c>ElapsedMs</c> 已夹到 <c>endMs</c>，
    /// 因此订阅方回调里查到的快照就是"播完那一刻"的口径；重复 <c>Tick</c> 不会重复广播。
    /// </para>
    /// <para>
    /// 它是"字幕/配音进度"事件而不是"配音真的放完了"事件：真实音频长度以适配层的音频源为准，
    /// 切片只保证配置的 <c>endMs</c> 边界被跨过一次。
    /// </para>
    /// </summary>
    public readonly struct VoiceLineCompletedEvent : IEvent
    {
        /// <summary>广播一次"某台词播放完成"。</summary>
        /// <param name="lineId">完成播放的台词 id。</param>
        public VoiceLineCompletedEvent(int lineId)
        {
            LineId = lineId;
        }

        /// <summary>完成播放的台词 id。</summary>
        public int LineId { get; }
    }
}
