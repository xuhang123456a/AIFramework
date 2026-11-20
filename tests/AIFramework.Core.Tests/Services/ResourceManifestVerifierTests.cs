using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using Xunit;

namespace AIFramework.Core.Tests.Services
{
    /// <summary>
    /// 热更资源清单校验：缺文件、截断、内容被改必须能分别报出来，且"全有或全无"的回退结论正确。
    /// </summary>
    public sealed class ResourceManifestVerifierTests
    {
        private static readonly byte[] Payload = Encoding.UTF8.GetBytes("{\"v\":1,\"name\":\"hero\"}");

        private static string HashOf(byte[] data)
        {
            return ResourceManifestVerifier.Sha256Hex(data);
        }

        private static AssetManifest ManifestOf(params AssetManifestEntry[] entries)
        {
            return new AssetManifest("v1", entries);
        }

        private static Func<string, byte[]?> Loader(Dictionary<string, byte[]> files)
        {
            return name => files.TryGetValue(name, out byte[]? data) ? data : null;
        }

        [Fact]
        public void Verify_MatchingContent_IsOk()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest manifest = ManifestOf(new AssetManifestEntry("hero.json", HashOf(Payload), Payload.Length));
            var files = new Dictionary<string, byte[]> { { "hero.json", Payload } };

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, Loader(files));

