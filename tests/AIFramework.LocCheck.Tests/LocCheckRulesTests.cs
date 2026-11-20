using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.LocCheck;
using AIFramework.TableGen;
using Xunit;

namespace AIFramework.LocCheck.Tests
{
    /// <summary>
    /// 本地化完整性工具的验收。核心口径：**缺 key 必须是错误，未被引用只是提示**
    /// （缺 key 在运行时会静默回退成 key 本身，正是最容易漏掉的一类线上问题）。
    /// </summary>
    public sealed class LocCheckRulesTests
    {
        [Fact]
        public void ScanReferences_FindsPrefixedKeysWithLocations()
        {
            string directory = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(directory, "Hud.cs"),
                    "var text = Query(\"ui.play\");\nvar other = \"ui.shop\";\nvar ignored = \"other.key\";");

                Dictionary<string, List<string>> references = LocCheckRules.ScanReferences(new[] { directory });

                Assert.True(references.ContainsKey("ui.play"));
                Assert.True(references.ContainsKey("ui.shop"));
                Assert.False(references.ContainsKey("other.key"));
                Assert.Contains("Hud.cs:1", references["ui.play"][0]);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void ScanReferences_IgnoreMarker_SkipsLine()
        {
            string directory = NewTempDirectory();
            try
            {
                File.WriteAllText(Path.Combine(directory, "Hud.cs"),
                    "var missing = Query(\"ui.missing\"); // loccheck:ignore 故意缺 key\nvar real = Query(\"ui.real\");");

                Dictionary<string, List<string>> references = LocCheckRules.ScanReferences(new[] { directory });

                Assert.False(references.ContainsKey("ui.missing"));
                Assert.True(references.ContainsKey("ui.real"));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void CrossCheck_MissingKey_IsError()
        {
            var references = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                { "ui.missing", new List<string> { "a.cs:1" } },
            };

            List<LocIssue> issues = LocCheckRules.CrossCheck(references, new[] { "ui.play" });

            Assert.Contains(issues, issue => issue.Key == "ui.missing" && issue.IsError);
        }

        [Fact]
        public void CrossCheck_UnusedTableKey_IsWarningOnly()
        {
            var references = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            List<LocIssue> issues = LocCheckRules.CrossCheck(references, new[] { "ui.unused" });

            LocIssue issue = Assert.Single(issues);
            Assert.False(issue.IsError);
        }

        [Fact]
        public void ReadTableKeys_RejectsDuplicatesAndEmptyKeys()
        {
            TableData table = ParseLocalization(new[]
            {
                ("ui.play", 0, "开始游戏"),
                ("ui.play", 1, "Play"),
                ("", 0, "空 key"),
            });

            var issues = new List<LocIssue>();
            HashSet<string> keys = LocCheckRules.ReadTableKeys(table, issues);

            Assert.Single(keys);
            Assert.Contains(issues, issue => issue.Message.Contains("重复") && issue.IsError);
            Assert.Contains(issues, issue => issue.Message.Contains("空 key") && issue.IsError);
        }

        [Fact]
        public void ReadTableKeys_ReturnsAllUniqueKeys()
        {
            TableData table = ParseLocalization(new[]
            {
                ("ui.play", 0, "开始游戏"),
                ("ui.shop", 0, "商店"),
            });

            var issues = new List<LocIssue>();
            HashSet<string> keys = LocCheckRules.ReadTableKeys(table, issues);

            Assert.Equal(2, keys.Count);
            Assert.Empty(issues);
        }

        private static TableData ParseLocalization((string Key, int Lang, string Text)[] rows)
        {
            string json = "{\"table\":\"TbLocalization\",\"types\":{\"LocalizationEntry\":["
                + "{\"name\":\"lang\",\"type\":\"int32\"},{\"name\":\"text\",\"type\":\"string\"}]},"
                + "\"columns\":[{\"name\":\"key\",\"type\":\"string\"},{\"name\":\"entries\",\"type\":\"LocalizationEntry[]\"}],"
                + "\"rows\":[";

            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    json += ",";
                }

                json += "{\"key\":\"" + rows[i].Key + "\",\"entries\":[{\"lang\":" + rows[i].Lang
                    + ",\"text\":\"" + rows[i].Text + "\"}]}";
            }

