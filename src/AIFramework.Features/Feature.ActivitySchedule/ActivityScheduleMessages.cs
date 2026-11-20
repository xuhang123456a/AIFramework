#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.ActivitySchedule
{
    /// <summary>
    /// 让切片"按当前时间重新计算活动开启集合"的命令：由适配层用可拨时间源每秒或切场景时驱动。
    /// <para>
    /// 幂等：同样的时间源读数重复发送不产生任何状态变化，也不重复广播事件。
    /// 本命令不携带时间参数——时间只认构造注入的 <c>ITimeProvider</c>（AIF003），
    /// 避免"调用方塞一个时间进来"绕过统一时间源。
    /// </para>
    /// </summary>
    public readonly struct ActivityTickCommand : ICommand
    {
    }

    /// <summary>
    /// 活动开启事实：某个活动进入"开启集合"的那一次 tick 广播一次（之后不重复）。
    /// <para>轮换把同组低优先级活动挤下线时，被挤掉的活动发 <see cref="ActivityClosedEvent"/>。</para>
    /// </summary>
    public readonly struct ActivityOpenedEvent : IEvent
    {
        public ActivityOpenedEvent(int activityId)
        {
            ActivityId = activityId;
        }

        /// <summary>本次被打开的活动 id。</summary>
        public int ActivityId { get; }
    }

    /// <summary>
    /// 活动关闭事实：某个活动离开"开启集合"的那一次 tick 广播一次（时间到期、或被同组更高优先级挤掉）。
    /// <para>它只表示"排期意义上关掉了"，不涉及活动进度结算与奖励补发。</para>
    /// </summary>
    public readonly struct ActivityClosedEvent : IEvent
    {
        public ActivityClosedEvent(int activityId)
        {
            ActivityId = activityId;
        }

        /// <summary>本次被关闭的活动 id。</summary>
        public int ActivityId { get; }
    }
}
