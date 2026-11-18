#nullable enable

namespace AIFramework.Features.DailySignIn
{
    /// <summary>单格签到状态。</summary>
    public enum SignInSlotState
    {
        /// <summary>尚未到达（未来格）。</summary>
        Locked = 0,

        /// <summary>今天可领。</summary>
        Claimable = 1,

        /// <summary>已领取。</summary>
        Claimed = 2,

        /// <summary>已错过（今天之前未领）。</summary>
        Missed = 3
    }

    /// <summary>单格展示数据：只读值类型，视图层不得回写。</summary>
    public readonly struct SignInSlotViewData
    {
        public SignInSlotViewData(int dayIndex, SignInSlotState state, int itemId, int itemCount)
        {
            DayIndex = dayIndex;
            State = state;
            ItemId = itemId;
            ItemCount = itemCount;
        }

        /// <summary>格子序号 1..7（周期内相对序号，不是绝对天序号）。</summary>
        public int DayIndex { get; }

        public SignInSlotState State { get; }

        public int ItemId { get; }

        public int ItemCount { get; }
    }
}
