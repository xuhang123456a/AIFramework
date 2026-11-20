#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AIFramework.Contracts.Ports;

namespace AIFramework.Core.Replay
{
    /// <summary>
    /// 竞技场录像库：把 <see cref="ReplayArchiveCodec"/> 编出的字节经 <see cref="IStoragePort"/> 落盘，
    /// 并提供"列表 / 读取 / 删除 / 容量回收"。
    /// <para>
    /// <b>为什么放在 Core</b>：Console 与 Unity 都要存录像，格式与键规则一旦分叉就会出现
    /// "命令行存的录像 Unity 列不出来"。键规则、容量策略、坏档跳过都只有这一处定义，两端共用端口实现即可。
    /// </para>
    /// <para>
    /// <b>键规则（写死）</b>：<c>prefix + "." + levelId + "-" + randomSeed + "-" + recordedAtUnixSeconds</c>，
    /// 数字一律按不变文化格式化，保证同一局在同一端口上永远落到同一个键。
    /// </para>
    /// <para>
    /// <b>列表不加载帧数据</b>：列表只走 <see cref="ReplayArchiveCodec.TryReadInfo"/>，不分配帧数组。
    /// 但要说清当前的端口限制——<see cref="IStoragePort"/> 只有整块 <c>Read(key)</c>，没有"读前 N 字节"，
    /// 所以列表实际上是"把整档读进内存、只解析头部"。真实实现（文件系统/云端）应改成分段读头部
    /// （固定头 32 字节 + waveCount 4 字节即可拼出元信息），单档读放大量从"帧数×8"降到 36 字节量级。
    /// </para>
    /// <para>
    /// <b>容量回收</b>：默认上限 <see cref="DefaultCapacity"/> 档；保存后若超限，按
    /// <see cref="List"/> 的倒序从尾部（最旧）删除，直到回到上限。
    /// <b>刚保存的那一档永不参与本次回收</b>：导入一档旧录像时它可能正是"时间最旧"，
    /// 若按字面删最旧，<see cref="Save"/> 会返回一个已经被自己删掉的键。超限时删的是"别的档里最旧的"，
    /// 数量仍然收敛到上限。
    /// </para>
    /// <para>
    /// <b>坏档不传染</b>：列表遇到 magic 不符 / 截断 / 版本不符的键就跳过并计入
    /// <see cref="SkippedCount"/>，绝不让一档坏字节把整个列表页打成异常。
    /// </para>
    /// </summary>
    public sealed class ReplayArchiveStore
    {
        /// <summary>默认容量：最多保留 20 档录像（按录制时间，超限删最旧）。</summary>
        public const int DefaultCapacity = 20;

        /// <summary>默认键前缀。</summary>
        public const string DefaultPrefix = "replays";

        /// <summary>键层级分隔符。</summary>
        private const string KeySeparator = ".";

        /// <summary>落盘端口（Console 是目录、Unity 是 persistentDataPath、云端由适配层换远端实现）。</summary>
        private readonly IStoragePort _storage;

        /// <summary>规范化后的键前缀（无尾部分隔符、非空）。</summary>
        private readonly string _prefix;

        /// <summary>
        /// 创建录像库。
        /// </summary>
        /// <param name="storage">落盘端口。</param>
        /// <param name="prefix">键前缀；空串会退回 <see cref="DefaultPrefix"/>，末尾的 <c>.</c> 会被去掉。</param>
        /// <exception cref="ArgumentNullException"><paramref name="storage"/> 为 <c>null</c>。</exception>
        public ReplayArchiveStore(IStoragePort storage, string prefix = DefaultPrefix)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _prefix = NormalizePrefix(prefix);
        }

        /// <summary>
        /// 最近一次 <see cref="List"/> 跳过（读不出 / 解码失败 / 读时已被删）的坏档数量。
        /// <para>
        /// <see cref="Save"/> 内部的容量整理<b>不</b>改写它：列表页看到的"跳过几档"只反映它自己那一次列举。
        /// </para>
        /// </summary>
        public int SkippedCount { get; private set; }

