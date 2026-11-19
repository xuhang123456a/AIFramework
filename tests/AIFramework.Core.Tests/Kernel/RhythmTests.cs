using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Kernel.Turn.Rhythm;
using Xunit;

namespace AIFramework.Core.Tests.Kernel
{
    /// <summary>
    /// <see cref="RhythmWorld"/> 的音游契约：谱面 1/2（30 音符 / 15s 与 50 音符 / 25s）与 <c>LevelId</c> 夹取、
    /// <c>Tick</c> 推进歌曲时钟、四个判定窗口的<b>上下取整边界</b>、连击/最高连击/四类计数、
    /// 空击不消耗音符、漏打自动结算、<c>IsFinished</c> 的双条件、<c>AutoPlay</c> 全 Perfect、
    /// 加权准确率、结算门禁/胜负/战利品、<c>Step</c> 计数，以及"预热后 1000 次 Tick + Hit 零分配"。
    /// <para>
    /// <b>为什么不硬编码音符时间</b>：谱面是"固定骨架 + 种子微调"生成的，硬编码某条时间既脆弱又会把测试
    /// 变成"抄实现"。本文件一律从 <see cref="IRhythmReadView.GetNote"/> 读出真实音符，再按"相对该音符的偏差"
    /// 构造场景——于是断言的是<b>规则</b>（窗口、连击、准确率），而不是某颗子落在 0.45 还是 0.456。
    /// </para>
    /// <para>
    /// <b>边界用例怎么造</b>：0.045/0.09/0.14 都不是二进制可精确表示的小数，<c>hit + 0.045</c> 一定落在窗口内或窗口外
    /// 的某一侧（取决于舍入方向）。因此 <see cref="AdvanceToOffset"/> 会用 <c>Math.BitIncrement</c>/<c>BitDecrement</c>
    /// 逐 ulp 微调落点，直到<b>实际算出的 <c>|SongTime - HitTime|</c></b> 明确位于期望一侧，再把该实际偏差交给断言。
    /// 这样测的是"窗口是否含边界"，不会因为一个 ulp 的舍入把用例变成随机红。
    /// </para>
    /// </summary>
    public sealed class RhythmTests
    {
        private const int Seed = 20261120;

        private const int ChartOneNotes = 30;

        private const int ChartTwoNotes = 50;

        private const double ChartOneSongLength = 15.0;

        private const double ChartTwoSongLength = 25.0;

        private const double PerfectWindow = 0.045;

        private const double GreatWindow = 0.09;

        private const double GoodWindow = 0.14;

        private const double FrameSeconds = 1.0 / 60.0;

        /// <summary>推演/循环的安全上限，防止行为回归时测试挂死。</summary>
        private const int LoopGuard = 100000;

        // ── 构造后读数与谱面 1/2 ───────────────────────────────────────

        [Fact]
        public void Constructor_ChartOne_IsThirtyNotesOverFifteenSeconds()
        {
            RhythmWorld world = CreateWorld(1);

            Assert.Equal(1, world.LevelId);
            Assert.Equal(4, world.LaneCount);
            Assert.Equal(ChartOneNotes, world.NoteCount);
            Assert.Equal(ChartOneSongLength, world.SongLengthSeconds);
            Assert.Equal(0.0, world.SongTime);
            Assert.Equal(0, world.Combo);
            Assert.Equal(0, world.MaxCombo);
            Assert.Equal(0, world.PerfectCount);
            Assert.Equal(0, world.GreatCount);
            Assert.Equal(0, world.GoodCount);
            Assert.Equal(0, world.MissCount);
            Assert.Equal(0.0, world.Accuracy);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.False(world.AutoPlay);
            Assert.False(world.IsFinished);
        }

