using System;
using AIFramework.Core.Replay;
using Xunit;

namespace AIFramework.Core.Tests.Replay
{
    /// <summary>
    /// 回放分享文本的往返与防御。
    /// <para>
    /// 口径：分享文本要能"粘贴到聊天里再贴回来"，所以任何截断/混入字符/头缺失都必须在导入端
    /// 明确失败并给原因——最忌讳的是把任意文本当录像解出一堆乱码再送到回放器里。
    /// </para>
    /// </summary>
    public sealed class ReplaySharingTests
    {
        private static byte[] SampleArchive()
        {
            var wave = new WaveSpecData(2, 0.35f);
            var script = new ReplayScriptData(randomSeed: 7, waveCount: 1, new[] { wave });
            return ReplayArchiveCodec.Encode(
                kernelType: 1,
                levelId: 3,
                randomSeed: 7,
                script,
                new long[] { 11L, 22L, 33L },
                recordedAtUnixSeconds: 1764000000L);
        }

        [Fact]
        public void Export_StartsWithFormatPrefix()
        {
            string text = ReplaySharing.Export(SampleArchive());

            Assert.StartsWith(ReplaySharing.Prefix, text, StringComparison.Ordinal);
        }

        [Fact]
        public void Import_AfterExport_RestoresIdenticalBytes()
        {
            byte[] original = SampleArchive();
            string text = ReplaySharing.Export(original);

            Assert.True(ReplaySharing.TryImport(text, out byte[]? imported, out string reason), reason);
            Assert.Equal(original, imported);
        }

        [Fact]
        public void RoundTrip_ArchiveStillDecodes()
        {
            byte[] original = SampleArchive();
            string text = ReplaySharing.Export(original);
            Assert.True(ReplaySharing.TryImport(text, out byte[]? imported, out _));

            Assert.True(ReplayArchiveCodec.TryDecode(imported, out ReplayArchiveInfo info, out long[] fingerprints, out string reason), reason);
            Assert.Equal(3, info.LevelId);
            Assert.Equal(3, fingerprints.Length);
        }

        [Fact]
        public void Export_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ReplaySharing.Export(null!));
        }

        [Fact]
        public void Export_Empty_Throws()
        {
            Assert.Throws<ArgumentException>(() => ReplaySharing.Export(Array.Empty<byte>()));
        }

        [Fact]
        public void Import_Null_ReturnsFalse()
        {
            Assert.False(ReplaySharing.TryImport(null, out _, out string reason));
            Assert.Contains("为空", reason);
        }

        [Fact]
        public void Import_Empty_ReturnsFalse()
        {
            Assert.False(ReplaySharing.TryImport(string.Empty, out _, out _));
        }

        [Fact]
        public void Import_WrongPrefix_ReturnsFalse()
        {
            Assert.False(ReplaySharing.TryImport("hello world", out _, out string reason));
            Assert.Contains("分享头", reason);
        }

        [Fact]
        public void Import_TruncatedText_ReturnsFalse()
        {
            string text = ReplaySharing.Export(SampleArchive());
            string truncated = text.Substring(0, text.Length - 8);

            Assert.False(ReplaySharing.TryImport(truncated, out _, out string reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Import_TamperedPayload_ReturnsFalse()
        {
            string text = ReplaySharing.Export(SampleArchive());
            char[] chars = text.ToCharArray();
            chars[chars.Length - 1] = chars[chars.Length - 1] == 'A' ? 'B' : 'A';

            Assert.False(ReplaySharing.TryImport(new string(chars), out _, out string reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Import_LengthMismatch_ReturnsFalse()
        {
            byte[] archive = SampleArchive();
            string text = ReplaySharing.Export(archive);
            string forged = ReplaySharing.Prefix + "1:0:" + Convert.ToBase64String(archive);

            Assert.False(ReplaySharing.TryImport(forged, out _, out _));
        }

        [Fact]
        public void Import_MalformedLengthField_ReturnsFalse()
        {
            Assert.False(ReplaySharing.TryImport(ReplaySharing.Prefix + "abc:0:AAAA", out _, out _));
        }

        [Fact]
        public void Import_OverlongText_ReturnsFalse()
        {
            string huge = ReplaySharing.Prefix + new string('A', ReplaySharing.MaxShareLength);

            Assert.False(ReplaySharing.TryImport(huge, out _, out string reason));
            Assert.Contains("过长", reason);
        }

        [Fact]
        public void Import_InvalidBase64_ReturnsFalse()
        {
            string text = ReplaySharing.Prefix + "4:0000:!!!!";

            Assert.False(ReplaySharing.TryImport(text, out _, out string reason));
            Assert.Contains("Base64", reason);
        }

        [Fact]
        public void Import_MissingChecksumField_ReturnsFalse()
        {
            Assert.False(ReplaySharing.TryImport(ReplaySharing.Prefix + "4:AAAA", out _, out _));
        }
    }
}
