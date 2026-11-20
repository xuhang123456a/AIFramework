using System;
using System.Collections.Generic;
using AIFramework.Features.Experiment;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.Experiment
{
    /// <summary>
    /// 实验切片行为验收：确定性分桶、禁用实验回退 A、曝光去重与幂等、evaluate 前查询语义、
    /// 快照字段一致性、空表退化、表缺失构造失败。
    /// <para>
    /// 分桶期望值不靠被测代码自证：夹具里的 id/key/流量是固定的，落 A 还是 B 由测试侧
    /// 独立实现的 FNV 结论硬编码下来（10001 号玩家在 50% 上落在 B、在 30% 上落在 A）。
    /// </para>
    /// </summary>
    public sealed class ExperimentHandlerTests
    {
        private const int Player = 10001;

        /// <summary>启用实验条数（= 一个玩家的曝光条数）。</summary>
        private const int EnabledCount = 4;

        private static readonly ExperimentRowSpec[] Rows =
        {
            new ExperimentRowSpec(1, "ui.new_shop", 0, 1, 50, 1),
            new ExperimentRowSpec(2, "balance.gold_rate", 1, 2, 30, 1),
            new ExperimentRowSpec(3, "feature.auto_battle", 0, 1, 0, 1),
            new ExperimentRowSpec(4, "feature.battle_speed", 1, 2, 100, 1),
            new ExperimentRowSpec(5, "event.anniversary_theme", 0, 1, 100, 0),
        };

        [Fact]
        public void Query_BeforeEvaluate_ReturnsEmptyAssignmentsAndZeroExposed()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot snapshot = harness.Query();

            // 推荐语义：未 evaluate 时不给分配——查询不做隐式分桶，否则 UI 随便查一下就会污染曝光。
            Assert.Empty(snapshot.Assignments);
            Assert.Equal(0, snapshot.ExposedCount);
            Assert.Empty(harness.Exposures());
        }

        [Fact]
        public void Evaluate_FillsEveryRowInTableOrder()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot snapshot = harness.Evaluate(Player);

            Assert.Equal(Rows.Length, snapshot.Assignments.Count);
            Assert.Equal(EnabledCount, snapshot.ExposedCount);

            AssertAssignment(snapshot, 1, variant: 1, isB: true, enabled: true);
            AssertAssignment(snapshot, 2, variant: 1, isB: false, enabled: true);
            AssertAssignment(snapshot, 3, variant: 0, isB: false, enabled: true);
            AssertAssignment(snapshot, 4, variant: 2, isB: true, enabled: true);
            AssertAssignment(snapshot, 5, variant: 0, isB: false, enabled: false);
        }

        [Fact]
        public void Evaluate_PublishesOneExposurePerEnabledExperimentInRowOrder()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            harness.SendEvaluate(Player);

            List<ExperimentExposedEvent> exposures = harness.Exposures();
            Assert.Equal(EnabledCount, exposures.Count);

            Assert.Equal(new[] { 1, 2, 3, 4 }, ExposureIds(exposures));
            for (int i = 0; i < exposures.Count; i++)
            {
                Assert.Equal(Player, exposures[i].PlayerId);
            }
        }

        [Fact]
        public void Evaluate_ExposureVariantMatchesSnapshotAssignment()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot snapshot = harness.Evaluate(Player);
            List<ExperimentExposedEvent> exposures = harness.Exposures();

            for (int i = 0; i < exposures.Count; i++)
            {
                ExperimentExposedEvent evt = exposures[i];
                Assert.Equal(ExperimentHarness.AssignmentOf(snapshot, evt.ExperimentId).Variant, evt.Variant);
            }
        }

        [Fact]
        public void Evaluate_DisabledExperiment_StaysOnVariantAWithoutExposure()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot snapshot = harness.Evaluate(Player);

            // 禁用行的 trafficPercent=100，若误参与分桶会全进 B；这里必须固定回退 variantA。
            ExperimentAssignment disabled = ExperimentHarness.AssignmentOf(snapshot, 5);
            Assert.False(disabled.Enabled);
            Assert.False(disabled.IsB);
            Assert.Equal(0, disabled.Variant);
            Assert.Equal("event.anniversary_theme", disabled.Key);

            List<ExperimentExposedEvent> exposures = harness.Exposures();
            for (int i = 0; i < exposures.Count; i++)
            {
                Assert.NotEqual(5, exposures[i].ExperimentId);
            }

            Assert.Equal(EnabledCount, snapshot.ExposedCount);
        }

        [Fact]
        public void Evaluate_ZeroTraffic_AlwaysKeepsAGroup()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot snapshot = harness.Evaluate(Player);

            ExperimentAssignment zero = ExperimentHarness.AssignmentOf(snapshot, 3);
            Assert.False(zero.IsB);
            Assert.Equal(0, zero.Variant);
            Assert.True(zero.Enabled);
        }

        [Fact]
        public void Evaluate_FullTraffic_AlwaysPutsPlayersInBGroup()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            // 100% 流量的实验对任意玩家都必须是 B（用两个差异极大的 id 各验一次）。
            int[] players = { 1, Player, int.MaxValue, -5 };
            for (int i = 0; i < players.Length; i++)
            {
                ExperimentSnapshot snapshot = harness.Evaluate(players[i]);

                ExperimentAssignment full = ExperimentHarness.AssignmentOf(snapshot, 4);
                Assert.True(full.IsB);
                Assert.Equal(2, full.Variant);
            }
        }

        [Fact]
        public void Evaluate_AssignmentMatchesDeterministicBucketing()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot snapshot = harness.Evaluate(Player);

            // 逐行和公开的分桶纯函数对齐：切片不允许另有分桶规则（例如按 experimentId 而不是 key）。
            for (int i = 0; i < Rows.Length; i++)
            {
                ExperimentRowSpec row = Rows[i];
                ExperimentAssignment assignment = ExperimentHarness.AssignmentOf(snapshot, row.ExperimentId);
                bool expectedB = row.Enabled == 1
                    && ExperimentBucketing.IsB(row.Key, Player, row.TrafficPercent);

                Assert.Equal(expectedB, assignment.IsB);
                Assert.Equal(expectedB ? row.VariantB : row.VariantA, assignment.Variant);
            }
        }

        [Fact]
        public void Evaluate_SamePlayerTwice_KeepsAssignmentsAndExposureCount()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot first = harness.Evaluate(Player);
            ExperimentSnapshot second = harness.Evaluate(Player);

            Assert.Equal(EnabledCount, harness.Exposures().Count);
            Assert.Equal(EnabledCount, second.ExposedCount);
            AssertSameAssignments(first, second);
        }

        [Fact]
        public void RepeatedEvaluate_ExposedCountStaysFlat()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            int baseline = harness.Evaluate(Player).ExposedCount;
            Assert.Equal(EnabledCount, baseline);

            // 幂等口径：重复 evaluate 后快照里的 ExposedCount 必须停在"启用实验数"，
            // 而不是每调一次就 +启用实验数（那样 ΣExposedCount 会随调用次数膨胀）。
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(baseline, harness.Evaluate(Player).ExposedCount);
            }

            Assert.Equal(EnabledCount, harness.Handler.ExposedCount);
        }

        [Fact]
        public void Evaluate_DifferentPlayer_AddsExposuresForTheNewPlayer()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            harness.Evaluate(Player);
            ExperimentSnapshot other = harness.Evaluate(Player + 1);

            List<ExperimentExposedEvent> exposures = harness.Exposures();
            Assert.Equal(EnabledCount * 2, exposures.Count);
            Assert.Equal(EnabledCount * 2, other.ExposedCount);

            // 后一半必须都属于新玩家（同一个 (experimentId, playerId) 才对，换玩家就是新曝光）。
            for (int i = EnabledCount; i < exposures.Count; i++)
            {
                Assert.Equal(Player + 1, exposures[i].PlayerId);
            }
        }

        [Fact]
        public void Evaluate_BackToFirstPlayer_DoesNotReExpose()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            harness.Evaluate(Player);
            harness.Evaluate(Player + 1);
            ExperimentSnapshot back = harness.Evaluate(Player);

            // (experimentId, playerId) 去重是全局的：回到旧玩家不补发、不增长计数。
            Assert.Equal(EnabledCount * 2, harness.Exposures().Count);
            Assert.Equal(EnabledCount * 2, back.ExposedCount);
            AssertAssignment(back, 1, variant: 1, isB: true, enabled: true);
        }

        [Fact]
        public void Evaluate_NegativePlayerId_IsDeterministicAcrossInstances()
        {
            ExperimentHarness first = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));
            ExperimentHarness second = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot left = first.Evaluate(-5);
            ExperimentSnapshot right = second.Evaluate(-5);

            // 两个独立实例对同一玩家必须给出同一份分配：分桶里不允许有进程级随机化。
            AssertSameAssignments(left, right);
        }

        [Fact]
        public void Evaluate_EmptyTable_ReturnsEmptySnapshotWithoutEvents()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows());

            ExperimentSnapshot snapshot = harness.Evaluate(Player);

            Assert.Empty(snapshot.Assignments);
            Assert.Equal(0, snapshot.ExposedCount);
            Assert.Empty(harness.Exposures());
        }

        [Fact]
        public void Query_AfterEvaluate_IsStableAndPublishesNothing()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));
            ExperimentSnapshot afterEvaluate = harness.Evaluate(Player);

            ExperimentSnapshot firstQuery = harness.Query();
            ExperimentSnapshot secondQuery = harness.Query();

            AssertSameAssignments(afterEvaluate, firstQuery);
            AssertSameAssignments(firstQuery, secondQuery);
            Assert.Equal(EnabledCount, harness.Exposures().Count);
        }

        [Fact]
        public void Snapshot_VariantMatchesGroupAndTableValues()
        {
            ExperimentHarness harness = ExperimentHarness.Create(ExperimentTableStub.WithRows(Rows));

            ExperimentSnapshot snapshot = harness.Evaluate(Player);

            for (int i = 0; i < snapshot.Assignments.Count; i++)
            {
                ExperimentAssignment assignment = snapshot.Assignments[i];
                ExperimentRowSpec row = Rows[i];

                Assert.Equal(row.ExperimentId, assignment.ExperimentId);
                Assert.Equal(row.Key, assignment.Key);

                if (!assignment.Enabled)
                {
                    // 禁用实验：Enabled=false / IsB=false / 取 variantA。
                    Assert.False(assignment.IsB);
                    Assert.Equal(row.VariantA, assignment.Variant);
                    continue;
                }

                Assert.Equal(assignment.IsB ? row.VariantB : row.VariantA, assignment.Variant);
            }
        }

        [Fact]
        public void Constructor_NullBus_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new ExperimentHandler(null!, ExperimentTableStub.WithRows(Rows)));
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new ExperimentHandler(new RecordingBus(), null!));
        }

        [Fact]
        public void Constructor_MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new ExperimentHandler(new RecordingBus(), ExperimentTableStub.WithoutTable()));

            Assert.Contains(ExperimentTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Register_NullBus_Throws()
        {
            var handler = new ExperimentHandler(new RecordingBus(), ExperimentTableStub.WithRows(Rows));

            Assert.Throws<ArgumentNullException>(() => handler.Register(null!));
        }

        /// <summary>断言某实验的分配四元组；顺带校验 key 也带上了（UI 侧要按 key 反查业务）。</summary>
        private static void AssertAssignment(
            ExperimentSnapshot snapshot,
            int experimentId,
            int variant,
            bool isB,
            bool enabled)
        {
            ExperimentAssignment assignment = ExperimentHarness.AssignmentOf(snapshot, experimentId);
            Assert.Equal(variant, assignment.Variant);
            Assert.Equal(isB, assignment.IsB);
            Assert.Equal(enabled, assignment.Enabled);
            Assert.True(assignment.Key.Length > 0, "experimentId " + experimentId + " 的 key 为空。");
        }

        /// <summary>逐项比对两份快照的分配（顺序 + 每个字段）。</summary>
        private static void AssertSameAssignments(ExperimentSnapshot expected, ExperimentSnapshot actual)
        {
            Assert.Equal(expected.Assignments.Count, actual.Assignments.Count);
            for (int i = 0; i < expected.Assignments.Count; i++)
            {
                ExperimentAssignment left = expected.Assignments[i];
                ExperimentAssignment right = actual.Assignments[i];

                Assert.Equal(left.ExperimentId, right.ExperimentId);
                Assert.Equal(left.Key, right.Key);
                Assert.Equal(left.Variant, right.Variant);
                Assert.Equal(left.IsB, right.IsB);
                Assert.Equal(left.Enabled, right.Enabled);
            }
        }

        private static int[] ExposureIds(List<ExperimentExposedEvent> exposures)
        {
            var ids = new int[exposures.Count];
            for (int i = 0; i < exposures.Count; i++)
            {
                ids[i] = exposures[i].ExperimentId;
            }

            return ids;
        }
    }
}
