using System;
using AIFramework.Contracts.Replay;

namespace AIFramework.Core.Replay
{
    /// <summary>
    /// 内存内输入播放器：按"帧"而不是按"条"吐出录制内容。
    /// <para>
    /// 用法固定为三段式：每个逻辑帧开头调用 <see cref="TryNext"/> 直到返回 <c>false</c>，
    /// 把取出的输入重新提交给内核，帧末调用 <see cref="AdvanceFrame"/>。这样
    /// <see cref="TryNext"/> 只会返回<b>当前帧</b>的记录，跨帧顺序由播放器保证。
    /// </para>
    /// <para>
    /// <b>Cursor 语义</b>：下一条待读记录的下标（<c>0..Count</c>），因此
    /// <c>Remaining == Count - Cursor</c> 恒成立；<see cref="Rewind"/> 直接把两者归零重放。
    /// </para>
    /// </summary>
    public sealed class ReplayPlayer : IReplaySource
    {
        private readonly ReplayRecorder _recorder;

        /// <summary>下一条待读记录的下标。</summary>
        private int _cursor;

        /// <summary>播放器当前帧号；<see cref="AdvanceFrame"/> 每次加一。</summary>
        private int _cursorFrame;

        /// <summary>绑定一个已完成的录制，从头开始播放。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="recorder"/> 为 <c>null</c>。</exception>
        public ReplayPlayer(ReplayRecorder recorder)
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            _recorder = recorder;
            _cursor = 0;
            _cursorFrame = 0;
        }

        /// <summary>下一条待读记录的下标；等于 <see cref="ReplayRecorder.Count"/> 表示已读完。</summary>
        public int Cursor
        {
            get { return _cursor; }
        }

        /// <summary>尚未读出的记录条数。</summary>
        public int Remaining
        {
            get { return _recorder.Count - _cursor; }
        }

        /// <summary>回到第 0 帧、第 0 条记录，可完整重放。</summary>
        public void Rewind()
        {
            _cursor = 0;
            _cursorFrame = 0;
        }

        /// <summary>
        /// 取当前帧的下一条输入。
        /// <para>
        /// 只有"记录帧 == 播放器游标帧 == 调用方 <paramref name="frame"/>"三者一致时才返回 <c>true</c>；
        /// 否则返回 <c>false</c> 且不移动游标。录制必须按帧号非递减写入，且从第 0 帧开始。
        /// </para>
        /// </summary>
        /// <param name="frame">调用方当前逻辑帧号，通常直接传 <c>world.FrameId</c>。</param>
        /// <param name="intent">取出的记录；返回 <c>false</c> 时为 <c>default</c>。</param>
        public bool TryNext(int frame, out RecordedIntent intent)
        {
            if (_cursor >= _recorder.Count)
            {
                intent = default;
                return false;
            }

            RecordedIntent candidate = _recorder.Get(_cursor);
            if (candidate.Frame != _cursorFrame || candidate.Frame != frame)
            {
                intent = default;
                return false;
            }

            _cursor++;
            intent = candidate;
            return true;
        }

        /// <summary>帧末推进到下一逻辑帧；跨帧时不丢记录，下一帧继续按顺序读。</summary>
        public void AdvanceFrame()
        {
            _cursorFrame++;
        }
    }
}
