#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Experiment
{
    /// <summary>
    /// 单个实验的分配结果：配置列 + 最近一次 evaluate 算出的分组。
    /// <para>
    /// <see cref="Enabled"/> 为 false 的实验不参与分桶：它的 <see cref="Variant"/> 恒等于表里的
    /// <c>variantA</c>、<see cref="IsB"/> 恒为 false（业务侧因此不需要为"实验没开"写第二条分支）。
    /// </para>
    /// <para>
    /// <see cref="IsB"/> 是**分桶结论**，不是"<c>Variant == variantB</c>"的等价判断：
    /// 配置允许 A/B 两组取同一个值（对照组），此时 <c>IsB</c> 仍然如实反映玩家落在哪一组。
    /// </para>
    /// </summary>
    public readonly struct ExperimentAssignment
    {
        public ExperimentAssignment(int experimentId, string key, int variant, bool isB, bool enabled)
        {
            ExperimentId = experimentId;
            Key = key;
            Variant = variant;
            IsB = isB;
            Enabled = enabled;
        }

        /// <summary>实验 id（配置主键，恒为正，表内唯一）。</summary>
        public int ExperimentId { get; }

        /// <summary>实验键（非空，表内唯一）：分桶哈希只用它，不认 <see cref="ExperimentId"/>。</summary>
        public string Key { get; }

        /// <summary>该玩家实际生效的档位取值（B 组为 <c>variantB</c>，否则 <c>variantA</c>）。</summary>
        public int Variant { get; }

        /// <summary>是否落在 B 组（<c>bucket &lt; trafficPercent</c>）；禁用实验恒为 false。</summary>
        public bool IsB { get; }

        /// <summary>配置是否启用（<c>enabled=1</c>）：禁用实验不分桶、不曝光、只回退 A 组值。</summary>
        public bool Enabled { get; }
    }

    /// <summary>
    /// 实验分配全量只读快照：顺序 = 配置表行顺序，构造后不可变。
    /// <para>
    /// <b>未 evaluate 前</b>：<see cref="Assignments"/> 为空、<see cref="ExposedCount"/> 为 0
    /// （本切片刻意不在查询里隐式分桶——"什么时候曝光"必须由适配层显式驱动，
    /// 否则 UI 随便查一下就会给玩家记一次曝光，实验数据立刻被污染）。
    /// </para>
    /// <para>
    /// <b>空表</b>：同样返回空数组与 0，不抛异常也不发事件。
    /// </para>
    /// <para>
    /// 快照内容来自**最近一次 <c>ExperimentEvaluateCommand</c>**，查询本身不重新分桶：
    /// 这样"曝光事件"和"快照"永远讲同一个故事。
    /// </para>
    /// </summary>
    public sealed class ExperimentSnapshot
    {
        public ExperimentSnapshot(IReadOnlyList<ExperimentAssignment> assignments, int exposedCount)
        {
            if (assignments == null)
            {
                throw new ArgumentNullException(nameof(assignments));
            }

            Assignments = assignments;
            ExposedCount = exposedCount;
        }

        /// <summary>
        /// 全部实验的分配结果，顺序 = 配置表行顺序（含 <c>enabled=0</c> 的行，它们带 <c>Enabled=false</c>）；
        /// 未 evaluate 前为空数组。
        /// </summary>
        public IReadOnlyList<ExperimentAssignment> Assignments { get; }

        /// <summary>
        /// 本切片自构造以来**已广播的曝光条数**（按 <c>(experimentId, playerId)</c> 去重后累加）。
        /// <para>
        /// 生产上客户端一个进程只有一个玩家，因此它等于"该玩家被曝光的实验数"；
        /// 联调里依次 evaluate 多个玩家时它是 Σ（每换一个玩家 +启用实验数），
        /// 重复 evaluate 同一玩家则不增长（幂等口径）。
        /// </para>
        /// </summary>
        public int ExposedCount { get; }
    }

    /// <summary>实验分配全量查询请求：无参数，返回最近一次 evaluate 的结果 + 累计曝光条数。</summary>
    public readonly struct ExperimentQueryRequest : IQuery<ExperimentSnapshot>
    {
    }
}
