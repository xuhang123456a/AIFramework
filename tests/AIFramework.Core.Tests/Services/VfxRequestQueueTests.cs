using System;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// <see cref="VfxRequestQueue"/> 的行为契约验收：FIFO 顺序、定长溢出淘汰最老、非法请求静默拒绝、
    /// 清空后计数器口径、容量边界。
    /// <para>
    /// <b>为什么这些用例值得单独写</b>：队列是"业务请求 → 适配层播放"之间唯一的有界缓冲。
    /// 它一旦无界（内存只涨）、或者溢出时丢掉<u>最新</u>那条（玩家看不到刚触发的暴击特效），
    /// 都只会在真机高负载时才暴露；这里用纯内存用例把两条红线钉死。
    /// </para>
    /// </summary>
    public sealed class VfxRequestQueueTests
    {
        [Fact]
        public void TryEnqueue_ThenDequeue_ReturnsRequestsInFifoOrder()
        {
            var queue = new VfxRequestQueue(4);

            Assert.True(queue.TryEnqueue(new VfxRequest(1001, 1f, 2f, 1f, 0.5f)));
            Assert.True(queue.TryEnqueue(new VfxRequest(1002, 3f, 4f, 2f, 1f)));

            Assert.Equal(2, queue.Count);

            Assert.True(queue.TryDequeue(out VfxRequest first));
            Assert.Equal(1001, first.EffectId);
            Assert.Equal(1f, first.X);
            Assert.Equal(2f, first.Y);
            Assert.Equal(1f, first.Scale);
            Assert.Equal(0.5f, first.DurationSeconds);

            Assert.True(queue.TryDequeue(out VfxRequest second));
            Assert.Equal(1002, second.EffectId);
            Assert.Equal(0, queue.Count);
            Assert.False(queue.TryDequeue(out _));
        }

        [Fact]
        public void TryDequeue_OnEmptyQueue_ReturnsFalseAndDefaultRequest()
        {
            var queue = new VfxRequestQueue();

            Assert.False(queue.TryDequeue(out VfxRequest request));
            Assert.Equal(0, request.EffectId);
            Assert.Equal(0f, request.DurationSeconds);
            Assert.Equal(0, queue.TotalAccepted);
        }

        [Fact]
        public void TryEnqueue_BeyondCapacity_DropsOldestAndKeepsNewest()
        {
            var queue = new VfxRequestQueue(4);

            for (int i = 1; i <= 6; i++)
            {
                Assert.True(queue.TryEnqueue(new VfxRequest(i, 0f, 0f, 1f, 1f)));
            }

            // 定长：永远只有 4 条在里面；多出来的 2 条按"丢最老"处理，最新的 6 一定还在。
            Assert.Equal(4, queue.Capacity);
            Assert.Equal(4, queue.Count);
            Assert.True(queue.IsFull);
            Assert.Equal(2, queue.TotalDropped);
            Assert.Equal(6, queue.TotalAccepted);

            Assert.True(queue.TryDequeue(out VfxRequest oldest));
            Assert.Equal(3, oldest.EffectId);

            Assert.True(queue.TryDequeue(out VfxRequest next));
            Assert.Equal(4, next.EffectId);
        }

        [Fact]
        public void TryEnqueue_InvalidRequests_AreRejectedSilently()
        {
            var queue = new VfxRequestQueue();

            // 三类契约级非法：ID 非正、时长为 0、缩放为 0；外加负值。
            Assert.False(queue.TryEnqueue(new VfxRequest(0, 0f, 0f, 1f, 1f)));
            Assert.False(queue.TryEnqueue(new VfxRequest(-5, 0f, 0f, 1f, 1f)));
            Assert.False(queue.TryEnqueue(new VfxRequest(1001, 0f, 0f, 1f, 0f)));
            Assert.False(queue.TryEnqueue(new VfxRequest(1001, 0f, 0f, 0f, 1f)));
            Assert.False(queue.TryEnqueue(new VfxRequest(1001, 0f, 0f, -2f, 1f)));

            Assert.Equal(0, queue.Count);
            Assert.Equal(0, queue.TotalAccepted);
            Assert.Equal(5, queue.TotalRejected);
        }

        [Fact]
        public void TryEnqueue_NaNOrInfiniteCoordinates_AreRejected()
        {
            var queue = new VfxRequestQueue();

            // NaN 会让 Transform 变成不可渲染的污染值，且引擎不会报错，必须在入口挡掉。
            Assert.False(queue.TryEnqueue(new VfxRequest(1001, float.NaN, 0f, 1f, 1f)));
            Assert.False(queue.TryEnqueue(new VfxRequest(1001, 0f, float.PositiveInfinity, 1f, 1f)));
            Assert.False(queue.TryEnqueue(new VfxRequest(1001, 0f, 0f, 1f, float.PositiveInfinity)));

            Assert.Equal(0, queue.Count);
            Assert.Equal(3, queue.TotalRejected);
        }

        [Fact]
        public void Clear_EmptiesBufferButKeepsLifetimeCounters()
        {
            var queue = new VfxRequestQueue(2);
            queue.TryEnqueue(new VfxRequest(1001, 0f, 0f, 1f, 1f));
            queue.TryEnqueue(new VfxRequest(0, 0f, 0f, 1f, 1f)); // 被拒
            queue.TryEnqueue(new VfxRequest(1002, 0f, 0f, 1f, 1f));

            queue.Clear();

            Assert.Equal(0, queue.Count);
            Assert.False(queue.TryDequeue(out _));

            // 累计计数是排查信息，清空缓冲不该把它抹掉。
            Assert.Equal(2, queue.TotalAccepted);
            Assert.Equal(1, queue.TotalRejected);

            Assert.True(queue.TryEnqueue(new VfxRequest(1003, 0f, 0f, 1f, 1f)));
            Assert.True(queue.TryDequeue(out VfxRequest afterClear));
            Assert.Equal(1003, afterClear.EffectId);
        }

        [Fact]
        public void TryPeek_DoesNotConsumeTheHead()
        {
            var queue = new VfxRequestQueue(2);
            queue.TryEnqueue(new VfxRequest(1001, 1f, 1f, 1f, 1f));

            Assert.True(queue.TryPeek(out VfxRequest peeked));
            Assert.Equal(1001, peeked.EffectId);
            Assert.Equal(1, queue.Count);

            Assert.True(queue.TryDequeue(out VfxRequest dequeued));
            Assert.Equal(1001, dequeued.EffectId);
            Assert.False(queue.TryPeek(out _));
        }

        [Fact]
        public void Constructor_NonPositiveCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VfxRequestQueue(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VfxRequestQueue(-1));
        }

        [Fact]
        public void DefaultCapacity_MatchesDocumentedValue()
        {
            var queue = new VfxRequestQueue();

            Assert.Equal(VfxRequestQueue.DefaultCapacity, queue.Capacity);
            Assert.Equal(64, queue.Capacity);
        }
    }
}
