using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AIFramework.TableCheck;
using AIFramework.TableGen;
using Xunit;

namespace AIFramework.TableCheck.Tests
{
    /// <summary>
    /// 数值体检工具的验收：**每条规则都要有一条"故意写坏"的用例证明它真的会红**。
    /// 只测"好数据能通过"没有意义——那只能证明工具没崩。
    /// </summary>
    public sealed class TableCheckRulesTests
    {
        [Fact]
        public void RepoTables_AreHealthy()
        {
            string repoRoot = FindRepoRoot();
            Dictionary<string, TableData> tables = TableCheckCli.LoadTables(Path.Combine(repoRoot, "tables"));

            List<TableIssue> issues = TableCheckRules.Validate(tables);

            Assert.True(tables.Count >= 14, "仓库表数量异常：" + tables.Count);
            Assert.Empty(issues);
        }

        [Fact]
        public void Shop_ZeroPrice_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbShopItem",
                "itemId,priceItemId,priceCount",
                "1001,1,0");

            Assert.Contains(issues, issue => issue.Column == "priceCount" && issue.RowIndex == 0);
        }

        [Fact]
        public void Shop_DuplicateItemId_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbShopItem",
                "itemId,priceItemId,priceCount",
                "2001,1,100;2001,1,200");

            Assert.Contains(issues, issue => issue.Column == "itemId" && issue.Message.Contains("主键重复"));
        }

        [Fact]
        public void Experiment_DuplicateKey_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbExperiment",
                "experimentId,key,variantA,variantB,trafficPercent,enabled",
                "1,ui.same,0,1,50,1;2,ui.same,0,1,10,1");

            Assert.Contains(issues, issue => issue.Column == "key" && issue.Message.Contains("重复"));
        }

        [Fact]
        public void Experiment_TrafficOutOfRange_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbExperiment",
                "experimentId,key,variantA,variantB,trafficPercent,enabled",
                "1,ui.bad,0,1,150,1");

            Assert.Contains(issues, issue => issue.Column == "trafficPercent");
        }

        [Fact]
        public void Experiment_InvalidEnabledFlag_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbExperiment",
                "experimentId,key,variantA,variantB,trafficPercent,enabled",
                "1,ui.bad,0,1,50,2");

            Assert.Contains(issues, issue => issue.Column == "enabled");
        }

        [Fact]
        public void Experiment_EqualVariants_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbExperiment",
                "experimentId,key,variantA,variantB,trafficPercent,enabled",
                "1,ui.same,7,7,50,1");

            Assert.Contains(issues, issue => issue.Column == "variantB");
        }
        [Fact]
        public void Achievement_OutOfRangeMetric_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbAchievement",
                "achievementId,metric,target",
                "1,9,10");

            Assert.Contains(issues, issue => issue.Column == "metric");
        }

        [Fact]
        public void Achievement_ZeroTarget_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbAchievement",
                "achievementId,metric,target",
                "1,0,0");

            Assert.Contains(issues, issue => issue.Column == "target");
        }

        [Fact]
        public void SeasonPass_NonIncreasingXp_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbSeasonPass",
                "level,requiredXp,freeItemId,freeCount,paidItemId,paidCount",
                "1,0,7501,10,0,0;2,0,7501,20,0,0");

            Assert.Contains(issues, issue => issue.Column == "requiredXp");
        }

        [Fact]
        public void SeasonPass_LevelGap_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbSeasonPass",
                "level,requiredXp,freeItemId,freeCount,paidItemId,paidCount",
                "1,0,7501,10,0,0;3,100,7501,20,0,0");

            Assert.Contains(issues, issue => issue.Column == "level");
        }

        [Fact]
        public void SeasonPass_HalfPaidReward_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbSeasonPass",
                "level,requiredXp,freeItemId,freeCount,paidItemId,paidCount",
                "1,0,7501,10,7502,0");

            Assert.Contains(issues, issue => issue.Column == "paidItemId");
        }

        [Fact]
        public void Gacha_AllZeroWeights_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbGachaPool",
                "itemId,weight",
                "2001,0;2002,0");

            Assert.Contains(issues, issue => issue.Column == "weight" && issue.RowIndex == -1);
        }

        [Fact]
        public void Localization_DuplicateKey_IsReported()
        {
            string json = "{\"table\":\"TbLocalization\",\"types\":{\"LocalizationEntry\":["
                + "{\"name\":\"lang\",\"type\":\"int32\"},{\"name\":\"text\",\"type\":\"string\"}]},"
                + "\"columns\":[{\"name\":\"key\",\"type\":\"string\"},{\"name\":\"entries\",\"type\":\"LocalizationEntry[]\"}],"
                + "\"rows\":["
                + "{\"key\":\"ui.play\",\"entries\":[{\"lang\":0,\"text\":\"Play\"}]},"
                + "{\"key\":\"ui.play\",\"entries\":[{\"lang\":0,\"text\":\"Play2\"}]}"
                + "]}";

            TableData table = TableSchemaReader.Parse(json, "TbLocalization");
            List<TableIssue> issues = TableCheckRules.Validate(new Dictionary<string, TableData>(StringComparer.Ordinal)
            {
                { "TbLocalization", table },
            });

            Assert.Contains(issues, issue => issue.Column == "key" && issue.Message.Contains("重复"));
        }

        [Fact]
        public void Guide_IllegalSkippable_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbGuideStep",
                "stepId,title,hint,trigger,targetId,skippable",
                "1,t,h,0,0,2");

            Assert.Contains(issues, issue => issue.Column == "skippable");
        }

        [Fact]
        public void Guide_StepIdGap_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbGuideStep",
                "stepId,title,hint,trigger,targetId,skippable",
                "1,t,h,0,0,1;3,t,h,1,0,1");

            Assert.Contains(issues, issue => issue.Column == "stepId");
        }

        [Fact]
        public void UnknownTable_WithDuplicateIdColumn_IsReported()
        {
            List<TableIssue> issues = ValidateSingle("TbMystery",
                "mysteryId,name",
                "1,a;1,b");

            Assert.Contains(issues, issue => issue.Column == "mysteryId");
        }

        private static List<TableIssue> ValidateSingle(string tableName, string columns, string rows)
        {
            string json = BuildTableJson(tableName, columns, rows);
            TableData table = TableSchemaReader.Parse(json, tableName);
            var tables = new Dictionary<string, TableData>(StringComparer.Ordinal) { { tableName, table } };
            return TableCheckRules.Validate(tables);
        }

        private static string BuildTableJson(string tableName, string columns, string rows)
        {
            string[] columnNames = columns.Split(',');
            string[] rowSpecs = rows.Split(';');
            string[][] cells = new string[rowSpecs.Length][];
            for (int r = 0; r < rowSpecs.Length; r++)
            {
                cells[r] = rowSpecs[r].Split(',');
            }

            // 类型推断：首行该列能解析成 int 就是 int32，否则按 string（表定义必须显式声明类型）。
            var columnTypes = new string[columnNames.Length];
            for (int c = 0; c < columnNames.Length; c++)
            {
                columnTypes[c] = int.TryParse(cells[0][c], out _) ? "int32" : "string";
            }

            var columnJson = new List<string>(columnNames.Length);
            for (int c = 0; c < columnNames.Length; c++)
            {
                columnJson.Add("{\"name\":\"" + columnNames[c] + "\",\"type\":\"" + columnTypes[c] + "\"}");
            }

            var rowsJson = new List<string>(rowSpecs.Length);
            for (int r = 0; r < rowSpecs.Length; r++)
            {
                var pairs = new List<string>(columnNames.Length);
                for (int c = 0; c < columnNames.Length; c++)
                {
                    string value = cells[r][c];
                    pairs.Add(columnTypes[c] == "string"
                        ? "\"" + columnNames[c] + "\":\"" + value + "\""
                        : "\"" + columnNames[c] + "\":" + value);
                }

                rowsJson.Add("{" + string.Join(",", pairs) + "}");
            }

            return "{\"table\":\"" + tableName + "\",\"columns\":[" + string.Join(",", columnJson)
                + "],\"rows\":[" + string.Join(",", rowsJson) + "]}";
        }

        [Fact]
        public void LocalizationRules_HandleStructArrayLayout()
        {
            // 用真实仓库表结构做一次快照校验：TbLocalization 的结构体数组不算"重复 key"。
            string repoRoot = FindRepoRoot();
            Dictionary<string, TableData> tables = TableCheckCli.LoadTables(Path.Combine(repoRoot, "tables"));
            Assert.True(tables.ContainsKey("TbLocalization"));
            Assert.Empty(TableCheckRules.Validate(new Dictionary<string, TableData>(StringComparer.Ordinal)
            {
                { "TbLocalization", tables["TbLocalization"] },
            }));
        }

        internal static string FindRepoRoot()
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

    /// <summary>CLI 层的退出码契约（CI 依赖它区分"有问题"与"用错了"）。</summary>
    public sealed class TableCheckCliTests
    {
        [Fact]
        public void Run_MissingTablesDirectory_ReturnsUsageError()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = TableCheckCli.Run(new[] { "--tables", "no-such-dir" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(TableCheckCli.ExitUsage, exitCode);
            Assert.Contains("找不到表目录", error.ToString());
        }

        [Fact]
        public void Run_UnknownArgument_ReturnsUsageError()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = TableCheckCli.Run(new[] { "--nope" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(TableCheckCli.ExitUsage, exitCode);
        }

        [Fact]
        public void Run_Help_ReturnsSuccess()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = TableCheckCli.Run(new[] { "--help" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(TableCheckCli.ExitSuccess, exitCode);
            Assert.Contains("用法", output.ToString());
        }

        [Fact]
        public void Run_BrokenJson_ReturnsInvalidTable()
        {
            string directory = CreateTempTablesDirectory();
            try
            {
                File.WriteAllText(Path.Combine(directory, "TbBad.json"), "{ not json");
                var output = new StringWriter();
                var error = new StringWriter();

                int exitCode = TableCheckCli.Run(new[] { "--tables", directory }, output, error, AppContext.BaseDirectory);

                Assert.Equal(TableCheckCli.ExitInvalidTable, exitCode);
                Assert.Contains("解析失败", error.ToString());
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_HealthyTable_ReturnsSuccess()
        {
            string directory = CreateTempTablesDirectory();
            try
            {
                File.WriteAllText(Path.Combine(directory, "TbShopItem.json"),
                    "{\"table\":\"TbShopItem\",\"columns\":[{\"name\":\"itemId\",\"type\":\"int32\"},{\"name\":\"priceItemId\",\"type\":\"int32\"},{\"name\":\"priceCount\",\"type\":\"int32\"}],\"rows\":[{\"itemId\":2001,\"priceItemId\":1,\"priceCount\":100}]}");
                var output = new StringWriter();
                var error = new StringWriter();

                int exitCode = TableCheckCli.Run(new[] { "--tables", directory }, output, error, AppContext.BaseDirectory);

                Assert.Equal(TableCheckCli.ExitSuccess, exitCode);
                Assert.Contains("通过", output.ToString());
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void Run_ViolatingTable_ReturnsViolations()
        {
            string directory = CreateTempTablesDirectory();
            try
            {
                File.WriteAllText(Path.Combine(directory, "TbShopItem.json"),
                    "{\"table\":\"TbShopItem\",\"columns\":[{\"name\":\"itemId\",\"type\":\"int32\"},{\"name\":\"priceItemId\",\"type\":\"int32\"},{\"name\":\"priceCount\",\"type\":\"int32\"}],\"rows\":[{\"itemId\":2001,\"priceItemId\":1,\"priceCount\":0}]}");
                var output = new StringWriter();
                var error = new StringWriter();

                int exitCode = TableCheckCli.Run(new[] { "--tables", directory }, output, error, AppContext.BaseDirectory);

                Assert.Equal(TableCheckCli.ExitViolations, exitCode);
                Assert.Contains("priceCount", output.ToString());
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static string CreateTempTablesDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "aifw-tablecheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }
    }
}
