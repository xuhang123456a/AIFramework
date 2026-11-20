using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 错误聚合上报器验收。
    /// <para>
    /// 口径：上报链路上的失败绝不能反过来伤害业务（<c>Report</c> 不抛、通道异常被吞、失败批次保留重试），
    /// 同时"聚合"必须有可观测的次数，否则服务端无法判断是偶发还是刷屏。
    /// </para>
    /// </summary>
    public sealed class ErrorReportAggregatorTests
    {
        [Fact]
        public void Report_SingleEntry_PendingCountIsOne()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time);

            aggregator.Report(ErrorReportLevel.Error, "network.timeout", "请求超时");

            Assert.Equal(1, aggregator.PendingCount);
            Assert.Equal(1, aggregator.ReportedCount);
            Assert.Equal("network.timeout", aggregator.LastCode);
        }

        [Fact]
        public void Report_SameCodeAndMessageWithinWindow_MergesWithRepeatCount()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time, dedupWindowSeconds: 60);
            var sink = new RecordingBus();
            aggregator = new ErrorReportAggregator(time, sink, dedupWindowSeconds: 60);

            aggregator.Report(ErrorReportLevel.Warning, "net", "timeout");
            aggregator.Report(ErrorReportLevel.Warning, "net", "timeout");
            aggregator.Report(ErrorReportLevel.Warning, "net", "timeout");

            Assert.Equal(1, aggregator.PendingCount);      // 待发队列合并成 1 条
            Assert.Equal(3, aggregator.ReportedCount);     // 但事件逐次广播（表现层要"实时看到每次错误"）
            Assert.Equal(3, sink.Events.Count);
            Assert.Equal(3, sink.Events[2].Entry.RepeatCount);
        }

        [Fact]
        public void Report_SameCodeOutsideWindow_DoesNotMerge()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time, dedupWindowSeconds: 10);

            aggregator.Report(ErrorReportLevel.Warning, "net", "timeout");
            time.AdvanceSeconds(11);
            aggregator.Report(ErrorReportLevel.Warning, "net", "timeout");

            Assert.Equal(2, aggregator.PendingCount);
        }

        [Fact]
        public void Report_DifferentMessage_DoesNotMerge()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time);

            aggregator.Report(ErrorReportLevel.Warning, "net", "timeout");
            aggregator.Report(ErrorReportLevel.Warning, "net", "dns failed");

            Assert.Equal(2, aggregator.PendingCount);
        }

        [Fact]
        public void Report_NullOrEmptyCode_NormalizesToUnknown()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time);

            aggregator.Report(ErrorReportLevel.Warning, null!, "x");
            aggregator.Report(ErrorReportLevel.Warning, string.Empty, "y");

            Assert.Equal(2, aggregator.PendingCount);
            Assert.Equal(2, aggregator.ReportedCount);
            Assert.Equal("unknown", aggregator.LastCode);
        }

        [Fact]
        public void Report_LevelUpgradeWithinWindow_TakesMoreSevereLevel()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time);
            var sink = new RecordingBus();
            aggregator = new ErrorReportAggregator(time, sink);

            aggregator.Report(ErrorReportLevel.Warning, "io", "failed");
            aggregator.Report(ErrorReportLevel.Error, "io", "failed");

            Assert.Equal(1, aggregator.PendingCount);
            Assert.Equal(ErrorReportLevel.Error, sink.Events[sink.Events.Count - 1].Entry.Level);
        }

        [Fact]
        public void Report_Fatal_TriggersImmediateFlush()
        {
            var time = new FakeTime();
            var uploader = new RecordingUploader();
            var aggregator = new ErrorReportAggregator(time, uploader: uploader);

            aggregator.Report(ErrorReportLevel.Fatal, "crash", "boom");

            Assert.Equal(1, aggregator.AttemptCount);
            Assert.True(aggregator.SuccessCount >= 1);
            Assert.Equal(0, aggregator.PendingCount);
            Assert.Single(uploader.Batches);
        }

        [Fact]
        public void Report_NoUploaderConfigured_NeverThrows()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time);

            aggregator.Report(ErrorReportLevel.Fatal, "crash", "boom");

            Assert.Equal(1, aggregator.PendingCount);
            Assert.Equal(0, aggregator.SuccessCount);
        }

        [Fact]
        public void TryFlush_Success_ClearsPending()
        {
            var time = new FakeTime();
            var uploader = new RecordingUploader();
            var aggregator = new ErrorReportAggregator(time, uploader: uploader);
            aggregator.Report(ErrorReportLevel.Error, "a", "a");

            Assert.True(aggregator.TryFlush());
            Assert.Equal(0, aggregator.PendingCount);
            Assert.Equal(1, aggregator.SuccessCount);
        }

        [Fact]
        public void TryFlush_Unavailable_KeepsPending()
        {
            var time = new FakeTime();
            var uploader = new RecordingUploader { IsAvailable = false };
            var aggregator = new ErrorReportAggregator(time, uploader: uploader);
            aggregator.Report(ErrorReportLevel.Error, "a", "a");

            Assert.False(aggregator.TryFlush());
            Assert.Equal(1, aggregator.PendingCount);
            Assert.Equal(0, aggregator.SuccessCount);
        }

        [Fact]
        public void TryFlush_Failed_KeepsPending()
        {
            var time = new FakeTime();
            var uploader = new RecordingUploader { Succeed = false };
            var aggregator = new ErrorReportAggregator(time, uploader: uploader);
            aggregator.Report(ErrorReportLevel.Error, "a", "a");

            Assert.False(aggregator.TryFlush());
            Assert.Equal(1, aggregator.PendingCount);
        }

        [Fact]
        public void TryFlush_UploaderThrows_ReturnsFalseAndKeepsPending()
        {
            var time = new FakeTime();
            var uploader = new RecordingUploader { ThrowOnUpload = true };
            var aggregator = new ErrorReportAggregator(time, uploader: uploader);
            aggregator.Report(ErrorReportLevel.Error, "a", "a");

            Assert.False(aggregator.TryFlush());
            Assert.Equal(1, aggregator.PendingCount);
        }

        [Fact]
        public void TryFlush_EmptyQueue_ReturnsFalse()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time, uploader: new RecordingUploader());

            Assert.False(aggregator.TryFlush());
        }

        [Fact]
        public void Capacity_EvictsOldestWarning()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time, capacity: 2);
            aggregator.Report(ErrorReportLevel.Warning, "w1", "1");
            aggregator.Report(ErrorReportLevel.Warning, "w2", "2");

            aggregator.Report(ErrorReportLevel.Warning, "w3", "3");

            Assert.Equal(2, aggregator.PendingCount);
            Assert.Equal(1, aggregator.DroppedCount);
        }

        [Fact]
        public void Capacity_NeverEvictsFatal()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time, capacity: 1);
            aggregator.Report(ErrorReportLevel.Fatal, "f1", "1");
            aggregator.Report(ErrorReportLevel.Fatal, "f2", "2");
            aggregator.Report(ErrorReportLevel.Fatal, "f3", "3");

            // 全是 Fatal：容量是软上限，不丢致命错误。
            Assert.Equal(3, aggregator.PendingCount);
            Assert.Equal(0, aggregator.DroppedCount);
        }

        [Fact]
        public void NullTime_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ErrorReportAggregator(null!));
        }

        [Fact]
        public void CapacityBelowOne_ClampsToOne()
        {
            var aggregator = new ErrorReportAggregator(new FakeTime(), capacity: 0);
            Assert.Equal(1, aggregator.Capacity);
        }

        [Fact]
        public void Report_PublishesToBusForFallbackPanel()
        {
            var time = new FakeTime();
            var bus = new RecordingBus();
            var aggregator = new ErrorReportAggregator(time, bus);

            aggregator.Report(ErrorReportLevel.Error, "boot", "启动失败");

            ErrorReportedEvent evt = Assert.Single(bus.Events);
            Assert.Equal("boot", evt.Entry.Code);
            Assert.Equal(ErrorReportLevel.Error, evt.Entry.Level);
        }

        [Fact]
        public void Report_BusThrows_DoesNotPropagate()
        {
            var time = new FakeTime();
            var aggregator = new ErrorReportAggregator(time, new ThrowingBus());

            // 降级面板的订阅者抛异常，不能反过来把上报打成崩溃。
            aggregator.Report(ErrorReportLevel.Error, "x", "y");
            Assert.Equal(1, aggregator.PendingCount);
        }

        private sealed class FakeTime : Contracts.Ports.ITimeProvider
        {
            private double _elapsedSeconds;

            public double ElapsedSeconds
            {
                get { return _elapsedSeconds; }
            }

            public DateTime UtcNow
            {
                get { return new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc).AddSeconds(_elapsedSeconds); }
            }

            public void AdvanceSeconds(double seconds)
            {
                _elapsedSeconds += seconds;
            }
        }

        private sealed class RecordingUploader : IErrorUploader
        {
            public bool IsAvailable { get; set; } = true;

            public bool Succeed { get; set; } = true;

            public bool ThrowOnUpload { get; set; }

            public List<ErrorReportEntry[]> Batches { get; } = new List<ErrorReportEntry[]>();

            public bool TryUpload(in ErrorReportBatch batch)
            {
                if (ThrowOnUpload)
                {
                    throw new InvalidOperationException("upload failed");
                }

                if (!Succeed)
                {
                    return false;
                }

                Batches.Add(batch.Entries);
                return true;
            }
        }

        private sealed class RecordingBus : ICqeBus
        {
            private readonly List<IEventHandler<ErrorReportedEvent>> _handlers =
                new List<IEventHandler<ErrorReportedEvent>>();

            public List<ErrorReportedEvent> Events { get; } = new List<ErrorReportedEvent>();

            public void Publish<TEvent>(in TEvent evt)
                where TEvent : struct, IEvent
            {
                if (evt is ErrorReportedEvent reported)
                {
                    Events.Add(reported);
                    for (int i = 0; i < _handlers.Count; i++)
                    {
                        _handlers[i].Handle(in reported);
                    }
                }
            }

            public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
                where TEvent : struct, IEvent
            {
                if (handler is IEventHandler<ErrorReportedEvent> h)
                {
                    _handlers.Add(h);
                }
            }

            public void Send<TCommand>(in TCommand command)
                where TCommand : struct, ICommand
            {
            }

            public TResult Query<TQuery, TResult>(in TQuery query)
                where TQuery : struct, IQuery<TResult>
            {
                throw new NotImplementedException();
            }

            public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
                where TCommand : struct, ICommand
            {
            }

            public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
                where TQuery : struct, IQuery<TResult>
            {
            }
        }

        private sealed class ThrowingBus : ICqeBus
        {
            public void Publish<TEvent>(in TEvent evt)
                where TEvent : struct, IEvent
            {
                throw new InvalidOperationException("subscriber crashed");
            }

            public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
                where TEvent : struct, IEvent
            {
            }

            public void Send<TCommand>(in TCommand command)
                where TCommand : struct, ICommand
            {
            }

            public TResult Query<TQuery, TResult>(in TQuery query)
                where TQuery : struct, IQuery<TResult>
            {
                throw new NotImplementedException();
            }

            public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
                where TCommand : struct, ICommand
            {
            }

            public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
                where TQuery : struct, IQuery<TResult>
            {
            }
        }
    }
}
