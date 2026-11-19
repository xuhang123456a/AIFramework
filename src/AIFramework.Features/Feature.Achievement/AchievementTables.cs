#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Achievement
{
    /// <summary>成就子表行：纯数据，对应 <c>TbAchievement</c> 的一行。</summary>
    public readonly struct AchievementDef
    {
        public AchievementDef(
            int achievementId,
            string name,
            int metric,
            int target,
            int rewardItemId,
            int rewardCount)
        {
            AchievementId = achievementId;
            Name = name;
            Metric = metric;
            Target = target;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        /// <summary>成就 ID（配置主键，子表内唯一）。</summary>
        public int AchievementId { get; }

        /// <summary>成就展示名（非空）。</summary>
        public string Name { get; }

        /// <summary>计数指标，取值域见 <see cref="AchievementMetrics"/>。</summary>
        public int Metric { get; }

        /// <summary>达成所需累计值，必须为正。</summary>
        public int Target { get; }

        /// <summary>解锁时发放的道具 ID。</summary>
        public int RewardItemId { get; }

        /// <summary>解锁时发放的数量（允许为 0，表示只解锁不发奖）。</summary>
        public int RewardCount { get; }
    }

    /// <summary>
    /// 本切片专属子表入口（<c>TbAchievement</c>）。
    /// <para>
    /// 布局（小端，由 <c>tools/tablegen</c> 按 JSON 列顺序生成）：
    /// <c>[int32 rowCount]</c> + rowCount × { <c>int32 achievementId</c>;
    /// <c>[int32 nameLen]</c> + UTF-8 name; <c>int32 metric</c>; <c>int32 target</c>;
    /// <c>int32 rewardItemId</c>; <c>int32 rewardCount</c> }。
    /// </para>
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——"表没打进包"必须当场暴露；
    /// 而 0 行是合法配置（<see cref="Count"/> == 0），查询返回空快照而不是崩溃。
    /// 解析完成后行按 <see cref="AchievementDef.AchievementId"/> 升序排列，解锁广播顺序因此确定。
    /// </para>
    /// </summary>
    public sealed class AchievementTables
    {
        /// <summary>子表名（与 <c>tables/TbAchievement.json</c> 的 <c>table</c> 字段一致）。</summary>
        private const string TableNameValue = "TbAchievement";

        /// <summary>头部：<c>[int32 rowCount]</c>。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单个 int32 字段宽度（小端）。</summary>
        private const int Int32Bytes = 4;

        /// <summary>一行的最小可读字节数：6 个 int32（string 字段至少 4 字节长度前缀）。</summary>
        private const int MinRowBytes = Int32Bytes * 6;

        private readonly AchievementDef[] _rows;

        /// <summary>加载并解析 <c>TbAchievement</c>；表缺失抛 <see cref="InvalidOperationException"/>，数据非法抛 <see cref="InvalidDataException"/>。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">加载器返回 <c>null</c>，即子表缺失。</exception>
        /// <exception cref="InvalidDataException">截断、尾部残留、ID 重复、指标未知、目标非正、名称为空、奖励数为负。</exception>
        public AchievementTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("成就子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数（成就条数）；0 是合法配置。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按下标取行（下标顺序即 <see cref="AchievementDef.AchievementId"/> 升序）；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public AchievementDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "成就子表下标越界，当前行数 " + _rows.Length + "。");
            }

            return _rows[index];
        }

        private static AchievementDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("成就子表头部长度不足 " + HeaderBytes + " 字节。");
            }

            PayloadReader reader = new PayloadReader(payload);
            int rowCount = reader.ReadInt32("rowCount");
            if (rowCount < 0)
            {
                throw new InvalidDataException("成就子表 rowCount 为负：" + rowCount + "。");
            }

            // 先做"声明行数 vs 剩余字节"前置校验，避免畸形头部触发超大分配。
            if ((long)rowCount * MinRowBytes > reader.Remaining)
            {
                throw new InvalidDataException(
                    "成就子表声明 " + rowCount + " 行，但只剩 " + reader.Remaining + " 字节，字节流被截断。");
            }

            AchievementDef[] rows = new AchievementDef[rowCount];
            HashSet<int> seenIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int achievementId = reader.ReadInt32("achievementId");
                string name = reader.ReadUtf8("name");
                int metric = reader.ReadInt32("metric");
                int target = reader.ReadInt32("target");
                int rewardItemId = reader.ReadInt32("rewardItemId");
                int rewardCount = reader.ReadInt32("rewardCount");

                if (name.Length == 0)
                {
                    throw new InvalidDataException("成就子表 name 不能为空，achievementId " + achievementId + "。");
                }

                if (!AchievementMetrics.IsSupported(metric))
                {
                    // 未知指标永远不会被累加，等于一条永远无法解锁的配置错误行，加载期直接挡掉。
                    throw new InvalidDataException(
                        "成就子表 metric 未知，achievementId " + achievementId + " 实际为 " + metric + "。");
                }

                if (target <= 0)
                {
                    throw new InvalidDataException(
                        "成就子表 target 必须为正，achievementId " + achievementId + " 实际为 " + target + "。");
                }

                if (rewardCount < 0)
                {
                    throw new InvalidDataException(
                        "成就子表 rewardCount 不能为负，achievementId " + achievementId + " 实际为 " + rewardCount + "。");
                }

                if (!seenIds.Add(achievementId))
                {
                    throw new InvalidDataException("成就子表 achievementId 重复：" + achievementId + "。");
                }

                rows[i] = new AchievementDef(achievementId, name, metric, target, rewardItemId, rewardCount);
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException(
                    "成就子表尾部残留 " + reader.Remaining + " 字节，声明行数与字节流长度不符。");
            }

            // 行序统一按 ID 升序：解锁广播顺序与查询快照顺序共用同一份有序表，避免两处排序。
            Array.Sort(rows, CompareByAchievementId);
            return rows;
        }

        private static int CompareByAchievementId(AchievementDef left, AchievementDef right)
        {
            return left.AchievementId.CompareTo(right.AchievementId);
        }

        /// <summary>
        /// 小端字节流游标：每次读取都做越界检查，越界统一转 <see cref="InvalidDataException"/>。
        /// 只在构造期使用（允许分配），因此不做零分配优化。
        /// </summary>
        private sealed class PayloadReader
        {
            private readonly byte[] _payload;
            private int _offset;

            internal PayloadReader(byte[] payload)
            {
                _payload = payload;
                _offset = 0;
            }

            /// <summary>尚未读取的字节数。</summary>
            internal int Remaining
            {
                get { return _payload.Length - _offset; }
            }

            /// <summary>读一个小端 int32；不足 4 字节即判定截断。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal int ReadInt32(string fieldName)
            {
                if (Remaining < Int32Bytes)
                {
                    throw new InvalidDataException(
                        "成就子表读取 " + fieldName + " 时字节流已截断：偏移 " + _offset + "，剩余 " + Remaining + " 字节。");
                }

                int value = _payload[_offset]
                    | (_payload[_offset + 1] << 8)
                    | (_payload[_offset + 2] << 16)
                    | (_payload[_offset + 3] << 24);
                _offset += Int32Bytes;
                return value;
            }

            /// <summary>读"长度前缀 + UTF-8 字节"的 string；负长度或超出剩余字节即判定非法。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal string ReadUtf8(string fieldName)
            {
                int length = ReadInt32(fieldName + "Len");
                if (length < 0)
                {
                    throw new InvalidDataException("成就子表 " + fieldName + " 长度为负：" + length + "。");
                }

                if (length > Remaining)
                {
                    throw new InvalidDataException(
                        "成就子表读取 " + fieldName + " 时字节流已截断：声明 " + length + " 字节，剩余 " + Remaining + " 字节。");
                }

                string text = Encoding.UTF8.GetString(_payload, _offset, length);
                _offset += length;
                return text;
            }
        }
    }
}
