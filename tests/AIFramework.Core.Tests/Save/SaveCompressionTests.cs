using System;
using System.Text;
using AIFramework.Core.Save;
using Xunit;

namespace AIFramework.Core.Tests.Save
{
    /// <summary>
    /// 存档压缩：往返一致、容器头校验、解压炸弹防护、小档不压的取舍。
    /// </summary>
    public sealed class SaveCompressionTests
    {
        private static byte[] Repetitive(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                data[i] = (byte)(i % 16);
            }

            return data;
        }

        [Fact]
        public void CompressDecompress_RoundTrips()
        {
            byte[] original = Encoding.UTF8.GetBytes("AIFW 存档压缩往返测试 payload，重复内容重复内容重复内容。");

            byte[] container = SaveCompression.Compress(original);

            Assert.True(SaveCompression.TryDecompress(container, out byte[]? restored, out string reason), reason);
            Assert.Equal(original, restored);
        }

        [Fact]
        public void Compress_HasExpectedHeader()
        {
            byte[] container = SaveCompression.Compress(Repetitive(1000));

            Assert.Equal(4, SaveCompression.Magic.Length);
            Assert.Equal((byte)'A', container[0]);
            Assert.Equal((byte)'I', container[1]);
            Assert.Equal((byte)'F', container[2]);
            Assert.Equal((byte)'C', container[3]);
            Assert.True(container.Length > SaveCompression.HeaderSize);
        }

        [Fact]
        public void Compress_RepetitiveData_Shrinks()
        {
            byte[] original = Repetitive(4096);

            SaveCompressionReport report = SaveCompression.Evaluate(original);

            Assert.True(report.CompressedBytes < report.OriginalBytes);
            Assert.True(report.Ratio < 0.5d);
        }

        [Fact]
        public void Compress_TinyData_ShouldNotCompress()
        {
            Assert.False(SaveCompression.ShouldCompress(1));
            Assert.False(SaveCompression.ShouldCompress(SaveCompression.MinCompressLength - 1));
            Assert.True(SaveCompression.ShouldCompress(SaveCompression.MinCompressLength));
        }

        [Fact]
        public void Compress_TooLarge_Throws()
        {
            var huge = new byte[SaveCompression.MaxOriginalLength + 1];

            Assert.Throws<ArgumentException>(() => SaveCompression.Compress(huge));
        }

        [Fact]
        public void Compress_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SaveCompression.Compress(null!));
        }

        [Fact]
        public void Decompress_Null_ReturnsFalse()
        {
            Assert.False(SaveCompression.TryDecompress(null, out _, out string reason));
            Assert.Contains("null", reason);
        }

        [Fact]
        public void Decompress_TooShort_ReturnsFalse()
        {
            Assert.False(SaveCompression.TryDecompress(new byte[4], out _, out string reason));
            Assert.Contains("长度不足", reason);
        }

        [Fact]
        public void Decompress_BadMagic_ReturnsFalse()
        {
            byte[] container = SaveCompression.Compress(Repetitive(100));
            container[0] = (byte)'X';

            Assert.False(SaveCompression.TryDecompress(container, out _, out string reason));
            Assert.Contains("魔数", reason);
        }

        [Fact]
        public void Decompress_BadVersion_ReturnsFalse()
        {
            byte[] container = SaveCompression.Compress(Repetitive(100));
            container[4] = 99;

            Assert.False(SaveCompression.TryDecompress(container, out _, out string reason));
            Assert.Contains("版本", reason);
        }

        [Fact]
        public void Decompress_DeclaredLengthMismatch_ReturnsFalse()
        {
            byte[] container = SaveCompression.Compress(Repetitive(100));
            // 把 compressedLength 改小，使 HeaderSize + length != container.Length
            container[12] = 1;

            Assert.False(SaveCompression.TryDecompress(container, out _, out string reason));
            Assert.Contains("长度", reason);
        }

        [Fact]
        public void Decompress_CorruptStream_ReturnsFalse()
        {
            byte[] container = SaveCompression.Compress(Repetitive(512));
            for (int i = SaveCompression.HeaderSize; i < container.Length; i++)
            {
                container[i] ^= 0xFF;
            }

            Assert.False(SaveCompression.TryDecompress(container, out _, out _));
        }

        [Fact]
        public void Decompress_EmptyPayload_RoundTrips()
        {
            byte[] container = SaveCompression.Compress(Array.Empty<byte>());

            Assert.True(SaveCompression.TryDecompress(container, out byte[]? restored, out string reason), reason);
            Assert.Empty(restored!);
        }

        [Fact]
        public void Evaluate_EmptyPayload_ReturnsZeroReport()
        {
            SaveCompressionReport report = SaveCompression.Evaluate(Array.Empty<byte>());

            Assert.Equal(0, report.OriginalBytes);
            Assert.Equal(1d, report.Ratio, 3);
        }

        [Fact]
        public void Evaluate_ReportLineHasFields()
        {
            SaveCompressionReport report = SaveCompression.Evaluate(Repetitive(2048));

            string line = report.ToReportLine();

            Assert.StartsWith("[Compress] deflate", line, StringComparison.Ordinal);
            Assert.Contains("原始=", line);
            Assert.Contains("压缩率=", line);
            Assert.Contains("节省=", line);
        }

        [Fact]
        public void Compress_DifferentInputs_ProduceDifferentContainers()
        {
            byte[] first = SaveCompression.Compress(Repetitive(100));
            byte[] second = SaveCompression.Compress(Encoding.UTF8.GetBytes(new string('x', 100)));

            Assert.NotEqual(first, second);
        }
    }
}
