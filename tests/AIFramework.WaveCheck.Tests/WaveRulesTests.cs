#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.TableGen;
using AIFramework.WaveCheck;
using Xunit;

namespace AIFramework.WaveCheck.Tests
{
    /// <summary>测试侧小工具：把"一组波次行"变成一份 <c>TbArenaWave.json</c> 文本。</summary>
    internal static class WaveJson
    {
        internal static string Build(params WaveRow[] rows)
        {
            var builder = new StringBuilder(768);
            builder.Append(@"{ ""table"": ""TbArenaWave"", ""columns"": [");
            AppendColumn(builder, "waveIndex", first: true);
            AppendColumn(builder, "enemyCount");
            AppendColumn(builder, "enemyHp");
            AppendColumn(builder, "enemySpeedPermille");
            AppendColumn(builder, "spawnIntervalMs");
            AppendColumn(builder, "rewardGold");
            AppendColumn(builder, "bossFlag");
            builder.Append("], \"rows\": [");

            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                WaveRow row = rows[i];
                builder.Append(@"{ ""waveIndex"": ").Append(row.WaveIndex)
                    .Append(@", ""enemyCount"": ").Append(row.EnemyCount)
                    .Append(@", ""enemyHp"": ").Append(row.EnemyHp)
                    .Append(@", ""enemySpeedPermille"": ").Append(row.EnemySpeedPermille)
                    .Append(@", ""spawnIntervalMs"": ").Append(row.SpawnIntervalMs)
                    .Append(@", ""rewardGold"": ").Append(row.RewardGold)
                    .Append(@", ""bossFlag"": ").Append(row.BossFlag)
                    .Append(" }");
            }

            builder.Append("] }");
            return builder.ToString();
        }

