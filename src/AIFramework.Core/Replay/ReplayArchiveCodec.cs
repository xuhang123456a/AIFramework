#nullable enable
using System;
using System.Buffers.Binary;
using System.Globalization;

namespace AIFramework.Core.Replay
{
    /// <summary>
    /// 落盘录像里的一波布场参数：数量 + 判定半径。
    /// <para>
    /// <b>为什么 Core 另立一套类型，而不是直接引用适配层的 <c>ArenaReplayScript</c> / <c>WaveSpec</c></b>：
    /// Core 属于纯逻辑程序集（<c>netstandard2.1</c>，编译期零引擎引用），适配层在物理程序集上就在它上面，
    /// 反向引用会立刻违反 <c>AIF004</c> 的拓扑约束。落盘格式只依赖"数量 + 半径"这两个业务字段，
    /// 适配层把 <c>WaveSpec</c> 映射进来只需要一行。
    /// </para>
    /// <para>
    /// <b>构造即校验</b>：负数敌人数、NaN/无穷/负半径都在构造期抛异常，因此任何已存在的
    /// <see cref="WaveSpecData"/> 都是"可落盘"的合法值；解码路径先校验字节再构造，坏档永远变不成实例。
    /// </para>
    /// </summary>
    public readonly struct WaveSpecData
    {
        /// <summary>构造一波参数。</summary>
        /// <param name="enemyCount">本波敌人数量（不包括主角）。</param>
        /// <param name="radius">敌人判定半径（世界单位）。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="enemyCount"/> 为负，或 <paramref name="radius"/> 非有限非负数。</exception>
        public WaveSpecData(int enemyCount, float radius)
        {
            if (enemyCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(enemyCount), enemyCount, "本波敌人数不能为负。");
            }

            if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(radius), radius, "判定半径必须是有限非负数。");
            }

            EnemyCount = enemyCount;
            Radius = radius;
        }

        /// <summary>本波敌人数量（不包括主角）。</summary>
        public int EnemyCount { get; }

        /// <summary>敌人判定半径（世界单位）。</summary>
        public float Radius { get; }
    }

    /// <summary>
    /// 落盘录像的布场脚本（Core 版）：种子 + 每波敌人参数。
    /// <para>
    /// <b>与适配层 <c>ArenaReplayScript</c> 一一对应</b>：<c>RandomSeed</c>、<c>WaveCount</c>、<c>Waves</c> 三个字段同形，
    /// 适配层只需要一行映射（见 <c>ReplayArchiveStore</c> 的类型注释）。位置不进脚本——它是
    /// <c>(序号, 数量)</c> 的纯函数，同一份代码必然算出同一组坐标。
    /// </para>
    /// <para>
    /// <b>防御性复制</b>：构造期只复制前 <paramref name="waveCount"/> 项，调用方之后改自己的数组不影响脚本
    /// （与 <c>ArenaReplayScript</c> 的语义一致，避免"保存后又被改"的隐式耦合）。
    /// </para>
    /// </summary>
    public readonly struct ReplayScriptData
    {
        /// <summary><c>default</c> 实例（未走构造期）用的空波次数组：共享且长度为零，改不动。</summary>
        private static readonly WaveSpecData[] NoWaves = Array.Empty<WaveSpecData>();

        /// <summary>构造期复制出的每波参数；<c>default</c> 实例为 <c>null</c>，经 <see cref="Waves"/> 兜底成空数组。</summary>
        private readonly WaveSpecData[]? _waves;

        /// <summary>
        /// 构造脚本。
        /// </summary>
        /// <param name="randomSeed">本局随机种子。</param>
        /// <param name="waveCount">参与回放的波次数，必须落在 <c>0..waves.Length</c>。</param>
        /// <param name="waves">每波敌人参数；只复制前 <paramref name="waveCount"/> 项。</param>
        /// <exception cref="ArgumentNullException"><paramref name="waves"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="waveCount"/> 为负或超过数组长度。</exception>
        public ReplayScriptData(int randomSeed, int waveCount, WaveSpecData[] waves)
        {
            if (waves == null)
            {
                throw new ArgumentNullException(nameof(waves));
            }

            if (waveCount < 0 || waveCount > waves.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(waveCount), waveCount, "波次数必须落在 0..waves.Length 之间。");
            }

            RandomSeed = randomSeed;
            WaveCount = waveCount;

            _waves = new WaveSpecData[waveCount];
            Array.Copy(waves, _waves, waveCount);
        }

        /// <summary>本局随机种子（第一波用它，后续波次按 <c>种子 + 波次</c> 派生）。</summary>
        public int RandomSeed { get; }

        /// <summary>参与回放的波次数（不含"主角"这个额外单位）。</summary>
        public int WaveCount { get; }

        /// <summary>
        /// 每波敌人参数，长度恒等于 <see cref="WaveCount"/>，<b>返回内部数组</b>（读取零分配）；
        /// 调用方只读，不得改写元素。<c>default</c> 实例返回共享的空数组。
        /// </summary>
        public WaveSpecData[] Waves
        {
            get { return _waves ?? NoWaves; }
        }
    }

    /// <summary>
    /// 落盘录像的元信息（列表页展示用，不加载帧数据）。
    /// <para>
    /// <see cref="SlotKey"/> 是存储端的逻辑键；<see cref="ReplayArchiveCodec"/> 解码时不知道键，
    /// 那里给出的 <see cref="SlotKey"/> 是空串，由 <see cref="ReplayArchiveStore"/> 列表/读取时补齐。
    /// </para>
    /// </summary>
    public readonly struct ReplayArchiveInfo
    {
        /// <summary>构造。</summary>
        /// <param name="slotKey">存储逻辑键。</param>
        /// <param name="kernelType">内核类型码（取值见 <c>GameplayKernelType</c>）。</param>
        /// <param name="levelId">关卡编号。</param>
        /// <param name="randomSeed">本局随机种子。</param>
        /// <param name="frameCount">录像帧数。</param>
        /// <param name="recordedAtUnixSeconds">录制时刻（Unix 秒，由调用方取时间源后传入）。</param>
        /// <param name="codecVersion">落盘格式版本。</param>
        public ReplayArchiveInfo(
            string slotKey,
            int kernelType,
            int levelId,
            int randomSeed,
            int frameCount,
            long recordedAtUnixSeconds,
            int codecVersion)
        {
            SlotKey = slotKey ?? string.Empty;
            KernelType = kernelType;
            LevelId = levelId;
            RandomSeed = randomSeed;
            FrameCount = frameCount;
            RecordedAtUnixSeconds = recordedAtUnixSeconds;
            CodecVersion = codecVersion;
        }

        /// <summary>存储逻辑键（<c>prefix/levelId-randomSeed-recordedAt</c>）。</summary>
        public string SlotKey { get; }

        /// <summary>内核类型码。</summary>
        public int KernelType { get; }

        /// <summary>关卡编号。</summary>
        public int LevelId { get; }

        /// <summary>本局随机种子。</summary>
        public int RandomSeed { get; }

        /// <summary>录像帧数。</summary>
        public int FrameCount { get; }

        /// <summary>录制时刻（Unix 秒）。</summary>
        public long RecordedAtUnixSeconds { get; }

        /// <summary>落盘格式版本（当前恒为 <see cref="ReplayArchiveCodec.CurrentVersion"/>）。</summary>
        public int CodecVersion { get; }

        /// <summary>列表页用的单行摘要（整数字段不涉及小数点，不受区域设置影响）。</summary>
        public override string ToString()
        {
            return SlotKey + " [v" + CodecVersion + "，内核 " + KernelType + "，关卡 " + LevelId
                + "，种子 " + RandomSeed + "，帧 " + FrameCount + "，录制于 " + RecordedAtUnixSeconds + "]";
        }
    }

    /// <summary>
    /// 竞技场录像的落盘编解码：脚本 + 逐帧指纹 + 头信息，全部小端、长度前缀手写。
    /// <para>
    /// <b>字节布局（写死，改它必须升 <see cref="CurrentVersion"/>）</b>：
    /// </para>
    /// <code>
    /// [int32 magic 'AIFR'] [int32 codecVersion] [int32 kernelType] [int32 levelId] [int32 randomSeed]
    /// [int64 recordedAtUnixSeconds] [int32 frameCount]
    /// [int32 waveCount] waveCount × { int32 enemyCount; float32 radius }
    /// frameCount × int64 fingerprint
    /// </code>
    /// <para>
    /// 固定头 <see cref="HeaderSize"/> = 32 字节（到 frameCount 为止），随后 4 字节 waveCount，
    /// 所以最短合法档是 <see cref="MinSize"/> = 36 字节（0 波次 + 0 帧）。
    /// </para>
    /// <para>
    /// <b>为什么自己写小端而不引序列化库</b>：与 <c>InventorySaveCodec</c> 同一口径——纯逻辑库要能在
    /// Unity/HybridCLR 里直接跑，少一个包就少一个 AOT 元数据风险，且字节布局能被回归夹具逐字节冻结。
    /// </para>
    /// <para>
    /// <b>解码是"防御式"的</b>：magic 不符、版本不符、长度不足、尾部多余、负数 count、count 超上限
    /// （<see cref="MaxCount"/>）、时间戳为负、半径非法、波次字段非法，全部返回 <c>false</c> 并给出
    /// <c>reason</c>；坏档绝不会解析成"半截录像"或触发巨额分配。
    /// </para>
    /// <para>
    /// <b>语义校验顺序</b>：magic → 版本 → 两个计数（先判负数/上限，再判长度）。先判计数再判长度是刻意的：
    /// 坏档声明 <c>frameCount = -1</c> 或 <c>100001</c> 时，长度必然也对不上，若先判长度就会把
    /// "计数非法"误报成"长度不符"，诊断信息会指向错误的方向。
    /// </para>
    /// <para>
    /// <b>版本升级钩子</b>：<see cref="TryDecode"/> 只认 <see cref="CurrentVersion"/>。把 <see cref="CurrentVersion"/>
    /// 提到 2 时，v1 旧档会在这里被明确判定为"版本过旧"，升级实现应加在本类的私有解析段之前
    /// （先把旧档迁移到当前字节布局，再走同一条解析路径），而不是在 Store 层做分支。
    /// </para>
    /// </summary>
    public sealed class ReplayArchiveCodec
    {
        /// <summary>落盘格式版本。<b>改变字节布局必须 +1</b>，否则旧档会被当成同格式解析。</summary>
        public const int CurrentVersion = 1;

        /// <summary>帧数/波次数上限：防止坏档声明的超大 count 触发巨额分配（每帧 8 字节）。</summary>
        public const int MaxCount = 100000;

        /// <summary>固定头长度（magic .. frameCount，共 32 字节）。</summary>
        public const int HeaderSize = 32;

        /// <summary>单波字节数（<c>int32 enemyCount</c> + <c>float32 radius</c>）。</summary>
        public const int WaveSpecSize = 8;

        /// <summary>单帧指纹字节数（<c>int64</c>）。</summary>
        public const int FingerprintSize = 8;

        /// <summary>最短合法档长度：固定头 32 + waveCount 4 = 36 字节（0 波次、0 帧）。</summary>
        public const int MinSize = HeaderSize + 4;

        /// <summary>小端下的 <c>'A' 'I' 'F' 'R'</c>（0x41 0x49 0x46 0x52）。</summary>
        private const int MagicValue = 0x52464941;

        private const int OffsetMagic = 0;
        private const int OffsetVersion = 4;
        private const int OffsetKernelType = 8;
        private const int OffsetLevelId = 12;
        private const int OffsetRandomSeed = 16;
        private const int OffsetRecordedAt = 20;
        private const int OffsetFrameCount = 28;
        private const int OffsetWaveCount = 32;

        /// <summary>
        /// 编码：头 + 脚本 + 帧指纹（小端）。
        /// <para>
        /// <b>种子只有一个来源</b>：头里的 <c>randomSeed</c> 与 <paramref name="script"/> 的
        /// <see cref="ReplayScriptData.RandomSeed"/> 必须一致，不一致直接抛——落盘只写一份种子，
        /// 允许两份种子打架等于制造"回放用的种子与录像头对不上"的隐形坏档。
        /// </para>
        /// </summary>
        /// <param name="kernelType">内核类型码。</param>
        /// <param name="levelId">关卡编号。</param>
        /// <param name="randomSeed">本局随机种子（必须等于 <paramref name="script"/> 的种子）。</param>
        /// <param name="script">布场脚本（可为 0 波次）。</param>
        /// <param name="fingerprints">逐帧状态指纹，长度即帧数。</param>
        /// <param name="recordedAtUnixSeconds">录制时刻（Unix 秒），不能为负。</param>
        /// <returns>完整档字节。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fingerprints"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="script"/> 的种子与 <paramref name="randomSeed"/> 不一致。</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// 帧数或波次数超过 <see cref="MaxCount"/>，或 <paramref name="recordedAtUnixSeconds"/> 为负。
        /// </exception>
        public static byte[] Encode(
            int kernelType,
            int levelId,
            int randomSeed,
            ReplayScriptData script,
            long[] fingerprints,
            long recordedAtUnixSeconds)
        {
            if (fingerprints == null)
            {
                throw new ArgumentNullException(nameof(fingerprints));
            }

            int frameCount = fingerprints.Length;
            if (frameCount > MaxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(fingerprints), frameCount, "帧数超过上限 " + MaxCount + "。");
            }

            int waveCount = script.WaveCount;
            if (waveCount > MaxCount)
            {
                throw new ArgumentOutOfRangeException(nameof(script), waveCount, "波次数超过上限 " + MaxCount + "。");
            }

            if (script.RandomSeed != randomSeed)
            {
                throw new ArgumentException(
                    "脚本种子（" + script.RandomSeed + "）与录像头种子（" + randomSeed + "）不一致：档里只保留一份种子，两份打架会让回放用错种子。",
                    nameof(script));
            }

            if (recordedAtUnixSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recordedAtUnixSeconds),
                    recordedAtUnixSeconds,
                    "录制时间戳不能为负（Unix 秒从 1970 起算）：负值只会来自调用方算错。");
            }

            // 上界：36 + 100000×8 + 100000×8 = 1,600,036 字节，int 不会溢出。
            int size = MinSize + waveCount * WaveSpecSize + frameCount * FingerprintSize;
            var data = new byte[size];

            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(OffsetMagic), MagicValue);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(OffsetVersion), CurrentVersion);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(OffsetKernelType), kernelType);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(OffsetLevelId), levelId);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(OffsetRandomSeed), randomSeed);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(OffsetRecordedAt), recordedAtUnixSeconds);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(OffsetFrameCount), frameCount);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(OffsetWaveCount), waveCount);

            WaveSpecData[] waves = script.Waves;
            int offset = MinSize;
            for (int i = 0; i < waveCount; i++)
            {
                WaveSpecData wave = waves[i];
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), wave.EnemyCount);

                // float 按 int32 位模式写（不依赖 BinaryPrimitives 的 float 重载）：位模式往返 bit-exact。
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4, 4), BitConverter.SingleToInt32Bits(wave.Radius));
                offset += WaveSpecSize;
            }

            for (int i = 0; i < frameCount; i++)
            {
                BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(offset, 8), fingerprints[i]);
                offset += FingerprintSize;
            }

            return data;
        }

        /// <summary>
        /// 只解析元信息（列表页用）：<b>不分配帧数组</b>，也不碰帧段字节。
        /// <para>
        /// 与 <see cref="TryDecode(byte[], out ReplayArchiveInfo, out long[], out string)"/> 共用同一段校验，
        /// 所以被它拒掉的字节，全量解码也一定拒掉（区别只在"是否把帧搬进数组"）。
        /// </para>
        /// </summary>
        /// <param name="data">完整档字节。</param>
        /// <param name="info">解出的元信息（<see cref="ReplayArchiveInfo.SlotKey"/> 为空串，键由 Store 补齐）。</param>
        /// <param name="reason">失败原因；成功时为空串。</param>
        /// <returns>是否解析成功。</returns>
        public static bool TryReadInfo(byte[]? data, out ReplayArchiveInfo info, out string reason)
        {
            info = default;

            if (!TryParseHeader(data, out ParsedHeader header, out reason))
            {
                return false;
            }

            info = CreateInfo(string.Empty, in header);
            return true;
        }

        /// <summary>
        /// 解码成元信息 + 帧指纹（脚本不返回；需要脚本时用带 <c>script</c> 的重载）。
        /// </summary>
        /// <param name="data">完整档字节。</param>
        /// <param name="info">解出的元信息（<see cref="ReplayArchiveInfo.SlotKey"/> 为空串）。</param>
        /// <param name="fingerprints">逐帧指纹；失败时为空数组。</param>
        /// <param name="reason">失败原因；成功时为空串。</param>
        /// <returns>是否解码成功。</returns>
        public static bool TryDecode(byte[]? data, out ReplayArchiveInfo info, out long[] fingerprints, out string reason)
        {
            return TryDecode(data, out info, out _, out fingerprints, out reason);
        }

        /// <summary>
        /// 解码成元信息 + 布场脚本 + 帧指纹：<b>真正回放一档需要脚本，所以它必须能被读回来</b>。
        /// </summary>
        /// <param name="data">完整档字节。</param>
        /// <param name="info">解出的元信息（<see cref="ReplayArchiveInfo.SlotKey"/> 为空串）。</param>
        /// <param name="script">解出的布场脚本（种子取自录像头）。</param>
        /// <param name="fingerprints">逐帧指纹；失败时为空数组。</param>
        /// <param name="reason">失败原因；成功时为空串。</param>
        /// <returns>是否解码成功。</returns>
        public static bool TryDecode(
            byte[]? data,
            out ReplayArchiveInfo info,
            out ReplayScriptData script,
            out long[] fingerprints,
            out string reason)
        {
            info = default;
            script = default;
            fingerprints = Array.Empty<long>();

            if (!TryParseHeader(data, out ParsedHeader header, out reason))
            {
                return false;
            }

            // 走到这里说明 data 非空、长度与计数严格相符，且波次字段已逐项校验过，下面的读取不可能越界。
            byte[] bytes = data!;

            var waves = new WaveSpecData[header.WaveCount];
            int offset = MinSize;
            for (int i = 0; i < waves.Length; i++)
            {
                int enemyCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
                float radius = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4)));

                // 构造期校验与 TryParseHeader 完全同口径，这里不会抛。
                waves[i] = new WaveSpecData(enemyCount, radius);
                offset += WaveSpecSize;
            }

            var frames = new long[header.FrameCount];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
                offset += FingerprintSize;
            }

            script = new ReplayScriptData(header.RandomSeed, header.WaveCount, waves);
            info = CreateInfo(string.Empty, in header);
            fingerprints = frames;
            reason = string.Empty;
            return true;
        }

        /// <summary>按头字段拼元信息（codec 不知道键，<paramref name="slotKey"/> 由调用方给）。</summary>
        private static ReplayArchiveInfo CreateInfo(string slotKey, in ParsedHeader header)
        {
            return new ReplayArchiveInfo(
                slotKey,
                header.KernelType,
                header.LevelId,
                header.RandomSeed,
                header.FrameCount,
                header.RecordedAtUnixSeconds,
                header.Version);
        }

        /// <summary>
        /// 固定头的统一校验与解析（零分配）：两个解码出口与 Store 的列表都走这里，
        /// 避免"列表能显示、打开却失败"或反过来的双口径。
        /// </summary>
        private static bool TryParseHeader(byte[]? data, out ParsedHeader header, out string reason)
        {
            header = default;
            reason = string.Empty;

            if (data == null)
            {
                reason = "数据为 null。";
                return false;
            }

            if (data.Length < MinSize)
            {
                reason = "长度不足：最短合法档 " + MinSize + " 字节（固定头 " + HeaderSize + " + 波次数 4），实际 " + data.Length + "。";
                return false;
            }

            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(OffsetMagic, 4)) != MagicValue)
            {
                reason = "magic 不匹配：不是 AIFR 录像档（可能是别的子系统的文件，或纯垃圾字节）。";
                return false;
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(OffsetVersion, 4));
            if (version != CurrentVersion)
            {
                if (version > CurrentVersion)
                {
                    reason = "codec 版本过新：文件 v" + version + " 高于当前 v" + CurrentVersion + "，拒绝半解析（请先升级读档端）。";
                }
                else
                {
                    // 升级钩子位置：CurrentVersion 提到 2 之后，v1 档必须在这里先迁移再落到下面的同一条解析路径。
                    reason = "codec 版本过旧：文件 v" + version + " 低于当前 v" + CurrentVersion + "，当前没有对应升级器。";
                }

                return false;
            }

            int kernelType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(OffsetKernelType, 4));
            int levelId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(OffsetLevelId, 4));
            int randomSeed = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(OffsetRandomSeed, 4));
            long recordedAtUnixSeconds = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(OffsetRecordedAt, 8));
            int frameCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(OffsetFrameCount, 4));
            int waveCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(OffsetWaveCount, 4));

            // 先判计数（负数/上限），再判长度：否则负计数会被误报成"长度不符"。
            if (frameCount < 0 || frameCount > MaxCount)
            {
                reason = "frameCount 非法：声明 " + frameCount + "，合法区间 0.." + MaxCount + "（负数或超上限会让分配失控）。";
                return false;
            }

            if (waveCount < 0 || waveCount > MaxCount)
            {
                reason = "waveCount 非法：声明 " + waveCount + "，合法区间 0.." + MaxCount + "。";
                return false;
            }

            long expectedLength = (long)MinSize + ((long)waveCount * WaveSpecSize) + ((long)frameCount * FingerprintSize);
            if (data.Length != expectedLength)
            {
                reason = "长度与声明计数不符：按 waveCount=" + waveCount + "、frameCount=" + frameCount
                    + " 应为 " + expectedLength + " 字节，实际 " + data.Length + "（截断或多出尾部字节）。";
                return false;
            }

            if (recordedAtUnixSeconds < 0)
            {
                reason = "recordedAtUnixSeconds 为负（" + recordedAtUnixSeconds + "）：录制时间戳不可能早于 1970。";
                return false;
            }

            int offset = MinSize;
            for (int i = 0; i < waveCount; i++)
            {
                int enemyCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                float radius = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4)));

                if (enemyCount < 0)
                {
                    reason = "第 " + i + " 波 enemyCount 为负（" + enemyCount + "）。";
                    return false;
                }

                if (float.IsNaN(radius) || float.IsInfinity(radius) || radius < 0f)
                {
                    reason = "第 " + i + " 波 radius 非法（" + radius.ToString(CultureInfo.InvariantCulture) + "）：必须是有限非负数。";
                    return false;
                }

                offset += WaveSpecSize;
            }

            header = new ParsedHeader(version, kernelType, levelId, randomSeed, frameCount, waveCount, recordedAtUnixSeconds);
            return true;
        }

        /// <summary>固定头解析结果（只在 <see cref="TryParseHeader"/> 成功后存在）。</summary>
        private readonly struct ParsedHeader
        {
            internal ParsedHeader(
                int version,
                int kernelType,
                int levelId,
                int randomSeed,
                int frameCount,
                int waveCount,
                long recordedAtUnixSeconds)
            {
                Version = version;
                KernelType = kernelType;
                LevelId = levelId;
                RandomSeed = randomSeed;
                FrameCount = frameCount;
                WaveCount = waveCount;
                RecordedAtUnixSeconds = recordedAtUnixSeconds;
            }

            internal int Version { get; }

            internal int KernelType { get; }

            internal int LevelId { get; }

            internal int RandomSeed { get; }

            internal int FrameCount { get; }

            internal int WaveCount { get; }

            internal long RecordedAtUnixSeconds { get; }
        }
    }
}
