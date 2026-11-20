using System;
using System.Diagnostics;
using AIFramework.Core.Replay;
using Xunit;

namespace AIFramework.Core.Tests.Replay
{
    /// <summary>
    /// 帧同步输入回放对拍（T9-9）验收：同一份输入序列跑两路（原始会话 / 回放重放），
    /// 逐帧比对状态指纹并定位首个分歧帧。
    /// <para>
    /// 覆盖口径：完全一致、首/中/末帧分歧、长度不一致（前缀一致与不一致）、双空、单空、
    /// null 入参、报告字段与 <c>ToReportLine</c> 固定格式、<c>DescribeDivergence</c> 的上下文/
    /// 缺失标记/"无分歧"、单帧、重复指纹、负值与极值指纹、1000 帧批量对拍的耗时与零分配，
    /// 以及用真实 <c>BattleRig</c> 录制 → 回放两路指纹做端到端对拍。
    /// </para>
    /// </summary>
    public sealed class FrameSyncComparerTests
    {
        // ── 1. 完全一致 ────────────────────────────────────────────────

        [Fact]
        public void Compare_IdenticalArrays_MatchesAndReportsNoDivergence()
        {
            long[] expected = { 11L, 22L, 33L };
            long[] actual = { 11L, 22L, 33L };

            FrameSyncReport report = FrameSyncComparer.Compare(expected, actual);

            Assert.True(report.IsMatch);
            Assert.Equal(3, report.ComparedFrames);
            Assert.Equal(-1, report.FirstDivergenceFrame);
            Assert.Equal(0L, report.ExpectedFingerprint);
            Assert.Equal(0L, report.ActualFingerprint);
        }

        // ── 2. 首帧分歧 ────────────────────────────────────────────────

