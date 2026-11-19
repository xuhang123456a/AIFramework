using System;
using System.Buffers.Binary;
using System.Text;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Save;
using Xunit;

namespace AIFramework.Core.Tests.Save
{
    /// <summary>
    /// T3-17 迁移链验收：v1 → v2 内容保持、幂等、坏 v1 必须整档失败（绝不产生半截数据）。
    /// </summary>
    public sealed class SaveMigrationsTests
    {
        private static InventoryEntrySnapshot[] SampleEntries()
        {
            return new[]
            {
                new InventoryEntrySnapshot(1, 1000),
                new InventoryEntrySnapshot(2001, 3)
            };
        }

        private static byte[] V1Payload()
        {
            return InventorySaveCodec.Encode(SampleEntries());
        }

        [Fact]
        public void TryMigrate_V1ToV2_PreservesItemIdAndCount_AndZeroesTicks()
        {
            bool ok = SaveMigrations.TryMigrate(V1Payload(), InventorySaveCodec.FormatVersion1, out byte[] migrated, out string reason);

            Assert.True(ok);
            Assert.Empty(reason);
            Assert.Equal(4 + 2 * InventorySaveCodec.EntrySizeV2, migrated.Length);

            Assert.True(InventorySaveCodec.TryDecode(migrated, InventorySaveCodec.FormatVersion2, out InventorySaveEntry[] v2));
            Assert.Equal(2, v2.Length);
            Assert.Equal(1, v2[0].ItemId);
            Assert.Equal(1000, v2[0].Count);
            Assert.Equal(0L, v2[0].UpdatedTicks);
            Assert.Equal(2001, v2[1].ItemId);
            Assert.Equal(3, v2[1].Count);
            Assert.Equal(0L, v2[1].UpdatedTicks);
        }

        [Fact]
        public void TryMigrate_V1ToV2_IsIdempotent()
        {
            byte[] v1 = V1Payload();

            Assert.True(SaveMigrations.TryMigrate(v1, InventorySaveCodec.FormatVersion1, out byte[] first, out _));
            Assert.True(SaveMigrations.TryMigrate(v1, InventorySaveCodec.FormatVersion1, out byte[] second, out _));

            Assert.Equal(first, second);
        }

        [Fact]
        public void TryMigrate_AlreadyCurrent_ReturnsPayloadUnchanged()
        {
            byte[] payload = InventorySaveCodec.EncodeV2(new[] { new InventorySaveEntry(5, 9, 12345) });

            Assert.True(SaveMigrations.TryMigrate(payload, SaveMigrations.CurrentVersion, out byte[] migrated, out string reason));

            Assert.Same(payload, migrated);
            Assert.Empty(reason);
        }

