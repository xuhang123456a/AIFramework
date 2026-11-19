#nullable enable
using System;
using System.Buffers.Binary;

namespace AIFramework.Core.Save
{
    /// <summary>一档存档的落盘包络：schema 版本号 + 原始负载字节。</summary>
    public readonly struct SaveEnvelope
    {
        public SaveEnvelope(int version, byte[] payload)
        {
            Version = version;
            Payload = payload ?? Array.Empty<byte>();
        }

        /// <summary>
        /// 负载的 <b>schema 版本</b>（由 <see cref="SaveMigrations"/> 的迁移链负责升级），
        /// <b>不是</b>包络自身的 <see cref="SaveService.FormatVersion"/>。
        /// </summary>
        public int Version { get; }

        /// <summary><see cref="Version"/> 的显式别名：需要表明"这是 schema 版本"时用，避免与包络格式混淆。</summary>
        public int SchemaVersion => Version;

        /// <summary>业务负载（序列化格式由调用方定）。</summary>
        public byte[] Payload { get; }
    }

    /// <summary>
    /// 存档服务：在 <see cref="Contracts.Ports.IStoragePort"/> 之上加一层"防半截写 / 防篡改 / 可迁移"的包络。
    /// <para>
    /// <b>为什么自己不序列化业务数据</b>：切片各自知道自己该怎么序列化，存档层只负责"整块字节安全落盘与读回"。
    /// 这样换 JSON/MessagePack/二进制都由业务决定，存档层不用跟着改。
    /// </para>
    /// <para>
    /// <b>为什么带 CRC 而不是只信 IStoragePort</b>：端口契约只保证"写整块"，但断电、磁盘满、云端半截响应都会让
    /// 文件长度对、内容坏。坏档必须能被识别成"没有存档"而不是解析出垃圾账本。
    /// </para>
    /// <para>
    /// <b>包络字段顺序（小端，共 <see cref="HeaderSize"/> 字节）</b>：
    /// <c>magic "AISV"(4) + 包络格式(4) + schema 版本(4) + 负载长度(4) + 负载 CRC32(4)</c>，其后紧跟负载。
    /// magic 与两个版本号都在 CRC 覆盖范围之外（CRC 只算负载），所以版本号被改不会被 CRC 发现，
    /// 必须由本类的版本判定与迁移链兜底。
    /// </para>
    /// </summary>
    public sealed class SaveService
    {
        /// <summary>包络格式版本（magic / 字段布局那一层，与负载 schema 版本无关）。</summary>
        public const int FormatVersion = 1;

        /// <summary>
        /// 当前存档 schema 版本（= <see cref="SaveMigrations.CurrentVersion"/>）。写新档请用它，
        /// 读档时低于它的自动迁移，高于它的一律拒绝。
        /// </summary>
        public const int CurrentVersion = SaveMigrations.CurrentVersion;

        /// <summary>文件头长度：magic(4) + 包络格式(4) + schema 版本(4) + 负载长度(4) + CRC(4)。</summary>
        public const int HeaderSize = 20;

        private static readonly byte[] Magic = { (byte)'A', (byte)'I', (byte)'S', (byte)'V' };

        private readonly Contracts.Ports.IStoragePort _storage;
        private readonly string _slotPrefix;

        /// <summary>构造。</summary>
        /// <param name="storage">落盘端口（Console 是文件系统，Unity 是 persistentDataPath，云端则由适配层换成远端）。</param>
        /// <param name="slotPrefix">key 前缀，避免与其它子系统抢占命名空间。</param>
        public SaveService(Contracts.Ports.IStoragePort storage, string slotPrefix = "save")
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _slotPrefix = string.IsNullOrEmpty(slotPrefix) ? "save" : slotPrefix;
        }

