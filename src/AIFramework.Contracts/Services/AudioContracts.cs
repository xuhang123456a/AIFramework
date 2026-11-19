#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 音频通道。<b>为什么只有四条固定通道</b>：业务只关心"这类声音该多大"，
    /// 不需要知道引擎侧的 AudioMixer 分组、Snapshot 与总线层级——那是适配层的实现细节。
    /// <para>数值必须保持稠密且从 0 开始：适配层用 <c>float[4]</c> 按下标存音量，不建字典（避免播放热路径装箱）。</para>
    /// </summary>
    public enum AudioChannel : byte
    {
        /// <summary>总线：影响所有通道的最终响度。</summary>
        Master = 0,

        /// <summary>背景音乐（单例，同一时刻最多一首）。</summary>
        Bgm = 1,

        /// <summary>短音效（可并发，帧内高频触发）。</summary>
        Sfx = 2,

        /// <summary>语音/对白（通常独占，优先级高于 Sfx）。</summary>
        Voice = 3
    }

    /// <summary>
    /// 音频服务端口：业务只发"播哪个"，不关心引擎音源池/衰减/并发限制。
    /// <para>
    /// <b>参数是逻辑 ID 而不是引擎资源</b>：<c>sfxId</c> / <c>bgmId</c> 由配置表或命名约定映射到具体 AudioClip，
    /// 因此切片里不会出现 <c>AudioClip</c> 类型，也就不会把 <c>UnityEngine</c> 泄漏进纯逻辑层（AIF002）。
    /// </para>
    /// <para>
    /// <b>不抛异常</b>：音频缺失/通道越界属于"表现问题"，实现方只记录日志并降级，
    /// 绝不允许让一个缺失的音效把战斗或 UI 流程打断。
    /// </para>
    /// </summary>
    public interface IAudioService
    {
        /// <summary>播放一次短音效（不阻塞调用方）。</summary>
        /// <param name="sfxId">音效逻辑 ID。</param>
        void PlaySfx(int sfxId);

        /// <summary>播放背景音乐；重复调用视为"切歌"，同一 ID 重复播放不应叠加音轨。</summary>
        /// <param name="bgmId">背景音乐逻辑 ID。</param>
        void PlayBgm(int bgmId);

        /// <summary>停止当前背景音乐（无音乐时静默成功）。</summary>
        void StopBgm();

        /// <summary>设置通道音量。</summary>
        /// <param name="channel">目标通道。</param>
        /// <param name="volume01">线性音量，实现必须夹到 <c>[0,1]</c>，越界值按边界处理。</param>
        void SetVolume(AudioChannel channel, float volume01);

        /// <summary>读回通道音量（<c>[0,1]</c>）。</summary>
        /// <param name="channel">目标通道；越界枚举值返回 0。</param>
        /// <returns>线性音量。</returns>
        float GetVolume(AudioChannel channel);

        /// <summary>当前"正在发声"的音效数量，用于诊断与并发上限验收（不是历史播放次数）。</summary>
        int ActiveVoiceCount { get; }
    }

    /// <summary>
    /// 音频事件（可选）：切片可以广播"该响什么"，由装配层决定播不播。
    /// <para>
    /// <b>为什么做成事件而不是让切片直接调 <see cref="IAudioService"/></b>：切片不该拥有"是否静音/是否在战斗内"
    /// 这类全局策略；广播事实、由装配层（或设置界面）决定播不播，才能做到"删掉音频不影响玩法逻辑"。
    /// </para>
    /// </summary>
    public readonly struct AudioPlayRequestedEvent : IEvent
    {
        /// <summary>构造一条播放请求事实。</summary>
        /// <param name="clipId">音频逻辑 ID（音效与音乐共用同一套 ID 空间）。</param>
        /// <param name="channel">请求播放到的通道。</param>
        public AudioPlayRequestedEvent(int clipId, AudioChannel channel)
        {
            ClipId = clipId;
            Channel = channel;
        }

        /// <summary>音频逻辑 ID。</summary>
        public int ClipId { get; }

        /// <summary>目标通道。</summary>
        public AudioChannel Channel { get; }
    }
}
