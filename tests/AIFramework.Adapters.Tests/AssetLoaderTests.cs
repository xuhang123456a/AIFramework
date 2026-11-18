using System;
using System.IO;
using AIFramework.Adapters.Console;
using Xunit;

namespace AIFramework.Adapters.Tests
{
    /// <summary>
    /// <see cref="DirectoryAssetLoader"/> 契约验收：字节一致、缺失直接抛
    /// <see cref="FileNotFoundException"/>（禁止空数组兜底）、根目录缺失启动期就炸、越界键拒绝。
    /// </summary>
    [Collection("AdapterFileSystem")]
    public sealed class AssetLoaderTests : TempDirectoryTest
    {
        /// <summary>嵌套资源键，验证 <c>A/B/file.bin</c> 形态。</summary>
        private const string NestedKey = "A/B/file.bin";

        [Fact]
        public void LoadBytes_ReturnsExactFileContent()
        {
            byte[] content = { 7, 0, 255 };
            WriteAsset("hero.bin", content);
            var loader = new DirectoryAssetLoader(RootPath);

            byte[] actual = loader.LoadBytes("hero.bin");

            Assert.Equal(content, actual);
        }

        [Fact]
        public void LoadBytes_NestedKey_ReadsFromSubdirectory()
        {
            byte[] content = { 42 };
            WriteAsset(NestedKey, content);
            var loader = new DirectoryAssetLoader(RootPath);

            byte[] actual = loader.LoadBytes(NestedKey);

            Assert.Equal(content, actual);
        }

        [Fact]
        public void Exists_ExistingFile_ReturnsTrue()
        {
            WriteAsset(NestedKey, new byte[] { 1 });
            var loader = new DirectoryAssetLoader(RootPath);

            Assert.True(loader.Exists(NestedKey));
        }

        [Fact]
        public void Exists_MissingFile_ReturnsFalse()
        {
            var loader = new DirectoryAssetLoader(RootPath);

            Assert.False(loader.Exists(NestedKey));
        }

        [Fact]
        public void LoadBytes_MissingFile_ThrowsFileNotFoundException()
        {
            var loader = new DirectoryAssetLoader(RootPath);

            Assert.Throws<FileNotFoundException>(() => loader.LoadBytes(NestedKey));
        }

        [Fact]
        public void LoadBytes_DirectoryKey_ThrowsFileNotFoundException()
        {
            Directory.CreateDirectory(Path.Combine(RootPath, "A"));
            var loader = new DirectoryAssetLoader(RootPath);

            Assert.Throws<FileNotFoundException>(() => loader.LoadBytes("A"));
        }

        [Fact]
        public void Constructor_MissingRoot_ThrowsDirectoryNotFoundException()
        {
            string missing = Path.Combine(RootPath, "not-there");

            Assert.Throws<DirectoryNotFoundException>(() => new DirectoryAssetLoader(missing));
        }

        [Theory]
        [InlineData("../x.bin")]
        [InlineData("A/../../x.bin")]
        public void LoadBytes_TraversalKey_ThrowsArgumentException(string key)
        {
            WriteAsset(NestedKey, new byte[] { 1 });
            var loader = new DirectoryAssetLoader(RootPath);

            Assert.Throws<ArgumentException>(() => loader.LoadBytes(key));
        }

        [Fact]
        public void Exists_TraversalKey_ThrowsArgumentException()
        {
            var loader = new DirectoryAssetLoader(RootPath);

            Assert.Throws<ArgumentException>(() => loader.Exists("../x.bin"));
        }

        [Fact]
        public void LoadBytes_AbsoluteKey_ThrowsArgumentException()
        {
            var loader = new DirectoryAssetLoader(RootPath);

            Assert.Throws<ArgumentException>(() => loader.LoadBytes("/etc/passwd"));
        }

        /// <summary>把资源字节按相对键写进临时根目录（自动建中间目录）。</summary>
        private void WriteAsset(string relativeKey, byte[] content)
        {
            string fullPath = Path.Combine(RootPath, relativeKey.Replace('/', Path.DirectorySeparatorChar));
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(fullPath, content);
        }
    }
}
