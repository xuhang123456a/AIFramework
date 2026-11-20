#nullable enable
using System;
using AIFramework.Contracts.Input;

namespace AIFramework.Adapters.Unity.Inputs
{
    /// <summary>
    /// 组合输入源：把多个 <see cref="IInputSource"/>（键鼠 + 触屏，将来可加手柄/回放）合并成一份输入。
    /// <para>
    /// <b>合并规则（锁死）</b>：边沿<b>取或</b>（任一设备按下即按下）、轴<b>取绝对值较大者</b>（保留符号，
    /// 避免"键盘松开摇杆还在推"被键盘的 0 覆盖）、<c>IsHeld</c> <b>取或</b>（任一设备按住即按住）。
    /// </para>
    /// <para>
    /// <b>为什么必须"同帧只 Poll 一次"</b>：<see cref="Poll"/> 对被合并的每个源都是<b>消费式</b>调用——
    /// 它会把各源的边沿清空。合并层自己不做缓存，所以调用方（<c>SceneInputRouter</c> 或测试）每帧只能调一次；
    /// 调两次第二次会得到空帧。这条约束写在契约里，不需要合并层再存一份"上一帧"。
    /// </para>
    /// <para>
    /// <b>为什么构造时克隆源数组</b>：调用方如果把数组传进来又复用同一数组去接别的设备，
    /// 合并层会静默跟着变。克隆一次（装配期，非热路径）把"这个组合有哪些源"钉成不可变事实。
    /// </para>
    /// </summary>
    public sealed class CompositeInputSource : IInputSource
    {
        /// <summary>被合并的源（构造时克隆，顺序不影响合并结果）。</summary>
        private readonly IInputSource[] _sources;

        /// <summary>创建一个组合输入源。</summary>
        /// <param name="sources">至少一个输入源；不允许 null 元素。</param>
        /// <exception cref="ArgumentNullException"><paramref name="sources"/> 为 null。</exception>
        /// <exception cref="ArgumentException"><paramref name="sources"/> 为空数组，或含 null 元素。</exception>
        public CompositeInputSource(params IInputSource[] sources)
        {
            if (sources == null)
            {
                throw new ArgumentNullException(nameof(sources));
            }

            if (sources.Length == 0)
            {
                throw new ArgumentException("组合输入源至少要有一个子源；没有输入时应该由装配层直接不挂路由。", nameof(sources));
            }

            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] == null)
                {
                    throw new ArgumentException(
                        "组合输入源的第 " + i + " 个子源为 null；装配期就应该发现，而不是每帧 Poll 时空引用。",
                        nameof(sources));
                }
            }

            _sources = (IInputSource[])sources.Clone();
        }

        /// <summary>被合并的源数量（诊断/断言用）。</summary>
        public int SourceCount
        {
            get { return _sources.Length; }
        }

        /// <summary>
        /// 依次拉取每个子源并合并：边沿取或、轴取绝对值较大者（保留符号）。
        /// </summary>
        /// <returns>合并后的本帧快照（所有子源都无输入时为空帧）。</returns>
        public InputFrame Poll()
        {
            int mask = 0;
            float horizontal = 0f;
            float vertical = 0f;

            for (int i = 0; i < _sources.Length; i++)
            {
                InputFrame frame = _sources[i].Poll();

                mask |= frame.PressedMask;

                if (Math.Abs(frame.Horizontal) > Math.Abs(horizontal))
                {
                    horizontal = frame.Horizontal;
                }

                if (Math.Abs(frame.Vertical) > Math.Abs(vertical))
                {
                    vertical = frame.Vertical;
                }
            }

            return new InputFrame(mask, horizontal, vertical);
        }

        /// <summary>任一子源报告按住即视为按住。</summary>
        /// <param name="action">待查询动作。</param>
        /// <returns>按住返回 <c>true</c>。</returns>
        public bool IsHeld(InputAction action)
        {
            for (int i = 0; i < _sources.Length; i++)
            {
                if (_sources[i].IsHeld(action))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
