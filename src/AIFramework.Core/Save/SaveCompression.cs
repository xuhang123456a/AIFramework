using System;
using System.IO;
using System.IO.Compression;

namespace AIFramework.Core.Save
{
    /// <summary>压缩结果（体积对比）。</summary>
    public readonly struct SaveCompressionReport
    {
        /// <summary>构造报告。</summary>
        /// <param name="originalBytes">原始字节数。</param>
        /// <param name="compressedBytes">压缩后字节数。</param>
        /// <param name="algorithm">算法标记。</param>
        public SaveCompressionReport(int originalBytes, int compressedBytes, string algorithm)
        {
            OriginalBytes = originalBytes < 0 ? 0 : originalBytes;
            CompressedBytes = compressedBytes < 0 ? 0 : compressedBytes;
            Algorithm = algorithm ?? string.Empty;
        }

        /// <summary>原始字节数。</summary>
        public int OriginalBytes { get; }

        /// <summary>压缩后字节数。</summary>
        public int CompressedBytes { get; }

        /// <summary>算法标记。</summary>
        public string Algorithm { get; }

        /// <summary>压缩率（压缩后/原始；原始为 0 时记 1）。</summary>
        public double Ratio
        {
            get { return OriginalBytes <= 0 ? 1d : (double)CompressedBytes / OriginalBytes; }
        }

        /// <summary>节省字节数（可能为负：极小存档压缩后反而更大）。</summary>
        public int SavedBytes
        {
            get { return OriginalBytes - CompressedBytes; }
        }

        /// <summary>一行报告（口径固定，便于体积回归对比）。</summary>
        public string ToReportLine()
        {
            return "[Compress] " + Algorithm + " 原始=" + OriginalBytes + "B 压缩后=" + CompressedBytes
                + "B 压缩率=" + Ratio.ToString("P1", System.Globalization.CultureInfo.InvariantCulture)
                + " 节省=" + SavedBytes + "B";
        }
    }

    /// <summary>
    /// 存档压缩：在 AISV 包络外再套一层压缩容器（AIFC 头 + Deflate）。
    /// <para>
    /// <b>为什么需要压缩</b>：背包/进度类存档里大量小整数与重复字符串，Deflate 通常能压到 20%~40%；
    /// 云存档（T7-2）按字节计费/限流，压缩直接省流量与存储。
    /// </para>
    /// <para>
    /// <b>为什么加自己的头而不是直接用 GZip 流</b>：读档时需要先判断"这是压缩档还是历史未压缩档"，
    /// 有 magic + 版本才能做平滑升级；同时把原始长度写进头里，解压前就能拒绝恶意超大长度（防解压炸弹）。
    /// </para>
    /// <para>
    /// <b>体积小到一定程度就不要压</b>：＜64 字节的存档压缩后通常更大（头开销），
    /// <see cref="ShouldCompress"/> 用阈值判断，调用方据此决定是否套容器。
    /// </para>
    /// </summary>
    public static class SaveCompression
    {
        /// <summary>压缩容器魔数。</summary>
        public static readonly byte[] Magic = { (byte)'A', (byte)'I', (byte)'F', (byte)'C' };

        /// <summary>容器格式版本。</summary>
        public const int FormatVersion = 1;

        /// <summary>容器头长度：magic(4) + version(4) + originalLength(4) + compressedLength(4)。</summary>
        public const int HeaderSize = 16;

        /// <summary>原始数据上限（16 MiB）：超过直接拒绝，防解压炸弹。</summary>
        public const int MaxOriginalLength = 16 * 1024 * 1024;

        /// <summary>低于该长度不值得压缩（头开销通常大于收益）。</summary>
        public const int MinCompressLength = 64;

        /// <summary>是否值得压缩。</summary>
        /// <param name="length">原始字节数。</param>
        /// <returns>长度 ≥ 阈值返回 true。</returns>
        public static bool ShouldCompress(int length)
        {
            return length >= MinCompressLength && length <= MaxOriginalLength;
        }