        [Fact]
        public void Constructor_ChartOne_NotesAreUnresolvedAndStrictlyAscending()
        {
            RhythmWorld world = CreateWorld(1);

            double previous = 0.0;
            for (int i = 0; i < world.NoteCount; i++)
            {
                RhythmNoteView note = world.GetNote(i);

                Assert.Equal(i, note.NoteId);
                Assert.InRange(note.Lane, 0, world.LaneCount - 1);
                Assert.True(note.HitTimeSeconds > previous, "音符时间必须严格升序");
                Assert.True(note.HitTimeSeconds < world.SongLengthSeconds, "音符必须落在曲长之内");
                Assert.False(note.IsResolved);

                // 未判定用契约文档写死的哨兵值表示（四个判定值之外）。
                Assert.Equal((RhythmJudgement)byte.MaxValue, note.Judgement);

                previous = note.HitTimeSeconds;
            }
        }

        [Fact]
        public void Constructor_ChartTwo_HasFiftyNotesOverTwentyFiveSeconds_AndDiffersFromChartOne()
        {
            RhythmWorld one = CreateWorld(1);
            RhythmWorld two = CreateWorld(2);

            Assert.Equal(2, two.LevelId);
            Assert.Equal(ChartTwoNotes, two.NoteCount);
            Assert.Equal(ChartTwoSongLength, two.SongLengthSeconds);
            Assert.Equal(4, two.LaneCount);
            Assert.NotEqual(one.NoteCount, two.NoteCount);
            Assert.NotEqual(one.SongLengthSeconds, two.SongLengthSeconds);

            // 两张谱面同种子但骨架不同（节拍 0.45/0.48、轨道步进 3/1），音符时间与轨道必须能看出来。
            int shared = Math.Min(one.NoteCount, two.NoteCount);
            bool differs = false;
            for (int i = 0; i < shared; i++)
            {
                if (one.GetNote(i).Lane != two.GetNote(i).Lane
                    || one.GetNote(i).HitTimeSeconds != two.GetNote(i).HitTimeSeconds)
                {
                    differs = true;
                    break;
                }
            }

            Assert.True(differs, "谱面 1 与谱面 2 的音符不能完全一致");
            AssertNotSameChart(one, two);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(9)]
        [InlineData(-3)]
        [InlineData(3)]
        [InlineData(int.MaxValue)]
        public void Constructor_UnknownLevelId_ClampsToChartOne(int levelId)
        {
            RhythmWorld world = CreateWorld(levelId);

            Assert.Equal(1, world.LevelId);
            Assert.Equal(ChartOneNotes, world.NoteCount);
            Assert.Equal(ChartOneSongLength, world.SongLengthSeconds);
        }

        [Fact]
        public void Constructor_SameSeed_ProducesIdenticalCharts()
        {
            AssertSameChart(CreateWorld(1, 7), CreateWorld(1, 7));
            AssertSameChart(CreateWorld(2, 7), CreateWorld(2, 7));
        }

        [Fact]
        public void Constructor_DifferentSeeds_ProduceDifferentCharts()
        {
            RhythmWorld first = CreateWorld(1, 1);
            RhythmWorld second = CreateWorld(1, 2);

            bool differs = false;
            for (int i = 0; i < first.NoteCount; i++)
            {
                if (first.GetNote(i).Lane != second.GetNote(i).Lane
                    || first.GetNote(i).HitTimeSeconds != second.GetNote(i).HitTimeSeconds)
                {
                    differs = true;
                    break;
                }
            }

            Assert.True(differs, "不同种子必须产生可分辨的谱面微调");
        }

        [Fact]
        public void GetNote_OutOfRange_Throws()
        {
            RhythmWorld world = CreateWorld(1);

            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetNote(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetNote(world.NoteCount));
            Assert.Throws<ArgumentOutOfRangeException>(() => world.GetNote(int.MaxValue));
        }

        // ── Tick 推进歌曲时间 ─────────────────────────────────────────

