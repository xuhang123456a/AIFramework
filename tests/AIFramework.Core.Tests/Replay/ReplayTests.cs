using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using AIFramework.Contracts.Replay;
using AIFramework.Core.Replay;
using Xunit;

namespace AIFramework.Core.Tests.Replay
{
    /// <summary>
    /// 确定性回放（T3-6）验收：录制 → 同种子 + 同输入序列重放 → 状态指纹逐帧一致。
    /// <para>
    /// 覆盖录制器定长/溢出/清空、播放器帧序/游标/回溯、录像头字段，以及
    /// <see cref="StateFingerprint"/> 的稳定性、敏感性、空局回放与整局结算一致性。
    /// </para>
    /// </summary>
    public sealed class ReplayTests
    {
        // ── 1. 录制 → 完整重放（逐帧指纹） ─────────────────────────────

        [Fact]
        public void RecordThenReplay_SameSeedAndInputs_MatchFingerprintEveryFrame()
        {
            const int seed = 20241118;
            const int frames = 120;

            ReplayRecorder recorder = new ReplayRecorder(512);
            BattleRig recorded = BattleRig.Create(seed);
            long[] recordedFingerprints = ReplayHarness.Record(recorded, recorder, frames);

            // 录制局本身的事实：跑满帧、未结束、四类意图都被内核消费。
            Assert.Equal(frames, recorded.World.FrameId);
            Assert.False(recorded.World.IsFinished);
            Assert.True(recorder.Count > 0);
            Assert.Equal(0, recorder.DroppedCount);
            Assert.True(recorded.Spatial.UnitQueryCount > 0);
            Assert.Equal(2, recorded.World.ReflectedCount);
            Assert.True(recorded.World.InterruptedCount > 0);

            // 回放局：同种子重建布场，输入只来自录像。
            BattleRig replayed = BattleRig.Create(seed);
            ReplayPlayer player = new ReplayPlayer(recorder);
            long[] replayedFingerprints = ReplayHarness.Replay(replayed, player, frames);

            Assert.Equal(recorder.Count, player.Cursor);
            Assert.Equal(0, player.Remaining);
            Assert.Equal(frames, replayed.World.FrameId);
            Assert.Equal(recorded.World.ReflectedCount, replayed.World.ReflectedCount);
            Assert.Equal(recorded.World.InterruptedCount, replayed.World.InterruptedCount);

            for (int frame = 0; frame < frames; frame++)
            {
                Assert.True(
                    recordedFingerprints[frame] == replayedFingerprints[frame],
                    "第 " + frame + " 帧指纹不一致：录制=" + recordedFingerprints[frame] +
                    "，回放=" + replayedFingerprints[frame]);
            }
        }

        // ── 2. 不同种子 → 指纹不同 ─────────────────────────────────────

        [Fact]
        public void DifferentSeed_ProducesDifferentFingerprint()
        {
            BattleRig first = BattleRig.Create(101);
            BattleRig second = BattleRig.Create(202);

            // 布场偏移由种子决定：还没推进任何一帧，初始状态就已经不同。
            long firstInitial = StateFingerprint.Of(first.World, first.World);
            long secondInitial = StateFingerprint.Of(second.World, second.World);
            Assert.NotEqual(firstInitial, secondInitial);

            // 跑同一份输入脚本后仍然不同，证明指纹对真实状态差异持续敏感。
            long[] firstFrames = ReplayHarness.StepScripted(first, 60);
            long[] secondFrames = ReplayHarness.StepScripted(second, 60);
            Assert.NotEqual(firstFrames[59], secondFrames[59]);
        }

        // ── 3. 录制器容量耗尽 ──────────────────────────────────────────

        [Fact]
        public void Recorder_CapacityExhausted_DropsWithoutThrowing()
        {
            ReplayRecorder recorder = new ReplayRecorder(3);

            for (int i = 0; i < 5; i++)
            {
                recorder.Record(i, BattleReplayCodec.KindDamage, i, i + 1, i + 2, i + 3);
            }

            Assert.Equal(3, recorder.Count);
            Assert.Equal(3, recorder.Capacity);
            Assert.Equal(2, recorder.DroppedCount);

            // 前三条完整保留，溢出只丢弃尾部。
            for (int i = 0; i < 3; i++)
            {
                RecordedIntent intent = recorder.Get(i);
                Assert.Equal(i, intent.Frame);
                Assert.Equal(i, intent.A);
            }

            // 继续写只增加丢弃计数，不抛异常、不覆盖已记录内容。
            recorder.Record(99, BattleReplayCodec.KindDamage, 0, 0, 0, 0);
            Assert.Equal(3, recorder.Count);
            Assert.Equal(3, recorder.DroppedCount);
            Assert.Equal(0, recorder.Get(0).Frame);
        }

