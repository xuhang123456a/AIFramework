using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Experiment;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.Experiment
{
    /// <summary>一条实验行的夹具定义（字段顺序与 <c>tables/TbExperiment.json</c> 列顺序一致）。</summary>
    internal readonly struct ExperimentRowSpec
    {
        internal ExperimentRowSpec(
            int experimentId,
            string key,
            int variantA,
            int variantB,
            int trafficPercent,
            int enabled)
        {
            ExperimentId = experimentId;
            Key = key;
            VariantA = variantA;
            VariantB = variantB;
            TrafficPercent = trafficPercent;
            Enabled = enabled;
        }

        internal int ExperimentId { get; }

        internal string Key { get; }

        internal int VariantA { get; }

        internal int VariantB { get; }

        internal int TrafficPercent { get; }

        internal int Enabled { get; }
    }

    /// <summary>
    /// 实验子表加载桩：严格按 <c>tools/tablegen</c> 的布局造字节
    /// （小端 <c>[int32 rowCount]</c> + 每行 <c>{ int32 experimentId; [int32 len]+UTF-8 key; int32 × 4 }</c>），
    /// 只在本测试目录内使用，不改共享 <c>Fakes/</c>。
    /// </summary>
    internal sealed class ExperimentTableStub : IBinaryTableLoader
    {
        internal const string TableName = "TbExperiment";

        private const int HeaderBytes = 4;

        private readonly Dictionary<string, byte[]?> _tables;

        private ExperimentTableStub(Dictionary<string, byte[]?> tables)
        {
            _tables = tables;
        }

        /// <summary>一次都没成表的加载器：任何表都返回 <c>null</c>（表缺失场景）。</summary>
        internal static ExperimentTableStub WithoutTable()
        {
            return new ExperimentTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        }

        /// <summary>按给定行顺序造一张合法子表。</summary>
        internal static ExperimentTableStub WithRows(params ExperimentRowSpec[] rows)
        {
            return WithPayload(BuildPayload(rows));
        }

        /// <summary>直接挂一份字节（可用于空表 / 畸形字节 / 仓库真实 <c>.bin</c>）。</summary>
        internal static ExperimentTableStub WithPayload(byte[]? payload)
        {
            var tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            tables[TableName] = payload;
            return new ExperimentTableStub(tables);
        }

        internal static byte[] BuildPayload(ExperimentRowSpec[] rows)
        {
            return BuildPayload(rows, rows.Length);
        }

        /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于长度/截断反例）。</summary>
        internal static byte[] BuildPayload(ExperimentRowSpec[] rows, int declaredRowCount)
        {
            var bytes = new List<byte>(HeaderBytes + (rows.Length * 48));
            AppendInt32(bytes, declaredRowCount);
            for (int i = 0; i < rows.Length; i++)
            {
                ExperimentRowSpec row = rows[i];
                AppendInt32(bytes, row.ExperimentId);
                byte[] key = Encoding.UTF8.GetBytes(row.Key);
                AppendInt32(bytes, key.Length);
                bytes.AddRange(key);
                AppendInt32(bytes, row.VariantA);
                AppendInt32(bytes, row.VariantB);
                AppendInt32(bytes, row.TrafficPercent);
                AppendInt32(bytes, row.Enabled);
            }

            return bytes.ToArray();
        }

        /// <summary>只写一串小端 int32（不写字符串体），用于手工构造"长度前缀本身畸形"的字节流。</summary>
        internal static byte[] BuildRaw(params int[] values)
        {
            var bytes = new List<byte>(values.Length * 4);
            for (int i = 0; i < values.Length; i++)
            {
                AppendInt32(bytes, values[i]);
            }

            return bytes.ToArray();
        }

        /// <summary>在合法字节尾部追加垃圾字节（模拟"声明行数与字节流不符"的尾部残留）。</summary>
        internal static byte[] AppendTrailingBytes(byte[] payload, int count)
        {
            var bytes = new List<byte>(payload.Length + count);
            bytes.AddRange(payload);
            for (int i = 0; i < count; i++)
            {
                bytes.Add(0x7F);
            }

            return bytes.ToArray();
        }

        public byte[]? Load(string tableName)
        {
            byte[]? payload;
            return _tables.TryGetValue(tableName, out payload) ? payload : null;
        }

        private static void AppendInt32(List<byte> target, int value)
        {
            target.Add((byte)(value & 0xFF));
            target.Add((byte)((value >> 8) & 0xFF));
            target.Add((byte)((value >> 16) & 0xFF));
            target.Add((byte)((value >> 24) & 0xFF));
        }
    }

    /// <summary>
    /// 装配：记录型总线（<see cref="RecordingBus"/>）+ 切片 <see cref="ExperimentHandler"/>。
    /// 分桶只认配置 key 与玩家 id，因此夹具完全不需要时间源（AIF003 无涉）。
    /// </summary>
    internal sealed class ExperimentHarness
    {
        private ExperimentHarness(RecordingBus bus, ExperimentHandler handler)
        {
            Bus = bus;
            Handler = handler;
        }

        internal RecordingBus Bus { get; }

        internal ExperimentHandler Handler { get; }

        internal static ExperimentHarness Create(ExperimentTableStub loader)
        {
            var bus = new RecordingBus();
            var handler = new ExperimentHandler(bus, loader);
            handler.Register(bus);
            return new ExperimentHarness(bus, handler);
        }

        /// <summary>只发 evaluate，不查询（用于断言"事件只发一次"这类只看副作用的场景）。</summary>
        internal void SendEvaluate(int playerId)
        {
            Bus.Send(new ExperimentEvaluateCommand(playerId));
        }

        /// <summary>先 evaluate 再查询，返回 evaluate 之后的快照。</summary>
        internal ExperimentSnapshot Evaluate(int playerId)
        {
            SendEvaluate(playerId);
            return Query();
        }

        internal ExperimentSnapshot Query()
        {
            return Bus.Query<ExperimentQueryRequest, ExperimentSnapshot>(new ExperimentQueryRequest());
        }

        internal List<ExperimentExposedEvent> Exposures()
        {
            return Bus.PublishedOf<ExperimentExposedEvent>();
        }

        /// <summary>按实验 id 取快照里的分配；找不到直接抛（说明快照漏行）。</summary>
        internal static ExperimentAssignment AssignmentOf(ExperimentSnapshot snapshot, int experimentId)
        {
            for (int i = 0; i < snapshot.Assignments.Count; i++)
            {
                if (snapshot.Assignments[i].ExperimentId == experimentId)
                {
                    return snapshot.Assignments[i];
                }
            }

            throw new InvalidOperationException("快照里没有 experimentId " + experimentId + "。");
        }
    }
}
