using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.Features.Achievement;
using Xunit;

namespace AIFramework.Features.Tests.Achievement
{
    /// <summary>
    /// 成就子表解析验收：字节布局必须与 <c>tools/tablegen</c> 按 JSON 列顺序导出的结果逐字节一致；
    /// 表缺失、截断、尾部残留、ID 重复、指标未知等畸形配置一律显式抛异常（空表除外）。
    /// </summary>
    public sealed class AchievementTablesTests
    {
        /// <summary>仓库真实子表字节数：4 字节表头 + 12 行（首行 30 字节 + 其余 11 行 × 36 字节）。</summary>
        private const int RealTableByteCount = 430;

        [Fact]
        public void RepoBinary_MatchesExporterLayoutAndParsesAllRows()
        {
            string repoRoot = RepoRoot();
            string consolePath = Path.Combine(
                repoRoot,
                "src",
                "Adapters",
                "AIFramework.Adapters.Console",
                "DemoTables",
                "TbAchievement.bin");
            string unityPath = Path.Combine(
                repoRoot,
                "unity",
                "Assets",
                "StreamingAssets",
                "AIFrameworkDemo",
                "TbAchievement.bin");

            byte[] consoleBytes = File.ReadAllBytes(consolePath);
            byte[] unityBytes = File.ReadAllBytes(unityPath);

            // 两个宿主同源：导出的 .bin 必须逐字节相同，且长度被本用例钉死（改 JSON 必须重新导出）。
            Assert.Equal(RealTableByteCount, consoleBytes.Length);
            Assert.Equal(consoleBytes, unityBytes);

            var tables = new AchievementTables(AchievementTableStub.WithPayload(consoleBytes));

            Assert.Equal(12, tables.Count);

            // 解析器按 JSON 列顺序读：首行 1001/首杀/m0/t1/3001/10，末行 5002/富甲一方/m4/t100000/3402/1。
            AchievementDef first = tables.Get(0);
            Assert.Equal(1001, first.AchievementId);
            Assert.Equal("首杀", first.Name);
            Assert.Equal(AchievementMetrics.Kill, first.Metric);
            Assert.Equal(1, first.Target);
            Assert.Equal(3001, first.RewardItemId);
            Assert.Equal(10, first.RewardCount);

            AchievementDef last = tables.Get(11);
            Assert.Equal(5002, last.AchievementId);
            Assert.Equal("富甲一方", last.Name);
            Assert.Equal(AchievementMetrics.Gold, last.Metric);
            Assert.Equal(100000, last.Target);
            Assert.Equal(3402, last.RewardItemId);
            Assert.Equal(1, last.RewardCount);

            // 12 行覆盖全部 5 种指标，且行序按 achievementId 升序。
            var coveredMetrics = new bool[AchievementMetrics.Count];
            for (int i = 0; i < tables.Count; i++)
            {
                AchievementDef def = tables.Get(i);
                coveredMetrics[def.Metric] = true;
                if (i > 0)
                {
                    Assert.True(tables.Get(i - 1).AchievementId < def.AchievementId);
                }
            }

            Assert.All(coveredMetrics, covered => Assert.True(covered));
        }

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new AchievementTables(AchievementTableStub.WithoutTable()));

            Assert.Contains(AchievementTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void EmptyTable_IsValidAndHasZeroRows()
        {
            AchievementTables tables = new AchievementTables(AchievementTableStub.WithRows());

            Assert.Equal(0, tables.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(0));
        }

        [Fact]
        public void ShortHeader_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithPayload(new byte[] { 0, 0 })));
        }

        [Fact]
        public void NegativeRowCount_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithPayload(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })));
        }

        [Fact]
        public void DeclaredRowCountLargerThanPayload_ThrowsInvalidDataException()
        {
            // 头部声明 1 行、实际 0 行：前置校验必须挡住，不能按"读到哪算哪"。
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(
                    AchievementTableStub.WithPayload(AchievementTableStub.BuildPayload(Array.Empty<AchievementRowSpec>(), 1))));
        }

        [Fact]
        public void TrailingBytes_ThrowsInvalidDataException()
        {
            byte[] payload = AchievementTableStub.BuildPayload(Array.Empty<AchievementRowSpec>(), 0);
            byte[] withTrailing = new byte[payload.Length + 1];
            Array.Copy(payload, withTrailing, payload.Length);
            withTrailing[payload.Length] = 7;

            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithPayload(withTrailing)));
        }

        [Fact]
        public void TruncatedString_ThrowsInvalidDataException()
        {
            // 一行合法字节里把 name 长度前缀改大，制造 UTF-8 字节越界。
            byte[] payload = AchievementTableStub.BuildPayload(
                new[] { new AchievementRowSpec(1001, "A", AchievementMetrics.Kill, 1, 3001, 1) });
            int nameLengthOffset = 4 + 4;
            payload[nameLengthOffset] = 0xFF;
            payload[nameLengthOffset + 1] = 0x00;
            payload[nameLengthOffset + 2] = 0x00;
            payload[nameLengthOffset + 3] = 0x00;

            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithPayload(payload)));
        }

        [Fact]
        public void DuplicateAchievementId_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithRows(
                    new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 1),
                    new AchievementRowSpec(1001, "重复", AchievementMetrics.Kill, 2, 3002, 1))));
        }

        [Fact]
        public void UnknownMetric_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithRows(
                    new AchievementRowSpec(1001, "未知指标", AchievementMetrics.Count, 1, 3001, 1))));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NonPositiveTarget_ThrowsInvalidDataException(int target)
        {
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithRows(
                    new AchievementRowSpec(1001, "非法目标", AchievementMetrics.Kill, target, 3001, 1))));
        }

        [Fact]
        public void EmptyName_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithRows(
                    new AchievementRowSpec(1001, string.Empty, AchievementMetrics.Kill, 1, 3001, 1))));
        }

        [Fact]
        public void NegativeRewardCount_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new AchievementTables(AchievementTableStub.WithRows(
                    new AchievementRowSpec(1001, "负奖励", AchievementMetrics.Kill, 1, 3001, -1))));
        }

        [Fact]
        public void Rows_AreSortedByAchievementId()
        {
            AchievementTables tables = new AchievementTables(AchievementTableStub.WithRows(
                new AchievementRowSpec(2001, "后写入", AchievementMetrics.Clear, 1, 3101, 1),
                new AchievementRowSpec(1001, "先写入", AchievementMetrics.Kill, 1, 3001, 1)));

            Assert.Equal(1001, tables.Get(0).AchievementId);
            Assert.Equal(2001, tables.Get(1).AchievementId);
        }

        [Fact]
        public void Get_RejectsOutOfRangeIndex()
        {
            AchievementTables tables = new AchievementTables(AchievementTableStub.WithRows(
                new AchievementRowSpec(1001, "首杀", AchievementMetrics.Kill, 1, 3001, 1)));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(1));
        }

        /// <summary>编译期拿到本文件绝对路径，向上三级即仓根（Achievement → AIFramework.Features.Tests → tests → 仓根）。</summary>
        private static string RepoRoot([CallerFilePath] string callerFile = "")
        {
            string directory = Path.GetDirectoryName(callerFile)
                ?? throw new InvalidOperationException("无法从 CallerFilePath 得到测试源文件目录：" + callerFile);
            return Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
        }
    }
}
