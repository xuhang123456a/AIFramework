#nullable enable
using System;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 离线校验台账：在拿到真正服务端之前，先把"客户端能自查的作弊形态"挡在本地。
    /// <para>
    /// <b>它在框架里的位置</b>：实现 <see cref="IMatchValidator"/>，因此对上层（结算流程、UI）
    /// 与未来的 HTTP/WebSocket 校验器完全等价；装配层换实现即可，调用方一行都不用改。
    /// </para>
    /// <para>
    /// <b>职责边界（三条，全部是"重放可发现"的硬事实）</b>：
    /// <list type="number">
    /// <item>重复提交：同一 <see cref="MatchSummary.StateFingerprint"/> 第二次进来即拒绝（防重放/刷分）。</item>
    /// <item>分数合理性：负分、或超过 <c>帧数 × 10</c> 的分数直接拒绝（本地粗筛，不是最终判定）。</item>
    /// <item>对局长度：帧数超过 <see cref="MaxFrames"/> 视为"非正常对局"（挂机脚本/改包）。</item>
    /// </list>
    /// 这里<b>不做</b>真正的反作弊：真正的判定必须由服务端重放内核完成，本地台账只是省一次网络往返。
    /// </para>
    /// <para>
    /// <b>零分配</b>：<see cref="Validate"/> 的整条路径只读写预分配数组，不建集合、不拼字符串
    /// （所有 reason 都是编译期字面量，运行时只引用驻留字符串），因此可以在帧内/结算热路径反复调用。
    /// 指纹表刻意用线性扫描而不是 <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>：
    /// 容量只有几百，线性扫描比哈希+装箱更省，且完全没有扩容与 GC 尖峰。
    /// </para>
    /// <para>线程模型：非线程安全。结算上报按单线程队列串行喂入（与 CQE 总线一致）。</para>
    /// </summary>
    public sealed class MatchValidationLedger : IMatchValidator
    {
        /// <summary>单局最大帧数（60 FPS 下约 2 分钟）。超过即视为异常对局。</summary>
        public const int MaxFrames = 7200;

        /// <summary>每帧允许的最高分（防止"1 帧打出天文数字"这类改包）。</summary>
        public const int MaxScorePerFrame = 10;

        /// <summary>默认指纹表容量：够覆盖一个会话内连续的对局，且远小于堆压力阈值。</summary>
        public const int DefaultCapacity = 256;

        /// <summary>拒绝原因：帧数超出上限。</summary>
        public const string ReasonFrameCountExceeded = "frame count exceeds limit";

        /// <summary>拒绝原因：帧数为负（构造错误或被篡改）。</summary>
        public const string ReasonNegativeFrameCount = "negative frame count";

        /// <summary>拒绝原因：负分。</summary>
        public const string ReasonNegativeScore = "negative score";

        /// <summary>拒绝原因：分数超出 <c>帧数 × <see cref="MaxScorePerFrame"/></c> 的合理上限。</summary>
        public const string ReasonScoreExceeded = "score exceeds plausible maximum";

        /// <summary>拒绝原因：状态指纹已提交过（重放/重复提交）。</summary>
        public const string ReasonDuplicate = "duplicate";

        /// <summary>已提交指纹的环形表；容量固定，满了覆盖最旧条目。</summary>
        private readonly long[] _fingerprints;

        /// <summary>当前有效条目数（不超过 <see cref="Capacity"/>）。</summary>
        private int _count;

        /// <summary>下一条写入位置；满员时它同时指向"最旧"的那条，覆盖即淘汰。</summary>
        private int _next;

        /// <summary>使用 <see cref="DefaultCapacity"/> 创建台账。</summary>
        public MatchValidationLedger()
            : this(DefaultCapacity)
        {
        }

        /// <summary>以指定容量创建台账。</summary>
        /// <param name="capacity">指纹表容量，必须大于 0；溢出时覆盖最旧条目。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> 小于等于 0。</exception>
        public MatchValidationLedger(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "Match validation ledger capacity must be greater than zero.");
            }

            _fingerprints = new long[capacity];
        }

        /// <summary>指纹表容量（定长，构造后不再变化）。</summary>
        public int Capacity
        {
            get { return _fingerprints.Length; }
        }

        /// <summary>当前已登记的有效指纹条数（不会超过 <see cref="Capacity"/>）。</summary>
        public int Count
        {
            get { return _count; }
        }

        /// <summary>
        /// 校验一局摘要。校验通过则**同时登记**指纹，因此同一指纹的第二次调用必然被拒绝。
        /// <para>顺序固定：先判对局长度（顺带给后面的乘法兜底），再判分数，最后判重复。</para>
        /// </summary>
        /// <param name="summary">只读摘要。</param>
        /// <returns>四态结果；本实现只会返回 <see cref="NetworkValidationResult.Accepted"/> 或 <see cref="NetworkValidationResult.Rejected"/>。</returns>
        public ValidationResponse Validate(in MatchSummary summary)
        {
            if (summary.FrameCount < 0)
            {
                return Reject(ReasonNegativeFrameCount);
            }

            // 先封顶再乘：MaxFrames * MaxScorePerFrame 远小于 int.MaxValue，不会溢出。
            if (summary.FrameCount > MaxFrames)
            {
                return Reject(ReasonFrameCountExceeded);
            }

            if (summary.Score < 0)
            {
                return Reject(ReasonNegativeScore);
            }

            if (summary.Score > summary.FrameCount * MaxScorePerFrame)
            {
                return Reject(ReasonScoreExceeded);
            }

            if (Contains(summary.StateFingerprint))
            {
                return Reject(ReasonDuplicate);
            }

            Remember(summary.StateFingerprint);
            return new ValidationResponse(NetworkValidationResult.Accepted, summary.Score, string.Empty);
        }

        /// <summary>清空台账（新会话/重连后调用；不参与热路径）。</summary>
        public void Reset()
        {
            Array.Clear(_fingerprints, 0, _fingerprints.Length);
            _count = 0;
            _next = 0;
        }

        /// <summary>线性扫描已登记指纹；容量上限决定它最多 256 次整数比较，无需哈希。</summary>
        private bool Contains(long stateFingerprint)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_fingerprints[i] == stateFingerprint)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>登记指纹；满员时覆盖最旧条目（环形推进），绝不扩容。</summary>
        private void Remember(long stateFingerprint)
        {
            _fingerprints[_next] = stateFingerprint;

            _next++;
            if (_next == _fingerprints.Length)
            {
                _next = 0;
            }

            if (_count < _fingerprints.Length)
            {
                _count++;
            }
        }

        /// <summary>构造一条拒绝响应；reason 必须是驻留字面量，保证零分配。</summary>
        private static ValidationResponse Reject(string reason)
        {
            return new ValidationResponse(NetworkValidationResult.Rejected, 0, reason);
        }
    }
}
