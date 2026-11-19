#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Quest
{
    /// <summary>
    /// 任务子表行：纯数据，对应 <c>TbQuest</c> 的一行。
    /// <see cref="TargetCount"/> 是进度上限，达到即可领取一次 <see cref="RewardItemId"/> × <see cref="RewardCount"/>。
    /// </summary>
    public readonly struct QuestDef
    {
        public QuestDef(int questId, int targetCount, int rewardItemId, int rewardCount)
        {
            QuestId = questId;
            TargetCount = targetCount;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
        }

        /// <summary>任务 ID（配置主键，子表内唯一）。</summary>
        public int QuestId { get; }

        /// <summary>完成所需进度，必须大于 0。</summary>
        public int TargetCount { get; }

        /// <summary>完成后发放的道具 ID。</summary>
        public int RewardItemId { get; }

        /// <summary>完成后发放的数量（允许为 0，表示只做流程不发奖）。</summary>
        public int RewardCount { get; }
    }

    /// <summary>
    /// 本切片专属子表入口（<c>TbQuest</c>）。表缺失或校验不过一律显式抛异常，禁止静默按空表继续。
    /// <para>
    /// Phase 1 夹具布局：小端 <c>[int32 rowCount]</c> + <c>rowCount × { int32 questId; int32 targetCount; int32 rewardItemId; int32 rewardCount; }</c>，
    /// 单行 16 字节。解析后按下标访问的行按 <see cref="QuestDef.QuestId"/> 升序排列，查询结果因此天然有序。
    /// </para>
    /// </summary>
    public sealed class QuestTables
    {
        /// <summary>子表名（与夹具一致）。</summary>
        private const string TableNameValue = "TbQuest";

        private const int HeaderBytes = 4;
        private const int RowBytes = 16;

        private readonly QuestDef[] _rows;

        /// <summary>加载并解析 <c>TbQuest</c>；表缺失抛 <see cref="InvalidOperationException"/>，数据非法抛 <see cref="InvalidDataException"/>。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">加载器返回 <c>null</c>，即子表缺失。</exception>
        /// <exception cref="InvalidDataException">长度与声明行数不符、<c>questId</c> 重复或 <c>targetCount &lt;= 0</c>。</exception>
        public QuestTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("任务子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数（任务条数）。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按下标取行（下标顺序即 <see cref="QuestDef.QuestId"/> 升序）；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public QuestDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "任务子表下标越界，当前行数 " + _rows.Length + "。");
            }

            return _rows[index];
        }

        private static QuestDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("任务子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            if (rowCount < 0)
            {
                throw new InvalidDataException("任务子表行数为负：" + rowCount + "。");
            }

            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "任务子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            QuestDef[] rows = new QuestDef[rowCount];
            HashSet<int> seenQuestIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int questId = ReadInt32LittleEndian(payload, offset);
                int targetCount = ReadInt32LittleEndian(payload, offset + 4);
                int rewardItemId = ReadInt32LittleEndian(payload, offset + 8);
                int rewardCount = ReadInt32LittleEndian(payload, offset + 12);

                if (targetCount <= 0)
                {
                    // 目标 <= 0 会让"未完成判定"永远为假，等价于一条白给奖励的行，必须在加载期挡掉。
                    throw new InvalidDataException("任务子表 targetCount 必须为正，questId " + questId + " 实际为 " + targetCount + "。");
                }

                if (!seenQuestIds.Add(questId))
                {
                    throw new InvalidDataException("任务子表 questId 重复：" + questId + "。");
                }

                rows[i] = new QuestDef(questId, targetCount, rewardItemId, rewardCount);
            }

            // 行序按配置书写顺序落地，这里统一排序，保证列表查询与 GetState 的二分查找共用同一顺序。
            Array.Sort(rows, CompareByQuestId);
            return rows;
        }

        private static int CompareByQuestId(QuestDef left, QuestDef right)
        {
            return left.QuestId.CompareTo(right.QuestId);
        }

        private static int ReadInt32LittleEndian(byte[] payload, int offset)
        {
            int value = payload[offset]
                | (payload[offset + 1] << 8)
                | (payload[offset + 2] << 16)
                | (payload[offset + 3] << 24);
            return value;
        }
    }
}
