#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.IdleTycoon
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbBuilding</c>）。
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——空表和"表没打进包"是两件事。
    /// 布局（Phase 1 夹具）：小端 <c>[int32 rowCount]</c> + <c>rowCount × { int32 buildingId; int32 baseIncome; int32 upgradeCost; int32 maxLevel; }</c>，单行 16 字节。
    /// </summary>
    public sealed class IdleTables
    {
        private const string TableNameValue = "TbBuilding";
        private const int HeaderBytes = 4;
        private const int RowBytes = 16;

        private readonly BuildingDef[] _rows;

        public IdleTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("放置经营子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数；0 行是合法配置（空建筑表），不是加载失败。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按行序取第 <paramref name="index"/> 行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public BuildingDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "建筑行下标必须落在 0.." + (_rows.Length - 1) + " 之间。");
            }

            return _rows[index];
        }

        private static BuildingDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("放置经营子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "放置经营子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            BuildingDef[] rows = new BuildingDef[rowCount];
            HashSet<int> seenBuildingIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int buildingId = ReadInt32LittleEndian(payload, offset);
                int baseIncome = ReadInt32LittleEndian(payload, offset + 4);
                int upgradeCost = ReadInt32LittleEndian(payload, offset + 8);
                int maxLevel = ReadInt32LittleEndian(payload, offset + 12);

                if (!seenBuildingIds.Add(buildingId))
                {
                    throw new InvalidDataException("放置经营子表 buildingId 重复：" + buildingId + "。");
                }

                if (baseIncome <= 0)
                {
                    throw new InvalidDataException(
                        "放置经营子表 baseIncome 必须大于 0，buildingId " + buildingId + " 实际为 " + baseIncome + "。");
                }

                if (upgradeCost < 0)
                {
                    throw new InvalidDataException(
                        "放置经营子表 upgradeCost 不能为负，buildingId " + buildingId + " 实际为 " + upgradeCost + "。");
                }

                if (maxLevel <= 0)
                {
                    throw new InvalidDataException(
                        "放置经营子表 maxLevel 必须大于 0，buildingId " + buildingId + " 实际为 " + maxLevel + "。");
                }

                rows[i] = new BuildingDef(buildingId, baseIncome, upgradeCost, maxLevel);
            }

            return rows;
        }

        private static int ReadInt32LittleEndian(byte[] payload, int offset)
        {
            int value = payload[offset]
                | (payload[offset + 1] << 8)
                | (payload[offset + 2] << 16)
                | (payload[offset + 3] << 24);
            return value;
        }
    }
}
