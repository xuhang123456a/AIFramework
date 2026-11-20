using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AIFramework.Contracts.Save;
using AIFramework.Core.Save;
using Xunit;

namespace AIFramework.Core.Tests.Save
{
    /// <summary>
    /// <see cref="SaveProtector"/> 的无头测试。
    /// <para>
    /// <b>为什么这里要"用另一套实现独立复算"</b>：签名/密钥派生这类代码，最没价值的测试就是"调用
    /// <c>DeriveKey</c> 再断言它等于 <c>DeriveKey</c>"——它只能证明代码没被删掉。本文件里的密钥与 HMAC 期望值
    /// 全部用 <see cref="SHA256"/> / <see cref="HMACSHA256"/> <b>在本文件内独立拼出来</b>，
    /// 外加一条由 Python 独立生成的黄金向量：C# 实现与外部实现逐字节一致，才说明"服务端/别的语言也能复现同一份签名"。
    /// </para>
    /// <para>
    /// <b>覆盖重点</b>：容器布局的每个字段偏移（写死成字面量，规约漂移会直接红）、密钥派生的每一个输入维度
    /// （pepper 有无/长度、seed 正负零）、五类失败原因<b>互不混淆</b>（尤其"换设备"绝不能报成"被篡改"）、
    /// 以及"任何单字节改动都不会被静默接受"这条性质。
    /// </para>
    /// </summary>
    public sealed class SaveProtectorTests
    {
        /// <summary>写档用的 pepper（16 字节 ASCII，仅测试）。</summary>
        private static readonly byte[] Pepper = Encoding.UTF8.GetBytes("aiframework-t8-5");

        /// <summary>另一个 pepper（模拟换渠道/密钥轮换后没做旧档兼容）。</summary>
        private static readonly byte[] OtherPepper = Encoding.UTF8.GetBytes("aiframework-t8-5-other");

        /// <summary>固定的设备种子（0x0102030405060708，正数且高位不为零，便于肉眼核对小端字节序）。</summary>
        private const long Seed = 0x0102030405060708L;

        /// <summary>黄金向量的明文（18 字节，含空格，便于发现偏移错位）。</summary>
        private const string GoldenPlainText = "AIFS golden vector";

        /// <summary>
        /// 黄金向量整包十六进制：由 <b>Python</b>（hashlib/hmac/struct）按同一份规约独立算出，
        /// 不是本实现跑出来再抄回去的。任何"改了签名范围/字节序/截断位置"的重构都会立刻被它抓住。
        /// </summary>
        private const string GoldenContainerHex =
            "4149465301000000080706050403020112000000" +
            "df548c8a679fc814e771234cb0f67521" +
            "4f7ea155c33adbb2fad578e1bf359bd0" +
            "4149465320676f6c64656e20766563746f72";

        // ---------------------------------------------------------------- 往返与确定性

        [Fact]
        public void ProtectThenUnprotect_RoundTripsPayload()
        {
            SaveProtector protector = Create();

            byte[] container = protector.Protect(SamplePlain(), Seed);
            SaveProtectionResult result = protector.TryUnprotect(container, Seed, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.Ok, result);
            Assert.Equal(SamplePlain(), plain);
            Assert.Equal(SaveProtector.ReasonOk, reason);
            Assert.Equal(string.Empty, reason);
        }

        [Fact]
        public void ProtectThenUnprotect_RoundTripsArbitraryBytes()
        {
            SaveProtector protector = Create();

            // 0x00 / 0xFF / 孤立高位字节（非 UTF-8）/ 中文 UTF-8：二进制负载不能因为"看起来像文本"而被特殊对待。
            var payload = new List<byte> { 0x00, 0xFF, 0x00, 0x80, 0x7F, 0x00 };
            payload.AddRange(Encoding.UTF8.GetBytes("存档·英雄联盟"));
            payload.Add(0x00);
            byte[] plainIn = payload.ToArray();

            byte[] container = protector.Protect(plainIn, Seed);
            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(container, Seed, out byte[]? plainOut, out _));
            Assert.Equal(plainIn, plainOut);
        }

