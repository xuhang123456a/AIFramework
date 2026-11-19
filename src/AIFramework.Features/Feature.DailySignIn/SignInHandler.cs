#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.DailySignIn
{
    /// <summary>
    /// 签到业务规则：唯一允许改写 <see cref="SignInState"/> 的地方。
    /// 跨天/跨周期判断全部集中在 <see cref="TodayDayIndex"/> 与 <see cref="NormalizeCycle"/>，
    /// 消息收发一律经注入的 <see cref="ICqeBus"/>，禁止直接改背包。
    /// </summary>
    public sealed class SignInHandler : ICommandHandler<ClaimDailySignInCommand>,
                                        ICommandHandler<RetroactiveSignInCommand>
    {
        /// <summary>周期长度（天）。</summary>
        private const int CycleLength = 7;

        /// <summary>签到发奖原因标签。</summary>
        private const int ClaimReasonTagId = 5001;

        /// <summary>补签消耗/发奖原因标签。</summary>
        private const int RetroactiveReasonTagId = 5002;

        /// <summary>补签卡道具 ID。</summary>
        private const int RetroactiveCardItemId = 9001;

        /// <summary>天序号纪元：2020-01-01 00:00:00 UTC。</summary>
        private static readonly DateTime DayEpoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly ITimeProvider _time;
        private readonly SignInTables _tables;
        private readonly SignInState _state;

        public SignInHandler(ICqeBus bus, ITimeProvider time, IBinaryTableLoader loader)
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

            Bus = bus;
            _time = time;
            _tables = new SignInTables(loader);

            int today = TodayDayIndex;
            _state = new SignInState
            {
                CycleStartDayIndex = today,
                ClaimedCount = 0,
                LastClaimDayIndex = -1,
            };
        }

        /// <summary>构造时注入的总线；构造里不自动注册，需显式调用 <see cref="Register"/>。</summary>
        public ICqeBus Bus { get; }

        /// <summary>本切片子表，仅供同程序集内的 Presenter 读取展示数据。</summary>
        internal SignInTables Tables
        {
            get { return _tables; }
        }

        /// <summary>当前状态快照（活引用，测试只读）。</summary>
        public SignInState Snapshot
        {
            get { return _state; }
        }

        /// <summary>
        /// 今天的天序号：<c>(int)(time.UtcNow.Date - epoch).TotalDays</c>。
        /// 只走 <see cref="ITimeProvider"/>，测试可推进 mock 时间后重新断言。
        /// </summary>
        public int TodayDayIndex
        {
            get { return (int)(_time.UtcNow.Date - DayEpoch).TotalDays; }
        }

        /// <summary>注册两条命令处理器（构造里不自动注册，装配层负责调用）。</summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<ClaimDailySignInCommand>(this);
            bus.RegisterCommandHandler<RetroactiveSignInCommand>(this);
        }

        /// <summary>领取当日奖励：跨周期先重置，随后做当日/满勤防重。</summary>
        public void Handle(in ClaimDailySignInCommand command)
        {
            int dayIndex = TodayDayIndex;

            // 跨天/跨周期判断只在这一处：滑到新周期就把周期起点前移并清空领取标记。
            NormalizeCycle(dayIndex);

            // 今日已领 或 本周期已领满：静默拒绝，不发指令、不发事件、状态不变。
            if (_state.LastClaimDayIndex == dayIndex || _state.ClaimedCount >= CycleLength)
            {
                return;
            }

            SignInRewardRow row = _tables.Get(dayIndex - _state.CycleStartDayIndex + 1);
            Bus.Send(new GrantItemCommand(row.ItemId, row.ItemCount, ClaimReasonTagId));
            Bus.Publish(new DailySignInClaimedEvent(dayIndex, row.ItemId, row.ItemCount));

            // 必须按"格子偏移"落标记，不能用 ClaimedCount：补签会先填后面的格子，
            // 此时 ClaimedCount 与格子下标不再相等，按计数写会把标记写到错的格子（历史缺陷，已修）。
            _state.ClaimedDays[dayIndex - _state.CycleStartDayIndex] = true;
            _state.ClaimedCount++;
            _state.LastClaimDayIndex = dayIndex;
        }

        /// <summary>补签：先做结构与周期校验，再读背包确认补签卡，最后消耗、发奖、广播并按格子落标记。</summary>
        public void Handle(in RetroactiveSignInCommand command)
        {
            int dayIndex = command.DayIndex;
            int today = TodayDayIndex;

            // 补签与正常领取共用同一套周期归一化，避免用过期周期起点做格子换算。
            NormalizeCycle(today);

            if (dayIndex > today)
            {
                // 未来天不能补签：Presenter 把未来格渲染成 Locked 点不到，命令通道必须同样拒绝。
                return;
            }

            int slotOffset = dayIndex - _state.CycleStartDayIndex;
            if (slotOffset < 0 || slotOffset >= CycleLength)
            {
                // 目标天不在当前周期内：静默拒绝（不是业务拒绝，连背包都不该查）。
                return;
            }

            if (_state.ClaimedDays[slotOffset] || _state.ClaimedCount >= CycleLength)
            {
                // 该格已领（或本周期已满）：静默拒绝，避免重复发奖与计数越界。
                return;
            }

            InventorySnapshot inventory = Bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(RetroactiveCardItemId));
            if (inventory.Count <= 0)
            {
                // 补签卡不足：静默拒绝，不发指令、状态不变。
                return;
            }

            SignInRewardRow row = _tables.Get(slotOffset + 1);
            Bus.Send(new ConsumeItemCommand(RetroactiveCardItemId, 1, RetroactiveReasonTagId));
            Bus.Send(new GrantItemCommand(row.ItemId, row.ItemCount, RetroactiveReasonTagId));
            Bus.Publish(new DailySignInClaimedEvent(dayIndex, row.ItemId, row.ItemCount));

            _state.ClaimedDays[slotOffset] = true;
            _state.ClaimedCount++;
        }

        /// <summary>把周期起点前移到包含 <paramref name="dayIndex"/> 的周期，并清空本周期领取标记。</summary>
        private void NormalizeCycle(int dayIndex)
        {
            while (dayIndex >= _state.CycleStartDayIndex + CycleLength)
            {
                _state.CycleStartDayIndex += CycleLength;
                _state.ClaimedCount = 0;
                Array.Clear(_state.ClaimedDays, 0, _state.ClaimedDays.Length);
            }
        }
    }
}
