#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Achievement
{
    /// <summary>单条成就的只读展示进度：配置列 + 当前计数 + 解锁标记，一次查询内合并完成。</summary>
    public readonly struct AchievementProgress
    {
        public AchievementProgress(
            int achievementId,
            string name,
            int metric,
            int current,
            int target,
            bool isUnlocked)
        {
            AchievementId = achievementId;
            Name = name;
            Metric = metric;
            Current = current;
            Target = target;
            IsUnlocked = isUnlocked;
        }

        /// <summary>成就 ID（配置主键，升序）。</summary>
        public int AchievementId { get; }

        /// <summary>成就展示名（来自子表）。</summary>
        public string Name { get; }

        /// <summary>计数指标，取值见 <see cref="AchievementMetrics"/>。</summary>
        public int Metric { get; }

        /// <summary>
        /// 当前展示进度：真实累计值**截断到 <see cref="Target"/>**（超出部分不展示）。
        /// 内部累计计数不截断，否则阶梯成就（1/10/100）会在第一档后无法继续推进。
        /// </summary>
        public int Current { get; }

        /// <summary>达成所需累计值，恒 &gt; 0。</summary>
        public int Target { get; }

        /// <summary>是否已解锁（同进程内不会回退）。</summary>
        public bool IsUnlocked { get; }
    }

    /// <summary>
    /// 全量成就快照：构造后不可变，按 <see cref="AchievementProgress.AchievementId"/> 升序排列。
    /// <para>
    /// 内存策略：快照只在查询时分配一个 <see cref="AchievementProgress"/> 数组（行数由配置决定，通常十几行），
    /// 切片内部状态是定长 <c>int[5]</c>（按 metric 索引的累计值）+ <c>bool[行数]</c>（解锁位），
    /// 不缓存快照、不在热路径重建字符串。
    /// </para>
    /// </summary>
    public sealed class AchievementSnapshot
    {
        public AchievementSnapshot(IReadOnlyList<AchievementProgress> items, int unlockedCount)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            Items = items;
            UnlockedCount = unlockedCount;
        }

        /// <summary>成就总数（等于配置表行数）。</summary>
        public int Total
        {
            get { return Items.Count; }
        }

        /// <summary>已解锁数量。</summary>
        public int UnlockedCount { get; }

        /// <summary>全部成就的展示进度，按成就 ID 升序。</summary>
        public IReadOnlyList<AchievementProgress> Items { get; }
    }

    /// <summary>成就全量查询请求：无参数，返回当前玩家的只读快照。</summary>
    public readonly struct AchievementQueryRequest : IQuery<AchievementSnapshot>
    {
    }
}
