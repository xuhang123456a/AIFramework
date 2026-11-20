using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.OpCheck;
using Xunit;

namespace AIFramework.OpCheck.Tests
{
    /// <summary>
    /// 运营排期体检：同组重叠要按 priority 区分"可裁决"与"不可裁决"，同一道具礼包重叠必须报错。
    /// </summary>
    public sealed class OpsRulesTests
    {
        private static ActivityRow Activity(int id, string name, long start, long end, int group, int priority)
        {
            return new ActivityRow(id, name, start, end, group, priority);
        }

        private static List<OpsIssue> Check(params ActivityRow[] rows)
        {
            var issues = new List<OpsIssue>();
            OpsRules.CheckActivities(rows, nowUnixSeconds: 0, issues);
            return issues;
        }

        [Fact]
        public void Overlaps_LeftClosedRightOpen()
        {
            Assert.True(OpsRules.Overlaps(0, 10, 9, 20));
            Assert.False(OpsRules.Overlaps(0, 10, 10, 20));
            Assert.False(OpsRules.Overlaps(10, 20, 0, 10));
        }

        [Fact]
        public void SameGroup_DifferentPriorityOverlap_IsWarning()
        {
            List<OpsIssue> issues = Check(
                Activity(1, "A", 0, 100, group: 1, priority: 10),
                Activity(2, "B", 50, 150, group: 1, priority: 20));

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Contains("按 priority 裁决", issues[0].Message);
        }

        [Fact]
        public void SameGroup_SamePriorityOverlap_IsError()
        {
            List<OpsIssue> issues = Check(
                Activity(1, "A", 0, 100, group: 1, priority: 20),
                Activity(2, "B", 50, 150, group: 1, priority: 20));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("无法裁决", issues[0].Message);
        }

        [Fact]
        public void DifferentGroups_DoNotConflict()
        {
            List<OpsIssue> issues = Check(
                Activity(1, "A", 0, 100, group: 1, priority: 10),
                Activity(2, "B", 50, 150, group: 2, priority: 10));

            Assert.Empty(issues);
        }

        [Fact]
        public void NonOverlapping_InSameGroup_IsFine()
        {
            List<OpsIssue> issues = Check(
                Activity(1, "A", 0, 100, group: 1, priority: 10),
                Activity(2, "B", 100, 200, group: 1, priority: 10));

            Assert.Empty(issues);
        }

        [Fact]
        public void NoActiveActivityAtNow_AddsWarning()
        {
            List<OpsIssue> issues = Check(Activity(1, "A", 100, 200, group: 1, priority: 10));

            Assert.Contains(issues, issue => !issue.IsError && issue.Subject == "(当前时刻)");
        }

        [Fact]
        public void TooManyConcurrentActivities_AddsWarning()
        {
            var issues = new List<OpsIssue>();
            OpsRules.CheckActivities(new[]
            {
                Activity(1, "A", 0, 1000, group: 1, priority: 10),
                Activity(2, "B", 0, 1000, group: 2, priority: 10),
                Activity(3, "C", 0, 1000, group: 3, priority: 10),
                Activity(4, "D", 0, 1000, group: 4, priority: 10),
            }, nowUnixSeconds: 500, issues);

            Assert.Contains(issues, issue => !issue.IsError && issue.Message.Contains("同时开启"));
        }

        [Fact]
        public void Offers_SameItemOverlap_IsError()
        {
            var issues = new List<OpsIssue>();
            OpsRules.CheckOffers(new[]
            {
                new OfferRow(1, "礼包A", itemId: 1001, start: 0, end: 100),
                new OfferRow(2, "礼包B", itemId: 1001, start: 50, end: 150),
            }, issues);

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("同一道具", issues[0].Message);
        }

        [Fact]
        public void Offers_DifferentItemOverlap_IsFine()
        {
            var issues = new List<OpsIssue>();
            OpsRules.CheckOffers(new[]
            {
                new OfferRow(1, "礼包A", itemId: 1001, start: 0, end: 100),
                new OfferRow(2, "礼包B", itemId: 2001, start: 50, end: 150),
            }, issues);

            Assert.Empty(issues);
        }

        [Fact]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => OpsRules.CheckActivities(null!, 0, new List<OpsIssue>()));
            Assert.Throws<ArgumentNullException>(() => OpsRules.CheckOffers(null!, new List<OpsIssue>()));
        }
    }

    /// <summary>CLI 退出码：有错误 1、正常 0、参数问题 2、表坏 3。</summary>
    public sealed class OpCheckCliTests
    {
        [Fact]
        public void Run_Repository_IsConflictFree()
        {
            string repoRoot = FindRepoRoot();
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = OpCheckCli.Run(Array.Empty<string>(), output, error, repoRoot, nowUnixSeconds: 1764000000L);

            Assert.Equal(OpCheckCli.ExitSuccess, exitCode);
            Assert.Contains("通过", output.ToString());
        }

        [Fact]
        public void Run_MissingDirectory_ReturnsSuccessWithNoTables()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = OpCheckCli.Run(Array.Empty<string>(), output, error, Path.Combine(Path.GetTempPath(), "no-such-ops-dir-" + Guid.NewGuid().ToString("N")), 0L);

            Assert.Equal(OpCheckCli.ExitSuccess, exitCode);
        }

        [Fact]
        public void Run_BadNow_ReturnsUsage()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = OpCheckCli.Run(new[] { "--now", "abc" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(OpCheckCli.ExitUsage, exitCode);
        }

        [Fact]
        public void Run_Help_ReturnsSuccess()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(OpCheckCli.ExitSuccess, OpCheckCli.Run(new[] { "--help" }, output, error, AppContext.BaseDirectory));
        }

        private static string FindRepoRoot()
        {
            string? directory = AppContext.BaseDirectory;
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory, "AIFramework.Pure.sln")))
                {
                    return directory;
                }

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new InvalidOperationException("找不到仓库根。");
        }
    }
}