            Assert.Single(outcomes);
            Assert.True(outcomes[0].IsOk);
        }

        [Fact]
        public void Verify_MissingFile_ReportsMissing()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest manifest = ManifestOf(new AssetManifestEntry("hero.json", HashOf(Payload), Payload.Length));

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, Loader(new Dictionary<string, byte[]>()));

            Assert.Equal(AssetCheckResult.Missing, outcomes[0].Result);
            Assert.Equal(-1, outcomes[0].ActualSizeBytes);
        }

        [Fact]
        public void Verify_TruncatedFile_ReportsSizeMismatch()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest manifest = ManifestOf(new AssetManifestEntry("hero.json", HashOf(Payload), Payload.Length));
            var files = new Dictionary<string, byte[]> { { "hero.json", new byte[] { Payload[0], Payload[1] } } };

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, Loader(files));

            Assert.Equal(AssetCheckResult.SizeMismatch, outcomes[0].Result);
            Assert.Equal(2, outcomes[0].ActualSizeBytes);
        }

        [Fact]
        public void Verify_TamperedContentSameSize_ReportsHashMismatch()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest manifest = ManifestOf(new AssetManifestEntry("hero.json", HashOf(Payload), Payload.Length));
            byte[] tampered = (byte[])Payload.Clone();
            tampered[0] ^= 0xFF;
            var files = new Dictionary<string, byte[]> { { "hero.json", tampered } };

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, Loader(files));

            Assert.Equal(AssetCheckResult.HashMismatch, outcomes[0].Result);
        }

        [Fact]
        public void Verify_HashComparison_IsCaseInsensitive()
        {
            var verifier = new ResourceManifestVerifier();
            string upper = HashOf(Payload).ToUpperInvariant();
            AssetManifest manifest = ManifestOf(new AssetManifestEntry("hero.json", upper, Payload.Length));
            var files = new Dictionary<string, byte[]> { { "hero.json", Payload } };

            Assert.True(verifier.Verify(in manifest, Loader(files))[0].IsOk);
        }

        [Fact]
        public void Verify_MultipleEntries_KeepsManifestOrder()
        {
            var verifier = new ResourceManifestVerifier();
            byte[] other = Encoding.UTF8.GetBytes("other");
            AssetManifest manifest = ManifestOf(
                new AssetManifestEntry("a.json", HashOf(Payload), Payload.Length),
                new AssetManifestEntry("b.json", HashOf(other), other.Length));
            var files = new Dictionary<string, byte[]>
            {
                { "a.json", Payload },
                { "b.json", other },
            };

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, Loader(files));

            Assert.Equal("a.json", outcomes[0].Name);
            Assert.Equal("b.json", outcomes[1].Name);
        }

        [Fact]
        public void Verify_EmptyManifest_ReturnsEmpty()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest empty = new AssetManifest("v1", Array.Empty<AssetManifestEntry>());

            Assert.Empty(verifier.Verify(in empty, _ => null));
        }

        [Fact]
        public void Verify_LoaderThrows_TreatedAsMissing()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest manifest = ManifestOf(new AssetManifestEntry("x", HashOf(Payload), Payload.Length));

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, _ => throw new InvalidOperationException("io"));

            Assert.Equal(AssetCheckResult.Missing, outcomes[0].Result);
        }

        [Fact]
        public void Verify_NullLoader_Throws()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest manifest = ManifestOf();

            Assert.Throws<ArgumentNullException>(() => verifier.Verify(in manifest, null!));
        }

        [Fact]
        public void AllOk_AllGood_ReturnsTrue()
        {
            var outcomes = new[]
            {
                new AssetCheckOutcome("a", AssetCheckResult.Ok, 1),
                new AssetCheckOutcome("b", AssetCheckResult.Ok, 2),
            };

            Assert.True(ResourceManifestVerifier.AllOk(outcomes));
        }

        [Fact]
        public void AllOk_AnyFailure_ReturnsFalse()
        {
            var outcomes = new[]
            {
                new AssetCheckOutcome("a", AssetCheckResult.Ok, 1),
                new AssetCheckOutcome("b", AssetCheckResult.HashMismatch, 2),
            };

            Assert.False(ResourceManifestVerifier.AllOk(outcomes));
        }

        [Fact]
        public void AllOk_Empty_ReturnsTrue()
        {
            Assert.True(ResourceManifestVerifier.AllOk(Array.Empty<AssetCheckOutcome>()));
        }

        [Fact]
        public void CountFailures_CountsNonOkOnly()
        {
            var outcomes = new[]
            {
                new AssetCheckOutcome("a", AssetCheckResult.Ok, 1),
                new AssetCheckOutcome("b", AssetCheckResult.Missing, -1),
                new AssetCheckOutcome("c", AssetCheckResult.SizeMismatch, 3),
                new AssetCheckOutcome("d", AssetCheckResult.HashMismatch, 4),
            };

            Assert.Equal(3, ResourceManifestVerifier.CountFailures(outcomes));
        }

        [Fact]
        public void Sha256Hex_KnownVector()
        {
            // "abc" 的 SHA-256 是公开已知向量，硬编码它防止实现被悄悄换成别的哈希。
            string hex = ResourceManifestVerifier.Sha256Hex(Encoding.ASCII.GetBytes("abc"));

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hex);
        }

        [Fact]
        public void Sha256Hex_Null_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ResourceManifestVerifier.Sha256Hex(null!));
        }

        [Fact]
        public void Manifest_DefaultsToEmpty()
        {
            var manifest = new AssetManifest(null!, null!);

            Assert.Equal(string.Empty, manifest.Version);
            Assert.Empty(manifest.Entries);
        }

        [Fact]
        public void Entry_NullNameAllowedButFailsOnLoad()
        {
            var verifier = new ResourceManifestVerifier();
            AssetManifest manifest = ManifestOf(new AssetManifestEntry(null!, HashOf(Payload), Payload.Length));
            var files = new Dictionary<string, byte[]> { { "hero.json", Payload } };

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, name => name == null ? null : files[name]);

            Assert.Equal(AssetCheckResult.Missing, outcomes[0].Result);
        }

        [Fact]
        public void Verify_PartialSuccess_ReportsPerEntry()
        {
            var verifier = new ResourceManifestVerifier();
            byte[] broken = new byte[Payload.Length + 1];
            AssetManifest manifest = ManifestOf(
                new AssetManifestEntry("good", HashOf(Payload), Payload.Length),
                new AssetManifestEntry("broken", HashOf(Payload), Payload.Length));
            var files = new Dictionary<string, byte[]>
            {
                { "good", Payload },
                { "broken", broken },
            };

            AssetCheckOutcome[] outcomes = verifier.Verify(in manifest, Loader(files));

            Assert.True(outcomes[0].IsOk);
            Assert.Equal(AssetCheckResult.SizeMismatch, outcomes[1].Result);
            Assert.False(ResourceManifestVerifier.AllOk(outcomes));
        }
    }
}
