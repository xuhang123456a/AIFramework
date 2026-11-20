#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// Console 的假错误上报通道：把批次按行打到输出流，便于无头验收"到底上报了什么"。
    /// <para>
    /// <b>为什么不直接接到日志</b>：错误上报与本地日志是两条链路（前者要脱敏、要限流、要能关），
    /// 混在一起会让"上报内容"不可断言。这里独立成类，测试与冒烟都能看到精确批次。
    /// </para>
    /// </summary>
    public sealed class ConsoleErrorUploader : IErrorUploader
    {
        private readonly IIOWriter _writer;

        /// <summary>构造。</summary>
        /// <param name="writer">输出流。</param>
        /// <param name="available">通道是否可用（false 时模拟离线）。</param>
        public ConsoleErrorUploader(IIOWriter writer, bool available = true)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            IsAvailable = available;
        }

        /// <summary>是否可用。</summary>
        public bool IsAvailable { get; set; }

        /// <summary>累计上传成功的批次。</summary>
        public List<ErrorReportEntry[]> Uploaded { get; } = new List<ErrorReportEntry[]>();

        /// <summary>上传一批：可用则记录并输出一行；不可用返回 false（聚合器会保留重试）。</summary>
        /// <param name="batch">批次。</param>
        /// <returns>成功返回 true。</returns>
        public bool TryUpload(in ErrorReportBatch batch)
        {
            if (!IsAvailable)
            {
                return false;
            }

            Uploaded.Add(batch.Entries);
            _writer.WriteLine("[ErrorReport] 上传 " + batch.Entries.Length + " 条："
                + (batch.Entries.Length > 0 ? batch.Entries[0].Code + " x" + batch.Entries[0].RepeatCount : "空"));
            return true;
        }
    }
}
