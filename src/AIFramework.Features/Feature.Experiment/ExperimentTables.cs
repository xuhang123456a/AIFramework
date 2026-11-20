#nullable enable
using System;
using System.IO;
using System.Text;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Experiment
{
    /// <summary>
    /// 实验配置行：纯数据，对应 <c>TbExperiment</c> 的一行。
    /// <para>
    /// <see cref="TrafficPercent"/> 是"进入 B 组的百分比"（0..100），其余玩家留在 A 组：
    /// 0 = 全量 A（灰度未放量的对照组），100 = 全量 B（放量完成），中间值按确定性哈希切分。
    /// </para>
    /// <para>
    /// <see cref="Enabled"/> 由表里的 <c>enabled</c>（只允许 0/1）解析而来：禁用实验不参与分桶、
    /// 不产生曝光，业务侧读到的是 <c>variantA</c>。
    /// </para>
    /// </summary>
    public readonly struct ExperimentDef
    {
        public ExperimentDef(
            int experimentId,
            string key,
            int variantA,
            int variantB,
            int trafficPercent,
            bool enabled)
        {
            ExperimentId = experimentId;
            Key = key;
            VariantA = variantA;
            VariantB = variantB;
            TrafficPercent = trafficPercent;
            Enabled = enabled;
        }

        /// <summary>实验 id（配置主键，恒为正，表内唯一）。</summary>
        public int ExperimentId { get; }

        /// <summary>实验键（非空，表内唯一）：确定性分桶只哈希它，改 key 等于重洗分组，必须当配置变更评审。</summary>
        public string Key { get; }

        /// <summary>A 组（对照组）档位取值。</summary>
        public int VariantA { get; }

        /// <summary>B 组（实验组）档位取值。</summary>
        public int VariantB { get; }

        /// <summary>进入 B 组的百分比（0..100）；<c>bucket &lt; TrafficPercent</c> 即落在 B 组。</summary>
        public int TrafficPercent { get; }

        /// <summary>是否启用（表里 <c>enabled</c> 只允许 0/1）。</summary>
        public bool Enabled { get; }
    }

    /// <summary>
    /// 本切片专属子表入口（<c>TbExperiment</c>）。
    /// <para>
    /// 布局（小端，由 <c>tools/tablegen</c> 按 JSON 列顺序生成）：
    /// <c>[int32 rowCount]</c> + rowCount ×
    /// { <c>int32 experimentId</c>; <c>[int32 keyLen]</c> + UTF-8 key;
    /// <c>int32 variantA</c>; <c>int32 variantB</c>; <c>int32 trafficPercent</c>; <c>int32 enabled</c> }。
    /// </para>
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——"表没打进包"必须当场暴露；
    /// 而 0 行是合法配置（<see cref="Count"/> == 0），切片此时不曝光任何实验、也不发事件。
    /// 列顺序即解析顺序，行顺序即快照与曝光事件顺序（不排序、不按 id 重排）。
    /// </para>
    /// </summary>
    public sealed class ExperimentTables
    {
        /// <summary>子表名（与 <c>tables/TbExperiment.json</c> 的 <c>table</c> 字段一致）。</summary>
        private const string TableNameValue = "TbExperiment";

        /// <summary>头部：<c>[int32 rowCount]</c>。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单个 int32 字段宽度（小端）。</summary>
        private const int Int32Bytes = 4;

        /// <summary>
        /// 一行至少可读的字节数：<c>experimentId</c>(4) + key 长度前缀(4) + key 体(0..) +
        /// <c>variantA/variantB/trafficPercent/enabled</c>(16) = 24。
        /// key 体按 0 字节算，好让"key 为空"走到专门的空 key 报错，而不是被截断前置检查抢先挡掉。
        /// </summary>
        private const int MinRowBytes = Int32Bytes * 6;

        /// <summary>B 组流量下界（0 = 全 A）。</summary>
        private const int MinTrafficPercent = 0;

        /// <summary>B 组流量上界（100 = 全 B）。</summary>
        private const int MaxTrafficPercent = 100;

        private readonly ExperimentDef[] _rows;

        /// <summary>加载并解析 <c>TbExperiment</c>；表缺失抛 <see cref="InvalidOperationException"/>，数据非法抛 <see cref="InvalidDataException"/>。</summary>
        /// <param name="loader">子表加载端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">加载器返回 <c>null</c>，即子表缺失。</exception>
        /// <exception cref="InvalidDataException">截断、尾部残留、行数非法、id 非正或重复、key 为空或重复、流量越界、enabled 非 0/1。</exception>
        public ExperimentTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("实验子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数（实验条数）；0 是合法配置。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按下标取行（行顺序 = 配置表行顺序）；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        /// <param name="index">0 起行下标。</param>
        public ExperimentDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "实验子表下标越界，当前行数 " + _rows.Length + "。");
            }

            return _rows[index];
        }

        private static ExperimentDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("实验子表头部长度不足 " + HeaderBytes + " 字节。");
            }

            PayloadReader reader = new PayloadReader(payload);
            int rowCount = reader.ReadInt32("rowCount");
            if (rowCount < 0)
            {
                throw new InvalidDataException("实验子表 rowCount 为负：" + rowCount + "。");
            }

            // 先把"声明行数 × 单行最小字节"和剩余字节比一遍，避免畸形头部触发超大分配。
            if ((long)rowCount * MinRowBytes > reader.Remaining)
            {
                throw new InvalidDataException(
                    "实验子表声明 " + rowCount + " 行，但只剩 " + reader.Remaining + " 字节，字节流被截断。");
            }

            ExperimentDef[] rows = new ExperimentDef[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                int experimentId = reader.ReadInt32("experimentId");
                string key = reader.ReadUtf8("key");
                int variantA = reader.ReadInt32("variantA");
                int variantB = reader.ReadInt32("variantB");
                int trafficPercent = reader.ReadInt32("trafficPercent");
                int enabled = reader.ReadInt32("enabled");

                if (experimentId <= 0)
                {
                    throw new InvalidDataException(
                        "实验子表 experimentId 必须为正，第 " + i + " 行实际为 " + experimentId + "。");
                }

                for (int k = 0; k < i; k++)
                {
                    if (rows[k].ExperimentId == experimentId)
                    {
                        // 主键重复会让"快照分配"与"曝光事件"无法一一对应：必须当成配置错误挡掉。
                        throw new InvalidDataException("实验子表 experimentId 重复：" + experimentId + "。");
                    }
                }

                if (key.Length == 0)
                {
                    throw new InvalidDataException(
                        "实验子表 key 不能为空，experimentId " + experimentId + "。");
                }

                for (int k = 0; k < i; k++)
                {
                    if (string.Equals(rows[k].Key, key, StringComparison.Ordinal))
                    {
                        // key 是分桶输入：两行同 key 会让两个实验被同一批玩家同时命中（隐含耦合），
                        // 也会让运营侧看板无法区分是哪一个实验的 key，必须拒绝。
                        throw new InvalidDataException("实验子表 key 重复：" + key + "。");
                    }
                }

                if (trafficPercent < MinTrafficPercent || trafficPercent > MaxTrafficPercent)
                {
                    throw new InvalidDataException(
                        "实验子表 trafficPercent 必须在 " + MinTrafficPercent + ".." + MaxTrafficPercent
                        + "，experimentId " + experimentId + " 实际为 " + trafficPercent + "。");
                }

                if (enabled != 0 && enabled != 1)
                {
                    throw new InvalidDataException(
                        "实验子表 enabled 只能是 0/1，experimentId " + experimentId + " 实际为 " + enabled + "。");
                }

                rows[i] = new ExperimentDef(
                    experimentId,
                    key,
                    variantA,
                    variantB,
                    trafficPercent,
                    enabled == 1);
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException(
                    "实验子表尾部残留 " + reader.Remaining + " 字节，声明行数与字节流长度不符。");
            }

            return rows;
        }

        /// <summary>
        /// 小端字节流游标：每次读取都做越界检查，越界统一转 <see cref="InvalidDataException"/>。
        /// 只在构造期使用（允许分配），因此不做零分配优化。
        /// </summary>
        private sealed class PayloadReader
        {
            private readonly byte[] _payload;
            private int _offset;

            internal PayloadReader(byte[] payload)
            {
                _payload = payload;
                _offset = 0;
            }

            /// <summary>尚未读取的字节数。</summary>
            internal int Remaining
            {
                get { return _payload.Length - _offset; }
            }

            /// <summary>读一个小端 int32；不足 4 字节即判定截断。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal int ReadInt32(string fieldName)
            {
                if (Remaining < Int32Bytes)
                {
                    throw new InvalidDataException(
                        "实验子表读取 " + fieldName + " 时字节流已截断：偏移 " + _offset
                        + "，剩余 " + Remaining + " 字节。");
                }

                int value = _payload[_offset]
                    | (_payload[_offset + 1] << 8)
                    | (_payload[_offset + 2] << 16)
                    | (_payload[_offset + 3] << 24);
                _offset += Int32Bytes;
                return value;
            }

            /// <summary>读"长度前缀 + UTF-8 字节"的 string；负长度或超出剩余字节即判定非法。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal string ReadUtf8(string fieldName)
            {
                int length = ReadInt32(fieldName + "Len");
                if (length < 0)
                {
                    throw new InvalidDataException(
                        "实验子表 " + fieldName + " 长度为负：" + length + "。");
                }

                if (length > Remaining)
                {
                    throw new InvalidDataException(
                        "实验子表读取 " + fieldName + " 时字节流已截断：声明 " + length
                        + " 字节，剩余 " + Remaining + " 字节。");
                }

                string text = Encoding.UTF8.GetString(_payload, _offset, length);
                _offset += length;
                return text;
            }
        }
    }
}
