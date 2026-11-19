#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.TowerDefense
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbTower</c>）。
    /// <para>
    /// <b>缺表为什么必须抛异常而不是按空表继续</b>："表没打进包"和"配了一张空塔表"是两件事：
    /// 前者是部署事故，必须让装配层在日志里看到并关掉本切片；后者是合法配置（塔全部下架）。
    /// 静默退化成空表只会表现为"点哪都建不出塔"，排查成本远高于一次显式失败。
    /// </para>
    /// </summary>
    public sealed class TowerTables
    {
        /// <summary>子表名（与 Luban 导出的表名一致，也是测试与夹具的唯一约定点）。</summary>
        public const string TableNameValue = "TbTower";

        private const int HeaderBytes = 4;
        private const int RowBytes = 16;

        private readonly TowerDef[] _rows;

        public TowerTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("塔防子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数；0 行是合法配置（无塔可建），不是加载失败。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按行序取第 <paramref name="index"/> 行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public TowerDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "塔行下标必须落在 0.." + (_rows.Length - 1) + " 之间。");
            }

            return _rows[index];
        }

        /// <summary>
        /// 按塔 ID 取行；找不到抛 <see cref="ArgumentOutOfRangeException"/>。
        /// <b>调用方若需要"查不到就静默拒绝"，必须自己先 TryFind</b>——本方法不提供静默分支，
        /// 免得把"配置缺失"和"玩家点了未知塔"混成同一条路径。
        /// </summary>
        public TowerDef GetById(int towerId)
        {
            TowerDef def;
            if (!TryFind(towerId, out def))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(towerId),
                    towerId,
                    "塔 ID 在子表 '" + TableNameValue + "' 中不存在。");
            }

            return def;
        }

        /// <summary>按塔 ID 线性查找；子表已保证 ID 唯一，找不到返回 <c>false</c> 且 <paramref name="def"/> 为 <c>default</c>。</summary>
        public bool TryFind(int towerId, out TowerDef def)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].TowerId == towerId)
                {
                    def = _rows[i];
                    return true;
                }
            }

            def = default;
            return false;
        }

        private static TowerDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("塔防子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (rowCount < 0 || payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "塔防子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            TowerDef[] rows = new TowerDef[rowCount];
            HashSet<int> seenTowerIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int towerId = ReadInt32LittleEndian(payload, offset);
                int cost = ReadInt32LittleEndian(payload, offset + 4);
                int damage = ReadInt32LittleEndian(payload, offset + 8);
                int range = ReadInt32LittleEndian(payload, offset + 12);

                if (!seenTowerIds.Add(towerId))
                {
                    throw new InvalidDataException("塔防子表 towerId 重复：" + towerId + "。");
                }

                // 一条配置非法就整表失败：只丢掉一行会让"表里写了但建不出来"变成不可解释的行为。
                if (cost < 0)
                {
                    throw new InvalidDataException("塔防子表 towerId=" + towerId + " 的 cost 为负数：" + cost + "。");
                }

                if (damage <= 0)
                {
                    throw new InvalidDataException("塔防子表 towerId=" + towerId + " 的 damage 必须大于 0，实际 " + damage + "。");
                }

                if (range <= 0)
                {
                    throw new InvalidDataException("塔防子表 towerId=" + towerId + " 的 range 必须大于 0，实际 " + range + "。");
                }

                rows[i] = new TowerDef(towerId, cost, damage, range);
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
