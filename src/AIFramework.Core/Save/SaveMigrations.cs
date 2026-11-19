#nullable enable
using System;

namespace AIFramework.Core.Save
{
    /// <summary>
    /// 存档 schema 迁移链：把受支持的历史版本负载逐跳升级到 <see cref="CurrentVersion"/>。
    /// <para>
    /// <b>为什么迁移链放在存档层而不是各业务切片</b>：存档是"整档"概念，跨版本连续可读是存档层的承诺；
    /// 若让每个切片自己判断版本，任何一处漏写都会出现"一半数据升级了、一半还是旧格式"的半截档。
    /// </para>
    /// <para>
    /// <b>为什么是逐跳（v1→v2→v3…）而不是每个旧版本各写一条直达当前版本的转换</b>：N 个版本只需 N-1 个跳板，
    /// 且每跳只描述"相邻两版差异"，新增 v3 时只要加一个 <c>v2 → v3</c> 跳板，旧的 v1 档自动走两跳。
    /// </para>
    /// <para>
    /// <b>为什么失败就整档失败、不做"能读多少算多少"</b>：缺失的条目在背包里等于道具凭空消失，
    /// 比"读出坏档"更难追查；宁可当作没有存档让上层走新档分支。
    /// </para>
    /// </summary>
    public static class SaveMigrations
    {
        /// <summary>
        /// 当前存档 schema 版本。<b>加新版本时改这里，并补一个 <c>v(n-1) → v(n)</c> 跳板</b>；
        /// 与 <see cref="InventorySaveCodec.FormatVersion2"/> 对齐：Phase 1 的整档负载就是背包。
        /// </summary>
        public const int CurrentVersion = 2;

        /// <summary>可以直接作为迁移起点的版本集合（当前只有 v1）。</summary>
        private static readonly int[] SourceVersions = { InventorySaveCodec.FormatVersion1 };

        /// <summary>
        /// 受支持的起点版本（当前 <c>{ 1 }</c>）。返回副本，调用方改它不会污染迁移链内部状态。
        /// </summary>
        public static int[] SupportedSourceVersions => (int[])SourceVersions.Clone();

        /// <summary>把负载迁移到 <see cref="CurrentVersion"/>（<see cref="Migrate"/> 的常用入口）。</summary>
        /// <param name="payload">源负载字节。</param>
        /// <param name="fromVersion">源 schema 版本。</param>
        /// <param name="migrated">迁移后的负载；失败时为空数组。</param>
        /// <param name="reason">失败原因（成功时为空串），供日志/诊断直接输出。</param>
        /// <returns>是否迁移成功。</returns>
        public static bool TryMigrate(byte[] payload, int fromVersion, out byte[] migrated, out string reason)
        {
            return Migrate(payload, fromVersion, CurrentVersion, out migrated, out reason);
        }

        /// <summary>
        /// 把负载从 <paramref name="fromVersion"/> 逐跳迁移到 <paramref name="toVersion"/>。
        /// <para>
        /// 规则：<c>fromVersion == toVersion</c> 原样返回 true（幂等，同一份数据重复迁移结果一致）；
        /// <c>fromVersion &gt; toVersion</c> 视为降级，明确拒绝；<paramref name="toVersion"/> 目前只接受
        /// <see cref="CurrentVersion"/>（迁移到未来版本等于凭空造数据）。
        /// </para>
        /// </summary>
        /// <param name="payload">源负载字节。</param>
        /// <param name="fromVersion">源 schema 版本。</param>
        /// <param name="toVersion">目标 schema 版本（必须等于 <see cref="CurrentVersion"/>）。</param>
        /// <param name="migrated">迁移后的负载；失败时为空数组。</param>
        /// <param name="reason">失败原因（成功时为空串）。</param>
        /// <returns>是否迁移成功。</returns>
        public static bool Migrate(byte[] payload, int fromVersion, int toVersion, out byte[] migrated, out string reason)
        {
            migrated = Array.Empty<byte>();

            if (payload == null)
            {
                reason = "负载为 null，无可迁移数据。";
                return false;
            }

            if (fromVersion > toVersion)
            {
                reason = "不支持降级迁移：v" + fromVersion + " → v" + toVersion + "。";
                return false;
            }

            if (toVersion != CurrentVersion)
            {
                reason = "迁移目标只能是当前版本 v" + CurrentVersion + "，收到 v" + toVersion + "。";
                return false;
            }

            if (fromVersion == toVersion)
            {
                // 已在当前版本：原样返回（不复制，避免读档热路径多一次整档分配）。
                migrated = payload;
                reason = string.Empty;
                return true;
            }

            byte[] current = payload;
            for (int version = fromVersion; version < toVersion; version++)
            {
                if (!TryApplySingleStep(version, current, out byte[] stepped, out string stepReason))
                {
                    reason = stepReason;
                    return false;
                }

                current = stepped;
            }

            migrated = current;
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// 单跳升级：只负责 <c>v(fromVersion) → v(fromVersion + 1)</c>。
        /// <b>未来加 v3 时在这里加一个 case（并改 <see cref="CurrentVersion"/> 与 <see cref="SourceVersions"/>）</b>。
        /// </summary>
        private static bool TryApplySingleStep(int fromVersion, byte[] payload, out byte[] migrated, out string reason)
        {
            migrated = Array.Empty<byte>();

            switch (fromVersion)
            {
                case InventorySaveCodec.FormatVersion1:
                    if (!InventorySaveCodec.TryDecode(payload, InventorySaveCodec.FormatVersion1, out InventorySaveEntry[] v1Entries))
                    {
                        reason = "v1 负载不是合法背包数据（截断、负 count 或字段非法），拒绝迁移以免产生半截存档。";
                        return false;
                    }

                    migrated = InventorySaveCodec.EncodeV2(ExpandV1Rows(v1Entries));
                    reason = string.Empty;
                    return true;

                default:
                    reason = "没有 v" + fromVersion + " → v" + (fromVersion + 1) + " 的迁移跳板；受支持的起点版本为 v1。";
                    return false;
            }
        }

        /// <summary>
        /// v1 每行 <c>(itemId, count)</c> 扩成 v2 的 <c>(itemId, count, updatedTicks)</c>。
        /// <para>
        /// <b>时间戳一律填 0（未知）</b>：v1 当年根本没记过这个时间，迁移时拿"现在"或文件时间兜底都是伪造，
        /// 会让云存档的"谁更新"比较把最老的档判成最新档。
        /// </para>
        /// </summary>
        private static InventorySaveEntry[] ExpandV1Rows(InventorySaveEntry[] v1Entries)
        {
            var expanded = new InventorySaveEntry[v1Entries.Length];
            for (int i = 0; i < v1Entries.Length; i++)
            {
                expanded[i] = new InventorySaveEntry(v1Entries[i].ItemId, v1Entries[i].Count, updatedTicks: 0);
            }

            return expanded;
        }
    }
}
