using System;
using System.Collections.Generic;
using AIFramework.Adapters.Console;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="ConsoleAudioService"/> 的行为契约验收。
    /// <para>
    /// 这些用例之所以有意义，是因为无头实现与 Unity 实现被要求**语义一致**：
    /// 并发上限、通道夹取、BGM 单例、活跃声部口径，任何一条在这里改了而 Unity 侧没改，
    /// 就是真机上"听得出来但测不出来"的回归。
    /// </para>
    /// </summary>
    public sealed class ConsoleAudioServiceTests
    {
        [Fact]
        public void PlaySfx_RecordsOrderedPlayEventsInDrainLog()
        {
            var audio = new ConsoleAudioService();

            audio.PlaySfx(1001);
            audio.PlaySfx(1002);

            IReadOnlyList<string> log = audio.DrainLog();

            Assert.Equal(2, log.Count);
            Assert.Equal("sfx#1001", log[0]);
            Assert.Equal("sfx#1002", log[1]);
        }

        [Fact]
        public void PlaySfx_BeyondConcurrency_DropsOldestAndCapsActiveVoices()
        {
            var audio = new ConsoleAudioService();

            for (int i = 0; i < 20; i++)
            {
                audio.PlaySfx(i);
            }

            // 音源槽被 16 条占满，超出的 4 条把最早的顶掉——"丢弃最早"而不是"拒绝最新"。
            Assert.Equal(ConsoleAudioService.MaxConcurrentSfx, audio.VoiceSlotCount);
            Assert.Equal(ConsoleAudioService.MaxConcurrentSfx, audio.ActiveVoiceCount);

            // 播放日志有自己的容量（32），与音源声部数无关，因此 20 条全在。
            Assert.Equal(20, audio.DrainLog().Count);
        }

        [Fact]
        public void DrainLog_KeepsOnlyLatestEvents_AndClearsAfterDrain()
        {
            var audio = new ConsoleAudioService();

            for (int i = 0; i < 40; i++)
            {
                audio.PlaySfx(i);
            }

            IReadOnlyList<string> log = audio.DrainLog();

            Assert.Equal(ConsoleAudioService.LogCapacity, log.Count);
            Assert.Equal("sfx#8", log[0]); // 40 - 32 = 8，前 8 条已被环形覆盖
            Assert.Equal("sfx#39", log[log.Count - 1]);

            // 排空语义：第二次取出必须为空，避免同一批事件被重复打印。
            Assert.Empty(audio.DrainLog());
        }

        [Fact]
        public void SetVolume_AboveOne_ClampsToOne()
        {
            var audio = new ConsoleAudioService();

            audio.SetVolume(AudioChannel.Sfx, 2.5f);

            Assert.Equal(1f, audio.GetVolume(AudioChannel.Sfx));
        }

        [Fact]
        public void SetVolume_BelowZero_ClampsToZero()
        {
            var audio = new ConsoleAudioService();

            audio.SetVolume(AudioChannel.Bgm, -0.3f);

            Assert.Equal(0f, audio.GetVolume(AudioChannel.Bgm));
        }

        [Fact]
        public void SetVolume_NaN_BecomesZeroInsteadOfPoisoningMix()
        {
            var audio = new ConsoleAudioService();

            audio.SetVolume(AudioChannel.Voice, float.NaN);

            Assert.Equal(0f, audio.GetVolume(AudioChannel.Voice));
        }

        [Fact]
        public void SetVolume_OutOfRangeChannel_IsIgnoredWithoutTouchingRealChannels()
        {
            var audio = new ConsoleAudioService();

            audio.SetVolume((AudioChannel)9, 0.5f);

            Assert.Equal(0f, audio.GetVolume((AudioChannel)9));
            Assert.Equal(1f, audio.GetVolume(AudioChannel.Master)); // 未被越界写入污染
        }

        [Fact]
        public void PlayBgm_RepeatedSameId_DoesNotStack()
        {
            var audio = new ConsoleAudioService();

            audio.PlayBgm(7);
            audio.PlayBgm(7);
            audio.PlayBgm(7);

            Assert.True(audio.IsBgmPlaying);
            Assert.Equal(7, audio.CurrentBgmId);
            // 只在第一次真正换曲；重复调用是空操作，不会叠出一条新音轨。
            Assert.Equal(1, audio.BgmSwitchCount);
            Assert.Single(audio.DrainLog());
        }

        [Fact]
        public void PlayBgm_DifferentId_SwitchesTrack()
        {
            var audio = new ConsoleAudioService();

            audio.PlayBgm(1);
            audio.PlayBgm(2);

            Assert.Equal(2, audio.CurrentBgmId);
            Assert.Equal(2, audio.BgmSwitchCount);

            IReadOnlyList<string> log = audio.DrainLog();
            Assert.Equal(2, log.Count);
            Assert.Equal("bgm#1", log[0]);
            Assert.Equal("bgm#2", log[1]);
        }

        [Fact]
        public void StopBgm_ClearsBgm_AndIsIdempotent()
        {
            var audio = new ConsoleAudioService();
            audio.PlayBgm(3);

            audio.StopBgm();

            Assert.False(audio.IsBgmPlaying);
            Assert.Equal(-1, audio.CurrentBgmId);
            Assert.Equal(2, audio.DrainLog().Count); // bgm#3 + bgm stop

            // 幂等：没在播时再停一次不写日志，避免 32 条环形日志被噪声刷满。
            audio.StopBgm();
            Assert.Empty(audio.DrainLog());
        }

        [Fact]
        public void PlayBgm_NegativeId_BehavesLikeStop()
        {
            var audio = new ConsoleAudioService();
            audio.PlayBgm(5);

            audio.PlayBgm(-1);

            Assert.False(audio.IsBgmPlaying);
            Assert.Equal(-1, audio.CurrentBgmId);
        }

        [Fact]
        public void ActiveVoiceCount_WithTimeProvider_ExpiresRightAfterWindow()
        {
            var time = new FakeTimeProvider();
            var audio = new ConsoleAudioService(time);

            audio.PlaySfx(1);
            audio.PlaySfx(2);
            Assert.Equal(2, audio.ActiveVoiceCount);

            // 满窗口即过期（边界取"不再算活跃"，避免浮点抖动导致的 1 声之差）。
            time.Advance(ConsoleAudioService.ActiveSfxWindowSeconds);
            Assert.Equal(0, audio.ActiveVoiceCount);

            audio.PlaySfx(3);
            Assert.Equal(1, audio.ActiveVoiceCount);

            // 槽位口径不受时间窗口影响：三条音效都曾占过槽。
            Assert.Equal(3, audio.VoiceSlotCount);
        }

        /// <summary>可快进的假时间源：无头测试的唯一时间入口（逻辑层禁止直接读系统时钟）。</summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private double _elapsedSeconds;
            private DateTime _utcNow = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            public void Advance(double seconds)
            {
                _elapsedSeconds += seconds;
                _utcNow = _utcNow.AddSeconds(seconds);
            }
        }
    }
}
