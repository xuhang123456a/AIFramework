using System;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Save;
using Xunit;

namespace AIFramework.Core.Tests.Save
{
    /// <summary>
    /// 旧档回归夹具：把**真实 v1 存档文件字节**冻结在测试里。
    /// <para>
    /// 为什么不用"现场构造 v1 再读"：那只能证明"当前代码写出来的 v1 能读"。
    /// 一旦 <c>InventorySaveCodec.Encode</c> 或包络写入被改坏，写与读会一起变形，测试照样绿；
    /// 冻结字节能真正锁住"历史玩家在 v1 时代存下的档，升级后仍然读得出来"。
    /// </para>
    /// <para>
    /// 夹具内容（2026-11-20 生成，schemaVersion=1）：背包 3 种道具 1001×5 / 2001×2 / 9001×1，共 48 字节。
    /// 未来加 v3 时，这个夹具必须仍然通过 v1→v2→v3 的完整迁移链。
    /// </para>
    /// </summary>
    public sealed class LegacySaveFixtureTests
    {
        /// <summary>冻结的 v1 存档（AISV 包络 + CRC32 + v1 背包负载）。</summary>
        private static readonly byte[] FrozenV1Save =
        {
            0x41, 0x49, 0x53, 0x56, // "AISV"
            0x01, 0x00, 0x00, 0x00, // 包络格式版本 = 1
            0x01, 0x00, 0x00, 0x00, // schemaVersion = 1（v1 业务负载）
            0x1C, 0x00, 0x00, 0x00, // payload 长度 = 28
            0x4D, 0x29, 0x7C, 0xF4, // CRC32
            0x03, 0x00, 0x00, 0x00, // count = 3
            0xE9, 0x03, 0x00, 0x00, // itemId = 1001
            0x05, 0x00, 0x00, 0x00, // count = 5
            0xD1, 0x07, 0x00, 0x00, // itemId = 2001
            0x02, 0x00, 0x00, 0x00, // count = 2
            0x29, 0x23, 0x00, 0x00, // itemId = 9001
            0x01, 0x00, 0x00, 0x00, // count = 1
        };

        [Fact]
        public void FrozenV1Save_HasExpectedEnvelopeShape()
        {
            Assert.Equal(48, FrozenV1Save.Length);
            Assert.Equal((byte)'A', FrozenV1Save[0]);
            Assert.Equal((byte)'I', FrozenV1Save[1]);
            Assert.Equal((byte)'S', FrozenV1Save[2]);
            Assert.Equal((byte)'V', FrozenV1Save[3]);

            int payloadLength = BitConverter.ToInt32(FrozenV1Save, 12);
            Assert.Equal(28, payloadLength);
            Assert.Equal(payloadLength, FrozenV1Save.Length - 20); // 20 = AISV 包络头
        }

        [Fact]
        public void FrozenV1Save_IsMigratedAndDecodableAfterUpgrade()
        {
            Assert.True(SaveFixture.TryLoad(0, FrozenV1Save, out SaveEnvelope envelope, out string reason), reason);

            Assert.Equal(SaveMigrations.CurrentVersion, envelope.SchemaVersion);

            Assert.True(InventorySaveCodec.TryDecodeAuto(envelope.Payload, out InventorySaveEntry[] entries));
            Assert.Equal(3, entries.Length);
            Assert.Equal(1001, entries[0].ItemId);
            Assert.Equal(5, entries[0].Count);
            Assert.Equal(2001, entries[1].ItemId);
            Assert.Equal(2, entries[1].Count);
            Assert.Equal(9001, entries[2].ItemId);
            Assert.Equal(1, entries[2].Count);
            Assert.All(entries, entry => Assert.Equal(0L, entry.UpdatedTicks));
        }

        [Fact]
        public void FrozenV1Save_WithCorruptedCrc_IsRejected()
        {
            byte[] damaged = (byte[])FrozenV1Save.Clone();
            damaged[damaged.Length - 1] ^= 0xFF;

            Assert.False(SaveFixture.TryLoad(0, damaged, out _, out string reason));
            Assert.Contains("CRC", reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void FrozenV1Save_WithFutureSchema_IsRejectedInsteadOfHalfParsed()
        {
            byte[] future = (byte[])FrozenV1Save.Clone();
            BitConverter.GetBytes(SaveMigrations.CurrentVersion + 1).CopyTo(future, 8);

            Assert.False(SaveFixture.TryLoad(0, future, out _, out string reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        /// <summary>把冻结字节灌进真实 <see cref="SaveService"/>，走完整读档路径（含 CRC 与迁移）。</summary>
        private static class SaveFixture
        {
            public static bool TryLoad(int slot, byte[] bytes, out SaveEnvelope envelope, out string reason)
            {
                var storage = new InMemoryStorage();
                var save = new SaveService(storage, "legacy");
                storage.Write(save.Key(slot), bytes);
                return save.TryLoad(slot, out envelope, out reason);
            }
        }

        private sealed class InMemoryStorage : IStoragePort
        {
            private readonly System.Collections.Generic.Dictionary<string, byte[]> _files =
                new System.Collections.Generic.Dictionary<string, byte[]>(StringComparer.Ordinal);

            public bool Exists(string key)
            {
                return _files.ContainsKey(key);
            }

            public byte[]? Read(string key)
            {
                return _files.TryGetValue(key, out byte[]? data) ? (byte[])data.Clone() : null;
            }

            public void Write(string key, byte[] data)
            {
                _files[key] = (byte[])data.Clone();
            }

            public void Delete(string key)
            {
                _files.Remove(key);
            }

            public string[] ListKeys()
            {
                var keys = new string[_files.Count];
                _files.Keys.CopyTo(keys, 0);
                Array.Sort(keys, StringComparer.Ordinal);
                return keys;
            }
        }
    }
}
