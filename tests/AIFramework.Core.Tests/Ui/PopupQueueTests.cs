using System;
using AIFramework.Contracts.Ui;
using AIFramework.Core.Ui;
using Xunit;

namespace AIFramework.Core.Tests.Ui
{
    /// <summary>
    /// <see cref="PopupQueue"/> 行为契约：优先级大者先出、同优先级 FIFO、同 ID 幂等入队、
    /// 容量满时淘汰最低优先级队尾并计入 <c>DroppedCount</c>、<c>Remove</c> / <c>Clear</c> / <c>Contains</c>，
    /// 空队列出队返回 false 且输出 0，以及非法 capacity 拒绝。
    /// </summary>
    public sealed class PopupQueueTests
    {
        [Fact]
        public void TryDequeue_ReturnsHighestPriorityFirst()
        {
            IPopupQueue queue = new PopupQueue(8);
            queue.Enqueue(1, 10);
            queue.Enqueue(2, 30);
            queue.Enqueue(3, 20);

            Assert.True(queue.TryDequeue(out int first));
            Assert.True(queue.TryDequeue(out int second));
            Assert.True(queue.TryDequeue(out int third));

            Assert.Equal(2, first);
            Assert.Equal(3, second);
            Assert.Equal(1, third);
            Assert.False(queue.TryDequeue(out int none));
            Assert.Equal(0, none);
        }

        [Fact]
        public void TryDequeue_SamePriority_IsFirstInFirstOut()
        {
            PopupQueue queue = new PopupQueue(8);
            queue.Enqueue(1, 5);
            queue.Enqueue(2, 5);
            queue.Enqueue(3, 5);

            Assert.Equal(new[] { 1, 2, 3 }, DequeueAll(queue, 3));
        }

        [Fact]
        public void TryDequeue_InterleavedPriorities_KeepFifoPerPriority()
        {
            PopupQueue queue = new PopupQueue(8);
            queue.Enqueue(10, 1);
            queue.Enqueue(20, 9);
            queue.Enqueue(30, 1);
            queue.Enqueue(40, 9);

            // 先按优先级分组（9 组先出），组内保持入队顺序。
            Assert.Equal(new[] { 20, 40, 10, 30 }, DequeueAll(queue, 4));
        }

        [Fact]
        public void Enqueue_DuplicateId_KeepsSingleEntryAndIgnoresLaterPriority()
        {
            PopupQueue queue = new PopupQueue(8);

            queue.Enqueue(7, 1);
            queue.Enqueue(7, 99);

            Assert.Equal(1, queue.Count);
            Assert.True(queue.TryDequeue(out int popupId));
            Assert.Equal(7, popupId);
            Assert.False(queue.TryDequeue(out int none));
            Assert.Equal(0, none);
        }

        [Fact]
        public void Enqueue_WhenFull_DropsLowestPriorityTailAndCountsDrop()
        {
            PopupQueue queue = new PopupQueue(3);
            queue.Enqueue(1, 10);
            queue.Enqueue(2, 20);
            queue.Enqueue(3, 30);

            queue.Enqueue(4, 5);

            Assert.Equal(1, queue.DroppedCount);
            Assert.Equal(3, queue.Count);
            Assert.False(queue.Contains(1));
            Assert.True(queue.Contains(4));

            Assert.Equal(new[] { 3, 2, 4 }, DequeueAll(queue, 3));
        }

        [Fact]
        public void Enqueue_WhenFull_WithTiedLowestPriority_DropsNewestTailEntry()
        {
            PopupQueue queue = new PopupQueue(2);
            queue.Enqueue(1, 5);
            queue.Enqueue(2, 5);

            // 队尾是"最低优先级里最晚入队"的那项：2 先被淘汰，1 保留。
            queue.Enqueue(3, 5);

            Assert.Equal(1, queue.DroppedCount);
            Assert.True(queue.Contains(1));
            Assert.False(queue.Contains(2));
            Assert.True(queue.Contains(3));
            Assert.Equal(new[] { 1, 3 }, DequeueAll(queue, 2));
        }

        [Fact]
        public void Enqueue_WhenFull_KeepsCapacityAndNeverThrows()
        {
            PopupQueue queue = new PopupQueue(2);
            for (int i = 1; i <= 5; i++)
            {
                queue.Enqueue(i, i * 10);
            }

            Assert.Equal(2, queue.Capacity);
            Assert.Equal(2, queue.Count);
            Assert.Equal(3, queue.DroppedCount);

            // 队列最终只留下两个优先级最高的项。
            Assert.Equal(new[] { 5, 4 }, DequeueAll(queue, 2));
        }

