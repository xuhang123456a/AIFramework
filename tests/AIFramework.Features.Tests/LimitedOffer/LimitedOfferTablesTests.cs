using System;
using System.IO;
using System.Runtime.CompilerServices;
using AIFramework.Features.LimitedOffer;
using Xunit;

namespace AIFramework.Features.Tests.LimitedOffer
{
    /// <summary>
    /// 限时礼包子表解析验收：合法字节逐字段回读（含 UTF-8 名字）；截断 / 尾部残留 / 时间倒置 /
    /// 折扣越界 / 限购为负 / id 重复 / 名字为空一律显式抛 <see cref="InvalidDataException"/>；
    /// 0 行是合法配置；表缺失抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public sealed class LimitedOfferTablesTests
    {
        private static readonly LimitedOfferRowSpec[] ThreeRows =
        {
            new LimitedOfferRowSpec(3001, "周年庆·限时礼包", 1001, 1, 1, 1280, 10, 1792540800, 1794873600, 1, 1),
            new LimitedOfferRowSpec(3002, "黑五·折扣礼包", 1002, 5, 1, 500, 50, 1794873600, 1795737600, 3, 99),
            new LimitedOfferRowSpec(3003, "每日特惠·强化石", 1004, 10, 1, 999, 33, 1794441600, 1796860800, 1, 60),
        };

        [Fact]
        public void Parse_ThreeRows_ExposesAllFieldsInRowOrder()
        {
            var tables = new LimitedOfferTables(LimitedOfferTableStub.WithRows(ThreeRows));

            Assert.Equal(3, tables.Count);

            LimitedOfferDef first = tables.Get(0);
            Assert.Equal(3001, first.OfferId);
            Assert.Equal("周年庆·限时礼包", first.Name);
            Assert.Equal(1001, first.ItemId);
            Assert.Equal(1, first.GrantCount);
            Assert.Equal(1, first.PriceItemId);
            Assert.Equal(1280, first.OriginalPrice);
            Assert.Equal(10, first.DiscountPercent);
            Assert.Equal(1792540800, first.StartUnixSeconds);
            Assert.Equal(1794873600, first.EndUnixSeconds);
            Assert.Equal(1, first.DailyLimit);
            Assert.Equal(1, first.TotalLimit);

            // UTF-8 多字节名字（含 2 字节的 '·'）必须按字节长度前缀正确回读。
            LimitedOfferDef second = tables.Get(1);
            Assert.Equal(3002, second.OfferId);
            Assert.Equal("黑五·折扣礼包", second.Name);
            Assert.Equal(5, second.GrantCount);
            Assert.Equal(50, second.DiscountPercent);
            Assert.Equal(99, second.TotalLimit);

            // 行顺序 = 配置表顺序：解析器不排序、不去重重排。
            LimitedOfferDef third = tables.Get(2);
            Assert.Equal(3003, third.OfferId);
            Assert.Equal("每日特惠·强化石", third.Name);
            Assert.Equal(999, third.OriginalPrice);
            Assert.Equal(33, third.DiscountPercent);
            Assert.Equal(60, third.TotalLimit);
        }

        [Fact]
        public void Parse_EmptyTable_IsAllowed()
        {
            var tables = new LimitedOfferTables(LimitedOfferTableStub.WithRows());

            Assert.Equal(0, tables.Count);
        }

        [Fact]
        public void Constructor_NullLoader_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new LimitedOfferTables(null!));
        }

        [Fact]
        public void Parse_MissingTable_ThrowsInvalidOperationException()
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithoutTable()));

            Assert.Contains(LimitedOfferTableStub.TableName, exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Get_OutOfRange_Throws()
        {
            var tables = new LimitedOfferTables(LimitedOfferTableStub.WithRows(ThreeRows));

            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => tables.Get(3));
        }

        [Fact]
        public void Parse_NegativeRowCount_Throws()
        {
            byte[] payload = { 0xFF, 0xFF, 0xFF, 0xFF };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload)));

            Assert.Contains("rowCount 为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_HeaderShorterThanFourBytes_Throws()
        {
            byte[] payload = { 1, 0 };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload)));

            Assert.Contains("头部长度不足", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DeclaredRowsExceedBytes_ThrowsTruncated()
        {
            // 头部声明 2 行，物理上只有 1 行多的字节：应在分配前按"行数 × 最小行宽"挡住。
            byte[] payload = LimitedOfferTableStub.BuildPayload(ThreeRows, declaredRowCount: 2);
            Array.Resize(ref payload, 4 + 30);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TruncatedMidRow_Throws()
        {
            // 声明 1 行且剩余字节数过了前置校验，但第 1 行读到一半就没了：字段级越界检查兜底。
            byte[] payload = LimitedOfferTableStub.BuildPayload(ThreeRows, declaredRowCount: 1);
            Array.Resize(ref payload, 4 + 44);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload)));

            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_TrailingBytes_Throws()
        {
            byte[] payload = LimitedOfferTableStub.AppendTrailingBytes(
                LimitedOfferTableStub.BuildPayload(ThreeRows), 4);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload)));

            Assert.Contains("尾部残留", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NameLengthNegative_Throws()
        {
            // [rowCount=1][offerId=3001][nameLen=-1] + 补齐到最小行宽（否则会先被"行数 × 行宽"前置校验挡住）。
            byte[] payload = LimitedOfferTableStub.BuildRaw(1, 3001, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload)));

            Assert.Contains("长度为负", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_NameDeclaredLongerThanPayload_Throws()
        {
            // [rowCount=1][offerId=3001][nameLen=999]：声明 999 字节名字，实际可读字节远不够。
            byte[] payload = LimitedOfferTableStub.BuildRaw(1, 3001, 999, 0, 0, 0, 0, 0, 0, 0, 0, 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload)));

            Assert.Contains("name", exception.Message, StringComparison.Ordinal);
            Assert.Contains("截断", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_EndNotAfterStart_Throws()
        {
            var sameSecond = new[]
            {
                new LimitedOfferRowSpec(3001, "零长度窗口", 1001, 1, 1, 100, 50, 100, 100, 1, 1),
            };

            InvalidDataException equal = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(sameSecond)));
            Assert.Contains("endUnixSeconds > startUnixSeconds", equal.Message, StringComparison.Ordinal);

            // 时间反向（结束早于开始）同样非法：窗口判定会整个失效。
            var reversed = new[]
            {
                new LimitedOfferRowSpec(3001, "时间倒置", 1001, 1, 1, 100, 50, 200, 100, 1, 1),
            };

            InvalidDataException backwards = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(reversed)));
            Assert.Contains("offerId 3001", backwards.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_DiscountOutOfRange_Throws()
        {
            var zero = new[]
            {
                new LimitedOfferRowSpec(3001, "零折扣", 1001, 1, 1, 100, 0, 100, 200, 1, 1),
            };

            InvalidDataException zeroDiscount = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(zero)));
            Assert.Contains("discountPercent", zeroDiscount.Message, StringComparison.Ordinal);

            // >100 是加价：同样必须在导表阶段红灯，而不是上线后靠客服退款。
            var above = new[]
            {
                new LimitedOfferRowSpec(3001, "加价折扣", 1001, 1, 1, 100, 101, 100, 200, 1, 1),
            };

            Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(above)));

            var negative = new[]
            {
                new LimitedOfferRowSpec(3001, "负折扣", 1001, 1, 1, 100, -10, 100, 200, 1, 1),
            };

            Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(negative)));
        }

        [Fact]
        public void Parse_NegativeLimit_Throws()
        {
            var dailyNegative = new[]
            {
                new LimitedOfferRowSpec(3001, "负日限", 1001, 1, 1, 100, 50, 100, 200, -1, 1),
            };

            InvalidDataException daily = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(dailyNegative)));
            Assert.Contains("dailyLimit", daily.Message, StringComparison.Ordinal);

            var totalNegative = new[]
            {
                new LimitedOfferRowSpec(3001, "负总限", 1001, 1, 1, 100, 50, 100, 200, 1, -1),
            };

            InvalidDataException total = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(totalNegative)));
            Assert.Contains("totalLimit", total.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_GrantCountNonPositive_Throws()
        {
            var zeroGrant = new[]
            {
                new LimitedOfferRowSpec(3001, "零发放", 1001, 0, 1, 100, 50, 100, 200, 1, 1),
            };

            InvalidDataException zero = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(zeroGrant)));
            Assert.Contains("grantCount", zero.Message, StringComparison.Ordinal);

            var negativeGrant = new[]
            {
                new LimitedOfferRowSpec(3001, "负发放", 1001, -5, 1, 100, 50, 100, 200, 1, 1),
            };

            Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(negativeGrant)));
        }

        [Fact]
        public void Parse_OriginalPriceNonPositive_Throws()
        {
            var zeroPrice = new[]
            {
                new LimitedOfferRowSpec(3001, "零原价", 1001, 1, 1, 0, 50, 100, 200, 1, 1),
            };

            InvalidDataException zero = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(zeroPrice)));
            Assert.Contains("originalPrice", zero.Message, StringComparison.Ordinal);

            var negativePrice = new[]
            {
                new LimitedOfferRowSpec(3001, "负原价", 1001, 1, 1, -100, 50, 100, 200, 1, 1),
            };

            Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(negativePrice)));
        }

        [Fact]
        public void Parse_ItemIdOrPriceItemIdNonPositive_Throws()
        {
            var zeroItem = new[]
            {
                new LimitedOfferRowSpec(3001, "零道具", 0, 1, 1, 100, 50, 100, 200, 1, 1),
            };

            InvalidDataException item = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(zeroItem)));
            Assert.Contains("itemId", item.Message, StringComparison.Ordinal);

            var zeroPriceItem = new[]
            {
                new LimitedOfferRowSpec(3001, "零货币", 1001, 1, 0, 100, 50, 100, 200, 1, 1),
            };

            InvalidDataException priceItem = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(zeroPriceItem)));
            Assert.Contains("priceItemId", priceItem.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_OfferIdNonPositive_Throws()
        {
            var negativeId = new[]
            {
                new LimitedOfferRowSpec(-7, "负 id", 1001, 1, 1, 100, 50, 100, 200, 1, 1),
            };

            InvalidDataException negative = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(negativeId)));
            Assert.Contains("offerId 必须为正", negative.Message, StringComparison.Ordinal);

            // 0 同样不可用：主键为 0 会让"限购计数"与事件 id 失去区分度。
            var zeroId = new[]
            {
                new LimitedOfferRowSpec(0, "零 id", 1001, 1, 1, 100, 50, 100, 200, 1, 1),
            };

            Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(zeroId)));
        }

        [Fact]
        public void Parse_DuplicateOfferId_Throws()
        {
            var duplicates = new[]
            {
                new LimitedOfferRowSpec(3001, "第一行", 1001, 1, 1, 100, 50, 100, 200, 1, 1),
                new LimitedOfferRowSpec(3001, "第二行", 1002, 1, 1, 100, 50, 300, 400, 1, 1),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(duplicates)));

            Assert.Contains("offerId 重复：3001", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Parse_EmptyName_Throws()
        {
            var emptyName = new[]
            {
                new LimitedOfferRowSpec(3001, string.Empty, 1001, 1, 1, 100, 50, 100, 200, 1, 1),
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => new LimitedOfferTables(LimitedOfferTableStub.WithRows(emptyName)));

            Assert.Contains("name 不能为空", exception.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 端到端守卫：直接解析仓库已导出的 <c>TbLimitedOffer.bin</c>，防止"tablegen 列顺序"与
        /// "切片解析器列顺序"各改各的。定长部分 4 字节表头 + 6 行 × 44 字节 + 名字 UTF-8 字节 123 = 391。
        /// </summary>
        [Fact]
        public void Parse_CommittedRepoBinary_MatchesSixRowContract()
        {
            string repoRoot = FindRepoRoot();
            string path = Path.Combine(
                repoRoot, "src", "Adapters", "AIFramework.Adapters.Console", "DemoTables", "TbLimitedOffer.bin");
            byte[] payload = File.ReadAllBytes(path);

            var tables = new LimitedOfferTables(LimitedOfferTableStub.WithPayload(payload));

            Assert.Equal(391, payload.Length);
            Assert.Equal(6, tables.Count);

            LimitedOfferDef first = tables.Get(0);
            Assert.Equal(3001, first.OfferId);
            Assert.Equal("周年庆·限时礼包", first.Name);
            Assert.Equal(1001, first.ItemId);
            Assert.Equal(1, first.GrantCount);
            Assert.Equal(1, first.PriceItemId);
            Assert.Equal(1280, first.OriginalPrice);
            Assert.Equal(10, first.DiscountPercent);
            Assert.Equal(1792540800, first.StartUnixSeconds);
            Assert.Equal(1794873600, first.EndUnixSeconds);
            Assert.Equal(1, first.DailyLimit);
            Assert.Equal(1, first.TotalLimit);

            LimitedOfferDef last = tables.Get(5);
            Assert.Equal(3006, last.OfferId);
            Assert.Equal("万圣·惊魂礼包", last.Name);
            Assert.Equal(1500, last.OriginalPrice);
            Assert.Equal(80, last.DiscountPercent);
            Assert.Equal(1791590400, last.StartUnixSeconds);
            Assert.Equal(1793491200, last.EndUnixSeconds);
            Assert.Equal(12, last.TotalLimit);

            // 六行必须覆盖"已结束 / 进行中 / 未开始"三态与折扣 10..90、日限 1..3、总限 1..99。
            int ended = 0;
            int open = 0;
            int future = 0;
            const int anchorUnix = 1795161600; // 2026-11-20T08:00:00Z
            for (int i = 0; i < tables.Count; i++)
            {
                LimitedOfferDef row = tables.Get(i);
                Assert.True(row.EndUnixSeconds > row.StartUnixSeconds, "offerId " + row.OfferId + " 时间窗口倒置。");
                Assert.InRange(row.DiscountPercent, 10, 90);
                Assert.InRange(row.DailyLimit, 1, 3);
                Assert.InRange(row.TotalLimit, 1, 99);

                if (anchorUnix >= row.EndUnixSeconds)
                {
                    ended++;
                }
                else if (anchorUnix < row.StartUnixSeconds)
                {
                    future++;
                }
                else
                {
                    open++;
                }
            }

            Assert.Equal(2, ended);
            Assert.Equal(3, open);
            Assert.Equal(1, future);
        }

        private static string FindRepoRoot([CallerFilePath] string callerFilePath = "")
        {
            // <repo>/tests/AIFramework.Features.Tests/LimitedOffer/LimitedOfferTablesTests.cs 向上三级
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