        [Fact]
        public void ProtectThenUnprotect_OneMegabytePayload_RoundTrips()
        {
            SaveProtector protector = Create();

            var payload = new byte[1024 * 1024];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)((i * 31) + 7);
            }

            byte[] container = protector.Protect(payload, Seed);

            Assert.Equal(SaveProtector.HeaderSize + payload.Length, container.Length);
            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(container, Seed, out byte[]? plain, out _));
            Assert.Equal(payload, plain);
        }

        [Theory]
        [InlineData(0L)]
        [InlineData(-1L)]
        [InlineData(long.MinValue)]
        [InlineData(long.MaxValue)]
        public void ProtectThenUnprotect_AnySeed_RoundTrips(long seed)
        {
            SaveProtector protector = Create();

            byte[] container = protector.Protect(SamplePlain(), seed);

            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(container, seed, out byte[]? plain, out _));
            Assert.Equal(SamplePlain(), plain);
        }

        [Fact]
        public void Protect_SamePlainSameSeed_ProducesIdenticalContainer()
        {
            SaveProtector protector = Create();

            // 不含随机、不读时钟：同样的输入必须得到同样的字节，否则"同一份存档两次写盘不一样"会让
            // 云同步/幂等/字节级比对全部失效。
            Assert.Equal(protector.Protect(SamplePlain(), Seed), protector.Protect(SamplePlain(), Seed));
        }

        [Fact]
        public void Protect_DifferentInstancesWithSamePepper_ProduceIdenticalContainer()
        {
            byte[] first = Create().Protect(SamplePlain(), Seed);
            byte[] second = Create().Protect(SamplePlain(), Seed);

            Assert.Equal(first, second);
        }

        [Fact]
        public void Protect_DifferentSeed_ProducesDifferentContainer()
        {
            SaveProtector protector = Create();

            byte[] a = protector.Protect(SamplePlain(), Seed);
            byte[] b = protector.Protect(SamplePlain(), Seed + 1);

            Assert.NotEqual(a, b);
            // 种子字段本身就是明文，两边的头必然不同；这里更关心签名也跟着变（换了密钥）。
            Assert.NotEqual(Slice(a, SaveProtector.SignatureOffset, SaveProtector.HmacSize), Slice(b, SaveProtector.SignatureOffset, SaveProtector.HmacSize));
        }

        [Fact]
        public void Protect_DifferentPepper_ProducesDifferentContainer()
        {
            byte[] a = Create(Pepper).Protect(SamplePlain(), Seed);
            byte[] b = Create(OtherPepper).Protect(SamplePlain(), Seed);

            // 头部的版本/种子/长度完全一致，只有签名域不同——这正是"pepper 参与密钥派生"的证据。
            Assert.Equal(Slice(a, 0, SaveProtector.MacFieldOffset), Slice(b, 0, SaveProtector.MacFieldOffset));
            Assert.NotEqual(Slice(a, SaveProtector.MacFieldOffset, SaveProtector.MacSize), Slice(b, SaveProtector.MacFieldOffset, SaveProtector.MacSize));
        }

        [Fact]
        public void Protect_DifferentPlainOfSameLength_ProducesDifferentSignature()
        {
            SaveProtector protector = Create();

            byte[] a = protector.Protect(Encoding.UTF8.GetBytes("aaaaaaaaaaaaaaaa"), Seed);
            byte[] b = protector.Protect(Encoding.UTF8.GetBytes("baaaaaaaaaaaaaaa"), Seed);

            Assert.Equal(a.Length, b.Length);
            Assert.NotEqual(Slice(a, SaveProtector.SignatureOffset, SaveProtector.HmacSize), Slice(b, SaveProtector.SignatureOffset, SaveProtector.HmacSize));
        }

        [Fact]
        public void Protect_DoesNotAliasInputPlain()
        {
            SaveProtector protector = Create();
            byte[] plain = SamplePlain();
            byte[] container = protector.Protect(plain, Seed);
            byte[] snapshot = (byte[])container.Clone();

            plain[0] ^= 0xFF;

            // 容器是独立副本：调用方事后改明文（比如复用缓冲区）不能污染已产出的字节。
            Assert.Equal(snapshot, container);
        }

        [Fact]
        public void TryUnprotect_ReturnsIndependentCopyOfPlain()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);
            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(container, Seed, out byte[]? plain, out _));

            // 解密出的明文与容器不共享缓冲区：容器是磁盘上的临时缓冲，随时可能被复用/覆盖。
            int plainOffset = SaveProtector.HeaderSize;
            container[plainOffset] ^= 0xFF;

            Assert.Equal(SamplePlain(), plain);
        }

        [Fact]
        public void Constructor_CopiesPepperArgument()
        {
            byte[] mutablePepper = (byte[])Pepper.Clone();
            var protector = new SaveProtector(mutablePepper);
            byte[] before = protector.Protect(SamplePlain(), Seed);

            mutablePepper[0] ^= 0xFF;

            // 构造时拷贝：调用方事后改自己的 pepper 数组不影响已构造实例（否则密钥会在运行期悄悄漂移）。
            Assert.Equal(before, protector.Protect(SamplePlain(), Seed));
            Assert.Equal(Create().Protect(SamplePlain(), Seed), before);
        }

        // ---------------------------------------------------------------- 容器布局

        [Fact]
        public void Protect_WritesDocumentedHeaderLayout()
        {
            SaveProtector protector = Create();
            byte[] plain = SamplePlain();
            byte[] container = protector.Protect(plain, Seed);

            // 偏移用字面量写死：这是"格式规约"，不是"实现细节"。
            Assert.Equal(new byte[] { (byte)'A', (byte)'I', (byte)'F', (byte)'S' }, Slice(container, 0, 4));
            Assert.Equal(SaveProtector.CurrentFormatVersion, BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(4)));
            Assert.Equal(Seed, BinaryPrimitives.ReadInt64LittleEndian(container.AsSpan(8)));
            Assert.Equal(plain.Length, BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(16)));
        }

        [Fact]
        public void Protect_ContainerLength_IsHeaderPlusPlain()
        {
            SaveProtector protector = Create();

            byte[] shortContainer = protector.Protect(Encoding.UTF8.GetBytes("x"), Seed);
            byte[] longContainer = protector.Protect(new byte[4096], Seed);

            Assert.Equal(SaveProtector.HeaderSize + 1, shortContainer.Length);
            Assert.Equal(SaveProtector.HeaderSize + 4096, longContainer.Length);
            Assert.Equal(SaveProtector.HeaderSize, 52);
            Assert.Equal(SaveProtector.MagicSize, 4);
            Assert.Equal(SaveProtector.MacSize, 32);
            Assert.Equal(SaveProtector.MacFieldOffset, 20);
            Assert.Equal(SaveProtector.KeyCheckOffset, 20);
            Assert.Equal(SaveProtector.SignatureOffset, 36);
            Assert.Equal(SaveProtector.KcvSize + SaveProtector.HmacSize, SaveProtector.MacSize);
        }

        [Fact]
        public void Protect_PlainIsStoredVerbatimAfterHeader()
        {
            SaveProtector protector = Create();
            byte[] plain = SamplePlain();

            byte[] container = protector.Protect(plain, Seed);

            // 明文不加密、不压缩：容器只加头。这是刻意设计（加密由别的层负责，见类注释的威胁模型）。
            Assert.Equal(plain, Slice(container, SaveProtector.HeaderSize, plain.Length));
        }

        [Fact]
        public void FormatVersion_IsOne_AndExposedByContract()
        {
            ISaveProtector contract = Create();

            Assert.Equal(1, SaveProtector.CurrentFormatVersion);
            Assert.Equal(SaveProtector.CurrentFormatVersion, contract.FormatVersion);
        }

        [Fact]
        public void SaveProtector_IsUsableThroughContract()
        {
            // 装配入口只会看到 ISaveProtector：用接口引用走一遍完整往返，保证契约本身可被消费。
            ISaveProtector protector = Create(Pepper);

            byte[] container = protector.Protect(SamplePlain(), Seed);
            SaveProtectionResult result = protector.TryUnprotect(container, Seed, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.Ok, result);
            Assert.Equal(SamplePlain(), plain);
            Assert.Equal(string.Empty, reason);
        }

        // ---------------------------------------------------------------- 密钥派生与签名（独立复算）

        [Fact]
        public void DeriveKey_IsSha256OfPepperFollowedByLittleEndianSeed()
        {
            byte[] expected = Sha256(Concat(Pepper, SeedLittleEndian(Seed)));

            byte[] actual = SaveProtector.DeriveKey(Pepper, Seed);

            Assert.Equal(SaveProtector.KeySize, actual.Length);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void DeriveKey_NullPepper_EqualsThirtyTwoZeroBytes()
        {
            Assert.Equal(SaveProtector.DeriveKey(new byte[32], Seed), SaveProtector.DeriveKey(null, Seed));
        }

        [Fact]
        public void DeriveKey_EmptyPepper_EqualsNull()
        {
            Assert.Equal(SaveProtector.DeriveKey(null, Seed), SaveProtector.DeriveKey(Array.Empty<byte>(), Seed));
        }

        [Fact]
        public void DeriveKey_DependsOnEveryInput()
        {
            byte[] baseline = SaveProtector.DeriveKey(Pepper, Seed);

            Assert.NotEqual(baseline, SaveProtector.DeriveKey(OtherPepper, Seed));
            Assert.NotEqual(baseline, SaveProtector.DeriveKey(Pepper, Seed + 1));
            Assert.NotEqual(baseline, SaveProtector.DeriveKey(null, Seed));
            // seed 的字节序进签名：+1 与 -1 必须得到不同密钥（小端写入，不是字符串拼接）。
            Assert.NotEqual(SaveProtector.DeriveKey(Pepper, -1L), SaveProtector.DeriveKey(Pepper, 1L));
        }

        [Fact]
        public void Protect_SignatureMatchesIndependentHmacComputation()
        {
            SaveProtector protector = Create();
            byte[] plain = SamplePlain();
            byte[] container = protector.Protect(plain, Seed);

            // 独立复算：message = 版本(4) + 种子(8) + 长度(4) + 明文；key = DeriveKey(pepper, seed)。
            byte[] message = Concat(
                Int32LittleEndian(SaveProtector.CurrentFormatVersion),
                SeedLittleEndian(Seed),
                Int32LittleEndian(plain.Length),
                plain);
            byte[] expectedFull = Hmac(SaveProtector.DeriveKey(Pepper, Seed), message);

            Assert.Equal(
                Slice(expectedFull, 0, SaveProtector.HmacSize),
                Slice(container, SaveProtector.SignatureOffset, SaveProtector.HmacSize));
        }

        [Fact]
        public void Protect_KeyCheckMatchesIndependentComputation()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);

            // 校验值只依赖密钥与头部（"AIFS-KCV" || 版本 || 种子），与内容无关——这是它能区分
            // "密钥不对"与"内容被改"的全部原因。
            byte[] message = Concat(
                Encoding.ASCII.GetBytes("AIFS-KCV"),
                Int32LittleEndian(SaveProtector.CurrentFormatVersion),
                SeedLittleEndian(Seed));
            byte[] expected = Slice(Hmac(SaveProtector.DeriveKey(Pepper, Seed), message), 0, SaveProtector.KcvSize);

            Assert.Equal(expected, Slice(container, SaveProtector.KeyCheckOffset, SaveProtector.KcvSize));
        }

        [Fact]
        public void Protect_GoldenVector_MatchesExternalImplementation()
        {
            // 这条向量由 Python 独立算出（见 GoldenContainerHex 的注释）：它同时钉住
            // 字节序、字段顺序、KCV 标签、HMAC 截断位置，以及"密钥派生用的是 seed 小端字节"。
            SaveProtector protector = Create(Pepper);

            byte[] container = protector.Protect(Encoding.ASCII.GetBytes(GoldenPlainText), Seed);

            Assert.Equal(GoldenContainerHex, ToHex(container));

            // 反向也要成立：外部实现产出的容器，本实现必须能验签并解出明文。
            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(FromHex(GoldenContainerHex), Seed, out byte[]? plain, out _));
            Assert.Equal(Encoding.ASCII.GetBytes(GoldenPlainText), plain);
        }

        // ---------------------------------------------------------------- 结构损坏（Malformed / UnsupportedVersion）

        [Fact]
        public void TryUnprotect_NullContainer_ReturnsMissing()
        {
            SaveProtector protector = Create();

            SaveProtectionResult result = protector.TryUnprotect(null, Seed, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.Missing, result);
            Assert.Null(plain);
            Assert.Equal(SaveProtector.ReasonMissing, reason);
            Assert.Equal(1, (byte)SaveProtectionResult.Missing);
        }

        [Fact]
        public void TryUnprotect_EmptyContainer_ReturnsMalformed()
        {
            SaveProtector protector = Create();

            Assert.Equal(SaveProtectionResult.Malformed, protector.TryUnprotect(Array.Empty<byte>(), Seed, out byte[]? plain, out string reason));
            Assert.Null(plain);
            Assert.Equal(SaveProtector.ReasonEmpty, reason);
        }

        [Fact]
        public void TryUnprotect_ShorterThanHeader_ReturnsMalformed()
        {
            SaveProtector protector = Create();

            // 只差 1 字节 + 只有 magic 两种"半截文件"：都必须在读字段之前被挡掉（否则会读越界/读出垃圾版本号）。
            Assert.Equal(SaveProtectionResult.Malformed, protector.TryUnprotect(new byte[SaveProtector.HeaderSize - 1], Seed, out _, out string tooShortReason));
            Assert.Equal(SaveProtector.ReasonTooShort, tooShortReason);

            Assert.Equal(SaveProtectionResult.Malformed, protector.TryUnprotect(new byte[4], Seed, out _, out string magicOnlyReason));
            Assert.Equal(SaveProtector.ReasonTooShort, magicOnlyReason);
        }

        [Fact]
        public void TryUnprotect_WrongMagic_ReturnsMalformed()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);
            container[0] = (byte)'X'; // 典型场景：把别的子系统的文件塞进了存档槽位

            Assert.Equal(SaveProtectionResult.Malformed, protector.TryUnprotect(container, Seed, out byte[]? plain, out string reason));
            Assert.Null(plain);
            Assert.Equal(SaveProtector.ReasonBadMagic, reason);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(-1)]
        public void TryUnprotect_UnsupportedVersion_ReturnsUnsupportedVersion(int version)
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);
            BinaryPrimitives.WriteInt32LittleEndian(container.AsSpan(4), version);

            SaveProtectionResult result = protector.TryUnprotect(container, Seed, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.UnsupportedVersion, result);
            Assert.Null(plain);
            Assert.StartsWith(SaveProtector.ReasonVersionMismatchPrefix, reason, StringComparison.Ordinal);
            Assert.Contains("v" + version, reason, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(1)]                 // 声明比实际多 1 字节（明文被截断）
        [InlineData(-1)]                // 声明比实际少 1 字节（尾部多出字节）
        [InlineData(0)]                 // 空明文
        [InlineData(-7)]                // 负数
        [InlineData(int.MaxValue)]      // 溢出诱饵：52 + int.MaxValue 在 int 里会溢出成负数
        public void TryUnprotect_DeclaredLengthMismatch_ReturnsMalformed(int delta)
        {
            SaveProtector protector = Create();
            byte[] plain = SamplePlain();
            byte[] container = protector.Protect(plain, Seed);
            int declared = delta == int.MaxValue || delta == -7 || delta == 0 ? delta : plain.Length + delta;
            BinaryPrimitives.WriteInt32LittleEndian(container.AsSpan(16), declared);

            SaveProtectionResult result = protector.TryUnprotect(container, Seed, out byte[]? plainOut, out string reason);

            Assert.Equal(SaveProtectionResult.Malformed, result);
            Assert.Null(plainOut);
            Assert.True(
                reason.StartsWith(SaveProtector.ReasonLengthMismatchPrefix, StringComparison.Ordinal)
                    || reason.StartsWith(SaveProtector.ReasonDeclaredLengthInvalidPrefix, StringComparison.Ordinal),
                "长度类失败必须落在这两个前缀之一，实际：" + reason);
        }

        [Fact]
        public void TryUnprotect_DeclaredLengthOverLimit_ReturnsMalformedWithoutHashing()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);
            byte[] mutated = PatchDeclaredLength(container, SaveProtector.MaxPlainLength + 1);

            // 上限闸门必须在拼签名消息之前：否则一个声明 2 GiB 的垃圾文件，
            // 会在"被拒绝"之前先把内存打爆。这里容器实际只有几十字节，断言的就是"没按声明去分配"。
            SaveProtectionResult result = protector.TryUnprotect(mutated, Seed, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.Malformed, result);
            Assert.Null(plain);
            Assert.StartsWith(SaveProtector.ReasonDeclaredLengthInvalidPrefix, reason, StringComparison.Ordinal);
        }

        [Fact]
        public void TryUnprotect_TruncatedPlain_ReturnsMalformed()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);

            // 断电半截写最常见的形态：文件长度比声明少几个字节。
            byte[] truncated = Slice(container, 0, container.Length - 1);

            Assert.Equal(SaveProtectionResult.Malformed, protector.TryUnprotect(truncated, Seed, out byte[]? plain, out string reason));
            Assert.Null(plain);
            Assert.StartsWith(SaveProtector.ReasonLengthMismatchPrefix, reason, StringComparison.Ordinal);
        }

        [Fact]
        public void TryUnprotect_TrailingGarbage_ReturnsMalformed()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);

            Assert.Equal(SaveProtectionResult.Malformed, protector.TryUnprotect(Concat(container, new byte[] { 0x00 }), Seed, out _, out _));
        }

        // ---------------------------------------------------------------- 篡改（Tampered）

        [Theory]
        [InlineData(0)]     // 明文第一个字节
        [InlineData(-1)]    // 明文最后一个字节
        [InlineData(-5)]    // 中间
        public void TryUnprotect_TamperedPlain_ReturnsTampered(int relativeIndex)
        {
            SaveProtector protector = Create();
            byte[] plain = SamplePlain();
            byte[] container = protector.Protect(plain, Seed);
            int offset = relativeIndex < 0 ? container.Length + relativeIndex : SaveProtector.HeaderSize + relativeIndex;
            container[offset] ^= 0x01;

            SaveProtectionResult result = protector.TryUnprotect(container, Seed, out byte[]? plainOut, out string reason);

            Assert.Equal(SaveProtectionResult.Tampered, result);
            Assert.Null(plainOut);
            Assert.Equal(SaveProtector.ReasonTampered, reason);
        }

        [Fact]
        public void TryUnprotect_TamperedSignatureField_ReturnsTampered()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);
            container[SaveProtector.SignatureOffset] ^= 0xFF;

            // 密钥校验值仍然匹配（pepper 没变）→ 只能判"内容/签名被改"，不能冤枉成"换设备"。
            Assert.Equal(SaveProtectionResult.Tampered, protector.TryUnprotect(container, Seed, out byte[]? plain, out string reason));
            Assert.Null(plain);
            Assert.Equal(SaveProtector.ReasonTampered, reason);
        }

        [Fact]
        public void TryUnprotect_TamperedKeyCheckOnly_StillLoads()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);

            // 已知取舍（见 SaveProtector 类注释）：校验值不在签名覆盖范围内，签名对时根本不看它。
            // 把它改坏不影响读档——这是"宁可少报一次坏档，也要保住换设备判定的准确性"的代价。
            container[SaveProtector.KeyCheckOffset] ^= 0xFF;

            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(container, Seed, out byte[]? plain, out _));
            Assert.Equal(SamplePlain(), plain);
        }

        [Fact]
        public void TryUnprotect_AnySingleByteInHeader_IsNeverSilentlyAccepted()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);

            for (int offset = 0; offset < SaveProtector.HeaderSize; offset++)
            {
                byte[] mutated = (byte[])container.Clone();
                mutated[offset] ^= 0xFF;

                SaveProtectionResult result = protector.TryUnprotect(mutated, Seed, out _, out string reason);

                bool inKeyCheckField = offset >= SaveProtector.KeyCheckOffset
                    && offset < SaveProtector.KeyCheckOffset + SaveProtector.KcvSize;

                if (inKeyCheckField)
                {
                    Assert.True(result == SaveProtectionResult.Ok, "校验值字段（偏移 " + offset + "）已知不在签名范围内，应仍然可读，实际 " + result);
                    continue;
                }

                Assert.True(result != SaveProtectionResult.Ok, "头部偏移 " + offset + " 被改却仍然通过验签（结果 " + result + "）。");
                Assert.NotEqual(string.Empty, reason);
            }
        }

        // ---------------------------------------------------------------- 密钥错误（WrongKey）

        [Fact]
        public void TryUnprotect_CallerSeedMismatch_ReturnsWrongKey()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);
            byte[] snapshot = (byte[])container.Clone();

            SaveProtectionResult result = protector.TryUnprotect(container, Seed + 1, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.WrongKey, result);
            Assert.Null(plain);
            Assert.StartsWith(SaveProtector.ReasonWrongKeySeedPrefix, reason, StringComparison.Ordinal);

            // "不改内容"：换设备失败不得就地"修正"种子或重写任何字节（容器可能要做备份/上报）。
            Assert.Equal(snapshot, container);
        }

        [Fact]
        public void TryUnprotect_TamperedSeedField_ReturnsWrongKey()
        {
            SaveProtector protector = Create();
            byte[] container = protector.Protect(SamplePlain(), Seed);
            container[8] ^= 0xFF; // 改容器里的 deviceSeed（最常见的"搬档到另一台机器"伪造手法）

            SaveProtectionResult result = protector.TryUnprotect(container, Seed, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.WrongKey, result);
            Assert.Null(plain);
            Assert.StartsWith(SaveProtector.ReasonWrongKeySeedPrefix, reason, StringComparison.Ordinal);
        }

        [Fact]
        public void TryUnprotect_DifferentPepper_ReturnsWrongKey()
        {
            byte[] container = Create(Pepper).Protect(SamplePlain(), Seed);

            // 种子一致、pepper 不同（换渠道 / 密钥轮换没做旧档兼容 / 装配时注入了空 pepper）。
            SaveProtectionResult result = Create(OtherPepper).TryUnprotect(container, Seed, out byte[]? plain, out string reason);

            Assert.Equal(SaveProtectionResult.WrongKey, result);
            Assert.Null(plain);
            Assert.Equal(SaveProtector.ReasonWrongKeyPepper, reason);
        }

        [Fact]
        public void TryUnprotect_MissingPepper_ReturnsWrongKeyWhenContainerWasPeppered()
        {
            byte[] container = Create(Pepper).Protect(SamplePlain(), Seed);

            // 最常见的一次真实事故：装配时忘了注入 pepper（走默认全零），所有带 pepper 写出的旧档都会"解不开"。
            // 这种情况必须报 WrongKey（引导检查配置），绝不能报 Tampered（会让人去查改档器）。
            SaveProtectionResult result = Create(null).TryUnprotect(container, Seed, out byte[]? plain, out _);

            Assert.Equal(SaveProtectionResult.WrongKey, result);
            Assert.Null(plain);
        }

        [Fact]
        public void HasPepper_ReportsWhetherKeyMaterialIsConfigured()
        {
            Assert.False(Create(null).HasPepper);
            Assert.False(Create(Array.Empty<byte>()).HasPepper);
            Assert.False(Create(new byte[32]).HasPepper);
            Assert.True(Create(Pepper).HasPepper);
            // 全零但长度不是 32：归一化后仍是"另一套 pepper"，必须算已配置（否则会误报"没配密钥"）。
            Assert.True(Create(new byte[16]).HasPepper);
        }

        // ---------------------------------------------------------------- 入参非法（Protect 必须抛）

        [Fact]
        public void Protect_NullPlain_Throws()
        {
            SaveProtector protector = Create();

            // null 与"空"是两种装配期错误，分别用 ArgumentNullException / ArgumentException 区分。
            Assert.ThrowsAny<ArgumentException>(() => protector.Protect(null!, Seed));
            Assert.Throws<ArgumentNullException>(() => protector.Protect(null!, Seed));
        }

        [Fact]
        public void Protect_EmptyPlain_Throws()
        {
            SaveProtector protector = Create();

            Assert.Throws<ArgumentException>(() => protector.Protect(Array.Empty<byte>(), Seed));
        }

        [Fact]
        public void Protect_PlainOverLimit_Throws()
        {
            SaveProtector protector = Create();
            var oversized = new byte[SaveProtector.MaxPlainLength + 1];

            // 超限必须"写盘前炸"，不能截断：截断会产出一份谁都解析不了的巨档。
            Assert.Throws<ArgumentException>(() => protector.Protect(oversized, Seed));
        }

        [Fact]
        public void Protect_PlainExactlyAtLimit_IsAccepted()
        {
            SaveProtector protector = Create();
            var atLimit = new byte[SaveProtector.MaxPlainLength];

            // 上限是闭区间（含）：边界写错一格就会让"刚好 16 MiB"的正常档被拒。
            byte[] container = protector.Protect(atLimit, Seed);

            Assert.Equal(SaveProtector.HeaderSize + SaveProtector.MaxPlainLength, container.Length);
        }

        // ---------------------------------------------------------------- 失败文案（稳定、可区分）

        [Fact]
        public void FailureReasons_AreStableAndDistinct()
        {
            SaveProtector protector = Create(Pepper);
            byte[] container = protector.Protect(SamplePlain(), Seed);
            SaveProtector otherPepper = Create(OtherPepper);

            (SaveProtectionResult Result, string Reason)[] probes =
            {
                Probe(protector, null, Seed),                                       // Missing
                Probe(protector, Array.Empty<byte>(), Seed),                        // Malformed：空
                Probe(protector, new byte[SaveProtector.HeaderSize - 1], Seed),     // Malformed：短于文件头
                Probe(protector, Mutate(container, 0), Seed),                       // Malformed：magic
                Probe(protector, PatchVersion(container, 2), Seed),                 // UnsupportedVersion
                Probe(protector, PatchDeclaredLength(container, 99), Seed),         // Malformed：长度
                Probe(protector, Mutate(container, 8), Seed),                       // WrongKey：种子
                Probe(otherPepper, container, Seed),                                // WrongKey：pepper
                Probe(protector, Mutate(container, container.Length - 1), Seed),    // Tampered
            };

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach ((SaveProtectionResult result, string reason) in probes)
            {
                Assert.True(reason.Length > 0, "失败必须给出原因（结果 " + result + "）。");
                Assert.True(seen.Add(reason), "两种不同的失败给了同一句原因，客服/埋点无法区分：" + reason);
            }

            // 同一失败重复调用，文案必须完全一致（日志/工单里能按文案聚合）。
            Assert.Equal(Probe(protector, Mutate(container, 0), Seed).Reason, Probe(protector, Mutate(container, 0), Seed).Reason);

            // 成功时是空串而不是 null：调用方可以无脑拼接日志。
            Assert.Equal(string.Empty, Probe(protector, container, Seed).Reason);

            // 结果枚举的数值是契约的一部分（进日志/埋点），这里锁死。
            Assert.Equal(0, (byte)SaveProtectionResult.Ok);
            Assert.Equal(2, (byte)SaveProtectionResult.Tampered);
            Assert.Equal(3, (byte)SaveProtectionResult.WrongKey);
            Assert.Equal(4, (byte)SaveProtectionResult.UnsupportedVersion);
            Assert.Equal(5, (byte)SaveProtectionResult.Malformed);
        }

        // ---------------------------------------------------------------- 与 SaveService 组合

        [Fact]
        public void ComposeWithSaveService_NestedEnvelopeRoundTrips()
        {
            // 真实装配：SaveService 先产出 AISV 包络字节 → 本类 Protect → 落盘。
            // 读盘反过来：TryUnprotect → SaveService.TryLoad（magic/CRC/schema 迁移都在里面）。
            SaveProtector protector = Create(Pepper);
            SaveService saveService = CreateSaveService(out InMemoryStorage storage);
            byte[] payload = Encoding.UTF8.GetBytes("{\"gold\":42}");

            saveService.Save(0, new SaveEnvelope(SaveService.CurrentVersion, payload));
            byte[] aivs = storage.Read(saveService.Key(0))!;
            storage.Write(saveService.Key(0), protector.Protect(aivs, Seed));

            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(storage.Read(saveService.Key(0)), Seed, out byte[]? unprotectPlain, out _));
            storage.Write(saveService.Key(0), unprotectPlain!);

            Assert.True(saveService.TryLoad(0, out SaveEnvelope envelope));
            Assert.Equal(payload, envelope.Payload);
        }

        [Fact]
        public void ComposeWithSaveService_UnsignedPlainTamperIsRejectedByProtector()
        {
            SaveProtector protector = Create(Pepper);
            SaveService saveService = CreateSaveService(out InMemoryStorage storage);

            saveService.Save(0, new SaveEnvelope(SaveService.CurrentVersion, Encoding.UTF8.GetBytes("{\"gold\":42}")));
            byte[] aivs = storage.Read(saveService.Key(0))!;
            byte[] container = protector.Protect(aivs, Seed);

            // 改 AISV 负载里的一个字节（不重算 CRC、不重签）：签名层先挡住，根本走不到 SaveService。
            container[SaveProtector.HeaderSize + SaveService.HeaderSize + 2] ^= 0x01;

            Assert.Equal(SaveProtectionResult.Tampered, protector.TryUnprotect(container, Seed, out byte[]? plain, out _));
            Assert.Null(plain);
        }

        [Fact]
        public void ComposeWithSaveService_ResignedButCorruptPayloadIsRejectedByCrc()
        {
            SaveProtector protector = Create(Pepper);
            SaveService saveService = CreateSaveService(out InMemoryStorage storage);

            saveService.Save(0, new SaveEnvelope(SaveService.CurrentVersion, Encoding.UTF8.GetBytes("{\"gold\":42}")));

            // 极端场景：攻击者已经拿到 pepper（客户端密钥必然可提取），伪造出了"签名合法"的容器。
            // 此时签名层放行——但内层 CRC32 仍会发现负载被改，两层各拦一类，谁也不能替代谁。
            byte[] tamperedAivs = storage.Read(saveService.Key(0))!;
            tamperedAivs[SaveService.HeaderSize + 2] ^= 0x01;
            byte[] forgedContainer = protector.Protect(tamperedAivs, Seed);

            Assert.Equal(SaveProtectionResult.Ok, protector.TryUnprotect(forgedContainer, Seed, out byte[]? plain, out _));
            storage.Write(saveService.Key(0), plain!);

            Assert.False(saveService.TryLoad(0, out _));
        }

        // ---------------------------------------------------------------- 工具方法

        /// <summary>标准测试保护器：<b>带 pepper</b>（正式发行该有的配置，绝大多数用例应走这条路径）。</summary>
        private static SaveProtector Create()
        {
            return new SaveProtector(Pepper);
        }

        /// <summary>指定 pepper 的保护器（含 <c>null</c> / 空 / 全零，用于"密钥没配对"的用例）。</summary>
        private static SaveProtector Create(byte[]? pepper)
        {
            return new SaveProtector(pepper);
        }

        private static SaveService CreateSaveService(out InMemoryStorage storage)
        {
            storage = new InMemoryStorage();
            return new SaveService(storage);
        }

        private static byte[] SamplePlain()
        {
            return Encoding.UTF8.GetBytes("{\"gold\":42,\"hero\":\"影刃\"}");
        }

        private static (SaveProtectionResult Result, string Reason) Probe(SaveProtector protector, byte[]? container, long deviceSeed)
        {
            SaveProtectionResult result = protector.TryUnprotect(container, deviceSeed, out byte[]? plain, out string reason);

            // 失败时明文必须是 null（"失败却带回半份负载"是最危险的返回形态）。
            if (result != SaveProtectionResult.Ok)
            {
                Assert.Null(plain);
            }

            return (result, reason);
        }

        private static byte[] Mutate(byte[] source, int offset)
        {
            byte[] copy = (byte[])source.Clone();
            copy[offset] ^= 0xFF;
            return copy;
        }

        private static byte[] PatchVersion(byte[] source, int version)
        {
            byte[] copy = (byte[])source.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(4), version);
            return copy;
        }

        private static byte[] PatchDeclaredLength(byte[] source, int length)
        {
            byte[] copy = (byte[])source.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(16), length);
            return copy;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(source, offset, slice, 0, length);
            return slice;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                total += parts[i].Length;
            }

            var result = new byte[total];
            int offset = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                Buffer.BlockCopy(parts[i], 0, result, offset, parts[i].Length);
                offset += parts[i].Length;
            }

            return result;
        }

        private static byte[] SeedLittleEndian(long seed)
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, seed);
            return bytes;
        }

        private static byte[] Int32LittleEndian(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return bytes;
        }

        private static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static byte[] Hmac(byte[] key, byte[] message)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(message);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }
    }
}
