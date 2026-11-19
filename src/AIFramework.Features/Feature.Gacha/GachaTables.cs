#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Gacha
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbGachaPool</c>）。
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——空池子和"表没打进包"是两件事，
    /// 且没有稀有项时保底规则根本无法成立，因此空表同样视为非法配置。
    /// </summary>
    public sealed class GachaTables
    {
        private const string TableNameValue = "TbGachaPool";
        private const int HeaderBytes = 4;
        private const int RowBytes = 12;

        private readonly GachaEntryDef[] _rows;
        private readonly int _totalWeight;

        public GachaTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("抽卡池子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload, out _totalWeight);
        }

        /// <summary>子表行数；至少为 1（空表因"没有稀有项"被判为非法配置）。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>全部行的整数权重之和，抽卡时随机区间上界即此值。</summary>
        public int TotalWeight
        {
            get { return _totalWeight; }
        }

        /// <summary>按行序取第 <paramref name="index"/> 行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public GachaEntryDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "抽卡池行下标必须落在 0.." + (_rows.Length - 1) + " 之间。");
            }

            return _rows[index];
        }

        private static GachaEntryDef[] Parse(byte[] payload, out int totalWeight)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("抽卡池子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (rowCount < 0 || payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "抽卡池子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            GachaEntryDef[] rows = new GachaEntryDef[rowCount];
            HashSet<int> seenItemIds = new HashSet<int>();
            long weightSum = 0;
            bool hasRare = false;
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int itemId = ReadInt32LittleEndian(payload, offset);
                int weight = ReadInt32LittleEndian(payload, offset + 4);
                int isRareRaw = ReadInt32LittleEndian(payload, offset + 8);

                if (!seenItemIds.Add(itemId))
                {
                    throw new InvalidDataException("抽卡池子表 itemId 重复：" + itemId + "。");
                }

                if (weight <= 0)
                {
                    throw new InvalidDataException("抽卡池子表 weight 必须为正：itemId " + itemId + " 的 weight 为 " + weight + "。");
                }

                if (isRareRaw != 0 && isRareRaw != 1)
                {
                    throw new InvalidDataException("抽卡池子表 isRare 只能为 0/1：itemId " + itemId + " 的 isRare 为 " + isRareRaw + "。");
                }

                bool isRare = isRareRaw == 1;
                hasRare |= isRare;
                weightSum += weight;
                rows[i] = new GachaEntryDef(itemId, weight, isRare);
            }

            if (weightSum > int.MaxValue)
            {
                throw new InvalidDataException("抽卡池子表权重和 " + weightSum + " 超出 int 上限 " + int.MaxValue + "。");
            }

            if (!hasRare)
            {
                throw new InvalidDataException("抽卡池子表没有任何 isRare = 1 的稀有项，保底规则无法成立。");
            }

            totalWeight = (int)weightSum;
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
