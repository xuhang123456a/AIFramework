using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using AIFramework.Contracts.Meta;

namespace AIFramework.Core.Save
{
    /// <summary>
    /// 背包存档编解码：把 <see cref="InventoryEntrySnapshot"/> 列表压成
    /// <c>[int32 count] + count × { int32 itemId; int32 count }</c>（小端）。
    /// <para>
    /// <b>为什么放在 Core 而不是适配层</b>：Console 与 Unity 都要存背包，格式一旦分叉就会出现
    /// "命令行存档 Unity 读不了"。格式只有一处定义，两边共用。
    /// </para>
    /// <para>
    /// 解码是"防御式"的：任何字段非法（数量为负、长度不符、count 超过上限）都返回 false，
    /// 由调用方决定"当作新档"还是报错——坏档绝不能变成负数背包。
    /// </para>
    /// </summary>
    public static class InventorySaveCodec
    {
        /// <summary>条目数上限：防止坏档声明的超大 count 导致巨额分配。</summary>
        public const int MaxEntries = 4096;

        /// <summary>单条目字节数（itemId + count）。</summary>
        public const int EntrySize = 8;

        /// <summary>编码。顺序由调用方决定（<c>InventoryHandler.Snapshot()</c> 已按 itemId 升序）。</summary>
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

            var payload = new byte[4 + entries.Count * EntrySize];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                int offset = 4 + i * EntrySize;
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), entries[i].ItemId);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4, 4), entries[i].Count);
            }

            return payload;
        }

        /// <summary>解码。坏档返回 false 且 <paramref name="entries"/> 为空数组。</summary>
        /// <param name="payload">负载字节。</param>
        /// <param name="entries">解出的条目（升序与否取决于写入方）。</param>
        public static bool TryDecode(byte[] payload, out InventoryEntrySnapshot[] entries)
        {
            entries = Array.Empty<InventoryEntrySnapshot>();

            if (payload == null || payload.Length < 4)
            {
                return false;
            }

            int count = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
            if (count < 0 || count > MaxEntries)
            {
                return false;
            }

            if (payload.Length != 4 + count * EntrySize)
            {
                return false;
            }

            var result = new InventoryEntrySnapshot[count];
            for (int i = 0; i < count; i++)
            {
                int offset = 4 + i * EntrySize;
                int itemId = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, 4));
                int value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + 4, 4));

                if (itemId <= 0 || value <= 0)
                {
                    entries = Array.Empty<InventoryEntrySnapshot>();
                    return false;
                }

                result[i] = new InventoryEntrySnapshot(itemId, value);
            }

            entries = result;
            return true;
        }
    }
}
