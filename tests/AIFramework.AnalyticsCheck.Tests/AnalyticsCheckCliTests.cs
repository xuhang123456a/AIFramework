using System;
using System.IO;
using AIFramework.AnalyticsCheck;
using Xunit;

namespace AIFramework.AnalyticsCheck.Tests
{
    /// <summary>CLI 退出码契约与仓库快照。</summary>
    public sealed class AnalyticsCheckCliTests
    {
        [Fact]
        public void Run_RealRepository_Passes()
        {
            string repository = FindRepositoryRoot();
            (int exitCode, string output, string error) = AnalyticsTestSupport.RunCli(repository);

            Assert.Equal(AnalyticsCheckCli.ExitSuccess, exitCode);
            Assert.Contains("错误 0", output);
            Assert.Contains("通过", output);
            Assert.Equal(string.Empty, error);
        }

        [Fact]
        public void Run_UnknownArgument_ReturnsUsage()
        {
            string directory = AnalyticsTestSupport.NewTempDirectory();
            try
            {
                (int exitCode, _, string error) = AnalyticsTestSupport.RunCli(directory, "--nope");

                Assert.Equal(AnalyticsCheckCli.ExitUsage, exitCode);
                Assert.Contains("未知参数", error);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_Help_ReturnsSuccess()
        {
            string directory = AnalyticsTestSupport.NewTempDirectory();
            try
            {
                (int exitCode, string output, _) = AnalyticsTestSupport.RunCli(directory, "--help");

                Assert.Equal(AnalyticsCheckCli.ExitSuccess, exitCode);
                Assert.Contains("用法", output);
                Assert.Contains("analyticscheck", output);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_MissingContract_ReturnsParseFailure()
        {
            string directory = AnalyticsTestSupport.NewTempDirectory();
            try
            {
                (int exitCode, _, string error) = AnalyticsTestSupport.RunCli(directory);

                Assert.Equal(AnalyticsCheckCli.ExitParseFailure, exitCode);
                Assert.Contains("契约解析失败", error);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_DuplicateEventValue_ReturnsErrors()
        {
            string directory = AnalyticsTestSupport.NewTempDirectory();
            try
            {
                const string Constants =
                    "        public const int AppLaunch = 1001;\n"
                    + "        public const int SceneEnter = 1001;\n";

                AnalyticsTestSupport.WriteRepository(
                    directory,
                    AnalyticsTestSupport.ContractSource(Constants),
                    ("src/Game/Ok.cs", "AnalyticsEventIds.AppLaunch;\nAnalyticsEventIds.SceneEnter;\n"));

                (int exitCode, string output, _) = AnalyticsTestSupport.RunCli(directory);

                Assert.Equal(AnalyticsCheckCli.ExitErrors, exitCode);
                Assert.Contains("✗", output);
                Assert.Contains("重复定义", output);
                Assert.Contains("错误 1", output);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_ExcludesTestsAndBuildDirectories()
        {
            string directory = AnalyticsTestSupport.NewTempDirectory();
            try
            {
                AnalyticsTestSupport.WriteRepository(
                    directory,
                    AnalyticsTestSupport.ContractSource(AnalyticsTestSupport.DefaultConstants),
                    ("src/Game/Ok.cs", "AnalyticsEventIds.AppLaunch;\n"),
                    ("src/Game/tests/Bad.cs", "AnalyticsEventIds.Nope;\n"),
                    ("src/Game/obj/Generated.cs", "AnalyticsEventIds.Nope2;\n"));

                (int exitCode, string output, _) = AnalyticsTestSupport.RunCli(directory);

                Assert.Equal(AnalyticsCheckCli.ExitSuccess, exitCode);
                Assert.DoesNotContain("Nope", output);
                Assert.Contains("扫描 2 个", output);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_SummaryContainsCounts()
        {
            string directory = AnalyticsTestSupport.NewTempDirectory();
            try
            {
                AnalyticsTestSupport.WriteRepository(
                    directory,
                    AnalyticsTestSupport.ContractSource(AnalyticsTestSupport.DefaultConstants),
                    ("src/Game/One.cs", "AnalyticsEventIds.AppLaunch;\n"));

                (int exitCode, string output, _) = AnalyticsTestSupport.RunCli(directory);

                Assert.Equal(AnalyticsCheckCli.ExitSuccess, exitCode);
                Assert.Contains("事件 2 个、引用 1 次、错误 0、提示 1。", output);
                Assert.Contains("AppLaunch=1", output);
                Assert.Contains("BattleEnd=0", output);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_RelativeRepoArgument_IsResolvedAgainstWorkingDirectory()
        {
            string directory = AnalyticsTestSupport.NewTempDirectory();
            try
            {
                string repository = Path.Combine(directory, "repo");
                Directory.CreateDirectory(repository);
                AnalyticsTestSupport.WriteRepository(
                    repository,
                    AnalyticsTestSupport.ContractSource(AnalyticsTestSupport.DefaultConstants),
                    ("src/Game/Ok.cs", "AnalyticsEventIds.AppLaunch;\nAnalyticsEventIds.BattleEnd;\n"));

                (int exitCode, string output, _) = AnalyticsTestSupport.RunCli(directory, "--repo", "repo");

                Assert.Equal(AnalyticsCheckCli.ExitSuccess, exitCode);
                Assert.Contains("错误 0", output);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string FindRepositoryRoot()
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

            throw new InvalidOperationException("找不到仓库根（AIFramework.Pure.sln）。");
        }
    }
}
