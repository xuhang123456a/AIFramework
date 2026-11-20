using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Replay;
using Xunit;

namespace AIFramework.Core.Tests.Replay
{
    /// <summary>内存版存储端口：只测落盘语义，不碰文件系统。</summary>
    /// <remarks>
    /// <paramref name="reverseKeys"/> 用于验证"列表顺序不依赖端口列举顺序"：
    /// <see cref="IStoragePort.ListKeys"/> 只承诺升序，但落盘实现的排序一旦被误信，
    /// 换一个端口（云端/字典）列表就会乱序，所以测试要能主动打乱它。
    /// </remarks>
    internal sealed class ArchiveTestStorage : IStoragePort
    {
        private readonly Dictionary<string, byte[]> _items = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly bool _reverseKeys;

        internal ArchiveTestStorage(bool reverseKeys = false)
        {
            _reverseKeys = reverseKeys;
        }

        /// <summary>写入次数（用于断言"一次 Save 只落盘一次"）。</summary>
        internal int WriteCount { get; private set; }

        public bool Exists(string key)
        {
            return _items.ContainsKey(key);
        }

        public byte[]? Read(string key)
        {
            return _items.TryGetValue(key, out byte[]? value) ? (byte[])value.Clone() : null;
        }

        public void Write(string key, byte[] data)
        {
            WriteCount++;
            _items[key] = (byte[])data.Clone();
        }

        public void Delete(string key)
        {
            _items.Remove(key);
        }

        public string[] ListKeys()
        {
            var keys = new List<string>(_items.Keys);
            keys.Sort(StringComparer.Ordinal);
            if (_reverseKeys)
            {
                keys.Reverse();
            }

            return keys.ToArray();
        }

        /// <summary>测试钩子：键数量（不经过 List，避免被 Store 的前缀过滤影响）。</summary>
        internal int KeyCount
        {
            get { return _items.Count; }
        }
    }

    /// <summary>
    /// 竞技场录像落盘与列表（T5-4）验收：编解码往返、格式/版本/坏档防御，以及
    /// <see cref="ReplayArchiveStore"/> 的落盘、倒序列表、容量回收、坏档跳过、跨实例读回。
    /// <para>
    /// 覆盖的核心承诺：
    /// </para>
    /// <list type="bullet">
    /// <item>字节布局写死（magic 'AIFR' + 版本 + 头 + 波次段 + 帧段），字段逐项往返；</item>
    /// <item>magic/版本不符、截断、尾部多余、负 count、超上限 count、非法半径一律 <c>false</c> 且给 reason；</item>
    /// <item>槽位键 <c>prefix.levelId-seed-recordedAt</c> 稳定（分隔符必须是端口白名单内的 <c>.</c>），同参数重复保存是覆盖而非新增；</item>
    /// <item>列表倒序、坏档跳过并计数、容量 20 超限删最旧（但绝不删刚保存的那一档）；</item>
    /// <item>同一 storage 重建 Store 仍能列出并读回（跨实例/跨进程重启的语义）。</item>
    /// </list>
    /// </summary>
    public sealed class ReplayArchiveTests
    {
        /// <summary>默认前缀（与 <see cref="ReplayArchiveStore.DefaultPrefix"/> 一致）。</summary>
        private const string Prefix = "replays";

        /// <summary>测试用的固定录制时刻。</summary>
        private const long RecordedAt = 1700000000L;

        // 固定头各字段的字节偏移（与 ReplayArchiveCodec 的格式注释一一对应）：
        private const int OffsetKernelType = 8;
        private const int OffsetLevelId = 12;
        private const int OffsetRandomSeed = 16;
        private const int OffsetRecordedAt = 20;
        private const int OffsetFrameCount = 28;
        private const int OffsetWaveCount = 32;
        private const int FirstWaveOffset = 36;

        /// <summary>往返用的指纹样本：覆盖正负、0 与 long 两端极值（指纹是哈希，负数是常态）。</summary>
        private static readonly long[] SampleFingerprints = { 11L, -22L, 0L, long.MaxValue, long.MinValue };

        // ── 编解码：往返 ───────────────────────────────────────────────

