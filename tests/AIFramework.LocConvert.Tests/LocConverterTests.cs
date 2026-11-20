using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.LocConvert;
using Xunit;

namespace AIFramework.LocConvert.Tests
{
    /// <summary>
    /// CSV ↔ JSON 双向转换：往返一致、特殊字符转义、冲突检测。
    /// <para>核心场景：翻译同学用 Excel 改了 CSV，导回仓库后 JSON 必须与预期逐行一致。</para>
    /// </summary>
    public sealed class LocConverterTests
    {
        private static List<LocRow> SampleRows()
        {
            return new List<LocRow>
            {
                new LocRow("ui.play", "开始游戏", "Play", "ゲーム開始"),
                new LocRow("ui.shop", "商店", "Shop", ""),
                new LocRow("err.network", "网络异常", "Network error", "通信エラー"),
            };
        }

        [Fact]
        public void ExportCsv_HasHeaderAndRows()
        {
            string csv = LocConverter.ExportCsv(SampleRows());

            Assert.StartsWith(LocConverter.Header, csv, StringComparison.Ordinal);
            Assert.Equal(4, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        [Fact]
        public void ExportThenParse_RoundTrips()
        {
            List<LocRow> rows = SampleRows();
            string csv = LocConverter.ExportCsv(rows);

            Assert.True(LocConverter.TryParseCsv(csv, out List<LocRow> parsed, out string error), error);
            Assert.Equal(rows.Count, parsed.Count);
            Assert.Equal(rows[0].Key, parsed[0].Key);
            Assert.Equal(rows[0].Japanese, parsed[0].Japanese);
            Assert.Equal(rows[1].Japanese, parsed[1].Japanese);
        }

        [Fact]
        public void Csv_EscapedCommaAndQuote_RoundTrips()
        {
            var rows = new List<LocRow> { new LocRow("ui.quote", "a,b", "say \"hi\"", "x") };
            string csv = LocConverter.ExportCsv(rows);

            Assert.True(LocConverter.TryParseCsv(csv, out List<LocRow> parsed, out string error), error);
            Assert.Equal("a,b", parsed[0].Chinese);
            Assert.Equal("say \"hi\"", parsed[0].English);
        }

        [Fact]
        public void Parse_BadHeader_ReturnsFalse()
        {
            Assert.False(LocConverter.TryParseCsv("key,zh,en\nui.play,a,b\n", out _, out string error));
            Assert.Contains("表头", error);
        }

        [Fact]
        public void Parse_WrongColumnCount_ReturnsFalse()
        {
            Assert.False(LocConverter.TryParseCsv("key,zh,en,ja\nui.play,a,b\n", out _, out string error));
            Assert.Contains("列数", error);
        }

        [Fact]
        public void Parse_EmptyKey_ReturnsFalse()
        {
            Assert.False(LocConverter.TryParseCsv("key,zh,en,ja\n,,,,,\n", out _, out _));
        }

        [Fact]
        public void Parse_NullOrEmpty_ReturnsFalse()
        {
            Assert.False(LocConverter.TryParseCsv(null, out _, out _));
            Assert.False(LocConverter.TryParseCsv(string.Empty, out _, out _));
        }

        [Fact]
        public void Parse_HeaderOnly_ReturnsFalse()
        {
            Assert.False(LocConverter.TryParseCsv("key,zh,en,ja\n", out _, out string error));
            Assert.Contains("没有翻译行", error);
        }

        [Fact]
        public void ToJsonThenParse_RoundTrips()
        {
            List<LocRow> rows = SampleRows();
            string json = LocConverter.ToLocalizationJson(rows);

            Assert.True(LocConverter.TryParseLocalizationJson(json, out List<LocRow> parsed, out string error), error);
            Assert.Equal(3, parsed.Count);
            Assert.Equal("ui.play", parsed[0].Key);
            Assert.Equal("开始游戏", parsed[0].Chinese);
            Assert.Equal("Play", parsed[0].English);
            Assert.Equal("ゲーム開始", parsed[0].Japanese);
            Assert.Equal(string.Empty, parsed[1].Japanese);
        }

        [Fact]
        public void ToJson_SkipsEmptyLanguageEntries()
        {
            string json = LocConverter.ToLocalizationJson(new List<LocRow> { new LocRow("ui.x", "中文", "", "") });

            Assert.Contains("\"lang\": 0", json);
            Assert.DoesNotContain("\"lang\": 1", json);
            Assert.DoesNotContain("\"lang\": 2", json);
        }

        [Fact]
        public void ToJson_EscapesQuotesAndNewlines()
        {
            string json = LocConverter.ToLocalizationJson(new List<LocRow> { new LocRow("ui.esc", "含\"引号\"", "line1\\nline2", "") });

            Assert.True(LocConverter.TryParseLocalizationJson(json, out List<LocRow> parsed, out _));
            Assert.Equal("含\"引号\"", parsed[0].Chinese);
        }

        [Fact]
        public void CheckConflicts_DuplicateKey_IsReported()
        {
            var issues = new List<string>();
            LocConverter.CheckConflicts(
                new List<LocRow> { new LocRow("ui.dup", "a", "", ""), new LocRow("ui.dup", "b", "", "") },
                issues);

            Assert.Contains(issues, issue => issue.Contains("重复"));
        }

        [Fact]
        public void CheckConflicts_AllLanguagesEmpty_IsReported()
        {
            var issues = new List<string>();
            LocConverter.CheckConflicts(new List<LocRow> { new LocRow("ui.empty", "", "", "") }, issues);

            Assert.Contains(issues, issue => issue.Contains("三种语言全空"));
        }

        [Fact]
        public void CheckConflicts_CleanRows_NoIssues()
        {
            var issues = new List<string>();
            LocConverter.CheckConflicts(SampleRows(), issues);

            Assert.Empty(issues);
        }

        [Fact]
        public void ParseLocalizationJson_BrokenJson_ReturnsFalse()
        {
            Assert.False(LocConverter.TryParseLocalizationJson("{ broken", out _, out string error));
            Assert.Contains("解析失败", error);
        }

        [Fact]
        public void Cli_Export_RealTable_WritesCsv()
        {
            string repoRoot = FindRepoRoot();
            string csvPath = Path.Combine(Path.GetTempPath(), "locconvert-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                var output = new StringWriter();
                var error = new StringWriter();
                int exitCode = LocConvertCli.Run(new[] { "--tables", Path.Combine(repoRoot, "tables"), "--export", csvPath }, output, error, repoRoot);

                Assert.Equal(LocConvertCli.ExitSuccess, exitCode);
                Assert.True(File.Exists(csvPath));
                Assert.Contains("ui.play", File.ReadAllText(csvPath));
            }
            finally
            {
                if (File.Exists(csvPath))
                {
                    File.Delete(csvPath);
                }
            }
        }

        [Fact]
        public void Cli_Import_ConflictingCsv_AbortsWithoutWriting()
        {
            string repoRoot = FindRepoRoot();
            string csvPath = Path.Combine(Path.GetTempPath(), "locconvert-bad-" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllText(csvPath, "key,zh,en,ja\nui.dup,a,b,c\nui.dup,d,e,f\n");
            string jsonBefore = File.ReadAllText(Path.Combine(repoRoot, "tables", "TbLocalization.json"));
            try
            {
                var output = new StringWriter();
                var error = new StringWriter();
                int exitCode = LocConvertCli.Run(new[] { "--tables", Path.Combine(repoRoot, "tables"), "--import", csvPath }, output, error, repoRoot);

                Assert.Equal(LocConvertCli.ExitIssues, exitCode);
                Assert.Equal(jsonBefore, File.ReadAllText(Path.Combine(repoRoot, "tables", "TbLocalization.json")));
            }
            finally
            {
                File.Delete(csvPath);
            }
        }

        [Fact]
        public void Cli_Help_ReturnsSuccess()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            Assert.Equal(LocConvertCli.ExitSuccess, LocConvertCli.Run(new[] { "--help" }, output, error, AppContext.BaseDirectory));
        }

        [Fact]
        public void Cli_MissingJson_ReturnsIo()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = LocConvertCli.Run(new[] { "--tables", Path.Combine(Path.GetTempPath(), "locconvert-none-" + Guid.NewGuid().ToString("N")) }, output, error, AppContext.BaseDirectory);

            Assert.Equal(LocConvertCli.ExitIo, exitCode);
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
