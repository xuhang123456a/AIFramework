using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.AnalyticsCheck;
using Xunit;

namespace AIFramework.AnalyticsCheck.Tests
{
    /// <summary>测试共用的契约样本与临时仓库工具。</summary>
    internal static class AnalyticsTestSupport
    {
        public const string ContractLocation = "src/AIFramework.Contracts/Services/AnalyticsContracts.cs";

        public const string DefaultConstants =
            "        public const int None = 0;\n"
            + "        public const int AppLaunch = 1001;\n"
            + "        public const int BattleEnd = 2002;\n";

        /// <summary>造一份最小可解析的契约源码（构造函数固定 5 个参数）。</summary>
        public static string ContractSource(string constants)
        {
            return "using System;\n"
                + "\n"
                + "namespace AIFramework.Contracts.Services\n"
                + "{\n"
                + "    public readonly struct AnalyticsEvent\n"
                + "    {\n"
                + "        public AnalyticsEvent(int eventId, float value0, float value1, float value2, string? label)\n"
                + "        {\n"
                + "            EventId = eventId;\n"
                + "            Label = label;\n"
                + "        }\n"
                + "\n"
                + "        public int EventId { get; }\n"
                + "\n"
                + "        public string? Label { get; }\n"
                + "    }\n"
                + "\n"
                + "    public static class AnalyticsEventIds\n"
                + "    {\n"
                + constants
                + "    }\n"
                + "}\n";
        }

        public static AnalyticsContract DefaultContract()
        {
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                ContractSource(DefaultConstants),
                ContractLocation);
            Assert.True(parsed.Success, parsed.FailureReason);
            Assert.Empty(parsed.Issues);

            AnalyticsContract? contract = parsed.Contract;
            Assert.NotNull(contract);
            return contract!;
        }

        public static Dictionary<string, string> Files(params (string Path, string Content)[] files)
        {
            var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < files.Length; i++)
            {
                dictionary.Add(files[i].Path, files[i].Content);
            }

