using System;
using System.Collections.Generic;

namespace AIFramework.Features.DailySignIn
{
    /// <summary>
    /// 纯逻辑 UI 大脑：把 <see cref="SignInHandler"/> 的状态翻译成 7 格展示数据，
    /// 并把视图点击转发成 CQE 命令。这里不做任何奖励计算与状态改写。
    /// </summary>
    public sealed class SignInPresenter : IDisposable
    {
        private readonly SignInHandler _handler;
        private readonly ISignInView _view;
        private bool _disposed;

        public SignInPresenter(SignInHandler handler, ISignInView view)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            _handler = handler;
            _view = view;
            _view.OnClaimClicked += HandleClaimClicked;
            _view.OnRetroactiveClicked += HandleRetroactiveClicked;
        }

        /// <summary>重新组装 7 格并交给视图；每次允许分配（非帧内路径）。</summary>
        public void Refresh()
        {
            SignInState state = _handler.Snapshot;
            SignInTables tables = _handler.Tables;
            int today = _handler.TodayDayIndex;
            int dayCount = tables.DayCount;

            List<SignInSlotViewData> slots = new List<SignInSlotViewData>(dayCount);
            for (int slotIndex = 1; slotIndex <= dayCount; slotIndex++)
            {
                int absoluteDay = state.CycleStartDayIndex + slotIndex - 1;
                SignInRewardRow row = tables.Get(slotIndex);

                // 已领判定同时覆盖两条路径：Claim 按顺序标记、Retroactive 按格子标记，
                // 以及"今天这一格刚被领过"（LastClaimDayIndex 命中格子对应天）。
                bool claimed = state.ClaimedDays[slotIndex - 1] || state.LastClaimDayIndex == absoluteDay;

                SignInSlotState slotState;
                if (claimed)
                {
                    slotState = SignInSlotState.Claimed;
                }
                else if (absoluteDay == today)
                {
                    slotState = SignInSlotState.Claimable;
                }
                else if (absoluteDay < today)
                {
                    slotState = SignInSlotState.Missed;
                }
                else
                {
                    slotState = SignInSlotState.Locked;
                }

                slots.Add(new SignInSlotViewData(slotIndex, slotState, row.ItemId, row.ItemCount));
            }

            _view.RenderSlots(slots);
        }

        /// <summary>解绑视图事件；可重复调用。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _view.OnClaimClicked -= HandleClaimClicked;
            _view.OnRetroactiveClicked -= HandleRetroactiveClicked;
        }

        private void HandleClaimClicked()
        {
            _handler.Bus.Send(new ClaimDailySignInCommand());
            Refresh();
        }

        private void HandleRetroactiveClicked(int dayIndexOrSlotIndex)
        {
            // 视图只会回传它拿到的 SignInSlotViewData.DayIndex（1..7 的格子序号），
            // 但事件本身也允许直接传绝对天序号：已落在当前周期内的值按绝对天序号原样转发，
            // 其余落入 1..7 的值按格子序号换算成绝对天序号。
            int cycleStart = _handler.Snapshot.CycleStartDayIndex;
            int dayCount = _handler.Tables.DayCount;
            bool insideCycle = dayIndexOrSlotIndex >= cycleStart && dayIndexOrSlotIndex < cycleStart + dayCount;
            int dayIndex = dayIndexOrSlotIndex;
            if (!insideCycle)
            {
                int slotOffset = dayIndexOrSlotIndex - 1;
                if (slotOffset >= 0 && slotOffset < dayCount)
                {
                    dayIndex = cycleStart + slotOffset;
                }
            }

            _handler.Bus.Send(new RetroactiveSignInCommand(dayIndex));
            Refresh();
        }
    }
}