        [Fact]
        public void Enqueue_WhenFull_DuplicateIdDoesNotCountAsDrop()
        {
            PopupQueue queue = new PopupQueue(2);
            queue.Enqueue(7, 1);
            queue.Enqueue(8, 2);

            // 已满且是重复 ID：直接忽略，既不能淘汰别人，也不能改原优先级。
            queue.Enqueue(7, 99);

            Assert.Equal(2, queue.Count);
            Assert.Equal(0, queue.DroppedCount);
            Assert.Equal(new[] { 8, 7 }, DequeueAll(queue, 2));
        }

        [Fact]
        public void Enqueue_NegativePriority_IsAcceptedAndSortsLast()
        {
            PopupQueue queue = new PopupQueue(8);
            queue.Enqueue(1, -5);
            queue.Enqueue(2, 0);
            queue.Enqueue(3, -1);

            Assert.Equal(new[] { 2, 3, 1 }, DequeueAll(queue, 3));
        }

        [Fact]
        public void TryDequeue_OnEmptyQueue_ReturnsFalseAndZero()
        {
            PopupQueue queue = new PopupQueue(4);

            Assert.False(queue.TryDequeue(out int popupId));
            Assert.Equal(0, popupId);

            // 清空后再出队同样是 false + 0。
            queue.Enqueue(5, 1);
            Assert.True(queue.TryDequeue(out int taken));
            Assert.Equal(5, taken);

            Assert.False(queue.TryDequeue(out int again));
            Assert.Equal(0, again);
        }

        [Fact]
        public void Remove_ExistingPopup_RemovesOnlyThatEntry()
        {
            PopupQueue queue = new PopupQueue(8);
            queue.Enqueue(1, 1);
            queue.Enqueue(2, 5);
            queue.Enqueue(3, 3);

            queue.Remove(2);

            Assert.False(queue.Contains(2));
            Assert.Equal(2, queue.Count);
            Assert.Equal(new[] { 3, 1 }, DequeueAll(queue, 2));
        }

        [Fact]
        public void Remove_UnknownPopup_IsSilent()
        {
            PopupQueue queue = new PopupQueue(4);
            queue.Enqueue(1, 1);

            queue.Remove(999);

            Assert.Equal(1, queue.Count);
            Assert.Equal(0, queue.DroppedCount);
            Assert.Equal(new[] { 1 }, DequeueAll(queue, 1));
        }

        [Fact]
        public void Contains_TracksEnqueueDequeueAndRemove()
        {
            PopupQueue queue = new PopupQueue(4);

            Assert.False(queue.Contains(11));

            queue.Enqueue(11, 1);
            Assert.True(queue.Contains(11));

            Assert.True(queue.TryDequeue(out int taken));
            Assert.Equal(11, taken);
            Assert.False(queue.Contains(11));

            queue.Enqueue(12, 1);
            queue.Remove(12);
            Assert.False(queue.Contains(12));
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void Clear_EmptiesQueueAndResetsDroppedCount()
        {
            PopupQueue queue = new PopupQueue(1);
            queue.Enqueue(1, 1);
            queue.Enqueue(2, 2);
            Assert.Equal(1, queue.DroppedCount);

            queue.Clear();

            Assert.Equal(0, queue.Count);
            Assert.Equal(0, queue.DroppedCount);
            Assert.False(queue.Contains(1));
            Assert.False(queue.Contains(2));
            Assert.False(queue.TryDequeue(out int popupId));
            Assert.Equal(0, popupId);

            // 清空后照常可用。
            queue.Enqueue(3, 4);
            Assert.Equal(new[] { 3 }, DequeueAll(queue, 1));
        }

        [Fact]
        public void Constructor_WithNonPositiveCapacity_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PopupQueue(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PopupQueue(-1));
        }

        [Fact]
        public void Capacity_ReflectsConstructorArgumentAndDefaultsTo64()
        {
            Assert.Equal(64, new PopupQueue().Capacity);
            Assert.Equal(3, new PopupQueue(3).Capacity);
        }

        /// <summary>按出队顺序取指定条数，返回数组便于整体断言顺序。</summary>
        private static int[] DequeueAll(PopupQueue queue, int expectedCount)
        {
            int[] order = new int[expectedCount];
            for (int i = 0; i < expectedCount; i++)
            {
                Assert.True(queue.TryDequeue(out int popupId));
                order[i] = popupId;
            }

            Assert.Equal(0, queue.Count);
            return order;
        }
    }
}
