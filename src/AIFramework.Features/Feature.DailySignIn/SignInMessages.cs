using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;

namespace AIFramework.Features.DailySignIn
{
    /// <summary>领取当日签到奖励。空结构体：当日奖励内容由切片子表决定，不由 UI 传入。</summary>
    public readonly struct ClaimDailySignInCommand : ICommand
    {
    }

    /// <summary>补签指定天：<see cref="DayIndex"/> 为绝对天序号（自纪元起），必须落在当前周期内。</summary>
    public readonly struct RetroactiveSignInCommand : ICommand
    {
        public RetroactiveSignInCommand(int dayIndex)
        {
            DayIndex = dayIndex;
        }

        /// <summary>要补签的绝对天序号，不是 1..7 的格子序号。</summary>
        public int DayIndex { get; }
    }

    /// <summary>签到成功事实：<see cref="DayIndex"/> 为绝对天序号，其余字段为本次实际发放内容。</summary>
    public readonly struct DailySignInClaimedEvent : IEvent
    {
        public DailySignInClaimedEvent(int dayIndex, int itemId, int itemCount)
        {
            DayIndex = dayIndex;
            ItemId = itemId;
            ItemCount = itemCount;
        }

        public int DayIndex { get; }

        public int ItemId { get; }

        public int ItemCount { get; }
    }

    /// <summary>
    /// 背包只读查询请求（本切片私有请求体）。<see cref="IInventoryQuery"/> 是接口，接口类型无法满足
    /// <c>ICqeBus.Query&lt;TQuery, TResult&gt;</c> 的 <c>where TQuery : struct</c> 约束，
    /// 因此调用点必须使用本结构体作为 TQuery 实参。
    /// </summary>
    public readonly struct InventoryQueryRequest : IInventoryQuery
    {
        public InventoryQueryRequest(int itemId)
        {
            ItemId = itemId;
        }

        /// <summary>要查询的道具 ID（补签卡为 9001）。</summary>
        public int ItemId { get; }
    }
}
