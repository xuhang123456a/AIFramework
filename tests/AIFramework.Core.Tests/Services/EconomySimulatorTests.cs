using System;
using System.Collections.Generic;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 经济模拟器口径：净流入/回收率/道具囤积三类告警，以及"输入为空"的边界。
    /// </summary>
    public sealed class EconomySimulatorTests
    {
        private static EconomyDay Day(long earn, long spend, long earnItems = 0, long spendItems = 0)
        {
            return new EconomyDay(earn, spend, earnItems, spendItems);
        }

        [Fact]
        public void Simulate_BalancedEconomy_NotInflationary()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(1000, 800),
                Day(1000, 900),
            });

            Assert.False(verdict.IsInflationary);
            Assert.Equal(300, verdict.NetCurrency); // (1000+1000) - (800+900)
            Assert.Equal(0.85d, verdict.SinkRatio, 3);
        }

        [Fact]
        public void Simulate_NetCurrencyIsEarnedMinusSpent()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(1000, 400),
                Day(500, 300),
            });

            Assert.Equal(800, verdict.NetCurrency);
            Assert.Equal(2, verdict.Days);
        }

        [Fact]
        public void Simulate_SinkRatio_IsSpentOverEarned()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(1000, 700),
            });

            Assert.Equal(0.7d, verdict.SinkRatio, 3);
        }

        [Fact]
        public void Simulate_SinkRatio_ClampsToOne()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(1000, 5000),
            });

            Assert.Equal(1d, verdict.SinkRatio, 3);
        }

        [Fact]
        public void Simulate_ZeroIncome_SinkRatioIsZero()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(0, 0),
            });

            Assert.Equal(0d, verdict.SinkRatio, 3);
        }

        [Fact]
        public void Simulate_HighNetInflow_IsInflationary()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(1_000_000, 0),
            }, dailyNetThreshold: 1000);

            Assert.True(verdict.IsInflationary);
            Assert.Contains(verdict.Reasons, reason => reason.Contains("净流入"));
        }

        [Fact]
        public void Simulate_LowSinkRatio_IsInflationary()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(1000, 100),
            }, dailyNetThreshold: long.MaxValue, minSinkRatio: 0.6d);

            Assert.True(verdict.IsInflationary);
            Assert.Contains(verdict.Reasons, reason => reason.Contains("回收率"));
        }

        [Fact]
        public void Simulate_ItemHoard_AddsWarning()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(0, 0, earnItems: 100, spendItems: 10),
            }, dailyNetThreshold: long.MaxValue, minSinkRatio: 0d);

            Assert.Contains(verdict.Reasons, reason => reason.Contains("道具净流入"));
        }

        [Fact]
        public void Simulate_ItemBalanced_NoItemWarning()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(0, 0, earnItems: 100, spendItems: 90),
            }, dailyNetThreshold: long.MaxValue, minSinkRatio: 0d);

            Assert.DoesNotContain(verdict.Reasons, reason => reason.Contains("道具净流入"));
        }

        [Fact]
        public void Simulate_EmptyDays_WarnsButDoesNotThrow()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(Array.Empty<EconomyDay>());

            Assert.Equal(0, verdict.Days);
            Assert.True(verdict.IsInflationary);
            Assert.Contains(verdict.Reasons, reason => reason.Contains("没有可模拟的天数"));
        }

        [Fact]
        public void Simulate_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => EconomySimulator.Simulate(null!));
        }

        [Fact]
        public void Simulate_NegativeThresholds_AreClamped()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[] { Day(100, 100) }, -1, -1);

            Assert.False(verdict.IsInflationary);
        }

        [Fact]
        public void Day_NegativeValuesAreClampedToZero()
        {
            EconomyDay day = new EconomyDay(-100, -50, -10, -5);

            Assert.Equal(0, day.EarnedCurrency);
            Assert.Equal(0, day.SpentCurrency);
            Assert.Equal(0, day.EarnedItems);
            Assert.Equal(0, day.SpentItems);
        }

        [Fact]
        public void Verdict_ReportLineContainsFixedFields()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[] { Day(1000, 700) });

            string line = verdict.ToReportLine();

            Assert.Contains("[Economy]", line);
            Assert.Contains("天=1", line);
            Assert.Contains("净货币=300", line);
            Assert.Contains("回收率=", line);
            Assert.Contains("通胀=", line);
        }

        [Fact]
        public void Verdict_Failure_ReportContainsReasons()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[] { Day(1_000_000, 0) });

            Assert.Contains("通胀=是", verdict.ToReportLine());
            Assert.Contains("净流入", verdict.ToReportLine());
        }

        [Fact]
        public void Simulate_MultiDayAverage_UsesPerDayThreshold()
        {
            EconomyVerdict verdict = EconomySimulator.Simulate(new[]
            {
                Day(1000, 0),
                Day(1000, 0),
            }, dailyNetThreshold: 600);

            Assert.Contains(verdict.Reasons, reason => reason.Contains("日均净流入"));
        }
    }
}
