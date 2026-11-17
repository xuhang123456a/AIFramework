using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>
    /// 二进制表加载桩：按 <c>Doc/03 §5.4</c> 的 Phase 1 夹具格式造字节。
    /// 布局：<c>[int32 rowCount]</c> + <c>rowCount × { int32 dayIndex; int32 itemId; int32 itemCount; }</c>，全部小端，单行 12 字节。
    /// </summary>
    public sealed class FakeTableLoader : IBinaryTableLoader
    {
        /// <summary>签到奖励子表的表名（与切片内常量一致）。</summary>
        public const string SignInRewardTableName = "TbSignInReward";

        /// <summary>夹具固定 7 日周期长度。</summary>
        public const int DayCount = 7;

        private const int RowHeaderBytes = 4;
        private const int RowBytes = 12;

        private readonly Dictionary<string, byte[]?> _tables;
        private readonly List<string> _requestedTables;

        public FakeTableLoader()
        {
            _tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            _requestedTables = new List<string>();
        }

        /// <summary>一次 <see cref="Load"/> 都没成表的加载器：任何表都返回 null（表缺失场景）。</summary>
        public static FakeTableLoader WithoutTables()
        {
            return new FakeTableLoader();
        }

        /// <summary>标准夹具：7 行，第 n 天发 <see cref="DayItemId"/> / <see cref="DayItemCount"/>。</summary>
        public static FakeTableLoader WithDefaultSignInReward()
        {
            return new FakeTableLoader().AddTable(SignInRewardTableName, BuildSignInRewardPayload(DayCount));
        }

        /// <summary>夹具第 <paramref name="day"/> 天（1..7）发放的道具 ID。</summary>
        public static int DayItemId(int day)
        {
            return 8100 + day;
        }

        /// <summary>夹具第 <paramref name="day"/> 天（1..7）发放的数量。</summary>
        public static int DayItemCount(int day)
        {
            return 10 * day;
        }

        /// <summary>
        /// 造一份 <paramref name="rowCount"/> 行的合法字节：头部声明行数与实际行数一致。
        /// </summary>
        public static byte[] BuildSignInRewardPayload(int rowCount)
        {
            return BuildSignInRewardPayloadDeclaringRows(rowCount, rowCount);
        }

        /// <summary>
        /// 造一份"头部声明行数"与"实际写入行数"不一致的字节，用于触发长度校验失败。
        /// </summary>
        public static byte[] BuildSignInRewardPayloadDeclaringRows(int declaredRowCount, int physicalRowCount)
        {
            if (declaredRowCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(declaredRowCount), declaredRowCount, "行数不能为负。");
            }

            if (physicalRowCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(physicalRowCount), physicalRowCount, "行数不能为负。");
            }

            byte[] payload = new byte[RowHeaderBytes + (physicalRowCount * RowBytes)];
            WriteInt32LittleEndian(payload, 0, declaredRowCount);

            for (int i = 0; i < physicalRowCount; i++)
            {
                int day = i + 1;
                int offset = RowHeaderBytes + (i * RowBytes);
                WriteInt32LittleEndian(payload, offset, day);
                WriteInt32LittleEndian(payload, offset + 4, DayItemId(day));
                WriteInt32LittleEndian(payload, offset + 8, DayItemCount(day));
            }

            return payload;
        }

        /// <summary>登记（或覆盖）一张表；<paramref name="payload"/> 传 null 表示该表缺失。</summary>
        public FakeTableLoader AddTable(string tableName, byte[]? payload)
        {
            if (tableName == null)
            {
                throw new ArgumentNullException(nameof(tableName));
            }

            _tables[tableName] = payload;
            return this;
        }

        /// <summary>被请求过的表名（按调用顺序），用于断言切片确实按名字取子表。</summary>
        public IReadOnlyList<string> RequestedTables
        {
            get { return _requestedTables; }
        }

        /// <inheritdoc />
        public byte[]? Load(string tableName)
        {
            _requestedTables.Add(tableName);

            byte[]? payload;
            if (_tables.TryGetValue(tableName, out payload))
            {
                return payload;
            }

            return null;
        }

        private static void WriteInt32LittleEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
            target[offset + 2] = (byte)((value >> 16) & 0xFF);
            target[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
