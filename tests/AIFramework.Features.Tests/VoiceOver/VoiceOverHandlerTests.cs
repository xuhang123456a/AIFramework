#nullable enable
using System;
using AIFramework.Features.VoiceOver;
using Xunit;

namespace AIFramework.Features.Tests.VoiceOver
{
    /// <summary>
    /// 语音/字幕机验收：语种三级回退、字幕时间轴左闭右开、Tick 推进与完成事件只发一次、
    /// 未播放/非正 delta 忽略、切行与重播、快照字段与事件顺序。
    /// </summary>
    public sealed class VoiceOverHandlerTests
    {
        /// <summary>标准夹具：lineId 1 三语种齐全；lineId 2 缺日语，且简中行没有配音（只有字幕）。</summary>
        private static readonly VoiceLineRowSpec[] ThreeLanguageRows =
        {
            new VoiceLineRowSpec(1, "莉亚", "vo.hero.greet", "zh_1", 0, 0, 1500),
            new VoiceLineRowSpec(1, "Lia", "vo.hero.greet", "en_1", 1, 0, 1400),
            new VoiceLineRowSpec(1, "リア", "vo.hero.greet", "ja_1", 2, 120, 1600),
            new VoiceLineRowSpec(2, "铁匠", "vo.smith.repair", string.Empty, 0, 300, 2100),
            new VoiceLineRowSpec(2, "Blacksmith", "vo.smith.repair", "en_2", 1, 200, 2000),
        };

        /// <summary>单行夹具：字幕窗口 100..600，用于边界（左闭右开）断言。</summary>
        private static readonly VoiceLineRowSpec[] WindowRows =
        {
            new VoiceLineRowSpec(5, "旁白", "vo.window", "zh_5", 0, 100, 600),
        };

        [Fact]
        public void Snapshot_EmptyTable_ReturnsEmptySnapshot()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.CreateWithPayload(
                0, VoiceLineTableStub.BuildPayload(new VoiceLineRowSpec[0]));

            VoiceOverSnapshot snapshot = harness.Snapshot();

            Assert.Empty(snapshot.Lines);
            Assert.Equal(VoiceOverHandler.NoLine, snapshot.CurrentLineId);
            Assert.False(snapshot.IsPlaying);
            Assert.Equal(0, snapshot.ElapsedMs);
            Assert.Equal(0, harness.Handler.LineCount);
        }

