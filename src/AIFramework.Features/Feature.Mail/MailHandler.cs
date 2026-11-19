using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Mail
{
    /// <summary>
    /// 邮件业务规则：唯一允许改写邮箱状态的地方。
    /// <para>
    /// 构造时按子表建信箱（每条邮件 <c>Claimed = false</c>、<c>SentDayIndex = 今天 - SendDayOffset</c>）；
    /// 有效期 7 天，过期只在查询时实时判定，不落状态机字段。
    /// 跨模块交互一律经注入的 <see cref="ICqeBus"/>：发奖走 <see cref="GrantItemCommand"/>，红点走 <see cref="RedDotDeltaCommand"/>，禁止直接改背包。
    /// </para>
    /// </summary>
    public sealed class MailHandler : ICommandHandler<ClaimMailCommand>,
                                      ICommandHandler<DeleteMailCommand>,
                                      IQueryHandler<MailListQueryRequest, MailListSnapshot>
    {
        /// <summary>邮件有效期（天）：<c>今天 - 收件天序号 &gt;= 7</c> 即过期。</summary>
        private const int ExpireDays = 7;

        /// <summary>邮件领奖原因标签。</summary>
        private const int ClaimReasonTagId = 6301;

        /// <summary>天序号纪元：2020-01-01 00:00:00 UTC（与其它切片保持同一纪元，跨切片不可引用常量）。</summary>
        private static readonly DateTime DayEpoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly ICqeBus _bus;
        private readonly ITimeProvider _time;

        /// <summary>信箱：按 mailId 升序维护，删除后顺序不变，保证查询天然有序。</summary>
        private readonly List<MailSlot> _slots;

        public MailHandler(ICqeBus bus, ITimeProvider time, IBinaryTableLoader loader)
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

            MailTables tables = new MailTables(loader);
            int today = TodayDayIndex;
            _slots = new List<MailSlot>(tables.Count);
            for (int i = 0; i < tables.Count; i++)
            {
                MailDef def = tables.Get(i);
                _slots.Add(new MailSlot(def.MailId, def.RewardItemId, def.RewardCount, today - def.SendDayOffset));
            }

            SortByMailId();
        }

        /// <summary>
        /// 注册三条处理器（构造里不自动注册，装配层负责调用）。
        /// 构造期不发总线：那时装配层可能还没挂上发奖/红点处理器，发出去会直接抛"未注册"。
        /// </summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<ClaimMailCommand>(this);
            bus.RegisterCommandHandler<DeleteMailCommand>(this);
            bus.RegisterQueryHandler<MailListQueryRequest, MailListSnapshot>(this);
        }

        /// <summary>
        /// 红点点亮：必须由装配层在 <see cref="Register"/> 之后调用，按"未领取邮件数"一次性发一条增量。
        /// 未领取数为 0 时不发（避免无意义的总线调用）。
        /// </summary>
        public void SeedRedDot(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            int unread = CountUnclaimed();
            if (unread <= 0)
            {
                return;
            }

            bus.Send(new RedDotDeltaCommand(RedDotNodeIds.Mail, unread));
        }

        /// <summary>
        /// 领取附件：不存在 / 已领取 / 已过期一律静默拒绝（不抛异常、不发指令）。
        /// 合法领取顺序固定为：发奖指令 → 领取事件 → 红点 -1 → 落已领取标记。
        /// </summary>
        public void Handle(in ClaimMailCommand command)
        {
            int index = IndexOf(command.MailId);
            if (index < 0)
            {
                return;
            }

            MailSlot slot = _slots[index];
            if (slot.Claimed || IsExpired(slot, TodayDayIndex))
            {
                // 已领或过期：静默拒绝，不发奖励、不发事件、不动红点。
                return;
            }

            _bus.Send(new GrantItemCommand(slot.RewardItemId, slot.RewardCount, ClaimReasonTagId));
            _bus.Publish(new MailClaimedEvent(slot.MailId, slot.RewardItemId, slot.RewardCount));
            _bus.Send(new RedDotDeltaCommand(RedDotNodeIds.Mail, -1));
            slot.Claimed = true;
        }

        /// <summary>
        /// 删除邮件：不存在 → 静默拒绝；存在且未领取 → 直接从列表移除（不发奖励）并令红点 -1。
        /// </summary>
        public void Handle(in DeleteMailCommand command)
        {
            int index = IndexOf(command.MailId);
            if (index < 0)
            {
                return;
            }

            MailSlot slot = _slots[index];
            if (slot.Claimed)
            {
                // 规则只定义了"存在且未领取"这一条删除路径；已领取邮件的删除属未定义项，
                // 这里按静默拒绝处理（不删、不发红点），等产品确认后再放开。
                return;
            }

            _slots.RemoveAt(index);
            _bus.Send(new RedDotDeltaCommand(RedDotNodeIds.Mail, -1));
        }

        /// <summary>查询邮件列表：按 mailId 升序，过期状态在本次调用时刻实时判定。</summary>
        public MailListSnapshot Handle(in MailListQueryRequest query)
        {
            int today = TodayDayIndex;
            MailViewData[] mails = new MailViewData[_slots.Count];
            for (int i = 0; i < _slots.Count; i++)
            {
                MailSlot slot = _slots[i];
                mails[i] = new MailViewData(slot.MailId, ResolveState(slot, today), slot.RewardItemId, slot.RewardCount);
            }

            return new MailListSnapshot(mails);
        }

        /// <summary>单封邮件状态；邮件不存在抛 <see cref="ArgumentOutOfRangeException"/>（查询单封 ≠ 列表过滤）。</summary>
        public MailState GetState(int mailId)
        {
            int index = IndexOf(mailId);
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mailId), mailId, "邮件不存在：" + mailId + "。");
            }

            return ResolveState(_slots[index], TodayDayIndex);
        }

        /// <summary>今天的天序号：只走 <see cref="ITimeProvider"/>，测试可推进 mock 时间后重新断言。</summary>
        private int TodayDayIndex
        {
            get { return (int)(_time.UtcNow.Date - DayEpoch).TotalDays; }
        }

        /// <summary>
        /// 状态优先级：已领取 &gt; 过期 &gt; 未读。已领取邮件不会因时间推移翻成"过期"，
        /// 否则玩家会看到"自己领过的邮件变成失效"，与发奖事实冲突。
        /// </summary>
        private static MailState ResolveState(MailSlot slot, int today)
        {
            if (slot.Claimed)
            {
                return MailState.Claimed;
            }

            return IsExpired(slot, today) ? MailState.Expired : MailState.Unread;
        }

        private static bool IsExpired(MailSlot slot, int today)
        {
            return today - slot.SentDayIndex >= ExpireDays;
        }

        private int CountUnclaimed()
        {
            int count = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].Claimed)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>线性查找邮件下标；未找到返回 -1（信箱规模为个位数，不做字典）。</summary>
        private int IndexOf(int mailId)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].MailId == mailId)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>插入排序：只在构造期按子表建箱时调用一次，保证查询输出天然按 mailId 升序。</summary>
        private void SortByMailId()
        {
            for (int i = 1; i < _slots.Count; i++)
            {
                MailSlot current = _slots[i];
                int j = i - 1;
                while (j >= 0 && _slots[j].MailId > current.MailId)
                {
                    _slots[j + 1] = _slots[j];
                    j--;
                }

                _slots[j + 1] = current;
            }
        }

        /// <summary>
        /// 单封邮件的运行时状态：子表静态字段 + 收件天序号 + 已领取标记。
        /// 用引用类型而不是可变结构体，避免 <c>List&lt;T&gt;</c> 里改字段改到副本上。
        /// </summary>
        private sealed class MailSlot
        {
            internal MailSlot(int mailId, int rewardItemId, int rewardCount, int sentDayIndex)
            {
                MailId = mailId;
                RewardItemId = rewardItemId;
                RewardCount = rewardCount;
                SentDayIndex = sentDayIndex;
                Claimed = false;
            }

            internal int MailId { get; }

            internal int RewardItemId { get; }

            internal int RewardCount { get; }

            /// <summary>收件天序号：构造时按 <c>今天 - SendDayOffset</c> 固化。</summary>
            internal int SentDayIndex { get; }

            internal bool Claimed { get; set; }
        }
    }
}
