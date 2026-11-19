#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Tycoon
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbTycoonBuilding</c>）。
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——空表和"表没打进包"是两件事。
    /// 布局：小端 <c>[int32 rowCount]</c> + <c>rowCount × { int32 buildingId; int32 kind; int32 buildCost; int32 baseIncome; int32 maxLevel; }</c>，
    /// 单行 20 字节。
    /// </summary>
    public sealed class TycoonTables
    {
        private const string TableNameValue = "TbTycoonBuilding";

        private const int HeaderBytes = 4;
        private const int RowBytes = 20;

        /// <summary><see cref="TycoonBuildingKind"/> 的合法取值上界（含），越界即数据错误。</summary>
        private const int MaxKindValue = (int)TycoonBuildingKind.Restaurant;

        private readonly TycoonBuildingDef[] _rows;

        public TycoonTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("模拟经营子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数；0 行是合法配置（空建筑表，什么都建不了），不是加载失败。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按行序取第 <paramref name="index"/> 行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public TycoonBuildingDef Get(int index)
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

        /// <summary>按 buildingId 线性查找配置行；子表已保证 ID 唯一，找不到返回 <c>false</c>。</summary>
        public bool TryFind(int buildingId, out TycoonBuildingDef def)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].BuildingId == buildingId)
                {
                    def = _rows[i];
                    return true;
                }
            }

            def = default;
            return false;
        }

        private static TycoonBuildingDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("模拟经营子表头部长度不足 4 字节，实际 " + payload.Length + " 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            if (rowCount < 0)
            {
                throw new InvalidDataException("模拟经营子表声明行数为负数：" + rowCount + "。");
            }

            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "模拟经营子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            TycoonBuildingDef[] rows = new TycoonBuildingDef[rowCount];
            HashSet<int> seenBuildingIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int buildingId = ReadInt32LittleEndian(payload, offset);
                int kindValue = ReadInt32LittleEndian(payload, offset + 4);
                int buildCost = ReadInt32LittleEndian(payload, offset + 8);
                int baseIncome = ReadInt32LittleEndian(payload, offset + 12);
                int maxLevel = ReadInt32LittleEndian(payload, offset + 16);

                if (!seenBuildingIds.Add(buildingId))
                {
                    throw new InvalidDataException("模拟经营子表 buildingId 重复：" + buildingId + "。");
                }

                if (kindValue < 0 || kindValue > MaxKindValue)
                {
                    throw new InvalidDataException(
                        "模拟经营子表 kind 越界（合法 0.." + MaxKindValue + "），buildingId " + buildingId + " 实际为 " + kindValue + "。");
                }

                if (buildCost < 0)
                {
                    throw new InvalidDataException(
                        "模拟经营子表 buildCost 不能为负，buildingId " + buildingId + " 实际为 " + buildCost + "。");
                }

                if (baseIncome < 0)
                {
                    throw new InvalidDataException(
                        "模拟经营子表 baseIncome 不能为负，buildingId " + buildingId + " 实际为 " + baseIncome + "。");
                }

                if (maxLevel < 1)
                {
                    throw new InvalidDataException(
                        "模拟经营子表 maxLevel 必须大于等于 1，buildingId " + buildingId + " 实际为 " + maxLevel + "。");
                }

                rows[i] = new TycoonBuildingDef(buildingId, (TycoonBuildingKind)kindValue, buildCost, baseIncome, maxLevel);
            }

            return rows;
        }

        private static int ReadInt32LittleEndian(byte[] payload, int offset)
        {
            return payload[offset]
                | (payload[offset + 1] << 8)
                | (payload[offset + 2] << 16)
                | (payload[offset + 3] << 24);
        }
    }
}
