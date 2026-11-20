#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.SkillCheck;
using AIFramework.TableGen;
using Xunit;

namespace AIFramework.SkillCheck.Tests
{
    /// <summary>测试侧小工具：把"一组技能行"变成一份 <c>TbSkill.json</c> 文本。</summary>
    internal static class SkillJson
    {
        internal static readonly string[] AllColumns =
        {
            "skillId",
            "name",
            "cooldownMs",
            "castRangePermille",
            "damage",
            "effectType",
            "tagId",
            "maxTargets",
            "level",
        };

        internal static string Build(params SkillRow[] rows)
        {
            return BuildWithColumns(AllColumns, rows);
        }

        internal static string BuildWithColumns(IReadOnlyList<string> columns, params SkillRow[] rows)
        {
            var builder = new StringBuilder(768);
            builder.Append(@"{ ""table"": ""TbSkill"", ""columns"": [");
            for (int c = 0; c < columns.Count; c++)
            {
                if (c > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(@"{ ""name"": """).Append(columns[c]).Append(@""", ""type"": """)
                    .Append(IsStringColumn(columns[c]) ? "string" : "int32")
                    .Append(@""" }");
            }

            builder.Append("], \"rows\": [");
            for (int r = 0; r < rows.Length; r++)
            {
                if (r > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("{ ");
                for (int c = 0; c < columns.Count; c++)
                {
                    if (c > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(@"""").Append(columns[c]).Append(@""": ");
                    if (IsStringColumn(columns[c]))
                    {
                        builder.Append(@"""").Append(rows[r].Name).Append(@"""");
                    }
                    else
                    {
                        builder.Append(IntValue(rows[r], columns[c]));
                    }
                }

                builder.Append(" }");
            }

            builder.Append("] }");
            return builder.ToString();
        }

        private static bool IsStringColumn(string column)
        {
            return string.Equals(column, "name", StringComparison.Ordinal);
        }

        private static int IntValue(SkillRow row, string column)
        {
            switch (column)
            {
                case "skillId":
                    return row.SkillId;
                case "cooldownMs":
                    return row.CooldownMs;
                case "castRangePermille":
                    return row.CastRangePermille;
                case "damage":
                    return row.Damage;
                case "effectType":
                    return row.EffectType;
                case "tagId":
                    return row.TagId;
                case "maxTargets":
                    return row.MaxTargets;
                case "level":
                    return row.Level;
                default:
                    throw new InvalidOperationException("测试构造器不认识的列：" + column);
            }
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
            Root = Path.Combine(Path.GetTempPath(), "aifw-skillcheck-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TablesDirectory);
        }

        internal void WriteTable(string json)
        {
            File.WriteAllText(Path.Combine(TablesDirectory, SkillCheckCli.TableFileName), json, new UTF8Encoding(false));
        }

        internal CliResult Run(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = SkillCheckCli.Run(args, output, error, Root);
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
    /// 规则分支：复合主键/等级缺档、单行区间、跨等级成长、效果组合、跨技能 Tag、极端值提示。
    /// <para>断言按规则 id 过滤，避免"等级缺档"这类伴生提示把用例写得又脆又长。</para>
    /// </summary>
    public sealed class SkillRuleTests
    {
        /// <summary>造一个合法的 3 级技能（默认：伤害类、冷却 1200、距离 1500、单目标、Tag 101）。</summary>
        private static SkillRow[] ThreeLevels(
            int skillId,
            int damage1,
            int damage2,
            int damage3,
            int cooldown = 1200,
            int effect = 0,
            int tag = 101,
            int castRange = 1500,
            int maxTargets = 1)
        {
            return new[]
            {
                new SkillRow(skillId, "技能", cooldown, castRange, damage1, effect, tag, maxTargets, 1),
                new SkillRow(skillId, "技能", cooldown, castRange, damage2, effect, tag, maxTargets, 2),
                new SkillRow(skillId, "技能", cooldown, castRange, damage3, effect, tag, maxTargets, 3),
            };
        }

        private static SkillRow[] Concat(params SkillRow[][] groups)
        {
            var rows = new List<SkillRow>();
            for (int i = 0; i < groups.Length; i++)
            {
                rows.AddRange(groups[i]);
            }

            return rows.ToArray();
        }

        private static List<SkillIssue> AllIssues(params SkillRow[] rows)
        {
            var issues = new List<SkillIssue>();
            SkillRules.Validate(rows, issues);
            return issues;
        }

        private static List<SkillIssue> IssuesOf(string rule, params SkillRow[] rows)
        {
            List<SkillIssue> all = AllIssues(rows);
            var filtered = new List<SkillIssue>();
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
        public void ValidThreeLevelSkill_ProducesNoIssues()
        {
            Assert.Empty(AllIssues(ThreeLevels(1001, 40, 60, 85)));
        }

        [Fact]
        public void DuplicateCompositeKey_IsError()
        {
            SkillRow[] rows =
            {
                new SkillRow(1001, "技能", 1200, 1500, 40, 0, 101, 1, 1),
                new SkillRow(1001, "技能", 1200, 1500, 60, 0, 101, 1, 2),
                new SkillRow(1001, "技能", 1200, 1500, 70, 0, 101, 1, 2),
            };

            List<SkillIssue> issues = IssuesOf("structure", rows);

            Assert.Contains(issues, issue => issue.IsError && issue.Message.Contains("复合主键"));
            Assert.Contains(issues, issue => issue.IsError && issue.Message.Contains("等级缺档"));
        }

        [Fact]
        public void MissingThirdLevel_IsError()
        {
            SkillRow[] rows =
            {
                new SkillRow(1001, "技能", 1200, 1500, 40, 0, 101, 1, 1),
                new SkillRow(1001, "技能", 1200, 1500, 60, 0, 101, 1, 2),
            };

            List<SkillIssue> issues = IssuesOf("structure", rows);

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("等级缺档", issues[0].Message);
            Assert.Contains("缺 3", issues[0].Message);
        }

        [Fact]
        public void LevelOutOfRange_IsError()
        {
            SkillRow[] rows =
            {
                new SkillRow(1001, "技能", 1200, 1500, 40, 0, 101, 1, 1),
                new SkillRow(1001, "技能", 1200, 1500, 60, 0, 101, 1, 2),
                new SkillRow(1001, "技能", 1200, 1500, 85, 0, 101, 1, 4),
            };

            List<SkillIssue> issues = IssuesOf("structure", rows);

            Assert.Contains(issues, issue => issue.IsError && issue.Message.Contains("level = 4 越界"));
        }

        [Fact]
        public void EmptyTable_RuleLayerReportsCountError()
        {
            List<SkillIssue> issues = AllIssues();

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Equal("count", issues[0].Rule);
            Assert.Contains("一行技能都没有", issues[0].Message);
        }

        [Fact]
        public void CooldownBelowMinimum_IsError()
        {
            List<SkillIssue> issues = IssuesOf("range", ThreeLevels(1001, 40, 60, 85, cooldown: 99));

            Assert.Equal(3, issues.Count);
            Assert.Contains(issues, issue => issue.IsError && issue.Message.Contains("cooldownMs = 99"));
        }

        [Fact]
        public void CooldownAtMinimum_IsAccepted()
        {
            Assert.Empty(IssuesOf("range", ThreeLevels(1001, 40, 60, 85, cooldown: 100)));
        }

        [Fact]
        public void NegativeDamage_IsError()
        {
            List<SkillIssue> issues = IssuesOf("range", ThreeLevels(1001, -1, 60, 85));

            Assert.Contains(issues, issue => issue.IsError && issue.Message.Contains("damage = -1"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(21)]
        public void MaxTargetsOutOfRange_IsError(int maxTargets)
        {
            List<SkillIssue> issues = IssuesOf("range", ThreeLevels(1001, 40, 60, 85, maxTargets: maxTargets));

            Assert.Equal(3, issues.Count);
            Assert.All(issues, issue => Assert.True(issue.IsError));
            Assert.Contains(issues, issue => issue.Message.Contains("maxTargets"));
        }

        [Fact]
        public void MaxTargetsAtUpperBound_IsAccepted()
        {
            Assert.Empty(IssuesOf("range", ThreeLevels(1001, 40, 60, 85, maxTargets: 20)));
        }

        [Theory]
        [InlineData(99)]
        [InlineData(5001)]
        public void CastRangeOutOfRange_IsError(int castRange)
        {
            List<SkillIssue> issues = IssuesOf("range", ThreeLevels(1001, 40, 60, 85, castRange: castRange));

            Assert.Equal(3, issues.Count);
            Assert.Contains(issues, issue => issue.Message.Contains("castRangePermille"));
        }

        [Fact]
        public void CastRangeAtBounds_IsAccepted()
        {
            SkillRow[] rows = Concat(
                ThreeLevels(1001, 40, 60, 85, castRange: 100, tag: 101),
                ThreeLevels(1002, 30, 45, 65, castRange: 5000, tag: 201));

            Assert.Empty(IssuesOf("range", rows));
        }

        [Fact]
        public void EffectTypeOutOfRange_IsError()
        {
            List<SkillIssue> issues = IssuesOf("range", ThreeLevels(1001, 0, 0, 0, effect: 5));

            Assert.Equal(3, issues.Count);
            Assert.Contains(issues, issue => issue.IsError && issue.Message.Contains("effectType = 5"));
        }

        [Fact]
        public void EqualDamageAcrossLevels_IsError()
        {
            List<SkillIssue> issues = IssuesOf("growth", ThreeLevels(1001, 40, 40, 85));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Equal(2, issues[0].Level);
            Assert.Contains("未严格高于", issues[0].Message);
        }

        [Fact]
        public void DecreasingDamage_IsError()
        {
            List<SkillIssue> issues = IssuesOf("growth", ThreeLevels(1001, 40, 30, 50));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("damage = 30", issues[0].Message);
        }

        [Fact]
        public void HealMustStrictlyGrow_IsError()
        {
            List<SkillIssue> issues = IssuesOf("growth", ThreeLevels(1002, 30, 30, 50, effect: 1, tag: 301));

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("治疗", issues[0].Message);
        }

        [Fact]
        public void CooldownDecreaseAcrossLevels_IsError()
        {
            SkillRow[] rows =
            {
                new SkillRow(1001, "技能", 1200, 1500, 40, 0, 101, 1, 1),
                new SkillRow(1001, "技能", 1200, 1500, 60, 0, 101, 1, 2),
                new SkillRow(1001, "技能", 1000, 1500, 85, 0, 101, 1, 3),
            };

            List<SkillIssue> issues = IssuesOf("growth", rows);

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Equal(3, issues[0].Level);
            Assert.Contains("冷却不得缩短", issues[0].Message);
        }

        [Fact]
        public void DisplacementWithDamage_IsError()
        {
            List<SkillIssue> issues = IssuesOf("effect", ThreeLevels(1003, 40, 60, 85, effect: 3, tag: 102));

            Assert.Equal(3, issues.Count);
            Assert.All(issues, issue => Assert.True(issue.IsError));
            Assert.Contains(issues, issue => issue.Message.Contains("位移"));
        }

        [Fact]
        public void DisplacementWithZeroDamage_IsAccepted()
        {
            Assert.Empty(AllIssues(ThreeLevels(1003, 0, 0, 0, effect: 3, tag: 102)));
        }

        [Fact]
        public void ControlCooldownTooShort_IsError()
        {
            List<SkillIssue> issues = IssuesOf("effect", ThreeLevels(1004, 0, 0, 0, cooldown: 1500, effect: 4, tag: 201));

            Assert.Equal(3, issues.Count);
            Assert.All(issues, issue => Assert.True(issue.IsError));
            Assert.Contains(issues, issue => issue.Message.Contains("控制"));
        }

        [Fact]
        public void ControlCooldownAtMinimum_IsAccepted()
        {
            Assert.Empty(AllIssues(ThreeLevels(1004, 0, 0, 0, cooldown: 2000, effect: 4, tag: 201)));
        }

        [Fact]
        public void UnknownTagId_IsError()
        {
            List<SkillIssue> issues = IssuesOf("tag", ThreeLevels(1001, 40, 60, 85, tag: 9999));

            Assert.Equal(3, issues.Count);
            Assert.All(issues, issue => Assert.True(issue.IsError));
            Assert.Contains(issues, issue => issue.Message.Contains("不是已定义的 GameplayTag"));
        }

        [Fact]
        public void ZeroTagId_IsAccepted()
        {
            Assert.Empty(IssuesOf("tag", ThreeLevels(1001, 40, 60, 85, tag: 0)));
        }

        [Fact]
        public void TagSharedByThreeSkills_IsError()
        {
            SkillRow[] rows = Concat(
                ThreeLevels(1001, 40, 60, 85, tag: 102),
                ThreeLevels(1002, 30, 45, 65, tag: 102),
                ThreeLevels(1003, 20, 30, 40, tag: 102));

            List<SkillIssue> issues = IssuesOf("tag", rows);

            Assert.Single(issues);
            Assert.True(issues[0].IsError);
            Assert.Contains("被 3 个技能共用", issues[0].Message);
            Assert.Equal(0, issues[0].SkillId);
        }

        [Fact]
        public void TagSharedByTwoSkills_IsAccepted()
        {
            SkillRow[] rows = Concat(
                ThreeLevels(1001, 40, 60, 85, tag: 102),
                ThreeLevels(1002, 30, 45, 65, tag: 102));

            Assert.Empty(IssuesOf("tag", rows));
        }

        [Fact]
        public void CooldownOutlier_IsWarningOnly()
        {
            SkillRow[] rows = Concat(
                ThreeLevels(1001, 40, 60, 85, cooldown: 1000, tag: 101),
                ThreeLevels(1002, 30, 45, 65, cooldown: 1000, tag: 102),
                ThreeLevels(1003, 20, 30, 40, cooldown: 1000, tag: 201),
                ThreeLevels(1004, 10, 15, 20, cooldown: 20000, tag: 301));

            List<SkillIssue> issues = AllIssues(rows);

            Assert.Single(issues);
            Assert.False(issues[0].IsError);
            Assert.Equal("outlier", issues[0].Rule);
            Assert.Contains("偏差", issues[0].Message);
        }

        [Fact]
        public void CooldownOutlier_WithinTolerance_IsAccepted()
        {
            SkillRow[] rows = Concat(
                ThreeLevels(1001, 40, 60, 85, cooldown: 1000, tag: 101),
                ThreeLevels(1002, 30, 45, 65, cooldown: 2000, tag: 102),
                ThreeLevels(1003, 20, 30, 40, cooldown: 3000, tag: 201),
                ThreeLevels(1004, 10, 15, 20, cooldown: 4000, tag: 301));

            Assert.Empty(IssuesOf("outlier", rows));
        }

        [Fact]
        public void IssueToString_LocatesSkillAndLevel()
        {
            var rowIssue = new SkillIssue(1001, 2, "growth", "伤害没涨", isError: true);
            Assert.Equal("✗ [growth] 技能 1001 L2：伤害没涨", rowIssue.ToString());

            var tableIssue = new SkillIssue(0, 0, "outlier", "冷却分布极端", isError: false);
            Assert.Equal("⚠ [outlier] 全表：冷却分布极端", tableIssue.ToString());

            var skillIssue = new SkillIssue(1001, 0, "structure", "缺档", isError: true);
            Assert.Equal("✗ [structure] 技能 1001：缺档", skillIssue.ToString());
        }

        [Fact]
        public void MeanMedianCooldownMs_ComputesEvenAndOdd()
        {
            SkillRow[] even = { SkillRowAt(1000), SkillRowAt(3000) };
            Assert.Equal(2000d, SkillRules.MeanCooldownMs(even));
            Assert.Equal(2000d, SkillRules.MedianCooldownMs(even));

            SkillRow[] odd = { SkillRowAt(1000), SkillRowAt(9000), SkillRowAt(2000) };
            Assert.Equal(4000d, SkillRules.MeanCooldownMs(odd));
            Assert.Equal(2000d, SkillRules.MedianCooldownMs(odd));
        }

        [Fact]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => SkillRules.Validate(null!, new List<SkillIssue>()));
            Assert.Throws<ArgumentNullException>(() => SkillRules.Validate(Array.Empty<SkillRow>(), null!));
            Assert.Throws<ArgumentNullException>(() => SkillRules.MeanCooldownMs(null!));
            Assert.Throws<ArgumentNullException>(() => SkillRules.MedianCooldownMs(null!));
            Assert.Throws<ArgumentNullException>(() => SkillTableReader.ReadSkills(null!));
        }

        private static SkillRow SkillRowAt(int cooldownMs)
        {
            return new SkillRow(1001, "技能", cooldownMs, 1500, 10, 0, 101, 1, 1);
        }
    }

    /// <summary>表读取：空表/缺列/类型不对必须显式失败，仓表必须逐行读完。</summary>
    public sealed class SkillTableReaderTests
    {
        [Fact]
        public void ReadSkills_RepositoryTableHasFourSkillsAndTwelveRows()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(repoRoot, "tables", SkillCheckCli.TableFileName);
            TableData table = TableSchemaReader.Parse(File.ReadAllText(path), SkillCheckCli.TableFileName);

            List<SkillRow> rows = SkillTableReader.ReadSkills(table);

            Assert.Equal(12, rows.Count);
            var skills = new HashSet<int>();
            for (int i = 0; i < rows.Count; i++)
            {
                skills.Add(rows[i].SkillId);
            }

            Assert.Equal(4, skills.Count);

            // 每个 skillId 必须恰好覆盖 level 1..3。
            foreach (int skillId in skills)
            {
                var levels = new List<int>();
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].SkillId == skillId)
                    {
                        levels.Add(rows[i].Level);
                    }
                }

                levels.Sort();
                Assert.Equal(new[] { 1, 2, 3 }, levels);
            }

            // 仓库表里至少要有一种位移/控制技能被真实数据覆盖（规则 4 才有落地样本）。
            Assert.Contains(rows, row => row.EffectType == SkillRules.EffectDisplace);
            Assert.Contains(rows, row => row.EffectType == SkillRules.EffectControl);
        }

        [Fact]
        public void ReadSkills_RoundTripsEveryColumn()
        {
            TableData table = TableSchemaReader.Parse(
                SkillJson.Build(new SkillRow(7, "冰封", 1234, 2500, 77, 4, 201, 5, 3)),
                "单元测试");

            SkillRow row = SkillTableReader.ReadSkills(table)[0];

            Assert.Equal(7, row.SkillId);
            Assert.Equal("冰封", row.Name);
            Assert.Equal(1234, row.CooldownMs);
            Assert.Equal(2500, row.CastRangePermille);
            Assert.Equal(77, row.Damage);
            Assert.Equal(4, row.EffectType);
            Assert.Equal(201, row.TagId);
            Assert.Equal(5, row.MaxTargets);
            Assert.Equal(3, row.Level);
        }

        [Fact]
        public void ReadSkills_EmptyTable_Throws()
        {
            TableData table = TableSchemaReader.Parse(SkillJson.Build(), "单元测试");

            TableGenException exception = Assert.Throws<TableGenException>(() => SkillTableReader.ReadSkills(table));
            Assert.Contains("一行数据都没有", exception.Message);
        }

        [Fact]
        public void ReadSkills_MissingColumn_Throws()
        {
            string[] columns =
            {
                "skillId",
                "name",
                "cooldownMs",
                "castRangePermille",
                "damage",
                "effectType",
                "maxTargets",
                "level",
            };

            TableData table = TableSchemaReader.Parse(
                SkillJson.BuildWithColumns(columns, new SkillRow(1001, "技能", 1200, 1500, 40, 0, 101, 1, 1)),
                "单元测试");

            TableGenException exception = Assert.Throws<TableGenException>(() => SkillTableReader.ReadSkills(table));
            Assert.Contains("tagId", exception.Message);
        }

        [Fact]
        public void ReadSkills_NonIntegerValue_Throws()
        {
            const string Json = @"
{
  ""table"": ""TbSkill"",
  ""columns"": [
    { ""name"": ""skillId"", ""type"": ""int32"" },
    { ""name"": ""name"", ""type"": ""string"" },
    { ""name"": ""cooldownMs"", ""type"": ""int32"" },
    { ""name"": ""castRangePermille"", ""type"": ""int32"" },
    { ""name"": ""damage"", ""type"": ""int32"" },
    { ""name"": ""effectType"", ""type"": ""int32"" },
    { ""name"": ""tagId"", ""type"": ""int32"" },
    { ""name"": ""maxTargets"", ""type"": ""int32"" },
    { ""name"": ""level"", ""type"": ""int32"" }
  ],
  ""rows"": [
    { ""skillId"": 1001, ""name"": ""技能"", ""cooldownMs"": 1200, ""castRangePermille"": 1500, ""damage"": 40, ""effectType"": 0, ""tagId"": 101, ""maxTargets"": 1, ""level"": ""3"" }
  ]
}";
            TableData table = TableSchemaReader.Parse(Json, "单元测试");

            TableGenException exception = Assert.Throws<TableGenException>(() => SkillTableReader.ReadSkills(table));
            Assert.Contains("期望 int32", exception.Message);
            Assert.Contains("level", exception.Message);
        }

        [Fact]
        public void ReadSkills_StringColumnWrongType_Throws()
        {
            const string Json = @"
{
  ""table"": ""TbSkill"",
  ""columns"": [
    { ""name"": ""skillId"", ""type"": ""int32"" },
    { ""name"": ""name"", ""type"": ""string"" },
    { ""name"": ""cooldownMs"", ""type"": ""int32"" },
    { ""name"": ""castRangePermille"", ""type"": ""int32"" },
    { ""name"": ""damage"", ""type"": ""int32"" },
    { ""name"": ""effectType"", ""type"": ""int32"" },
    { ""name"": ""tagId"", ""type"": ""int32"" },
    { ""name"": ""maxTargets"", ""type"": ""int32"" },
    { ""name"": ""level"", ""type"": ""int32"" }
  ],
  ""rows"": [
    { ""skillId"": 1001, ""name"": 123, ""cooldownMs"": 1200, ""castRangePermille"": 1500, ""damage"": 40, ""effectType"": 0, ""tagId"": 101, ""maxTargets"": 1, ""level"": 1 }
  ]
}";
            TableData table = TableSchemaReader.Parse(Json, "单元测试");

            TableGenException exception = Assert.Throws<TableGenException>(() => SkillTableReader.ReadSkills(table));
            Assert.Contains("期望 string", exception.Message);
            Assert.Contains("name", exception.Message);
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

    /// <summary>CLI 退出码：仓库表 0、坏表 1、用法 2、表坏 3。</summary>
    public sealed class SkillCheckCliTests
    {
        [Fact]
        public void RepositoryTable_Passes()
        {
            string repoRoot = FindRepoRoot();
            var output = new StringWriter();
            var error = new StringWriter();

            int code = SkillCheckCli.Run(Array.Empty<string>(), output, error, repoRoot);

            Assert.Equal(SkillCheckCli.ExitSuccess, code);
            Assert.Contains("4 个技能 / 12 行", output.ToString());
            Assert.Contains("通过", output.ToString());
            Assert.Equal(string.Empty, error.ToString());
        }

        [Fact]
        public void CheckFlag_BehavesLikeDefault()
        {
            string repoRoot = FindRepoRoot();
            var output = new StringWriter();
            var error = new StringWriter();

            int code = SkillCheckCli.Run(
                new[] { "--check", "--tables", Path.Combine(repoRoot, "tables") },
                output,
                error,
                repoRoot);

            Assert.Equal(SkillCheckCli.ExitSuccess, code);
            Assert.Contains("通过", output.ToString());
        }

        [Fact]
        public void BadTable_ReturnsErrorCode()
        {
            using var workspace = new TempTables();
            workspace.WriteTable(SkillJson.Build(
                new SkillRow(1001, "技能", 1200, 1500, 40, 0, 101, 1, 1),
                new SkillRow(1001, "技能", 1200, 1500, 40, 0, 101, 1, 2),
                new SkillRow(1001, "技能", 1200, 1500, 85, 0, 101, 1, 3)));

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            Assert.Equal(SkillCheckCli.ExitErrors, result.Code);
            Assert.Contains("✗", result.Out);
            Assert.Contains("growth", result.Out);
        }

        [Fact]
        public void WarningsOnly_ReturnsSuccess()
        {
            using var workspace = new TempTables();
            workspace.WriteTable(SkillJson.Build(
                new SkillRow(1001, "技能", 1000, 1500, 40, 0, 101, 1, 1),
                new SkillRow(1001, "技能", 1000, 1500, 60, 0, 101, 1, 2),
                new SkillRow(1001, "技能", 1000, 1500, 85, 0, 101, 1, 3),
                new SkillRow(1002, "技能", 1000, 1500, 30, 0, 102, 1, 1),
                new SkillRow(1002, "技能", 1000, 1500, 45, 0, 102, 1, 2),
                new SkillRow(1002, "技能", 1000, 1500, 65, 0, 102, 1, 3),
                new SkillRow(1003, "技能", 1000, 1500, 20, 0, 201, 1, 1),
                new SkillRow(1003, "技能", 1000, 1500, 30, 0, 201, 1, 2),
                new SkillRow(1003, "技能", 1000, 1500, 40, 0, 201, 1, 3),
                new SkillRow(1004, "技能", 20000, 1500, 10, 0, 301, 1, 1),
                new SkillRow(1004, "技能", 20000, 1500, 15, 0, 301, 1, 2),
                new SkillRow(1004, "技能", 20000, 1500, 20, 0, 301, 1, 3)));

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            // 有提示但无错误时退出码必须是 0（否则接入 verify-all 会无差别变红）。
            Assert.Equal(SkillCheckCli.ExitSuccess, result.Code);
            Assert.Contains("⚠", result.Out);
            Assert.Contains("偏差", result.Out);
        }

        [Fact]
        public void InvalidJson_ReturnsParseCode()
        {
            using var workspace = new TempTables();
            workspace.WriteTable("{ \"table\": \"TbSkill\", ");

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            Assert.Equal(SkillCheckCli.ExitInvalidTable, result.Code);
            Assert.Contains("解析失败", result.Err);
        }

        [Fact]
        public void EmptyTable_ReturnsParseCode()
        {
            using var workspace = new TempTables();
            workspace.WriteTable(SkillJson.Build());

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            Assert.Equal(SkillCheckCli.ExitInvalidTable, result.Code);
            Assert.Contains("解析失败", result.Err);
            Assert.Contains("一行数据都没有", result.Err);
        }

        [Fact]
        public void MissingTableFile_ReturnsParseCode()
        {
            using var workspace = new TempTables();

            CliResult result = workspace.Run("--tables", workspace.TablesDirectory);

            Assert.Equal(SkillCheckCli.ExitInvalidTable, result.Code);
            Assert.Contains("TbSkill.json", result.Err);
        }

        [Fact]
        public void MissingTablesDirectory_ReturnsUsageCode()
        {
            string workspaceRoot = Path.Combine(Path.GetTempPath(), "aifw-skillcheck-missing-" + Guid.NewGuid().ToString("N"));
            var output = new StringWriter();
            var error = new StringWriter();

            int code = SkillCheckCli.Run(new[] { "--tables", Path.Combine(workspaceRoot, "nope") }, output, error, workspaceRoot);

            Assert.Equal(SkillCheckCli.ExitUsage, code);
            Assert.Contains("找不到表目录", error.ToString());
        }

        [Fact]
        public void UnknownOption_ReturnsUsageCode()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int code = SkillCheckCli.Run(new[] { "--nope" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(SkillCheckCli.ExitUsage, code);
            Assert.Contains("未知参数", error.ToString());
        }

        [Fact]
        public void TablesWithoutValue_ReturnsUsageCode()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int code = SkillCheckCli.Run(new[] { "--tables" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(SkillCheckCli.ExitUsage, code);
            Assert.Contains("需要一个目录参数", error.ToString());
        }

        [Fact]
        public void Help_ReturnsSuccess()
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int code = SkillCheckCli.Run(new[] { "--help" }, output, error, AppContext.BaseDirectory);

            Assert.Equal(SkillCheckCli.ExitSuccess, code);
            Assert.Contains("技能配置体检", output.ToString());
        }

        [Fact]
        public void NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => SkillCheckCli.Run(null!, new StringWriter(), new StringWriter()));
            Assert.Throws<ArgumentNullException>(() => SkillCheckCli.Run(Array.Empty<string>(), null!, new StringWriter()));
            Assert.Throws<ArgumentNullException>(() => SkillCheckCli.Run(Array.Empty<string>(), new StringWriter(), null!));
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
