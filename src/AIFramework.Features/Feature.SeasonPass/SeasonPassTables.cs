#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.SeasonPass
{
    /// <summary>通行证子表行：纯数据，对应 <c>TbSeasonPass</c> 的一行。</summary>
    public readonly struct SeasonPassDef
    {
        public SeasonPassDef(
            int level,
            int requiredXp,
            int freeItemId,
            int freeCount,
            int paidItemId,
            int paidCount)
        {
            Level = level;
            RequiredXp = requiredXp;
            FreeItemId = freeItemId;
            FreeCount = freeCount;
            PaidItemId = paidItemId;
            PaidCount = paidCount;
        }

        /// <summary>等级（1 基，子表内连续升序）。</summary>
        public int Level { get; }

        /// <summary>达到该等级所需的**累计总经验**（不是升级增量）；1 级恒为 0，逐级严格递增。</summary>
        public int RequiredXp { get; }

        /// <summary>免费轨道具 ID，恒为正。</summary>
        public int FreeItemId { get; }

        /// <summary>免费轨发放数量，恒为正。</summary>
        public int FreeCount { get; }

        /// <summary>付费轨道具 ID；0 表示该等级没有付费奖励。</summary>
        public int PaidItemId { get; }

        /// <summary>付费轨发放数量；无付费奖励时为 0。</summary>
        public int PaidCount { get; }
    }

    /// <summary>
    /// 本切片专属子表入口（<c>TbSeasonPass</c>）。
    /// <para>
    /// 布局（小端，由 <c>tools/tablegen</c> 按 JSON 列顺序生成，全数值定长）：
    /// <c>[int32 rowCount]</c> + rowCount ×
    /// { <c>int32 level</c>; <c>int32 requiredXp</c>; <c>int32 freeItemId</c>; <c>int32 freeCount</c>;
    /// <c>int32 paidItemId</c>; <c>int32 paidCount</c> }，单行 24 字节。
    /// </para>
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——"表没打进包"必须当场暴露；
    /// 而 0 行是合法配置（<see cref="Count"/> == 0），查询返回退化快照而不是崩溃。
    /// 列顺序即解析顺序，<c>level</c> 必须从 1 起连续递增：等级直接由行号决定，handler 不做二次排序。
    /// </para>
    /// </summary>
    public sealed class SeasonPassTables
    {
        /// <summary>子表名（与 <c>tables/TbSeasonPass.json</c> 的 <c>table</c> 字段一致）。</summary>
        private const string TableNameValue = "TbSeasonPass";

        /// <summary>头部：<c>[int32 rowCount]</c>。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单个 int32 字段宽度（小端）。</summary>
        private const int FieldBytes = 4;

        /// <summary>单行字段数：level / requiredXp / freeItemId / freeCount / paidItemId / paidCount。</summary>
        private const int FieldsPerRow = 6;

        /// <summary>单行定长字节数（全 int32，24 字节）。</summary>
        private const int RowBytes = FieldBytes * FieldsPerRow;

        private readonly SeasonPassDef[] _rows;

        /// <summary>加载并解析 <c>TbSeasonPass</c>；表缺失抛 <see cref="InvalidOperationException"/>，数据非法抛 <see cref="InvalidDataException"/>。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">加载器返回 <c>null</c>，即子表缺失。</exception>
        /// <exception cref="InvalidDataException">截断、尾部残留、行数非法、等级不连续、经验门槛不递增、奖励列非法。</exception>
        public SeasonPassTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("通行证子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数（等级数）；0 是合法配置。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按下标取行（下标即 <c>level - 1</c>）；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public SeasonPassDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "通行证子表下标越界，当前行数 " + _rows.Length + "。");
            }

            return _rows[index];
        }

        private static SeasonPassDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("通行证子表头部长度不足 " + HeaderBytes + " 字节。");
            }

            int rowCount = ReadInt32(payload, 0);
            if (rowCount < 0)
            {
                throw new InvalidDataException("通行证子表 rowCount 为负：" + rowCount + "。");
            }

            // 定长表：先按"声明行数 × 单行字节"精确校验总长，截断/残留都能在分配前挡住。
            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length < expectedLength)
            {
                throw new InvalidDataException(
                    "通行证子表声明 " + rowCount + " 行，期望 " + expectedLength + " 字节，实际 "
                    + payload.Length + " 字节，字节流被截断。");
            }

            if (payload.Length > expectedLength)
            {
                throw new InvalidDataException(
                    "通行证子表尾部残留 " + (payload.Length - expectedLength)
                    + " 字节，声明行数与字节流长度不符。");
            }

            SeasonPassDef[] rows = new SeasonPassDef[rowCount];
            int previousRequiredXp = 0;
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int level = ReadInt32(payload, offset);
                int requiredXp = ReadInt32(payload, offset + FieldBytes);
                int freeItemId = ReadInt32(payload, offset + (FieldBytes * 2));
                int freeCount = ReadInt32(payload, offset + (FieldBytes * 3));
                int paidItemId = ReadInt32(payload, offset + (FieldBytes * 4));
                int paidCount = ReadInt32(payload, offset + (FieldBytes * 5));

                // level 必须从 1 起连续：等级 = 行号 + 1，领取/升级都按这个映射直接寻址。
                int expectedLevel = i + 1;
                if (level != expectedLevel)
                {
                    throw new InvalidDataException(
                        "通行证子表 level 必须从 1 起连续递增，第 " + i + " 行期望 " + expectedLevel
                        + "，实际 " + level + "。");
                }

                if (i == 0)
                {
                    if (requiredXp != 0)
                    {
                        throw new InvalidDataException("通行证子表 1 级 requiredXp 必须为 0，实际为 " + requiredXp + "。");
                    }
                }
                else if (requiredXp <= previousRequiredXp)
                {
                    throw new InvalidDataException(
                        "通行证子表 requiredXp 必须逐级严格递增，level " + level + " 实际为 " + requiredXp
                        + "，上一级为 " + previousRequiredXp + "。");
                }

                if (freeItemId <= 0)
                {
                    throw new InvalidDataException(
                        "通行证子表 freeItemId 必须为正，level " + level + " 实际为 " + freeItemId + "。");
                }

                if (freeCount <= 0)
                {
                    throw new InvalidDataException(
                        "通行证子表 freeCount 必须为正，level " + level + " 实际为 " + freeCount + "。");
                }

                if (paidItemId < 0)
                {
                    throw new InvalidDataException(
                        "通行证子表 paidItemId 不能为负，level " + level + " 实际为 " + paidItemId + "。");
                }

                if (paidCount < 0)
                {
                    throw new InvalidDataException(
                        "通行证子表 paidCount 不能为负，level " + level + " 实际为 " + paidCount + "。");
                }

                // "无付费奖励"必须整对置 0：留半个配置（有 ID 没数量 / 有数量没 ID）一律当配置错误挡掉。
                if (paidItemId == 0 && paidCount != 0)
                {
                    throw new InvalidDataException(
                        "通行证子表 level " + level + " 的 paidItemId 为 0（无付费奖励）时 paidCount 必须为 0，实际为 "
                        + paidCount + "。");
                }

                if (paidItemId > 0 && paidCount == 0)
                {
                    throw new InvalidDataException(
                        "通行证子表 level " + level + " 声明了付费奖励 paidItemId " + paidItemId
                        + " 但 paidCount 为 0。");
                }

                rows[i] = new SeasonPassDef(level, requiredXp, freeItemId, freeCount, paidItemId, paidCount);
                previousRequiredXp = requiredXp;
            }

            return rows;
        }

        private static int ReadInt32(byte[] payload, int offset)
        {
            return payload[offset]
                | (payload[offset + 1] << 8)
                | (payload[offset + 2] << 16)
                | (payload[offset + 3] << 24);
        }
    }
}
