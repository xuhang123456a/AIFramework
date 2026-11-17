using System.Threading;

namespace AIFramework.Core.Cqe
{
    /// <summary>
    /// 消息类型槽位号的非泛型计数器。
    /// <para>
    /// 必须放在非泛型静态类里：泛型类型的静态字段是"每个闭合类型各持一份"的，
    /// 若把计数器写进 <see cref="MessageTypeId{T}"/> 内部，每个 T 都会各自拿到 0，槽位必然撞车。
    /// </para>
    /// </summary>
    internal static class MessageTypeCounter
    {
        /// <summary>下一个待分配的槽位号；0 表示尚未分配过任何槽位。</summary>
        internal static int NextSlot;
    }

    /// <summary>
    /// 消息类型（命令 / 查询 / 事件 struct）在进程内的唯一槽位号，从 0 开始单调递增。
    /// <para>
    /// 首次访问 <see cref="Id"/> 时才分配，之后恒定不变。总线用该槽位号直接下标访问数组，
    /// 全程不查字典、不装箱、不用 <c>Reflection.Emit</c> 动态造类型。
    /// </para>
    /// </summary>
    /// <typeparam name="T">消息类型（<c>ICommand</c> / <c>IQuery&lt;TResult&gt;</c> / <c>IEvent</c> 的实现）。</typeparam>
    public static class MessageTypeId<T>
    {
        /// <summary>该消息类型的全局唯一槽位号，取值范围从 0 开始连续递增。</summary>
        public static readonly int Id = Interlocked.Increment(ref MessageTypeCounter.NextSlot) - 1;
    }
}
