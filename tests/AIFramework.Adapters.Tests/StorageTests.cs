using System;
using System.IO;
using System.Text;
using AIFramework.Adapters.Console;
using Xunit;

namespace AIFramework.Adapters.Tests
{
    /// <summary>
    /// <see cref="FileSystemStoragePort"/> 契约验收：写读往返、缺失返回 <c>null</c>、
    /// 删除静默成功、枚举序数升序且不含 <c>.tmp</c>、非法键一律拒绝。
    /// 每个用例独占一个临时根目录，结束即删。
    /// </summary>
    [Collection("AdapterFileSystem")]
    public sealed class StorageTests : TempDirectoryTest
    {
        /// <summary>冒烟与测试统一使用的键名。</summary>
        private const string StateKey = "signin.state";

        [Fact]
        public void Write_ThenRead_RoundTripsExactBytes()
        {
            var storage = new FileSystemStoragePort(RootPath);
            byte[] payload = { 1, 2, 3, 240, 255 };

            storage.Write(StateKey, payload);

            byte[]? actual = storage.Read(StateKey);
            Assert.NotNull(actual);
            Assert.Equal(payload, actual);
        }

        [Fact]
        public void Write_BinaryPayload_RoundTripsThroughBinaryReader()
        {
            var storage = new FileSystemStoragePort(RootPath);

            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(2513);
                    writer.Write(3);
                    writer.Write(2515);
                }

                payload = buffer.ToArray();
            }

            storage.Write(StateKey, payload);

            byte[]? raw = storage.Read(StateKey);
            Assert.NotNull(raw);
            using (var reader = new BinaryReader(new MemoryStream(raw, writable: false)))
            {
                Assert.Equal(2513, reader.ReadInt32());
                Assert.Equal(3, reader.ReadInt32());
                Assert.Equal(2515, reader.ReadInt32());

                // 恰好 12 字节：读完三个 int 已到流尾。
                Assert.Equal(-1, reader.Read());
            }
        }

        [Fact]
        public void Write_OverwritesExistingPayload()
        {
            var storage = new FileSystemStoragePort(RootPath);

            storage.Write(StateKey, new byte[] { 1 });
            storage.Write(StateKey, new byte[] { 2, 3 });

            byte[]? actual = storage.Read(StateKey);
            Assert.NotNull(actual);
            Assert.Equal(new byte[] { 2, 3 }, actual);
        }

        [Fact]
        public void Write_StoresPayloadAsKeyPlusSavExtension()
        {
            var storage = new FileSystemStoragePort(RootPath);

            storage.Write(StateKey, new byte[] { 1 });

            Assert.True(File.Exists(Path.Combine(RootPath, StateKey + ".sav")));
        }

        [Fact]
        public void Write_LeavesNoTempFileBehind()
        {
            var storage = new FileSystemStoragePort(RootPath);

            storage.Write(StateKey, new byte[] { 1, 2 });

            string[] files = Directory.GetFiles(RootPath);
            int tempCount = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (files[i].EndsWith(".tmp", StringComparison.Ordinal))
                {
                    tempCount++;
                }
            }

            Assert.Equal(0, tempCount);
        }

        [Fact]
        public void Read_MissingKey_ReturnsNull()
        {
            var storage = new FileSystemStoragePort(RootPath);

            Assert.Null(storage.Read("never-written"));
        }

        [Fact]
        public void Read_EmptyPayload_ReturnsEmptyArray()
        {
            var storage = new FileSystemStoragePort(RootPath);
            storage.Write(StateKey, Array.Empty<byte>());

            byte[]? actual = storage.Read(StateKey);

            // 契约：键存在但长度为 0 → 返回空数组（不是 null），由存档服务自行判定"新档"。
            Assert.NotNull(actual);
            Assert.Empty(actual);
        }

        [Fact]
        public void Exists_AfterWrite_ReturnsTrue()
        {
            var storage = new FileSystemStoragePort(RootPath);
            storage.Write(StateKey, new byte[] { 1 });

            Assert.True(storage.Exists(StateKey));
        }

        [Fact]
        public void Exists_BeforeWrite_ReturnsFalse()
        {
            var storage = new FileSystemStoragePort(RootPath);

            Assert.False(storage.Exists(StateKey));
        }

        [Fact]
        public void Delete_ExistingKey_ClearsExistsAndRead()
        {
            var storage = new FileSystemStoragePort(RootPath);
            storage.Write(StateKey, new byte[] { 1 });

            storage.Delete(StateKey);

            Assert.False(storage.Exists(StateKey));
            Assert.Null(storage.Read(StateKey));
        }

        [Fact]
        public void Delete_MissingKey_SilentlySucceeds()
        {
            var storage = new FileSystemStoragePort(RootPath);

            storage.Delete("never-written");

            Assert.Empty(storage.ListKeys());
            Assert.False(storage.Exists("never-written"));
        }

        [Fact]
        public void ListKeys_ReturnsOrdinalAscending()
        {
            var storage = new FileSystemStoragePort(RootPath);
            storage.Write("b", new byte[] { 1 });
            storage.Write("a", new byte[] { 1 });
            storage.Write("Z", new byte[] { 1 });

            // 序数升序：'Z'(90) < 'a'(97) < 'b'(98)；文化相关比较会排成 a,b,Z，这里必须是前者。
            Assert.Equal(new[] { "Z", "a", "b" }, storage.ListKeys());
        }

        [Fact]
        public void ListKeys_IgnoresTempFiles()
        {
            var storage = new FileSystemStoragePort(RootPath);
            storage.Write(StateKey, new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(RootPath, StateKey + ".sav.tmp"), new byte[] { 9 });

            Assert.Equal(new[] { StateKey }, storage.ListKeys());
        }

        [Fact]
        public void ListKeys_EmptyDirectory_ReturnsEmptyArray()
        {
            var storage = new FileSystemStoragePort(RootPath);

            Assert.Empty(storage.ListKeys());
        }

        [Fact]
        public void Constructor_MissingRoot_CreatesDirectory()
        {
            string nested = Path.Combine(RootPath, "nested", "saves");

            var storage = new FileSystemStoragePort(nested);

            Assert.True(Directory.Exists(nested));
            Assert.Empty(storage.ListKeys());
        }

        [Theory]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("a..b")]
        [InlineData("../escape")]
        [InlineData("dir/name")]
        [InlineData("win\\name")]
        [InlineData("win:name")]
        [InlineData("with space")]
        public void Write_IllegalKey_ThrowsArgumentException(string key)
        {
            var storage = new FileSystemStoragePort(RootPath);

            Assert.Throws<ArgumentException>(() => storage.Write(key, new byte[] { 1 }));

            // 被拒绝的键不许落盘任何东西（含临时文件）。
            Assert.Empty(storage.ListKeys());
        }

        [Theory]
        [InlineData("/absolute")]
        [InlineData("a..b")]
        public void Read_IllegalKey_ThrowsArgumentException(string key)
        {
            var storage = new FileSystemStoragePort(RootPath);

            Assert.Throws<ArgumentException>(() => storage.Read(key));
        }

        [Theory]
        [InlineData("../escape")]
        [InlineData("win:name")]
        public void Delete_IllegalKey_ThrowsArgumentException(string key)
        {
            var storage = new FileSystemStoragePort(RootPath);

            Assert.Throws<ArgumentException>(() => storage.Delete(key));
        }
    }
}
