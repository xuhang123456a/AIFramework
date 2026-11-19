using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Gacha
{
    /// <summary>
    /// 抽卡业务规则：次数校验、费用校验、扣费、按权重抽卡、保底改判与发货广播全部收口在这里。
    /// <para>
    /// 随机只走注入的 <see cref="IRandomSource"/>：同一种子 + 同一子表 + 同一出发点必然得到同一序列，
    /// 便于回放与"概率公示"对账。保底计数是处理器状态，跨命令持续累计，不随单次命令清零。
    /// </para>
    /// </summary>
    public sealed class GachaHandler : ICommandHandler<DrawGachaCommand>
    {
        /// <summary>单抽费用（金币）。</summary>
        private const int SingleDrawCost = 100;

        /// <summary>十连费用（金币）。</summary>
        private const int TenDrawCost = 900;

        /// <summary>扣金币原因标签（因果日志用）。</summary>
        private const int ConsumeReasonTagId = 6501;

        /// <summary>发货原因标签（因果日志用）。</summary>
        private const int GrantReasonTagId = 6502;

        /// <summary>Phase 1 唯一支持的单抽次数。</summary>
        private const int SingleDrawCount = 1;

        /// <summary>Phase 1 唯一支持的十连次数。</summary>
        private const int TenDrawCount = 10;

        /// <summary>保底阈值：连续第 10 次仍非稀有则本次强制改判为稀有。</summary>
        private const int PityThreshold = 10;

        private readonly ICqeBus _bus;
        private readonly IRandomSource _random;
        private readonly GachaTables _tables;

        /// <summary>稀有行的行下标池（按行序），保底改判只允许从这里取。</summary>
        private readonly int[] _rareIndices;

        private int _pityCounter;
        private GachaResult? _lastResult;

        public GachaHandler(ICqeBus bus, IRandomSource random, IBinaryTableLoader loader)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (random == null)
            {
                throw new ArgumentNullException(nameof(random));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _random = random;
            _tables = new GachaTables(loader);
            _rareIndices = BuildRareIndices(_tables);
            _pityCounter = 0;
            _lastResult = null;
        }

        /// <summary>最近一次抽卡结果（演示/UI 读取；未抽过或上一次被拒绝时为 null）。</summary>
        public GachaResult? LastResult
        {
            get { return _lastResult; }
        }

        /// <summary>当前保底计数（0..9，第 10 次未出稀有则下次强制）。</summary>
        public int PityCounter
        {
            get { return _pityCounter; }
        }

        /// <summary>注册抽卡命令处理器（构造里不自动注册，装配层负责调用）。</summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<DrawGachaCommand>(this);
        }

        /// <summary>
        /// 抽卡：次数非法或余额不足 → 静默拒绝（连随机都不取）；
        /// 否则严格按"先扣费、再逐抽发货并广播、最后落本次结果"执行。
        /// </summary>
        public void Handle(in DrawGachaCommand command)
        {
            int count = command.Count;
            if (count != SingleDrawCount && count != TenDrawCount)
            {
                // Phase 1 没有"抽 N 次"的通用换算：非 1/10 一律拒绝，不查余额、不取随机。
                return;
            }

            int cost = count == SingleDrawCount ? SingleDrawCost : TenDrawCost;
            InventorySnapshot inventory = _bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold));
            if (inventory.Count < cost)
            {
                // 金币不足：静默拒绝，状态不变（保底计数与 LastResult 都保持原值）。
                return;
            }

            // 顺序契约：先扣钱。扣费指令在抽卡之前发出，因果日志才能按"花费 → 产出"回放。
            _bus.Send(new ConsumeItemCommand(MetaItemIds.Gold, cost, ConsumeReasonTagId));

            int pityBefore = _pityCounter;
            GachaResultItem[] items = new GachaResultItem[count];
            bool anyPityTriggered = false;
            for (int i = 0; i < count; i++)
            {
                bool pityTriggered;
                GachaResultItem item = DrawOne(out pityTriggered);
                items[i] = item;
                if (pityTriggered)
                {
                    anyPityTriggered = true;
                }

                _bus.Send(new GrantItemCommand(item.ItemId, 1, GrantReasonTagId));
                _bus.Publish(new GachaDrawnEvent(item.ItemId, item.IsRare, pityTriggered));
            }

            // 整次命令处理完才落结果：中途任何一步抛异常都不会留下"半次抽卡"的结果对象。
            _lastResult = new GachaResult(items, pityBefore, anyPityTriggered);
        }

        /// <summary>
        /// 抽一项：先按全池权重自然命中；命中稀有则计数归零，
        /// 命中非稀有则计数 +1，累计到阈值时本次改判为稀有池中随机一项（计数归零）。
        /// </summary>
        private GachaResultItem DrawOne(out bool pityTriggered)
        {
            pityTriggered = false;
            GachaEntryDef natural = DrawByWeight();
            if (natural.IsRare)
            {
                _pityCounter = 0;
                return new GachaResultItem(natural.ItemId, true);
            }

            _pityCounter++;
            if (_pityCounter < PityThreshold)
            {
                return new GachaResultItem(natural.ItemId, false);
            }

            // 第 10 次仍非稀有：丢弃本次自然结果，改判为稀有池中随机一项。
            _pityCounter = 0;
            pityTriggered = true;
            GachaEntryDef forced = _tables.Get(_rareIndices[_random.NextInt(0, _rareIndices.Length)]);
            return new GachaResultItem(forced.ItemId, true);
        }

        /// <summary>在 <c>[0, TotalWeight)</c> 上取整数，按行序累加权重落行。</summary>
        private GachaEntryDef DrawByWeight()
        {
            int roll = _random.NextInt(0, _tables.TotalWeight);
            int cumulative = 0;
            for (int i = 0; i < _tables.Count; i++)
            {
                GachaEntryDef entry = _tables.Get(i);
                cumulative += entry.Weight;
                if (roll < cumulative)
                {
                    return entry;
                }
            }

            // TotalWeight 已由子表保证等于权重和，走到这里说明表被绕过校验改了行，必须炸出来而不是静默返回错误行。
            throw new InvalidOperationException("抽卡池权重累计与 TotalWeight 不一致，roll = " + roll + "。");
        }

        /// <summary>构造期冻结稀有行下标；子表已保证至少一行稀有，序列非空。</summary>
        private static int[] BuildRareIndices(GachaTables tables)
        {
            int rareCount = 0;
            for (int i = 0; i < tables.Count; i++)
            {
                if (tables.Get(i).IsRare)
                {
                    rareCount++;
                }
            }

            int[] indices = new int[rareCount];
            int next = 0;
            for (int i = 0; i < tables.Count; i++)
            {
                if (tables.Get(i).IsRare)
                {
                    indices[next] = i;
                    next++;
                }
            }

            return indices;
        }
    }
}