        [Fact]
        public void Encode_WritesAifrMagicAndDocumentedHeaderLayout()
        {
            byte[] data = ReplayArchiveCodec.Encode(5, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            // magic 直接按字节读：写死成 'A' 'I' 'F' 'R'，任何"改成另一种 int32 表示"的改动都会红。
            Assert.Equal((byte)'A', data[0]);
            Assert.Equal((byte)'I', data[1]);
            Assert.Equal((byte)'F', data[2]);
            Assert.Equal((byte)'R', data[3]);

            Assert.Equal(ReplayArchiveCodec.CurrentVersion, BitConverter.ToInt32(data, 4));
            Assert.Equal(5, BitConverter.ToInt32(data, OffsetKernelType));
            Assert.Equal(7, BitConverter.ToInt32(data, OffsetLevelId));
            Assert.Equal(42, BitConverter.ToInt32(data, OffsetRandomSeed));
            Assert.Equal(RecordedAt, BitConverter.ToInt64(data, OffsetRecordedAt));
            Assert.Equal(SampleFingerprints.Length, BitConverter.ToInt32(data, OffsetFrameCount));
            Assert.Equal(3, BitConverter.ToInt32(data, OffsetWaveCount));

            Assert.Equal(
                ReplayArchiveCodec.MinSize + (3 * ReplayArchiveCodec.WaveSpecSize) + (SampleFingerprints.Length * ReplayArchiveCodec.FingerprintSize),
                data.Length);

            // 波次段紧跟 waveCount：第一波 enemyCount=2、radius=0.5f。
            Assert.Equal(2, BitConverter.ToInt32(data, FirstWaveOffset));
            Assert.Equal(0.5f, BitConverter.Int32BitsToSingle(BitConverter.ToInt32(data, FirstWaveOffset + 4)));

            // 帧段在波次段之后：第一帧就是样本第一项。
            int firstFrameOffset = FirstWaveOffset + 3 * ReplayArchiveCodec.WaveSpecSize;
            Assert.Equal(SampleFingerprints[0], BitConverter.ToInt64(data, firstFrameOffset));
        }

        [Fact]
        public void EncodeThenTryDecode_RoundTripsHeaderFields()
        {
            byte[] data = ReplayArchiveCodec.Encode(5, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.True(ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo info, out long[] frames, out string reason), reason);

            Assert.Equal(5, info.KernelType);
            Assert.Equal(7, info.LevelId);
            Assert.Equal(42, info.RandomSeed);
            Assert.Equal(SampleFingerprints.Length, info.FrameCount);
            Assert.Equal(RecordedAt, info.RecordedAtUnixSeconds);
            Assert.Equal(ReplayArchiveCodec.CurrentVersion, info.CodecVersion);

            // codec 不知道存储键：解码出的 SlotKey 一律空串，由 Store 补齐。
            Assert.Equal(string.Empty, info.SlotKey);
            Assert.Equal(SampleFingerprints, frames);
        }

        [Fact]
        public void EncodeThenTryDecode_RoundTripsFingerprintsElementByElement()
        {
            var fingerprints = new long[2400];
            for (int i = 0; i < fingerprints.Length; i++)
            {
                // 包含负数（(long)ulong 强转的常态）与逐帧变化的值。
                fingerprints[i] = (i % 2 == 0 ? 1L : -1L) * (i * 2654435761L);
            }

            byte[] data = ReplayArchiveCodec.Encode(1, 1, 9, ThreeWaves(9), fingerprints, RecordedAt);

            Assert.True(ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo info, out long[] frames, out string reason), reason);
            Assert.Equal(fingerprints.Length, info.FrameCount);
            Assert.Equal(fingerprints.Length, frames.Length);
            for (int i = 0; i < fingerprints.Length; i++)
            {
                Assert.True(fingerprints[i] == frames[i], "第 " + i + " 帧指纹不一致：" + fingerprints[i] + " vs " + frames[i]);
            }
        }

