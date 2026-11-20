using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.Features.Guide;
using Xunit;

namespace AIFramework.Features.Tests.Guide
{
    /// <summary>
    /// 引导子表解析验收：字节布局必须与 <c>tools/tablegen</c> 按 JSON 列顺序导出的结果逐字节一致；
    /// 表缺失、截断、尾部残留、步号不连续、可跳过/触发/目标非法等畸形配置一律显式抛异常（空表除外）。
    /// </summary>
    public sealed class GuideTablesTests
    {
        /// <summary>仓库真实子表字节数：4 字节表头 + 10 行（标题与提示的 UTF-8 长度不等长）。</summary>
        private const int RealTableByteCount = 674;

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
                "TbGuideStep.bin");
            string unityPath = Path.Combine(
                repoRoot,
                "unity",
                "Assets",
                "StreamingAssets",
                "AIFrameworkDemo",
                "TbGuideStep.bin");

            byte[] consoleBytes = File.ReadAllBytes(consolePath);
            byte[] unityBytes = File.ReadAllBytes(unityPath);

            // 两个宿主同源：导出的 .bin 必须逐字节相同，且长度被本用例钉死（改 JSON 必须重新导出）。
            Assert.Equal(RealTableByteCount, consoleBytes.Length);
            Assert.Equal(consoleBytes, unityBytes);

            var tables = new GuideTables(GuideTableStub.WithPayload(consoleBytes));

            Assert.Equal(10, tables.Count);

            // 解析器按 JSON 列顺序读：首行 1/欢迎/…/t0/target0/skippable0，末行 10/…/t4/target7/skippable1。
            GuideDef first = tables.Get(0);
            Assert.Equal(1, first.StepId);
            Assert.Equal("欢迎", first.Title);
            Assert.Equal("点击开始按钮进入游戏", first.Hint);
            Assert.Equal(GuideTriggers.Launch, first.Trigger);
            Assert.Equal(0, first.TargetId);
            Assert.False(first.Skippable);

            GuideDef last = tables.Get(9);
            Assert.Equal(10, last.StepId);
            Assert.Equal("领取第 7 天签到", last.Title);
            Assert.Equal(GuideTriggers.SignInClaimed, last.Trigger);
            Assert.Equal(7, last.TargetId);
            Assert.True(last.Skippable);

            // 链路不变量：stepId 从 1 连续（stepId == 下标 + 1），且 0..5 的触发类别全部被真实配置覆盖。
            var coveredTriggers = new bool[GuideTriggers.Count];
            for (int i = 0; i < tables.Count; i++)
            {
                GuideDef def = tables.Get(i);
                Assert.Equal(i + 1, def.StepId);
                Assert.True(def.Title.Length > 0);
                coveredTriggers[def.Trigger] = true;
            }