        /// <summary>
        /// 保存一局录像，返回 slotKey（同参数重复保存即覆盖同一档）。
        /// <para>保存后若档数超过 <see cref="DefaultCapacity"/>，删除录制时间最旧的那一档（不含本次保存的档）。</para>
        /// </summary>
        /// <param name="kernelType">内核类型码。</param>
        /// <param name="levelId">关卡编号。</param>
        /// <param name="randomSeed">本局随机种子（必须等于 <paramref name="script"/> 的种子）。</param>
        /// <param name="script">布场脚本。</param>
        /// <param name="fingerprints">逐帧状态指纹。</param>
        /// <param name="recordedAtUnixSeconds">录制时刻（Unix 秒，由调用方从时间源取）。</param>
        /// <returns>本档的逻辑键。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="fingerprints"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="script"/> 的种子与 <paramref name="randomSeed"/> 不一致。</exception>
        /// <exception cref="ArgumentOutOfRangeException">编码参数非法（负数时间戳、帧数/波次数超上限）。</exception>
        public string Save(
            int kernelType,
            int levelId,
            int randomSeed,
            ReplayScriptData script,
            long[] fingerprints,
            long recordedAtUnixSeconds)
        {
            // 先编码再算键：编码会顺手校验全部参数，非法参数在写盘之前就被拦住。
            byte[] data = ReplayArchiveCodec.Encode(kernelType, levelId, randomSeed, script, fingerprints, recordedAtUnixSeconds);
            string slotKey = BuildSlotKey(levelId, randomSeed, recordedAtUnixSeconds);

            _storage.Write(slotKey, data);
            EvictOverflow(slotKey);
            return slotKey;
        }

        /// <summary>
        /// 列出全部录像（按 <see cref="ReplayArchiveInfo.RecordedAtUnixSeconds"/> 倒序，同一时刻按 slotKey 升序）。
        /// <para>
        /// 只解析头部，坏档跳过并计入 <see cref="SkippedCount"/>；不属于本库前缀的键直接忽略（不计入跳过）。
        /// 返回顺序不依赖端口的 <see cref="IStoragePort.ListKeys"/> 顺序：排序在这里做，端口只提供"有哪些键"。
        /// </para>
        /// </summary>
        /// <returns>元信息数组（可能为空）。</returns>
        public ReplayArchiveInfo[] List()
        {
            ReplayArchiveInfo[] archives = ListCore(out int skipped);
            SkippedCount = skipped;
            return archives;
        }

        /// <summary>
        /// 读取指定录像的帧指纹；坏档 / 缺档返回 <c>false</c> 并给出 <paramref name="reason"/>。
        /// </summary>
        /// <param name="slotKey">逻辑键（<see cref="Save"/> 的返回值或 <see cref="ReplayArchiveInfo.SlotKey"/>）。</param>
        /// <param name="fingerprints">逐帧指纹；失败时为空数组。</param>
        /// <param name="reason">失败原因；成功时为空串。</param>
        /// <returns>是否读取成功。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="slotKey"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="slotKey"/> 为空串。</exception>
        public bool TryLoadFingerprints(string slotKey, out long[] fingerprints, out string reason)
        {
            return TryLoad(slotKey, out _, out _, out fingerprints, out reason);
        }

        /// <summary>
        /// 读取整档：元信息（含真实 <see cref="ReplayArchiveInfo.SlotKey"/>）+ 布场脚本 + 帧指纹。
        /// <para>
        /// 回放一档需要脚本，所以光有指纹不够；这个重载是 <see cref="TryLoadFingerprints"/> 的超集。
        /// </para>
        /// </summary>
        /// <param name="slotKey">逻辑键。</param>
        /// <param name="info">元信息；失败时为 <c>default</c>。</param>
        /// <param name="script">布场脚本；失败时为 <c>default</c>。</param>
        /// <param name="fingerprints">逐帧指纹；失败时为空数组。</param>
        /// <param name="reason">失败原因；成功时为空串。</param>
        /// <returns>是否读取成功。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="slotKey"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="slotKey"/> 为空串。</exception>
        public bool TryLoad(
            string slotKey,
            out ReplayArchiveInfo info,
            out ReplayScriptData script,
            out long[] fingerprints,
            out string reason)
        {
            ValidateSlotKey(slotKey);

            info = default;
            script = default;
            fingerprints = Array.Empty<long>();
            reason = string.Empty;

            byte[]? data = _storage.Read(slotKey);
            if (data == null)
            {
                reason = "槽位不存在：" + slotKey + "。";
                return false;
            }

            if (!ReplayArchiveCodec.TryDecode(data, out ReplayArchiveInfo parsed, out script, out fingerprints, out reason))
            {
                // 失败时把半成品清干净：坏档不能留下"看起来可用的"脚本/帧，调用方一个 if 就能判死。
                script = default;
                fingerprints = Array.Empty<long>();
                return false;
            }

            info = new ReplayArchiveInfo(
                slotKey,
                parsed.KernelType,
                parsed.LevelId,
                parsed.RandomSeed,
                parsed.FrameCount,
                parsed.RecordedAtUnixSeconds,
                parsed.CodecVersion);
            return true;
        }