        [Fact]
        public void EncodeThenTryDecode_RoundTripsScript()
        {
            byte[] data = ReplayArchiveCodec.Encode(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.True(
                ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo info, out ReplayScriptData script, out long[] frames, out string reason),
                reason);

            Assert.Equal(42, script.RandomSeed);
            Assert.Equal(3, script.WaveCount);
            Assert.Equal(3, script.Waves.Length);
            Assert.Equal(2, script.Waves[0].EnemyCount);
            Assert.Equal(0.5f, script.Waves[0].Radius);
            Assert.Equal(3, script.Waves[1].EnemyCount);
            Assert.Equal(1.25f, script.Waves[1].Radius);
            Assert.Equal(4, script.Waves[2].EnemyCount);
            Assert.Equal(2f, script.Waves[2].Radius);

            // 走完整解码时元信息与只读头一致（同一段校验，不允许双口径）。
            Assert.Equal(SampleFingerprints.Length, info.FrameCount);
        }

        [Fact]
        public void EncodeThenTryDecode_ZeroFrames_RoundTrips()
        {
            // 竞技场"一开始就收场"的极端局：有脚本、没帧。
            byte[] data = ReplayArchiveCodec.Encode(1, 7, 42, ThreeWaves(42), Array.Empty<long>(), RecordedAt);
            Assert.Equal(ReplayArchiveCodec.MinSize + (3 * ReplayArchiveCodec.WaveSpecSize), data.Length);

            Assert.True(ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo info, out long[] frames, out string reason), reason);
            Assert.Equal(0, info.FrameCount);
            Assert.Empty(frames);
        }

        [Fact]
        public void EncodeThenTryDecode_ZeroWaves_RoundTrips()
        {
            var script = new ReplayScriptData(42, 0, Array.Empty<WaveSpecData>());
            byte[] data = ReplayArchiveCodec.Encode(1, 7, 42, script, SampleFingerprints, RecordedAt);
            Assert.Equal(ReplayArchiveCodec.MinSize + (SampleFingerprints.Length * ReplayArchiveCodec.FingerprintSize), data.Length);

            Assert.True(
                ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo info, out ReplayScriptData decoded, out long[] frames, out string reason),
                reason);

