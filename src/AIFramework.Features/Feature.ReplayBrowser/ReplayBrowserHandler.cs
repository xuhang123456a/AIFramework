using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Replay;

namespace AIFramework.Features.ReplayBrowser
{
    /// <summary>回放列表排序方式。</summary>
    public enum ReplaySortOrder : byte
    {
        /// <summary>最新录制在前（默认）。</summary>
        NewestFirst = 0,

        /// <summary>最旧录制在前。</summary>
        OldestFirst = 1,

        /// <summary>帧数最多在前。</summary>
        LongestFirst = 2,
    }

    /// <summary>回放列表查询（分页 + 排序）。</summary>
    public readonly struct ReplayListQuery
    {
        /// <summary>构造查询。</summary>
        /// <param name="page">页码（0 基）。</param>
        /// <param name="pageSize">每页条数。</param>
        /// <param name="order">排序。</param>
        public ReplayListQuery(int page, int pageSize, ReplaySortOrder order = ReplaySortOrder.NewestFirst)
        {
            Page = page < 0 ? 0 : page;
            PageSize = pageSize <= 0 ? 10 : pageSize;
            Order = order;
        }

        /// <summary>页码（0 基）。</summary>
        public int Page { get; }

        /// <summary>每页条数。</summary>
        public int PageSize { get; }

        /// <summary>排序方式。</summary>
        public ReplaySortOrder Order { get; }
    }

    /// <summary>回放列表一页数据。</summary>
    public readonly struct ReplayListPage
    {
        /// <summary>构造一页。</summary>
        /// <param name="entries">本页条目。</param>
        /// <param name="totalCount">总条数。</param>
        /// <param name="page">页码（0 基）。</param>
        /// <param name="pageSize">每页条数。</param>
        public ReplayListPage(ReplayBrowserEntry[] entries, int totalCount, int page, int pageSize)
        {
            Entries = entries ?? new ReplayBrowserEntry[0];
            TotalCount = totalCount < 0 ? 0 : totalCount;
            Page = page < 0 ? 0 : page;
            PageSize = pageSize <= 0 ? 10 : pageSize;
        }

        /// <summary>本页条目。</summary>
        public ReplayBrowserEntry[] Entries { get; }

        /// <summary>总条数。</summary>
        public int TotalCount { get; }

        /// <summary>页码（0 基）。</summary>
        public int Page { get; }

        /// <summary>每页条数。</summary>
        public int PageSize { get; }

        /// <summary>总页数（无数据时为 0）。</summary>
        public int PageCount
        {
            get { return TotalCount == 0 ? 0 : ((TotalCount + PageSize - 1) / PageSize); }
        }

        /// <summary>是否还有下一页。</summary>
        public bool HasNext
        {
            get { return Page + 1 < PageCount; }
        }

        /// <summary>是否还有上一页。</summary>
        public bool HasPrevious
        {
            get { return Page > 0; }
        }

        /// <summary>一行报告（日志/验收用）。</summary>
        public string ToReportLine()
        {
            return "[ReplayList] 页=" + Page + "/" + PageCount + " 本页=" + Entries.Length
                + " 总数=" + TotalCount + " 下一页=" + (HasNext ? "有" : "无");
        }
    }

    /// <summary>删除回放请求。</summary>
    public readonly struct ReplayDeleteCommand : ICommand
    {
        /// <summary>构造删除请求。</summary>
        /// <param name="slotKey">存储逻辑键。</param>
        public ReplayDeleteCommand(string slotKey)
        {
            SlotKey = slotKey ?? string.Empty;
        }

        /// <summary>存储逻辑键。</summary>
        public string SlotKey { get; }
    }

    /// <summary>回放列表查询请求。</summary>
    public readonly struct ReplayListQueryRequest : IQuery<ReplayListPage>
    {
        /// <summary>构造查询请求。</summary>
        /// <param name="page">页码（0 基）。</param>
        /// <param name="pageSize">每页条数。</param>
        /// <param name="order">排序。</param>
        public ReplayListQueryRequest(int page, int pageSize, ReplaySortOrder order = ReplaySortOrder.NewestFirst)
        {
            Query = new ReplayListQuery(page, pageSize, order);
        }

        /// <summary>查询参数。</summary>
        public ReplayListQuery Query { get; }
    }

    /// <summary>回放被删除事件。</summary>
    public readonly struct ReplayDeletedEvent : IEvent
    {
        /// <summary>构造事件。</summary>
        /// <param name="slotKey">被删的键。</param>
        /// <param name="success">是否删除成功。</param>
        public ReplayDeletedEvent(string slotKey, bool success)
        {
            SlotKey = slotKey ?? string.Empty;
            Success = success;
        }

        /// <summary>被删的键。</summary>
        public string SlotKey { get; }

        /// <summary>是否删除成功。</summary>
        public bool Success { get; }
    }