            Assert.All(coveredTriggers, covered => Assert.True(covered));
        }

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new GuideTables(GuideTableStub.WithoutTable()));

            Assert.Contains(GuideTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void EmptyTable_IsValidAndHasZeroRows()
        {
            GuideTables tables = new GuideTables(GuideTableStub.WithRows());

            Assert.Equal(0, tables.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(0));
        }

        [Fact]
        public void ShortHeader_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithPayload(new byte[] { 0, 0 })));
        }

        [Fact]
        public void NegativeRowCount_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithPayload(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF })));
        }

        [Fact]
        public void MoreThanThirtyTwoSteps_ThrowsInvalidDataException()
        {
            // 存档位图是 int：33 步必须加载期挡住，否则落盘会静默丢进度。
            var rows = new GuideRowSpec[33];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new GuideRowSpec(i + 1, "步", "提示", GuideTriggers.Launch, 0, 1);
            }

            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithRows(rows)));
        }

        [Fact]
        public void DeclaredRowCountLargerThanPayload_ThrowsInvalidDataException()
        {
            // 头部声明 2 行、实际 0 行：前置校验必须挡住，不能按"读到哪算哪"。
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(
                    GuideTableStub.WithPayload(GuideTableStub.BuildPayload(Array.Empty<GuideRowSpec>(), 2))));
        }

        [Fact]
        public void TrailingBytes_ThrowsInvalidDataException()
        {
            byte[] payload = GuideTableStub.BuildPayload(Array.Empty<GuideRowSpec>(), 0);
            byte[] withTrailing = new byte[payload.Length + 1];
            Array.Copy(payload, withTrailing, payload.Length);
            withTrailing[payload.Length] = 7;

            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithPayload(withTrailing)));
        }

        [Fact]
        public void TruncatedTitle_ThrowsInvalidDataException()
        {
            // 一行合法字节里把 title 长度前缀改大，制造 UTF-8 字节越界。
            byte[] payload = GuideTableStub.BuildPayload(
                new[] { new GuideRowSpec(1, "A", "h", GuideTriggers.Launch, 0, 0) });
            int titleLengthOffset = 4 + 4;
            payload[titleLengthOffset] = 0xFF;
            payload[titleLengthOffset + 1] = 0x00;
            payload[titleLengthOffset + 2] = 0x00;
            payload[titleLengthOffset + 3] = 0x00;

            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithPayload(payload)));
        }

        [Fact]
        public void DuplicateStepId_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithRows(
                    new GuideRowSpec(1, "第一步", "h", GuideTriggers.Launch, 0, 0),
                    new GuideRowSpec(1, "重复", "h", GuideTriggers.Lobby, 0, 1))));
        }

        [Fact]
        public void StepIdGap_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithRows(
                    new GuideRowSpec(1, "第一步", "h", GuideTriggers.Launch, 0, 0),
                    new GuideRowSpec(3, "跳了一步", "h", GuideTriggers.Lobby, 0, 1))));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(2)]
        public void IllegalSkippable_ThrowsInvalidDataException(int skippable)
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithRows(
                    new GuideRowSpec(1, "非法可跳过", "h", GuideTriggers.Launch, 0, skippable))));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(6)]
        public void UnknownTrigger_ThrowsInvalidDataException(int trigger)
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithRows(
                    new GuideRowSpec(1, "未知触发", "h", trigger, 0, 0))));
        }

        [Fact]
        public void NegativeTargetId_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithRows(
                    new GuideRowSpec(1, "负目标", "h", GuideTriggers.ShopOpen, -1, 0))));
        }

        [Fact]
        public void EmptyTitle_ThrowsInvalidDataException()
        {
            Assert.Throws<InvalidDataException>(
                () => new GuideTables(GuideTableStub.WithRows(
                    new GuideRowSpec(1, string.Empty, "h", GuideTriggers.Launch, 0, 0))));
        }

        [Fact]
        public void EmptyHint_IsAccepted()
        {
            // hint 允许为空串：有些步骤只要高亮目标，不需要额外文案。
            GuideTables tables = new GuideTables(GuideTableStub.WithRows(
                new GuideRowSpec(1, "只高亮", string.Empty, GuideTriggers.Launch, 0, 0)));

            Assert.Equal(string.Empty, tables.Get(0).Hint);
        }

        [Fact]
        public void Rows_AreSortedByStepId()
        {
            GuideTables tables = new GuideTables(GuideTableStub.WithRows(
                new GuideRowSpec(2, "后写入", "h", GuideTriggers.Lobby, 0, 0),
                new GuideRowSpec(1, "先写入", "h", GuideTriggers.Launch, 0, 0)));

            Assert.Equal(1, tables.Get(0).StepId);
            Assert.Equal(2, tables.Get(1).StepId);
        }

        [Fact]
        public void Get_RejectsOutOfRangeIndex()
        {
            GuideTables tables = new GuideTables(GuideTableStub.WithRows(
                new GuideRowSpec(1, "唯一步", "h", GuideTriggers.Launch, 0, 0)));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(1));
        }

        /// <summary>编译期拿到本文件绝对路径，向上三级即仓根（Guide → AIFramework.Features.Tests → tests → 仓根）。</summary>
        private static string RepoRoot([CallerFilePath] string callerFile = "")
        {
            string directory = Path.GetDirectoryName(callerFile)
                ?? throw new InvalidOperationException("无法从 CallerFilePath 得到测试源文件目录：" + callerFile);
            return Path.GetFullPath(Path.Combine(directory, "..", "..", ".."));
        }
    }
}