        [Fact]
        public void Compare_FirstFrameDivergence_ReportsFrameOne()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 5L, 6L, 7L },
                new long[] { 9L, 6L, 7L });

            Assert.False(report.IsMatch);
            Assert.Equal(1, report.FirstDivergenceFrame);
            Assert.Equal(1, report.ComparedFrames);
            Assert.Equal(5L, report.ExpectedFingerprint);
            Assert.Equal(9L, report.ActualFingerprint);
        }

        // ── 3. 中间帧分歧 ──────────────────────────────────────────────

        [Fact]
        public void Compare_MiddleFrameDivergence_ReportsExactFrame()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 1L, 2L, 3L, 4L, 5L },
                new long[] { 1L, 2L, 9L, 4L, 5L });

            Assert.False(report.IsMatch);
            Assert.Equal(3, report.FirstDivergenceFrame);
            Assert.Equal(3, report.ComparedFrames);
            Assert.Equal(3L, report.ExpectedFingerprint);
            Assert.Equal(9L, report.ActualFingerprint);
        }

        // ── 4. 末帧分歧 ────────────────────────────────────────────────

        [Fact]
        public void Compare_LastFrameDivergence_ReportsLastFrame()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 1L, 2L, 3L },
                new long[] { 1L, 2L, 9L });

            Assert.False(report.IsMatch);
            Assert.Equal(3, report.FirstDivergenceFrame);
            Assert.Equal(3, report.ComparedFrames);
            Assert.Equal(3L, report.ExpectedFingerprint);
            Assert.Equal(9L, report.ActualFingerprint);
        }

        // ── 5. 长度不一致：前缀一致 / 前缀已分歧 ────────────────────────

        [Fact]
        public void Compare_LengthMismatchWithEqualPrefix_ReportsFirstMissingFrame()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 1L, 2L, 3L, 4L },
                new long[] { 1L, 2L });

            Assert.False(report.IsMatch);
            Assert.Equal(3, report.FirstDivergenceFrame);
            Assert.Equal(2, report.ComparedFrames);

            // 缺失那一路（实际）没有对应指纹，按约定记 0；另一路给"多出来的第一帧"真实值。
            Assert.Equal(3L, report.ExpectedFingerprint);
            Assert.Equal(0L, report.ActualFingerprint);
        }

        [Fact]
        public void Compare_ActualLongerThanExpected_ReportsExpectedMissing()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 1L, 2L },
                new long[] { 1L, 2L, 3L });

            Assert.False(report.IsMatch);
            Assert.Equal(3, report.FirstDivergenceFrame);
            Assert.Equal(2, report.ComparedFrames);
            Assert.Equal(0L, report.ExpectedFingerprint);
            Assert.Equal(3L, report.ActualFingerprint);
        }

        [Fact]
        public void Compare_LengthMismatchWithEarlierDivergence_ReportsEarlierFrame()
        {
            // 先把"数值分歧优先于长度分歧"钉住：前缀第 2 帧就不同，答案必须是第 2 帧而不是 min+1。
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 1L, 9L },
                new long[] { 1L, 2L, 3L });

            Assert.False(report.IsMatch);
            Assert.Equal(2, report.FirstDivergenceFrame);
            Assert.Equal(2, report.ComparedFrames);
            Assert.Equal(9L, report.ExpectedFingerprint);
            Assert.Equal(2L, report.ActualFingerprint);
        }

        // ── 6. 空数组 ──────────────────────────────────────────────────

        [Fact]
        public void Compare_BothEmpty_MatchesWithoutComparingFrames()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(new long[0], new long[0]);

            Assert.True(report.IsMatch);
            Assert.Equal(0, report.ComparedFrames);
            Assert.Equal(-1, report.FirstDivergenceFrame);
            Assert.Equal(0L, report.ExpectedFingerprint);
            Assert.Equal(0L, report.ActualFingerprint);
        }

        [Fact]
        public void Compare_OnlyActualHasFrames_DivergesAtFrameOne()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(new long[0], new long[] { 7L, 8L });

            Assert.False(report.IsMatch);
            Assert.Equal(1, report.FirstDivergenceFrame);
            Assert.Equal(0, report.ComparedFrames);
            Assert.Equal(0L, report.ExpectedFingerprint);
            Assert.Equal(7L, report.ActualFingerprint);
        }

        [Fact]
        public void Compare_OnlyExpectedHasFrames_DivergesAtFrameOne()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(new long[] { 7L, 8L }, new long[0]);

            Assert.False(report.IsMatch);
            Assert.Equal(1, report.FirstDivergenceFrame);
            Assert.Equal(0, report.ComparedFrames);
            Assert.Equal(7L, report.ExpectedFingerprint);
            Assert.Equal(0L, report.ActualFingerprint);
        }

        // ── 7. null 入参 ───────────────────────────────────────────────

        [Fact]
        public void Compare_NullExpected_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => FrameSyncComparer.Compare(null!, new long[0]));
        }

        [Fact]
        public void Compare_NullActual_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => FrameSyncComparer.Compare(new long[0], null!));
        }

        // ── 8. 单帧 ────────────────────────────────────────────────────

        [Fact]
        public void Compare_SingleFrameMatch_ComparesOneFrame()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(new long[] { 42L }, new long[] { 42L });

            Assert.True(report.IsMatch);
            Assert.Equal(1, report.ComparedFrames);
            Assert.Equal(-1, report.FirstDivergenceFrame);
        }

        [Fact]
        public void Compare_SingleFrameDivergence_ReportsFrameOne()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(new long[] { 42L }, new long[] { 43L });

            Assert.False(report.IsMatch);
            Assert.Equal(1, report.FirstDivergenceFrame);
            Assert.Equal(1, report.ComparedFrames);
            Assert.Equal(42L, report.ExpectedFingerprint);
            Assert.Equal(43L, report.ActualFingerprint);
        }

        // ── 9. 重复指纹 / 负数 / 极值 ──────────────────────────────────

        [Fact]
        public void Compare_DuplicateFingerprints_ReportsFirstRealChange()
        {
            // 重复指纹很常见（静止场景每帧折叠出同一个值），只有真正变化的那帧才算分歧。
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 7L, 7L, 7L, 7L },
                new long[] { 7L, 7L, 8L, 7L });

            Assert.False(report.IsMatch);
            Assert.Equal(3, report.FirstDivergenceFrame);
            Assert.Equal(3, report.ComparedFrames);
            Assert.Equal(7L, report.ExpectedFingerprint);
            Assert.Equal(8L, report.ActualFingerprint);
        }

        [Fact]
        public void Compare_NegativeFingerprints_ComparesExactValues()
        {
            // FNV-1a 的 64 位结果按 long 解读常为负数，比较必须按位精确，不能取绝对值或截断。
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { -1L, -2L, -3L },
                new long[] { -1L, -22L, -3L });

            Assert.False(report.IsMatch);
            Assert.Equal(2, report.FirstDivergenceFrame);
            Assert.Equal(-2L, report.ExpectedFingerprint);
            Assert.Equal(-22L, report.ActualFingerprint);
        }

        [Fact]
        public void Compare_ExtremeLongValues_DoNotAlias()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { long.MinValue, 0L, long.MaxValue },
                new long[] { long.MaxValue, 0L, long.MaxValue });

            Assert.False(report.IsMatch);
            Assert.Equal(1, report.FirstDivergenceFrame);
            Assert.Equal(long.MinValue, report.ExpectedFingerprint);
            Assert.Equal(long.MaxValue, report.ActualFingerprint);
        }

        // ── 10. 大数组：1000 帧 ────────────────────────────────────────

        [Fact]
        public void Compare_ThousandFrames_MatchesWithinBudget()
        {
            long[] expected = BuildFrames(1000);
            long[] actual = BuildFrames(1000);

            FrameSyncComparer.Compare(expected, actual); // 预热 JIT

            var stopwatch = Stopwatch.StartNew();
            FrameSyncReport report = FrameSyncComparer.Compare(expected, actual);
            stopwatch.Stop();

            Assert.True(report.IsMatch);
            Assert.Equal(1000, report.ComparedFrames);
            Assert.Equal(-1, report.FirstDivergenceFrame);

            // 纯遍历 1000 个 long 应在微秒级；给 500ms 只是拦截"退化成 O(n²)/反射"这类质变，
            // 不拿它当性能基准（CI 机器负载不可控）。
            Assert.True(
                stopwatch.ElapsedMilliseconds < 500,
                "1000 帧对拍耗时 " + stopwatch.ElapsedMilliseconds + "ms，超出 500ms 兜底预算。");
        }

        [Fact]
        public void Compare_ThousandFrames_DivergenceNearEnd_ReportsFrame999()
        {
            long[] expected = BuildFrames(1000);
            long[] actual = BuildFrames(1000);
            actual[998] += 1L;

            FrameSyncReport report = FrameSyncComparer.Compare(expected, actual);

            Assert.False(report.IsMatch);
            Assert.Equal(999, report.FirstDivergenceFrame);
            Assert.Equal(999, report.ComparedFrames);
            Assert.Equal(expected[998], report.ExpectedFingerprint);
            Assert.Equal(actual[998], report.ActualFingerprint);
        }

        [Fact]
        public void Compare_AfterWarmup_AllocatesNothing()
        {
            long[] expected = BuildFrames(1000);
            long[] actual = BuildFrames(1000);
            actual[500] += 1L;

            FrameSyncComparer.Compare(expected, actual); // 预热 JIT

            long before = GC.GetAllocatedBytesForCurrentThread();
            FrameSyncComparer.Compare(expected, actual);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0L, allocated);
        }

        // ── 11. 报告字段与固定格式 ─────────────────────────────────────

        [Fact]
        public void Report_DefaultInstance_DoesNotClaimMatch()
        {
            // 未比对（default）必须报"分歧"：宁可误报，也不能让空报告冒充"已验证一致"。
            FrameSyncReport report = default;

            Assert.False(report.IsMatch);
            Assert.Equal(0, report.ComparedFrames);
            Assert.Contains("结论=分歧", report.ToReportLine());
        }

        [Fact]
        public void Report_Constructor_ClampsNegativeComparedFrames()
        {
            var report = new FrameSyncReport(-5, -1, 0L, 0L);

            Assert.Equal(0, report.ComparedFrames);
            Assert.True(report.IsMatch);
        }

        [Fact]
        public void Report_Constructor_NormalizesNegativeDivergence()
        {
            // 任何负数的分歧帧都归一为 -1（无分歧），避免 -7 与 -1 在日志里出现两种"无分歧"。
            var report = new FrameSyncReport(10, -7, 0L, 0L);

            Assert.Equal(-1, report.FirstDivergenceFrame);
            Assert.True(report.IsMatch);
        }

        [Fact]
        public void Report_ToReportLine_Match_UsesFixedFields()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 1L, 2L, 3L },
                new long[] { 1L, 2L, 3L });

            Assert.Equal(
                "[FrameSync] 比对=3 结论=一致 首个分歧=-1 期望=0 实际=0",
                report.ToReportLine());
        }

        [Fact]
        public void Report_ToReportLine_Divergence_UsesFixedFields()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 1L, 2L, 3L },
                new long[] { 1L, 9L, 3L });

            Assert.Equal(
                "[FrameSync] 比对=2 结论=分歧 首个分歧=2 期望=2 实际=9",
                report.ToReportLine());
        }

        [Fact]
        public void Report_ToReportLine_LengthMismatch_ShowsZeroForMissingSide()
        {
            FrameSyncReport report = FrameSyncComparer.Compare(
                new long[] { 5L, 6L, 7L },
                new long[] { 5L, 6L });

            Assert.Equal(
                "[FrameSync] 比对=2 结论=分歧 首个分歧=3 期望=7 实际=0",
                report.ToReportLine());
        }

        // ── 12. DescribeDivergence：无分歧 / 上下文 / 缺失标记 ─────────

        [Fact]
        public void DescribeDivergence_NoDivergence_ReturnsFixedText()
        {
            Assert.Equal(
                "无分歧",
                FrameSyncComparer.DescribeDivergence(new long[] { 1L, 2L }, new long[] { 1L, 2L }));

            // 双空一致也走同一条出口，不返回空串。
            Assert.Equal("无分歧", FrameSyncComparer.DescribeDivergence(new long[0], new long[0]));
        }

        [Fact]
        public void DescribeDivergence_MiddleDivergence_ShowsCenteredContext()
        {
            long[] expected = { 10L, 20L, 30L, 40L, 50L, 60L, 70L, 80L, 90L };
            long[] actual = { 10L, 20L, 30L, 40L, 99L, 60L, 70L, 80L, 90L };

            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual, 2));

            Assert.Equal(6, lines.Length);
            Assert.Equal("[FrameSync] 首个分歧帧=5（1 基） 期望帧数=9 实际帧数=9 上下文=2", lines[0]);
            Assert.Equal("  帧 3: 期望=30 实际=30", lines[1]);
            Assert.Equal("  帧 4: 期望=40 实际=40", lines[2]);
            Assert.Equal("> 帧 5: 期望=50 实际=99", lines[3]);
            Assert.Equal("  帧 6: 期望=60 实际=60", lines[4]);
            Assert.Equal("  帧 7: 期望=70 实际=70", lines[5]);
        }

        [Fact]
        public void DescribeDivergence_ClampsContextAtStart()
        {
            long[] expected = { 1L, 2L, 3L, 4L, 5L, 6L };
            long[] actual = { 9L, 2L, 3L, 4L, 5L, 6L };

            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual, 3));

            // 第 1 帧就是分歧：只能向后展开，不能出现"帧 0"或负数帧号。
            Assert.Equal(5, lines.Length);
            Assert.Equal("> 帧 1: 期望=1 实际=9", lines[1]);
            Assert.Equal("  帧 2: 期望=2 实际=2", lines[2]);
            Assert.Equal("  帧 3: 期望=3 实际=3", lines[3]);
            Assert.Equal("  帧 4: 期望=4 实际=4", lines[4]);
        }

        [Fact]
        public void DescribeDivergence_ClampsContextAtEnd()
        {
            long[] expected = { 1L, 2L, 3L, 4L, 5L, 9L };
            long[] actual = { 1L, 2L, 3L, 4L, 5L, 7L };

            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual, 3));

            Assert.Equal(5, lines.Length);
            Assert.Equal("  帧 3: 期望=3 实际=3", lines[1]);
            Assert.Equal("  帧 4: 期望=4 实际=4", lines[2]);
            Assert.Equal("  帧 5: 期望=5 实际=5", lines[3]);
            Assert.Equal("> 帧 6: 期望=9 实际=7", lines[4]);
        }

        [Fact]
        public void DescribeDivergence_LengthMismatch_MarksMissingActual()
        {
            long[] expected = { 1L, 2L, 3L, 4L };
            long[] actual = { 1L, 2L };

            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual, 3));

            Assert.Equal(5, lines.Length);
            Assert.Equal("[FrameSync] 首个分歧帧=3（1 基） 期望帧数=4 实际帧数=2 上下文=3", lines[0]);
            Assert.Equal("  帧 1: 期望=1 实际=1", lines[1]);
            Assert.Equal("  帧 2: 期望=2 实际=2", lines[2]);
            Assert.Equal("> 帧 3: 期望=3 实际=0（缺失）", lines[3]);
            Assert.Equal("  帧 4: 期望=4 实际=0（缺失）", lines[4]);
        }

        [Fact]
        public void DescribeDivergence_LengthMismatch_MarksMissingExpected()
        {
            long[] expected = { 1L, 2L };
            long[] actual = { 1L, 2L, 3L };

            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual, 3));

            Assert.Equal(4, lines.Length);
            Assert.Equal("[FrameSync] 首个分歧帧=3（1 基） 期望帧数=2 实际帧数=3 上下文=3", lines[0]);
            Assert.Equal("  帧 1: 期望=1 实际=1", lines[1]);
            Assert.Equal("  帧 2: 期望=2 实际=2", lines[2]);
            Assert.Equal("> 帧 3: 期望=0（缺失） 实际=3", lines[3]);
        }

        [Fact]
        public void DescribeDivergence_ZeroContext_ShowsOnlyDivergenceFrame()
        {
            long[] expected = { 1L, 2L, 3L };
            long[] actual = { 1L, 2L, 9L };

            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual, 0));

            Assert.Equal(2, lines.Length);
            Assert.Equal("[FrameSync] 首个分歧帧=3（1 基） 期望帧数=3 实际帧数=3 上下文=0", lines[0]);
            Assert.Equal("> 帧 3: 期望=3 实际=9", lines[1]);
        }

        [Fact]
        public void DescribeDivergence_NegativeContext_TreatedAsZero()
        {
            long[] expected = { 1L, 2L, 3L };
            long[] actual = { 1L, 2L, 9L };

            Assert.Equal(
                FrameSyncComparer.DescribeDivergence(expected, actual, 0),
                FrameSyncComparer.DescribeDivergence(expected, actual, -5));
        }

        [Fact]
        public void DescribeDivergence_HugeContext_ShowsAllFramesWithoutOverflow()
        {
            long[] expected = { 1L, 2L, 3L, 9L };
            long[] actual = { 1L, 2L, 3L, 4L };

            // int.MaxValue 若不做收敛会溢出成负下标；正确行为是"最多展示全部帧"。
            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual, int.MaxValue));

            Assert.Equal(5, lines.Length);
            Assert.Equal("> 帧 4: 期望=9 实际=4", lines[4]);
        }

        [Fact]
        public void DescribeDivergence_DefaultContext_IsThreeFramesEachSide()
        {
            // 用"第 N 帧指纹 = N×10"的可读值，方便按帧号心算期望行。
            long[] expected = new long[20];
            long[] actual = new long[20];
            for (int i = 0; i < expected.Length; i++)
            {
                expected[i] = (i + 1) * 10L;
                actual[i] = expected[i];
            }

            actual[10] = 999L;

            string[] lines = SplitLines(FrameSyncComparer.DescribeDivergence(expected, actual));

            // 默认上下文 3：帧 8..14 共 7 行 + 1 行头部。
            Assert.Equal(8, lines.Length);
            Assert.Equal("[FrameSync] 首个分歧帧=11（1 基） 期望帧数=20 实际帧数=20 上下文=3", lines[0]);
            Assert.Equal("  帧 8: 期望=80 实际=80", lines[1]);
            Assert.Equal("> 帧 11: 期望=110 实际=999", lines[4]);
            Assert.Equal("  帧 14: 期望=140 实际=140", lines[7]);
        }

        [Fact]
        public void DescribeDivergence_NullExpected_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => FrameSyncComparer.DescribeDivergence(null!, new long[0]));
        }

        [Fact]
        public void DescribeDivergence_NullActual_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => FrameSyncComparer.DescribeDivergence(new long[0], null!));
        }

        // ── 13. 端到端：真实录制 → 回放两路指纹对拍 ─────────────────

        [Fact]
        public void RecordThenReplay_RealBattle_ComparerReportsMatch()
        {
            const int seed = 20241118;
            const int frames = 120;

            var recorder = new ReplayRecorder(8192);
            long[] expected = ReplayHarness.Record(BattleRig.Create(seed), recorder, frames);
            long[] actual = ReplayHarness.Replay(BattleRig.Create(seed), new ReplayPlayer(recorder), frames);

            FrameSyncReport report = FrameSyncComparer.Compare(expected, actual);

            Assert.True(
                report.IsMatch,
                report.ToReportLine() + "\n" + FrameSyncComparer.DescribeDivergence(expected, actual));
            Assert.Equal(frames, report.ComparedFrames);
            Assert.Equal(-1, report.FirstDivergenceFrame);
        }

        [Fact]
        public void TruncatedReplay_RealBattle_ComparerReportsFirstMissingFrame()
        {
            const int seed = 20241118;

            var recorder = new ReplayRecorder(8192);
            long[] expected = ReplayHarness.Record(BattleRig.Create(seed), recorder, 120);
            long[] actual = ReplayHarness.Replay(BattleRig.Create(seed), new ReplayPlayer(recorder), 60);

            FrameSyncReport report = FrameSyncComparer.Compare(expected, actual);

            // 回放只跑了 60 帧：前 60 帧一致，第 61 帧起期望侧"多出来"，缺失侧指纹记 0。
            Assert.False(report.IsMatch);
            Assert.Equal(61, report.FirstDivergenceFrame);
            Assert.Equal(60, report.ComparedFrames);
            Assert.Equal(expected[60], report.ExpectedFingerprint);
            Assert.Equal(0L, report.ActualFingerprint);

            string description = FrameSyncComparer.DescribeDivergence(expected, actual);
            Assert.Contains("首个分歧帧=61", description);
            Assert.Contains("（缺失）", description);
        }

        [Fact]
        public void DifferentSeedReplay_RealBattle_ComparerReportsFrameOne()
        {
            var recorder = new ReplayRecorder(8192);
            long[] expected = ReplayHarness.Record(BattleRig.Create(101), recorder, 60);

            // 同一份录像换到不同种子的布场里重放：输入序列相同，但初始状态已经不同，
            // 因此第 1 帧就对不上——这正是对拍器要抓的"非确定性/环境漂移"。
            long[] actual = ReplayHarness.Replay(BattleRig.Create(202), new ReplayPlayer(recorder), 60);

            FrameSyncReport report = FrameSyncComparer.Compare(expected, actual);

            Assert.False(report.IsMatch);
            Assert.Equal(1, report.FirstDivergenceFrame);
            Assert.Equal(expected[0], report.ExpectedFingerprint);
            Assert.Equal(actual[0], report.ActualFingerprint);
        }

        // ── 辅助 ──────────────────────────────────────────────────────

        /// <summary>构造 <paramref name="count"/> 帧可预测指纹（每帧相差 37，避免相邻值撞车）。</summary>
        private static long[] BuildFrames(int count)
        {
            long[] frames = new long[count];
            for (int i = 0; i < count; i++)
            {
                frames[i] = (i * 37L) - 12345L;
            }

            return frames;
        }

        /// <summary>按 <c>'\n'</c> 切行（与 <see cref="FrameSyncComparer.DescribeDivergence"/> 的输出口径一致）。</summary>
        private static string[] SplitLines(string text)
        {
            return text.Split('\n');
        }
    }
}
