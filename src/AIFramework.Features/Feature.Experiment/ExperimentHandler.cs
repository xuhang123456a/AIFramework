#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Experiment
{
    /// <summary>
    /// 确定性分桶：把 <c>key + ":" + playerId</c> 压成一个 0..99 的桶号。
    /// <para>
    /// <b>为什么必须是 FNV-1a32 而不是 <c>string.GetHashCode()</c></b>：
    /// .NET Core 起字符串哈希默认开启随机化种子（每进程不同，可开关），同一个玩家
    /// 今天 A 组、明天重启变 B 组，实验数据直接作废。这里用固定参数（偏移基 2166136261 /
    /// 质数 16777619）逐字节推进的 FNV-1a32，输入相同则结果在任何进程、任何机器、任何
    /// 运行时版本上都完全一致；实现里也不允许出现 <c>Random</c>、时间或环境变量。
    /// </para>
    /// <para>
    /// <b>编码口径（必须与运营侧独立实现逐字节对齐）</b>：把 <c>key</c> 按 UTF-8 编码逐字节混入，
    /// 再混入一个 ASCII 冒号 <c>':'</c>（0x3A），最后混入 <see cref="int"/> 的十进制文本
    /// （<see cref="CultureInfo.InvariantCulture"/>，负数带 <c>'-'</c>，逐字符按 ASCII 低字节混入）。
    /// 等价于对字符串 <c>key + ":" + playerId</c> 的 UTF-8 字节序列做标准 FNV-1a32——
    /// 只是不真的把字符串拼出来。
    /// </para>
    /// <para>
    /// 每次调用会产生 1~2 个短命分配（key 的 UTF-8 缓冲、playerId 的十进制文本）：
    /// 只在该玩家第一次 evaluate 时跑一遍，不在帧热路径上。
    /// </para>
    /// </summary>
    public static class ExperimentBucketing
    {
        /// <summary>FNV-1a 32 位偏移基（标准值 2166136261）。</summary>
        public const uint OffsetBasis = 2166136261u;

        /// <summary>FNV-1a 32 位质数（标准值 16777619）。</summary>
        public const uint Prime = 16777619u;

        /// <summary>桶数量：<c>hash % 100</c> 得到 0..99，正好对上 <c>trafficPercent</c> 的百分数刻度。</summary>
        public const int BucketCount = 100;

        /// <summary>分隔 key 与 playerId 的 ASCII 冒号 <c>':'</c>（0x3A）。</summary>
        private const uint ColonByte = 0x3Au;

        /// <summary>
        /// 对 <c>key + ":" + playerId</c> 求 FNV-1a32。
        /// </summary>
        /// <param name="key">实验键（非空；UTF-8 编码后逐字节参与哈希）。</param>
        /// <param name="playerId">玩家 id（十进制文本，InvariantCulture）。</param>
        /// <returns>32 位哈希值。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 <c>null</c>。</exception>
        public static uint Hash(string key, int playerId)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            uint hash = OffsetBasis;

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            for (int i = 0; i < keyBytes.Length; i++)
            {
                hash = unchecked((hash ^ keyBytes[i]) * Prime);
            }

            hash = unchecked((hash ^ ColonByte) * Prime);

            // 十进制文本只含 '0'..'9' 与 '-'，全是 ASCII：直接取低字节即可，无需再编码一遍。
            string digits = playerId.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < digits.Length; i++)
            {
                hash = unchecked((hash ^ digits[i]) * Prime);
            }

            return hash;
        }

        /// <summary>
        /// 桶号（0..99）：<c>Hash(key, playerId) % 100</c>。
        /// </summary>
        /// <param name="key">实验键。</param>
        /// <param name="playerId">玩家 id。</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 <c>null</c>。</exception>
        public static int Bucket(string key, int playerId)
        {
            return (int)(Hash(key, playerId) % (uint)BucketCount);
        }

        /// <summary>
        /// 是否落在 B 组：<c>Bucket(key, playerId) &lt; trafficPercent</c>。
        /// <para>
        /// 边界天然正确：<paramref name="trafficPercent"/> 为 0 时任何桶号都不小于 0（全 A），
        /// 为 100 时 0..99 全部小于 100（全 B），无需特判。
        /// </para>
        /// </summary>
        /// <param name="key">实验键。</param>
        /// <param name="playerId">玩家 id。</param>
        /// <param name="trafficPercent">进入 B 组的百分比（0..100）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="trafficPercent"/> 不在 0..100。</exception>
        public static bool IsB(string key, int playerId, int trafficPercent)
        {
            if (trafficPercent < 0 || trafficPercent > BucketCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(trafficPercent),
                    trafficPercent,
                    "trafficPercent 必须在 0.." + BucketCount + "。");
            }

            return Bucket(key, playerId) < trafficPercent;
        }
    }

    /// <summary>
    /// 表驱动 A/B 实验机：唯一允许裁定"这个玩家在每个实验里落在哪一组"的地方。
    /// <para>
    /// <b>只做三件事</b>：按 <c>TbExperiment</c> 做确定性分桶 → 按 <c>(experimentId, playerId)</c> 去重广播曝光 →
    /// 提供全量只读快照。业务功能是否真的按 B 组生效由使用方自己 <c>if</c>：本切片不控制开关、
    /// 不发奖、不落存档、不做统计聚合，也不碰服务端下发。
    /// </para>
    /// <para>
    /// <b>确定性</b>：分桶只用配置里的 <c>key</c> 与玩家 id（见 <see cref="ExperimentBucketing"/>），
    /// 不含时间、随机数、进程状态；同一份表 + 同一个玩家在任何环境下结果一致。
    /// </para>
    /// <para>
    /// <b>曝光口径</b>：只有 <c>enabled=1</c> 的实验参与分桶并曝光；禁用实验在快照里以
    /// <c>Enabled=false</c> + <c>Variant=variantA</c> 出现，且永不曝光。曝光去重不靠"记住上一个玩家"，
    /// 而是按 <c>(experimentId, playerId)</c> 累加，因此重复 evaluate 同一玩家不会重复计数，
    /// 中途换玩家也不会给旧玩家再补发一次。
    /// </para>
    /// <para>
    /// <b>顺序</b>：evaluate 先整体落定分配与计数，再按配置表行顺序广播曝光事件——
    /// 订阅方在事件回调里查询到的一定是最终态。
    /// </para>
    /// </summary>
    public sealed class ExperimentHandler
        : ICommandHandler<ExperimentEvaluateCommand>,
          IQueryHandler<ExperimentQueryRequest, ExperimentSnapshot>
    {
        /// <summary>尚未 evaluate 时返回的空分配（静态只读，避免每次查询都新建空数组）。</summary>
        private static readonly ExperimentAssignment[] EmptyAssignments = new ExperimentAssignment[0];

        private readonly ICqeBus _bus;
        private readonly ExperimentTables _tables;

        /// <summary>已曝光过的 <c>(playerId, experimentId)</c> 对：曝光去重的唯一依据。</summary>
        private readonly HashSet<long> _exposedPairs;

        /// <summary>最近一次 evaluate 的分配结果（未 evaluate 时为空数组）；整体替换，从不原地改写。</summary>
        private ExperimentAssignment[] _assignments;

        /// <summary>累计曝光条数（按 pair 去重，单调不减）。</summary>
        private int _exposedCount;

        /// <summary>加载子表并初始化空分配；构造里不 evaluate、不注册总线、不发事件。</summary>
        /// <param name="bus">切片消息总线（只用于广播曝光事实）。</param>
        /// <param name="loader">子表加载端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">实验子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">实验子表数据非法。</exception>
        public ExperimentHandler(ICqeBus bus, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _tables = new ExperimentTables(loader);
            _exposedPairs = new HashSet<long>();
            _assignments = EmptyAssignments;
            _exposedCount = 0;
        }

        /// <summary>实验条数（= 子表行数，含禁用行）。</summary>
        public int ExperimentCount
        {
            get { return _tables.Count; }
        }

        /// <summary>累计曝光条数（与 <see cref="ExperimentSnapshot.ExposedCount"/> 同源）。</summary>
        public int ExposedCount
        {
            get { return _exposedCount; }
        }

        /// <summary>
        /// 注册 evaluate 命令与全量查询处理器。
        /// 装配入口必须传入构造本对象时的同一个总线实例（广播用的是构造注入的总线）。
        /// </summary>
        /// <param name="bus">切片消息总线。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<ExperimentEvaluateCommand>(this);
            bus.RegisterQueryHandler<ExperimentQueryRequest, ExperimentSnapshot>(this);
        }

        /// <summary>
        /// 按玩家 id 做一次确定性分桶并记录曝光（幂等）：
        /// <list type="number">
        /// <item>逐行按配置表顺序算分配：禁用行 → <c>variantA</c>、<c>IsB=false</c>；
        /// 启用行 → <c>Bucket(key, playerId) &lt; trafficPercent</c> 决定落在 B（<c>variantB</c>）还是 A（<c>variantA</c>）；</item>
        /// <item>分配结果整体落定后，再按行顺序对"从未曝光过的 <c>(experimentId, playerId)</c> 对"广播
        /// <see cref="ExperimentExposedEvent"/> 并累加曝光计数；</item>
        /// <item>同一玩家重复调用：分配结果不变、不广播任何事件、<c>ExposedCount</c> 不变。</item>
        /// </list>
        /// <para>空表：退化为"无实验"，快照为空、不广播事件、不抛异常。</para>
        /// </summary>
        public void Handle(in ExperimentEvaluateCommand command)
        {
            int playerId = command.PlayerId;
            int count = _tables.Count;
            ExperimentAssignment[] assignments = new ExperimentAssignment[count];

            for (int i = 0; i < count; i++)
            {
                ExperimentDef def = _tables.Get(i);
                if (!def.Enabled)
                {
                    // 禁用实验不参与分桶：固定回退 A 组值，业务侧无需为"实验下线"再写一条分支。
                    assignments[i] = new ExperimentAssignment(
                        def.ExperimentId,
                        def.Key,
                        def.VariantA,
                        false,
                        false);
                    continue;
                }

                bool isB = ExperimentBucketing.IsB(def.Key, playerId, def.TrafficPercent);
                assignments[i] = new ExperimentAssignment(
                    def.ExperimentId,
                    def.Key,
                    isB ? def.VariantB : def.VariantA,
                    isB,
                    true);
            }

            // 状态先整体落定，再广播事件：订阅方回调里查询到的是最终分配与最终曝光计数。
            _assignments = assignments;

            for (int i = 0; i < count; i++)
            {
                ExperimentDef def = _tables.Get(i);
                if (!def.Enabled)
                {
                    continue;
                }

                if (!_exposedPairs.Add(ExposureKey(def.ExperimentId, playerId)))
                {
                    // 这个玩家在这个实验上已经曝光过：不重复发事件，也不重复计数。
                    continue;
                }

                _exposedCount++;
                _bus.Publish(new ExperimentExposedEvent(def.ExperimentId, assignments[i].Variant, playerId));
            }
        }

        /// <summary>
        /// 返回最近一次 evaluate 的分配快照（顺序 = 配置表行顺序）。
        /// <para>
        /// 未 evaluate 前返回空分配 + <c>ExposedCount=0</c>：查询不做隐式分桶，
        /// "什么时候曝光"必须由适配层显式驱动，否则 UI 随便查一下就会污染实验数据。
        /// </para>
        /// </summary>
        public ExperimentSnapshot Handle(in ExperimentQueryRequest query)
        {
            return new ExperimentSnapshot(_assignments, _exposedCount);
        }

        /// <summary>
        /// 把 <c>(playerId, experimentId)</c> 压成一个 long 做去重键：
        /// 高 32 位放 playerId、低 32 位放非负的 experimentId（子表已校验恒为正），两者都恰好占满 32 位，不会撞。
        /// </summary>
        private static long ExposureKey(int experimentId, int playerId)
        {
            return ((long)playerId << 32) | (uint)experimentId;
        }
    }
}