        [Fact]
        public void Tick_AdvancesSongTime_AndIgnoresNonPositiveOrNaNDelta()
        {
            RhythmWorld world = CreateWorld(1);

            // 0.125 / 0.0625 都是二进制精确小数，累加结果可以逐位断言；总量 0.1875s 也还没到第一颗音符。
            world.Tick(0.125);
            Assert.Equal(0.125, world.SongTime);

            world.Tick(0.0625);
            Assert.Equal(0.1875, world.SongTime);

            world.Tick(0.0);
            world.Tick(-1.0);
            world.Tick(double.NaN);
            world.Tick(double.NegativeInfinity);

            Assert.Equal(0.1875, world.SongTime);
            Assert.Equal(0, world.ProcessedSteps);
            Assert.Equal(0, world.PerfectCount + world.GreatCount + world.GoodCount + world.MissCount);
        }

        // ── 判定窗口边界：0.045 / 0.09 / 0.14 ─────────────────────────

        [Fact]
        public void Hit_AtExactNoteTime_IsPerfect()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView note = world.GetNote(0);

            Assert.Equal(0.0, AdvanceToOffset(world, note.HitTimeSeconds, 0.0, beyond: false));

            Assert.Equal(RhythmJudgement.Perfect, world.Hit(note.Lane));

            RhythmNoteView resolved = world.GetNote(0);
            Assert.True(resolved.IsResolved);
            Assert.Equal(RhythmJudgement.Perfect, resolved.Judgement);
            Assert.Equal(1, world.PerfectCount);
            Assert.Equal(1, world.Combo);
            Assert.Equal(1, world.MaxCombo);
            Assert.Equal(1.0, world.Accuracy);
        }

        [Fact]
        public void Hit_WithinPerfectWindowOnBothSides_IsPerfect()
        {
            // 迟 0.045（含边界）：仍是 Perfect——窗口是"≤"，不是"<"。
            RhythmWorld late = CreateWorld(1);
            RhythmNoteView lateNote = late.GetNote(0);
            double lateDelta = AdvanceToOffset(late, lateNote.HitTimeSeconds, PerfectWindow, beyond: false);
            Assert.True(lateDelta <= PerfectWindow);
            Assert.Equal(RhythmJudgement.Perfect, late.Hit(lateNote.Lane));

            // 早 0.045：同样 Perfect（判定看的是 |delta|，早打迟打对称）。
            RhythmWorld early = CreateWorld(1);
            RhythmNoteView earlyNote = early.GetNote(0);
            double earlyDelta = AdvanceToOffset(early, earlyNote.HitTimeSeconds, -PerfectWindow, beyond: false);
            Assert.True(earlyDelta <= PerfectWindow);
            Assert.Equal(RhythmJudgement.Perfect, early.Hit(earlyNote.Lane));
        }

        [Fact]
        public void Hit_JustOutsidePerfectWindow_IsGreat()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView note = world.GetNote(0);

            double delta = AdvanceToOffset(world, note.HitTimeSeconds, PerfectWindow, beyond: true);

            Assert.True(delta > PerfectWindow);
            Assert.True(delta <= GreatWindow);

