#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;

namespace AIFramework.Core.Save
{
    /// <summary>
    /// 背包存档里的一行：<see cref="InventoryEntrySnapshot"/> 的业务字段 + "这一行最后变更时刻"。
    /// <para>
    /// <b>为什么在 Core 另立类型，而不是给 <see cref="InventoryEntrySnapshot"/> 加字段</b>：
    /// 那是给 UI/切片读的只读视图，多背一个存档专用时间戳会污染契约层；
    /// 存档格式的演进只允许改存档格式自己的类型。
    /// </para>
    /// </summary>
    public readonly struct InventorySaveEntry
    {
        /// <summary>构造。</summary>
        /// <param name="itemId">道具 ID（必须为正）。</param>
        /// <param name="count">数量（必须为正）。</param>
        /// <param name="updatedTicks">最后更新时刻；<b>0 表示未知</b>（v1 历史数据迁移后就是这个值）。</param>
        public InventorySaveEntry(int itemId, int count, long updatedTicks)
        {
            ItemId = itemId;
            Count = count;
            UpdatedTicks = updatedTicks;
        }

        /// <summary>道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>数量。</summary>
        public int Count { get; }

        /// <summary>
        /// 该行最后更新时刻。<b>单位由调用方约定</b>（例如 UTC 毫秒），存档层只负责原样搬运；
        /// <c>0</c> 专表示"未知"，绝不用当前时刻兜底——否则云存档冲突判定会把最老的档当成最新档。
        /// </summary>
        public long UpdatedTicks { get; }
    }

    /// <summary>
    /// 背包存档编解码：把 <see cref="InventoryEntrySnapshot"/> 行列表压成二进制（小端）。
    /// <para>
    /// <b>两代行布局</b>：
    /// </para>
    /// <list type="bullet">
    /// <item>v1（<see cref="FormatVersion1"/>）：<c>[int32 count] + count × { int32 itemId; int32 count }</c>，每行 8 字节。</item>
    /// <item>v2（<see cref="FormatVersion2"/>）：<c>[int32 count] + count × { int32 itemId; int32 count; int64 updatedTicks }</c>，每行 16 字节。</item>
    /// </list>
    /// <para>
    /// <b>为什么负载里不带版本前缀</b>：版本号由 <see cref="SaveService"/> 包络统一持有，负载再写一份就会出现
    /// "包络说 v2、负载说 v1"的双源不一致；不带前缀还能让 v1 旧档按字节原样继续读。
    /// </para>
    /// <para>
    /// <b>为什么放在 Core 而不是适配层</b>：Console 与 Unity 都要存背包，格式一旦分叉就会出现
    /// "命令行存档 Unity 读不了"。格式只有一处定义，两边共用。
    /// </para>
    /// <para>
    /// 解码是"防御式"的：任何字段非法（数量为负、长度不符、count 超过上限、时间戳为负）都返回 false，
    /// 由调用方决定"当作新档"还是报错——坏档绝不能变成负数背包。
    /// </para>
    /// </summary>
    public static class InventorySaveCodec
    {
        /// <summary>条目数上限：防止坏档声明的超大 count 导致巨额分配。</summary>
        public const int MaxEntries = 4096;

        /// <summary>v1 格式版本号。</summary>
        public const int FormatVersion1 = 1;

        /// <summary>v2 格式版本号（每行多一个 int64 时间戳）。</summary>
        public const int FormatVersion2 = 2;

        /// <summary>v1 单条目字节数（itemId + count）。</summary>
        public const int EntrySizeV1 = 8;

        /// <summary>v2 单条目字节数（itemId + count + updatedTicks）。</summary>
        public const int EntrySizeV2 = 16;

        /// <summary>单条目字节数（历史遗留名，等价于 <see cref="EntrySizeV1"/>，保留只为不打断旧调用点）。</summary>
        public const int EntrySize = EntrySizeV1;