        // ── 4. 录制器清空 / 录像头 / 下标边界 ──────────────────────────

        [Fact]
        public void Recorder_Clear_ResetsCountAndDroppedCount()
        {
            ReplayRecorder recorder = new ReplayRecorder(2);
            recorder.Record(0, BattleReplayCodec.KindControl, 1, 2, 3, 4);
            recorder.Record(1, BattleReplayCodec.KindMove, 5, 6, 7, 8);
            recorder.Record(2, BattleReplayCodec.KindReflect, 9, 10, 11, 12);
            Assert.Equal(2, recorder.Count);
            Assert.Equal(1, recorder.DroppedCount);

            recorder.Clear();

            Assert.Equal(0, recorder.Count);
            Assert.Equal(0, recorder.DroppedCount);
            Assert.Equal(2, recorder.Capacity);

            recorder.Record(0, BattleReplayCodec.KindReflect, 13, 14, 15, 16);
            Assert.Equal(1, recorder.Count);
            Assert.Equal(BattleReplayCodec.KindReflect, recorder.Get(0).Kind);
            Assert.Equal(13, recorder.Get(0).A);
        }

        [Fact]
        public void Recorder_BuildHeader_CopiesAllFields()
        {
            ReplayRecorder recorder = new ReplayRecorder();

            ReplayHeader header = recorder.BuildHeader(
                (int)GameplayKernelType.Realtime,
                42,
                20241118,
                3,
                120);

            Assert.Equal((int)GameplayKernelType.Realtime, header.KernelType);
            Assert.Equal(42, header.LevelId);
            Assert.Equal(20241118, header.RandomSeed);
            Assert.Equal(3, header.UnitCount);
            Assert.Equal(120, header.FrameCount);
        }