        /// <summary>压缩并套容器头。</summary>
        /// <param name="plain">原始字节（AISV 包络或任意 payload）。</param>
        /// <returns>压缩容器字节。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="plain"/> 为 null。</exception>
        /// <exception cref="ArgumentException">超长（防解压炸弹）。</exception>
        public static byte[] Compress(byte[] plain)
        {
            if (plain == null)
            {
                throw new ArgumentNullException(nameof(plain));
            }

            if (plain.Length > MaxOriginalLength)
            {
                throw new ArgumentException("原始数据超过 " + MaxOriginalLength + " 字节上限。", nameof(plain));
            }

            byte[] compressed = Deflate(plain);
            var output = new byte[HeaderSize + compressed.Length];
            Magic.CopyTo(output, 0);
            WriteInt32(output, 4, FormatVersion);
            WriteInt32(output, 8, plain.Length);
            WriteInt32(output, 12, compressed.Length);
            compressed.CopyTo(output, HeaderSize);
            return output;
        }

        /// <summary>校验容器并解压。</summary>
        /// <param name="container">压缩容器字节。</param>
        /// <param name="plain">解压结果（失败为 null）。</param>
        /// <param name="reason">失败原因（成功为空串）。</param>
        /// <returns>成功返回 true。</returns>
        public static bool TryDecompress(byte[]? container, out byte[]? plain, out string reason)
        {
            plain = null;

            if (container == null)
            {
                reason = "压缩容器为空（null）。";
                return false;
            }

            if (container.Length < HeaderSize)
            {
                reason = "压缩容器长度不足 " + HeaderSize + " 字节。";
                return false;
            }

            for (int i = 0; i < Magic.Length; i++)
            {
                if (container[i] != Magic[i])
                {
                    reason = "压缩容器魔数不符（可能不是压缩档）。";
                    return false;
                }
            }

            int version = ReadInt32(container, 4);
            if (version != FormatVersion)
            {
                reason = "压缩容器版本不支持：" + version;
                return false;
            }

            int originalLength = ReadInt32(container, 8);
            int compressedLength = ReadInt32(container, 12);
            if (originalLength < 0 || originalLength > MaxOriginalLength)
            {
                reason = "声明的原始长度非法：" + originalLength;
                return false;
            }

            if (compressedLength < 0 || HeaderSize + compressedLength != container.Length)
            {
                reason = "压缩数据长度与容器长度不符。";
                return false;
            }

            byte[] compressed = new byte[compressedLength];
            Array.Copy(container, HeaderSize, compressed, 0, compressedLength);

            try
            {
                plain = Inflate(compressed, originalLength);
            }
            catch (InvalidDataException)
            {
                reason = "Deflate 数据损坏。";
                plain = null;
                return false;
            }

            if (plain.Length != originalLength)
            {
                reason = "解压后长度不符（声明 " + originalLength + "，实际 " + plain.Length + "）。";
                plain = null;
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>生成体积对比报告（不改变数据）。</summary>
        /// <param name="plain">原始字节。</param>
        /// <returns>报告；原始为 null 时返回 0/0 报告。</returns>
        public static SaveCompressionReport Evaluate(byte[]? plain)
        {
            if (plain == null || plain.Length == 0)
            {
                return new SaveCompressionReport(0, 0, "deflate");
            }

            byte[] compressed = Compress(plain);
            return new SaveCompressionReport(plain.Length, compressed.Length, "deflate");
        }

        private static byte[] Deflate(byte[] plain)
        {
            using (var output = new MemoryStream(plain.Length / 2 + 32))
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    deflate.Write(plain, 0, plain.Length);
                }

                return output.ToArray();
            }
        }

        private static byte[] Inflate(byte[] compressed, int expectedLength)
        {
            using (var input = new MemoryStream(compressed))
            {
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    using (var output = new MemoryStream(expectedLength > 0 ? expectedLength : 256))
                    {
                        deflate.CopyTo(output);
                        return output.ToArray();
                    }
                }
            }
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }
    }
}
