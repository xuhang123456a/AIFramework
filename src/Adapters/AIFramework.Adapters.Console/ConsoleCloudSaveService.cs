#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台云存档服务：用**内存字典**模拟云端的参考实现，用来在无头环境验收
    /// <c>CloudSaveMerger</c> 的全部规则（上传 / 下载 / 冲突 / 版本单调性）。
    /// <para>
    /// <b>它不是空实现</b>：默认拒绝"版本号不递增"的写入、下载时返回 payload 副本、失败原因可读，
    /// 这三条行为与未来 HTTP 云端必须一致——否则测试在假云上绿、真云上红。
    /// </para>
    /// <para>
    /// <b>为什么校验键名</b>：真实云端一定会拒绝非法 key（路径穿越 / 超长 / 控制字符），
    /// 假云如果来者不拒，就等于把"key 拼错"这类错误留到真机才暴露。这里复用本地存档端口的
    /// 白名单 <c>[A-Za-z0-9._-]</c> 并额外拒绝 <c>..</c>。
    /// </para>
    /// <para>
    /// <b>日志是排空的环形缓冲</b>：只服务冒烟与排障（<see cref="DrainLog"/> 取走即清空），
    /// 不参与业务判定，因此允许在这里拼字符串（正式逻辑路径禁止动态拼接）。
    /// </para>
    /// <para>线程模型：非线程安全。由主线程单一存档服务串行调用。</para>
    /// </summary>
    public sealed class ConsoleCloudSaveService : ICloudSaveService
    {
        /// <summary>同步日志环形容量（最近 32 条）。</summary>
        public const int LogCapacity = 32;

        /// <summary>不可用原因：构造时被配置成离线，或后端未就绪。</summary>
        public const string ReasonUnavailable = "cloud unavailable";

        /// <summary>不可用原因：键名非法（空、白名单外字符、含 <c>..</c>）。</summary>
        public const string ReasonInvalidKey = "invalid key";

        /// <summary>不可用原因：待写版本号没有严格大于云端已有版本（拒绝回退/重复写）。</summary>
        public const string ReasonRevisionNotIncreasing = "revision not increasing";

        /// <summary>成功原因：已写入内存台账。</summary>
        public const string ReasonStored = "stored";

        /// <summary>内存台账：逻辑键 → 云端包络。</summary>
        private readonly Dictionary<string, CloudSaveEnvelope> _store =
            new Dictionary<string, CloudSaveEnvelope>(StringComparer.Ordinal);

        /// <summary>同步日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>是否配置为可用（构造后不可变，避免运行中"假装掉线/上线"造成不确定行为）。</summary>
        private readonly bool _available;

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置；满员时指向最旧的一条。</summary>
        private int _logNext;

        /// <summary>创建内存假云。</summary>
        /// <param name="available">是否可用；<c>false</c> 时所有操作都返回"不可用"，用于验收离线降级路径。</param>
        public ConsoleCloudSaveService(bool available = true)
        {
            _available = available;
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <summary>当前台账里的存档数量（诊断/冒烟用）。</summary>
        public int Count
        {
            get { return _store.Count; }
        }

        /// <inheritdoc />
        /// <remarks>返回的是 payload 副本：调用方改写它不得影响台账（云端数据在真实实现里也必须是不可变快照）。</remarks>
        public CloudSaveEnvelope? TryDownload(string key)
        {
            if (!_available)
            {
                Log("download " + Describe(key) + " -> unavailable");
                return null;
            }

            if (!IsValidKey(key))
            {
                Log("download " + Describe(key) + " -> invalid key");
                return null;
            }

            CloudSaveEnvelope stored;
            if (!_store.TryGetValue(key, out stored))
            {
                Log("download " + Describe(key) + " -> miss");
                return null;
            }

            Log("download " + Describe(key) + " -> hit rev=" + stored.Revision);
            return new CloudSaveEnvelope(stored.Key, Copy(stored.Payload), stored.Revision, stored.UpdatedAtUnixSeconds);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 严格单调：新版本号必须 <b>大于</b> 云端已有版本，相等也拒绝（相等意味着两端对"同一版内容"理解不同，
        /// 放行就等于允许后写者静默覆盖前者）。拒绝时返回 <see cref="CloudSaveSyncResult.Unavailable"/> 并保留云端原值。
        /// </remarks>
        public CloudSaveSyncOutcome Upload(in CloudSaveEnvelope envelope)
        {
            if (!_available)
            {
                Log("upload " + Describe(envelope.Key) + " -> unavailable");
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, 0, ReasonUnavailable);
            }

            if (!IsValidKey(envelope.Key))
            {
                Log("upload " + Describe(envelope.Key) + " -> invalid key");
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, 0, ReasonInvalidKey);
            }

            CloudSaveEnvelope existing;
            if (_store.TryGetValue(envelope.Key, out existing) && envelope.Revision <= existing.Revision)
            {
                Log("upload " + Describe(envelope.Key) + " rev=" + envelope.Revision
                    + " -> rejected (existing rev=" + existing.Revision + ")");
                return new CloudSaveSyncOutcome(
                    CloudSaveSyncResult.Unavailable, existing.Revision, ReasonRevisionNotIncreasing);
            }

            // 存入时复制 payload：包络构造不复制，若不在这里复制，调用方之后改写数组就会改坏"云端"。
            var stored = new CloudSaveEnvelope(
                envelope.Key, Copy(envelope.Payload), envelope.Revision, envelope.UpdatedAtUnixSeconds);
            _store[envelope.Key] = stored;

            Log("upload " + Describe(envelope.Key) + " rev=" + envelope.Revision + " -> stored");
            return new CloudSaveSyncOutcome(CloudSaveSyncResult.Uploaded, envelope.Revision, ReasonStored);
        }

        /// <summary>取走并清空同步日志（冒烟/排障用；取走即清空，避免同一批事件被重复打印）。</summary>
        /// <returns>按发生顺序排列的日志条目。</returns>
        public IReadOnlyList<string> DrainLog()
        {
            var entries = new List<string>(_logCount);
            int start = _logCount < LogCapacity ? 0 : _logNext;
            for (int i = 0; i < _logCount; i++)
            {
                int index = start + i;
                if (index >= LogCapacity)
                {
                    index -= LogCapacity;
                }

                entries.Add(_log[index]);
            }

            Array.Clear(_log, 0, _log.Length);
            _logCount = 0;
            _logNext = 0;
            return entries;
        }

        /// <summary>写入一条日志；满员时覆盖最旧的一条（仅诊断路径，允许分配）。</summary>
        private void Log(string entry)
        {
            _log[_logNext] = entry;

            _logNext++;
            if (_logNext == LogCapacity)
            {
                _logNext = 0;
            }

            if (_logCount < LogCapacity)
            {
                _logCount++;
            }
        }

        /// <summary>日志里的键描述：非法/空键也要可读，不能因为拼日志而抛异常。</summary>
        private static string Describe(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return "key=(empty)";
            }

            return "key=" + key;
        }

        /// <summary>
        /// 键名校验：与本地存档端口同一套白名单（<c>[A-Za-z0-9._-]</c>，拒绝 <c>..</c> 与 <c>"."</c>）。
        /// 真实云端一定会做这件事，假云不做就等于把错误推迟到真机。
        /// </summary>
        private static bool IsValidKey(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool allowed = (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '.'
                    || c == '_'
                    || c == '-';
                if (!allowed)
                {
                    return false;
                }
            }

            return key != "." && key.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        /// <summary>复制字节数组（云端快照必须与调用方缓冲隔离）。</summary>
        private static byte[] Copy(byte[] source)
        {
            var copy = new byte[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }
    }
}
