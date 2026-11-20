using System;
using System.Collections.Generic;
using AIFramework.Core.Cqe;
using AIFramework.Features.Guide;
using Xunit;

namespace AIFramework.Features.Tests.Guide
{
    /// <summary>
    /// 引导步骤机验收：表驱动推进、触发命中与去重、完成/跳过/不再提示、存档断点续引导、空表与表缺失。
    /// 全部用例走真实 <see cref="CqeBus"/>（消息通道与事件顺序都是被测行为的一部分）。
    /// </summary>
    public sealed class GuideHandlerTests
    {
        // ---------- 初始状态 ----------

        [Fact]
        public void NewHandler_StartsActiveOnFirstStepNotReached()
        {
            GuideHarness harness = NewHarness();
            GuideSnapshot snapshot = harness.Query();

            Assert.True(snapshot.IsActive);
            Assert.False(snapshot.IsCompleted);
            Assert.False(snapshot.IsSkippedAll);
            Assert.Equal(1, snapshot.StepId);
            Assert.Equal("启动", snapshot.Title);
            Assert.Equal("点击开始按钮", snapshot.Hint);
            Assert.Equal(GuideTriggers.Launch, snapshot.Trigger);
            Assert.Equal(0, snapshot.TargetId);
            Assert.False(snapshot.Skippable);
            Assert.False(snapshot.Reached);
            Assert.Equal(0, snapshot.CompletedCount);
            Assert.Equal(4, snapshot.TotalCount);
            Assert.Equal(4, snapshot.Steps.Length);

            for (int i = 0; i < snapshot.Steps.Length; i++)
            {
                Assert.False(snapshot.Steps[i].Completed);
                Assert.False(snapshot.Steps[i].Skipped);
            }

            Assert.True(harness.Handler.IsActive);
            Assert.False(harness.Handler.IsCompleted);
        }

        [Fact]
        public void Constructor_PublishesNoEvents()
        {
            GuideHarness harness = NewHarness();

            Assert.Empty(harness.Events);
        }

        [Fact]
        public void Constructor_MissingTable_ThrowsInvalidOperationException()
        {
            Assert.Throws<InvalidOperationException>(
                () => new GuideHandler(new CqeBus(), GuideTableStub.WithoutTable()));
        }

