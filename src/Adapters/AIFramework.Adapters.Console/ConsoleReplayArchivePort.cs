using System;
using AIFramework.Contracts.Replay;
using AIFramework.Core.Replay;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 把底座 <see cref="ReplayArchiveStore"/> 适配成回放列表端口。
    /// <para>
    /// <b>为什么需要这层薄适配</b>：Features 切片只能看到 <see cref="IReplayArchivePort"/>，
    /// 而落盘格式在 Core；适配层是唯一允许同时看到两边的地方（与 Unity 的同一职责实现对称）。
    /// </para>
    /// </summary>
    public sealed class ConsoleReplayArchivePort : IReplayArchivePort
    {
        private readonly ReplayArchiveStore _store;

        /// <summary>构造适配器。</summary>
        /// <param name="store">回放存档仓库。</param>
        public ConsoleReplayArchivePort(ReplayArchiveStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>列出全部录像（转换失败的单条会被底座跳过，见 <see cref="ReplayArchiveStore.SkippedCount"/>）。</summary>
        public ReplayBrowserEntry[] List()
        {
            ReplayArchiveInfo[] infos = _store.List();
            var entries = new ReplayBrowserEntry[infos.Length];
            for (int i = 0; i < infos.Length; i++)
            {
                ReplayArchiveInfo info = infos[i];
                entries[i] = new ReplayBrowserEntry(
                    info.SlotKey,
                    info.LevelId,
                    info.FrameCount,
                    info.RecordedAtUnixSeconds,
                    info.KernelType,
                    info.RandomSeed);
            }

            return entries;
        }

        /// <summary>删除一条录像。</summary>
        public bool Delete(string slotKey)
        {
            return _store.Delete(slotKey);
        }
    }
}
