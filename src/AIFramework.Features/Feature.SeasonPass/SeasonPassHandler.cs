#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.SeasonPass
{
    /// <summary>
    /// 赛季通行证业务规则：唯一允许改写经验、等级与领取状态的地方。
    /// <para>
    /// 跨模块通信只走注入的 <see cref="ICqeBus"/>：领取时先发 <see cref="GrantItemCommand"/> 发奖，
    /// 再广播 <see cref="SeasonRewardClaimedEvent"/>；升级只广播 <see cref="SeasonLevelUpEvent"/>（不自动发奖）。
    /// 不碰背包、不碰红点树、不碰 UI、不落存档。
    /// </para>
    /// <para>
    /// 内存策略：经验/等级用 3 个标量，领取位用与子表同长的两个 <c>bool[]</c>；
    /// 查询时才分配一份 <see cref="SeasonPassSnapshot"/>，内部不缓存快照。
    /// </para>
    /// <para>
    /// 时间策略：赛季剩余时间与过期判定全部经注入的 <see cref="ITimeProvider"/>，
    /// 与构造注入的 <c>seasonEndUnixSeconds</c> 比较；测试用可快进假时间源即可覆盖过期。
    /// </para>
    /// </summary>
    public sealed class SeasonPassHandler
        : ICommandHandler<AddSeasonXpCommand>,
          ICommandHandler<ClaimSeasonRewardCommand>,
          ICommandHandler<UnlockPaidTrackCommand>,
          IQueryHandler<SeasonPassQueryRequest, SeasonPassSnapshot>
    {
        /// <summary>通行证发奖原因标签（因果日志追溯用）。</summary>
        private const int ClaimReasonTagId = 6501;

        /// <summary>Unix 纪元（1970-01-01T00:00:00Z）：只用于把 <see cref="ITimeProvider.UtcNow"/> 折成秒。</summary>
        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly ICqeBus _bus;
        private readonly ITimeProvider _time;
        private readonly SeasonPassTables _tables;
        private readonly long _seasonEndUnixSeconds;

        /// <summary>与子表行对齐的免费轨领取位。</summary>
        private readonly bool[] _freeClaimed;

        /// <summary>与子表行对齐的付费轨领取位。</summary>
        private readonly bool[] _paidClaimed;

        /// <summary>赛季累计经验（饱和累加，满级后继续累加）。</summary>
        private int _currentXp;

        /// <summary>付费轨是否已解锁（由 IAP 侧发 <see cref="UnlockPaidTrackCommand"/> 置位）。</summary>
        private bool _paidUnlocked;

        /// <summary>加载子表并初始化经验/领取状态；构造里不注册总线。</summary>
        /// <param name="bus">切片消息总线（发送/广播都走它）。</param>
        /// <param name="time">可快进时间源，用于赛季剩余时间与过期判定。</param>
        /// <param name="loader">子表加载端口。</param>
        /// <param name="seasonEndUnixSeconds">赛季结束时刻（Unix 秒，UTC）；now &gt;= 该值即过期。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/>、<paramref name="time"/> 或 <paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">通行证子表缺失。</exception>
        /// <exception cref="System.IO.InvalidDataException">通行证子表数据非法。</exception>
        public SeasonPassHandler(
            ICqeBus bus,
            ITimeProvider time,
            IBinaryTableLoader loader,
            long seasonEndUnixSeconds)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (time == null)
            {
                throw new ArgumentNullException(nameof(time));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _time = time;
            _tables = new SeasonPassTables(loader);
            _seasonEndUnixSeconds = seasonEndUnixSeconds;
            _freeClaimed = new bool[_tables.Count];
            _paidClaimed = new bool[_tables.Count];
        }

        /// <summary>
        /// 注册经验上报 / 奖励领取 / 付费轨解锁三条命令与全量查询处理器。
        /// 装配入口必须传入构造本对象时的同一个总线实例（发送侧用的是构造注入的总线）。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<AddSeasonXpCommand>(this);
            bus.RegisterCommandHandler<ClaimSeasonRewardCommand>(this);
            bus.RegisterCommandHandler<UnlockPaidTrackCommand>(this);
            bus.RegisterQueryHandler<SeasonPassQueryRequest, SeasonPassSnapshot>(this);
        }

        /// <summary>
        /// 累计赛季经验并在跨过等级门槛时**逐级**广播 <see cref="SeasonLevelUpEvent"/>（等级升序）。
        /// <para>
        /// 静默忽略：<c>amount &lt;= 0</c>、空表；满级后经验继续累加但不再升级、不再广播。
        /// 经验累计不因赛季过期而停止（过期只冻结领取），保证快照里的 CurrentXp 始终是真实值。
        /// </para>
        /// </summary>
        public void Handle(in AddSeasonXpCommand command)
        {
            if (command.Amount <= 0 || _tables.Count == 0)
            {
                return;
            }

            int previousLevel = ResolveLevel(_currentXp);
            int amount = command.Amount;
            _currentXp = amount > int.MaxValue - _currentXp ? int.MaxValue : _currentXp + amount;
            int newLevel = ResolveLevel(_currentXp);

            for (int level = previousLevel + 1; level <= newLevel; level++)
            {
                _bus.Publish(new SeasonLevelUpEvent(level));
            }
        }

        /// <summary>
        /// 领取某等级某轨奖励；任何拒绝条件都**静默忽略**（命令无返回值，不改状态、不发奖、不广播）：
        /// 等级越界 / 等级未到达 / 重复领取 / 付费轨未解锁 / 该级无付费奖励 / 赛季已过期。
        /// </summary>
        public void Handle(in ClaimSeasonRewardCommand command)
        {
            if (_tables.Count == 0)
            {
                return;
            }

            if (command.Level < 1 || command.Level > _tables.Count)
            {
                return;
            }

            if (IsExpiredNow())
            {
                return;
            }

            if (command.Level > ResolveLevel(_currentXp))
            {
                return;
            }

            int index = command.Level - 1;
            SeasonPassDef def = _tables.Get(index);

            if (!command.PaidTrack)
            {
                if (_freeClaimed[index])
                {
                    return;
                }

                _bus.Send(new GrantItemCommand(def.FreeItemId, def.FreeCount, ClaimReasonTagId));
                _bus.Publish(new SeasonRewardClaimedEvent(command.Level, false, def.FreeItemId, def.FreeCount));
                _freeClaimed[index] = true;
                return;
            }

            if (!_paidUnlocked || _paidClaimed[index])
            {
                return;
            }

            if (def.PaidItemId == 0)
            {
                // 该等级没有付费奖励：不是错误，只是无奖可领（配置允许付费列整对为 0）。
                return;
            }

            _bus.Send(new GrantItemCommand(def.PaidItemId, def.PaidCount, ClaimReasonTagId));
            _bus.Publish(new SeasonRewardClaimedEvent(command.Level, true, def.PaidItemId, def.PaidCount));
            _paidClaimed[index] = true;
        }

        /// <summary>解锁付费轨；重复调用幂等（不广播事件、不改其它状态）。</summary>
        public void Handle(in UnlockPaidTrackCommand command)
        {
            _paidUnlocked = true;
        }

        /// <summary>
        /// 返回通行证全量只读快照。
        /// <para>
        /// 空表（0 行）是合法状态：<see cref="SeasonPassSnapshot.Level"/> = 0、
        /// <see cref="SeasonPassSnapshot.IsMaxLevel"/> = <c>true</c>、Items 为空数组、可领数 0。
        /// </para>
        /// </summary>
        public SeasonPassSnapshot Handle(in SeasonPassQueryRequest query)
        {
            long now = CurrentUnixSeconds();
            bool isExpired = now >= _seasonEndUnixSeconds;
            long remainingSeconds = isExpired ? 0 : _seasonEndUnixSeconds - now;

            int level = _tables.Count == 0 ? 0 : ResolveLevel(_currentXp);
            bool isMaxLevel = _tables.Count == 0 || level == _tables.Count;
            int nextLevelXp = isMaxLevel ? _currentXp : _tables.Get(level).RequiredXp;

            SeasonRewardState[] items = new SeasonRewardState[_tables.Count];
            for (int i = 0; i < items.Length; i++)
            {
                SeasonPassDef def = _tables.Get(i);
                items[i] = new SeasonRewardState(
                    def.Level,
                    def.RequiredXp,
                    def.FreeItemId,
                    def.FreeCount,
                    def.PaidItemId,
                    def.PaidCount,
                    _freeClaimed[i],
                    _paidClaimed[i],
                    i < level);
            }

            return new SeasonPassSnapshot(
                level,
                _currentXp,
                nextLevelXp,
                isMaxLevel,
                _paidUnlocked,
                items,
                isExpired ? 0 : CountClaimable(level),
                remainingSeconds,
                isExpired);
        }

        /// <summary>把累计经验折成等级：返回"已到达的最高等级"（1 基）；经验按 requiredXp 单调比较。</summary>
        private int ResolveLevel(int totalXp)
        {
            int level = 1;
            for (int i = 1; i < _tables.Count; i++)
            {
                if (totalXp < _tables.Get(i).RequiredXp)
                {
                    break;
                }

                level = i + 1;
            }

            return level;
        }

        /// <summary>已到达等级里未领取的奖励条数（过期由调用方先挡掉）；付费轨需已解锁且该级确实有付费奖励。</summary>
        private int CountClaimable(int level)
        {
            int count = 0;
            for (int i = 0; i < level; i++)
            {
                if (!_freeClaimed[i])
                {
                    count++;
                }

                if (_paidUnlocked && !_paidClaimed[i] && _tables.Get(i).PaidItemId > 0)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>当前 UTC 时刻折成 Unix 秒；<see cref="ITimeProvider.UtcNow"/> 按 UTC 语义解释（Local 先转 UTC）。</summary>
        private long CurrentUnixSeconds()
        {
            DateTime utcNow = _time.UtcNow;
            if (utcNow.Kind == DateTimeKind.Local)
            {
                utcNow = utcNow.ToUniversalTime();
            }
            else if (utcNow.Kind != DateTimeKind.Utc)
            {
                // Unspecified 一律按 UTC 解释：逻辑层不猜测本机时区（AIF003）。
                utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            }

            return (long)Math.Floor((utcNow - UnixEpoch).TotalSeconds);
        }

        /// <summary>赛季是否已过期：now &gt;= 结束时刻即过期（边界秒算过期）。</summary>
        private bool IsExpiredNow()
        {
            return CurrentUnixSeconds() >= _seasonEndUnixSeconds;
        }
    }
}