            return dictionary;
        }

        public static string NewTempDirectory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "aifw-analyticscheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        /// <summary>按真实仓库结构写一份契约 + 若干 src 文件。</summary>
        public static void WriteRepository(
            string root,
            string contractSource,
            params (string Path, string Content)[] files)
        {
            string contractPath = Path.Combine(root, ContractLocation.Replace('/', Path.DirectorySeparatorChar));
            string? directory = Path.GetDirectoryName(contractPath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(contractPath, contractSource);

            for (int i = 0; i < files.Length; i++)
            {
                string path = Path.Combine(root, files[i].Path.Replace('/', Path.DirectorySeparatorChar));
                string? parent = Path.GetDirectoryName(path);
                if (parent != null)
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllText(path, files[i].Content);
            }
        }

        public static (int ExitCode, string Output, string Error) RunCli(string workingDirectory, params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int exitCode = AnalyticsCheckCli.Run(args, output, error, workingDirectory);
            return (exitCode, output.ToString(), error.ToString());
        }
    }

    /// <summary>
    /// 规则库验收。核心口径：
    /// <list type="bullet">
    /// <item>契约内撞号/重复名/非正号 = 错误；保留哨兵 None=0 豁免（否则真实仓库永远红）。</item>
    /// <item>悬空引用与构造参数数量不符 = 错误；直接写数字、引用 0 次 = 提示。</item>
    /// <item>引用次数按原始文本统计，注释里的引用也算（已知误报口径，测试锁定）。</item>
    /// </list>
    /// </summary>
    public sealed class AnalyticsRulesTests
    {
        [Fact]
        public void ParseContract_ExtractsEventsAndConstructorArity()
        {
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource(AnalyticsTestSupport.DefaultConstants),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            Assert.Null(parsed.FailureReason);
            Assert.Empty(parsed.Issues);

            AnalyticsContract? contract = parsed.Contract;
            Assert.NotNull(contract);
            Assert.Equal(3, contract!.Events.Count);
            Assert.Equal(2, contract.EventCount);
            Assert.Equal(5, contract.ConstructorParameterCount);
            Assert.True(contract.TryGetValue("AppLaunch", out int appLaunch));
            Assert.Equal(1001, appLaunch);
            Assert.True(contract.TryGetValue("BattleEnd", out int battleEnd));
            Assert.Equal(2002, battleEnd);
            Assert.False(contract.TryGetValue("Missing", out _));
            Assert.True(contract.Contains("None"));
        }

        [Fact]
        public void ParseContract_SupportsHexAndDigitSeparators()
        {
            const string Constants =
                "        public const int Hex = 0x3E9;\n"
                + "        public const int Separated = 2_002;\n";

            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource(Constants),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            Assert.Empty(parsed.Issues);

            AnalyticsContract? contract = parsed.Contract;
            Assert.NotNull(contract);
            Assert.True(contract!.TryGetValue("Hex", out int hex));
            Assert.Equal(1001, hex);
            Assert.True(contract.TryGetValue("Separated", out int separated));
            Assert.Equal(2002, separated);
        }

        [Fact]
        public void ParseContract_EmptyEventClass_IsValid()
        {
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource(string.Empty),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            Assert.Empty(parsed.Issues);

            AnalyticsContract? contract = parsed.Contract;
            Assert.NotNull(contract);
            Assert.Empty(contract!.Events);
            Assert.Equal(0, contract.EventCount);
        }

        [Fact]
        public void ParseContract_MissingEventIdsClass_Fails()
        {
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                "namespace X { public static class Something { public const int A = 1; } }",
                AnalyticsTestSupport.ContractLocation);

            Assert.False(parsed.Success);
            Assert.Null(parsed.Contract);
            Assert.Contains("AnalyticsEventIds", parsed.FailureReason);
        }

        [Fact]
        public void ParseContract_MissingConstructor_Fails()
        {
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                "public static class AnalyticsEventIds { public const int AppLaunch = 1001; }",
                AnalyticsTestSupport.ContractLocation);

            Assert.False(parsed.Success);
            Assert.Contains("构造函数", parsed.FailureReason);
        }

        [Fact]
        public void ParseContract_IgnoresCommentedOutConstants()
        {
            const string Constants =
                "        // public const int Removed = 9001;\n"
                + "        /* public const int AlsoRemoved = 9002; */\n"
                + "        public const int AppLaunch = 1001;\n";

            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource(Constants),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            AnalyticsContract? contract = parsed.Contract;
            Assert.NotNull(contract);
            Assert.Single(contract!.Events);
            Assert.Equal("AppLaunch", contract.Events[0].Name);
        }

        [Fact]
        public void ParseContract_IgnoresConstantsOutsideEventIdsClass()
        {
            string source = AnalyticsTestSupport.ContractSource(AnalyticsTestSupport.DefaultConstants)
                .Replace(
                    "    public static class AnalyticsEventIds",
                    "    public static class OtherIds\n    {\n        public const int Other = 42;\n    }\n\n"
                        + "    public static class AnalyticsEventIds");

            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                source,
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            AnalyticsContract? contract = parsed.Contract;
            Assert.NotNull(contract);
            Assert.Equal(3, contract!.Events.Count);
            Assert.False(contract.Contains("Other"));
        }

        [Fact]
        public void ValidateContract_DuplicateValue_IsError()
        {
            const string Constants =
                "        public const int AppLaunch = 1001;\n"
                + "        public const int SceneEnter = 1001;\n";

            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource(Constants),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            AnalyticsIssue issue = Assert.Single(parsed.Issues);
            Assert.True(issue.IsError);
            Assert.Equal("SceneEnter", issue.EventName);
            Assert.Contains("1001", issue.Message);
            Assert.Contains("AppLaunch", issue.Message);
        }

        [Fact]
        public void ValidateContract_DuplicateName_IsError()
        {
            const string Constants =
                "        public const int AppLaunch = 1001;\n"
                + "        public const int AppLaunch = 1002;\n";

            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource(Constants),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            AnalyticsIssue issue = Assert.Single(parsed.Issues);
            Assert.True(issue.IsError);
            Assert.Contains("重复定义", issue.Message);
        }

        [Fact]
        public void ValidateContract_NonPositiveValue_IsError()
        {
            const string Constants =
                "        public const int BadZero = 0;\n"
                + "        public const int BadNegative = -3;\n";

            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource(Constants),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            Assert.Equal(2, parsed.Issues.Count);
            Assert.All(parsed.Issues, issue => Assert.True(issue.IsError));
            Assert.All(parsed.Issues, issue => Assert.Contains("正数", issue.Message));
        }

        [Fact]
        public void ValidateContract_ReservedNoneZero_IsAccepted()
        {
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource("        public const int None = 0;\n"),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            Assert.Empty(parsed.Issues);
        }

        [Fact]
        public void ValidateContract_ReservedNoneNonZero_IsError()
        {
            AnalyticsContractParseResult parsed = AnalyticsRules.ParseContract(
                AnalyticsTestSupport.ContractSource("        public const int None = 5;\n"),
                AnalyticsTestSupport.ContractLocation);

            Assert.True(parsed.Success);
            AnalyticsIssue issue = Assert.Single(parsed.Issues);
            Assert.True(issue.IsError);
            Assert.Contains("保留哨兵", issue.Message);
        }

        [Fact]
        public void Scan_UnknownEventReference_IsError()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Bad.cs", "var id = AnalyticsEventIds.Missing;\n")),
                AnalyticsTestSupport.DefaultContract());

            AnalyticsIssue issue = Assert.Single(result.Issues, item => item.IsError);
            Assert.Equal("Missing", issue.EventName);
            Assert.Equal("src/Game/Bad.cs:1", issue.Location);
            Assert.Contains("悬空引用", issue.Message);
            Assert.Equal(1, result.ErrorCount);
        }

        [Fact]
        public void Scan_KnownReferencesAreCounted()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Ok.cs", "AnalyticsEventIds.AppLaunch;\nAnalyticsEventIds.AppLaunch;\n")),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(2, result.ReferenceCount);
            Assert.Equal(2, result.ReferenceCounts["AppLaunch"]);
            Assert.Equal(0, result.ReferenceCounts["BattleEnd"]);
        }

        [Fact]
        public void Scan_MagicNumberConstructor_IsWarning()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(
                    ("src/Game/Track.cs", "analytics.Track(new AnalyticsEvent(1001, 1f, 2f, 3f, null));\n")),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(0, result.ErrorCount);
            Assert.Contains(
                result.Issues,
                issue => !issue.IsError && issue.Message.Contains("魔法数字") && issue.EventName == "1001");
            Assert.Equal(1, result.ConstructorCallCount);
        }

        [Fact]
        public void Scan_ConstructorArityMismatch_IsError()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(
                    ("src/Game/Track.cs", "analytics.Track(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, 1f, 2f, null));\n")),
                AnalyticsTestSupport.DefaultContract());

            AnalyticsIssue issue = Assert.Single(result.Issues, item => item.IsError);
            Assert.Equal("AppLaunch", issue.EventName);
            Assert.Contains("4", issue.Message);
            Assert.Contains("5", issue.Message);
        }

        [Fact]
        public void Scan_ConstantFirstArgument_NoWarning()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(
                    ("src/Game/Track.cs", "analytics.Track(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, 1f, 2f, 3f, null));\n")),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(0, result.ErrorCount);
            Assert.DoesNotContain(result.Issues, issue => issue.Message.Contains("魔法数字"));
            Assert.Equal(1, result.ConstructorCallCount);
            Assert.Equal(1, result.ReferenceCounts["AppLaunch"]);
        }

        [Fact]
        public void Scan_MultiFileAggregation_CountsAllReferences()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(
                    ("src/A.cs", "AnalyticsEventIds.AppLaunch;\n"),
                    ("src/B.cs", "AnalyticsEventIds.AppLaunch;\nAnalyticsEventIds.BattleEnd;\n")),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(3, result.ReferenceCount);
            Assert.Equal(2, result.ReferenceCounts["AppLaunch"]);
            Assert.Equal(1, result.ReferenceCounts["BattleEnd"]);
            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(0, result.HintCount);
        }

        [Fact]
        public void Scan_CommentReference_CountsAsReference()
        {
            // 锁定口径：文本级扫描不区分注释/字符串，注释里的引用也计入次数（已知误报）。
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Doc.cs", "// 见 AnalyticsEventIds.AppLaunch\n")),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(1, result.ReferenceCount);
            Assert.Equal(1, result.ReferenceCounts["AppLaunch"]);
            Assert.Equal(0, result.ErrorCount);
        }

        [Fact]
        public void Scan_UnusedEvent_IsHint()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Only.cs", "AnalyticsEventIds.AppLaunch;\n")),
                AnalyticsTestSupport.DefaultContract());

            AnalyticsIssue hint = Assert.Single(result.Issues, item => item.EventName == "BattleEnd");
            Assert.False(hint.IsError);
            Assert.Contains("废弃", hint.Message);
            Assert.Contains(AnalyticsTestSupport.ContractLocation, hint.Location);
            Assert.Equal(1, result.HintCount);
        }

        [Fact]
        public void Scan_ReservedNone_IsNeverReportedUnused()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                new Dictionary<string, string>(StringComparer.Ordinal),
                AnalyticsTestSupport.DefaultContract());

            Assert.DoesNotContain(result.Issues, issue => issue.EventName == "None");
            Assert.Equal(2, result.HintCount);
        }

        [Fact]
        public void Scan_MultiLineCall_IsParsed()
        {
            const string Content =
                "analytics.Track(new AnalyticsEvent(\n"
                + "    AnalyticsEventIds.AppLaunch,\n"
                + "    1f,\n"
                + "    2f,\n"
                + "    3f,\n"
                + "    null));\n";

            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Multi.cs", Content)),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(1, result.ConstructorCallCount);
            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(1, result.ReferenceCounts["AppLaunch"]);
        }

        [Fact]
        public void Scan_StringArgumentWithCommasAndParens_IsSingleArgument()
        {
            const string Content =
                "analytics.Track(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, 1f, 2f, 3f, \"a,b(c)\"));\n";

            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/StringArg.cs", Content)),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(0, result.ErrorCount);
            Assert.DoesNotContain(result.Issues, issue => issue.Message.Contains("构造参数"));
        }

        [Fact]
        public void Scan_NestedCallArgument_IsSingleArgument()
        {
            const string Content =
                "analytics.Track(new AnalyticsEvent(AnalyticsEventIds.AppLaunch, Math.Max(1, 2), 0f, 0f, null));\n";

            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Nested.cs", Content)),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(1, result.ConstructorCallCount);
        }

        [Fact]
        public void Scan_ArrayCreation_IsNotAConstructorCall()
        {
            const string Content = "private readonly AnalyticsEvent[] buffer = new AnalyticsEvent[4];\n";

            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Buffer.cs", Content)),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(0, result.ConstructorCallCount);
            Assert.Equal(0, result.ErrorCount);
        }

        [Fact]
        public void Scan_QualifiedConstructorName_IsRecognized()
        {
            const string Content =
                "metrics.Track(new AIFramework.Contracts.Services.AnalyticsEvent(\n"
                + "    AIFramework.Contracts.Services.AnalyticsEventIds.BattleEnd, 1, 1200, 0, null));\n";

            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Qualified.cs", Content)),
                AnalyticsTestSupport.DefaultContract());

            Assert.Equal(1, result.ConstructorCallCount);
            Assert.Equal(0, result.ErrorCount);
            Assert.Equal(1, result.ReferenceCounts["BattleEnd"]);
        }

        [Fact]
        public void Scan_ParameterlessConstruction_IsArityError()
        {
            AnalyticsScanResult result = AnalyticsRules.Scan(
                AnalyticsTestSupport.Files(("src/Game/Default.cs", "var e = new AnalyticsEvent();\n")),
                AnalyticsTestSupport.DefaultContract());

            AnalyticsIssue issue = Assert.Single(result.Issues, item => item.IsError);
            Assert.Contains("0 个", issue.Message);
        }

        [Fact]
        public void Issue_ToString_UsesRequiredFormat()
        {
            var error = new AnalyticsIssue("AppLaunch", "src/A.cs:12", "撞号", isError: true);
            var hint = new AnalyticsIssue("BattleEnd", "src/A.cs:13", "未引用", isError: false);

            Assert.Equal("✗ 事件=AppLaunch 位置=src/A.cs:12 撞号", error.ToString());
            Assert.Equal("⚠ 事件=BattleEnd 位置=src/A.cs:13 未引用", hint.ToString());
        }

        [Theory]
        [InlineData("src/Game/Foo.cs", false)]
        [InlineData("src/Game/tests/Foo.cs", true)]
        [InlineData("src/Game/TESTS/Foo.cs", true)]
        [InlineData("src/Game/bin/Foo.cs", true)]
        [InlineData("src/Game/obj/Debug/Foo.cs", true)]
        [InlineData("artifacts/obj/Foo.cs", true)]
        [InlineData("src/tests.cs", false)]
        public void IsExcludedPath_MatchesExcludedSegments(string path, bool expected)
        {
            Assert.Equal(expected, AnalyticsRules.IsExcludedPath(path));
        }
    }
}