            Assert.Equal(0, decoded.WaveCount);
            Assert.Empty(decoded.Waves);
            Assert.Equal(42, decoded.RandomSeed);
            Assert.Equal(SampleFingerprints.Length, info.FrameCount);
        }

        [Fact]
        public void EncodeThenTryDecode_SingleFrame_RoundTrips()
        {
            var one = new[] { -7L };
            byte[] data = ReplayArchiveCodec.Encode(1, 7, 42, ThreeWaves(42), one, RecordedAt);

            Assert.True(ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo info, out long[] frames, out string reason), reason);
            Assert.Equal(1, info.FrameCount);
            Assert.Equal(one, frames);
        }

        [Fact]
        public void EncodeThenTryReadInfo_MatchesDecodedHeader()
        {
            byte[] data = ReplayArchiveCodec.Encode(5, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.True(ReplayArchiveCodec.TryReadInfo(data, out ReplayArchiveInfo info, out string reason), reason);
            Assert.True(ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo decoded, out long[] frames, out string decodeReason), decodeReason);

            Assert.Equal(decoded.KernelType, info.KernelType);
            Assert.Equal(decoded.LevelId, info.LevelId);
            Assert.Equal(decoded.RandomSeed, info.RandomSeed);
            Assert.Equal(decoded.FrameCount, info.FrameCount);
            Assert.Equal(decoded.RecordedAtUnixSeconds, info.RecordedAtUnixSeconds);
            Assert.Equal(decoded.CodecVersion, info.CodecVersion);
            Assert.Equal(SampleFingerprints.Length, frames.Length);
        }

        // ── 编解码：编码侧拒绝 ─────────────────────────────────────────

        [Fact]
        public void Encode_NullFingerprints_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ReplayArchiveCodec.Encode(1, 7, 42, ThreeWaves(42), null!, RecordedAt));
        }

        [Fact]
        public void Encode_ScriptSeedDiffersFromHeaderSeed_Throws()
        {
            // 落盘只写一份种子；两份种子打架等于制造"回放用错种子"的隐形坏档。
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => ReplayArchiveCodec.Encode(1, 7, 43, ThreeWaves(42), SampleFingerprints, RecordedAt));
            Assert.Equal("script", error.ParamName);
        }

        [Fact]
        public void Encode_NegativeRecordedAt_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ReplayArchiveCodec.Encode(1, 7, 42, ThreeWaves(42), SampleFingerprints, -1L));
        }

        [Fact]
        public void Encode_FrameCountOverLimit_Throws()
        {
            var tooMany = new long[ReplayArchiveCodec.MaxCount + 1];
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ReplayArchiveCodec.Encode(1, 7, 42, ThreeWaves(42), tooMany, RecordedAt));
        }

        [Fact]
        public void Encode_WaveCountOverLimit_Throws()
        {
            var script = new ReplayScriptData(42, ReplayArchiveCodec.MaxCount + 1, new WaveSpecData[ReplayArchiveCodec.MaxCount + 1]);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ReplayArchiveCodec.Encode(1, 7, 42, script, SampleFingerprints, RecordedAt));
        }

        // ── 编解码：解码侧拒绝（坏档必须是 false，不是异常、也不是半截数据） ──

        [Fact]
        public void TryDecode_NullData_ReturnsFalse()
        {
            Assert.False(ReplayArchiveCodec.TryDecode(null, out ReplayArchiveInfo info, out long[] frames, out string reason));
            Assert.Equal(0, info.FrameCount);
            Assert.Empty(frames);
            Assert.Contains("null", reason);
        }

        [Fact]
        public void TryDecode_EmptyData_ReturnsFalse()
        {
            Assert.False(ReplayArchiveCodec.TryDecode(Array.Empty<byte>(), out _, out long[] frames, out string reason));
            Assert.Empty(frames);
            Assert.Contains("长度不足", reason);
        }

        [Fact]
        public void TryDecode_BadMagic_ReturnsFalse()
        {
            byte[] data = WithInt32(Encoded(), 0, 0);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out long[] frames, out string reason));
            Assert.Empty(frames);
            Assert.Contains("magic", reason);
        }

        [Fact]
        public void TryDecode_FutureVersion_ReturnsFalse()
        {
            byte[] data = WithInt32(Encoded(), 4, ReplayArchiveCodec.CurrentVersion + 1);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("过新", reason);
        }

        [Fact]
        public void TryDecode_OlderVersion_ReturnsFalse_WithUpgradeHookReason()
        {
            // CurrentVersion 还是 1：任何更低的版本号（含 0/负数垃圾）都归入"过旧"，
            // 将来提升版本时这条分支就是旧档的唯一入口。
            byte[] data = WithInt32(Encoded(), 4, 0);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("过旧", reason);
        }

        [Fact]
        public void TryDecode_TruncatedData_ReturnsFalse()
        {
            byte[] data = Encoded();
            var truncated = new byte[data.Length - 1];
            Array.Copy(data, truncated, truncated.Length);

            Assert.False(ReplayArchiveCodec.TryDecode(truncated, out _, out _, out string reason));
            Assert.Contains("长度", reason);
        }

        [Fact]
        public void TryDecode_TrailingBytes_ReturnsFalse()
        {
            byte[] data = Encoded();
            var longer = new byte[data.Length + 1];
            Array.Copy(data, longer, data.Length);
            longer[longer.Length - 1] = 0x7F;

            Assert.False(ReplayArchiveCodec.TryDecode(longer, out _, out _, out string reason));
            Assert.Contains("长度", reason);
        }

        [Fact]
        public void TryDecode_NegativeFrameCount_ReturnsFalse()
        {
            byte[] data = WithInt32(Encoded(), OffsetFrameCount, -1);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("frameCount", reason);
        }

        [Fact]
        public void TryDecode_FrameCountOverLimit_ReturnsFalse()
        {
            byte[] data = WithInt32(Encoded(), OffsetFrameCount, ReplayArchiveCodec.MaxCount + 1);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("frameCount", reason);
        }

        [Fact]
        public void TryDecode_NegativeWaveCount_ReturnsFalse()
        {
            byte[] data = WithInt32(Encoded(), OffsetWaveCount, -1);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("waveCount", reason);
        }

        [Fact]
        public void TryDecode_WaveCountOverLimit_ReturnsFalse()
        {
            byte[] data = WithInt32(Encoded(), OffsetWaveCount, ReplayArchiveCodec.MaxCount + 1);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("waveCount", reason);
        }

        [Fact]
        public void TryDecode_NegativeRecordedAt_ReturnsFalse()
        {
            byte[] data = WithInt64(Encoded(), OffsetRecordedAt, -1L);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("时间戳", reason);
        }

        [Fact]
        public void TryDecode_NegativeEnemyCount_ReturnsFalse()
        {
            byte[] data = WithInt32(Encoded(), FirstWaveOffset, -2);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("enemyCount", reason);
        }

        [Fact]
        public void TryDecode_NonFiniteRadius_ReturnsFalse()
        {
            byte[] data = WithSingle(Encoded(), FirstWaveOffset + 4, float.NaN);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("radius", reason);
        }

        [Fact]
        public void TryDecode_NegativeRadius_ReturnsFalse()
        {
            byte[] data = WithSingle(Encoded(), FirstWaveOffset + 4, -0.5f);

            Assert.False(ReplayArchiveCodec.TryDecode(data, out _, out _, out string reason));
            Assert.Contains("radius", reason);
        }

        // ── 结构类型自身的校验（编码器的上游闸门） ─────────────────────

        [Fact]
        public void WaveSpecData_InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaveSpecData(-1, 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaveSpecData(1, -0.5f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaveSpecData(1, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaveSpecData(1, float.PositiveInfinity));
        }

        [Fact]
        public void ReplayScriptData_InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new ReplayScriptData(1, 0, null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayScriptData(1, -1, Array.Empty<WaveSpecData>()));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ReplayScriptData(1, 2, new[] { new WaveSpecData(1, 1f) }));
        }

        [Fact]
        public void ReplayScriptData_CopiesWavesAtConstruction()
        {
            var waves = new[] { new WaveSpecData(2, 0.5f) };
            var script = new ReplayScriptData(42, 1, waves);

            waves[0] = new WaveSpecData(99, 9f);

            Assert.Equal(2, script.Waves[0].EnemyCount);
            Assert.Equal(0.5f, script.Waves[0].Radius);
            Assert.Equal(1, script.WaveCount);
        }

        [Fact]
        public void ReplayScriptData_Default_IsEmptyScript()
        {
            ReplayScriptData script = default;

            Assert.Equal(0, script.RandomSeed);
            Assert.Equal(0, script.WaveCount);
            Assert.Empty(script.Waves);
        }

        // ── Store：键与保存 ───────────────────────────────────────────

        [Fact]
        public void Save_UsesPrefixLevelSeedAndRecordedAtKey()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);

            string key = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.Equal("replays.7-42-1700000000", key);
            Assert.True(storage.Exists(key));
            Assert.Equal(1, storage.WriteCount);
            Assert.Equal(1, storage.KeyCount);
        }

        [Fact]
        public void Save_ThenList_ReturnsSingleEntryWithHeaderFields()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());
            string key = store.Save(3, 9, 777, ThreeWaves(777), SampleFingerprints, RecordedAt);

            ReplayArchiveInfo[] list = store.List();

            ReplayArchiveInfo info = Assert.Single(list);
            Assert.Equal(key, info.SlotKey);
            Assert.Equal(3, info.KernelType);
            Assert.Equal(9, info.LevelId);
            Assert.Equal(777, info.RandomSeed);
            Assert.Equal(SampleFingerprints.Length, info.FrameCount);
            Assert.Equal(RecordedAt, info.RecordedAtUnixSeconds);
            Assert.Equal(ReplayArchiveCodec.CurrentVersion, info.CodecVersion);
            Assert.Equal(0, store.SkippedCount);
        }

        [Fact]
        public void Save_SameParameters_ReusesSlotKeyAndOverwrites()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);

            string first = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);
            string second = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.Equal(first, second);
            Assert.Single(store.List());
            Assert.Equal(2, storage.WriteCount);
            Assert.Equal(1, storage.KeyCount);
        }

        [Fact]
        public void Save_EmptyOrSeparatorSuffixedPrefix_KeepsSingleSeparator()
        {
            // 前缀末尾的分隔符（含误传的目录式 "/"）必须被吃掉，否则会出现 replays..7-… 这种双分隔键。
            var trimmed = new ReplayArchiveStore(new ArchiveTestStorage(), "replays.");
            var slash = new ReplayArchiveStore(new ArchiveTestStorage(), "replays/");
            var fallback = new ReplayArchiveStore(new ArchiveTestStorage(), string.Empty);

            Assert.Equal("replays.7-42-1700000000", trimmed.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt));
            Assert.Equal("replays.7-42-1700000000", slash.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt));
            Assert.Equal("replays.7-42-1700000000", fallback.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt));
        }

        // ── Store：列表排序 ──────────────────────────────────────────

        [Fact]
        public void List_SortsByRecordedAtDescending()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 100L);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 300L);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 200L);

            ReplayArchiveInfo[] list = store.List();

            Assert.Equal(3, list.Length);
            Assert.Equal(300L, list[0].RecordedAtUnixSeconds);
            Assert.Equal(200L, list[1].RecordedAtUnixSeconds);
            Assert.Equal(100L, list[2].RecordedAtUnixSeconds);
        }

        [Fact]
        public void List_DoesNotRelyOnStorageKeyOrder()
        {
            // 端口故意倒序返回键：列表顺序必须由 Store 自己排，换了端口实现也不能乱。
            var store = new ReplayArchiveStore(new ArchiveTestStorage(reverseKeys: true));
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 100L);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 200L);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 300L);

            ReplayArchiveInfo[] list = store.List();

            Assert.Equal(300L, list[0].RecordedAtUnixSeconds);
            Assert.Equal(200L, list[1].RecordedAtUnixSeconds);
            Assert.Equal(100L, list[2].RecordedAtUnixSeconds);
        }

        [Fact]
        public void List_SameRecordedAt_OrdersBySlotKeyAscending()
        {
            // 同一秒内存储两档（不同关卡）：时间打平时按 slotKey 升序兜底，顺序必须稳定可复现。
            var store = new ReplayArchiveStore(new ArchiveTestStorage());
            store.Save(1, 8, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            ReplayArchiveInfo[] list = store.List();

            Assert.Equal(2, list.Length);
            Assert.Equal("replays.7-42-1700000000", list[0].SlotKey);
            Assert.Equal("replays.8-42-1700000000", list[1].SlotKey);
        }

        [Fact]
        public void List_EmptyStorage_ReturnsEmpty()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());

            Assert.Empty(store.List());
            Assert.Equal(0, store.SkippedCount);
        }

        [Fact]
        public void List_IgnoresKeysOutsidePrefix()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);

            // 另一前缀（别的子系统）下的合法录像档：对本报不算坏档，也不该出现在列表里。
            var other = new ReplayArchiveStore(storage, "other");
            other.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.Empty(store.List());
            Assert.Equal(0, store.SkippedCount);
            Assert.Single(other.List());
        }

        // ── Store：坏档跳过 ───────────────────────────────────────────

        [Fact]
        public void List_SkipsTruncatedArchiveAndCountsIt()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 100L);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 200L);

            // 只有 magic、没有头的半截档（写入被掉电截断的典型形态）。
            storage.Write(Prefix + ".7-42-1500", new byte[] { 0x41, 0x49, 0x46, 0x52 });

            ReplayArchiveInfo[] list = store.List();

            Assert.Equal(2, list.Length);
            Assert.Equal(1, store.SkippedCount);
        }

        [Fact]
        public void List_SkipsBadMagicArchiveAndCountsIt()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            storage.Write(Prefix + ".9-9-9", WithInt32(Encoded(), 0, 0));

            ReplayArchiveInfo[] list = store.List();

            Assert.Single(list);
            Assert.Equal(1, store.SkippedCount);
        }

        [Fact]
        public void List_ResetsSkippedCountOnEachCall()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);
            store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);
            storage.Write(Prefix + ".7-42-1500", new byte[] { 1, 2, 3, 4 });

            Assert.Single(store.List());
            Assert.Equal(1, store.SkippedCount);

            // 坏档修掉之后，下一次列举的跳过数必须归零（它是"本次列举"的快照，不是累计计数器）。
            storage.Delete(Prefix + ".7-42-1500");

            Assert.Single(store.List());
            Assert.Equal(0, store.SkippedCount);
        }

        // ── Store：容量回收 ──────────────────────────────────────────

        [Fact]
        public void Save_ExactlyAtCapacity_KeepsAll()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);

            for (int i = 0; i < ReplayArchiveStore.DefaultCapacity; i++)
            {
                store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 1000L + i);
            }

            Assert.Equal(ReplayArchiveStore.DefaultCapacity, store.List().Length);
            Assert.Equal(ReplayArchiveStore.DefaultCapacity, storage.KeyCount);
        }

        [Fact]
        public void Save_OverCapacity_EvictsOldestArchive()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);

            string oldest = string.Empty;
            string newest = string.Empty;
            for (int i = 0; i < ReplayArchiveStore.DefaultCapacity + 1; i++)
            {
                string key = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 1000L + i);
                if (i == 0)
                {
                    oldest = key;
                }

                newest = key;
            }

            ReplayArchiveInfo[] list = store.List();

            Assert.Equal(ReplayArchiveStore.DefaultCapacity, list.Length);
            Assert.Equal(ReplayArchiveStore.DefaultCapacity, storage.KeyCount);
            Assert.DoesNotContain(list, info => info.SlotKey == oldest);
            Assert.Contains(list, info => info.SlotKey == newest);
            Assert.False(storage.Exists(oldest));
            Assert.True(storage.Exists(newest));
        }

        [Fact]
        public void Save_OldestTimestamp_IsNeverEvictedByItsOwnSave()
        {
            // 导入一档更旧的录像：它正是"时间最旧"，但绝不能立刻被自己的 Save 删掉，
            // 否则 Save 返回的是一个已失效的键。
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);

            for (int i = 0; i < ReplayArchiveStore.DefaultCapacity; i++)
            {
                store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 2000L + i);
            }

            string imported = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, 1000L);

            ReplayArchiveInfo[] list = store.List();

            Assert.Equal(ReplayArchiveStore.DefaultCapacity, list.Length);
            Assert.True(storage.Exists(imported));
            Assert.Contains(list, info => info.SlotKey == imported);

            // 被回收的是"别的档里最旧的那一档"（2000），而不是刚存的 1000。
            Assert.False(storage.Exists("replays.7-42-2000"));
            Assert.True(storage.Exists("replays.7-42-2001"));
        }

        // ── Store：读取 / 删除 / 跨实例 ───────────────────────────────

        [Fact]
        public void TryLoadFingerprints_RoundTripsFrames()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());
            string key = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.True(store.TryLoadFingerprints(key, out long[] frames, out string reason), reason);
            Assert.Equal(SampleFingerprints, frames);
        }

        [Fact]
        public void TryLoad_ReturnsScriptAndRealSlotKey()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());
            string key = store.Save(3, 9, 777, ThreeWaves(777), SampleFingerprints, RecordedAt);

            Assert.True(
                store.TryLoad(key, out ReplayArchiveInfo info, out ReplayScriptData script, out long[] frames, out string reason),
                reason);

            // 与 codec 不同，Store 的解码结果必须带真实键（列表/读取用的是同一个键）。
            Assert.Equal(key, info.SlotKey);
            Assert.Equal(3, info.KernelType);
            Assert.Equal(777, info.RandomSeed);
            Assert.Equal(3, script.WaveCount);
            Assert.Equal(777, script.RandomSeed);
            Assert.Equal(2, script.Waves[0].EnemyCount);
            Assert.Equal(0.5f, script.Waves[0].Radius);
            Assert.Equal(SampleFingerprints, frames);
        }

        [Fact]
        public void TryLoadFingerprints_MissingKey_ReturnsFalse()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());

            Assert.False(store.TryLoadFingerprints(Prefix + ".7-42-1700000000", out long[] frames, out string reason));
            Assert.Empty(frames);
            Assert.Contains("不存在", reason);
        }

        [Fact]
        public void TryLoadFingerprints_CorruptArchive_ReturnsFalse()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);
            string key = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            // 覆盖成 magic 正确的截断档：存在、但读不出来（要让上层区分"没有"与"坏了"）。
            storage.Write(key, new byte[] { 0x41, 0x49, 0x46, 0x52, 0x01, 0x00, 0x00, 0x00 });

            Assert.False(store.TryLoadFingerprints(key, out long[] frames, out string reason));
            Assert.Empty(frames);
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void Delete_ExistingKey_RemovesItAndReturnsTrue()
        {
            var storage = new ArchiveTestStorage();
            var store = new ReplayArchiveStore(storage);
            string key = store.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            Assert.True(store.Delete(key));
            Assert.False(storage.Exists(key));
            Assert.Empty(store.List());
        }

        [Fact]
        public void Delete_MissingKey_ReturnsFalse()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());

            Assert.False(store.Delete(Prefix + ".7-42-1700000000"));
        }

        [Fact]
        public void StoreRebuiltOnSameStorage_SeesPreviousArchives()
        {
            // 跨实例语义 = 跨进程重启：新 Store 只拿到同一个端口，必须能列出并读回旧档。
            var storage = new ArchiveTestStorage();
            var first = new ReplayArchiveStore(storage);
            string key = first.Save(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);

            var reopened = new ReplayArchiveStore(storage);
            ReplayArchiveInfo info = Assert.Single(reopened.List());

            Assert.Equal(key, info.SlotKey);
            Assert.Equal(SampleFingerprints.Length, info.FrameCount);
            Assert.True(reopened.TryLoadFingerprints(key, out long[] frames, out string reason), reason);
            Assert.Equal(SampleFingerprints, frames);
        }

        [Fact]
        public void StoreInvalidArguments_Throw()
        {
            var store = new ReplayArchiveStore(new ArchiveTestStorage());

            Assert.Throws<ArgumentNullException>(() => new ReplayArchiveStore(null!));
            Assert.Throws<ArgumentNullException>(() => store.TryLoadFingerprints(null!, out _, out _));
            Assert.Throws<ArgumentException>(() => store.TryLoadFingerprints(string.Empty, out _, out _));
            Assert.Throws<ArgumentNullException>(() => store.Delete(null!));
            Assert.Throws<ArgumentException>(() => store.Delete(string.Empty));
        }

        // ── 辅助 ─────────────────────────────────────────────────────

        /// <summary>三波脚本（敌人 2/3/4，半径 0.5/1.25/2）：覆盖整数字段与浮点字段的往返。</summary>
        private static ReplayScriptData ThreeWaves(int seed)
        {
            return new ReplayScriptData(
                seed,
                3,
                new[]
                {
                    new WaveSpecData(2, 0.5f),
                    new WaveSpecData(3, 1.25f),
                    new WaveSpecData(4, 2f),
                });
        }

        /// <summary>一档"全字段都有值"的合法档：kernel=1、level=7、seed=42、3 波、5 帧。</summary>
        private static byte[] Encoded()
        {
            return ReplayArchiveCodec.Encode(1, 7, 42, ThreeWaves(42), SampleFingerprints, RecordedAt);
        }

        /// <summary>拷贝并覆盖一个 int32 字段（模拟坏档/版本漂移）。</summary>
        private static byte[] WithInt32(byte[] source, int offset, int value)
        {
            var copy = (byte[])source.Clone();
            BitConverter.GetBytes(value).CopyTo(copy, offset);
            return copy;
        }

        /// <summary>拷贝并覆盖一个 int64 字段。</summary>
        private static byte[] WithInt64(byte[] source, int offset, long value)
        {
            var copy = (byte[])source.Clone();
            BitConverter.GetBytes(value).CopyTo(copy, offset);
            return copy;
        }

        /// <summary>拷贝并覆盖一个 float 字段（按位模式，保证 NaN 不会变成 0）。</summary>
        private static byte[] WithSingle(byte[] source, int offset, float value)
        {
            return WithInt32(source, offset, BitConverter.SingleToInt32Bits(value));
        }
    }
}
