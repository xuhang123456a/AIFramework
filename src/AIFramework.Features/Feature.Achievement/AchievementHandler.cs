#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Achievement
{
    /// <summary>
    /// 成就业务规则：唯一允许改写进度与解锁状态的地方。
    /// <para>
    /// 跨模块通信只走注入的 <see cref="ICqeBus"/>：解锁时先发 <see cref="GrantItemCommand"/> 发奖，
    /// 再广播 <see cref="AchievementUnlockedEvent"/>；不碰背包、不碰红点树、不碰 UI、不落存档。
    /// </para>
    /// <para>
    /// 内存策略：累计值用定长 <c>int[5]</c>（按 metric 下标），解锁位用与子表同长的 <c>bool[]</c>；
    /// 查询时才分配一份 <see cref="AchievementSnapshot"/>，内部不缓存快照。
    /// 累计值按真实增量累加、不做截断（截断只发生在展示快照里），否则阶梯成就 1/10/100 会在第一档后卡死。
    /// </para>
    /// </summary>
    public sealed class AchievementHandler
        : ICommandHandler<AchievementRecordCommand>,
          IQueryHandler<AchievementQueryRequest, AchievementSnapshot>
    {
        /// <summary>成就发奖原因标签（因果日志追溯用）。</summary>
        private const int UnlockRewardReasonTagId = 6401;

        private readonly ICqeBus _bus;
        private readonly AchievementTables _tables;

        /// <summary>各 metric 的真实累计值，下标即 <see cref="AchievementMetrics"/> 常量。</summary>
        private readonly int[] _counters;

        /// <summary>与子表行对齐的解锁位（表已按 achievementId 升序）。</summary>
        private readonly bool[] _unlocked;

        /// <summary>加载子表并初始化计数/解锁状态；构造里不注册总线。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">成就子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">成就子表数据非法。</exception>
        public AchievementHandler(ICqeBus bus, IBinaryTableLoader loader)
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
            _tables = new AchievementTables(loader);
            _counters = new int[AchievementMetrics.Count];
            _unlocked = new bool[_tables.Count];
        }

        /// <summary>
        /// 注册进度上报命令与全量查询处理器。
        /// 装配入口必须传入构造本对象时的同一个总线实例（发送侧用的是构造注入的总线）。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<AchievementRecordCommand>(this);
            bus.RegisterQueryHandler<AchievementQueryRequest, AchievementSnapshot>(this);
        }

        /// <summary>
        /// 累计指标并在跨过任意未解锁成就的 <c>target</c> 时解锁：每次解锁固定"发奖指令 → 解锁事件"。
        /// <para>
        /// 静默忽略：<c>amount &lt;= 0</c>、未知 metric；一次上报可跨越多个阶梯，按 achievementId 升序全部解锁。
        /// </para>
        /// </summary>
        public void Handle(in AchievementRecordCommand command)
        {
            if (command.Amount <= 0)
            {
                return;
            }

            if (!AchievementMetrics.IsSupported(command.Metric))
            {
                return;
            }

            int previous = _counters[command.Metric];
            // 饱和累加：上报方传入 int.MaxValue 级别的增量也不得回绕成负数。
            _counters[command.Metric] = command.Amount > int.MaxValue - previous
                ? int.MaxValue
                : previous + command.Amount;
            int current = _counters[command.Metric];

            for (int i = 0; i < _tables.Count; i++)
            {
                if (_unlocked[i])
                {
                    continue;
                }

                AchievementDef def = _tables.Get(i);
                if (def.Metric != command.Metric || current < def.Target)
                {
                    continue;
                }

                _bus.Send(new GrantItemCommand(def.RewardItemId, def.RewardCount, UnlockRewardReasonTagId));
                _bus.Publish(new AchievementUnlockedEvent(
                    def.AchievementId,
                    def.Name,
                    def.RewardItemId,
                    def.RewardCount));
                _unlocked[i] = true;
            }
        }

        /// <summary>
        /// 返回按 achievementId 升序的全量快照。
        /// 空表（0 行）是合法状态：<see cref="AchievementSnapshot.Total"/> 与
        /// <see cref="AchievementSnapshot.UnlockedCount"/> 均为 0，<see cref="AchievementSnapshot.Items"/> 为空数组。
        /// </summary>
        public AchievementSnapshot Handle(in AchievementQueryRequest query)
        {
            AchievementProgress[] items = new AchievementProgress[_tables.Count];
            int unlockedCount = 0;
            for (int i = 0; i < items.Length; i++)
            {
                AchievementDef def = _tables.Get(i);
                bool isUnlocked = _unlocked[i];
                if (isUnlocked)
                {
                    unlockedCount++;
                }

                int accumulated = _counters[def.Metric];
                int current = accumulated > def.Target ? def.Target : accumulated;
                items[i] = new AchievementProgress(
                    def.AchievementId,
                    def.Name,
                    def.Metric,
                    current,
                    def.Target,
                    isUnlocked);
            }

            return new AchievementSnapshot(items, unlockedCount);
        }
    }
}
