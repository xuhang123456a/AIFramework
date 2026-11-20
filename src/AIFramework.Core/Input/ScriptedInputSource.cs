#nullable enable
using System;
using AIFramework.Contracts.Input;

namespace AIFramework.Core.Input
{
    /// <summary>
    /// 脚本化输入源：纯逻辑实现，供无头测试、回放与演示脚本"按剧本喂输入"。
    /// <para>
    /// <b>为什么它必须存在（而不是让测试直接 <c>new KeyboardInputSource()</c>）</b>：
    /// <see cref="IInputSource"/> 的 Unity 实现全部依赖 <c>UnityEngine.Input</c>（AIF002 禁止进 Core），
    /// 而无头测试又必须能把"第几帧按了跳"这种剧本精确地喂给内核。把"可编程输入"做成 Core 里的纯逻辑源，
    /// 同一份剧本既能驱动 Console 宿主、也能驱动 Unity 场景（PlayMode 里注入给 <c>SceneInputRouter</c>），
    /// 且测试不依赖设备、不依赖帧率。
    /// </para>
    /// <para>
    /// <b>为什么是定长环形队列而不是 <c>Queue&lt;InputAction&gt;</c></b>：无界队列会在"每帧误按"的
    /// 压测脚本里无限扩容；定长环构造后零分配、零扩容，溢出时丢**最老**的一条——玩家刚按下的那一下
    /// 最该被响应，与 <c>VfxRequestQueue</c> 的"丢最老"口径一致。掉帧/单帧内多次按键的正常量级
    /// （≤ 十几个）远小于默认容量 16。
    /// </para>
    /// <para>
    /// <b>边沿与持续态为什么分开存</b>：<see cref="Press"/> 只影响"本帧按下"（<see cref="Poll"/> 后清空），
    /// <see cref="SetHeld"/> 只影响 <see cref="IsHeld"/>（跨帧保持）。两者互不清除，才能让
    /// "点一下跳"和"按住加速"在同一份剧本里互不干扰。
    /// </para>
    /// <para>线程模型：非线程安全。剧本写入与帧推进必须同线程（与真实输入回调同线程的前提一致）。</para>
    /// </summary>
    public sealed class ScriptedInputSource : IInputSource
    {
        /// <summary>
        /// 默认边沿队列容量。<b>为什么是 16</b>：正常人类一帧最多按 2~3 个动作；16 足够覆盖
        /// "一帧内切换设备 + 连点"的压测脚本，同时让"容量溢出"这条分支能被小容量用例低成本覆盖。
        /// </summary>
        public const int DefaultCapacity = 16;

        /// <summary>定长环形边沿队列；下标按 <see cref="Capacity"/> 取模。</summary>
        private readonly InputAction[] _edges;

        /// <summary>队头下标（最老的那条边沿）。</summary>
        private int _head;

        /// <summary>当前有效边沿条数。</summary>
        private int _count;

        /// <summary>累计因容量溢出被淘汰的边沿数（丢的是队头最老那条）。</summary>
        private int _dropped;

        /// <summary>累计因动作值非法而被拒绝的调用数（<see cref="Press"/> / <see cref="SetHeld"/>）。</summary>
        private int _rejected;

        /// <summary>按住状态的位掩码（<c>1 &lt;&lt; (int)InputAction</c>），跨帧保持。</summary>
        private int _heldMask;

        /// <summary>本帧待取走的横向轴（<see cref="Poll"/> 后归零）。</summary>
        private float _horizontal;

        /// <summary>本帧待取走的纵向轴（<see cref="Poll"/> 后归零）。</summary>
        private float _vertical;

        /// <summary>创建一个脚本化输入源。</summary>
        /// <param name="capacity">边沿队列容量，必须 ≥ 1。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 小于 1。</exception>
        public ScriptedInputSource(int capacity = DefaultCapacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    "边沿队列容量必须 ≥ 1，传 0 会让所有按下事件静默丢失。");
            }

