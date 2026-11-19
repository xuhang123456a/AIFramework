#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Merge
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbMergeItem</c>）：等级 → 道具 ID / 金币售价的完整等级链。
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——空表和"表没打进包"是两件事。
    /// 等级必须从 1 连续到 <see cref="Count"/>：合成是"下一级"语义，缺一级就会产出无配置行的等级，
    /// 因此不连续与重复同样视为非法配置，而不是运行时再兜底。
    /// </para>
    /// </summary>
    public sealed class MergeTables
    {
        private const string TableNameValue = "TbMergeItem";
        private const int HeaderBytes = 4;
        private const int RowBytes = 12;

        private readonly MergeItemDef[] _rows;

        public MergeTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("合成子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数；0 行是合法配置（空等级链），此时任何生成/合成都被拒绝。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>最高等级：等级连续 1..Count，故等于 <see cref="Count"/>（空表为 0）。</summary>
        public int MaxLevel
        {
            get { return _rows.Length; }
        }

        /// <summary>按物理行序取第 <paramref name="index"/> 行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public MergeItemDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "合成行下标必须落在 0.." + (_rows.Length - 1) + " 之间。");
            }

            return _rows[index];
        }

        /// <summary>
        /// 按等级取行：合成奖励以"合成后等级"为键，不能用行序猜。
        /// 表中没有该等级抛 <see cref="ArgumentOutOfRangeException"/>（合法配置下 1..MaxLevel 必然齐全）。
        /// </summary>
        public MergeItemDef GetByLevel(int level)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].Level == level)
                {
                    return _rows[i];
                }
            }

            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "合成子表中没有等级 " + level + " 的行。");
        }

        private static MergeItemDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("合成子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (rowCount < 0 || payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "合成子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            MergeItemDef[] rows = new MergeItemDef[rowCount];
            HashSet<int> seenLevels = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int level = ReadInt32LittleEndian(payload, offset);
                int itemId = ReadInt32LittleEndian(payload, offset + 4);
                int sellGold = ReadInt32LittleEndian(payload, offset + 8);

                if (level <= 0)
                {
                    throw new InvalidDataException("合成子表 level 必须为正：第 " + i + " 行的 level 为 " + level + "。");
                }

                if (itemId <= 0)
                {
                    throw new InvalidDataException("合成子表 itemId 必须为正：level " + level + " 的 itemId 为 " + itemId + "。");
                }

                if (!seenLevels.Add(level))
                {
                    throw new InvalidDataException("合成子表 level 重复：" + level + "。");
                }

                if (level > rowCount)
                {
                    // 行数就是等级上限：level 超出行数时 1..Count 必然有空洞，直接判不连续。
                    throw new InvalidDataException(
                        "合成子表 level 必须从 1 连续到 Count(" + rowCount + ")，出现了 level " + level + "。");
                }

                rows[i] = new MergeItemDef(level, itemId, sellGold);
            }

            for (int level = 1; level <= rowCount; level++)
            {
                if (!seenLevels.Contains(level))
                {
                    throw new InvalidDataException(
                        "合成子表 level 必须从 1 连续到 Count(" + rowCount + ")，缺少 level " + level + "。");
                }
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