        [Fact]
        public void TryMigrate_UnknownSourceVersion_FailsWithReason()
        {
            Assert.False(SaveMigrations.TryMigrate(V1Payload(), fromVersion: 0, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.False(string.IsNullOrEmpty(reason));

            Assert.False(SaveMigrations.TryMigrate(V1Payload(), fromVersion: 3, out migrated, out reason));
            Assert.Empty(migrated);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Migrate_Downgrade_IsRejected()
        {
            byte[] payload = InventorySaveCodec.EncodeV2(new[] { new InventorySaveEntry(5, 9, 0) });

            Assert.False(SaveMigrations.Migrate(payload, fromVersion: 2, toVersion: 1, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.Contains("降级", reason);
        }

        [Fact]
        public void Migrate_ToFutureTargetVersion_IsRejected()
        {
            Assert.False(SaveMigrations.Migrate(V1Payload(), fromVersion: 1, toVersion: 3, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void TryMigrate_TruncatedV1_Fails()
        {
            byte[] v1 = V1Payload();
            var truncated = new byte[v1.Length - 1];
            Array.Copy(v1, truncated, truncated.Length);

            Assert.False(SaveMigrations.TryMigrate(truncated, InventorySaveCodec.FormatVersion1, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.Contains("合法背包", reason);
        }

        [Fact]
        public void TryMigrate_NegativeCountV1_Fails()
        {
            var payload = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), -1);

            Assert.False(SaveMigrations.TryMigrate(payload, InventorySaveCodec.FormatVersion1, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void TryMigrate_AbsurdCountV1_Fails()
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), InventorySaveCodec.MaxEntries + 1);

            Assert.False(SaveMigrations.TryMigrate(payload, InventorySaveCodec.FormatVersion1, out byte[] migrated, out _));
            Assert.Empty(migrated);
        }

        [Fact]
        public void TryMigrate_EmptyPayload_Fails()
        {
            Assert.False(SaveMigrations.TryMigrate(Array.Empty<byte>(), InventorySaveCodec.FormatVersion1, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void TryMigrate_NullPayload_Fails()
        {
            Assert.False(SaveMigrations.TryMigrate(null!, InventorySaveCodec.FormatVersion1, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void TryMigrate_NonInventoryGarbage_Fails()
        {
            // 旧版把 JSON 之类别的负载误标成 v1 时必须整档失败，而不是按前 4 字节猜出一个 count。
            byte[] garbage = Encoding.UTF8.GetBytes("{\"gold\":42}");

            Assert.False(SaveMigrations.TryMigrate(garbage, InventorySaveCodec.FormatVersion1, out byte[] migrated, out string reason));
            Assert.Empty(migrated);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void TryMigrate_EmptyInventory_ProducesValidEmptyV2()
        {
            byte[] v1 = InventorySaveCodec.Encode(Array.Empty<InventoryEntrySnapshot>());

            Assert.True(SaveMigrations.TryMigrate(v1, InventorySaveCodec.FormatVersion1, out byte[] migrated, out _));
            Assert.Equal(4, migrated.Length);
            Assert.True(InventorySaveCodec.TryDecode(migrated, InventorySaveCodec.FormatVersion2, out InventorySaveEntry[] v2));
            Assert.Empty(v2);
        }

        [Fact]
        public void TryMigrate_DuplicateItemIds_AreKeptAsSeparateRows()
        {
            // 与 Encode 的既有语义一致：不去重、不合并，顺序原样保留（调用方负责去重）。
            byte[] v1 = InventorySaveCodec.Encode(new[]
            {
                new InventoryEntrySnapshot(7, 2),
                new InventoryEntrySnapshot(7, 5)
            });

            Assert.True(SaveMigrations.TryMigrate(v1, InventorySaveCodec.FormatVersion1, out byte[] migrated, out _));
            Assert.True(InventorySaveCodec.TryDecode(migrated, InventorySaveCodec.FormatVersion2, out InventorySaveEntry[] v2));

            Assert.Equal(2, v2.Length);
            Assert.Equal(7, v2[0].ItemId);
            Assert.Equal(2, v2[0].Count);
            Assert.Equal(7, v2[1].ItemId);
            Assert.Equal(5, v2[1].Count);
        }

        [Fact]
        public void SupportedSourceVersions_ReportsV1_AndIsDefensiveCopy()
        {
            int[] versions = SaveMigrations.SupportedSourceVersions;

            Assert.Single(versions);
            Assert.Equal(InventorySaveCodec.FormatVersion1, versions[0]);

            versions[0] = 99;

            Assert.Equal(InventorySaveCodec.FormatVersion1, SaveMigrations.SupportedSourceVersions[0]);
        }
    }

    /// <summary>
    /// T3-17 codec 验收：v2 行布局、按版本解码、自动识别的歧义边界。
    /// </summary>
    public sealed class InventorySaveCodecV2Tests
    {
        private static byte[] SampleV2Payload()
        {
            return InventorySaveCodec.EncodeV2(new[]
            {
                new InventorySaveEntry(1, 1000, 0x0102030405060708L),
                new InventorySaveEntry(2001, 3, 0)
            });
        }

        [Fact]
        public void EncodeV2_LayoutIsSixteenBytesPerRow_LittleEndian()
        {
            byte[] payload = SampleV2Payload();

            Assert.Equal(36, payload.Length);
            Assert.Equal(4 + 2 * InventorySaveCodec.EntrySizeV2, payload.Length);

            Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4)));

            // 行 0：偏移 4 起 itemId(4) + count(4) + updatedTicks(8)
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4)));
            Assert.Equal(1000, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4)));
            Assert.Equal(0x0102030405060708L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(12, 8)));
            Assert.Equal(0x08, payload[12]); // 小端：低字节在前
            Assert.Equal(0x01, payload[19]);

            // 行 1：紧接 16 字节之后
            Assert.Equal(2001, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(20, 4)));
            Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(24, 4)));
            Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(28, 8)));
        }

        [Fact]
        public void EncodeV2_ThenTryDecodeV2_RoundTripsTicks()
        {
            Assert.True(InventorySaveCodec.TryDecode(SampleV2Payload(), InventorySaveCodec.FormatVersion2, out InventorySaveEntry[] entries));

            Assert.Equal(2, entries.Length);
            Assert.Equal(1, entries[0].ItemId);
            Assert.Equal(1000, entries[0].Count);
            Assert.Equal(0x0102030405060708L, entries[0].UpdatedTicks);
            Assert.Equal(2001, entries[1].ItemId);
            Assert.Equal(3, entries[1].Count);
            Assert.Equal(0L, entries[1].UpdatedTicks);
        }

        [Fact]
        public void TryDecode_V1Explicit_ReturnsZeroTicks()
        {
            byte[] v1 = InventorySaveCodec.Encode(new[] { new InventoryEntrySnapshot(9, 4) });

            Assert.True(InventorySaveCodec.TryDecode(v1, InventorySaveCodec.FormatVersion1, out InventorySaveEntry[] entries));
            Assert.Single(entries);
            Assert.Equal(9, entries[0].ItemId);
            Assert.Equal(4, entries[0].Count);
            Assert.Equal(0L, entries[0].UpdatedTicks);
        }

        [Fact]
        public void TryDecode_V2PayloadWithV1Version_Fails()
        {
            // 版本是显式契约：v2 字节按 v1 读长度必然不符，不能被"猜"着读出来。
            Assert.False(InventorySaveCodec.TryDecode(SampleV2Payload(), InventorySaveCodec.FormatVersion1, out InventorySaveEntry[] entries));
            Assert.Empty(entries);
        }

        [Fact]
        public void TryDecode_UnsupportedFormatVersion_Fails()
        {
            byte[] v1 = InventorySaveCodec.Encode(new[] { new InventoryEntrySnapshot(9, 4) });

            Assert.False(InventorySaveCodec.TryDecode(v1, 0, out _));
            Assert.False(InventorySaveCodec.TryDecode(v1, 3, out _));
            Assert.False(InventorySaveCodec.TryDecode(v1, -1, out _));
        }

        [Fact]
        public void TryDecodeAuto_DetectsV1AndV2()
        {
            Assert.True(InventorySaveCodec.TryDecodeAuto(InventorySaveCodec.Encode(new[] { new InventoryEntrySnapshot(2, 5) }), out InventorySaveEntry[] v1));
            Assert.Single(v1);
            Assert.Equal(2, v1[0].ItemId);
            Assert.Equal(5, v1[0].Count);
            Assert.Equal(0L, v1[0].UpdatedTicks);

            Assert.True(InventorySaveCodec.TryDecodeAuto(SampleV2Payload(), out InventorySaveEntry[] v2));
            Assert.Equal(2, v2.Length);
            Assert.Equal(0x0102030405060708L, v2[0].UpdatedTicks);
        }

        [Fact]
        public void TryDecodeAuto_EmptyInventory_IsAmbiguousButResolvesToEmpty()
        {
            // count == 0 时 4 + 8n 与 4 + 16n 都是 4，是唯一的双长度成立点；两种解释都等于空背包。
            byte[] payload = InventorySaveCodec.Encode(Array.Empty<InventoryEntrySnapshot>());

            Assert.Equal(4, payload.Length);
            Assert.True(InventorySaveCodec.TryDecodeAuto(payload, out InventorySaveEntry[] entries));
            Assert.Empty(entries);
        }

        [Fact]
        public void TryDecode_V2LengthMismatch_Fails()
        {
            byte[] payload = SampleV2Payload();

            var truncated = new byte[payload.Length - 1];
            Array.Copy(payload, truncated, truncated.Length);
            Assert.False(InventorySaveCodec.TryDecode(truncated, InventorySaveCodec.FormatVersion2, out _));
            Assert.False(InventorySaveCodec.TryDecodeAuto(truncated, out _));

            var extended = new byte[payload.Length + 1];
            Array.Copy(payload, extended, payload.Length);
            Assert.False(InventorySaveCodec.TryDecode(extended, InventorySaveCodec.FormatVersion2, out _));
            Assert.False(InventorySaveCodec.TryDecodeAuto(extended, out _));
        }

        [Fact]
        public void TryDecodeAuto_LengthMatchingNeitherVersion_Fails()
        {
            var payload = new byte[7];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), 0);

