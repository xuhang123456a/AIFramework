#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbShopItem</c>）。
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——空表和"表没打进包"是两件事。
    /// </summary>
    public sealed class ShopTables
    {
        private const string TableNameValue = "TbShopItem";
        private const int HeaderBytes = 4;
        private const int RowBytes = 12;

        private readonly ShopItemDef[] _rows;

        public ShopTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("商店子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数；0 行是合法配置（空商店），不是加载失败。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按行序取第 <paramref name="index"/> 行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public ShopItemDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "商品行下标必须落在 0.." + (_rows.Length - 1) + " 之间。");
            }

            return _rows[index];
        }

        private static ShopItemDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("商店子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "商店子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            ShopItemDef[] rows = new ShopItemDef[rowCount];
            HashSet<int> seenItemIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int itemId = ReadInt32LittleEndian(payload, offset);
                int priceItemId = ReadInt32LittleEndian(payload, offset + 4);
                int priceCount = ReadInt32LittleEndian(payload, offset + 8);

                if (!seenItemIds.Add(itemId))
                {
                    throw new InvalidDataException("商店子表 itemId 重复：" + itemId + "。");
                }

                rows[i] = new ShopItemDef(itemId, priceItemId, priceCount);
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
