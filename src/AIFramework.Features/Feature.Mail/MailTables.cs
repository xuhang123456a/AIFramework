using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Mail
{
    /// <summary>
    /// 邮件子表行（纯数据）：一封邮件的静态定义，不含任何运行时状态。
    /// 运行时的"是否已领 / 收件天序号"由 <see cref="MailHandler"/> 按本行派生，禁止回写本结构。
    /// </summary>
    public readonly struct MailDef
    {
        public MailDef(int mailId, int rewardItemId, int rewardCount, int sendDayOffset)
        {
            MailId = mailId;
            RewardItemId = rewardItemId;
            RewardCount = rewardCount;
            SendDayOffset = sendDayOffset;
        }

        /// <summary>邮件唯一 ID（子表内不允许重复）。</summary>
        public int MailId { get; }

        /// <summary>附件道具 ID。</summary>
        public int RewardItemId { get; }

        /// <summary>附件数量，必须大于 0。</summary>
        public int RewardCount { get; }

        /// <summary>相对于"今天"第几天发的（0 = 今天，1 = 昨天…），用于生成收件时间。</summary>
        public int SendDayOffset { get; }
    }

    /// <summary>
    /// 本切片专属子表入口。表缺失或校验不过一律显式抛异常，禁止静默按空表继续。
    /// 布局（Phase 1 夹具）：小端 <c>[int32 rowCount]</c> + <c>rowCount × { int32 mailId; int32 rewardItemId; int32 rewardCount; int32 sendDayOffset; }</c>，单行 16 字节。
    /// </summary>
    public sealed class MailTables
    {
        private const string TableNameValue = "TbMail";
        private const int HeaderBytes = 4;
        private const int RowBytes = 16;

        private readonly MailDef[] _rows;

        public MailTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("邮件子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数（即邮箱初始邮件数）。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按子表物理下标取行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public MailDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "邮件子表下标必须在 0.." + (_rows.Length - 1) + " 之间。");
            }

            return _rows[index];
        }

        private static MailDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("邮件子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            if (rowCount < 0)
            {
                throw new InvalidDataException("邮件子表行数为负：" + rowCount + "。");
            }

            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "邮件子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            MailDef[] rows = new MailDef[rowCount];
            HashSet<int> seenMailIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int mailId = ReadInt32LittleEndian(payload, offset);
                int rewardItemId = ReadInt32LittleEndian(payload, offset + 4);
                int rewardCount = ReadInt32LittleEndian(payload, offset + 8);
                int sendDayOffset = ReadInt32LittleEndian(payload, offset + 12);

                if (!seenMailIds.Add(mailId))
                {
                    throw new InvalidDataException("邮件子表 mailId 重复：" + mailId + "。");
                }

                if (rewardCount <= 0)
                {
                    throw new InvalidDataException(
                        "邮件子表 rewardCount 必须大于 0，mailId " + mailId + " 实际为 " + rewardCount + "。");
                }

                rows[i] = new MailDef(mailId, rewardItemId, rewardCount, sendDayOffset);
            }

            return rows;
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
