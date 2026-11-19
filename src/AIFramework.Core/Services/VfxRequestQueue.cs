#nullable enable
using System;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 特效请求的合法性判定（<b>唯一一份</b>，Console / Unity 两个适配层共用）。
    /// <para>
    /// <b>为什么放在 Core 而不是各适配层自己写一遍</b>：一旦两边的"什么算非法"出现分叉，
    /// 就会出现"Console 单测拒绝、真机却播出一个 scale=0 的不可见特效"这类只在设备上复现的回归；
    /// 判定规则是纯函数，天然属于纯逻辑层。
    /// </para>
    /// <para>
    /// <b>为什么坐标也要判有限性</b>：契约本身只要求 <c>effectId &gt; 0</c> / <c>duration &gt; 0</c> /
    /// <c>scale &gt; 0</c>；但 <c>NaN</c> 坐标会让 Unity 的 Transform 变成不可渲染的污染值，
    /// 且不会报错——在入口挡掉比在真机上排查便宜得多。
    /// </para>
    /// </summary>
    public static class VfxRequestValidator
    {
        /// <summary>判定一条请求是否可被播放。</summary>
        /// <param name="request">待判定请求。</param>
        /// <returns>合法返回 <c>true</c>；任何一项越界返回 <c>false</c>（调用方应静默丢弃）。</returns>
        public static bool IsValid(in VfxRequest request)
        {
            if (request.EffectId <= 0)
            {
                return false;
            }

            if (!(request.DurationSeconds > 0f) || float.IsInfinity(request.DurationSeconds))
            {
                return false;
            }

            if (!(request.Scale > 0f) || float.IsInfinity(request.Scale))
            {
                return false;
            }

            return IsFinite(request.X) && IsFinite(request.Y);
        }

        /// <summary>是否为有限值（<c>NaN</c> 与正负无穷都不算）。</summary>
        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// 定长环形特效请求队列：装配层每帧把业务侧请求攒成一批，再统一喂给 <see cref="IVfxService"/>。
    /// <para>
    /// <b>它在框架里的位置</b>：不实现任何引擎能力，只做"有界缓冲 + 合法性过滤 + 溢出计数"，
    /// 因此 Console 宿主、Unity 宿主、无头测试可以共用同一套排队语义。
    /// </para>
    /// <para>
    /// <b>为什么必须有界</b>：演出脚本或连击特效很容易在一帧内提交几百条请求（暴击连锁、十连抽开箱）。
    /// 无界 <c>Queue&lt;T&gt;</c> 会在这种瞬间扩容并产生长命存活数组；定长环在构造后零分配、零扩容，
    /// 溢出时按"**丢弃最老的**"处理——最新的一次表现最该被播出来，与适配层"池满复用最老槽位"同语义。
    /// </para>
    /// <para>
    /// <b>计数器口径</b>：<see cref="TotalAccepted"/> / <see cref="TotalRejected"/> / <see cref="TotalDropped"/>
    /// 是<u>生命周期累计</u>诊断值，<see cref="Clear"/> 只清空缓冲，<b>不</b>回退计数——
    /// 否则"清空后到底丢过多少条"这个排查信息会凭空消失。
    /// </para>
    /// <para>线程模型：非线程安全。入队/出队与帧推进同线程。</para>
    /// </summary>
    public sealed class VfxRequestQueue
    {
        /// <summary>默认容量：一帧内几百条特效的上限场景里，64 条足够覆盖"一次爆发"。</summary>
        public const int DefaultCapacity = 64;

        /// <summary>定长缓冲；下标按 <see cref="Capacity"/> 取模。</summary>
        private readonly VfxRequest[] _buffer;

        /// <summary>下一个出队位置（队头）。</summary>
        private int _head;

        /// <summary>当前有效条数。</summary>
        private int _count;

        /// <summary>累计被接受的请求数（含之后被溢出淘汰的）。</summary>
        private int _accepted;

        /// <summary>累计因参数非法被拒绝的请求数。</summary>
        private int _rejected;

        /// <summary>累计因容量溢出被淘汰的请求数（淘汰的是队头最老那条）。</summary>
        private int _dropped;

        /// <summary>创建请求队列。</summary>
        /// <param name="capacity">缓冲容量，必须 ≥ 1。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 小于 1。</exception>
        public VfxRequestQueue(int capacity = DefaultCapacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "容量必须 ≥ 1，传 0 会让入队永远失败。");
            }

            _buffer = new VfxRequest[capacity];
        }

        /// <summary>缓冲容量（构造后不可变）。</summary>
        public int Capacity
        {
            get { return _buffer.Length; }
        }

        /// <summary>当前待播放的请求数。</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>是否已满（下一次入队会淘汰队头）。</summary>
        public bool IsFull
        {
            get { return _count == _buffer.Length; }
        }

        /// <summary>累计接受的请求数（生命周期）。</summary>
        public int TotalAccepted
        {
            get { return _accepted; }
        }

        /// <summary>累计因参数非法被拒绝的请求数（生命周期）。</summary>
        public int TotalRejected
        {
            get { return _rejected; }
        }

        /// <summary>累计因容量溢出被淘汰的请求数（生命周期）。</summary>
        public int TotalDropped
        {
            get { return _dropped; }
        }

        /// <summary>
        /// 入队一条请求。
        /// <para>参数非法时<u>静默拒绝</u>（返回 <c>false</c>，计入 <see cref="TotalRejected"/>，不影响已有队列）。</para>
        /// <para>队列已满时<u>丢弃队头最老那条</u>再接纳新请求（返回 <c>true</c>，计入 <see cref="TotalDropped"/>）。</para>
        /// </summary>
        /// <param name="request">待入队请求。</param>
        /// <returns>是否被接纳（非法请求返回 <c>false</c>）。</returns>
        public bool TryEnqueue(in VfxRequest request)
        {
            if (!VfxRequestValidator.IsValid(in request))
            {
                _rejected++;
                return false;
            }

            int length = _buffer.Length;

            if (_count == length)
            {
                // 满：覆盖队头（最老一条），队头前移——新请求成为队尾，最老一条消失。
                _buffer[_head] = request;
                _head++;
                if (_head == length)
                {
                    _head = 0;
                }

                _dropped++;
                _accepted++;
                return true;
            }

            int tail = _head + _count;
            if (tail >= length)
            {
                tail -= length;
            }

            _buffer[tail] = request;
            _count++;
            _accepted++;
            return true;
        }

        /// <summary>出队一条请求（FIFO）。</summary>
        /// <param name="request">出队结果；队列为空时为 <c>default</c>。</param>
        /// <returns>是否取到有效请求。</returns>
        public bool TryDequeue(out VfxRequest request)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _buffer[_head];

            // 清零槽位：结构体虽不含引用，但保留旧数值会让"空槽里躺着已播请求"在调试器里误导排查。
            _buffer[_head] = default;

            _head++;
            if (_head == _buffer.Length)
            {
                _head = 0;
            }

            _count--;
            return true;
        }

        /// <summary>看一眼队头但不弹出（调试/断言用）。</summary>
        /// <param name="request">队头请求；队列为空时为 <c>default</c>。</param>
        /// <returns>队列非空返回 <c>true</c>。</returns>
        public bool TryPeek(out VfxRequest request)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _buffer[_head];
            return true;
        }

        /// <summary>清空缓冲（<see cref="TotalAccepted"/> 等累计计数保持不变，见类注释）。</summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
        }
    }
}