            _edges = new InputAction[capacity];
        }

        /// <summary>边沿队列容量（构造后不可变）。</summary>
        public int Capacity
        {
            get { return _edges.Length; }
        }

        /// <summary>当前尚未被 <see cref="Poll"/> 取走的边沿条数（诊断/断言用）。</summary>
        public int PendingCount
        {
            get { return _count; }
        }

        /// <summary>累计因容量溢出被淘汰的边沿数（生命周期累计，不随 <see cref="Poll"/> 回退）。</summary>
        public int DroppedCount
        {
            get { return _dropped; }
        }

        /// <summary>
        /// 累计因动作值非法被拒绝的调用数（生命周期累计）。
        /// <para>非法动作包括负数与 ≥ <see cref="InputFrame.ActionCount"/> 的值；它们<b>不会</b>入队、
        /// <b>不会</b>改按住状态，也不会影响已有边沿。</para>
        /// </summary>
        public int RejectedCount
        {
            get { return _rejected; }
        }

        /// <summary>
        /// 压入一次"本帧按下"边沿。同一动作多次压入是安全的（<see cref="Poll"/> 的掩码天然去重）。
        /// <para>动作值非法时静默拒绝；队列已满时丢弃最老的一条再接纳本次。</para>
        /// </summary>
        /// <param name="action">要模拟按下的动作。</param>
        public void Press(InputAction action)
        {
            if (!IsValidAction(action))
            {
                _rejected++;
                return;
            }

            int length = _edges.Length;

            if (_count == length)
            {
                // 满：覆盖队头（最老一条），队头前移，新元素成为队尾。与 VfxRequestQueue 同语义。
                _edges[_head] = action;
                _head++;
                if (_head == length)
                {
                    _head = 0;
                }

                _dropped++;
                return;
            }

            int tail = _head + _count;
            if (tail >= length)
            {
                tail -= length;
            }

            _edges[tail] = action;
            _count++;
        }

        /// <summary>
        /// 设置本帧轴。<b>越界与 <c>NaN</c> 不在这里处理</b>——<see cref="InputFrame"/> 构造期统一归一化
        /// （夹到 <c>-1..1</c>、<c>NaN</c> 归零），输入源只负责搬运。
        /// </summary>
        /// <param name="horizontal">横向轴（-1 左 .. +1 右）。</param>
        /// <param name="vertical">纵向轴（-1 下 .. +1 上）。</param>
        public void SetAxis(float horizontal, float vertical)
        {
            _horizontal = horizontal;
            _vertical = vertical;
        }

        /// <summary>
        /// 设置某动作的"按住"状态（跨帧保持，<see cref="Poll"/> 不清除）。
        /// <para>动作值非法时静默忽略并计入 <see cref="RejectedCount"/>。</para>
        /// </summary>
        /// <param name="action">目标动作。</param>
        /// <param name="held">是否按住。</param>
        public void SetHeld(InputAction action, bool held)
        {
            if (!IsValidAction(action))
            {
                _rejected++;
                return;
            }

            int bit = 1 << (int)action;

            if (held)
            {
                _heldMask |= bit;
            }
            else
            {
                _heldMask &= ~bit;
            }
        }

        /// <summary>
        /// 取走本帧快照：汇总所有待处理边沿成掩码、带上本帧轴，然后把边沿队列与轴<b>一起清空</b>。
        /// <para>清空的实现只是把 <see cref="_count"/> 归零并把 <see cref="_head"/> 复位——槽位里的旧值
        /// 不再被引用，下一次 <see cref="Press"/> 会覆盖它，因此不需要逐项清零（避免给稳态路径加无谓的写）。</para>
        /// <para>轴在返回<b>之后</b>才归零：本次返回的快照必须带上本帧设置的值，下一帧从 0 重新累积。</para>
        /// </summary>
        /// <returns>本帧输入快照（边沿只出现一次；无输入时为空帧）。</returns>
        public InputFrame Poll()
        {
            int mask = 0;
            int length = _edges.Length;

            for (int i = 0; i < _count; i++)
            {
                int index = _head + i;
                if (index >= length)
                {
                    index -= length;
                }

                mask |= 1 << (int)_edges[index];
            }

            _count = 0;
            _head = 0;

            var frame = new InputFrame(mask, _horizontal, _vertical);

            _horizontal = 0f;
            _vertical = 0f;

            return frame;
        }

        /// <summary>查询脚本设置的按住状态（非法动作返回 <c>false</c>，不计入拒绝计数）。</summary>
        /// <param name="action">待查询动作。</param>
        /// <returns>按住返回 <c>true</c>。</returns>
        public bool IsHeld(InputAction action)
        {
            if (!IsValidAction(action))
            {
                return false;
            }

            return (_heldMask & (1 << (int)action)) != 0;
        }

        /// <summary>
        /// 动作值是否落在 <see cref="InputAction"/> 的合法区间。
        /// <para><b>为什么要判上界</b>：底层类型是 <c>byte</c>，<c>(InputAction)200</c> 能编译通过；
        /// <c>1 &lt;&lt; 200</c> 会被 C# 截断成 <c>1 &lt;&lt; 8</c>，既不越界也不报错，只会变成错误的位。
        /// 下界判断是防御性的（<c>byte</c> 枚举当前不会为负），保留它以免将来换底层类型时静默失效。</para>
        /// </summary>
        /// <param name="action">待判定动作。</param>
        /// <returns>合法返回 <c>true</c>。</returns>
        private static bool IsValidAction(InputAction action)
        {
            int index = (int)action;
            return index >= 0 && index < InputFrame.ActionCount;
        }
    }
}
