using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.IdleTycoon
{
    /// <summary>
    /// 放置/经营玩法内核：建筑升级、产出累计与离线收益结算。
    /// <para>
    /// 状态只有"每栋建筑的当前等级"与"上次结算时刻"两项，全部收口在本类；
    /// 金币只走总线（<see cref="ConsumeItemCommand"/> / <see cref="GrantItemCommand"/>），禁止直接改背包。
    /// 时间只读 <see cref="ITimeProvider"/>，因此可被测试任意快进。
    /// </para>
    /// </summary>
    public sealed class IdleTycoonHandler : ICommandHandler<UpgradeBuildingCommand>
    {
        /// <summary>升级扣款原因标签（因果日志用）。</summary>
        private const int ConsumeReasonTagId = 6401;

        /// <summary>离线收益发放原因标签（因果日志用）。</summary>
        private const int GrantReasonTagId = 6402;

        /// <summary>单次离线结算的时长上限（秒）：24 小时，防止改系统时间刷爆经济。</summary>
        private const double MaxOfflineSeconds = 86400d;

        private readonly ICqeBus _bus;
        private readonly ITimeProvider _time;
        private readonly BuildingDef[] _defs;
        private readonly int[] _levels;

        /// <summary>上次结算时刻（取自 <see cref="ITimeProvider.ElapsedSeconds"/>，不是墙钟）。</summary>
        private double _lastSettleSeconds;

        private int _totalUpgradesPurchased;

        public IdleTycoonHandler(ICqeBus bus, ITimeProvider time, IBinaryTableLoader loader)
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

            IdleTables tables = new IdleTables(loader);
            _defs = new BuildingDef[tables.Count];
            _levels = new int[tables.Count];
            for (int i = 0; i < tables.Count; i++)
            {
                // 构造即全 0 级：等级是运行时状态，绝不从表里读初始值。
                _defs[i] = tables.Get(i);
                _levels[i] = 0;
            }

            _lastSettleSeconds = time.ElapsedSeconds;
        }

        /// <summary>当前每秒产出（所有建筑等级 × 基础产出累加）。</summary>
        public int IncomePerSecond
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _defs.Length; i++)
                {
                    total += _levels[i] * _defs[i].BaseIncome;
                }

                return total;
            }
        }

        /// <summary>已成功购买的升级总次数（被拒绝的请求不计）。</summary>
        public int GetTotalUpgradesPurchased
        {
            get { return _totalUpgradesPurchased; }
        }

        /// <summary>注册升级命令处理器（构造里不自动注册，装配层负责调用）。</summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<UpgradeBuildingCommand>(this);
        }

        /// <summary>
        /// 升级：未知建筑 / 已满级 / 金币不足 → 静默拒绝（状态不变、不发指令、不发事件）；
        /// 合法 → 先扣金币、再落等级。升级成功<b>不广播任何事件</b>，产出变化由 <see cref="IncomePerSecond"/> 反映。
        /// </summary>
        public void Handle(in UpgradeBuildingCommand command)
        {
            int index = IndexOf(command.BuildingId);
            if (index < 0)
            {
                // 未知建筑：静默拒绝，连背包都不查。
                return;
            }

            BuildingDef def = _defs[index];
            if (_levels[index] >= def.MaxLevel)
            {
                // 已满级：静默拒绝，同样不查背包。
                return;
            }

            InventorySnapshot inventory = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold));
            if (inventory.Count < def.UpgradeCost)
            {
                // 金币不足：静默拒绝，状态不变。
                return;
            }

            // 先扣款（金币数量由背包切片结算），扣款指令发出后本切片才落等级，
            // 保证"等级涨了但钱没扣"的脏状态不会出现在因果日志里。
            _bus.Send(new ConsumeItemCommand(MetaItemIds.Gold, def.UpgradeCost, ConsumeReasonTagId));
            _levels[index]++;
            _totalUpgradesPurchased++;
        }

        /// <summary>
        /// 结算离线收益：按 <c>elapsed = now - 上次结算</c> 产出 <c>floor(elapsed × IncomePerSecond)</c> 金币，
        /// 单次最多结算 24 小时；金额 &gt; 0 时发放金币并对每栋有等级的建筑广播
        /// <see cref="IdleIncomeCollectedEvent"/>（各建筑的 Amount 之和严格等于本次总额）。
        /// </summary>
        /// <returns>本次结算到的金币总额。</returns>
        public int SettleOfflineIncome()
        {
            double now = _time.ElapsedSeconds;
            double elapsed = now - _lastSettleSeconds;

            // 无条件对齐基线：即便本次被 24 小时封顶或时钟回拨，下次也从"现在"重新计时。
            _lastSettleSeconds = now;

            if (elapsed <= 0d)
            {
                // 时钟回拨或零间隔：本次产出 0，不发指令不发事件。
                return 0;
            }

            if (elapsed > MaxOfflineSeconds)
            {
                elapsed = MaxOfflineSeconds;
            }

            int total = FloorAmount(elapsed, IncomePerSecond);
            if (total <= 0)
            {
                return 0;
            }

            _bus.Send(new GrantItemCommand(MetaItemIds.Gold, total, GrantReasonTagId));
            PublishCollectedEvents(elapsed);
            return total;
        }

        /// <summary>查询某建筑等级；未知 ID 抛 <see cref="ArgumentOutOfRangeException"/>（查询 ≠ 静默过滤）。</summary>
        public int GetLevel(int buildingId)
        {
            int index = IndexOf(buildingId);
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(buildingId), buildingId, "未知建筑 ID：" + buildingId + "。");
            }

            return _levels[index];
        }

        /// <summary>
        /// 按累计产出率分摊本次总额：<c>share_i = floor(elapsed × 累计率_i) - floor(elapsed × 累计率_i-1)</c>。
        /// 差额法保证所有事件金额之和恰好等于 <paramref name="total"/>，不会因逐栋向下取整而漏金币。
        /// </summary>
        private void PublishCollectedEvents(double elapsed)
        {
            int cumulativeRate = 0;
            int previousAmount = 0;
            for (int i = 0; i < _defs.Length; i++)
            {
                int level = _levels[i];
                if (level <= 0)
                {
                    continue;
                }

                cumulativeRate += level * _defs[i].BaseIncome;
                int cumulativeAmount = FloorAmount(elapsed, cumulativeRate);
                _bus.Publish(new IdleIncomeCollectedEvent(_defs[i].BuildingId, cumulativeAmount - previousAmount));
                previousAmount = cumulativeAmount;
            }
        }

        /// <summary>线性查找建筑行下标；未找到返回 -1（建筑规模为个位数，不做字典）。</summary>
        private int IndexOf(int buildingId)
        {
            for (int i = 0; i < _defs.Length; i++)
            {
                if (_defs[i].BuildingId == buildingId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>向下取整的产出额，夹在 <c>0..int.MaxValue</c> 内，避免极端配置溢出成负金币。</summary>
        private static int FloorAmount(double seconds, int rate)
        {
            double raw = Math.Floor(seconds * rate);
            if (raw <= 0d)
            {
                return 0;
            }

            if (raw >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)raw;
        }
    }
}