        /// <summary>
        /// 删除一档录像。<b>不存在返回 <c>false</c></b>（与"删除成功"区分开，便于列表页反馈）。
        /// </summary>
        /// <param name="slotKey">逻辑键。</param>
        /// <returns>删除前该键是否存在。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="slotKey"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="slotKey"/> 为空串。</exception>
        public bool Delete(string slotKey)
        {
            ValidateSlotKey(slotKey);

            if (!_storage.Exists(slotKey))
            {
                return false;
            }

            _storage.Delete(slotKey);
            return true;
        }

        /// <summary>把超限的档从最旧开始删；<paramref name="justSavedKey"/> 本身永不被本次回收删掉。</summary>
        private void EvictOverflow(string justSavedKey)
        {
            ReplayArchiveInfo[] archives = ListCore(out _);
            int excess = archives.Length - DefaultCapacity;
            if (excess <= 0)
            {
                return;
            }

            // List 已是倒序：从尾部往上就是"最旧优先"。
            for (int i = archives.Length - 1; i >= 0 && excess > 0; i--)
            {
                string key = archives[i].SlotKey;
                if (string.Equals(key, justSavedKey, StringComparison.Ordinal))
                {
                    continue;
                }

                _storage.Delete(key);
                excess--;
            }
        }

        /// <summary>列举核心：<paramref name="skipped"/> 由调用方决定要不要记到 <see cref="SkippedCount"/>。</summary>
        private ReplayArchiveInfo[] ListCore(out int skipped)
        {
            skipped = 0;

            // 端口契约保证非空数组；这里仍按"可能为 null"防御一次，坏实现只会让列表为空，不会 NRE。
            string[]? keys = _storage.ListKeys();
            if (keys == null)
            {
                return Array.Empty<ReplayArchiveInfo>();
            }

            var archives = new List<ReplayArchiveInfo>(keys.Length);
            string prefix = _prefix + KeySeparator;

            for (int i = 0; i < keys.Length; i++)
            {
                string? key = keys[i];
                if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    // 别的子系统的键：不是坏档，不计数。
                    continue;
                }

                byte[]? data = _storage.Read(key);
                if (data == null)
                {
                    // ListKeys 与 Read 之间被删掉（或端口读失败）：算作跳过，列表继续。
                    skipped++;
                    continue;
                }

                if (!ReplayArchiveCodec.TryReadInfo(data, out ReplayArchiveInfo info, out _))
                {
                    skipped++;
                    continue;
                }

                archives.Add(new ReplayArchiveInfo(
                    key,
                    info.KernelType,
                    info.LevelId,
                    info.RandomSeed,
                    info.FrameCount,
                    info.RecordedAtUnixSeconds,
                    info.CodecVersion));
            }

            archives.Sort(CompareNewestFirst);
            return archives.ToArray();
        }

        /// <summary>倒序比较：录制时间新的在前；同一时刻用 slotKey 升序兜底，保证顺序稳定。</summary>
        private static int CompareNewestFirst(ReplayArchiveInfo left, ReplayArchiveInfo right)
        {
            int byTime = right.RecordedAtUnixSeconds.CompareTo(left.RecordedAtUnixSeconds);
            if (byTime != 0)
            {
                return byTime;
            }

            return string.CompareOrdinal(left.SlotKey, right.SlotKey);
        }

        /// <summary>
        /// 构造键：<c>prefix/levelId-randomSeed-recordedAt</c>。数字按不变文化格式化，
        /// 避免某些区域的小数/分组分隔符混进键里（键要能原样当文件名用）。
        /// </summary>
        private string BuildSlotKey(int levelId, int randomSeed, long recordedAtUnixSeconds)
        {
            return _prefix + KeySeparator
                + levelId.ToString(CultureInfo.InvariantCulture) + "-"
                + randomSeed.ToString(CultureInfo.InvariantCulture) + "-"
                + recordedAtUnixSeconds.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>前缀规范化：空串/全分隔符退回默认值，去掉尾部分隔符避免 <c>replays//…</c>。</summary>
        private static string NormalizePrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return DefaultPrefix;
            }

            string trimmed = prefix.TrimEnd('.', '/');
            return trimmed.Length == 0 ? DefaultPrefix : trimmed;
        }

        /// <summary>键参数校验：<c>null</c> 抛 <see cref="ArgumentNullException"/>，空串抛 <see cref="ArgumentException"/>。</summary>
        private static void ValidateSlotKey(string slotKey)
        {
            if (slotKey == null)
            {
                throw new ArgumentNullException(nameof(slotKey));
            }

            if (slotKey.Length == 0)
            {
                throw new ArgumentException("槽位键不能为空串。", nameof(slotKey));
            }
        }
    }
}
