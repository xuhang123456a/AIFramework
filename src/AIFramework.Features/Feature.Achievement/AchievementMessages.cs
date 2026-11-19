#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Achievement
{
    /// <summary>
    /// 成就计数指标取值域：必须与 <c>tables/TbAchievement.json</c> 的 <c>metric</c> 列语义一致。
    /// <para>指标是开放集合的封闭子集（0..4）；不在集合内的数值按"未知指标"忽略，不抛异常。</para>
    /// </summary>
    public static class AchievementMetrics
    {
        /// <summary>累计击杀数。</summary>
        public const int Kill = 0;

        /// <summary>累计通关次数。</summary>
        public const int Clear = 1;

        /// <summary>累计抽卡次数。</summary>
        public const int Gacha = 2;

        /// <summary>累计签到天数。</summary>
        public const int SignIn = 3;

        /// <summary>累计金币产出。</summary>
        public const int Gold = 4;

        /// <summary>受支持的指标数量（内部计数器数组长度）。</summary>
        public const int Count = 5;

        /// <summary>是否为已知指标；未知指标的命令一律忽略。</summary>
        public static bool IsSupported(int metric)
        {
            return metric >= 0 && metric < Count;
        }
    }

    /// <summary>
    /// 成就进度上报命令：把 <paramref name="Amount"/> 累加到 <paramref name="Metric"/> 指标上。
    /// <para>规则：<c>Amount &lt;= 0</c> 或指标未知时静默忽略（不抛、不改状态）。</para>
    /// </summary>
    public readonly struct AchievementRecordCommand : ICommand
    {
        public AchievementRecordCommand(int metric, int amount)
        {
            Metric = metric;
            Amount = amount;
        }

        /// <summary>计数指标，取值见 <see cref="AchievementMetrics"/>。</summary>
        public int Metric { get; }

        /// <summary>本次增量，必须为正整数才会生效。</summary>
        public int Amount { get; }
    }

    /// <summary>
    /// 成就解锁事实：同一成就在一次游戏进程内**只会广播一次**（重复上报只累计、不重复解锁）。
    /// <para>发出顺序固定为：先 <c>GrantItemCommand</c> 发奖，再广播本事件。</para>
    /// </summary>
    public readonly struct AchievementUnlockedEvent : IEvent
    {
        public AchievementUnlockedEvent(int achievementId, string name, int rewardItemId, int rewardCount)
        {
            AchievementId = achievementId;
            Name = name;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        /// <summary>成就 ID（配置主键）。</summary>
        public int AchievementId { get; }

        /// <summary>成就展示名（来自子表）。</summary>
        public string Name { get; }

        /// <summary>解锁奖励道具 ID。</summary>
        public int RewardItemId { get; }

        /// <summary>解锁奖励数量。</summary>
        public int RewardCount { get; }
    }
}
