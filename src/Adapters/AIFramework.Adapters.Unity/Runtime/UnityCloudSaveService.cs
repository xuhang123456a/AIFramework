#nullable enable
using System;
using System.Globalization;
using System.Text;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 云存档服务：<b>不联网</b>，只实现"离线可用/断网演示"这条路径。
    /// <para>
    /// <b>为什么这里没有 HTTP</b>：真机上的云存档必须由登录态、重试队列、后台线程/协程等待一起构成，
    /// 一旦写进适配层，无头测试与断网演示就都用不上这份代码。因此 Phase 1 的边界是：
    /// 真正的 HTTP 云由后续独立的 <c>ICloudSaveService</c> 实现接线，本类只负责两件事——
    /// "明确不可用"（<see cref="IsAvailable"/> 为 <c>false</c> 时所有操作返回 <see cref="CloudSaveSyncResult.Unavailable"/>）
    /// 与"用本地文件模拟云端"（传入 <see cref="IStoragePort"/> 时走本地镜像）。
    /// </para>
    /// <para>
    /// <b>本地镜像就是云端语义的替身</b>：负载写 <c>&lt;key&gt;.bin</c>，版本号与时间写旁路 <c>&lt;key&gt;.rev</c>（UTF-8 文本，
    /// 首行 revision、次行 updatedAtUnixSeconds）。端口是"逻辑键 → 文件"的映射，
    /// 因此 <see cref="CreateOfflineSimulator"/> 用 <c>new UnityStoragePort("cloudsaves")</c> 作根，
    /// 落盘位置即 <c>&lt;persistentDataPath&gt;/cloudsaves/&lt;key&gt;.bin</c>（与 <c>.rev</c> 同目录）。
    /// </para>
    /// <para>
    /// <b>版本号单调性与 Console 假云一致</b>：新 revision 必须严格大于镜像里已有的值，否则返回
    /// <see cref="CloudSaveSyncResult.Unavailable"/>（原因 <see cref="ReasonRevisionNotIncreasing"/>）并保留原文件，
    /// 这样"离线演示"和"线上实现"对冲突处理的反馈完全相同。
    /// </para>
    /// <para>
    /// <b>绝不抛异常</b>：读写失败、key 非法、没有后端，全部降级为 <c>null</c> / <see cref="CloudSaveSyncResult.Unavailable"/>，
    /// 存档流程不允许被一次 IO 异常打断。
    /// </para>
    /// <para>线程模型：非线程安全。Unity 主线程串行调用（与 <see cref="UnityStoragePort"/> 一致）。</para>
    /// </summary>
    public sealed class UnityCloudSaveService : ICloudSaveService
    {
        /// <summary>本地镜像子目录名（<see cref="CreateOfflineSimulator"/> 用它作 <see cref="UnityStoragePort"/> 的根）。</summary>
        public const string OfflineMirrorDirectoryName = "cloudsaves";

        /// <summary>不可用原因：无后端（未注入 <see cref="IStoragePort"/>）或被配置为离线。</summary>
        public const string ReasonUnavailable = "cloud unavailable";

        /// <summary>不可用原因：键名非法（空、白名单外字符、含 <c>..</c>）。</summary>
        public const string ReasonInvalidKey = "invalid key";

        /// <summary>不可用原因：待写版本号没有严格大于镜像里已有的版本（拒绝回退/重复写）。</summary>
        public const string ReasonRevisionNotIncreasing = "revision not increasing";

        /// <summary>成功原因：已写入本地镜像。</summary>
        public const string ReasonStored = "stored in local cloud mirror";

        /// <summary>不可用原因：镜像读写失败（磁盘满/权限/端口异常）。</summary>
        public const string ReasonWriteFailed = "cloud mirror write failed";

        /// <summary>负载文件后缀（逻辑键 + 后缀即端口键）。</summary>
        private const string PayloadSuffix = ".bin";

        /// <summary>版本号旁路文件后缀。</summary>
        private const string RevisionSuffix = ".rev";

        /// <summary>本地镜像端口；<c>null</c> 表示没有后端（只能回答不可用）。</summary>
        private readonly IStoragePort? _localStorage;

        /// <summary>是否配置为可用（构造后不可变）。</summary>
        private readonly bool _available;

        /// <summary>创建"无后端"的云存档服务：没有真云也没有本地镜像，因此恒为不可用（显式桩，不假装能同步）。</summary>
        /// <param name="available">是否允许使用；无后端时即使传 <c>true</c>，<see cref="IsAvailable"/> 仍为 <c>false</c>。</param>
        public UnityCloudSaveService(bool available = true)
            : this(null, available)
        {
        }

        /// <summary>创建以本地文件模拟云端的服务（真机断网演示）。</summary>
        /// <param name="localStorage">镜像端口；建议用 <c>new UnityStoragePort("cloudsaves")</c> 作根，落盘即 <c>cloudsaves/&lt;key&gt;.bin</c>。</param>
        /// <param name="available">是否启用；<c>false</c> 时所有操作返回不可用（用于演示"断网且无缓存"）。</param>
        public UnityCloudSaveService(IStoragePort? localStorage, bool available = true)
        {
            _localStorage = localStorage;
            _available = available;
        }

        /// <inheritdoc />
        /// <remarks>只有"被启用"且"存在后端"时才为 <c>true</c>：没有后端的实例必须如实报告不可用，
        /// 否则上层会以为同步成功而跳过重试。</remarks>
        public bool IsAvailable
        {
            get { return _available && _localStorage != null; }
        }

        /// <summary>创建"本地文件模拟云端"的服务：镜像落在 <c>&lt;persistentDataPath&gt;/cloudsaves/</c>。</summary>
        /// <returns>可用（<see cref="IsAvailable"/> 为 <c>true</c>）的本地镜像服务。</returns>
        public static UnityCloudSaveService CreateOfflineSimulator()
        {
            return new UnityCloudSaveService(new UnityStoragePort(OfflineMirrorDirectoryName));
        }

        /// <inheritdoc />
        /// <remarks>镜像里没有该键、键非法、服务不可用或读失败，一律返回 <c>null</c>（调用方按"云端无此档"处理）。</remarks>
        public CloudSaveEnvelope? TryDownload(string key)
        {
            IStoragePort? storage = _localStorage;
            if (!_available || storage == null)
            {
                return null;
            }

            if (!IsValidKey(key))
            {
                return null;
            }

            try
            {
                byte[]? payload = storage.Read(PayloadKey(key));
                if (payload == null)
                {
                    return null;
                }

                long updatedAtUnixSeconds;
                long revision = ReadRevision(storage, key, out updatedAtUnixSeconds);

                return new CloudSaveEnvelope(key, payload, revision, updatedAtUnixSeconds);
            }
            catch (Exception)
            {
                // 读失败等价于"取不到远端"：存档流程按无远端继续，异常不许穿透。
                return null;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 写入顺序固定为"先负载、后版本旁路"：中途失败时镜像里最多是"旧版本号 + 新负载"，
        /// 下一次上传仍会被单调性校验挡住，不会出现"坏内容被当成新版本"。
        /// </remarks>
        public CloudSaveSyncOutcome Upload(in CloudSaveEnvelope envelope)
        {
            IStoragePort? storage = _localStorage;
            if (!_available || storage == null)
            {
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, 0, ReasonUnavailable);
            }

            if (!IsValidKey(envelope.Key))
            {
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, 0, ReasonInvalidKey);
            }

            try
            {
                long existingUpdatedAtUnixSeconds;
                long existingRevision = ReadRevision(storage, envelope.Key, out existingUpdatedAtUnixSeconds);

                if (storage.Exists(PayloadKey(envelope.Key)) && envelope.Revision <= existingRevision)
                {
                    return new CloudSaveSyncOutcome(
                        CloudSaveSyncResult.Unavailable, existingRevision, ReasonRevisionNotIncreasing);
                }

                storage.Write(PayloadKey(envelope.Key), envelope.Payload);
                storage.Write(
                    RevisionKey(envelope.Key),
                    EncodeRevision(envelope.Revision, envelope.UpdatedAtUnixSeconds));

                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Uploaded, envelope.Revision, ReasonStored);
            }
            catch (Exception)
            {
                return new CloudSaveSyncOutcome(CloudSaveSyncResult.Unavailable, 0, ReasonWriteFailed);
            }
        }

        /// <summary>负载文件的端口键。</summary>
        private static string PayloadKey(string key)
        {
            return key + PayloadSuffix;
        }

        /// <summary>版本号旁路文件的端口键。</summary>
        private static string RevisionKey(string key)
        {
            return key + RevisionSuffix;
        }

        /// <summary>把"版本号 + 更新时间"编码成两行 UTF-8 文本（首行 revision，次行 updatedAtUnixSeconds）。</summary>
        private static byte[] EncodeRevision(long revision, long updatedAtUnixSeconds)
        {
            string text = revision.ToString(CultureInfo.InvariantCulture)
                + "\n"
                + updatedAtUnixSeconds.ToString(CultureInfo.InvariantCulture);
            return Encoding.UTF8.GetBytes(text);
        }

        /// <summary>
        /// 读取旁路 <c>.rev</c>：缺失/为空/解析失败都返回 0（"镜像里没有可信版本号"），
        /// 因此首次上传（revision &gt; 0）总能通过单调性校验。
        /// </summary>
        private static long ReadRevision(IStoragePort storage, string key, out long updatedAtUnixSeconds)
        {
            updatedAtUnixSeconds = 0;

            byte[]? bytes = storage.Read(RevisionKey(key));
            if (bytes == null || bytes.Length == 0)
            {
                return 0;
            }

            string text = Encoding.UTF8.GetString(bytes);
            int lineBreak = text.IndexOf('\n');
            string revisionText = lineBreak >= 0 ? text.Substring(0, lineBreak) : text;
            string updatedAtText = lineBreak >= 0 ? text.Substring(lineBreak + 1) : string.Empty;

            long revision = 0;
            long.TryParse(revisionText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out revision);

            if (updatedAtText.Length > 0)
            {
                long.TryParse(updatedAtText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out updatedAtUnixSeconds);
            }

            return revision;
        }

        /// <summary>
        /// 键名校验：与本地存档端口同一套白名单（<c>[A-Za-z0-9._-]</c>，拒绝 <c>"."</c> 与含 <c>..</c> 的键）。
        /// <para>必须自己先挡：<see cref="UnityStoragePort"/> 对非法键是**抛异常**的，而云端口契约要求不抛。</para>
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
    }
}
