using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.IdleTycoon
{
    /// <summary>
    /// 升级建筑指令：只表达"想把某栋建筑升一级"的意图，
    /// 能否升级（存在性 / 满级 / 金币）全部由 <see cref="IdleTycoonHandler"/> 判定。
    /// </summary>
    public readonly struct UpgradeBuildingCommand : ICommand
    {
        public UpgradeBuildingCommand(int buildingId)
        {
            BuildingId = buildingId;
        }

        /// <summary>目标建筑 ID。</summary>
        public int BuildingId { get; }
    }

    /// <summary>
    /// 离线收益结算事实：本次结算中"某栋建筑"产生的金币。
    /// 一次 <see cref="IdleTycoonHandler.SettleOfflineIncome"/> 只对等级 &gt; 0 的建筑各发一条，
    /// 所有事件的 <see cref="Amount"/> 之和严格等于本次发放的金币总额。
    /// </summary>
    public readonly struct IdleIncomeCollectedEvent : IEvent
    {
        public IdleIncomeCollectedEvent(int buildingId, int amount)
        {
            BuildingId = buildingId;
            Amount = amount;
        }

        /// <summary>产生收益的建筑 ID。</summary>
        public int BuildingId { get; }

        /// <summary>该建筑本次结算到的金币（向下取整后的分摊值，可为 0）。</summary>
        public int Amount { get; }
    }
}