    /// <summary>
    /// 回放列表切片：查询分页 + 删除。
    /// <para>
    /// <b>为什么排序/分页放在切片而不是端口</b>：存储端只保证"能列出、能删"，
    /// 排序与分页是 UI 语义（策划随时可能要求按关卡/时长筛），放切片里可无头测试、改起来不碰适配层。
    /// </para>
    /// <para>
    /// <b>为什么 PageSize 有上限</b>：防止 UI 传 int.MaxValue 导致一次分配过大；
    /// 上限 200，超出按 200 截断并照常返回（不报错，UI 场景不值得抛异常）。
    /// </para>
    /// </summary>
    public sealed class ReplayBrowserHandler
    {
        /// <summary>每页条数上限。</summary>
        public const int MaxPageSize = 200;

        private readonly ICqeBus _bus;
        private readonly IReplayArchivePort _archive;

        /// <summary>构造并注册到总线。</summary>
        /// <param name="bus">CQE 总线。</param>
        /// <param name="archive">回放存储端口。</param>
        public ReplayBrowserHandler(ICqeBus bus, IReplayArchivePort archive)
        {
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        }

        /// <summary>注册查询/命令处理器。</summary>
        /// <param name="bus">CQE 总线。</param>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterQueryHandler<ReplayListQueryRequest, ReplayListPage>(new ListQueryHandler(this));
            bus.RegisterCommandHandler<ReplayDeleteCommand>(new DeleteHandler(this));
        }

        /// <summary>查询一页（供 UI 直接调用，无需走总线）。</summary>
        /// <param name="query">查询参数。</param>
        /// <returns>一页数据。</returns>
        public ReplayListPage Query(in ReplayListQuery query)
        {
            ReplayBrowserEntry[] all = _archive.List() ?? new ReplayBrowserEntry[0];
            Sort(all, query.Order);

            int pageSize = query.PageSize;
            if (pageSize > MaxPageSize)
            {
                pageSize = MaxPageSize;
            }

            int page = query.Page;
            int pageCount = all.Length == 0 ? 0 : ((all.Length + pageSize - 1) / pageSize);
            if (pageCount > 0 && page >= pageCount)
            {
                page = pageCount - 1;
            }

            int start = page * pageSize;
            int count = 0;
            if (start < all.Length)
            {
                count = Math.Min(pageSize, all.Length - start);
            }

            var entries = new ReplayBrowserEntry[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = all[start + i];
            }

            return new ReplayListPage(entries, all.Length, page, pageSize);
        }

        /// <summary>删除一条回放（并发事件）。</summary>
        /// <param name="slotKey">存储逻辑键。</param>
        /// <returns>是否删掉。</returns>
        public bool Delete(string slotKey)
        {
            if (string.IsNullOrEmpty(slotKey))
            {
                return false;
            }

            bool deleted = _archive.Delete(slotKey);
            var evt = new ReplayDeletedEvent(slotKey, deleted);
            _bus.Publish(in evt);
            return deleted;
        }

        private static void Sort(ReplayBrowserEntry[] entries, ReplaySortOrder order)
        {
            switch (order)
            {
                case ReplaySortOrder.OldestFirst:
                    Array.Sort(entries, CompareOldest);
                    break;
                case ReplaySortOrder.LongestFirst:
                    Array.Sort(entries, CompareLongest);
                    break;
                default:
                    Array.Sort(entries, CompareNewest);
                    break;
            }
        }

        private static int CompareNewest(ReplayBrowserEntry left, ReplayBrowserEntry right)
        {
            int byTime = right.RecordedAtUnixSeconds.CompareTo(left.RecordedAtUnixSeconds);
            if (byTime != 0)
            {
                return byTime;
            }

            return string.CompareOrdinal(left.SlotKey, right.SlotKey);
        }

        private static int CompareOldest(ReplayBrowserEntry left, ReplayBrowserEntry right)
        {
            int byTime = left.RecordedAtUnixSeconds.CompareTo(right.RecordedAtUnixSeconds);
            if (byTime != 0)
            {
                return byTime;
            }

            return string.CompareOrdinal(left.SlotKey, right.SlotKey);
        }

        private static int CompareLongest(ReplayBrowserEntry left, ReplayBrowserEntry right)
        {
            int byFrames = right.FrameCount.CompareTo(left.FrameCount);
            if (byFrames != 0)
            {
                return byFrames;
            }

            return CompareNewest(left, right);
        }

        private sealed class ListQueryHandler : IQueryHandler<ReplayListQueryRequest, ReplayListPage>
        {
            private readonly ReplayBrowserHandler _owner;

            public ListQueryHandler(ReplayBrowserHandler owner)
            {
                _owner = owner;
            }

            public ReplayListPage Handle(in ReplayListQueryRequest query)
            {
                // 属性不是变量，不能直接 in 传递；先取局部副本（结构体拷贝，成本可忽略）。
                ReplayListQuery parameters = query.Query;
                return _owner.Query(in parameters);
            }
        }

        private sealed class DeleteHandler : ICommandHandler<ReplayDeleteCommand>
        {
            private readonly ReplayBrowserHandler _owner;

            public DeleteHandler(ReplayBrowserHandler owner)
            {
                _owner = owner;
            }

            public void Handle(in ReplayDeleteCommand command)
            {
                _owner.Delete(command.SlotKey);
            }
        }
    }
}
