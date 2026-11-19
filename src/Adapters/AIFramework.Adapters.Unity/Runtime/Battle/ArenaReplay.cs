#nullable enable
using System;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 一局竞技场的可重放脚本：随机种子 + 每波敌人参数（本局不含玩家输入）。
    /// <para>
    /// <b>为什么这里录的是"布场脚本"而不是"意图"</b>：竞技场本身几乎不提交意图——每波只有
    /// <c>Begin</c> + 一次布场（主角/敌人/飞行物的位置、半径、血量、速度），之后完全由固定帧
    /// <c>Step</c> 自动推进；唯一一条运行时意图是超时兜底里的那次自伤。也就是说，
    /// 只要种子、每波敌人数量/半径、布场位置算法与步长一致，"重开一局"就等于"重放这一局"。
    /// 因此脚本只需要记住这些布场参数，逐帧一致性由 <see cref="StateFingerprint"/> 比对兜底。
    /// </para>
    /// <para>
    /// <b>防御性复制</b>：构造期把传入数组复制一份，之后调用方改自己的数组不会影响脚本。
    /// 本类不依赖 Unity，可在纯 .NET 环境直接构造与断言。
    /// </para>
    /// </summary>
    public sealed class ArenaReplayScript
    {
        /// <summary>构造期复制出的每波参数；只读使用，长度恒等于 <see cref="WaveCount"/>。</summary>
        private readonly WaveSpec[] _waves;

        /// <summary>
        /// 构造脚本。
        /// </summary>
        /// <param name="randomSeed">本局随机种子；后续波次按 <c>种子 + 波次</c> 派生（与竞技场开波逻辑一致）。</param>
        /// <param name="waveCount">参与回放的波次数，必须落在 <c>0..waves.Length</c>。</param>
        /// <param name="waves">每波敌人参数；构造期只复制前 <paramref name="waveCount"/> 项。</param>
        /// <exception cref="ArgumentNullException"><paramref name="waves"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="waveCount"/> 为负数或超过数组长度。</exception>
        public ArenaReplayScript(int randomSeed, int waveCount, WaveSpec[] waves)
        {
            if (waves == null)
            {
                throw new ArgumentNullException(nameof(waves));
            }

            if (waveCount < 0 || waveCount > waves.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(waveCount),
                    waveCount,
                    "波次数必须落在 0..waves.Length 之间。");
            }

            RandomSeed = randomSeed;
            WaveCount = waveCount;

            _waves = new WaveSpec[waveCount];
            Array.Copy(waves, _waves, waveCount);
        }

        /// <summary>本局随机种子（第一波用它，后续波次按 <c>种子 + 波次</c> 派生）。</summary>
        public int RandomSeed { get; }

        /// <summary>参与回放的波次数（不含"主角"这个额外单位）。</summary>
        public int WaveCount { get; }

        /// <summary>
        /// 每波敌人参数。<b>返回构造期复制出的内部数组</b>：读取零分配，调用方只读、不得改写元素。
        /// </summary>
        public WaveSpec[] Waves
        {
            get { return _waves; }
        }
    }

    /// <summary>
    /// 一波敌人的布场参数：数量与判定半径。
    /// <para>位置由竞技场的等角圆算法按 <c>(序号, 数量)</c> 唯一决定，因此不需要进脚本；
    /// 速度/伤害/血量是全局常量，同属"同一份代码"的前提，也不进脚本。</para>
    /// </summary>
    public readonly struct WaveSpec
    {
        /// <summary>构造一波参数。</summary>
        /// <param name="enemyCount">本波敌人数量（不包括主角）。</param>
        /// <param name="radius">敌人判定半径（世界单位）。</param>
        public WaveSpec(int enemyCount, float radius)
        {
            EnemyCount = enemyCount;
            Radius = radius;
        }

        /// <summary>本波敌人数量（不包括主角）。</summary>
        public int EnemyCount { get; }

        /// <summary>敌人判定半径（世界单位）。</summary>
        public float Radius { get; }
    }

    /// <summary>
    /// 录制一局的脚本 + 每帧状态指纹；回放时逐帧比对。
    /// <para>
    /// <b>定长预分配</b>：构造期一次性分配 <c>long[]</c>，<see cref="RecordFrame"/> 只写不扩容；
    /// 容量写满后置 <see cref="DroppedFrames"/> 并丢弃后续帧，不抛异常、不覆盖已录内容。
    /// 录制/读取路径零托管分配（构造期除外）。
    /// </para>
    /// <para>
    /// <b>为什么只存指纹</b>：单位/飞行物的完整快照每帧都要复制，内存与拷贝成本都不可控；
    /// 64 位指纹由 <c>StateFingerprint.Of</c> 按稳定顺序折叠全部可观测字段，
    /// 逐帧相等即"逻辑状态逐帧一致"，不相等时再由 <see cref="ArenaReplayVerifier"/> 定位首个差异帧。
    /// </para>
    /// </summary>
    public sealed class ArenaRecorder
    {
        /// <summary>本局可重放的布场脚本。</summary>
        private readonly ArenaReplayScript _script;

        /// <summary>逐帧指纹：定长直线缓冲，只追加、不回卷、不扩容。</summary>
        private readonly long[] _fingerprints;

        /// <summary>已记录帧数（成功写入的帧数，丢弃的帧不计）。</summary>
        private int _frameCount;

        /// <summary>是否发生过容量耗尽（发生即置位，不自动复位）。</summary>
        private bool _droppedFrames;

        /// <summary>
        /// 创建录制器。
        /// </summary>
        /// <param name="script">本局布场脚本。</param>
        /// <param name="frameCapacity">帧容量，默认 2400（覆盖竞技场 1200 帧总上限的两倍余量），必须为正数。</param>
        /// <exception cref="ArgumentNullException"><paramref name="script"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameCapacity"/> 小于等于 0。</exception>
        public ArenaRecorder(ArenaReplayScript script, int frameCapacity = 2400)
        {
            if (script == null)
            {
                throw new ArgumentNullException(nameof(script));
            }

            if (frameCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frameCapacity),
                    frameCapacity,
                    "录制帧容量必须为正数。");
            }

            _script = script;
            _fingerprints = new long[frameCapacity];
            _frameCount = 0;
            _droppedFrames = false;
        }

        /// <summary>本局可重放的布场脚本。</summary>
        public ArenaReplayScript Script
        {
            get { return _script; }
        }

        /// <summary>已成功记录的帧数。</summary>
        public int FrameCount
        {
            get { return _frameCount; }
        }

        /// <summary>是否因容量耗尽丢弃过帧（回放只能比对已录部分）。</summary>
        public bool DroppedFrames
        {
            get { return _droppedFrames; }
        }

        /// <summary>
        /// 记录一帧指纹。<b>容量满后只置 <see cref="DroppedFrames"/> 并丢弃</b>：
        /// 回放尾部被截断是可诊断的降级，不能让整局录制抛异常。
        /// </summary>
        /// <param name="fingerprint">该逻辑帧结束后的状态指纹。</param>
        public void RecordFrame(long fingerprint)
        {
            if (_frameCount >= _fingerprints.Length)
            {
                _droppedFrames = true;
                return;
            }

            _fingerprints[_frameCount] = fingerprint;
            _frameCount++;
        }

        /// <summary>按录制顺序取第 <paramref name="frame"/> 帧指纹。</summary>
        /// <param name="frame">帧下标（从 0 开始）。</param>
        /// <returns>该帧指纹。</returns>
        /// <exception cref="ArgumentOutOfRangeException">下标不在 <c>0..FrameCount-1</c>。</exception>
        public long GetFingerprint(int frame)
        {
            if (frame < 0 || frame >= _frameCount)
            {
                throw new ArgumentOutOfRangeException(nameof(frame), frame, "录制帧下标越界。");
            }

            return _fingerprints[frame];
        }
    }

    /// <summary>
    /// 回放执行器：喂同一脚本重开一局，逐帧比对指纹。
    /// <para>
    /// <b>比对语义</b>：每推进一帧就 <see cref="PushFrame"/> 一次；
    /// 一旦某帧与录制不符，<see cref="IsMatch"/> 置 <c>false</c> 并记录首个不匹配帧，
    /// 之后继续推也不抛异常（保留完整比对计数便于诊断）。
    /// </para>
    /// <para>
    /// <b>多出来的帧</b>：推入帧数超过录制帧数时没有对标对象，<see cref="IsMatch"/> 置 <c>false</c>，
    /// <see cref="FirstMismatchFrame"/> 为 <c>-1</c>（没有可定位的录制帧）。
    /// </para>
    /// <para>热路径（<see cref="PushFrame"/>）零托管分配。</para>
    /// </summary>
    public sealed class ArenaReplayVerifier
    {
        /// <summary>被比对的录制器（只读引用）。</summary>
        private readonly ArenaRecorder _recorded;

        /// <summary>已推入并参与比对的帧数（含录制范围之外的多余帧）。</summary>
        private int _comparedFrames;

        /// <summary>到目前为止是否逐帧一致（出现任何不一致或多余帧都会置 <c>false</c>）。</summary>
        private bool _isMatch;

        /// <summary>首个落在录制范围内的不匹配帧；<c>-1</c> 表示没有可定位的帧内不匹配。</summary>
        private int _firstMismatchFrame;

        /// <summary>
        /// 绑定一份已完成的录制。
        /// </summary>
        /// <param name="recorded">录制结果。</param>
        /// <exception cref="ArgumentNullException"><paramref name="recorded"/> 为 <c>null</c>。</exception>
        public ArenaReplayVerifier(ArenaRecorder recorded)
        {
            if (recorded == null)
            {
                throw new ArgumentNullException(nameof(recorded));
            }

            _recorded = recorded;
            _comparedFrames = 0;
            _isMatch = true;
            _firstMismatchFrame = -1;
        }

        /// <summary>推入的帧数是否已达到（或超过）录制帧数。</summary>
        public bool IsComplete
        {
            get { return _comparedFrames >= _recorded.FrameCount; }
        }

        /// <summary>到目前为止是否逐帧一致（尚未推入任何帧且录制为空时也为 <c>true</c>）。</summary>
        public bool IsMatch
        {
            get { return _isMatch; }
        }

        /// <summary>已推入并参与比对的帧数。</summary>
        public int ComparedFrames
        {
            get { return _comparedFrames; }
        }

        /// <summary>
        /// 首个不匹配帧：<c>&gt;= 0</c> 是录制范围内的帧下标；
        /// <c>-1</c> 表示"没有可定位的帧内不匹配"（包括从未不匹配，以及不匹配就是多出来的帧）。
        /// </summary>
        public int FirstMismatchFrame
        {
            get { return _firstMismatchFrame; }
        }

        /// <summary>
        /// 推入一帧实际指纹并与录制逐帧比对。
        /// </summary>
        /// <param name="actualFingerprint">回放局该帧结束后的状态指纹。</param>
        /// <returns>本帧是否与录制一致（多出来的帧恒为 <c>false</c>）。</returns>
        public bool PushFrame(long actualFingerprint)
        {
            int recordedFrames = _recorded.FrameCount;
            if (_comparedFrames < recordedFrames)
            {
                long expected = _recorded.GetFingerprint(_comparedFrames);
                bool matched = expected == actualFingerprint;
                if (!matched)
                {
                    _isMatch = false;
                    if (_firstMismatchFrame < 0)
                    {
                        _firstMismatchFrame = _comparedFrames;
                    }
                }

                _comparedFrames++;
                return matched;
            }

            // 录制里没有这一帧：没有可比对的对象，位置无法定位，保持 -1。
            _isMatch = false;
            _comparedFrames++;
            return false;
        }
    }
}
