#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Contracts.Services
{
    /// <summary>错误上报级别：决定是否立即尝试上报、以及降级面板是否弹出。</summary>
    public enum ErrorReportLevel : byte
    {
        /// <summary>可恢复问题（例如单次网络失败），聚合后批量上报。</summary>
        Warning = 0,

        /// <summary>错误（功能失败），批量上报。</summary>
        Error = 1,

        /// <summary>致命（进程可能崩溃/存档可能损坏），应立即上报。</summary>
        Fatal = 2,
    }

    /// <summary>
    /// 一条错误上报记录（纯数据）。
    /// <para>
    /// <b>为什么不上报 <see cref="System.Exception"/></b>：异常对象不能跨线程/序列化，且带着堆栈会把上报体积撑大十倍；
    /// 上报端只该拿"错误码 + 摘要 + 级别 + 时间"，堆栈在本地日志里（真机由日志文件兜底）。
    /// </para>
    /// </summary>
    public readonly struct ErrorReportEntry
    {
        /// <summary>构造一条上报记录。</summary>
        /// <param name="code">稳定错误码（与 <c>ErrorCatalog</c> 的码可同源，也可用模块前缀，如 <c>network.timeout</c>）。</param>
        /// <param name="message">摘要（不含堆栈、不含玩家可见文案）。</param>
        /// <param name="level">级别。</param>
        /// <param name="occurredAtUnixSeconds">发生时刻（UTC 秒）。</param>
        /// <param name="repeatCount">同码同摘要的重复次数（聚合器会合并）。</param>
        public ErrorReportEntry(string code, string message, ErrorReportLevel level, long occurredAtUnixSeconds, int repeatCount)
        {
            Code = code;
            Message = message;
            Level = level;
            OccurredAtUnixSeconds = occurredAtUnixSeconds;
            RepeatCount = repeatCount;
        }

        /// <summary>稳定错误码。</summary>
        public string Code { get; }

        /// <summary>摘要。</summary>
        public string Message { get; }

        /// <summary>级别。</summary>
        public ErrorReportLevel Level { get; }

        /// <summary>发生时刻（UTC 秒）。</summary>
        public long OccurredAtUnixSeconds { get; }

        /// <summary>重复次数（聚合合并）。</summary>
        public int RepeatCount { get; }
    }

    /// <summary>上报批次（一次 flush 的内容；不可变数组）。</summary>
    public readonly struct ErrorReportBatch
    {
        /// <summary>构造批次。</summary>
        /// <param name="entries">批次内容。</param>
        public ErrorReportBatch(ErrorReportEntry[] entries)
        {
            Entries = entries ?? new ErrorReportEntry[0];
        }

        /// <summary>批次内容（只读副本）。</summary>
        public ErrorReportEntry[] Entries { get; }
    }

    /// <summary>上报通道：真实实现走 HTTP；Phase 1 由适配层假实现记录即可。</summary>
    public interface IErrorUploader
    {
        /// <summary>是否可用（离线/未配置时 false）。</summary>
        bool IsAvailable { get; }

        /// <summary>尝试上传一批；返回是否成功（失败由聚合器保留重试）。</summary>
        bool TryUpload(in ErrorReportBatch batch);
    }

    /// <summary>错误上报器（业务侧唯一入口）：聚合+去重+阈值上报。</summary>
    public interface IErrorReporter
    {
        /// <summary>上报一条错误；<c>code</c> 为 null/空时按 <c>"unknown"</c> 处理。</summary>
        void Report(ErrorReportLevel level, string code, string message);

        /// <summary>待上报（去重后）的条目数。</summary>
        int PendingCount { get; }

        /// <summary>累计上报次数（不论通道是否可用）。</summary>
        int ReportedCount { get; }

        /// <summary>尝试立即上报；返回是否真的发出。</summary>
        bool TryFlush();
    }

    /// <summary>错误上报广播事件（降级面板/提示可订阅；错误处理本身不依赖订阅方）。</summary>
    public readonly struct ErrorReportedEvent : IEvent
    {
        /// <summary>构造事件。</summary>
        /// <param name="entry">刚记录的错误。</param>
        public ErrorReportedEvent(in ErrorReportEntry entry)
        {
            Entry = entry;
        }

        /// <summary>刚记录的错误。</summary>
        public ErrorReportEntry Entry { get; }
    }
}
