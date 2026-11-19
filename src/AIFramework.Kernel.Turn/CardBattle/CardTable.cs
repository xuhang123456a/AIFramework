#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;

namespace AIFramework.Kernel.Turn.CardBattle
{
    /// <summary>
    /// 卡牌模板（纯值类型）：一张具体牌的全部规则字段。牌堆里按 <c>copies</c> 展开成多份，
    /// 因此运行期只需要这一种结构，不再按 cardId 反查表。
    /// </summary>
    internal readonly struct CardDefinition
    {
        internal CardDefinition(int cardId, int cost, int damage, int block)
        {
            CardId = cardId;
            Cost = cost;
            Damage = damage;
            Block = block;
        }

        /// <summary>卡牌模板 ID（对应 TbCard 行）。</summary>
        internal int CardId { get; }

        /// <summary>打出消耗的能量。</summary>
        internal int Cost { get; }

        /// <summary>造成的伤害（0 表示不造成伤害）。</summary>
        internal int Damage { get; }

        /// <summary>提供的护甲（0 表示不提供护甲）。</summary>
        internal int Block { get; }
    }

    /// <summary>
    /// 卡牌子表 <c>TbCard</c> 的解析入口（Phase 1 手写解析，替代 Luban cs-bin 导出）。
    /// <para>
    /// 二进制布局（小端）：<c>[int32 rowCount]</c> + <c>rowCount × { int32 cardId; int32 cost;
    /// int32 damage; int32 block; int32 copies; }</c>，即 4 字节头 + 20 字节/行。
    /// </para>
    /// <para>
    /// 校验口径：表缺失（<see cref="IBinaryTableLoader"/> 返回 <c>null</c>）或 <c>rowCount == 0</c>
    /// 抛 <see cref="InvalidOperationException"/>（数据源不可用，切片拒绝启用）；
    /// 长度不符 / cardId 重复 / cost &lt; 0 / copies &lt;= 0 抛 <see cref="InvalidDataException"/>
    /// （数据本身非法），一律不静默按空表继续。
    /// </para>
    /// <para>
    /// 未校验项：<c>damage</c>/<c>block</c> 允许为负——运行期只走 <c>&gt; 0</c> 分支，
    /// 负值等价于该效果不存在，不影响结算正确性。
    /// </para>
    /// </summary>
    internal static class CardTable
    {
        /// <summary>子表名：Phase 1 全仓约定 <c>表名</c> 与 <c>&lt;资源根&gt;/&lt;表名&gt;.bin</c> 对齐。</summary>
        internal const string TableNameValue = "TbCard";

        private const int HeaderBytes = 4;
        private const int RowBytes = 20;

        /// <summary>
        /// 内置默认牌库（无 loader 时使用）：斩击 ×3、防御 ×3、重击 ×2、小刀 ×2，共 10 张。
        /// </summary>
        internal static CardDefinition[] CreateDefault()
        {
            CardDefinition[] cards = new CardDefinition[10];
            int index = 0;

            // (1,1,6,0,3) 斩击
            index = FillCopies(cards, index, 1, 1, 6, 0, 3);

            // (2,1,0,5,3) 防御
            index = FillCopies(cards, index, 2, 1, 0, 5, 3);

            // (3,2,12,0,2) 重击
            index = FillCopies(cards, index, 3, 2, 12, 0, 2);

            // (4,0,3,3,2) 小刀
            FillCopies(cards, index, 4, 0, 3, 3, 2);
            return cards;
        }

        /// <summary>从 <see cref="IBinaryTableLoader"/> 读取并校验 TbCard，返回按 copies 展开后的牌序。</summary>
        internal static CardDefinition[] Load(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("卡牌子表 '" + TableNameValue + "' 不存在，卡牌战斗拒绝启用。");
            }

            return Parse(payload);
        }

        private static int FillCopies(
            CardDefinition[] cards,
            int index,
            int cardId,
            int cost,
            int damage,
            int block,
            int copies)
        {
            for (int i = 0; i < copies; i++)
            {
                cards[index] = new CardDefinition(cardId, cost, damage, block);
                index++;
            }

            return index;
        }

        private static CardDefinition[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("卡牌子表头部长度不足 4 字节，实际 " + payload.Length + " 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            if (rowCount == 0)
            {
                throw new InvalidOperationException("卡牌子表 '" + TableNameValue + "' 行数为 0，卡牌战斗拒绝启用。");
            }

            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "卡牌子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            CardDefinition[] rows = new CardDefinition[rowCount];
            int[] copies = new int[rowCount];
            HashSet<int> seenCardIds = new HashSet<int>();
            long totalCards = 0L;

            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int cardId = ReadInt32LittleEndian(payload, offset);
                int cost = ReadInt32LittleEndian(payload, offset + 4);
                int damage = ReadInt32LittleEndian(payload, offset + 8);
                int block = ReadInt32LittleEndian(payload, offset + 12);
                int copyCount = ReadInt32LittleEndian(payload, offset + 16);

                if (cost < 0)
                {
                    throw new InvalidDataException("卡牌子表 cost < 0：" + cost + "（cardId " + cardId + "）。");
                }

                if (copyCount <= 0)
                {
                    throw new InvalidDataException("卡牌子表 copies <= 0：" + copyCount + "（cardId " + cardId + "）。");
                }

                if (!seenCardIds.Add(cardId))
                {
                    throw new InvalidDataException("卡牌子表 cardId 重复：" + cardId + "。");
                }

                rows[i] = new CardDefinition(cardId, cost, damage, block);
                copies[i] = copyCount;
                totalCards += copyCount;

                if (totalCards > int.MaxValue)
                {
                    throw new InvalidDataException("卡牌子表 copies 总和溢出 int：" + totalCards + "。");
                }
            }

            CardDefinition[] cards = new CardDefinition[(int)totalCards];
            int cursor = 0;
            for (int i = 0; i < rowCount; i++)
            {
                for (int c = 0; c < copies[i]; c++)
                {
                    cards[cursor] = rows[i];
                    cursor++;
                }
            }

            return cards;
        }

        private static int ReadInt32LittleEndian(byte[] payload, int offset)
        {
            return payload[offset]
                | (payload[offset + 1] << 8)
                | (payload[offset + 2] << 16)
                | (payload[offset + 3] << 24);
        }
    }
}