            json += "]}";
            return TableSchemaReader.Parse(json, "TbLocalization");
        }

        internal static string NewTempDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "aifw-loccheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    /// <summary>CLI 退出码契约与仓库快照。</summary>
    public sealed class LocCheckCliTests
    {
        [Fact]
        public void Run_Repository_IsConsistent()
        {
            string repoRoot = FindRepoRoot();
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = LocCheckCli.Run(Array.Empty<string>(), output, error, repoRoot);

            Assert.Equal(LocCheckCli.ExitSuccess, exitCode);
            Assert.Contains("通过", output.ToString());
        }

        [Fact]
        public void Run_MissingTablesDirectory_ReturnsUsageError()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = LocCheckCli.Run(new[] { "--tables", "no-such-dir" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(LocCheckCli.ExitUsage, exitCode);
        }

        [Fact]
        public void Run_Help_ReturnsSuccess()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = LocCheckCli.Run(new[] { "--help" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(LocCheckCli.ExitSuccess, exitCode);
            Assert.Contains("用法", output.ToString());
        }

        [Fact]
        public void Run_MissingReferencedKey_ReturnsErrors()
        {
            string directory = LocCheckRulesTests.NewTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(directory, "tables"));
                File.WriteAllText(Path.Combine(directory, "tables", "TbLocalization.json"),
                    "{\"table\":\"TbLocalization\",\"types\":{\"LocalizationEntry\":[{\"name\":\"lang\",\"type\":\"int32\"},{\"name\":\"text\",\"type\":\"string\"}]},"
                    + "\"columns\":[{\"name\":\"key\",\"type\":\"string\"},{\"name\":\"entries\",\"type\":\"LocalizationEntry[]\"}],"
                    + "\"rows\":[{\"key\":\"ui.play\",\"entries\":[{\"lang\":0,\"text\":\"开始\"}]}]}");
                Directory.CreateDirectory(Path.Combine(directory, "src"));
                File.WriteAllText(Path.Combine(directory, "src", "Hud.cs"), "Query(\"ui.missing\");");

                var output = new StringWriter();
                var error = new StringWriter();
                int exitCode = LocCheckCli.Run(Array.Empty<string>(), output, error, directory);

                Assert.Equal(LocCheckCli.ExitErrors, exitCode);
                Assert.Contains("ui.missing", output.ToString());
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_StrictMode_TreatsUnusedKeysAsErrors()
        {
            string directory = LocCheckRulesTests.NewTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(directory, "tables"));
                File.WriteAllText(Path.Combine(directory, "tables", "TbLocalization.json"),
                    "{\"table\":\"TbLocalization\",\"types\":{\"LocalizationEntry\":[{\"name\":\"lang\",\"type\":\"int32\"},{\"name\":\"text\",\"type\":\"string\"}]},"
                    + "\"columns\":[{\"name\":\"key\",\"type\":\"string\"},{\"name\":\"entries\",\"type\":\"LocalizationEntry[]\"}],"
                    + "\"rows\":[{\"key\":\"ui.unused\",\"entries\":[{\"lang\":0,\"text\":\"未用\"}]}]}");

                var output = new StringWriter();
                var error = new StringWriter();
                int exitCode = LocCheckCli.Run(new[] { "--strict" }, output, error, directory);

                Assert.Equal(LocCheckCli.ExitErrors, exitCode);
                Assert.Contains("ui.unused", output.ToString());
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_BrokenTable_ReturnsInvalidTable()
        {
            string directory = LocCheckRulesTests.NewTempDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(directory, "tables"));
                File.WriteAllText(Path.Combine(directory, "tables", "TbLocalization.json"), "{ broken");

                var output = new StringWriter();
                var error = new StringWriter();
                int exitCode = LocCheckCli.Run(Array.Empty<string>(), output, error, directory);

                Assert.Equal(LocCheckCli.ExitInvalidTable, exitCode);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
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

            throw new InvalidOperationException("找不到仓库根（AIFramework.Pure.sln）。");
        }
    }
}