        private static void AppendColumn(StringBuilder builder, string name, bool first = false)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(@"{ ""name"": """).Append(name).Append(@""", ""type"": ""int32"" }");
        }
    }

    /// <summary>CLI 一次调用的结果快照。</summary>
    internal sealed class CliResult
    {
        internal int Code { get; }

        internal string Out { get; }

        internal string Err { get; }

        internal CliResult(int code, string output, string error)
        {
            Code = code;
            Out = output;
            Err = error;
        }
    }

    /// <summary>临时工作区：<c>tables/</c> 放表 JSON，用完删干净。</summary>
    internal sealed class TempTables : IDisposable
    {
        internal string Root { get; }

        internal string TablesDirectory
        {
            get { return Path.Combine(Root, "tables"); }
        }

        internal TempTables()
        {
            Root = Path.Combine(Path.GetTempPath(), "aifw-wavecheck-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TablesDirectory);
        }

        internal void WriteTable(string json)
        {
            File.WriteAllText(Path.Combine(TablesDirectory, WaveCheckCli.TableFileName), json, new UTF8Encoding(false));
        }

        internal CliResult Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = WaveCheckCli.Run(args, output, error, Root);
            return new CliResult(code, output.ToString(), error.ToString());
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响用例结论（下次跑用新 GUID）。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// 跨行时间轴规则：连续/不连续、难度回落、节奏突变、奖励脱钩、Boss 位置与波数边界。
    /// <para>断言按规则 id 过滤，避免"总波数不足 5"这类伴生提示把用例写得又脆又长。</para>
    /// </summary>
    public sealed class WaveRuleTests
    {
        private static WaveRow Wave(
            int index,
            int count = 4,
            int hp = 100,
            int speed = 1000,
            int spawn = 1000,
            int gold = 100,
            int boss = 0)
        {
            return new WaveRow(index, count, hp, speed, spawn, gold, boss);
        }

        private static List<WaveIssue> AllIssues(params WaveRow[] rows)
        {
            var issues = new List<WaveIssue>();
            WaveRules.Validate(rows, issues);
            return issues;
        }

        private static List<WaveIssue> IssuesOf(string rule, params WaveRow[] rows)
        {
            List<WaveIssue> all = AllIssues(rows);
            var filtered = new List<WaveIssue>();
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Rule, rule, StringComparison.Ordinal))
                {
                    filtered.Add(all[i]);
                }
            }

            return filtered;
        }

        [Fact]
        public void DifficultyFormula_MultipliesCountHpAndSpeedPermille()
        {
            Assert.Equal(600L, WaveRules.Difficulty(Wave(1, count: 3, hp: 100, speed: 1000)));
            Assert.Equal(300L, WaveRules.Difficulty(Wave(1, count: 3, hp: 100, speed: 0)));
            Assert.Equal(780L, WaveRules.Difficulty(Wave(1, count: 3, hp: 100, speed: 1600)));
        }

        [Fact]
        public void ClearTimeFormula_MultipliesCountAndSpawnInterval()
        {
            Assert.Equal(4000L, WaveRules.ClearTimeMs(Wave(1, count: 4, spawn: 1000)));
            Assert.Equal(4750L, WaveRules.ClearTimeMs(Wave(1, count: 5, spawn: 950)));
        }

        [Fact]
        public void RewardPerDifficulty_ZeroDifficultyIsZeroNotInfinity()
        {
            Assert.Equal(0d, WaveRules.RewardPerDifficulty(Wave(1, count: 0, gold: 100)));
            Assert.Equal(0.25d, WaveRules.RewardPerDifficulty(Wave(1, count: 2, hp: 100, gold: 100)));
        }

        [Fact]
        public void EmptyTable_IsError()
        {
            List<WaveIssue> issues = IssuesOf("count");
            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("一行波次都没有", issues[0].Message);
        }

        [Fact]
        public void ContinuousWaveIndex_ProducesNoStructureIssue()
        {
            List<WaveIssue> issues = IssuesOf("structure", Wave(1), Wave(2), Wave(3));
            Assert.Empty(issues);
        }

        [Fact]
        public void NonContinuousWaveIndex_IsError()
        {
            List<WaveIssue> issues = IssuesOf("structure", Wave(1), Wave(2), Wave(4));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("连续递增", issues[0].Message);
        }

        [Fact]
        public void DuplicateWaveIndex_IsError()
        {
            List<WaveIssue> issues = IssuesOf("structure", Wave(1), Wave(2), Wave(2));

            Assert.Contains(issues, issue => issue.IsError && issue.Message.Contains("重复"));
        }

        [Fact]
        public void DifficultyDrop_IsError()
        {
            List<WaveIssue> issues = IssuesOf("difficulty", Wave(1, count: 4, hp: 100), Wave(2, count: 2, hp: 100));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Equal(2, issues[0].WaveIndex);
            Assert.Contains("低于上一波", issues[0].Message);
        }

        [Fact]
        public void DifficultyEqual_IsAccepted()
        {
            Assert.Empty(IssuesOf("difficulty", Wave(1, count: 4, hp: 100), Wave(2, count: 4, hp: 100)));
        }

        [Fact]
        public void DifficultyRising_IsAccepted()
        {
            Assert.Empty(IssuesOf("difficulty", Wave(1, count: 2, hp: 10), Wave(2, count: 3, hp: 10)));
        }

        [Fact]
        public void PacingShorterThanPrevious_IsWarningOnly()
        {
            List<WaveIssue> issues = IssuesOf("pacing", Wave(1, count: 4, spawn: 1000), Wave(2, count: 2, spawn: 1000));

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Contains("节奏突然变快", issues[0].Message);
        }

        [Fact]
        public void PacingExactlyThreeTimesPrevious_IsAccepted()
        {
            Assert.Empty(IssuesOf("pacing", Wave(1, count: 2, spawn: 500), Wave(2, count: 2, spawn: 1500)));
        }

        [Fact]
        public void PacingOverThreeTimesPrevious_IsWarning()
        {
            List<WaveIssue> issues = IssuesOf("pacing", Wave(1, count: 2, spawn: 500), Wave(2, count: 2, spawn: 1600));

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Contains("长时间卡顿", issues[0].Message);
        }

        [Fact]
        public void RewardRatioDriftOverHalf_IsWarning()
        {
            List<WaveIssue> issues = IssuesOf("reward", Wave(1, count: 4, hp: 100, gold: 200), Wave(2, count: 4, hp: 100, gold: 400));

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Contains("奖励与强度脱钩", issues[0].Message);
        }

        [Fact]
        public void RewardRatioDriftWithinHalf_IsAccepted()
        {
            Assert.Empty(IssuesOf("reward", Wave(1, count: 4, hp: 100, gold: 200), Wave(2, count: 4, hp: 100, gold: 260)));
        }

        [Fact]
        public void RewardRatioExactlyHalfDrift_IsAccepted()
        {
            // 阈值是"> 50%"：正好 50% 不该报警（0.5 → 0.75，相对波动恰好 50%）。
            Assert.Empty(IssuesOf("reward", Wave(1, count: 4, hp: 100, gold: 200), Wave(2, count: 4, hp: 100, gold: 300)));
        }

        [Fact]
        public void RewardRatioFromZeroToPositive_IsWarning()
        {
            List<WaveIssue> issues = IssuesOf("reward", Wave(1, count: 4, hp: 100, gold: 0), Wave(2, count: 4, hp: 100, gold: 100));

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
        }

        [Fact]
        public void BossNotLast_IsError()
        {
            var rows = new[]
            {
                Wave(1), Wave(2), Wave(3, boss: 1), Wave(4), Wave(5),
            };

            List<WaveIssue> issues = IssuesOf("boss", rows[0], rows[1], rows[2], rows[3], rows[4]);
            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("不是最后一行", issues[0].Message);
        }

        [Fact]
        public void MultipleBossRows_IsError()
        {
            List<WaveIssue> issues = IssuesOf("boss", Wave(1), Wave(2), Wave(3), Wave(4, boss: 1), Wave(5, boss: 1));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("不唯一", issues[0].Message);
        }

        [Fact]
        public void BossWithoutEscortsBeyondFive_IsWarning()
        {
            List<WaveIssue> issues = IssuesOf("boss", Wave(1), Wave(2), Wave(3), Wave(4), Wave(5, count: 6, boss: 1));

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Contains("小怪", issues[0].Message);
        }

        [Fact]
        public void BossWithFiveEscorts_IsAccepted()
        {
            Assert.Empty(IssuesOf("boss", Wave(1), Wave(2), Wave(3), Wave(4), Wave(5, count: 5, boss: 1)));
        }

        [Fact]
        public void TableWithoutBossRow_HasNoBossIssue()
        {
            Assert.Empty(IssuesOf("boss", Wave(1), Wave(2), Wave(3), Wave(4), Wave(5)));
        }

        [Fact]
        public void WaveCountBelowMinimum_IsWarning()
        {
            List<WaveIssue> issues = IssuesOf("count", Wave(1), Wave(2), Wave(3), Wave(4));

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Contains("总波数 4", issues[0].Message);
        }

        [Fact]
        public void WaveCountAboveMaximum_IsWarning()
        {
            var rows = new WaveRow[21];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = Wave(i + 1);
            }

            List<WaveIssue> issues = IssuesOf("count", rows);
            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Contains("总波数 21", issues[0].Message);
        }

        [Fact]
        public void WaveCountInRange_HasNoCountIssue()
        {
            var rows = new WaveRow[5];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = Wave(i + 1);
            }

            Assert.Empty(IssuesOf("count", rows));

            var upper = new WaveRow[20];
            for (int i = 0; i < upper.Length; i++)
            {
                upper[i] = Wave(i + 1);
            }

            Assert.Empty(IssuesOf("count", upper));
        }

        [Fact]
        public void IssueToString_LocatesRuleAndWave()
        {
            var issue = new WaveIssue(3, "difficulty", "难度回落", isError: true);
            Assert.Equal("✗ [difficulty] 第 3 波：难度回落", issue.ToString());

            var tableIssue = new WaveIssue(0, "count", "总波数越界", isError: false);
            Assert.Equal("⚠ [count] 全表：总波数越界", tableIssue.ToString());
        }

        [Theory]
        [InlineData("enemyCount")]
        [InlineData("enemyHp")]
        [InlineData("enemySpeedPermille")]
        [InlineData("spawnIntervalMs")]
        public void ColumnOutOfRange_IsError(string column)
        {
            WaveRow row;
            switch (column)
            {
                case "enemyCount":
                    row = new WaveRow(1, 0, 10, 1000, 500, 10, 0);
                    break;
                case "enemyHp":
                    row = new WaveRow(1, 2, 0, 1000, 500, 10, 0);
                    break;
                case "enemySpeedPermille":
                    row = new WaveRow(1, 2, 10, -1, 500, 10, 0);
                    break;
                default:
                    row = new WaveRow(1, 2, 10, 1000, 49, 10, 0);
                    break;
            }

            List<WaveIssue> issues = IssuesOf("range", row);
            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains(column, issues[0].Message);
        }

        [Fact]
        public void NegativeRewardGold_IsError()
        {
            List<WaveIssue> issues = IssuesOf("range", new WaveRow(1, 2, 10, 1000, 500, -1, 0));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("rewardGold", issues[0].Message);
        }

        [Fact]
        public void BossFlagOutsideZeroOne_IsError()
        {
            List<WaveIssue> issues = IssuesOf("range", new WaveRow(1, 2, 10, 1000, 500, 10, 2));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("bossFlag", issues[0].Message);
        }

        [Fact]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => WaveRules.Validate(null!, new List<WaveIssue>()));
            Assert.Throws<ArgumentNullException>(() => WaveRules.Validate(new List<WaveRow>(), null!));
        }
    }

    /// <summary>CLI 退出码：仓库表 0、构造坏表 1、表坏 3、用法 2。</summary>
    public sealed class WaveCheckCliTests
    {
        [Fact]
        public void RepositoryTable_Passes()
        {
            string repoRoot = FindRepoRoot();
            var output = new StringWriter();
            var error = new StringWriter();

            int code = WaveCheckCli.Run(Array.Empty<string>(), output, error, repoRoot);

            Assert.Equal(WaveCheckCli.ExitSuccess, code);
            Assert.Contains("10 波", output.ToString());
            Assert.Contains("通过", output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }

        [Fact]
        public void CheckFlag_BehavesLikeDefault()
        {
            string repoRoot = FindRepoRoot();
            var output = new StringWriter();
            var error = new StringWriter();

            int code = WaveCheckCli.Run(new[] { "--check", "--tables", Path.Combine(repoRoot, "tables") }, output, error, repoRoot);

            Assert.Equal(WaveCheckCli.ExitSuccess, code);
            Assert.Contains("通过", output.ToString());
        }

        [Fact]
        public void TableWithDifficultyDrop_ReturnsErrorCode()
        {
            using var workspace = new TempTables();
            workspace.WriteTable(WaveJson.Build(
                new WaveRow(1, 4, 100, 1000, 1000, 100, 0),
                new WaveRow(2, 2, 100, 1000, 1000, 100, 0)));

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            Assert.Equal(WaveCheckCli.ExitErrors, result.Code);
            Assert.Contains("✗", result.Out);
            Assert.Contains("难度", result.Out);
        }

        [Fact]
        public void WarningsOnly_StillReturnsSuccess()
        {
            using var workspace = new TempTables();

            // 第 2 波"数量减半但血量翻倍"：难度不变（400），清场时间却从 4000ms 掉到 2000ms——
            // 这正是"节奏突然变快"的典型配方，只该出提示，不该把门禁判红。
            workspace.WriteTable(WaveJson.Build(
                new WaveRow(1, 4, 100, 1000, 1000, 100, 0),
                new WaveRow(2, 2, 200, 1000, 1000, 100, 0),
                new WaveRow(3, 4, 200, 1000, 1000, 200, 0),
                new WaveRow(4, 4, 300, 1000, 1000, 300, 0),
                new WaveRow(5, 4, 300, 1000, 1000, 300, 1)));

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            // 有提示但无错误时退出码必须是 0（否则接入 verify-all 会无差别变红）。
            Assert.Equal(WaveCheckCli.ExitSuccess, result.Code);
            Assert.Contains("⚠", result.Out);
            Assert.Contains("节奏突然变快", result.Out);
        }

        [Fact]
        public void InvalidJson_ReturnsInvalidTableCode()
        {
            using var workspace = new TempTables();
            workspace.WriteTable("{ \"table\": \"TbArenaWave\", ");

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            Assert.Equal(WaveCheckCli.ExitInvalidTable, result.Code);
            Assert.Contains("解析失败", result.Err);
        }

        [Fact]
        public void MissingTableFile_ReturnsInvalidTableCode()
        {
            using var workspace = new TempTables();

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            Assert.Equal(WaveCheckCli.ExitInvalidTable, result.Code);
            Assert.Contains("TbArenaWave.json", result.Err);
        }

        [Fact]
        public void MissingTablesDirectory_ReturnsUsageCode()
        {
            var workspaceRoot = Path.Combine(Path.GetTempPath(), "aifw-wavecheck-missing-" + Guid.NewGuid().ToString("N"));
            var output = new StringWriter();
            var error = new StringWriter();

            int code = WaveCheckCli.Run(new[] { "--tables", Path.Combine(workspaceRoot, "nope") }, output, error, workspaceRoot);

            Assert.Equal(WaveCheckCli.ExitUsage, code);
            Assert.Contains("找不到表目录", error.ToString());
        }

        [Fact]
        public void UnknownOption_ReturnsUsageCode()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int code = WaveCheckCli.Run(new[] { "--nope" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(WaveCheckCli.ExitUsage, code);
            Assert.Contains("未知参数", error.ToString());
        }

        [Fact]
        public void TablesWithoutValue_ReturnsUsageCode()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int code = WaveCheckCli.Run(new[] { "--tables" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(WaveCheckCli.ExitUsage, code);
            Assert.Contains("需要一个目录参数", error.ToString());
        }

        [Fact]
        public void Help_ReturnsSuccess()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int code = WaveCheckCli.Run(new[] { "--help" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(WaveCheckCli.ExitSuccess, code);
            Assert.Contains("波次/关卡节奏体检", output.ToString());
        }

        [Fact]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => WaveCheckCli.Run(null!, new StringWriter(), new StringWriter()));
            Assert.Throws<ArgumentNullException>(() => WaveCheckCli.Run(Array.Empty<string>(), null!, new StringWriter()));
            Assert.Throws<ArgumentNullException>(() => WaveCheckCli.Run(Array.Empty<string>(), new StringWriter(), null!));
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

    /// <summary>表读取辅助：列缺失 / 类型不对必须显式失败，仓表必须逐行读完。</summary>
    public sealed class WaveTableReaderTests
    {
        [Fact]
        public void ReadWaves_RepositoryTableHasTenContinuousRows()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(repoRoot, "tables", WaveCheckCli.TableFileName);
            TableData table = TableSchemaReader.Parse(File.ReadAllText(path), WaveCheckCli.TableFileName);

            List<WaveRow> rows = WaveTableReader.ReadWaves(table);

            Assert.Equal(10, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(i + 1, rows[i].WaveIndex);
            }

            Assert.False(rows[0].IsBoss);
            Assert.True(rows[9].IsBoss);
            Assert.True(rows[9].EnemyCount <= WaveRules.MaxBossEscortCount);
        }

        [Fact]
        public void ReadWaves_RoundTripsEveryColumn()
        {
            TableData table = TableSchemaReader.Parse(
                WaveJson.Build(new WaveRow(7, 9, 1234, 2500, 777, 456, 1)),
                "单元测试");

            WaveRow row = WaveTableReader.ReadWaves(table)[0];

            Assert.Equal(7, row.WaveIndex);
            Assert.Equal(9, row.EnemyCount);
            Assert.Equal(1234, row.EnemyHp);
            Assert.Equal(2500, row.EnemySpeedPermille);
            Assert.Equal(777, row.SpawnIntervalMs);
            Assert.Equal(456, row.RewardGold);
            Assert.True(row.IsBoss);
        }

        [Fact]
        public void ReadWaves_MissingColumn_Throws()
        {
            const string Json = @"
{
  ""table"": ""TbArenaWave"",
  ""columns"": [
    { ""name"": ""waveIndex"", ""type"": ""int32"" },
    { ""name"": ""enemyCount"", ""type"": ""int32"" },
    { ""name"": ""enemyHp"", ""type"": ""int32"" },
    { ""name"": ""enemySpeedPermille"", ""type"": ""int32"" },
    { ""name"": ""spawnIntervalMs"", ""type"": ""int32"" },
    { ""name"": ""rewardGold"", ""type"": ""int32"" }
  ],
  ""rows"": [
    { ""waveIndex"": 1, ""enemyCount"": 2, ""enemyHp"": 10, ""enemySpeedPermille"": 1000, ""spawnIntervalMs"": 500, ""rewardGold"": 10 }
  ]
}";
            TableData table = TableSchemaReader.Parse(Json, "单元测试");

            TableGenException exception = Assert.Throws<TableGenException>(() => WaveTableReader.ReadWaves(table));
            Assert.Contains("bossFlag", exception.Message);
        }

        [Fact]
        public void ReadWaves_NonIntegerValue_ThrowsWithColumnName()
        {
            const string Json = @"
{
  ""table"": ""TbArenaWave"",
  ""columns"": [
    { ""name"": ""waveIndex"", ""type"": ""string"" },
    { ""name"": ""enemyCount"", ""type"": ""int32"" },
    { ""name"": ""enemyHp"", ""type"": ""int32"" },
    { ""name"": ""enemySpeedPermille"", ""type"": ""int32"" },
    { ""name"": ""spawnIntervalMs"", ""type"": ""int32"" },
    { ""name"": ""rewardGold"", ""type"": ""int32"" },
    { ""name"": ""bossFlag"", ""type"": ""int32"" }
  ],
  ""rows"": [
    { ""waveIndex"": ""1"", ""enemyCount"": 2, ""enemyHp"": 10, ""enemySpeedPermille"": 1000, ""spawnIntervalMs"": 500, ""rewardGold"": 10, ""bossFlag"": 0 }
  ]
}";
            TableData table = TableSchemaReader.Parse(Json, "单元测试");

            TableGenException exception = Assert.Throws<TableGenException>(() => WaveTableReader.ReadWaves(table));
            Assert.Contains("期望 int32", exception.Message);
            Assert.Contains("waveIndex", exception.Message);
        }

        [Fact]
        public void ReadWaves_NullTable_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => WaveTableReader.ReadWaves(null!));
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
