using System;
using System.IO;
using AIFramework.Features.DailySignIn;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.DailySignIn
{
    /// <summary>
    /// 子表加载校验（<c>Doc/03 §5.4</c>）：表缺失、长度不符、行数不符都必须显式抛异常，
    /// 禁止静默按空表继续。
    /// </summary>
    public sealed class SignInTablesTests
    {
        /// <summary>夹具起点：2020-03-01 12:00 UTC。</summary>
        private static readonly DateTime StartUtc = new DateTime(2020, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void MissingTable_ThrowsInvalidOperationException()
        {
            FakeTableLoader loader = FakeTableLoader.WithoutTables();

            InvalidOperationException fromTables = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new SignInTables(loader);
                });
            Assert.Contains(FakeTableLoader.SignInRewardTableName, fromTables.Message);

            InvalidOperationException fromHandler = Assert.Throws<InvalidOperationException>(
                () =>
                {
                    new SignInHandler(new RecordingBus(), new FakeTimeProvider(StartUtc), loader);
                });
            Assert.Contains(FakeTableLoader.SignInRewardTableName, fromHandler.Message);

            // 两次构造都按名字取同一张子表，没有退化成"随便找一张表"。
            Assert.Equal(2, loader.RequestedTables.Count);
            Assert.All(loader.RequestedTables, name => Assert.Equal(FakeTableLoader.SignInRewardTableName, name));
        }

        [Fact]
        public void LengthMismatch_ThrowsInvalidDataException()
        {
            // 头部声明 7 行，实际只写 6 行：长度 4 + 72 = 76，不等于 4 + 7 * 12。
            byte[] payload = FakeTableLoader.BuildSignInRewardPayloadDeclaringRows(7, 6);
            FakeTableLoader loader = FakeTableLoader.WithoutTables()
                .AddTable(FakeTableLoader.SignInRewardTableName, payload);

            InvalidDataException fromTables = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new SignInTables(loader);
                });
            Assert.Contains("长度", fromTables.Message);

            InvalidDataException fromHandler = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new SignInHandler(new RecordingBus(), new FakeTimeProvider(StartUtc), loader);
                });
            Assert.Contains("长度", fromHandler.Message);
        }

        [Fact]
        public void RowCountNotSeven_ThrowsInvalidDataException()
        {
            // 长度自洽（4 + 3 * 12 = 40），只是行数不是 7。
            byte[] payload = FakeTableLoader.BuildSignInRewardPayload(3);
            FakeTableLoader loader = FakeTableLoader.WithoutTables()
                .AddTable(FakeTableLoader.SignInRewardTableName, payload);

            InvalidDataException fromTables = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new SignInTables(loader);
                });
            Assert.Contains("行数", fromTables.Message);

            Assert.Throws<InvalidDataException>(
                () =>
                {
                    new SignInHandler(new RecordingBus(), new FakeTimeProvider(StartUtc), loader);
                });
        }

        [Fact]
        public void Get_MapsDayIndexToRowAndRejectsOutOfRange()
        {
            SignInTables tables = new SignInTables(FakeTableLoader.WithDefaultSignInReward());

            Assert.Equal(FakeTableLoader.DayCount, tables.DayCount);
            Assert.Equal(FakeTableLoader.DayItemId(1), tables.Get(1).ItemId);
            Assert.Equal(FakeTableLoader.DayItemCount(1), tables.Get(1).ItemCount);
            Assert.Equal(1, tables.Get(1).DayIndex);
            Assert.Equal(FakeTableLoader.DayItemId(7), tables.Get(7).ItemId);
            Assert.Equal(7, tables.Get(7).DayIndex);

            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(0);
                });
            Assert.Throws<ArgumentOutOfRangeException>(
                () =>
                {
                    tables.Get(8);
                });
        }

        [Fact]
        public void DuplicateDayIndex_ThrowsInvalidDataException()
        {
            byte[] payload = FakeTableLoader.BuildSignInRewardPayload(7);

            // 把第 7 行的 dayIndex 改回 1，制造重复：长度仍自洽，行数仍是 7。
            payload[4 + (6 * 12)] = 1;
            payload[4 + (6 * 12) + 1] = 0;
            payload[4 + (6 * 12) + 2] = 0;
            payload[4 + (6 * 12) + 3] = 0;

            FakeTableLoader loader = FakeTableLoader.WithoutTables()
                .AddTable(FakeTableLoader.SignInRewardTableName, payload);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () =>
                {
                    new SignInTables(loader);
                });
            Assert.Contains("重复", exception.Message);
        }
    }
}
