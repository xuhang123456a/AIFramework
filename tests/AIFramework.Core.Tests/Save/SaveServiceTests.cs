using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Save;
using Xunit;

namespace AIFramework.Core.Tests.Save
{
    /// <summary>内存版存储端口：只测存档层，不碰文件系统。</summary>
    internal sealed class InMemoryStorage : IStoragePort
    {
        private readonly Dictionary<string, byte[]> _items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public bool Exists(string key)
        {
            return _items.ContainsKey(key);
        }

        public byte[]? Read(string key)
        {
            return _items.TryGetValue(key, out byte[]? value) ? (byte[])value.Clone() : null;
        }

        public void Write(string key, byte[] data)
        {
            WriteCount++;
            _items[key] = (byte[])data.Clone();
        }

        public void Delete(string key)
        {
            _items.Remove(key);
        }

        public string[] ListKeys()
        {
            var keys = new List<string>(_items.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys.ToArray();
        }

        /// <summary>测试钩子：直接篡改磁盘内容，模拟坏档。</summary>
        internal void Tamper(string key, Action<byte[]> mutate)
        {
            byte[] bytes = _items[key];
            mutate(bytes);
        }

        internal byte[] Raw(string key)
        {
            return _items[key];
        }
    }

    public sealed class SaveServiceTests
    {
        private static SaveService CreateService(out InMemoryStorage storage)
        {
            storage = new InMemoryStorage();
            return new SaveService(storage);
        }

        [Fact]
        public void SaveThenLoad_RoundTripsPayloadAndVersion()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            byte[] payload = Encoding.UTF8.GetBytes("{\"gold\":42}");

            // T3-17：schema 版本高于 CurrentVersion 的档被视为"未来档"而拒绝读取，
            // 所以这里必须用当前版本（原来的 3 已不再合法）。
            service.Save(0, new SaveEnvelope(version: SaveService.CurrentVersion, payload: payload));
            bool loaded = service.TryLoad(0, out SaveEnvelope envelope);

            Assert.True(loaded);
            Assert.Equal(SaveService.CurrentVersion, envelope.Version);
            Assert.Equal(payload, envelope.Payload);
            Assert.Equal(1, storage.WriteCount);
        }

        [Fact]
        public void Save_EmptyPayload_RoundTrips()
        {
            SaveService service = CreateService(out _);

            // T3-17：v1 负载要过迁移链（空负载不是合法背包），空负载往返改用当前版本。
            service.Save(1, new SaveEnvelope(version: SaveService.CurrentVersion, payload: Array.Empty<byte>()));
            Assert.True(service.TryLoad(1, out SaveEnvelope envelope));
            Assert.Empty(envelope.Payload);
        }

        [Fact]
        public void TryLoad_MissingSlot_ReturnsFalse()
        {
            SaveService service = CreateService(out _);

            Assert.False(service.TryLoad(7, out SaveEnvelope envelope));
            Assert.Null(envelope.Payload);
            Assert.False(service.HasSave(7));
        }

        [Fact]
        public void TryLoad_CorruptedPayload_ReturnsFalse()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(2, Encoding.UTF8.GetBytes("hello")));

            // 篡改负载最后一个字节：长度仍合法，但 CRC 不再匹配。
            storage.Tamper(service.Key(0), bytes => bytes[bytes.Length - 1] ^= 0xFF);

            Assert.False(service.TryLoad(0, out _));
            Assert.False(service.HasSave(0));
        }

        [Fact]
        public void TryLoad_TruncatedFile_ReturnsFalse()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(1, Encoding.UTF8.GetBytes("12345")));

            byte[] raw = storage.Raw(service.Key(0));
            var truncated = new byte[raw.Length - 2];
            Array.Copy(raw, truncated, truncated.Length);
            storage.Write(service.Key(0), truncated);

            Assert.False(service.TryLoad(0, out _));
        }

        [Fact]
        public void TryLoad_TooShortForHeader_ReturnsFalse()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            storage.Write(service.Key(0), new byte[SaveService.HeaderSize - 1]);

            Assert.False(service.TryLoad(0, out _));
        }

        [Fact]
        public void TryLoad_WrongMagic_ReturnsFalse()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(1, Array.Empty<byte>()));
            storage.Tamper(service.Key(0), bytes => bytes[0] = (byte)'X');

            Assert.False(service.TryLoad(0, out _));
        }

        [Fact]
        public void TryLoad_UnknownFormatVersion_ReturnsFalse()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(1, Array.Empty<byte>()));
            storage.Tamper(service.Key(0), bytes => bytes[4] = 99);

            Assert.False(service.TryLoad(0, out _));
        }

        [Fact]
        public void Save_ReplacesExistingSlot()
        {
            SaveService service = CreateService(out _);
            service.Save(0, new SaveEnvelope(1, Encoding.UTF8.GetBytes("old")));
            service.Save(0, new SaveEnvelope(2, Encoding.UTF8.GetBytes("new")));

            Assert.True(service.TryLoad(0, out SaveEnvelope envelope));
            Assert.Equal(2, envelope.Version);
            Assert.Equal("new", Encoding.UTF8.GetString(envelope.Payload));
        }

        [Fact]
        public void Delete_RemovesSlot()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(1, Array.Empty<byte>()));
            service.Delete(0);

            Assert.False(service.HasSave(0));
            Assert.Empty(storage.ListKeys());
        }

        [Fact]
        public void Delete_MissingSlot_IsSilent()
        {
            SaveService service = CreateService(out _);
            service.Delete(5); // 不应抛异常

            Assert.False(service.HasSave(5));
        }

        [Fact]
        public void NegativeSlot_Throws()
        {
            SaveService service = CreateService(out _);

            Assert.Throws<ArgumentOutOfRangeException>(() => service.Save(-1, new SaveEnvelope(1, Array.Empty<byte>())));
            Assert.Throws<ArgumentOutOfRangeException>(() => service.TryLoad(-1, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => service.Delete(-1));
        }

        [Fact]
        public void Key_UsesSlotPrefix()
        {
            var service = new SaveService(new InMemoryStorage(), "profile");
            Assert.Equal("profile.3", service.Key(3));
        }

        [Fact]
        public void SlotsAreIndependent()
        {
            SaveService service = CreateService(out _);

            // T3-17：v1 的 "a" 不是合法背包负载，会被迁移链拒绝，这里统一用当前版本。
            service.Save(0, new SaveEnvelope(SaveService.CurrentVersion, Encoding.UTF8.GetBytes("a")));
            service.Save(1, new SaveEnvelope(2, Encoding.UTF8.GetBytes("b")));

            Assert.True(service.TryLoad(0, out SaveEnvelope first));
            Assert.True(service.TryLoad(1, out SaveEnvelope second));
            Assert.Equal("a", Encoding.UTF8.GetString(first.Payload));
            Assert.Equal("b", Encoding.UTF8.GetString(second.Payload));
        }
    }
}
