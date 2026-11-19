#nullable enable
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using System;
using System.Collections.Generic;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台音频服务：无头环境下验证"音频端口契约"的参考实现。
    /// <para>
    /// <b>它不是空实现</b>：无头测试要能验收并发上限、通道音量、BGM 单例这些**可观测行为**，
    /// 所以这里维护了真实的环形播放日志、定长音源槽与最近 1 秒活跃窗口，
    /// 只是不把声音送到扬声器。行为与 Unity 实现保持同一套语义，切换适配器时验收用例不用改。
    /// </para>
    /// <para>
    /// <b>时间源是可选的</b>：
    /// <list type="bullet">
    /// <item>注入 <see cref="ITimeProvider"/>：<see cref="ActiveVoiceCount"/> = 最近 <see cref="ActiveSfxWindowSeconds"/> 秒内
    /// 起播的音效数（过期自然衰减），适合演示与"音效真的有寿命"的验收。</item>
    /// <item>不注入（<c>null</c>）：没有统一时钟可读，音效视为**持续发声**，
    /// <see cref="ActiveVoiceCount"/> = 已占用音源槽（只受并发上限淘汰最旧那条影响）。
    /// 这不是偷懒：纯逻辑层禁止直接读系统时间（AIF003），没有时钟时任何"按秒过期"都是假精度。</item>
    /// </list>
    /// </para>
    /// <para>线程模型：非线程安全。音频调用与帧推进同线程。</para>
    /// </summary>
    public sealed class ConsoleAudioService : IAudioService
    {
        /// <summary>并发音效上限：超出后丢弃**最早**的那条，而不是拒绝新音效。</summary>
        public const int MaxConcurrentSfx = 16;

        /// <summary>播放日志环形容量（最近 32 条）。</summary>
        public const int LogCapacity = 32;

        /// <summary>活跃音效的时间窗口（秒）；仅在注入了时间源时生效。</summary>
        public const double ActiveSfxWindowSeconds = 1.0;

        /// <summary>
        /// 通道数量。<see cref="AudioChannel"/> 必须是 0..3 稠密枚举：
        /// 适配层用定长数组按下标存音量，加通道要同步改这里（不做字典，避免热路径装箱）。
        /// </summary>
        private const int ChannelCount = 4;

        /// <summary>"没有 BGM 在播"的哨兵值。</summary>
        private const int NoBgm = -1;

        /// <summary>可选时间源；<c>null</c> 时退化为"音效持续发声"模型。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>四通道音量（<c>[0,1]</c>），下标即 <see cref="AudioChannel"/> 数值。</summary>
        private readonly float[] _volumes;

        /// <summary>音效音源槽（定长 <see cref="MaxConcurrentSfx"/>，环形淘汰最旧）。</summary>
        private readonly SfxVoice[] _voices;

        /// <summary>最近播放事件的环形日志（定长 <see cref="LogCapacity"/>）。</summary>
        private readonly string[] _playLog;

        /// <summary>已占用的音源槽数（不超过 <see cref="MaxConcurrentSfx"/>）。</summary>
        private int _voiceCount;

        /// <summary>下一个写入槽位；满员时它同时指向最旧的一条。</summary>
        private int _voiceNext;

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置。</summary>
        private int _logNext;

        /// <summary>当前 BGM 的 ID；<see cref="NoBgm"/> 表示没在播。</summary>
        private int _currentBgmId = NoBgm;

        /// <summary>真正发生过的"换曲"次数（重复播同一首不计数），用于验收 BGM 单例语义。</summary>
        private int _bgmSwitchCount;

        /// <summary>构造音频服务。</summary>
        /// <param name="timeProvider">可选时间源；传 <c>null</c> 表示按"持续发声"模型统计活跃音效。</param>
        public ConsoleAudioService(ITimeProvider? timeProvider = null)
        {
            _time = timeProvider;
            _volumes = new float[ChannelCount];
            _voices = new SfxVoice[MaxConcurrentSfx];
            _playLog = new string[LogCapacity];

            for (int i = 0; i < ChannelCount; i++)
            {
                _volumes[i] = 1f;
            }
        }

        /// <summary>已占用的音效音源槽数量（不受时间窗口影响，用于区分"槽占用"与"活跃"两种口径）。</summary>
        public int VoiceSlotCount
        {
            get { return _voiceCount; }
        }

        /// <summary>当前 BGM 的 ID；没有播放时返回 <see cref="NoBgm"/>（-1）。</summary>
        public int CurrentBgmId
        {
            get { return _currentBgmId; }
        }

        /// <summary>当前是否有 BGM 在播。</summary>
        public bool IsBgmPlaying
        {
            get { return _currentBgmId != NoBgm; }
        }

        /// <summary>累计真正换曲次数（重复播同一首不计；用于验收"不叠加"）。</summary>
        public int BgmSwitchCount
        {
            get { return _bgmSwitchCount; }
        }

        /// <inheritdoc />
        public int ActiveVoiceCount
        {
            get
            {
                ITimeProvider? time = _time;
                if (time == null)
                {
                    // 无时钟：没有"过期"这个概念，只有并发上限会淘汰最旧的一条。
                    return _voiceCount;
                }

                double now = time.ElapsedSeconds;
                int active = 0;

                // 只扫**写过的**槽：未满时有效槽就是前 _voiceCount 个（写入严格从 0 顺推），
                // 满员后 _voiceCount == 长度，等价于全扫。绝不能全扫未写槽——默认 StartSeconds=0
                // 会让"从没播过"的槽在 now 还小于 1 秒时被误算成正在响。
                for (int i = 0; i < _voiceCount; i++)
                {
                    if (_voices[i].StartSeconds + ActiveSfxWindowSeconds > now)
                    {
                        active++;
                    }
                }

                return active;
            }
        }

        /// <inheritdoc />
        public void PlaySfx(int sfxId)
        {
            AppendLog("sfx#" + sfxId);
            OccupyVoice(sfxId);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 同一 ID 重复调用是**空操作**：BGM 是单例，重播只会把正在放的歌从头切一遍，
        /// 听感上是 bug 而不是功能；真要重播请先 <see cref="StopBgm"/>。
        /// 负值等价于 <see cref="StopBgm"/>，方便把配置表里"没有 BGM"直接透传进来。
        /// </remarks>
        public void PlayBgm(int bgmId)
        {
            int requested = bgmId < 0 ? NoBgm : bgmId;
            if (requested == _currentBgmId)
            {
                return;
            }

            if (requested == NoBgm)
            {
                StopBgm();
                return;
            }

            _currentBgmId = requested;
            _bgmSwitchCount++;
            AppendLog("bgm#" + requested);
        }

        /// <inheritdoc />
        public void StopBgm()
        {
            if (_currentBgmId == NoBgm)
            {
                // 幂等：没在播就不写日志，避免"停止"把 32 条环形日志刷满噪声。
                return;
            }

            _currentBgmId = NoBgm;
            AppendLog("bgm stop");
        }

        /// <inheritdoc />
        public void SetVolume(AudioChannel channel, float volume01)
        {
            int index = (int)channel;
            if (index < 0 || index >= ChannelCount)
            {
                // 越界枚举值（(AudioChannel)9）不抛：音频是表现层，绝不允许把流程炸掉。
                return;
            }

            _volumes[index] = Clamp01(volume01);
        }

        /// <inheritdoc />
        public float GetVolume(AudioChannel channel)
        {
            int index = (int)channel;
            if (index < 0 || index >= ChannelCount)
            {
                return 0f;
            }

            return _volumes[index];
        }

        /// <summary>
        /// 取走并清空最近播放日志（按时间顺序返回）。<b>会清空</b>：这是"排空"语义，
        /// 方便冒烟/测试分阶段断言，而不会把同一批事件重复打印两次。
        /// </summary>
        /// <returns>自上次排空以来的播放事件，最多 <see cref="LogCapacity"/> 条。</returns>
        public IReadOnlyList<string> DrainLog()
        {
            var entries = new List<string>(_logCount);
            int start = _logCount < LogCapacity ? 0 : _logNext;
            for (int i = 0; i < _logCount; i++)
            {
                int index = start + i;
                if (index >= LogCapacity)
                {
                    index -= LogCapacity;
                }

                entries.Add(_playLog[index]);
            }

            Array.Clear(_playLog, 0, _playLog.Length);
            _logCount = 0;
            _logNext = 0;
            return entries;
        }

        /// <summary>占用一个音效槽；满员时覆盖最旧的一条（"超出丢弃最早的"）。</summary>
        private void OccupyVoice(int sfxId)
        {
            // 先取局部再判空：字段可能在两次读取之间被重新赋值，可空流分析只认局部副本。
            ITimeProvider? time = _time;
            double startSeconds = time != null ? time.ElapsedSeconds : 0d;
            _voices[_voiceNext] = new SfxVoice(sfxId, startSeconds);

            _voiceNext++;
            if (_voiceNext == _voices.Length)
            {
                _voiceNext = 0;
            }

            if (_voiceCount < _voices.Length)
            {
                _voiceCount++;
            }
        }

        /// <summary>把一条事件写进环形日志；满了覆盖最旧的一条。</summary>
        private void AppendLog(string entry)
        {
            _playLog[_logNext] = entry;

            _logNext++;
            if (_logNext == LogCapacity)
            {
                _logNext = 0;
            }

            if (_logCount < LogCapacity)
            {
                _logCount++;
            }
        }

        /// <summary>夹到 <c>[0,1]</c>；NaN 归 0（NaN 会污染后续混音计算，必须挡在入口）。</summary>
        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value < 0f)
            {
                return 0f;
            }

            if (value > 1f)
            {
                return 1f;
            }

            return value;
        }

        /// <summary>音源槽记录：够诊断"谁在响、什么时候起播"即可。</summary>
        private readonly struct SfxVoice
        {
            public SfxVoice(int clipId, double startSeconds)
            {
                ClipId = clipId;
                StartSeconds = startSeconds;
            }

            /// <summary>音效 ID。</summary>
            public int ClipId { get; }

            /// <summary>起播时刻（秒）；无时间源时为 0。</summary>
            public double StartSeconds { get; }
        }
    }

    /// <summary>
    /// 外围服务自检入口：在无头环境里各打一行，证明"音频端口"和"服务端校验端口"都真的接上了。
    /// <para>
    /// <b>为什么单独做成静态方法而不是塞进 <c>ConsoleAppBootstrap</c></b>：是否接入冒烟主流程由装配层决定，
    /// 这里只提供可独立调用的最小证据，避免为了演示一个新端口去改动已有装配链。
    /// </para>
    /// </summary>
    public static class ConsoleServices
    {
        /// <summary>跑一遍音频与校验服务的自检，各输出一行结果。</summary>
        /// <param name="writer">输出端口。</param>
        public static void RunSelfCheck(IIOWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            // 不注入时间源：自检要的是**可复现**的输出，而不是依赖"两次调用间隔小于 1 秒"这种运气。
            var audio = new ConsoleAudioService();
            audio.SetVolume(AudioChannel.Master, 0.8f);
            audio.SetVolume(AudioChannel.Bgm, 0.6f);
            audio.SetVolume(AudioChannel.Sfx, 2f); // 故意越界，验证夹取
            writer.WriteLine(
                "[Audio] 音量 Master=" + FormatVolume(audio.GetVolume(AudioChannel.Master))
                + " Bgm=" + FormatVolume(audio.GetVolume(AudioChannel.Bgm))
                + " Sfx=" + FormatVolume(audio.GetVolume(AudioChannel.Sfx))
                + " Voice=" + FormatVolume(audio.GetVolume(AudioChannel.Voice)));

            audio.PlayBgm(7);
            audio.PlaySfx(1001);
            audio.PlaySfx(1002);
            IReadOnlyList<string> log = audio.DrainLog();
            writer.WriteLine(
                "[Audio] 播放 BGM=#" + audio.CurrentBgmId
                + " 活跃音效=" + audio.ActiveVoiceCount
                + " 事件=" + JoinEvents(log));

            var ledger = new MatchValidationLedger();
            var normal = new MatchSummary(
                kernelType: 2, levelId: 1, randomSeed: 20261120, frameCount: 600, stateFingerprint: 0x5EEDL, score: 1200);
            ValidationResponse accepted = ledger.Validate(in normal);
            ValidationResponse duplicate = ledger.Validate(in normal);
            var absurd = new MatchSummary(
                kernelType: 2, levelId: 1, randomSeed: 20261120, frameCount: 600, stateFingerprint: 0x5EEEL, score: 6001);
            ValidationResponse rejected = ledger.Validate(in absurd);

            writer.WriteLine(
                "[Validate] 正常=" + accepted.Result + "(分=" + accepted.ServerScore + ")"
                + " 重复=" + duplicate.Result + "(" + duplicate.Reason + ")"
                + " 离谱分数=" + rejected.Result + "(" + rejected.Reason + ")"
                + " 已登记=" + ledger.Count + "/" + ledger.Capacity);
        }

        /// <summary>格式化音量（固定两位小数、固定不变文化，保证 CI 输出可比对）。</summary>
        private static string FormatVolume(float volume)
        {
            return volume.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>把事件列表拼成一行（自检专用，允许分配）。</summary>
        private static string JoinEvents(IReadOnlyList<string> entries)
        {
            if (entries.Count == 0)
            {
                return "无";
            }

            var builder = new System.Text.StringBuilder(entries.Count * 8);
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(entries[i]);
            }

            return builder.ToString();
        }
    }
}
