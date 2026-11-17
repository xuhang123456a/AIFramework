using AIFramework.Contracts.Ports;
using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// Phase 1 内存配置表加载器。按 <c>Doc/03</c> §5.4 的夹具格式产出字节：
    /// <c>TbSignInReward</c> = <c>[int32 rowCount]</c> + <c>rowCount × { int32 day; int32 itemId; int32 itemCount; }</c>（小端）。
    /// Phase 2 接入 Luban <c>cs-bin</c> 后本类整体替换，切片与测试不动。
    /// </summary>
    public sealed class MockBinaryTableLoader : IBinaryTableLoader
    {
        public const string SignInRewardTable = "TbSignInReward";

        private readonly Dictionary<string, byte[]> _tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public MockBinaryTableLoader()
        {
            _tables[SignInRewardTable] = BuildSignInRewardTable(7);
        }

        public byte[]? Load(string tableName)
        {
            byte[]? bytes;
            return _tables.TryGetValue(tableName, out bytes) ? bytes : null;
        }

        /// <summary>覆盖某张表的字节，测试可注入非法数据验证切片校验逻辑。</summary>
        public void Override(string tableName, byte[] bytes)
        {
            _tables[tableName] = bytes;
        }

        private static byte[] BuildSignInRewardTable(int dayCount)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(dayCount);
                    for (int day = 1; day <= dayCount; day++)
                    {
                        writer.Write(day);
                        writer.Write(1000 + day);
                        writer.Write(day * 10);
                    }

                    writer.Flush();
                }

                return stream.ToArray();
            }
        }
    }
}
