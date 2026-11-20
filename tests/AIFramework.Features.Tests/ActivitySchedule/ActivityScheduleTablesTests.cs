using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.Features.ActivitySchedule;
using Xunit;

namespace AIFramework.Features.Tests.ActivitySchedule
{
    /// <summary>
    /// 活动排期子表解析验收：合法字节逐字段回读（含 UTF-8 名字）；截断 / 尾部残留 / 时间倒置 /
    /// 重置小时越界 / 负 id / id 重复 / 空名字一律显式抛 <see cref="InvalidDataException"/>；
    /// 0 行是合法配置；表缺失抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public sealed class ActivityScheduleTablesTests
    {
        private static readonly ActivityScheduleRowSpec[] ThreeRows =
        {
            new ActivityScheduleRowSpec(1001, "周年庆典", 1767225600, 1775001600, 4, 1, 10),
            new ActivityScheduleRowSpec(2001, "轮换·青铜", 1793491200, 1796083200, 0, 2, 10),
            new ActivityScheduleRowSpec(2002, "限时冲刺", 1795564800, 1795651200, 23, 2, 20),
        };

        [Fact]
        public void Parse_ThreeRows_ExposesAllFieldsInRowOrder()
        {
            var tables = new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(ThreeRows));

            Assert.Equal(3, tables.Count);

            ActivityScheduleDef first = tables.Get(0);
            Assert.Equal(1001, first.ActivityId);
            Assert.Equal("周年庆典", first.Name);
            Assert.Equal(1767225600, first.StartUnixSeconds);
            Assert.Equal(1775001600, first.EndUnixSeconds);
            Assert.Equal(4, first.DailyResetHourUtc);
            Assert.Equal(1, first.RotationGroup);
            Assert.Equal(10, first.Priority);

            // UTF-8 多字节名字（含 2 字节的 '·'）必须按字节长度前缀正确回读。
            ActivityScheduleDef second = tables.Get(1);
            Assert.Equal(2001, second.ActivityId);
            Assert.Equal("轮换·青铜", second.Name);
            Assert.Equal(2, second.RotationGroup);

            // 行顺序 = 配置表顺序：解析器不排序、不去重重排。
            ActivityScheduleDef third = tables.Get(2);
            Assert.Equal(2002, third.ActivityId);
            Assert.Equal(23, third.DailyResetHourUtc);
            Assert.Equal(20, third.Priority);
        }