        [Fact]
        public void Constructor_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new GuideHandler(null!, GuideTableStub.Standard()));
            Assert.Throws<ArgumentNullException>(() => new GuideHandler(new CqeBus(), null!));
        }

        [Fact]
        public void Register_NullBus_Throws()
        {
            GuideHarness harness = NewHarness();

            Assert.Throws<ArgumentNullException>(() => harness.Handler.Register(null!));
        }

        // ---------- 触发（只观测，不推进） ----------

        [Fact]
        public void Trigger_MatchingStep_MarksReachedAndPublishesOnce()
        {
            GuideHarness harness = NewHarness();

            harness.Trigger(GuideTriggers.Launch, 0);

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.Reached);

            List<GuideStepChangedEvent> events = harness.EventsOf<GuideStepChangedEvent>();
            Assert.Single(events);
            Assert.Equal(1, events[0].StepId);
            Assert.Equal("启动", events[0].Title);
        }

        [Fact]
        public void Trigger_DoesNotAdvanceStep()
        {
            GuideHarness harness = NewHarness();

            harness.Trigger(GuideTriggers.Launch, 0);

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.StepId);
            Assert.Equal(0, snapshot.CompletedCount);
            Assert.False(snapshot.Steps[0].Completed);
        }

        [Fact]
        public void Trigger_WrongCategory_IsIgnored()
        {
            GuideHarness harness = NewHarness();

            harness.Trigger(GuideTriggers.Lobby, 0);

            Assert.False(harness.Query().Reached);
            Assert.Empty(harness.Events);
        }

        [Fact]
        public void Trigger_ForFutureStep_IsIgnored()
        {
            GuideHarness harness = NewHarness();

            // 后续步的触发事实先到了：不能被提前兑现，否则引导会在第 1 步就显示"已达成"。
            harness.Trigger(GuideTriggers.ShopOpen, 0);
            harness.Trigger(GuideTriggers.BattleFinished, 2001);

            Assert.False(harness.Query().Reached);
            Assert.Empty(harness.Events);
        }

        [Fact]
        public void Trigger_Repeated_DoesNotPublishTwice()
        {
            GuideHarness harness = NewHarness();

            harness.Trigger(GuideTriggers.Launch, 0);
            harness.Trigger(GuideTriggers.Launch, 0);
            harness.Trigger(GuideTriggers.Launch, 55);

            Assert.Single(harness.EventsOf<GuideStepChangedEvent>());
            Assert.True(harness.Query().Reached);
        }

        [Fact]
        public void Trigger_UnqualifiedTarget_MatchesAnyTargetId()
        {
            GuideHarness harness = NewHarness();
            harness.CompleteStep(1, GuideTriggers.Launch, 0);

            // 第 2 步 targetId=0，表示不限定目标：任何 targetId 都应命中。
            harness.Trigger(GuideTriggers.Lobby, 777);

            Assert.True(harness.Query().Reached);
            Assert.Equal("进大厅", harness.Query().Title);
        }

        [Fact]
        public void Trigger_QualifiedTarget_MismatchIsIgnored()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);
            int before = harness.CountOf<GuideStepChangedEvent>();

            harness.Trigger(GuideTriggers.BattleFinished, 999);

            Assert.False(harness.Query().Reached);
            Assert.Equal(before, harness.CountOf<GuideStepChangedEvent>());
        }

        [Fact]
        public void Trigger_QualifiedTarget_MatchMarksReached()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);

            harness.Trigger(GuideTriggers.BattleFinished, 2001);

            Assert.True(harness.Query().Reached);
            Assert.Equal(4, harness.Query().StepId);
        }

        // ---------- 完成（必须已 reached） ----------

        [Fact]
        public void Advance_WithoutReached_IsRejected()
        {
            GuideHarness harness = NewHarness();

            // 不支持"跳过触发条件直接完成"：没观测到触发事实就上报完成，必须被拒绝。
            harness.Advance(1);

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.StepId);
            Assert.Equal(0, snapshot.CompletedCount);
            Assert.False(snapshot.Steps[0].Completed);
            Assert.Empty(harness.Events);
        }

        [Fact]
        public void Advance_WrongStepId_IsRejected()
        {
            GuideHarness harness = NewHarness();
            harness.Trigger(GuideTriggers.Launch, 0);
            int before = harness.CountOf<GuideStepChangedEvent>();

            harness.Advance(2);

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.StepId);
            Assert.Equal(0, snapshot.CompletedCount);
            Assert.Equal(before, harness.CountOf<GuideStepChangedEvent>());
        }

        [Fact]
        public void Advance_AfterReached_MovesToNextStepAndPublishes()
        {
            GuideHarness harness = NewHarness();

            harness.CompleteStep(1, GuideTriggers.Launch, 0);

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsActive);
            Assert.Equal(2, snapshot.StepId);
            Assert.Equal("进大厅", snapshot.Title);
            Assert.False(snapshot.Reached);
            Assert.Equal(1, snapshot.CompletedCount);
            Assert.True(snapshot.Steps[0].Completed);
            Assert.False(snapshot.Steps[1].Completed);

            List<GuideStepChangedEvent> events = harness.EventsOf<GuideStepChangedEvent>();
            Assert.Equal(2, events.Count);
            Assert.Equal(2, events[1].StepId);
            Assert.Equal("进大厅", events[1].Title);
            Assert.Empty(harness.EventsOf<GuideCompletedEvent>());
        }

        [Fact]
        public void Advance_LastStep_CompletesGuideAndPublishesCompletedEvent()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);

            harness.CompleteStep(4, GuideTriggers.BattleFinished, 2001);

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.False(snapshot.IsActive);
            Assert.Equal(4, snapshot.CompletedCount);
            Assert.Equal(4, snapshot.TotalCount);
            Assert.True(snapshot.Steps[3].Completed);

            Assert.Single(harness.EventsOf<GuideCompletedEvent>());
            Assert.IsType<GuideCompletedEvent>(harness.Events[harness.Events.Count - 1]);
            Assert.True(harness.Handler.IsCompleted);
        }

        [Fact]
        public void Advance_AfterCompleted_IsIgnored()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);
            harness.CompleteStep(4, GuideTriggers.BattleFinished, 2001);
            int before = harness.Events.Count;

            harness.Trigger(GuideTriggers.Launch, 0);
            harness.Advance(4);
            harness.Skip(4);

            Assert.Equal(before, harness.Events.Count);
            Assert.True(harness.Query().IsCompleted);
            Assert.Equal(4, harness.Query().CompletedCount);
        }

        [Fact]
        public void Snapshot_WhenFinished_HasZeroedCurrentStepFields()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);
            harness.CompleteStep(4, GuideTriggers.BattleFinished, 2001);

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(0, snapshot.StepId);
            Assert.Equal(string.Empty, snapshot.Title);
            Assert.Equal(string.Empty, snapshot.Hint);
            Assert.Equal(GuideTriggers.Launch, snapshot.Trigger);
            Assert.Equal(0, snapshot.TargetId);
            Assert.False(snapshot.Skippable);
            Assert.False(snapshot.Reached);
        }

        // ---------- 跳过 ----------

        [Fact]
        public void Skip_AllowedStep_MarksSkippedAndAdvances()
        {
            GuideHarness harness = GuideHarness.Create(GuideTableStub.WithRows(
                new GuideRowSpec(1, "可跳步", "点这里", GuideTriggers.Launch, 0, 1),
                new GuideRowSpec(2, "第二步", "再点这里", GuideTriggers.Lobby, 0, 0)));

            // 跳过不要求先 reached：用户可以在没做动作时直接拒绝这一步。
            harness.Skip(1);

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(2, snapshot.StepId);
            Assert.Equal(0, snapshot.CompletedCount);
            Assert.True(snapshot.Steps[0].Skipped);
            Assert.False(snapshot.Steps[0].Completed);

            List<GuideStepChangedEvent> events = harness.EventsOf<GuideStepChangedEvent>();
            Assert.Single(events);
            Assert.Equal(2, events[0].StepId);
            Assert.Empty(harness.EventsOf<GuideCompletedEvent>());
        }

        [Fact]
        public void Skip_NotSkippable_IsIgnored()
        {
            GuideHarness harness = NewHarness();

            // 第 1 步 skippable=0：必须做，命令静默忽略。
            harness.Skip(1);

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.StepId);
            Assert.False(snapshot.Steps[0].Skipped);
            Assert.Empty(harness.Events);
        }

        [Fact]
        public void Skip_WrongStepId_IsIgnored()
        {
            GuideHarness harness = NewHarness();
            harness.Skip(3);

            Assert.Equal(1, harness.Query().StepId);
            Assert.Empty(harness.Events);
        }

        [Fact]
        public void Skip_LastStep_CompletesGuide()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);

            harness.Skip(4);

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.False(snapshot.IsActive);
            Assert.Equal(3, snapshot.CompletedCount);
            Assert.True(snapshot.Steps[3].Skipped);
            Assert.False(snapshot.Steps[3].Completed);
            Assert.Single(harness.EventsOf<GuideCompletedEvent>());
        }

        [Fact]
        public void CompletedCount_ExcludesSkippedSteps()
        {
            GuideHarness harness = GuideHarness.Create(GuideTableStub.WithRows(
                new GuideRowSpec(1, "第一步", "h", GuideTriggers.Launch, 0, 1),
                new GuideRowSpec(2, "第二步", "h", GuideTriggers.Lobby, 0, 1),
                new GuideRowSpec(3, "第三步", "h", GuideTriggers.ShopOpen, 0, 1)));

            harness.Skip(1);
            harness.CompleteStep(2, GuideTriggers.Lobby, 0);
            Assert.Equal(1, harness.Query().CompletedCount);

            harness.Skip(3);

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.Equal(1, snapshot.CompletedCount);
            Assert.Equal(3, snapshot.TotalCount);
            Assert.True(snapshot.Steps[0].Skipped);
            Assert.True(snapshot.Steps[1].Completed);
            Assert.True(snapshot.Steps[2].Skipped);
        }

        // ---------- 不再提示 ----------

        [Fact]
        public void SkipAll_DeactivatesWithoutPublishing()
        {
            GuideHarness harness = NewHarness();

            harness.SkipAll();

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsSkippedAll);
            Assert.False(snapshot.IsActive);
            Assert.False(snapshot.IsCompleted);
            Assert.Equal(0, snapshot.StepId);
            Assert.Equal(string.Empty, snapshot.Title);
            Assert.False(snapshot.Reached);

            // "不再提示"仍要通知表现层：否则引导高亮条会停在旧步骤直到重开场景。
            // 只发一条完成事件（跳过不是"逐步完成"，所以不发 StepChanged）。
            Assert.Single(harness.Events);
            Assert.Single(harness.EventsOf<GuideCompletedEvent>());
            Assert.True(harness.Handler.IsSkippedAll);
        }

        [Fact]
        public void SkipAll_IgnoresSubsequentCommands()
        {
            GuideHarness harness = NewHarness();
            harness.SkipAll();

            harness.Trigger(GuideTriggers.Launch, 0);
            harness.Advance(1);
            harness.Skip(3);

            Assert.Single(harness.Events); // 只有第一次 SkipAll 的那条完成事件
            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsSkippedAll);
            Assert.Equal(0, snapshot.CompletedCount);
        }

        [Fact]
        public void SkipAll_IsIdempotent()
        {
            GuideHarness harness = NewHarness();

            harness.SkipAll();
            harness.SkipAll();

            Assert.True(harness.Query().IsSkippedAll);
            Assert.Single(harness.Events); // 幂等：重复跳过不再发事件
        }

        [Fact]
        public void SkipAll_AfterCompleted_IsIgnored()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);
            harness.CompleteStep(4, GuideTriggers.BattleFinished, 2001);

            harness.SkipAll();

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.False(snapshot.IsSkippedAll);
        }

        // ---------- 事件顺序 ----------

        [Fact]
        public void EventOrder_FullPlaythrough_IsStepChangedPerTransitionThenCompleted()
        {
            GuideHarness harness = NewHarness();

            harness.CompleteStep(1, GuideTriggers.Launch, 0);
            harness.CompleteStep(2, GuideTriggers.Lobby, 0);
            harness.CompleteStep(3, GuideTriggers.ShopOpen, 0);
            harness.CompleteStep(4, GuideTriggers.BattleFinished, 2001);

            var labels = new List<string>();
            for (int i = 0; i < harness.Events.Count; i++)
            {
                if (harness.Events[i] is GuideStepChangedEvent stepChanged)
                {
                    labels.Add("S" + stepChanged.StepId);
                }
                else if (harness.Events[i] is GuideCompletedEvent)
                {
                    labels.Add("C");
                }
                else
                {
                    labels.Add("?");
                }
            }

            // 每一步都贡献两条事件：进入该步 + 该步触发命中（同一事件类型，语义由 stepId 区分）。
            Assert.Equal(new[] { "S1", "S2", "S2", "S3", "S3", "S4", "S4", "C" }, labels.ToArray());
        }

        // ---------- 空表 ----------

        [Fact]
        public void EmptyTable_IsCompletedImmediately()
        {
            GuideHarness harness = GuideHarness.Create(GuideTableStub.WithRows());

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.False(snapshot.IsActive);
            Assert.False(snapshot.IsSkippedAll);
            Assert.Equal(0, snapshot.TotalCount);
            Assert.Equal(0, snapshot.CompletedCount);
            Assert.Empty(snapshot.Steps);
            Assert.Equal(0, snapshot.StepId);
            Assert.True(harness.Handler.IsCompleted);
        }

        [Fact]
        public void EmptyTable_CommandsAreIgnoredSilently()
        {
            GuideHarness harness = GuideHarness.Create(GuideTableStub.WithRows());

            harness.Trigger(GuideTriggers.Launch, 0);
            harness.Advance(1);
            harness.Skip(1);
            harness.SkipAll();

            Assert.Empty(harness.Events);
            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.False(snapshot.IsSkippedAll);
        }

        [Fact]
        public void ThirtyOneSteps_FullMaskRoundTripsAtTheBitmapBoundary()
        {
            // 31 步是 int 位图（去掉符号位）的容量上限：跑满后位图必须是 0x7FFFFFFF 且能无损读回。
            var rows = new GuideRowSpec[31];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new GuideRowSpec(i + 1, "步 " + (i + 1), "h", GuideTriggers.Launch, 0, 1);
            }

            GuideHarness harness = GuideHarness.Create(GuideTableStub.WithRows(rows));
            for (int stepId = 1; stepId <= 31; stepId++)
            {
                harness.CompleteStep(stepId, GuideTriggers.Launch, 0);
            }

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.Equal(31, snapshot.CompletedCount);
            Assert.Equal(int.MaxValue, harness.Capture().CompletedMask);

            GuideHarness restored = GuideHarness.Create(GuideTableStub.WithRows(rows));
            Assert.True(restored.Restore(harness.Capture()));
            Assert.True(restored.Query().IsCompleted);
            Assert.Equal(31, restored.Query().CompletedCount);
        }

        // ---------- 存档：导出 / 恢复 ----------

        [Fact]
        public void CaptureState_ExportsCurrentStepAndBitmaps()
        {
            GuideHarness harness = NewHarness();
            harness.CompleteStep(1, GuideTriggers.Launch, 0);
            harness.CompleteStep(2, GuideTriggers.Lobby, 0);
            harness.Skip(3);

            GuideSaveState state = harness.Capture();

            Assert.Equal(4, state.CurrentStepId);
            Assert.Equal(0b0011, state.CompletedMask);
            Assert.Equal(0b0100, state.SkippedMask);
            Assert.False(state.SkippedAll);
        }

        [Fact]
        public void CaptureState_WhenFinished_ReportsNoCurrentStep()
        {
            GuideHarness harness = NewHarness();
            AdvanceToStep4(harness);
            harness.CompleteStep(4, GuideTriggers.BattleFinished, 2001);

            GuideSaveState state = harness.Capture();

            Assert.Equal(0, state.CurrentStepId);
            Assert.Equal(0b1111, state.CompletedMask);
            Assert.Equal(0, state.SkippedMask);
        }

        [Fact]
        public void CaptureState_AfterSkipAll_KeepsSkippedAllFlag()
        {
            GuideHarness harness = NewHarness();
            harness.CompleteStep(1, GuideTriggers.Launch, 0);
            harness.SkipAll();

            GuideSaveState state = harness.Capture();

            Assert.Equal(0, state.CurrentStepId);
            Assert.True(state.SkippedAll);
            Assert.Equal(0b0001, state.CompletedMask);
        }

        [Fact]
        public void TryRestoreState_RoundTripsIntoFreshHandler()
        {
            GuideHarness original = NewHarness();
            original.CompleteStep(1, GuideTriggers.Launch, 0);
            original.CompleteStep(2, GuideTriggers.Lobby, 0);
            original.Skip(3);
            GuideSaveState state = original.Capture();

            GuideHarness restored = NewHarness();
            Assert.True(restored.Restore(state));

            GuideSnapshot expected = original.Query();
            GuideSnapshot actual = restored.Query();

            Assert.Equal(expected.IsActive, actual.IsActive);
            Assert.Equal(expected.IsCompleted, actual.IsCompleted);
            Assert.Equal(expected.IsSkippedAll, actual.IsSkippedAll);
            Assert.Equal(expected.StepId, actual.StepId);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Hint, actual.Hint);
            Assert.Equal(expected.CompletedCount, actual.CompletedCount);
            Assert.Equal(expected.TotalCount, actual.TotalCount);
            for (int i = 0; i < expected.Steps.Length; i++)
            {
                Assert.Equal(expected.Steps[i].Completed, actual.Steps[i].Completed);
                Assert.Equal(expected.Steps[i].Skipped, actual.Steps[i].Skipped);
            }

            // 读档不重放历史：一条事件都不发。
            Assert.Empty(restored.Events);
        }

        [Fact]
        public void TryRestoreState_ResetsReached()
        {
            GuideHarness original = NewHarness();
            original.Trigger(GuideTriggers.Launch, 0);
            Assert.True(original.Query().Reached);
            GuideSaveState state = original.Capture();

            GuideHarness restored = NewHarness();
            Assert.True(restored.Restore(state));

            // Reached 不入档：触发事实必须在读档后重新观测，否则"看到过"会被永久继承。
            GuideSnapshot snapshot = restored.Query();
            Assert.Equal(1, snapshot.StepId);
            Assert.False(snapshot.Reached);
        }

        [Fact]
        public void TryRestoreState_UnknownStepId_ReturnsFalse()
        {
            GuideHarness harness = NewHarness();

            Assert.False(harness.Restore(new GuideSaveState(9, 0, 0, false)));
            Assert.False(harness.Restore(new GuideSaveState(-3, 0, 0, false)));

            // 4 步的表上"没有当前步"意味着已结束，但位图并不完整 —— 同样属于坏档。
            Assert.False(harness.Restore(new GuideSaveState(0, 0, 0, false)));
        }

        [Fact]
        public void TryRestoreState_OverlappingMasks_ReturnsFalse()
        {
            GuideHarness harness = NewHarness();

            // 第 2 步既完成又跳过：位图重叠只能是坏档。
            Assert.False(harness.Restore(new GuideSaveState(3, 0b0011, 0b0010, false)));
        }

        [Fact]
        public void TryRestoreState_MaskOutOfRange_ReturnsFalse()
        {
            GuideHarness harness = NewHarness();

            Assert.False(harness.Restore(new GuideSaveState(2, 0b10000, 0, false)));
            Assert.False(harness.Restore(new GuideSaveState(2, -1, 0, false)));
        }

        [Fact]
        public void TryRestoreState_CurrentStepAlreadyDone_ReturnsFalse()
        {
            GuideHarness harness = NewHarness();

            Assert.False(harness.Restore(new GuideSaveState(2, 0b0011, 0, false)));
            Assert.False(harness.Restore(new GuideSaveState(2, 0, 0b0010, false)));
        }

        [Fact]
        public void TryRestoreState_FutureProgress_ReturnsFalse()
        {
            GuideHarness harness = NewHarness();

            // 当前步是 2，却已经跳过了第 3 步：步骤机是线性的，这种进度不可能存在。
            Assert.False(harness.Restore(new GuideSaveState(2, 0b0001, 0b0100, false)));
        }

        [Fact]
        public void TryRestoreState_FinishedState_RequiresAllStepsDone()
        {
            GuideHarness harness = NewHarness();
            Assert.False(harness.Restore(new GuideSaveState(0, 0b0011, 0, false)));

            Assert.True(harness.Restore(new GuideSaveState(0, 0b1111, 0, false)));

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsCompleted);
            Assert.False(snapshot.IsActive);
            Assert.Equal(4, snapshot.CompletedCount);
            Assert.Empty(harness.Events);
        }

        [Fact]
        public void TryRestoreState_SkippedAllState_RestoresWithoutEvents()
        {
            GuideHarness harness = NewHarness();

            Assert.True(harness.Restore(new GuideSaveState(0, 0b0001, 0, true)));

            GuideSnapshot snapshot = harness.Query();
            Assert.True(snapshot.IsSkippedAll);
            Assert.False(snapshot.IsActive);
            Assert.False(snapshot.IsCompleted);
            Assert.Equal(1, snapshot.CompletedCount);
            Assert.Empty(harness.Events);

            // "不再提示"是终态：存档里还挂着当前步属于自相矛盾。
            Assert.False(harness.Restore(new GuideSaveState(3, 0, 0, true)));
        }

        [Fact]
        public void TryRestoreState_BadStateKeepsExistingProgress()
        {
            GuideHarness harness = NewHarness();
            harness.CompleteStep(1, GuideTriggers.Launch, 0);

            Assert.False(harness.Restore(new GuideSaveState(9, 0, 0, false)));

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(2, snapshot.StepId);
            Assert.Equal(1, snapshot.CompletedCount);
            Assert.True(snapshot.Steps[0].Completed);
            Assert.True(snapshot.IsActive);
        }

        [Fact]
        public void TryRestoreState_CanBeCalledRepeatedly()
        {
            GuideHarness harness = NewHarness();
            Assert.True(harness.Restore(new GuideSaveState(3, 0b0011, 0, false)));
            Assert.True(harness.Restore(new GuideSaveState(1, 0, 0, false)));

            GuideSnapshot snapshot = harness.Query();
            Assert.Equal(1, snapshot.StepId);
            Assert.Equal(0, snapshot.CompletedCount);
            Assert.False(snapshot.Steps[0].Completed);
        }

        [Fact]
        public void TryRestoreState_EmptyTable_OnlyAcceptsBlankState()
        {
            GuideHarness harness = GuideHarness.Create(GuideTableStub.WithRows());

            Assert.False(harness.Restore(new GuideSaveState(0, 0b0001, 0, false)));
            Assert.False(harness.Restore(new GuideSaveState(0, 0, 0, true)));
            Assert.False(harness.Restore(new GuideSaveState(1, 0, 0, false)));

            Assert.True(harness.Restore(new GuideSaveState(0, 0, 0, false)));
            Assert.True(harness.Query().IsCompleted);
            Assert.Equal(0, harness.Query().TotalCount);
        }

        // ---------- 夹具 ----------

        private static GuideHarness NewHarness()
        {
            return GuideHarness.Create(GuideTableStub.Standard());
        }

        /// <summary>用标准夹具把状态推到第 4 步（1..3 全部"触发命中 + 显式完成"）。</summary>
        private static void AdvanceToStep4(GuideHarness harness)
        {
            harness.CompleteStep(1, GuideTriggers.Launch, 0);
            harness.CompleteStep(2, GuideTriggers.Lobby, 0);
            harness.CompleteStep(3, GuideTriggers.ShopOpen, 0);
        }
    }
}
