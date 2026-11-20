#nullable enable
namespace AIFramework.Contracts.Replay
{
    /// <summary>回放列表项（只读快照，供列表页展示；不携带帧数据）。</summary>
    public readonly struct ReplayBrowserEntry
    {
        /// <summary>构造列表项。</summary>
        /// <param name="slotKey">存储逻辑键（删除/播放时回传）。</param>
        /// <param name="levelId">关卡编号。</param>
        /// <param name="frameCount">帧数。</param>
        /// <param name="recordedAtUnixSeconds">录制时刻（Unix 秒）。</param>
        /// <param name="kernelType">内核类型码。</param>
        /// <param name="randomSeed">随机种子。</param>
        public ReplayBrowserEntry(string slotKey, int levelId, int frameCount, long recordedAtUnixSeconds, int kernelType, int randomSeed)
        {
            SlotKey = slotKey ?? string.Empty;
            LevelId = levelId;
            FrameCount = frameCount < 0 ? 0 : frameCount;
            RecordedAtUnixSeconds = recordedAtUnixSeconds;
            KernelType = kernelType;
            RandomSeed = randomSeed;
        }

        /// <summary>存储逻辑键。</summary>
        public string SlotKey { get; }

        /// <summary>关卡编号。</summary>
        public int LevelId { get; }

        /// <summary>帧数。</summary>
        public int FrameCount { get; }

        /// <summary>录制时刻（Unix 秒）。</summary>
        public long RecordedAtUnixSeconds { get; }

        /// <summary>内核类型码。</summary>
        public int KernelType { get; }

        /// <summary>随机种子。</summary>
        public int RandomSeed { get; }
    }

    /// <summary>
    /// 回放存储端口：列表/删除。
    /// <para>
    /// <b>为什么在 Contracts 而不是直接给切片一个 Core 引用</b>：回放存档的落盘格式属于底座能力，
    /// 列表页只关心"有哪些录像、能不能删"；用端口隔开后，Features 切片可以无头测试，
    /// Unity/Console 各自提供实现（本地文件/SDK 云存档）。
    /// </para>
    /// </summary>
    public interface IReplayArchivePort
    {
        /// <summary>列出全部录像（实现方保证顺序稳定；排序由上层处理）。</summary>
        ReplayBrowserEntry[] List();

        /// <summary>删除一条录像；返回是否真的删掉了。</summary>
        bool Delete(string slotKey);
    }
}