            Assert.Equal(RhythmJudgement.Great, world.Hit(note.Lane));
            Assert.Equal(1, world.GreatCount);
            Assert.Equal(0.8, world.Accuracy);
        }

        [Fact]
        public void Hit_AtGreatWindowBoundary_IsStillGreat()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView note = world.GetNote(0);

            double delta = AdvanceToOffset(world, note.HitTimeSeconds, GreatWindow, beyond: false);

            Assert.True(delta > PerfectWindow);
            Assert.True(delta <= GreatWindow);

            Assert.Equal(RhythmJudgement.Great, world.Hit(note.Lane));
            Assert.Equal(1, world.GreatCount);
        }

        [Fact]
        public void Hit_JustOutsideGreatWindow_IsGood()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView note = world.GetNote(0);

            double delta = AdvanceToOffset(world, note.HitTimeSeconds, GreatWindow, beyond: true);

            Assert.True(delta > GreatWindow);
            Assert.True(delta <= GoodWindow);

            Assert.Equal(RhythmJudgement.Good, world.Hit(note.Lane));
            Assert.Equal(1, world.GoodCount);
            Assert.Equal(0.5, world.Accuracy);
        }

        [Fact]
        public void Hit_AtGoodWindowBoundary_IsStillGood_AndSweepHasNotFired()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView note = world.GetNote(0);

            double delta = AdvanceToOffset(world, note.HitTimeSeconds, GoodWindow, beyond: false);

            Assert.True(delta > GreatWindow);
            Assert.True(delta <= GoodWindow);

            // 恰好卡在 0.14：漏打扫描必须"严格大于"才触发，否则边界上既不能打也不判 Miss。
            Assert.False(world.GetNote(0).IsResolved);
            Assert.Equal(0, world.MissCount);
            Assert.Equal(RhythmJudgement.Good, world.Hit(note.Lane));
        }

        [Fact]
        public void Tick_JustBeyondGoodWindow_AutoMissesTheNote()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView note = world.GetNote(0);

            double delta = AdvanceToOffset(world, note.HitTimeSeconds, GoodWindow, beyond: true);

            Assert.True(delta > GoodWindow);

            RhythmNoteView swept = world.GetNote(0);
            Assert.True(swept.IsResolved);
            Assert.Equal(RhythmJudgement.Miss, swept.Judgement);
            Assert.Equal(1, world.MissCount);
            Assert.Equal(0, world.Combo);
            Assert.Equal(0.0, world.Accuracy);
        }

        // ── 空击、连击与漏打 ──────────────────────────────────────────

        [Fact]
        public void Hit_FarFromAnyNote_ReturnsMissWithoutConsumingOrCounting()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView note = world.GetNote(0);

            // 开局（t = 0）第一颗音符还没到：偏差必然远超 0.14s。
            double delta = Math.Abs(world.SongTime - note.HitTimeSeconds);
            Assert.True(delta > GoodWindow);

            Assert.Equal(RhythmJudgement.Miss, world.Hit(note.Lane));

            Assert.False(world.GetNote(0).IsResolved);
            Assert.Equal(0, world.MissCount);
            Assert.Equal(0, world.PerfectCount);
            Assert.Equal(0, world.Combo);
            Assert.Equal(0.0, world.Accuracy);
        }

        [Fact]
        public void Hit_OutOfWindow_BreaksComboWithoutConsumingTheNextNote()
        {
            RhythmWorld world = CreateWorld(1);
            RhythmNoteView first = world.GetNote(0);
            RhythmNoteView second = world.GetNote(1);

            AdvanceToNote(world, 0, 0.0);
            Assert.Equal(RhythmJudgement.Perfect, world.Hit(first.Lane));
            Assert.Equal(1, world.Combo);

            // 第二颗音符还远在判定窗外；在它的轨道上戳一下：只断连击，不吃音符、不记 Miss。
            Assert.True(Math.Abs(world.SongTime - second.HitTimeSeconds) > GoodWindow);
            Assert.Equal(RhythmJudgement.Miss, world.Hit(second.Lane));

            Assert.Equal(0, world.Combo);
            Assert.Equal(1, world.MaxCombo);
            Assert.Equal(0, world.MissCount);
            Assert.Equal(1, world.PerfectCount);
            Assert.False(world.GetNote(1).IsResolved);
        }

        [Fact]
        public void Hit_InvalidLane_ReturnsMissAndKeepsNotes()
        {
            RhythmWorld world = CreateWorld(1);

            Assert.Equal(RhythmJudgement.Miss, world.Hit(-1));
            Assert.Equal(RhythmJudgement.Miss, world.Hit(world.LaneCount));
            Assert.Equal(RhythmJudgement.Miss, world.Hit(int.MaxValue));

            for (int i = 0; i < world.NoteCount; i++)
            {
                Assert.False(world.GetNote(i).IsResolved);
            }

            Assert.Equal(0, world.MissCount);
        }

        [Fact]
        public void Hit_ConsecutiveNotes_ChainComboAndKeepMaxCombo()
        {
            RhythmWorld world = CreateWorld(1);

            for (int i = 0; i < 5; i++)
            {
                AdvanceToNote(world, i, 0.0);
                Assert.Equal(RhythmJudgement.Perfect, world.Hit(world.GetNote(i).Lane));
                Assert.Equal(i + 1, world.Combo);
                Assert.Equal(i + 1, world.MaxCombo);
            }

            Assert.Equal(5, world.PerfectCount);
            Assert.Equal(1.0, world.Accuracy);
        }

        [Fact]
        public void Tick_MissingOneNote_ResetsComboButKeepsMaxCombo()
        {
            RhythmWorld world = CreateWorld(1);

            AdvanceToNote(world, 0, 0.0);
            Assert.Equal(RhythmJudgement.Perfect, world.Hit(world.GetNote(0).Lane));
            Assert.Equal(1, world.Combo);

            // 放过第二颗音符：越过它的判定窗后由 Tick 自动判 Miss。
            AdvanceToNote(world, 1, GoodWindow + 0.01);

            RhythmNoteView missed = world.GetNote(1);
            Assert.True(missed.IsResolved);
            Assert.Equal(RhythmJudgement.Miss, missed.Judgement);
            Assert.Equal(1, world.MissCount);
            Assert.Equal(0, world.Combo);
            Assert.Equal(1, world.MaxCombo);
        }

        // ── 准确率 ────────────────────────────────────────────────────

        [Fact]
        public void Accuracy_IsWeightedByJudgement_AndMissCountsInDenominator()
        {
            RhythmWorld world = CreateWorld(1);

            AdvanceToNote(world, 0, 0.0);
            Assert.Equal(RhythmJudgement.Perfect, world.Hit(world.GetNote(0).Lane));

            AdvanceToNote(world, 1, 0.07);
            Assert.Equal(RhythmJudgement.Great, world.Hit(world.GetNote(1).Lane));

            AdvanceToNote(world, 2, 0.12);
            Assert.Equal(RhythmJudgement.Good, world.Hit(world.GetNote(2).Lane));

            // 第四颗漏掉：Miss 权重 0，但仍然占分母（已结算音符）。
            AdvanceToNote(world, 3, GoodWindow + 0.01);

            Assert.Equal(1, world.PerfectCount);
            Assert.Equal(1, world.GreatCount);
            Assert.Equal(1, world.GoodCount);
            Assert.Equal(1, world.MissCount);

            // 四类计数必然等于已结算音符数（空击不写计数，所以这个恒等式成立）。
            int counted = world.PerfectCount + world.GreatCount + world.GoodCount + world.MissCount;
            Assert.Equal(4, counted);

            double expected = (1.0 + 0.8 + 0.5) / 4.0;
            Assert.True(Math.Abs(world.Accuracy - expected) < 1e-12, "准确率必须按 1 / 0.8 / 0.5 / 0 加权");
            Assert.Equal(0, world.Combo);
            Assert.Equal(3, world.MaxCombo);
        }

        [Fact]
        public void Accuracy_StaysZeroUntilFirstNoteIsResolved()
        {
            RhythmWorld world = CreateWorld(1);

            world.Tick(0.1);
            Assert.Equal(0.0, world.Accuracy);

            AdvanceToNote(world, 0, 0.0);
            Assert.Equal(RhythmJudgement.Perfect, world.Hit(world.GetNote(0).Lane));
            Assert.Equal(1.0, world.Accuracy);
        }

        // ── AutoPlay ──────────────────────────────────────────────────

        [Fact]
        public void AutoPlay_FullRun_IsAllPerfectWithAccuracyOne()
        {
            RhythmWorld world = CreateWorld(1);
            world.AutoPlay = true;

            int frames = RunFramesUntilFinished(world);

            Assert.True(world.IsFinished);
            Assert.True(world.SongTime > ChartOneSongLength);
            Assert.True(frames > 0);
            Assert.Equal(ChartOneNotes, world.PerfectCount);
            Assert.Equal(0, world.GreatCount);
            Assert.Equal(0, world.GoodCount);
            Assert.Equal(0, world.MissCount);
            Assert.Equal(1.0, world.Accuracy);
            Assert.Equal(ChartOneNotes, world.Combo);
            Assert.Equal(ChartOneNotes, world.MaxCombo);

            for (int i = 0; i < world.NoteCount; i++)
            {
                RhythmNoteView note = world.GetNote(i);
                Assert.True(note.IsResolved);
                Assert.Equal(RhythmJudgement.Perfect, note.Judgement);
            }
        }

        [Fact]
        public void AutoPlay_IsDeterministicAcrossRuns()
        {
            RhythmWorld first = CreateWorld(1);
            first.AutoPlay = true;
            RunFramesUntilFinished(first);

            RhythmWorld second = CreateWorld(1);
            second.AutoPlay = true;
            RunFramesUntilFinished(second);

            Assert.Equal(first.SongTime, second.SongTime);
            Assert.Equal(first.PerfectCount, second.PerfectCount);
            Assert.Equal(first.MaxCombo, second.MaxCombo);
            Assert.Equal(first.Accuracy, second.Accuracy);

            for (int i = 0; i < first.NoteCount; i++)
            {
                Assert.Equal(first.GetNote(i).Judgement, second.GetNote(i).Judgement);
            }

            // 关掉 AutoPlay 用同样的帧步长跑同一条时间轴：结果必须完全不同（全是漏打）。
            RhythmWorld manual = CreateWorld(1);
            RunFramesUntilFinished(manual);
            Assert.Equal(0, manual.PerfectCount);
            Assert.Equal(ChartOneNotes, manual.MissCount);
            Assert.NotEqual(first.PerfectCount, manual.PerfectCount);
        }

        // ── IsFinished / Step / 结算 ───────────────────────────────────

        [Fact]
        public void IsFinished_RequiresAllNotesResolvedAndSongEnded()
        {
            RhythmWorld world = CreateWorld(1);
            world.AutoPlay = true;

            // 先把最后一颗音符打完：30 个音符全部结算，但歌曲还没走到 15s。
            int guard = 0;
            while (world.PerfectCount < ChartOneNotes && guard < LoopGuard)
            {
                world.Tick(FrameSeconds);
                guard++;
            }

            Assert.Equal(ChartOneNotes, world.PerfectCount);
            Assert.True(world.SongTime <= ChartOneSongLength);
            Assert.False(world.IsFinished);

            // 越过曲长之后才算结束。
            guard = 0;
            while (!world.IsFinished && guard < LoopGuard)
            {
                world.Tick(FrameSeconds);
                guard++;
            }

            Assert.True(world.IsFinished);
            Assert.True(world.SongTime > ChartOneSongLength);
        }

        [Fact]
        public void Step_IncrementsProcessedStepsAndDrivesSongTime()
        {
            RhythmWorld world = CreateWorld(1);

            world.Step(0.5f);
            Assert.Equal(1, world.ProcessedSteps);
            Assert.Equal(0.5, world.SongTime);

            world.Step(0.25f);
            Assert.Equal(2, world.ProcessedSteps);
            Assert.Equal(0.75, world.SongTime);

            // 结束后为空操作，步数不再累加。
            RhythmWorld finished = CreateWorld(1);
            finished.AutoPlay = true;
            RunFramesUntilFinished(finished);
            int stepsAtEnd = finished.ProcessedSteps;

            finished.Step(1.0f);
            Assert.Equal(stepsAtEnd, finished.ProcessedSteps);
        }

        [Fact]
        public void CollectResult_BeforeFinished_Throws()
        {
            RhythmWorld world = CreateWorld(1);

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());

            // 所有音符打完但曲未终，同样不许结算。
            world.AutoPlay = true;
            int guard = 0;
            while (world.PerfectCount < ChartOneNotes && guard < LoopGuard)
            {
                world.Tick(FrameSeconds);
                guard++;
            }

            Assert.False(world.IsFinished);
            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
        }

        [Fact]
        public void CollectResult_AfterAutoPlay_IsTurnVictoryWithLoot()
        {
            RhythmWorld world = CreateWorld(1);
            world.AutoPlay = true;

            for (int i = 0; i < 10; i++)
            {
                world.Step((float)FrameSeconds);
            }

            RunFramesUntilFinished(world);
            int steps = world.ProcessedSteps;
            Assert.Equal(10, steps);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.True(result.IsVictory);
            Assert.Single(result.Loots);
            Assert.Equal(1003, result.Loots[0].ItemId);
            Assert.Equal(1, result.Loots[0].Count);
            Assert.Empty(result.TriggeredTags);
            Assert.Equal(steps, result.StepCount);
            Assert.Equal(world.MaxCombo, result.RemainingUnits);
            Assert.Equal(ChartOneNotes, result.RemainingUnits);
        }

        [Fact]
        public void CollectResult_SecondCall_Throws()
        {
            RhythmWorld world = CreateWorld(1);
            world.AutoPlay = true;
            RunFramesUntilFinished(world);

            world.CollectResult();

            Assert.Throws<InvalidOperationException>(() => world.CollectResult());
            Assert.True(world.IsFinished);
        }

        [Fact]
        public void CollectResult_AllMissed_IsDefeatWithoutLoot()
        {
            RhythmWorld world = CreateWorld(1);

            RunFramesUntilFinished(world);

            Assert.Equal(ChartOneNotes, world.MissCount);
            Assert.Equal(0, world.PerfectCount);
            Assert.Equal(0.0, world.Accuracy);
            Assert.Equal(0, world.MaxCombo);

            GameplayResult result = world.CollectResult();

            Assert.Equal(GameplayKernelType.Turn, result.KernelType);
            Assert.False(result.IsVictory);
            Assert.Empty(result.Loots);
            Assert.Equal(0, result.RemainingUnits);
        }

        [Fact]
        public void CollectResult_ChartTwoVictory_UsesChartTwoNoteCount()
        {
            RhythmWorld world = CreateWorld(2);
            world.AutoPlay = true;
            RunFramesUntilFinished(world);

            GameplayResult result = world.CollectResult();

            Assert.True(result.IsVictory);
            Assert.Equal(ChartTwoNotes, world.PerfectCount);
            Assert.Equal(ChartTwoNotes, result.RemainingUnits);
            Assert.Single(result.Loots);
        }

        // ── 零分配基线 ────────────────────────────────────────────────

        [Fact]
        public void TickAndHit_AfterWarmup_DoNotAllocate()
        {
            RhythmWorld world = CreateWorld(1);

            // 1/240s 的步长让 200 + 1000 次调用只走到 5s（曲长 15s），测量区间全程"对局进行中"。
            const double step = 1.0 / 240.0;
            for (int i = 0; i < 200; i++)
            {
                world.Tick(step);
                world.Hit(i & 3);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.Tick(step);
                world.Hit(i & 3);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void TickUnderAutoPlay_AfterWarmup_DoesNotAllocate()
        {
            RhythmWorld world = CreateWorld(1);
            world.AutoPlay = true;

            const double step = 1.0 / 240.0;
            for (int i = 0; i < 200; i++)
            {
                world.Tick(step);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                world.Tick(step);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, allocated);
            Assert.True(world.PerfectCount > 0, "测量区间内必须真的在判定（否则零分配断言会退化成空跑）");
        }

        // ── 夹具与小工具 ──────────────────────────────────────────────

        private static RhythmWorld CreateWorld(int levelId)
        {
            return CreateWorld(levelId, Seed);
        }

        private static RhythmWorld CreateWorld(int levelId, int seed)
        {
            return new RhythmWorld(new GameplayInitData(levelId, seed, 0));
        }

        /// <summary>
        /// 把歌曲时间推进到 <c>note[index].HitTime + offset</c>，返回<b>实际算出的</b> <c>|SongTime - HitTime|</c>。
        /// <para>
        /// 多段累加后 <c>SongTime</c> 与目标点可能差一个 ulp，而判定看的是内核真正用来比较的那个差值，
        /// 所以返回值交给断言，而不是拿 <paramref name="offset"/> 当预期。差值只可能比 offset 小/大几个 ulp，
        /// 不影响"落在哪个窗口"的结论（窗口间距 ≥ 0.045s）。
        /// </para>
        /// </summary>
        private static double AdvanceToNote(RhythmWorld world, int index, double offset)
        {
            RhythmNoteView note = world.GetNote(index);
            double target = note.HitTimeSeconds + offset;
            double step = target - world.SongTime;
            if (step > 0.0)
            {
                world.Tick(step);
            }

            return Math.Abs(world.SongTime - note.HitTimeSeconds);
        }

        /// <summary>
        /// 从开局一次推进到 <c>hitTime + offset</c>，并用 <see cref="Math.BitIncrement"/> /
        /// <see cref="Math.BitDecrement"/> 逐 ulp 微调落点，直到实际偏差明确落在
        /// <paramref name="beyond"/> 指定的那一侧（<c>true</c> = 严格大于 <c>|offset|</c>）。
        /// <para>只用于 <see cref="RhythmWorld.SongTime"/> 仍为 0 的新世界：单次 <c>Tick</c> 才能保证落在算好的那个 double 上。</para>
        /// </summary>
        private static double AdvanceToOffset(RhythmWorld world, double hitTime, double offset, bool beyond)
        {
            Assert.Equal(0.0, world.SongTime);

            double magnitude = Math.Abs(offset);
            double target = hitTime + offset;
            bool farSideIsUp = offset >= 0.0;
            bool increaseMagnitude = beyond == farSideIsUp;

            for (int i = 0; i < 16; i++)
            {
                double delta = Math.Abs(target - hitTime);
                if ((delta > magnitude) == beyond)
                {
                    world.Tick(target);
                    Assert.Equal(target, world.SongTime);
                    return Math.Abs(world.SongTime - hitTime);
                }

                target = increaseMagnitude ? Math.BitIncrement(target) : Math.BitDecrement(target);
            }

            throw new InvalidOperationException("无法构造出落在期望一侧的判定偏差。");
        }

        /// <summary>按固定帧步长推进直到对局结束，返回消耗的帧数（带安全上限）。</summary>
        private static int RunFramesUntilFinished(RhythmWorld world)
        {
            int frames = 0;
            while (!world.IsFinished && frames < LoopGuard)
            {
                world.Tick(FrameSeconds);
                frames++;
            }

            Assert.True(world.IsFinished, "对局未在安全上限内结束（曲长或结束条件被改坏了）");
            return frames;
        }

        private static void AssertSameChart(RhythmWorld expected, RhythmWorld actual)
        {
            Assert.Equal(expected.LevelId, actual.LevelId);
            Assert.Equal(expected.NoteCount, actual.NoteCount);
            Assert.Equal(expected.SongLengthSeconds, actual.SongLengthSeconds);

            for (int i = 0; i < expected.NoteCount; i++)
            {
                RhythmNoteView left = expected.GetNote(i);
                RhythmNoteView right = actual.GetNote(i);

                Assert.Equal(left.NoteId, right.NoteId);
                Assert.Equal(left.Lane, right.Lane);
                Assert.Equal(left.HitTimeSeconds, right.HitTimeSeconds);
            }
        }

        private static void AssertNotSameChart(RhythmWorld first, RhythmWorld second)
        {
            int shared = Math.Min(first.NoteCount, second.NoteCount);
            for (int i = 0; i < shared; i++)
            {
                bool same = first.GetNote(i).Lane == second.GetNote(i).Lane
                    && first.GetNote(i).HitTimeSeconds == second.GetNote(i).HitTimeSeconds;

                if (!same)
                {
                    return;
                }
            }

            Assert.Fail("两张谱面在共同长度内完全一致，说明 LevelId 没有真正选到不同谱面");
        }
    }
}
