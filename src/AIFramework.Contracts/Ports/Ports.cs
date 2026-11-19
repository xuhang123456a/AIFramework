#nullable enable
using System;

namespace AIFramework.Contracts.Ports
{
    /// <summary>时间源。禁止在逻辑层直接使用 <c>DateTime.Now</c>，一律经由此端口。</summary>
    public interface ITimeProvider
    {
        /// <summary>单调递增的秒数，可被测试快进；用于帧推进与超时判定。</summary>
        double ElapsedSeconds { get; }

        /// <summary>UTC 时间，仅用于局外业务（跨天刷新、活动周期）；测试可注入固定值。</summary>
        DateTime UtcNow { get; }
    }

    /// <summary>确定性随机源。禁止在逻辑层使用无参 <c>new Random()</c>。</summary>
    public interface IRandomSource
    {
        int NextInt(int minInclusive, int maxExclusive);
    }

    /// <summary>极简日志端口，实现方决定落地方式（控制台、文件、引擎日志）。</summary>
    public interface ILogger
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message);
    }

    /// <summary>
    /// 二进制配置表加载端口。返回原始字节，由表定义自行解析。
    /// 表不存在返回 <c>null</c>，调用方必须显式判定失败，禁止静默按空表继续。
    /// </summary>
    public interface IBinaryTableLoader
    {
        byte[]? Load(string tableName);
    }
}
