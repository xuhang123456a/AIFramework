using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Replay;
using AIFramework.Features.ReplayBrowser;
using Xunit;

namespace AIFramework.Features.Tests.ReplayBrowser
{
    /// <summary>
    /// 回放列表：排序/分页/删除，以及"页码越界回退"这类 UI 常见边界。
    /// </summary>
    public sealed class ReplayBrowserHandlerTests
    {
        private static ReplayBrowserEntry Entry(string key, int level, int frames, long time)
        {
            return new ReplayBrowserEntry(key, level, frames, time, kernelType: 0, randomSeed: 7);
        }

        private static ReplayBrowserHandler CreateHandler(RecordingBus bus, params ReplayBrowserEntry[] entries)
        {
            return new ReplayBrowserHandler(bus, new FakeArchive(entries));
        }

        [Fact]
        public void Query_NewestFirst_OrdersByTimeDescending()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus,
                Entry("a", 1, 100, 1000),
                Entry("b", 1, 100, 3000),
                Entry("c", 1, 100, 2000));

            ReplayListPage page = handler.Query(new ReplayListQuery(0, 10));

            Assert.Equal(3, page.TotalCount);
            Assert.Equal("b", page.Entries[0].SlotKey);
            Assert.Equal("c", page.Entries[1].SlotKey);
            Assert.Equal("a", page.Entries[2].SlotKey);
        }

        [Fact]
        public void Query_OldestFirst_OrdersByTimeAscending()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus,
                Entry("a", 1, 100, 1000),
                Entry("b", 1, 100, 3000));

            ReplayListPage page = handler.Query(new ReplayListQuery(0, 10, ReplaySortOrder.OldestFirst));

            Assert.Equal("a", page.Entries[0].SlotKey);
            Assert.Equal("b", page.Entries[1].SlotKey);
        }

        [Fact]
        public void Query_LongestFirst_OrdersByFrameCount()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus,
                Entry("short", 1, 10, 1000),
                Entry("long", 1, 900, 1000),
                Entry("mid", 1, 100, 1000));

            ReplayListPage page = handler.Query(new ReplayListQuery(0, 10, ReplaySortOrder.LongestFirst));

            Assert.Equal("long", page.Entries[0].SlotKey);
            Assert.Equal("mid", page.Entries[1].SlotKey);
            Assert.Equal("short", page.Entries[2].SlotKey);
        }

        [Fact]
        public void Query_SameTime_SortsBySlotKey()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus,
                Entry("b", 1, 10, 1000),
                Entry("a", 1, 10, 1000));

            ReplayListPage page = handler.Query(new ReplayListQuery(0, 10));

            Assert.Equal("a", page.Entries[0].SlotKey);
            Assert.Equal("b", page.Entries[1].SlotKey);
        }

        [Fact]
        public void Query_Paginates()
        {
            var bus = new RecordingBus();
            var entries = new List<ReplayBrowserEntry>();
            for (int i = 0; i < 25; i++)
            {
                entries.Add(Entry("r" + i, 1, 10, 1000 + i));
            }

            ReplayBrowserHandler handler = CreateHandler(bus, entries.ToArray());

            ReplayListPage page0 = handler.Query(new ReplayListQuery(0, 10));
            ReplayListPage page2 = handler.Query(new ReplayListQuery(2, 10));

            Assert.Equal(10, page0.Entries.Length);
            Assert.Equal(3, page0.PageCount);
            Assert.Equal(5, page2.Entries.Length);
            Assert.False(page2.HasNext);
        }

        [Fact]
        public void Query_PageBeyondEnd_ClampsToLastPage()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus, Entry("a", 1, 10, 1000));

            ReplayListPage page = handler.Query(new ReplayListQuery(99, 10));

            Assert.Equal(0, page.Page);
            Assert.Single(page.Entries);
        }

        [Fact]
        public void Query_EmptyArchive_ReturnsZeroPages()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus);

            ReplayListPage page = handler.Query(new ReplayListQuery(0, 10));

            Assert.Empty(page.Entries);
            Assert.Equal(0, page.PageCount);
            Assert.False(page.HasNext);
            Assert.False(page.HasPrevious);
        }

        [Fact]
        public void Query_PageSizeCap_IsApplied()
        {
            var bus = new RecordingBus();
            var entries = new List<ReplayBrowserEntry>();
            for (int i = 0; i < 300; i++)
            {
                entries.Add(Entry("r" + i, 1, 10, 1000 + i));
            }

            ReplayBrowserHandler handler = CreateHandler(bus, entries.ToArray());

            ReplayListPage page = handler.Query(new ReplayListQuery(0, 10_000));

            Assert.Equal(ReplayBrowserHandler.MaxPageSize, page.Entries.Length);
            Assert.Equal(ReplayBrowserHandler.MaxPageSize, page.PageSize);
        }

        [Fact]
        public void Query_NegativePage_NormalizedToZero()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus, Entry("a", 1, 10, 1000));

            ReplayListPage page = handler.Query(new ReplayListQuery(-5, 10));

            Assert.Equal(0, page.Page);
        }

        [Fact]
        public void Query_PageSizeZero_NormalizedToTen()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus, Entry("a", 1, 10, 1000));

            ReplayListPage page = handler.Query(new ReplayListQuery(0, 0));

            Assert.Equal(10, page.PageSize);
        }

        [Fact]
        public void Delete_RemovesAndPublishesEvent()
        {
            var bus = new RecordingBus();
            var archive = new FakeArchive(Entry("a", 1, 10, 1000));
            var handler = new ReplayBrowserHandler(bus, archive);

            bool deleted = handler.Delete("a");

            Assert.True(deleted);
            Assert.Empty(archive.List());
            Assert.Single(bus.Events);
            Assert.True(bus.Events[0].Success);
        }

        [Fact]
        public void Delete_UnknownKey_PublishesFailureEvent()
        {
            var bus = new RecordingBus();
            var handler = new ReplayBrowserHandler(bus, new FakeArchive());

            Assert.False(handler.Delete("missing"));
            Assert.Single(bus.Events);
            Assert.False(bus.Events[0].Success);
        }

        [Fact]
        public void Delete_EmptyKey_ReturnsFalseAndPublishesNothing()
        {
            var bus = new RecordingBus();
            var handler = new ReplayBrowserHandler(bus, new FakeArchive());

            Assert.False(handler.Delete(string.Empty));
            Assert.Empty(bus.Events);
        }

        [Fact]
        public void Register_DispatchesQueryThroughBus()
        {
            var bus = new RecordingBus();
            var handler = new ReplayBrowserHandler(bus, new FakeArchive(Entry("a", 1, 10, 1000)));
            handler.Register(bus);

            ReplayListPage page = bus.Query<ReplayListQueryRequest, ReplayListPage>(new ReplayListQueryRequest(0, 10));

            Assert.Single(page.Entries);
        }

        [Fact]
        public void Register_DispatchesDeleteThroughBus()
        {
            var bus = new RecordingBus();
            var archive = new FakeArchive(Entry("a", 1, 10, 1000));
            var handler = new ReplayBrowserHandler(bus, archive);
            handler.Register(bus);

            bus.Send(new ReplayDeleteCommand("a"));

            Assert.Empty(archive.List());
        }

        [Fact]
        public void Constructor_NullBusOrPort_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new ReplayBrowserHandler(null!, new FakeArchive()));
            Assert.Throws<System.ArgumentNullException>(() => new ReplayBrowserHandler(new RecordingBus(), null!));
        }

        [Fact]
        public void Page_ReportLineHasFields()
        {
            var bus = new RecordingBus();
            ReplayBrowserHandler handler = CreateHandler(bus, Entry("a", 1, 10, 1000));

            string line = handler.Query(new ReplayListQuery(0, 10)).ToReportLine();

            Assert.StartsWith("[ReplayList] 页=0/1", line, System.StringComparison.Ordinal);
            Assert.Contains("总数=1", line);
        }

        [Fact]
        public void Page_NegativeValuesNormalized()
        {
            var page = new ReplayListPage(null!, -1, -1, 0);

            Assert.Empty(page.Entries);
            Assert.Equal(0, page.TotalCount);
            Assert.Equal(0, page.Page);
            Assert.Equal(10, page.PageSize);
        }

        [Fact]
        public void Entry_NegativeFrameCount_ClampedToZero()
        {
            Assert.Equal(0, new ReplayBrowserEntry(null!, 1, -5, 0, 0, 0).FrameCount);
            Assert.Equal(string.Empty, new ReplayBrowserEntry(null!, 1, 5, 0, 0, 0).SlotKey);
        }

        private sealed class FakeArchive : IReplayArchivePort
        {
            private readonly List<ReplayBrowserEntry> _entries;

            public FakeArchive(params ReplayBrowserEntry[] entries)
            {
                _entries = new List<ReplayBrowserEntry>(entries);
            }

            public ReplayBrowserEntry[] List()
            {
                return _entries.ToArray();
            }

            public bool Delete(string slotKey)
            {
                return _entries.RemoveAll(entry => entry.SlotKey == slotKey) > 0;
            }
        }

        private sealed class RecordingBus : ICqeBus
        {
            private readonly Dictionary<System.Type, object> _queryHandlers = new Dictionary<System.Type, object>();
            private readonly Dictionary<System.Type, System.Action<object>> _commandHandlers = new Dictionary<System.Type, System.Action<object>>();

            public List<ReplayDeletedEvent> Events { get; } = new List<ReplayDeletedEvent>();

            public void Send<TCommand>(in TCommand command)
                where TCommand : struct, ICommand
            {
                if (_commandHandlers.TryGetValue(typeof(TCommand), out System.Action<object>? handler))
                {
                    handler(command);
                }
            }

            public TResult Query<TQuery, TResult>(in TQuery query)
                where TQuery : struct, IQuery<TResult>
            {
                if (_queryHandlers.TryGetValue(typeof(TQuery), out object? handler))
                {
                    return ((IQueryHandler<TQuery, TResult>)handler).Handle(in query);
                }

                throw new System.InvalidOperationException("未注册查询处理器：" + typeof(TQuery).Name);
            }

            public void Publish<TEvent>(in TEvent evt)
                where TEvent : struct, IEvent
            {
                if (evt is ReplayDeletedEvent deleted)
                {
                    Events.Add(deleted);
                }
            }

            public void RegisterCommandHandler<TCommand>(ICommandHandler<TCommand> handler)
                where TCommand : struct, ICommand
            {
                _commandHandlers[typeof(TCommand)] = command => handler.Handle((TCommand)command);
            }

            public void RegisterQueryHandler<TQuery, TResult>(IQueryHandler<TQuery, TResult> handler)
                where TQuery : struct, IQuery<TResult>
            {
                _queryHandlers[typeof(TQuery)] = handler;
            }

            public void Subscribe<TEvent>(IEventHandler<TEvent> handler)
                where TEvent : struct, IEvent
            {
            }
        }
    }
}
