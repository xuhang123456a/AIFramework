using System;
using AIFramework.Contracts.Meta;
using AIFramework.Core.Save;
using Xunit;

namespace AIFramework.Core.Tests.Save
{
    public sealed class InventorySaveCodecTests
    {
        [Fact]
        public void EncodeThenDecode_RoundTrips()
        {
            var source = new[]
            {
                new InventoryEntrySnapshot(1, 1000),
                new InventoryEntrySnapshot(2001, 3)
            };

            byte[] payload = InventorySaveCodec.Encode(source);
            bool ok = InventorySaveCodec.TryDecode(payload, out InventoryEntrySnapshot[] decoded);

            Assert.True(ok);
            Assert.Equal(2, decoded.Length);
            Assert.Equal(1, decoded[0].ItemId);
            Assert.Equal(1000, decoded[0].Count);
            Assert.Equal(2001, decoded[1].ItemId);
            Assert.Equal(3, decoded[1].Count);
        }

        [Fact]
        public void EncodeEmpty_RoundTripsToEmpty()
        {
            byte[] payload = InventorySaveCodec.Encode(Array.Empty<InventoryEntrySnapshot>());
            Assert.True(InventorySaveCodec.TryDecode(payload, out InventoryEntrySnapshot[] decoded));
            Assert.Empty(decoded);
        }

        [Fact]
        public void TruncatedPayload_ReturnsFalse()
        {
            byte[] payload = InventorySaveCodec.Encode(new[] { new InventoryEntrySnapshot(1, 5) });
            var truncated = new byte[payload.Length - 1];
            Array.Copy(payload, truncated, truncated.Length);

            Assert.False(InventorySaveCodec.TryDecode(truncated, out InventoryEntrySnapshot[] decoded));
            Assert.Empty(decoded);
        }

        [Fact]
        public void TooShortPayload_ReturnsFalse()
        {
            Assert.False(InventorySaveCodec.TryDecode(new byte[3], out _));
            Assert.False(InventorySaveCodec.TryDecode(Array.Empty<byte>(), out _));
        }

        [Fact]
        public void NegativeCount_ReturnsFalse()
        {
            var payload = new byte[4];
            payload[0] = 0xFF;
            payload[1] = 0xFF;
            payload[2] = 0xFF;
            payload[3] = 0xFF;

            Assert.False(InventorySaveCodec.TryDecode(payload, out _));
        }

        [Fact]
        public void AbsurdCount_ReturnsFalse()
        {
            var payload = new byte[4];
            payload[0] = 0x10;
            payload[1] = 0x27;
            payload[2] = 0x00;
            payload[3] = 0x00; // 10000

            Assert.False(InventorySaveCodec.TryDecode(payload, out _));
        }

        [Fact]
        public void NonPositiveItemId_ReturnsFalse()
        {
            var payload = new byte[12];
            payload[0] = 1; // count = 1
            // itemId = 0，count = 5
            payload[8] = 5;

            Assert.False(InventorySaveCodec.TryDecode(payload, out _));
        }

        [Fact]
        public void NonPositiveQuantity_ReturnsFalse()
        {
            var payload = new byte[12];
            payload[0] = 1; // count = 1
            payload[4] = 7; // itemId = 7
            // count = 0 → 非法

            Assert.False(InventorySaveCodec.TryDecode(payload, out _));
        }

        [Fact]
        public void Encode_NullList_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => InventorySaveCodec.Encode(null!));
        }

        [Fact]
        public void Encode_TooManyEntries_Throws()
        {
            var entries = new InventoryEntrySnapshot[InventorySaveCodec.MaxEntries + 1];
            Assert.Throws<ArgumentOutOfRangeException>(() => InventorySaveCodec.Encode(entries));
        }
    }
}