        [Fact]
        public void Query_BeforeAnyPlay_ReportsNoLineAndNotPlaying()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);

            VoiceOverSnapshot snapshot = harness.Snapshot();

            Assert.Equal(2, snapshot.Lines.Length);
            Assert.Equal(VoiceOverHandler.NoLine, snapshot.CurrentLineId);
            Assert.False(snapshot.IsPlaying);
            Assert.Equal(0, snapshot.ElapsedMs);
            Assert.Equal(1, harness.Bus.CountQueried<VoiceLineQueryRequest>());
        }

        [Fact]
        public void Snapshot_ListsDistinctLineIdsInFirstAppearanceOrder()
        {
            // 行顺序 2 → 1 → 2：快照按 lineId 首次出现顺序输出，且同一 lineId 只出一条。
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(
                new VoiceLineRowSpec(2, "铁匠", "vo.smith", "b_0", 0, 0, 500),
                new VoiceLineRowSpec(1, "莉亚", "vo.hero", "a_0", 0, 0, 500),
                new VoiceLineRowSpec(2, "Blacksmith", "vo.smith", "b_1", 1, 0, 500));

            VoiceOverSnapshot snapshot = harness.Snapshot();

            Assert.Equal(2, snapshot.Lines.Length);
            Assert.Equal(2, snapshot.Lines[0].LineId);
            Assert.Equal(1, snapshot.Lines[1].LineId);
            Assert.Equal(2, harness.Handler.LineCount);
        }

        [Fact]
        public void Play_PublishesChangedEventWithResolvedRowFields()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);

            harness.Play(1);

            Assert.Single(harness.Events.Changed);
            VoiceLineChangedEvent first = harness.Events.Changed[0];
            Assert.Equal(1, first.LineId);
            Assert.Equal("莉亚", first.Speaker);
            Assert.Equal("vo.hero.greet", first.TextKey);
            Assert.Equal(0, first.StartMs);

            Assert.Empty(harness.Events.Completed);
            Assert.True(harness.Handler.IsPlaying);
            Assert.Equal(1, harness.Handler.CurrentLineId);
            Assert.Equal(0, harness.Handler.ElapsedMs);
        }

        [Fact]
        public void Play_UnknownLine_LeavesStateUntouched()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);
            harness.Play(1);
            harness.Handler.Tick(100);

            harness.Play(999);

            Assert.Equal(1, harness.Handler.CurrentLineId);
            Assert.Equal(100, harness.Handler.ElapsedMs);
            Assert.True(harness.Handler.IsPlaying);
            Assert.Single(harness.Events.Changed);
            Assert.Empty(harness.Events.Completed);
        }

        [Fact]
        public void Play_EmptyTable_ReturnsFalse()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.CreateWithPayload(
                0, VoiceLineTableStub.BuildPayload(new VoiceLineRowSpec[0]));

            Assert.False(harness.Handler.Play(1));
            Assert.Equal(VoiceOverHandler.NoLine, harness.Handler.CurrentLineId);
            Assert.False(harness.Handler.IsPlaying);
            Assert.Empty(harness.Events.Events);
        }

        [Fact]
        public void Play_CurrentLanguageHit_UsesThatLanguageRow()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(1, ThreeLanguageRows);

            harness.Play(1);

            Assert.Equal("Lia", harness.Events.Changed[0].Speaker);
            VoiceLineState line = harness.Line(harness.Snapshot(), 1);
            Assert.Equal(1, line.Language);
            Assert.Equal("en_1", line.AudioKey);
        }

        [Fact]
        public void Play_MissingCurrentLanguage_FallsBackToDefaultLanguage()
        {
            // 当前日语，但 lineId 2 没有日语行 → 回退简中（默认语种），字幕/配音口径也随简中行。
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(2, ThreeLanguageRows);

            harness.Play(2);

            VoiceLineChangedEvent changed = harness.Events.Changed[0];
            Assert.Equal("铁匠", changed.Speaker);
            Assert.Equal(300, changed.StartMs);

            VoiceOverSnapshot snapshot = harness.Snapshot();
            VoiceLineState line = harness.Line(snapshot, 2);
            Assert.Equal(0, line.Language);
            Assert.Equal(string.Empty, line.AudioKey);
            Assert.False(line.HasAudio);

            // 简中行的字幕窗口是 300..2100：进度刚到起点就该显示。
            harness.Handler.Tick(300);
            Assert.True(harness.Line(harness.Snapshot(), 2).HasSubtitle);
        }

        [Fact]
        public void Play_NoCurrentOrDefaultLanguage_ReturnsRowWithBothFlagsFalse()
        {
            // 只有日语行的台词 + 当前英语 + 默认简中也缺 → 只能给元数据，绝不能播日语配音。
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(
                1,
                new VoiceLineRowSpec(9, "ナレーター", "vo.ja_only", "ja_9", 2, 0, 900));

            harness.Play(9);

            Assert.Equal("ナレーター", harness.Events.Changed[0].Speaker);

            harness.Handler.Tick(100);
            VoiceOverSnapshot snapshot = harness.Snapshot();
            VoiceLineState line = harness.Line(snapshot, 9);
            Assert.Equal(2, line.Language);
            Assert.False(line.HasAudio);
            Assert.False(line.HasSubtitle);
            Assert.Equal(100, snapshot.ElapsedMs);
        }

        [Fact]
        public void Subtitle_BeforeStart_Hidden()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);
            harness.Play(5);

            harness.Handler.Tick(99);

            VoiceOverSnapshot snapshot = harness.Snapshot();
            Assert.Equal(99, snapshot.ElapsedMs);
            Assert.False(harness.Line(snapshot, 5).HasSubtitle);
        }

        [Fact]
        public void Subtitle_AtStart_Visible()
        {
            // 左闭：进度正好等于 startMs 时字幕就该出现。
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);
            harness.Play(5);

            harness.Handler.Tick(100);

            Assert.True(harness.Line(harness.Snapshot(), 5).HasSubtitle);
        }

        [Fact]
        public void Subtitle_AtEnd_HiddenAndCompleted()
        {
            // 右开：进度等于 endMs 时字幕已消失，同拍广播完成。
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);
            harness.Play(5);

            harness.Handler.Tick(600);

            VoiceOverSnapshot snapshot = harness.Snapshot();
            Assert.Equal(600, snapshot.ElapsedMs);
            Assert.False(snapshot.IsPlaying);
            Assert.False(harness.Line(snapshot, 5).HasSubtitle);
            Assert.Single(harness.Events.Completed);
            Assert.Equal(5, harness.Events.Completed[0].LineId);
        }

        [Fact]
        public void Tick_AdvancesElapsedMsWithoutCompletion()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);
            harness.Play(5);

            harness.Handler.Tick(250);

            VoiceOverSnapshot snapshot = harness.Snapshot();
            Assert.Equal(250, snapshot.ElapsedMs);
            Assert.True(snapshot.IsPlaying);
            Assert.True(harness.Line(snapshot, 5).HasSubtitle);
            Assert.Empty(harness.Events.Completed);
        }

        [Fact]
        public void Tick_CompletedEventFiresOnlyOnce()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);
            harness.Play(5);

            harness.Handler.Tick(500);
            Assert.Empty(harness.Events.Completed);

            harness.Handler.Tick(200);
            Assert.Single(harness.Events.Completed);

            // 播放态已结束：后续 Tick 被"未播放"分支挡住，完成事件不会再发。
            harness.Handler.Tick(1000);
            Assert.Single(harness.Events.Completed);
            Assert.Equal(600, harness.Handler.ElapsedMs);
        }

        [Fact]
        public void Tick_WhenNotPlaying_IsIgnored()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);

            harness.Handler.Tick(100);
            Assert.Equal(VoiceOverHandler.NoLine, harness.Handler.CurrentLineId);
            Assert.Equal(0, harness.Handler.ElapsedMs);
            Assert.Empty(harness.Events.Events);

            harness.Play(5);
            harness.Handler.Tick(600);
            harness.Handler.Tick(100);
            Assert.Equal(600, harness.Handler.ElapsedMs);
            Assert.Single(harness.Events.Completed);
        }

        [Fact]
        public void Tick_NonPositiveDelta_IsIgnored()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);
            harness.Play(5);

            harness.Handler.Tick(0);
            harness.Handler.Tick(-300);

            Assert.Equal(0, harness.Handler.ElapsedMs);
            Assert.True(harness.Handler.IsPlaying);
            Assert.Empty(harness.Events.Completed);
        }

        [Fact]
        public void Tick_HugeDelta_ClampsElapsedToEndMs()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);
            harness.Play(5);

            harness.Handler.Tick(int.MaxValue);

            Assert.Equal(600, harness.Handler.ElapsedMs);
            Assert.False(harness.Handler.IsPlaying);
            Assert.Single(harness.Events.Completed);
        }

        [Fact]
        public void Play_AnotherLine_ResetsElapsedAndPublishesChangedAgain()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);
            harness.Play(1);
            harness.Handler.Tick(300);

            harness.Play(2);

            VoiceOverSnapshot snapshot = harness.Snapshot();
            Assert.Equal(2, snapshot.CurrentLineId);
            Assert.Equal(0, snapshot.ElapsedMs);
            Assert.True(snapshot.IsPlaying);
            Assert.Equal(2, harness.Events.Changed.Count);
            Assert.Equal(2, harness.Events.Changed[1].LineId);
            Assert.Empty(harness.Events.Completed);
        }

        [Fact]
        public void Play_SameLine_RestartsTimeline()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);
            harness.Play(1);
            harness.Handler.Tick(900);

            harness.Play(1);

            Assert.Equal(0, harness.Handler.ElapsedMs);
            Assert.True(harness.Handler.IsPlaying);
            Assert.Equal(2, harness.Events.Changed.Count);
            Assert.Empty(harness.Events.Completed);
        }

        [Fact]
        public void Snapshot_EmptyAudioKey_HasAudioFalse()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);

            VoiceLineState line = harness.Line(harness.Snapshot(), 2);

            Assert.Equal(string.Empty, line.AudioKey);
            Assert.False(line.HasAudio);
            Assert.Equal("铁匠", line.Speaker);
            Assert.Equal("vo.smith.repair", line.TextKey);
        }

        [Fact]
        public void Snapshot_AudioKeyAndHasAudio_ForCurrentLanguageRow()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(1, ThreeLanguageRows);

            VoiceLineState line = harness.Line(harness.Snapshot(), 1);

            Assert.Equal("en_1", line.AudioKey);
            Assert.True(line.HasAudio);
            Assert.Equal(1, line.Language);
        }

        [Fact]
        public void EventOrder_ChangedBeforeCompleted()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(WindowRows);

            harness.Play(5);
            harness.Handler.Tick(600);

            Assert.Equal(2, harness.Events.Events.Count);
            Assert.IsType<VoiceLineChangedEvent>(harness.Events.Events[0]);
            Assert.IsType<VoiceLineCompletedEvent>(harness.Events.Events[1]);
        }

        [Fact]
        public void SetLanguage_InvalidOrSameValue_ReturnsFalse()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);

            Assert.False(harness.Handler.SetLanguage(0));
            Assert.False(harness.Handler.SetLanguage(3));
            Assert.False(harness.Handler.SetLanguage(-1));
            Assert.Equal(0, harness.Handler.CurrentLanguage);
        }

        [Fact]
        public void SetLanguage_ChangesResolutionForNextPlay()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);
            harness.Play(1);
            Assert.Equal("莉亚", harness.Events.Changed[0].Speaker);

            Assert.True(harness.Handler.SetLanguage(1));
            harness.Play(1);

            Assert.Equal(1, harness.Handler.CurrentLanguage);
            Assert.Equal("Lia", harness.Events.Changed[1].Speaker);
            Assert.Equal(1, harness.Line(harness.Snapshot(), 1).Language);
        }

        [Fact]
        public void LanguageSwitchDuringPlayback_KeepsPlayTimeRow()
        {
            // 播放中切语种：已在播的行不换轨（否则语音与字幕时间轴会跳变）。
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);
            harness.Play(1);
            harness.Handler.Tick(200);

            Assert.True(harness.Handler.SetLanguage(2));
            VoiceOverSnapshot snapshot = harness.Snapshot();
            VoiceLineState current = harness.Line(snapshot, 1);

            Assert.Equal(1, snapshot.CurrentLineId);
            Assert.Equal(0, current.Language);
            Assert.Equal("zh_1", current.AudioKey);
            Assert.True(current.HasSubtitle);
            Assert.Equal(200, snapshot.ElapsedMs);
        }

        [Fact]
        public void Register_ExposesQueryAndCommandOnBus()
        {
            VoiceOverTestHarness harness = VoiceOverTestHarness.Create(ThreeLanguageRows);

            harness.Play(1);
            VoiceOverSnapshot snapshot = harness.Snapshot();

            Assert.Equal(1, harness.Bus.CountSent<VoiceLinePlayCommand>());
            Assert.Equal(1, harness.Bus.CountQueried<VoiceLineQueryRequest>());
            Assert.Equal(1, snapshot.CurrentLineId);
            Assert.Single(harness.Events.Changed);
        }

        [Fact]
        public void Constructor_InvalidLanguage_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new VoiceOverHandler(
                    new AIFramework.Features.Tests.Fakes.RecordingBus(),
                    VoiceLineTableStub.WithRows(ThreeLanguageRows),
                    3));
        }

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(
                () => new VoiceOverHandler(null!, VoiceLineTableStub.WithRows(ThreeLanguageRows)));

            Assert.Throws<ArgumentNullException>(
                () => new VoiceOverHandler(new AIFramework.Features.Tests.Fakes.RecordingBus(), null!));
        }
    }
}
