#nullable enable
namespace AIFramework.Contracts.Ui
{
    /// <summary>
    /// 弹窗队列端口（UI 表现层用）：排队与优先级由主板管，具体怎么弹由引擎适配层实现。
    /// <para>
    /// <b>为什么在 Contracts</b>：业务切片只负责"我要弹这个窗"，不关心弹窗栈、动效、遮罩；
    /// 把队列抽成端口后，Console 可以把它当队列测，Unity 可以接真 UI 栈。
    /// </para>
    /// </summary>
    public interface IPopupQueue
    {
        /// <summary>排队数（含正在展示的）。</summary>
        int Count { get; }

        /// <summary>入队；同一 popupId 重复入队按"只保留一个"处理（避免狂点弹出一堆）。</summary>
        void Enqueue(int popupId, int priority);

        /// <summary>取出优先级最高（priority 大者优先，同优先级先进先出）的弹窗；空队列返回 false。</summary>
        bool TryDequeue(out int popupId);

        /// <summary>是否在队列里（含正在展示的）。</summary>
        bool Contains(int popupId);

        /// <summary>移除指定弹窗（玩家已处理或条件失效）。</summary>
        void Remove(int popupId);

        /// <summary>清空队列（切场景/登出/热更时用）。</summary>
        void Clear();
    }
}
