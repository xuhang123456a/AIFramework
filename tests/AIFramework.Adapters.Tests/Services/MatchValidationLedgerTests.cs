using System;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Adapters.Tests.Services
{
    /// <summary>
    /// <see cref="MatchValidationLedger"/> 的行为契约验收：三条硬规则（重复指纹 / 分数合理 / 对局长度）、
    /// 环形容量溢出语义，以及"预热后 1000 次校验零分配"这条热路径红线。
    /// </summary>
    public sealed class MatchValidationLedgerTests
    {
        [Fact]
        public void Validate_NormalSummary_ReturnsAcceptedWithEchoedScore()
        {
            var ledger = new MatchValidationLedger();

            ValidationResponse response = ledger.Validate(Summary(0x5EEDL, frameCount: 600, score: 1200));

            Assert.True(response.IsAccepted);
            Assert.Equal(NetworkValidationResult.Accepted, response.Result);
            Assert.Equal(1200, response.ServerScore);
            Assert.Equal(string.Empty, response.Reason);
            Assert.Equal(1, ledger.Count);
        }

        [Fact]
        public void Validate_SameFingerprintTwice_RejectsSecondAsDuplicate()
        {
            var ledger = new MatchValidationLedger();

            ValidationResponse first = ledger.Validate(Summary(0x5EEDL));
            ValidationResponse second = ledger.Validate(Summary(0x5EEDL));

            Assert.True(first.IsAccepted);
            Assert.False(second.IsAccepted);
            Assert.Equal(NetworkValidationResult.Rejected, second.Result);
            Assert.Equal(MatchValidationLedger.ReasonDuplicate, second.Reason);

            // 被拒绝的那次不得占用容量（否则重放一次就能把台账挤满）。
            Assert.Equal(1, ledger.Count);
        }

        [Fact]
        public void Validate_NegativeScore_Rejected()
        {
            var ledger = new MatchValidationLedger();

            ValidationResponse response = ledger.Validate(Summary(1L, frameCount: 600, score: -1));

            Assert.False(response.IsAccepted);
            Assert.Equal(MatchValidationLedger.ReasonNegativeScore, response.Reason);
            Assert.Equal(0, response.ServerScore);
        }

        [Fact]
        public void Validate_ScoreAboveFrameCountTimesTen_Rejected()
        {
            var ledger = new MatchValidationLedger();

            ValidationResponse response = ledger.Validate(Summary(1L, frameCount: 600, score: 6001));

            Assert.False(response.IsAccepted);
            Assert.Equal(MatchValidationLedger.ReasonScoreExceeded, response.Reason);
        }

        [Fact]
        public void Validate_ScoreExactlyAtCeiling_Accepted()
        {
            var ledger = new MatchValidationLedger();

            // 边界必须闭区间：600 * 10 = 6000 是合法的，6001 才非法。
            ValidationResponse response = ledger.Validate(Summary(1L, frameCount: 600, score: 6000));

            Assert.True(response.IsAccepted);
            Assert.Equal(6000, response.ServerScore);
        }

        [Fact]
        public void Validate_FrameCountAboveMaxFrames_Rejected()
        {
            var ledger = new MatchValidationLedger();

            ValidationResponse response = ledger.Validate(
                Summary(1L, frameCount: MatchValidationLedger.MaxFrames + 1, score: 0));

            Assert.False(response.IsAccepted);
            Assert.Equal(MatchValidationLedger.ReasonFrameCountExceeded, response.Reason);
        }

        [Fact]
        public void Validate_FrameCountExactlyAtMaxFrames_Accepted()
        {
            var ledger = new MatchValidationLedger();

            ValidationResponse response = ledger.Validate(
                Summary(1L, frameCount: MatchValidationLedger.MaxFrames, score: 0));

            Assert.True(response.IsAccepted);
        }

        [Fact]
        public void Validate_NegativeFrameCount_Rejected()
        {
            var ledger = new MatchValidationLedger();

            ValidationResponse response = ledger.Validate(Summary(1L, frameCount: -1, score: 0));

            Assert.False(response.IsAccepted);
            Assert.Equal(MatchValidationLedger.ReasonNegativeFrameCount, response.Reason);
        }

        [Fact]
        public void Validate_CapacityOverflow_ReplacesOldestFingerprint()
        {
            var ledger = new MatchValidationLedger(2);

            Assert.True(ledger.Validate(Summary(11L)).IsAccepted);
            Assert.True(ledger.Validate(Summary(22L)).IsAccepted);
            Assert.Equal(2, ledger.Count);

            // 第三条挤掉最旧的 11（环形覆盖，不扩容、不抛）。
            Assert.True(ledger.Validate(Summary(33L)).IsAccepted);
            Assert.Equal(2, ledger.Count);
            Assert.Equal(2, ledger.Capacity);

            // 22 还在台账里 → 仍被判重。
            Assert.False(ledger.Validate(Summary(22L)).IsAccepted);

            // 11 已被淘汰 → 重新可提交（这正是"重放窗口"的边界语义）。
            Assert.True(ledger.Validate(Summary(11L)).IsAccepted);
        }

        [Fact]
        public void Validate_AfterWarmUp_AllocatesNothingOnHeap()
        {
            var ledger = new MatchValidationLedger();

            // 预热：把 JIT、字面量驻留、拒绝分支与环形回绕都跑热，排除一次性分配。
            for (int i = 0; i < 300; i++)
            {
                ledger.Validate(Summary(900000L + i, frameCount: 60, score: 10));
            }

            ledger.Reset();

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 1000; i++)
            {
                // 容量 256 < 1000：后半程持续触发环形覆盖，确保覆盖分支也在被测范围内。
                ValidationResponse response = ledger.Validate(Summary(1000000L + i, frameCount: 240, score: 100));
                Assert.True(response.IsAccepted);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.Equal(0L, allocated);
            Assert.Equal(MatchValidationLedger.DefaultCapacity, ledger.Count);
        }

        [Fact]
        public void Constructor_NonPositiveCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MatchValidationLedger(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MatchValidationLedger(-1));
        }

        [Fact]
        public void ValidationResponse_IsAccepted_OnlyTrueForAcceptedResult()
        {
            Assert.True(new ValidationResponse(NetworkValidationResult.Accepted, 100, "ok").IsAccepted);
            Assert.False(new ValidationResponse(NetworkValidationResult.Rejected, 0, "duplicate").IsAccepted);
            Assert.False(new ValidationResponse(NetworkValidationResult.Unavailable, 0, "offline").IsAccepted);
            Assert.False(new ValidationResponse(NetworkValidationResult.Timeout, 0, "timeout").IsAccepted);
        }

        [Fact]
        public void ValidationResponse_NullReason_BecomesEmptyString()
        {
            // Reason 是诊断字段，绝不允许为了它抛异常；调用方传 null 时必须归一成空串。
            var response = new ValidationResponse(NetworkValidationResult.Rejected, 0, null!);

            Assert.Equal(string.Empty, response.Reason);
        }

        /// <summary>构造一份合法摘要；只覆盖被测字段，其余取固定值。</summary>
        private static MatchSummary Summary(long fingerprint, int frameCount = 600, int score = 1200)
        {
            return new MatchSummary(
                kernelType: 2,
                levelId: 1,
                randomSeed: 20261120,
                frameCount: frameCount,
                stateFingerprint: fingerprint,
                score: score);
        }
    }
}