        /// <summary>
        /// 写档。<paramref name="envelope"/> 的 <see cref="SaveEnvelope.Version"/> 会按原值落到 header 上
        /// （<b>不强制改成 <see cref="CurrentVersion"/></b>：导入旧档、故障注入、跨版本测试都需要写非当前版本的文件），
        /// 但正常业务必须传 <see cref="CurrentVersion"/>，否则写进去的是"格式与版本号对不上"的档。
        /// </summary>
        /// <param name="slot">槽位号，必须 >= 0。</param>
        /// <param name="envelope">包络。</param>
        public void Save(int slot, in SaveEnvelope envelope)
        {
            ValidateSlot(slot);

            byte[] payload = envelope.Payload;
            int length = payload.Length;

            var buffer = new byte[HeaderSize + length];
            Magic.CopyTo(buffer, 0);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), FormatVersion);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), envelope.Version);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), Crc32.Compute(payload));

            payload.CopyTo(buffer, HeaderSize);

            _storage.Write(Key(slot), buffer);
        }

        /// <summary>
        /// 读取槽位。<b>坏档 / 缺档 / 版本不兼容的一律返回 false</b>（不抛异常），让上层走"新档"分支；
        /// 只有调用参数非法才抛。需要知道"为什么读不出来"时用带 <c>reason</c> 的重载。
        /// </summary>
        /// <param name="slot">槽位号。</param>
        /// <param name="envelope">读出的包络（成功时 <see cref="SaveEnvelope.Version"/> 恒为 <see cref="CurrentVersion"/>）。</param>
        public bool TryLoad(int slot, out SaveEnvelope envelope)
        {
            return TryLoad(slot, out envelope, out _);
        }

        /// <summary>
        /// 读取槽位并给出失败原因（成功时 <paramref name="reason"/> 为空串）。
        /// <para>
        /// <b>版本语义</b>：header 里的 schema 版本 &gt; <see cref="CurrentVersion"/> 视为"未来档"，
        /// 直接拒绝且<b>绝不半解析</b>（旧客户端读新档必须先升级）；&lt; <see cref="CurrentVersion"/> 则走
        /// <see cref="SaveMigrations.Migrate"/> 迁移链，迁移成功才返回，失败同样视为没有存档。
        /// </para>
        /// <para>
        /// 迁移只在内存里发生、<b>不回写磁盘</b>：读档不该产生写入副作用（云端/只读介质都会因此出错），
        /// 由上层下次 <see cref="Save"/> 时自然落成新版本。
        /// </para>
        /// </summary>
        /// <param name="slot">槽位号。</param>
        /// <param name="envelope">读出的包络（成功时 <see cref="SaveEnvelope.Version"/> 恒为 <see cref="CurrentVersion"/>）。</param>
        /// <param name="reason">失败原因；成功时为空串。</param>
        public bool TryLoad(int slot, out SaveEnvelope envelope, out string reason)
        {
            ValidateSlot(slot);

            envelope = default;

            byte[]? bytes = _storage.Read(Key(slot));
            if (bytes == null || bytes.Length < HeaderSize)
            {
                reason = "槽位没有存档，或文件长度不足文件头（" + HeaderSize + " 字节）。";
                return false;
            }

            if (bytes[0] != Magic[0] || bytes[1] != Magic[1] || bytes[2] != Magic[2] || bytes[3] != Magic[3])
            {
                reason = "magic 不匹配：不是 AISV 包络（可能是别的子系统的文件或纯垃圾）。";
                return false;
            }

            int envelopeFormat = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
            if (envelopeFormat != FormatVersion)
            {
                reason = "包络格式 v" + envelopeFormat + " 不受支持（当前 v" + FormatVersion + "）。";
                return false;
            }

            int schemaVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));

            if (length < 0 || bytes.Length != HeaderSize + length)
            {
                reason = "负载长度字段与实际文件长度不符（声明 " + length + "，实际 " + (bytes.Length - HeaderSize) + "）。";
                return false;
            }

            var payload = new byte[length];
            Array.Copy(bytes, HeaderSize, payload, 0, length);

            if (Crc32.Compute(payload) != expectedCrc)
            {
                reason = "CRC32 校验失败：负载已被截断或篡改。";
                return false;
            }

            // 版本判定放在 CRC 之后：坏档的版本字段本身不可信，先确认字节完整，再谈"这个版本能不能读"。
            if (schemaVersion > CurrentVersion)
            {
                reason = "存档 schema v" + schemaVersion + " 高于当前支持的 v" + CurrentVersion + "，拒绝半解析（请先升级客户端）。";
                return false;
            }

            if (schemaVersion < 1)
            {
                reason = "存档 schema v" + schemaVersion + " 非法（必须 >= 1）。";
                return false;
            }

            if (schemaVersion < CurrentVersion)
            {
                if (!SaveMigrations.Migrate(payload, schemaVersion, CurrentVersion, out byte[] migrated, out string migrationReason))
                {
                    reason = "存档 schema v" + schemaVersion + " → v" + CurrentVersion + " 迁移失败：" + migrationReason;
                    return false;
                }

                payload = migrated;
            }

            envelope = new SaveEnvelope(CurrentVersion, payload);
            reason = string.Empty;
            return true;
        }

        /// <summary>槽位是否存在<b>合法</b>存档（坏档 / 未来档算没有）。</summary>
        public bool HasSave(int slot)
        {
            return TryLoad(slot, out _);
        }

        /// <summary>删除槽位；不存在时不报错。</summary>
        public void Delete(int slot)
        {
            ValidateSlot(slot);
            _storage.Delete(Key(slot));
        }

        /// <summary>槽位 key（适配层只管把逻辑 key 映射成文件名）。</summary>
        public string Key(int slot)
        {
            return _slotPrefix + "." + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void ValidateSlot(int slot)
        {
            if (slot < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot, "槽位号不能为负数。");
            }
        }
    }

    /// <summary>
    /// CRC32（IEEE 802.3，多项式 0xEDB88320）。手写而非引包：纯逻辑库要能在 Unity/HybridCLR 里直接跑，
    /// 少一个包就少一个 AOT 元数据风险。
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