        [Fact]
        public void Recorder_Get_RejectsOutOfRangeIndex()
        {
            ReplayRecorder recorder = new ReplayRecorder(4);
            recorder.Record(0, BattleReplayCodec.KindMove, 1, 0, 0, 0);

            Assert.Equal(0, recorder.Get(0).Frame);
            Assert.Throws<ArgumentOutOfRangeException>(() => recorder.Get(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => recorder.Get(-1));
        }

        // ── 5. 播放器帧序 / 游标 / 回溯 ────────────────────────────────

        [Fact]
        public void Player_EmitsOnlyCurrentFrameAndTracksCursorAndRemaining()
        {
            ReplayRecorder recorder = new ReplayRecorder(8);
            recorder.Record(0, BattleReplayCodec.KindControl, 10, 1, 2, 0);
            recorder.Record(0, BattleReplayCodec.KindMove, 20, 3, 4, 0);
            recorder.Record(1, BattleReplayCodec.KindDamage, 30, 5, 6, 7);

            ReplayPlayer player = new ReplayPlayer(recorder);
            Assert.Equal(0, player.Cursor);
            Assert.Equal(3, player.Remaining);

            // 帧号不匹配：一条都不吐，游标不动。
            Assert.False(player.TryNext(1, out _));
            Assert.Equal(0, player.Cursor);
            Assert.Equal(3, player.Remaining);

            // 第 0 帧两条按录制顺序吐出。
            Assert.True(player.TryNext(0, out RecordedIntent first));
            Assert.Equal(BattleReplayCodec.KindControl, first.Kind);
            Assert.Equal(10, first.A);
            Assert.Equal(1, player.Cursor);
            Assert.Equal(2, player.Remaining);

            Assert.True(player.TryNext(0, out RecordedIntent second));
            Assert.Equal(BattleReplayCodec.KindMove, second.Kind);
            Assert.Equal(20, second.A);
            Assert.Equal(2, player.Cursor);
            Assert.Equal(1, player.Remaining);

            // 下一条记录属于第 1 帧：本帧不能再吐。
            Assert.False(player.TryNext(0, out RecordedIntent exhausted));
            Assert.Equal(0, exhausted.Frame);
            Assert.Equal(2, player.Cursor);
            Assert.Equal(1, player.Remaining);

            player.AdvanceFrame();
            Assert.True(player.TryNext(1, out RecordedIntent third));
            Assert.Equal(BattleReplayCodec.KindDamage, third.Kind);
            Assert.Equal(30, third.A);
            Assert.Equal(3, player.Cursor);
            Assert.Equal(0, player.Remaining);

            Assert.False(player.TryNext(1, out _));
        }

        [Fact]
        public void Player_RequiresAdvanceFrameToReachLaterFrames()
        {
            ReplayRecorder recorder = new ReplayRecorder(4);
            recorder.Record(0, BattleReplayCodec.KindControl, 1, 0, 0, 0);
            recorder.Record(2, BattleReplayCodec.KindDamage, 2, 0, 0, 0);

            ReplayPlayer player = new ReplayPlayer(recorder);
            Assert.True(player.TryNext(0, out _));
            Assert.False(player.TryNext(0, out _));

            // 第 1 帧无记录：只能空过，不能提前拿到第 2 帧的输入。
            player.AdvanceFrame();
            Assert.False(player.TryNext(1, out _));
            Assert.Equal(1, player.Cursor);
            Assert.Equal(1, player.Remaining);

            player.AdvanceFrame();
            Assert.True(player.TryNext(2, out RecordedIntent record));
            Assert.Equal(BattleReplayCodec.KindDamage, record.Kind);
            Assert.Equal(2, player.Cursor);
            Assert.Equal(0, player.Remaining);
        }

        [Fact]
        public void Player_Rewind_RestartsFromFirstRecord()
        {
            ReplayRecorder recorder = new ReplayRecorder(4);
            recorder.Record(0, BattleReplayCodec.KindControl, 7, 0, 0, 0);
            recorder.Record(1, BattleReplayCodec.KindMove, 8, 0, 0, 0);

            ReplayPlayer player = new ReplayPlayer(recorder);
            Assert.True(player.TryNext(0, out _));
            player.AdvanceFrame();
            Assert.True(player.TryNext(1, out _));
            Assert.Equal(2, player.Cursor);
            Assert.Equal(0, player.Remaining);

            player.Rewind();

            Assert.Equal(0, player.Cursor);
            Assert.Equal(2, player.Remaining);
            Assert.True(player.TryNext(0, out RecordedIntent first));
            Assert.Equal(7, first.A);

            player.AdvanceFrame();
            Assert.True(player.TryNext(1, out RecordedIntent second));
            Assert.Equal(8, second.A);
        }

        [Fact]
        public void Player_RejectsNullRecorder()
        {
            Assert.Throws<ArgumentNullException>(() => new ReplayPlayer(null!));
        }

        [Fact]
        public void Player_EmptyRecorder_YieldsNothing()
        {
            ReplayRecorder recorder = new ReplayRecorder(1);
            ReplayPlayer player = new ReplayPlayer(recorder);

            Assert.Equal(0, player.Cursor);
            Assert.Equal(0, player.Remaining);
            Assert.False(player.TryNext(0, out RecordedIntent intent));
            Assert.Equal(0, intent.Frame);

            player.AdvanceFrame();
            Assert.False(player.TryNext(1, out _));
            player.Rewind();
            Assert.Equal(0, player.Cursor);
            Assert.Equal(0, player.Remaining);
        }

        // ── 6. 指纹稳定性 / 敏感性 / 空局回放 ─────────────────────────

        [Fact]
        public void Fingerprint_IsStableAcrossRepeatedCalls()
        {
            BattleRig rig = BattleRig.Create(20241118);
            ReplayHarness.StepScripted(rig, 30);

            long first = StateFingerprint.Of(rig.World, rig.World);
            long second = StateFingerprint.Of(rig.World, rig.World);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Fingerprint_AfterWarmup_AllocatesNothing()
        {
            BattleRig rig = BattleRig.Create(20241118);
            ReplayHarness.StepScripted(rig, 30);

            // 预热：JIT / 静态初始化 / 接口调用抖动都发生在测量窗口之前。
            for (int i = 0; i < 32; i++)
            {
                StateFingerprint.Of(rig.World, rig.World);
            }

            const int MeasuredCalls = 1000;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredCalls; i++)
            {
                StateFingerprint.Of(rig.World, rig.World);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal(0L, allocated);
        }

        [Fact]
        public void Fingerprint_ChangesWhenUnitMoves()
        {
            BattleRig baseline = BattleRig.Create(20241118);
            BattleRig moved = BattleRig.Create(20241118);

            for (int i = 0; i < 5; i++)
            {
                baseline.World.Step(ReplayHarness.StepSeconds);
                moved.World.Step(ReplayHarness.StepSeconds);
            }

            Assert.Equal(
                StateFingerprint.Of(baseline.World, baseline.World),
                StateFingerprint.Of(moved.World, moved.World));

            moved.World.Submit(new MoveIntent(moved.Hero, new Vec2(0.5f, 0f)));
            baseline.World.Step(ReplayHarness.StepSeconds);
            moved.World.Step(ReplayHarness.StepSeconds);

            Assert.NotEqual(
                StateFingerprint.Of(baseline.World, baseline.World),
                StateFingerprint.Of(moved.World, moved.World));
        }

        [Fact]
        public void Fingerprint_ChangesWhenHpChanges()
        {
            BattleRig baseline = BattleRig.Create(20241118);
            BattleRig damaged = BattleRig.Create(20241118);

            for (int i = 0; i < 5; i++)
            {
                baseline.World.Step(ReplayHarness.StepSeconds);
                damaged.World.Step(ReplayHarness.StepSeconds);
            }

            damaged.World.Submit(new DamageIntent(damaged.Hero, damaged.Enemy, 7, 0));
            baseline.World.Step(ReplayHarness.StepSeconds);
            damaged.World.Step(ReplayHarness.StepSeconds);

            Assert.NotEqual(
                StateFingerprint.Of(baseline.World, baseline.World),
                StateFingerprint.Of(damaged.World, damaged.World));
        }

        [Fact]
        public void EmptyBattle_WithoutInputs_ReplaysToIdenticalFingerprints()
        {
            const int seed = 20241118;
            const int frames = 120;

            ReplayRecorder recorder = new ReplayRecorder(16);
            BattleRig recorded = BattleRig.Create(seed);
            long[] recordedFingerprints = new long[frames];

            for (int frame = 0; frame < frames; frame++)
            {
                recorded.World.Step(ReplayHarness.StepSeconds);
                recordedFingerprints[frame] = StateFingerprint.Of(recorded.World, recorded.World);
            }

            Assert.Equal(0, recorder.Count);
            Assert.Equal(0, recorder.DroppedCount);

            BattleRig replayed = BattleRig.Create(seed);
            ReplayPlayer player = new ReplayPlayer(recorder);
            long[] replayedFingerprints = ReplayHarness.Replay(replayed, player, frames);

            Assert.Equal(frames, replayed.World.FrameId);
            Assert.Equal(0, player.Cursor);
            Assert.Equal(0, player.Remaining);

            for (int frame = 0; frame < frames; frame++)
            {
                Assert.True(
                    recordedFingerprints[frame] == replayedFingerprints[frame],
                    "空局第 " + frame + " 帧指纹不一致：录制=" + recordedFingerprints[frame] +
                    "，回放=" + replayedFingerprints[frame]);
            }
        }

        // ── 7. 整局结算一致 ───────────────────────────────────────────

        [Fact]
        public void RecordThenReplay_FinishesWithSameGameplayResult()
        {
            const int seed = 20241118;
            const int fatalFrame = 20;

            ReplayRecorder recorder = new ReplayRecorder(64);
            BattleRig recorded = BattleRig.Create(seed);
            RecordingIntentWriter writer = new RecordingIntentWriter(recorded.World, recorder);

            int frame = 0;
            while (!recorded.World.IsFinished && frame < 60)
            {
                writer.BeginFrame(frame);
                if (frame == fatalFrame)
                {
                    writer.Submit(new DamageIntent(recorded.Hero, recorded.Enemy, 50, 0));
                }

                recorded.World.Step(ReplayHarness.StepSeconds);
                frame++;
            }

            Assert.True(recorded.World.IsFinished);
            Assert.Equal(fatalFrame + 1, recorded.World.FrameId);
            Assert.Equal(1, recorder.Count);
            GameplayResult expected = recorded.World.CollectResult();

            BattleRig replayed = BattleRig.Create(seed);
            ReplayPlayer player = new ReplayPlayer(recorder);
            int recordedFrames = recorded.World.FrameId;

            for (int replayFrame = 0; replayFrame < recordedFrames; replayFrame++)
            {
                while (player.TryNext(replayed.World.FrameId, out RecordedIntent record))
                {
                    BattleReplayCodec.Submit(replayed.World, in record);
                }

                replayed.World.Step(ReplayHarness.StepSeconds);
                player.AdvanceFrame();
            }

            Assert.True(replayed.World.IsFinished);
            Assert.Equal(recordedFrames, replayed.World.FrameId);
            GameplayResult actual = replayed.World.CollectResult();

            Assert.Equal(expected.KernelType, actual.KernelType);
            Assert.Equal(expected.IsVictory, actual.IsVictory);
            Assert.Equal(expected.StepCount, actual.StepCount);
            Assert.Equal(expected.RemainingUnits, actual.RemainingUnits);
            Assert.Equal(expected.Loots.Count, actual.Loots.Count);
            Assert.Equal(expected.TriggeredTags.Count, actual.TriggeredTags.Count);

            for (int i = 0; i < expected.Loots.Count; i++)
            {
                Assert.Equal(expected.Loots[i].ItemId, actual.Loots[i].ItemId);
                Assert.Equal(expected.Loots[i].Count, actual.Loots[i].Count);
            }

            for (int i = 0; i < expected.TriggeredTags.Count; i++)
            {
                Assert.Equal(expected.TriggeredTags[i].Id, actual.TriggeredTags[i].Id);
            }
        }
    }
}