        /// <summary>
        /// 按 v1 编码。顺序由调用方决定（<c>InventoryHandler.Snapshot()</c> 已按 itemId 升序）；
        /// <b>重复 itemId 会原样写成两行</b>（本方法不做去重/合并），解码同样原样返回两行。
        /// </summary>
        /// <param name="entries">条目列表。</param>
        /// <returns>负载字节。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="entries"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException">条目数超过 <see cref="MaxEntries"/>。</exception>
        public static byte[] Encode(IReadOnlyList<InventoryEntrySnapshot> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (entries.Count > MaxEntries)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), entries.Count, "背包条目数超过上限。");
            }

            var payload = new byte[4 + entries.Count * EntrySizeV1];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                int offset = 4 + i * EntrySizeV1;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), entries[i].ItemId);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4, 4), entries[i].Count);
            }

            return payload;
        }

        /// <summary>
        /// 按 v2 编码（每行 16 字节，尾部 8 字节是 <see cref="InventorySaveEntry.UpdatedTicks"/> 小端 int64）。
        /// <para>
        /// itemId / count 的宽松程度与 <see cref="Encode"/> 一致（不额外校验），但 <b>负时间戳直接拒绝</b>：
        /// 那是 v2 才引入的字段，写出去连自己都读不回来，属于编码器必须当场拦住的错误。
        /// </para>
        /// </summary>
        /// <param name="entries">条目列表（顺序由调用方决定，重复 itemId 原样保留）。</param>
        /// <returns>负载字节。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="entries"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException">条目数超过 <see cref="MaxEntries"/>，或某行 <c>UpdatedTicks &lt; 0</c>。</exception>
        public static byte[] EncodeV2(IReadOnlyList<InventorySaveEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (entries.Count > MaxEntries)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), entries.Count, "背包条目数超过上限。");
            }

            var payload = new byte[4 + entries.Count * EntrySizeV2];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                InventorySaveEntry entry = entries[i];
                if (entry.UpdatedTicks < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(entries), entry.UpdatedTicks, "updatedTicks 不能为负（0 表示未知）。");
                }

                int offset = 4 + i * EntrySizeV2;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), entry.ItemId);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4, 4), entry.Count);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + 8, 8), entry.UpdatedTicks);
            }

            return payload;
        }

        /// <summary>
        /// 按<b>显式版本</b>解码；版本不受支持或长度与声明 count 不符则返回 false。
        /// </summary>
        /// <param name="payload">负载字节。</param>
        /// <param name="formatVersion">期望的格式版本（<see cref="FormatVersion1"/> 或 <see cref="FormatVersion2"/>）。</param>
        /// <param name="entries">解出的条目；v1 解出的 <see cref="InventorySaveEntry.UpdatedTicks"/> 恒为 0。</param>
        /// <returns>是否解码成功。</returns>
        public static bool TryDecode(byte[] payload, int formatVersion, out InventorySaveEntry[] entries)
        {
            entries = Array.Empty<InventorySaveEntry>();

            if (payload == null || payload.Length < 4)
            {
                return false;
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
            if (count < 0 || count > MaxEntries)
            {
                return false;
            }

            switch (formatVersion)
            {
                case FormatVersion1:
                    return TryDecodeRows(payload, count, EntrySizeV1, out entries);

                case FormatVersion2:
                    return TryDecodeRows(payload, count, EntrySizeV2, out entries);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 不带版本前缀的自动识别：按"声明 count 推出的应有长度"判断是 v1（<c>4 + 8n</c>）还是 v2（<c>4 + 16n</c>）。
        /// <para>
        /// <b>歧义边界只有 count == 0</b>：两种布局在空背包时都是 4 字节，此时两者解出的都是空数组，
        /// 按约定优先按 v1 解析，结果无差别；count &gt; 0 时 <c>4 + 8n</c> 与 <c>4 + 16n</c> 不可能同时成立，
        /// 所以"长度同时匹配"不可能把 v2 数据误判成 v1 而读出半截数据。
        /// </para>
        /// </summary>
        /// <param name="payload">负载字节。</param>
        /// <param name="entries">解出的条目（v1 的 <see cref="InventorySaveEntry.UpdatedTicks"/> 为 0）。</param>
        /// <returns>是否解码成功。</returns>
        public static bool TryDecodeAuto(byte[] payload, out InventorySaveEntry[] entries)
        {
            entries = Array.Empty<InventorySaveEntry>();

            if (payload == null || payload.Length < 4)
            {
                return false;
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
            if (count < 0 || count > MaxEntries)
            {
                return false;
            }

            // count == 0 时两个长度都是 4：优先 v1（见上方注释，两者等价）。
            if (payload.Length == 4 + count * EntrySizeV1)
            {
                return TryDecode(payload, FormatVersion1, out entries);
            }

            if (payload.Length == 4 + count * EntrySizeV2)
            {
                return TryDecode(payload, FormatVersion2, out entries);
            }

            return false;
        }

        /// <summary>
        /// 兼容旧调用点的解码：<b>自动识别 v1/v2</b>，但丢弃 v2 的 <see cref="InventorySaveEntry.UpdatedTicks"/>
        /// （旧调用点如 <c>CloudSaveMerger</c> / 大厅存档只关心 itemId 与 count）。
        /// <para>
        /// 为什么要自动识别而不是固定 v1：<see cref="SaveService"/> 读档时会就地迁移旧档，调用方拿到的
        /// payload 可能已经是 v2；本重载若只认 v1，旧调用点会在"迁移成功"之后反而解不出来。
        /// </para>
        /// </summary>
        /// <param name="payload">负载字节。</param>
        /// <param name="entries">解出的条目；失败时为空数组。</param>
        /// <returns>是否解码成功。</returns>
        public static bool TryDecode(byte[] payload, out InventoryEntrySnapshot[] entries)
        {
            entries = Array.Empty<InventoryEntrySnapshot>();

            if (!TryDecodeAuto(payload, out InventorySaveEntry[] decoded))
            {
                return false;
            }

            var result = new InventoryEntrySnapshot[decoded.Length];
            for (int i = 0; i < decoded.Length; i++)
            {
                result[i] = new InventoryEntrySnapshot(decoded[i].ItemId, decoded[i].Count);
            }

            entries = result;
            return true;
        }

        private static bool TryDecodeRows(byte[] payload, int count, int entrySize, out InventorySaveEntry[] entries)
        {
            entries = Array.Empty<InventorySaveEntry>();

            if (payload.Length != 4 + count * entrySize)
            {
                return false;
            }

            var result = new InventorySaveEntry[count];
            for (int i = 0; i < count; i++)
            {
                int offset = 4 + i * entrySize;
                int itemId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
                int value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + 4, 4));

                if (itemId <= 0 || value <= 0)
                {
                    return false;
                }

                long updatedTicks = 0;
                if (entrySize == EntrySizeV2)
                {
                    updatedTicks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset + 8, 8));

                    // v2 才有的字段：负值无意义（0 已表示"未知"），说明字节被写坏或伪造。
                    if (updatedTicks < 0)
                    {
                        return false;
                    }
                }

                result[i] = new InventorySaveEntry(itemId, value, updatedTicks);
            }

            entries = result;
            return true;
        }
    }
}
