namespace AIFramework.Contracts.Replay
{
    /// <summary>
    /// 一条被记录的输入：第几帧提交的、以及意图本身。
    /// <para>
    /// <b>kind 是数值枚举</b>，与具体意图类型的对应关系由回放协议写死：
    /// <c>1 = ControlIntent</c>、<c>2 = MoveIntent</c>、<c>3 = DamageIntent</c>、<c>4 = ReflectProjectileIntent</c>。
    /// <c>a..d</c> 是各意图的关键字段（具体打包方式由录制方与播放方约定，内核完全不知道回放在发生）。
    /// </para>
    /// </summary>
    public readonly struct RecordedIntent
    {
        public RecordedIntent(int frame, int kind, int a, int b, int c, int d)
        {
            Frame = frame;
            Kind = kind;
            A = a;
            B = b;
            C = c;
            D = d;
        }

        /// <summary>提交该意图时的逻辑帧号（从 0 开始，语义等同于 <c>BattleWorld.FrameId</c>）。</summary>
        public int Frame { get; }

        /// <summary>意图类型码：1=Control 2=Move 3=Damage 4=Reflect。</summary>
        public int Kind { get; }

        /// <summary>关键字段 1。</summary>
        public int A { get; }

        /// <summary>关键字段 2。</summary>
        public int B { get; }

        /// <summary>关键字段 3。</summary>
        public int C { get; }

        /// <summary>关键字段 4。</summary>
        public int D { get; }
    }

    /// <summary>一局录像头：种子、关卡、以及内核类型。</summary>
    public readonly struct ReplayHeader
    {
        public ReplayHeader(int kernelType, int levelId, int randomSeed, int unitCount, int frameCount)
        {
            KernelType = kernelType;
            LevelId = levelId;
            RandomSeed = randomSeed;
            UnitCount = unitCount;
            FrameCount = frameCount;
        }

        /// <summary>内核类型码，取值见 <c>GameplayKernelType</c>。</summary>
        public int KernelType { get; }

        /// <summary>关卡编号。</summary>
        public int LevelId { get; }

        /// <summary>本局随机种子。</summary>
        public int RandomSeed { get; }

        /// <summary>布场单位数量。</summary>
        public int UnitCount { get; }

        /// <summary>本局逻辑帧数。</summary>
        public int FrameCount { get; }
    }

    /// <summary>回放通道：录制与播放都走它，内核完全不知道回放在发生。</summary>
    public interface IReplaySink
    {
        /// <summary>记录一条输入；实现方满容量时只允许丢弃并计数，禁止扩容或抛异常。</summary>
        void Record(int frame, int kind, int a, int b, int c, int d);
    }

    /// <summary>回放来源：按帧把已记录的输入交还给播放器。</summary>
    public interface IReplaySource
    {
        /// <summary>
        /// 取当前帧的下一条输入。没有"本帧"的输入时返回 <c>false</c>；
        /// 成功时 <paramref name="intent"/> 为有效记录，失败时为 <c>default</c>。
        /// </summary>
        bool TryNext(int frame, out RecordedIntent intent);
    }
}
