namespace AIFramework.Contracts.Cqe
{
    /// <summary>因果日志单条记录：全部为数值字段，热路径零字符串、零装箱。</summary>
    public readonly struct CausalRecord
    {
        public CausalRecord(long sequenceId, int messageTagId, int sourceTagId, int outcomeCode)
        {
            SequenceId = sequenceId;
            MessageTagId = messageTagId;
            SourceTagId = sourceTagId;
            OutcomeCode = outcomeCode;
        }

        /// <summary>全局单调递增序号，从 1 开始。</summary>
        public long SequenceId { get; }

        /// <summary>消息类型数值 ID（由注册表映射为可读名）。</summary>
        public int MessageTagId { get; }

        /// <summary>发起源数值 ID（切片、内核对列均可复用同一注册表）。</summary>
        public int SourceTagId { get; }

        /// <summary>执行结果码。</summary>
        public int OutcomeCode { get; }
    }

    /// <summary>执行结果码常量。</summary>
    public static class CausalOutcome
    {
        public const int Success = 0;
        public const int Rejected = 1;
        public const int Dropped = 2;
        public const int Overflow = 3;
    }

    /// <summary>
    /// 定长环形因果日志。热路径只追加数值记录，字符串映射仅发生在导出或断言阶段。
    /// </summary>
    public interface ICausalLog
    {
        /// <summary>环形缓冲的固定容量（构造后不变）；当前有效记录数 = <c>min(Count, Capacity)</c>。</summary>
        int Capacity { get; }

        /// <summary>已写入的总条数（可超过容量，表示发生过环形覆盖）。</summary>
        int Count { get; }

        /// <summary>追加一条记录。环形缓冲满时覆盖最旧记录，不扩容、不抛异常。</summary>
        void Record(int messageTagId, int sourceTagId, int outcomeCode);

        /// <summary>按逻辑顺序读取：索引 0 为当前缓冲中最旧的记录。</summary>
        CausalRecord this[int logicalIndex] { get; }

        void Clear();
    }

    /// <summary>数值 Tag 与可读名字的映射表，只用于日志导出与测试断言。</summary>
    public interface ICausalTagRegistry
    {
        /// <summary>注册名字并返回数值 ID；同名重复注册返回同一 ID。</summary>
        int Register(string name);

        /// <summary>按数值 ID 取回名字；未注册返回 <c>null</c>。</summary>
        string? Resolve(int tagId);
    }
}
