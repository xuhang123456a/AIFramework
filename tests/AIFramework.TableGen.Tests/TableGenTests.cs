#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace AIFramework.TableGen.Tests
{
    /// <summary>测试侧小工具：把"一段 JSON"直接变成形状/字节/回读结果。</summary>
    internal static class TableJson
    {
        internal static TableData Parse(string json)
        {
            return TableSchemaReader.Parse(json, "单元测试");
        }

        internal static TableSchema Schema(string json)
        {
            return Parse(json).Schema;
        }

        internal static byte[] Encode(string json)
        {
            return TableEncoder.Encode(Parse(json));
        }

        internal static DecodedTable EncodeAndDecode(string json)
        {
            TableData data = Parse(json);
            return TableBinaryReader.Read(data.Schema, TableEncoder.Encode(data));
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

    /// <summary>一次性的临时工作区：<c>tables/</c> 放 JSON，<c>out/</c> 放导出结果，用完删干净。</summary>
    internal sealed class TempWorkspace : IDisposable
    {
        internal string Root { get; }

        internal string TablesDirectory
        {
            get { return Path.Combine(Root, "tables"); }
        }

        internal string OutputDirectory
        {
            get { return Path.Combine(Root, "out"); }
        }

        internal TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "aifw-tablegen-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TablesDirectory);
            Directory.CreateDirectory(OutputDirectory);
        }

        internal void WriteTable(string fileName, string json)
        {
            File.WriteAllText(Path.Combine(TablesDirectory, fileName), json, new UTF8Encoding(false));
        }

        internal CliResult RunCli(params string[] args)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            int code = TableGenCli.Run(args, output, error, Root);
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

    /// <summary>单列编码：逐字节断言，不依赖解析器。</summary>
    public sealed class ColumnEncodingTests
    {
        [Fact]
        public void Int32_WritesRowCountThenLittleEndianValues()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""v"", ""type"": ""int32"" } ],
  ""rows"": [ { ""v"": 1 }, { ""v"": 258 }, { ""v"": -2 } ]
}";

            byte[] bytes = TableJson.Encode(Json);

            Assert.Equal(
                new byte[] { 3, 0, 0, 0, 1, 0, 0, 0, 2, 1, 0, 0, 0xFE, 0xFF, 0xFF, 0xFF },
                bytes);
        }

        [Fact]
        public void Float32_WritesIeee754LittleEndian()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""v"", ""type"": ""float32"" } ],
  ""rows"": [ { ""v"": 1.5 }, { ""v"": -0.5 } ]
}";

            byte[] bytes = TableJson.Encode(Json);

            Assert.Equal(
                new byte[] { 2, 0, 0, 0, 0x00, 0x00, 0xC0, 0x3F, 0x00, 0x00, 0x00, 0xBF },
                bytes);
        }

        [Fact]
        public void Bool_WritesExactlyOneBytePerValue()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""v"", ""type"": ""bool"" } ],
  ""rows"": [ { ""v"": true }, { ""v"": false }, { ""v"": true } ]
}";

            byte[] bytes = TableJson.Encode(Json);

            // 4 字节表头 + 3 个单字节 bool = 7 字节（bool 不是 4 字节 int32）。
            Assert.Equal(new byte[] { 3, 0, 0, 0, 1, 0, 1 }, bytes);
        }

        [Fact]
        public void String_EmptyStringStillWritesFourByteLengthPrefix()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""s"", ""type"": ""string"" } ],
  ""rows"": [ { ""s"": """" } ]
}";

            byte[] bytes = TableJson.Encode(Json);

            Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }, bytes);
        }

        [Theory]
        [InlineData("Play", 4)]
        [InlineData("开始", 6)]
        [InlineData("ゲーム開始", 15)]
        public void String_LengthPrefixIsUtf8ByteCountNotCharCount(string text, int expectedByteCount)
        {
            string json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""s"", ""type"": ""string"" } ],
  ""rows"": [ { ""s"": """ + text + @""" } ]
}";

            byte[] bytes = TableJson.Encode(json);

            Assert.Equal(4 + 4 + expectedByteCount, bytes.Length);
            Assert.Equal(expectedByteCount, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(text, TableJson.EncodeAndDecode(json).Rows[0]["s"]);
        }

        [Fact]
        public void Int32Array_WritesCountThenElements()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""t"", ""type"": ""int32[]"" } ],
  ""rows"": [ { ""t"": [3, 1] }, { ""t"": [] } ]
}";

            byte[] bytes = TableJson.Encode(Json);

            Assert.Equal(
                new byte[] { 2, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 },
                bytes);
        }

        [Fact]
        public void StringArray_WritesCountThenLengthPrefixedItems()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""s"", ""type"": ""string[]"" } ],
  ""rows"": [ { ""s"": ["""", ""签到""] } ]
}";

            byte[] bytes = TableJson.Encode(Json);

            // 行数(4) + 元素数(4) + 空串长度前缀(4) + 长度前缀(4) + "签到" 的 6 字节
            Assert.Equal(22, bytes.Length);
            Assert.Equal(1, BitConverter.ToInt32(bytes, 0));
            Assert.Equal(2, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(0, BitConverter.ToInt32(bytes, 8));
            Assert.Equal(6, BitConverter.ToInt32(bytes, 12));

            string[] items = (string[])TableJson.EncodeAndDecode(Json).Rows[0]["s"];
            Assert.Equal(new[] { "", "签到" }, items);
        }

        [Fact]
        public void AllScalarKinds_RoundTripThroughBinaryReader()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [
    { ""name"": ""id"", ""type"": ""int32"" },
    { ""name"": ""rate"", ""type"": ""float32"" },
    { ""name"": ""flag"", ""type"": ""bool"" },
    { ""name"": ""name"", ""type"": ""string"" }
  ],
  ""rows"": [
    { ""id"": -7, ""rate"": 0.25, ""flag"": true, ""name"": ""甲"" },
    { ""id"": 0, ""rate"": -3, ""flag"": false, ""name"": """" }
  ]
}";

            DecodedTable decoded = TableJson.EncodeAndDecode(Json);

            Assert.Equal("TbUnit", decoded.TableName);
            Assert.Equal(2, decoded.Rows.Count);
            Assert.Equal(-7, decoded.Rows[0]["id"]);
            Assert.Equal(0.25f, decoded.Rows[0]["rate"]);
            Assert.Equal(true, decoded.Rows[0]["flag"]);
            Assert.Equal("甲", decoded.Rows[0]["name"]);
            Assert.Equal(0, decoded.Rows[1]["id"]);
            Assert.Equal(-3f, decoded.Rows[1]["rate"]);
            Assert.Equal(false, decoded.Rows[1]["flag"]);
            Assert.Equal(string.Empty, decoded.Rows[1]["name"]);
        }

        [Fact]
        public void BinaryReader_NegativeStringLength_Throws()
        {
            TableSchema schema = TableJson.Schema(@"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""s"", ""type"": ""string"" } ],
  ""rows"": []
}");

            // [rowCount=1][length=-1]：长度前缀为负是畸形字节流，必须显式报错而不是空串兜底。
            byte[] payload = { 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

            TableGenException ex = Assert.Throws<TableGenException>(() => TableBinaryReader.Read(schema, payload));
            Assert.Contains("字符串长度为负", ex.Message);
        }

        [Fact]
        public void BinaryReader_TruncatedPayload_Throws()
        {
            TableSchema schema = TableJson.Schema(@"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""v"", ""type"": ""int32"" } ],
  ""rows"": []
}");

            TableGenException ex = Assert.Throws<TableGenException>(() => TableBinaryReader.Read(schema, new byte[] { 2, 0, 0, 0, 1 }));
            Assert.Contains("截断", ex.Message);
        }
    }

    /// <summary>表定义校验：坏 JSON 必须当场炸，且错误定位到行/列。</summary>
    public sealed class SchemaValidationTests
    {
        [Fact]
        public void UnknownColumnType_ThrowsWithColumnName()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""id"", ""type"": ""int64"" } ],
  ""rows"": [ { ""id"": 1 } ]
}";

            TableGenException ex = Assert.Throws<TableGenException>(() => TableJson.Parse(Json));
            Assert.Contains("列类型未知", ex.Message);
            Assert.Contains("int64", ex.Message);
            Assert.Contains("id", ex.Message);
        }

        [Fact]
        public void MissingRowField_Throws()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [
    { ""name"": ""id"", ""type"": ""int32"" },
    { ""name"": ""name"", ""type"": ""string"" }
  ],
  ""rows"": [ { ""id"": 1 } ]
}";

            TableGenException ex = Assert.Throws<TableGenException>(() => TableJson.Parse(Json));
            Assert.Contains("缺少列", ex.Message);
            Assert.Contains("name", ex.Message);
        }

        [Fact]
        public void ExtraRowField_ThrowsColumnCountMismatch()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""id"", ""type"": ""int32"" } ],
  ""rows"": [ { ""id"": 1, ""ghost"": 2 } ]
}";

            TableGenException ex = Assert.Throws<TableGenException>(() => TableJson.Parse(Json));
            Assert.Contains("列数不匹配", ex.Message);
            Assert.Contains("ghost", ex.Message);
        }

        [Fact]
        public void ValueTypeMismatch_ThrowsWithRowIndex()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""id"", ""type"": ""int32"" } ],
  ""rows"": [ { ""id"": ""abc"" } ]
}";

            TableGenException ex = Assert.Throws<TableGenException>(() => TableJson.Encode(Json));
            Assert.Contains("期望 int32", ex.Message);
            Assert.Contains("第 0 行", ex.Message);
        }

        [Fact]
        public void BoolColumn_RejectsNumberOneInsteadOfTrue()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [ { ""name"": ""flag"", ""type"": ""bool"" } ],
  ""rows"": [ { ""flag"": 1 } ]
}";

            TableGenException ex = Assert.Throws<TableGenException>(() => TableJson.Encode(Json));
            Assert.Contains("期望 bool", ex.Message);
        }

        [Fact]
        public void DuplicateColumnName_Throws()
        {
            const string Json = @"
{
  ""table"": ""TbUnit"",
  ""columns"": [
    { ""name"": ""id"", ""type"": ""int32"" },
    { ""name"": ""id"", ""type"": ""string"" }
  ],
  ""rows"": []
}";

            TableGenException ex = Assert.Throws<TableGenException>(() => TableJson.Parse(Json));
            Assert.Contains("列名重复", ex.Message);
        }

        [Fact]
        public void ExportDirectory_FileNameMustMatchTableField()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbUnit.json", @"{ ""table"": ""TbOther"", ""columns"": [ { ""name"": ""id"", ""type"": ""int32"" } ], ""rows"": [] }");

            TableGenException ex = Assert.Throws<TableGenException>(
                () => TableExporter.ExportDirectory(workspace.TablesDirectory));
            Assert.Contains("与文件名不一致", ex.Message);
        }
    }

    /// <summary>目录级导出 + <c>--check</c> 漂移门禁。</summary>
    public sealed class ExportAndCheckTests
    {
        private const string TableA = @"
{
  ""table"": ""TbTestA"",
  ""columns"": [ { ""name"": ""id"", ""type"": ""int32"" } ],
  ""rows"": [ { ""id"": 1 }, { ""id"": 2 } ]
}";

        private const string TableB = @"
{
  ""table"": ""TbTestB"",
  ""columns"": [
    { ""name"": ""key"", ""type"": ""string"" },
    { ""name"": ""count"", ""type"": ""int32"" }
  ],
  ""rows"": [ { ""key"": ""金币"", ""count"": 10 } ]
}";

        [Fact]
        public void Export_WritesOneBinPerJson_WithExpectedBytes()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbTestA.json", TableA);
            workspace.WriteTable("TbTestB.json", TableB);

            CliResult result = workspace.RunCli(
                "--tables", workspace.TablesDirectory,
                "--out", workspace.OutputDirectory);

            Assert.Equal(TableGenCli.ExitSuccess, result.Code);
            Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0 },
                File.ReadAllBytes(Path.Combine(workspace.OutputDirectory, "TbTestA.bin")));
            Assert.Equal(4 + 4 + 6 + 4, File.ReadAllBytes(Path.Combine(workspace.OutputDirectory, "TbTestB.bin")).Length);
            Assert.Equal(2, Directory.GetFiles(workspace.OutputDirectory, "*.bin").Length);
        }

        [Fact]
        public void Check_FreshlyExported_Fails()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbTestA.json", TableA);

            // 还没导出过：--check 必须先报缺失，不能因为"目录存在"就放行。
            CliResult result = workspace.RunCli(
                "--tables", workspace.TablesDirectory,
                "--out", workspace.OutputDirectory,
                "--check");

            Assert.Equal(TableGenCli.ExitDrift, result.Code);
            Assert.Contains("缺失", result.Err);
        }

        [Fact]
        public void Check_AfterExport_Passes()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbTestA.json", TableA);
            workspace.WriteTable("TbTestB.json", TableB);
            workspace.RunCli("--tables", workspace.TablesDirectory, "--out", workspace.OutputDirectory);

            CliResult result = workspace.RunCli(
                "--tables", workspace.TablesDirectory,
                "--out", workspace.OutputDirectory,
                "--check");

            Assert.Equal(TableGenCli.ExitSuccess, result.Code);
            Assert.Contains("--check 通过", result.Out);
        }

        [Fact]
        public void Check_AfterSingleByteTamper_FailsAndNamesTable()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbTestA.json", TableA);
            workspace.WriteTable("TbTestB.json", TableB);
            workspace.RunCli("--tables", workspace.TablesDirectory, "--out", workspace.OutputDirectory);

            string path = Path.Combine(workspace.OutputDirectory, "TbTestB.bin");
            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            CliResult result = workspace.RunCli(
                "--tables", workspace.TablesDirectory,
                "--out", workspace.OutputDirectory,
                "--check");

            Assert.Equal(TableGenCli.ExitDrift, result.Code);
            Assert.Contains("TbTestB", result.Err);
            Assert.Contains("内容不一致", result.Err);
        }

        [Fact]
        public void Check_ExtraBinaryWithoutJson_Fails()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbTestA.json", TableA);
            workspace.RunCli("--tables", workspace.TablesDirectory, "--out", workspace.OutputDirectory);
            File.WriteAllBytes(Path.Combine(workspace.OutputDirectory, "TbGhost.bin"), new byte[] { 0, 0, 0, 0 });

            CliResult result = workspace.RunCli(
                "--tables", workspace.TablesDirectory,
                "--out", workspace.OutputDirectory,
                "--check");

            Assert.Equal(TableGenCli.ExitDrift, result.Code);
            Assert.Contains("多余", result.Err);
            Assert.Contains("TbGhost", result.Err);
        }

        [Fact]
        public void InvalidTableJson_ExitsWithSchemaErrorCode()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbBad.json", @"{ ""table"": ""TbBad"", ""columns"": [ { ""name"": ""id"", ""type"": ""money"" } ], ""rows"": [] }");

            CliResult result = workspace.RunCli(
                "--tables", workspace.TablesDirectory,
                "--out", workspace.OutputDirectory);

            Assert.Equal(TableGenCli.ExitInvalidTable, result.Code);
            Assert.Contains("列类型未知", result.Err);
        }

        [Fact]
        public void UnknownOption_ExitsWithUsageCode()
        {
            using var workspace = new TempWorkspace();
            workspace.WriteTable("TbTestA.json", TableA);

            CliResult result = workspace.RunCli("--out", workspace.OutputDirectory, "--nope");

            Assert.Equal(TableGenCli.ExitUsage, result.Code);
            Assert.Contains("未知参数", result.Err);
        }
    }

    /// <summary>
    /// 仓库金标准：<c>tables/*.json</c> 导出的字节必须与仓库里已提交的 <c>.bin</c> 逐字节一致。
    /// <para>这条用例替代"CI 记得跑 --check"：改了 JSON 忘导出，`dotnet test` 当场变红。</para>
    /// </summary>
    public sealed class RepoTablesTests
    {
        [Fact]
        public void AllRepoTables_ExportToSixteenTables()
        {
            string repoRoot = FindRepoRoot();
            IReadOnlyList<SchemaExport> exports = TableExporter.ExportDirectory(Path.Combine(repoRoot, "tables"));

            Assert.Equal(16, exports.Count);
            Assert.Equal("TbAchievement", exports[0].TableName);
            Assert.Equal("TbActivitySchedule", exports[1].TableName);
            Assert.Equal("TbGuideStep", exports[5].TableName);
            Assert.Equal("TbTycoonBuilding", exports[15].TableName);
        }

        [Fact]
        public void AllRepoTables_MatchCommittedBinariesByteForByte()
        {
            string repoRoot = FindRepoRoot();
            IReadOnlyList<SchemaExport> exports = TableExporter.ExportDirectory(Path.Combine(repoRoot, "tables"));

            string[] targets =
            {
                Path.Combine(repoRoot, "unity", "Assets", "StreamingAssets", "AIFrameworkDemo"),
                Path.Combine(repoRoot, "src", "Adapters", "AIFramework.Adapters.Console", "DemoTables")
            };

            var problems = new List<string>();
            for (int i = 0; i < targets.Length; i++)
            {
                IReadOnlyList<TableDifference> differences = TableComparison.CompareDirectory(exports, targets[i]);
                for (int k = 0; k < differences.Count; k++)
                {
                    problems.Add("[" + targets[i] + "] " + differences[k].Detail);
                }
            }

            Assert.True(
                problems.Count == 0,
                "配置表产物与 JSON 源不一致，执行 dotnet run --project tools/tablegen 重新导出："
                + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Cli_CheckAgainstDefaultTargets_Passes()
        {
            string repoRoot = FindRepoRoot();
            var output = new StringWriter();
            var error = new StringWriter();

            int code = TableGenCli.Run(new[] { "--check" }, output, error, repoRoot);

            Assert.Equal(TableGenCli.ExitSuccess, code);
            Assert.Contains("逐字节一致", output.ToString());
        }

        private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
        {
            // <repo>/tests/AIFramework.TableGen.Tests/TableGenTests.cs 向上两级
            DirectoryInfo? directory = new FileInfo(callerFilePath).Directory;
            for (int i = 0; i < 2 && directory != null; i++)
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}