            Assert.False(InventorySaveCodec.TryDecodeAuto(payload, out InventorySaveEntry[] entries));
            Assert.Empty(entries);
        }

        [Fact]
        public void TryDecode_V2NegativeTicks_Fails()
        {
            byte[] payload = InventorySaveCodec.EncodeV2(new[] { new InventorySaveEntry(5, 9, 0) });
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12, 8), -1L);

            Assert.False(InventorySaveCodec.TryDecode(payload, InventorySaveCodec.FormatVersion2, out _));
            Assert.False(InventorySaveCodec.TryDecodeAuto(payload, out _));
        }

        [Fact]
        public void TryDecode_LegacyOverload_ReadsV2AndDropsTicks()
        {
            // 迁移后的 payload 是 v2：旧调用点（CloudSaveMerger / 大厅存档）只认 id 与 count，必须继续能解。
            Assert.True(InventorySaveCodec.TryDecode(SampleV2Payload(), out InventoryEntrySnapshot[] entries));

            Assert.Equal(2, entries.Length);
            Assert.Equal(1, entries[0].ItemId);
            Assert.Equal(1000, entries[0].Count);
            Assert.Equal(2001, entries[1].ItemId);
            Assert.Equal(3, entries[1].Count);
        }

        [Fact]
        public void Encode_WithDuplicateItemIds_KeepsBothRows()
        {
            // 与 Encode 的既有语义一致：编码器不去重、不求和，两行原样写、原样读；去重是调用方（InventoryHandler）的职责。
            byte[] payload = InventorySaveCodec.Encode(new[]
            {
                new InventoryEntrySnapshot(7, 2),
                new InventoryEntrySnapshot(7, 5)
            });

            Assert.True(InventorySaveCodec.TryDecode(payload, out InventoryEntrySnapshot[] entries));
            Assert.Equal(2, entries.Length);
            Assert.Equal(7, entries[0].ItemId);
            Assert.Equal(2, entries[0].Count);
            Assert.Equal(7, entries[1].ItemId);
            Assert.Equal(5, entries[1].Count);
        }

        [Fact]
        public void EncodeV2_NegativeTicks_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => InventorySaveCodec.EncodeV2(new[] { new InventorySaveEntry(1, 1, -1) }));
        }

        [Fact]
        public void EncodeV2_NullList_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => InventorySaveCodec.EncodeV2(null!));
        }

        [Fact]
        public void EncodeV2_TooManyEntries_Throws()
        {
            var entries = new InventorySaveEntry[InventorySaveCodec.MaxEntries + 1];
            Assert.Throws<ArgumentOutOfRangeException>(() => InventorySaveCodec.EncodeV2(entries));
        }
    }

    /// <summary>
    /// T3-17 SaveService 验收：schema 版本落盘、旧档自动迁移、未来版本拒绝、CRC/截断兜底。
    /// </summary>
    public sealed class SaveServiceSchemaVersionTests
    {
        private static SaveService CreateService(out InMemoryStorage storage)
        {
            storage = new InMemoryStorage();
            return new SaveService(storage);
        }

        private static byte[] V1Payload()
        {
            return InventorySaveCodec.Encode(new[] { new InventoryEntrySnapshot(1, 10) });
        }

        private static byte[] V2Payload()
        {
            return InventorySaveCodec.EncodeV2(new[] { new InventorySaveEntry(1, 10, 0) });
        }

        [Fact]
        public void Constants_CurrentVersionIsTwo_AndMatchesCodecAndChain()
        {
            Assert.Equal(2, SaveService.CurrentVersion);
            Assert.Equal(SaveMigrations.CurrentVersion, SaveService.CurrentVersion);
            Assert.Equal(InventorySaveCodec.FormatVersion2, SaveMigrations.CurrentVersion);
        }

        [Fact]
        public void Save_WritesSchemaVersionIntoHeaderAndPreservesEnvelopeFormat()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(SaveService.CurrentVersion, V2Payload()));

            byte[] raw = storage.Raw(service.Key(0));

            Assert.Equal(SaveService.HeaderSize + V2Payload().Length, raw.Length);
            Assert.Equal(SaveService.FormatVersion, BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4, 4)));
            Assert.Equal(SaveService.CurrentVersion, BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8, 4)));
        }

        [Fact]
        public void SaveThenLoad_V2_RoundTripsPayloadAndReportsCurrentVersion()
        {
            SaveService service = CreateService(out _);
            byte[] payload = V2Payload();

            service.Save(0, new SaveEnvelope(SaveService.CurrentVersion, payload));

            Assert.True(service.TryLoad(0, out SaveEnvelope loaded, out string reason));
            Assert.Empty(reason);
            Assert.Equal(SaveService.CurrentVersion, loaded.Version);
            Assert.Equal(SaveService.CurrentVersion, loaded.SchemaVersion);
            Assert.Equal(payload, loaded.Payload);
        }

        [Fact]
        public void Load_LegacyV1File_MigratesToCurrentVersion()
        {
            SaveService service = CreateService(out _);
            service.Save(0, new SaveEnvelope(version: InventorySaveCodec.FormatVersion1, payload: V1Payload()));

            Assert.True(service.TryLoad(0, out SaveEnvelope loaded, out string reason));

            Assert.Empty(reason);
            Assert.Equal(SaveService.CurrentVersion, loaded.Version);
            Assert.True(InventorySaveCodec.TryDecode(loaded.Payload, InventorySaveCodec.FormatVersion2, out InventorySaveEntry[] migrated));
            Assert.Single(migrated);
            Assert.Equal(1, migrated[0].ItemId);
            Assert.Equal(10, migrated[0].Count);
            Assert.Equal(0L, migrated[0].UpdatedTicks);
        }

        [Fact]
        public void Load_V1PayloadWithSchemaByteRewrittenTo1_Migrates()
        {
            // 版本号不在 CRC 覆盖范围内：把 header 的 schema 字段改回 1 不会被 CRC 拦住，
            // 能确定走的是"迁移分支"而不是"CRC 分支"。
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(version: SaveService.CurrentVersion, payload: V1Payload()));
            storage.Tamper(service.Key(0), bytes => bytes[8] = (byte)InventorySaveCodec.FormatVersion1);

            Assert.True(service.TryLoad(0, out SaveEnvelope loaded, out string reason));

            Assert.Empty(reason);
            Assert.Equal(SaveService.CurrentVersion, loaded.Version);
            Assert.Equal(4 + InventorySaveCodec.EntrySizeV2, loaded.Payload.Length);
        }

        [Fact]
        public void Load_FutureSchemaVersion_ReturnsFalseWithoutPartialParse()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(SaveService.CurrentVersion, V2Payload()));
            storage.Tamper(service.Key(0), bytes => bytes[8] = 99);

            Assert.False(service.TryLoad(0, out SaveEnvelope envelope, out string reason));

            Assert.Null(envelope.Payload);
            Assert.Contains("v99", reason);
            Assert.False(service.HasSave(0));
        }

        [Fact]
        public void Load_CorruptedCrc_ReturnsFalseWithReason()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(SaveService.CurrentVersion, V2Payload()));
            storage.Tamper(service.Key(0), bytes => bytes[bytes.Length - 1] ^= 0xFF);

            Assert.False(service.TryLoad(0, out _, out string reason));
            Assert.Contains("CRC32", reason);
        }

        [Fact]
        public void Load_TruncatedFile_ReturnsFalseWithReason()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(SaveService.CurrentVersion, V2Payload()));

            byte[] raw = storage.Raw(service.Key(0));
            var truncated = new byte[raw.Length - 2];
            Array.Copy(raw, truncated, truncated.Length);
            storage.Write(service.Key(0), truncated);

            Assert.False(service.TryLoad(0, out _, out string reason));
            Assert.Contains("长度", reason);
        }

        [Fact]
        public void Load_TruncatedV1PayloadInsideValidEnvelope_FailsMigration()
        {
            // 包络完整、CRC 正确，但 v1 负载少一个字节：迁移必须失败，而不是读出半截背包。
            byte[] v1 = V1Payload();
            var truncated = new byte[v1.Length - 1];
            Array.Copy(v1, truncated, truncated.Length);

            SaveService service = CreateService(out _);
            service.Save(0, new SaveEnvelope(version: InventorySaveCodec.FormatVersion1, payload: truncated));

            Assert.False(service.TryLoad(0, out _, out string reason));
            Assert.Contains("迁移失败", reason);
        }

        [Fact]
        public void Load_EmptyPayloadAtSchemaV1_FailsMigration()
        {
            SaveService service = CreateService(out _);
            service.Save(0, new SaveEnvelope(version: InventorySaveCodec.FormatVersion1, payload: Array.Empty<byte>()));

            Assert.False(service.TryLoad(0, out SaveEnvelope envelope, out string reason));
            Assert.Null(envelope.Payload);
            Assert.Contains("迁移失败", reason);
        }

        [Fact]
        public void Load_MissingSlot_ReturnsFalseWithReason()
        {
            SaveService service = CreateService(out _);

            Assert.False(service.TryLoad(3, out SaveEnvelope envelope, out string reason));
            Assert.Null(envelope.Payload);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Load_InvalidSchemaVersionZero_ReturnsFalse()
        {
            SaveService service = CreateService(out InMemoryStorage storage);
            service.Save(0, new SaveEnvelope(SaveService.CurrentVersion, V2Payload()));
            storage.Tamper(service.Key(0), bytes => bytes[8] = 0);

            Assert.False(service.TryLoad(0, out _, out string reason));
            Assert.Contains("v0", reason);
        }
    }
}