        [Fact]
        public void Parse_EmptyTable_IsAllowed()
        {
            var tables = new ActivityScheduleTables(ActivityScheduleTableStub.WithRows());

            Assert.Equal(0, tables.Count);
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ActivityScheduleTables(null!));
        }

        [Fact]
        public void Parse_MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithoutTable()));

            Assert.Contains(ActivityScheduleTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Get_OutOfRange_Throws()
        {
            var tables = new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(ThreeRows));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(3));
        }

        [Fact]
        public void Parse_NegativeRowCount_Throws()
        {
            byte[] payload = { 0xFF, 0xFF, 0xFF, 0xFF };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload)));

            Assert.Contains("rowCount 为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_HeaderShorterThanFourBytes_Throws()
        {
            byte[] payload = { 1, 0 };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload)));

            Assert.Contains("头部长度不足", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DeclaredRowsExceedBytes_ThrowsTruncated()
        {
            // 头部声明 2 行，物理上只有 1 行多的字节：应在分配前按"行数 × 最小行宽"挡住。
            byte[] payload = ActivityScheduleTableStub.BuildPayload(ThreeRows, declaredRowCount: 2);
            Array.Resize(ref payload, 4 + 30);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TruncatedMidRow_Throws()
        {
            // 声明 1 行且剩余字节数过了前置校验，但第 1 行读到一半就没了：字段级越界检查兜底。
            byte[] payload = ActivityScheduleTableStub.BuildPayload(ThreeRows, declaredRowCount: 1);
            Array.Resize(ref payload, 4 + 31);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TrailingBytes_Throws()
        {
            byte[] payload = ActivityScheduleTableStub.AppendTrailingBytes(
                ActivityScheduleTableStub.BuildPayload(ThreeRows), 4);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload)));

            Assert.Contains("尾部残留", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NameLengthNegative_Throws()
        {
            // [rowCount=1][activityId=1001][nameLen=-1] + 补齐到最小行宽（否则会先被"行数 × 行宽"前置校验挡住）。
            byte[] payload = ActivityScheduleTableStub.BuildRaw(1, 1001, -1, 0, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload)));

            Assert.Contains("长度为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NameDeclaredLongerThanPayload_Throws()
        {
            // [rowCount=1][activityId=1001][nameLen=999]：声明 999 字节名字，实际可读字节远不够。
            byte[] payload = ActivityScheduleTableStub.BuildRaw(1, 1001, 999, 0, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload)));

            Assert.Contains("name", exception.Message, StringComparison.Ordinal);
            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_EndNotAfterStart_Throws()
        {
            var sameSecond = new[]
            {
                new ActivityScheduleRowSpec(1001, "零长度窗口", 100, 100, 4, 1, 10),
            };

            InvalidDataException equal = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(sameSecond)));
            Assert.Contains("endUnixSeconds > startUnixSeconds", equal.Message, StringComparison.Ordinal);

            // 时间反向（结束早于开始）同样非法：窗口判定会整个失效。
            var reversed = new[]
            {
                new ActivityScheduleRowSpec(1001, "时间倒置", 200, 100, 4, 1, 10),
            };

            InvalidDataException backwards = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(reversed)));
            Assert.Contains("activityId 1001", backwards.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DailyResetHourOutOfRange_Throws()
        {
            var tooLate = new[]
            {
                new ActivityScheduleRowSpec(1001, "越界小时", 100, 200, 24, 1, 10),
            };

            InvalidDataException upper = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(tooLate)));
            Assert.Contains("dailyResetHourUtc", upper.Message, StringComparison.Ordinal);

            var negative = new[]
            {
                new ActivityScheduleRowSpec(1001, "越界小时", 100, 200, -1, 1, 10),
            };

            Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(negative)));
        }

        [Fact]
        public void Parse_ActivityIdNonPositive_Throws()
        {
            var negativeId = new[]
            {
                new ActivityScheduleRowSpec(-7, "负 id", 100, 200, 4, 1, 10),
            };

            InvalidDataException negative = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(negativeId)));
            Assert.Contains("activityId 必须为正", negative.Message, StringComparison.Ordinal);

            // 0 同样不可用：主键为 0 会让"打开集合"与事件 id 失去区分度。
            var zeroId = new[]
            {
                new ActivityScheduleRowSpec(0, "零 id", 100, 200, 4, 1, 10),
            };

            Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(zeroId)));
        }

        [Fact]
        public void Parse_DuplicateActivityId_Throws()
        {
            var duplicates = new[]
            {
                new ActivityScheduleRowSpec(1001, "第一行", 100, 200, 4, 1, 10),
                new ActivityScheduleRowSpec(1001, "第二行", 300, 400, 4, 1, 10),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(duplicates)));

            Assert.Contains("activityId 重复：1001", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_EmptyName_Throws()
        {
            var emptyName = new[]
            {
                new ActivityScheduleRowSpec(1001, string.Empty, 100, 200, 4, 1, 10),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new ActivityScheduleTables(ActivityScheduleTableStub.WithRows(emptyName)));

            Assert.Contains("name 不能为空", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 端到端守卫：直接解析仓库已导出的 <c>TbActivitySchedule.bin</c>，防止"tablegen 列顺序"与
        /// "切片解析器列顺序"各改各的。定长部分 4 字节表头 + 8 行（每行 28 字节 + UTF-8 名字字节）。
        /// </summary>
        [Fact]
        public void Parse_CommittedRepoBinary_MatchesEightRowContract()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(
                repoRoot, "src", "Adapters", "AIFramework.Adapters.Console", "DemoTables", "TbActivitySchedule.bin");
            byte[] payload = File.ReadAllBytes(path);

            var tables = new ActivityScheduleTables(ActivityScheduleTableStub.WithPayload(payload));

            // 4 + 40×4（四个 4 字中文名）+ 42×3（"轮换·青铜/白银/黄金"）+ 48（"轮换·限时冲刺"）= 338。
            Assert.Equal(338, payload.Length);
            Assert.Equal(8, tables.Count);

            ActivityScheduleDef first = tables.Get(0);
            Assert.Equal(1001, first.ActivityId);
            Assert.Equal("周年庆典", first.Name);
            Assert.Equal(1767225600, first.StartUnixSeconds);
            Assert.Equal(1775001600, first.EndUnixSeconds);
            Assert.Equal(4, first.DailyResetHourUtc);
            Assert.Equal(1, first.RotationGroup);
            Assert.Equal(10, first.Priority);

            ActivityScheduleDef last = tables.Get(7);
            Assert.Equal(2004, last.ActivityId);
            Assert.Equal("轮换·限时冲刺", last.Name);
            Assert.Equal(1795564800, last.StartUnixSeconds);
            Assert.Equal(1795651200, last.EndUnixSeconds);
            Assert.Equal(12, last.DailyResetHourUtc);
            Assert.Equal(2, last.RotationGroup);
            Assert.Equal(20, last.Priority);

            // 两个轮换组各 4 条，且每组内时间窗口首尾错开：组内同时只应有一个活动落在窗口里。
            for (int i = 0; i < tables.Count; i++)
            {
                ActivityScheduleDef row = tables.Get(i);
                Assert.True(row.EndUnixSeconds > row.StartUnixSeconds, "activityId " + row.ActivityId + " 时间窗口倒置。");
                Assert.InRange(row.DailyResetHourUtc, 0, 23);
                Assert.Equal(i < 4 ? 1 : 2, row.RotationGroup);
            }
        }

        private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
        {
            // <repo>/tests/AIFramework.Features.Tests/ActivitySchedule/ActivityScheduleTablesTests.cs 向上三级
            DirectoryInfo? directory = new FileInfo(callerFilePath).Directory;
            for (int i = 0; i < 3 && directory != null; i++)
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory!.FullName;
        }
    }
}
