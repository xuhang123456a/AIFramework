#nullable enable
using System;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 的假错误上报通道：把批次写进日志（真实实现应换成 HTTP 上报并带鉴权/脱敏/限流）。
    /// <para>
    /// <b>为什么 Phase 1 就落日志而不是空实现</b>：空实现会让"错误到底有没有进上报链路"完全不可观测；
    /// 落日志后，真机日志里能直接看到上报批次，接入真实服务时只需替换本类。
    /// </para>
    /// </summary>
    public sealed class UnityErrorUploader : IErrorUploader
    {
        private readonly ILogger _logger;

        /// <summary>是否可用（Phase 1 恒 true：写日志永远可写）。</summary>
        public bool IsAvailable
        {
            get { return true; }
        }

        /// <summary>累计上传批次数。</summary>
        public int UploadedCount { get; private set; }

        /// <summary>构造。</summary>
        /// <param name="logger">日志端口。</param>
        public UnityErrorUploader(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>记录批次（一次一行，避免刷屏）。</summary>
        /// <param name="batch">批次。</param>
        /// <returns>恒为 true（日志写失败也不影响业务）。</returns>
        public bool TryUpload(in ErrorReportBatch batch)
        {
            if (batch.Entries.Length == 0)
            {
                return true;
            }

            UploadedCount++;
            ErrorReportEntry first = batch.Entries[0];
            _logger.Warn("[ErrorReport] 批次 " + UploadedCount + "：共 " + batch.Entries.Length
                + " 条，首条=" + first.Code + " x" + first.RepeatCount + "（" + first.Level + "）");
            return true;
        }
    }
}
