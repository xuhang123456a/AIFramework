using System;
using System.Buffers.Binary;

namespace AIFramework.Core.Save
{
    /// <summary>一档存档的落盘包络：业务版本号 + 原始负载字节。</summary>
    public readonly struct SaveEnvelope
    {
        public SaveEnvelope(int version, byte[] payload)
        {
            Version = version;
            Payload = payload ?? Array.Empty<byte>();
        }

        /// <summary>业务侧版本号（由各切片/主板自行递增，用于迁移）。</summary>
        public int Version { get; }

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
    /// </summary>
    public sealed class SaveService
    {
        /// <summary>包络格式版本（存档层自己用；业务版本另存一个字段）。</summary>
        public const int FormatVersion = 1;

        /// <summary>文件头长度：magic(4) + format(4) + businessVersion(4) + payloadLength(4) + crc(4)。</summary>
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

        /// <summary>存档槽位数量上限（负数非法，调用方应保证 0..N）。</summary>
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
        /// 读取槽位。<b>坏档 / 缺档 / 版本不符的一律返回 false</b>（不抛异常），让上层走"新档"分支；
        /// 只有调用参数非法才抛。
        /// </summary>
        public bool TryLoad(int slot, out SaveEnvelope envelope)
        {
            ValidateSlot(slot);

            byte[]? bytes = _storage.Read(Key(slot));
            if (bytes == null || bytes.Length < HeaderSize)
            {
                envelope = default;
                return false;
            }

            if (bytes[0] != Magic[0] || bytes[1] != Magic[1] || bytes[2] != Magic[2] || bytes[3] != Magic[3])
            {
                envelope = default;
                return false;
            }

            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != FormatVersion)
            {
                envelope = default;
                return false;
            }

            int businessVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));

            if (length < 0 || bytes.Length != HeaderSize + length)
            {
                envelope = default;
                return false;
            }

            var payload = new byte[length];
            Array.Copy(bytes, HeaderSize, payload, 0, length);

            if (Crc32.Compute(payload) != expectedCrc)
            {
                envelope = default;
                return false;
            }

            envelope = new SaveEnvelope(businessVersion, payload);
            return true;
        }

        /// <summary>槽位是否存在<b>合法</b>存档（坏档算没有）。</summary>
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
