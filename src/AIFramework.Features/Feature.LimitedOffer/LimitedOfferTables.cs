#nullable enable
using System;
using System.IO;
using System.Text;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.LimitedOffer
{
    /// <summary>
    /// 限时礼包行：纯数据，对应 <c>TbLimitedOffer</c> 的一行。
    /// <para>
    /// 时间窗口语义固定为**左闭右开** <c>[startUnixSeconds, endUnixSeconds)</c>：
    /// 开始那一秒已经可买，结束那一秒已经不可买（相邻两期礼包不会在同一秒里同时可买）。
    /// </para>
    /// <para>
    /// <see cref="DiscountPercent"/> 是"实付百分比"而不是"减免百分比"：<c>100</c> = 原价、
    /// <c>10</c> = 一折。实付价由 <see cref="LimitedOfferHandler"/> 统一按
    /// <c>floor(originalPrice * discountPercent / 100)</c> 折算，配置层不做四舍五入。
    /// </para>
    /// </summary>
    public readonly struct LimitedOfferDef
    {
        public LimitedOfferDef(
            int offerId,
            string name,
            int itemId,
            int grantCount,
            int priceItemId,
            int originalPrice,
            int discountPercent,
            int startUnixSeconds,
            int endUnixSeconds,
            int dailyLimit,
            int totalLimit)
        {
            OfferId = offerId;
            Name = name;
            ItemId = itemId;
            GrantCount = grantCount;
            PriceItemId = priceItemId;
            OriginalPrice = originalPrice;
            DiscountPercent = discountPercent;
            StartUnixSeconds = startUnixSeconds;
            EndUnixSeconds = endUnixSeconds;
            DailyLimit = dailyLimit;
            TotalLimit = totalLimit;
        }

        /// <summary>礼包 id（配置主键，恒为正，表内唯一）。</summary>
        public int OfferId { get; }

        /// <summary>礼包显示名（非空）。</summary>
        public string Name { get; }

        /// <summary>购买成功后发放的道具 id（恒为正）。</summary>
        public int ItemId { get; }

        /// <summary>购买成功后发放的道具数量（恒为正）。</summary>
        public int GrantCount { get; }

        /// <summary>结算货币道具 id（恒为正）：本切片只透出，不做余额判定。</summary>
        public int PriceItemId { get; }

        /// <summary>原价（恒为正，以 <see cref="PriceItemId"/> 计价）。</summary>
        public int OriginalPrice { get; }

        /// <summary>实付百分比（1..100）：100 = 原价，10 = 一折。</summary>
        public int DiscountPercent { get; }

        /// <summary>开始时刻（Unix 秒，UTC，左闭）。</summary>
        public int StartUnixSeconds { get; }

        /// <summary>结束时刻（Unix 秒，UTC，右开）；恒大于 <see cref="StartUnixSeconds"/>。</summary>
        public int EndUnixSeconds { get; }

        /// <summary>每日限购（UTC 自然日，<c>0</c> 表示不限当日）。</summary>
        public int DailyLimit { get; }

        /// <summary>总限购（本切片生命周期内累计，<c>0</c> 表示一次都不能买）。</summary>
        public int TotalLimit { get; }
    }

    /// <summary>
    /// 本切片专属子表入口（<c>TbLimitedOffer</c>）。
    /// <para>
    /// 布局（小端，由 <c>tools/tablegen</c> 按 JSON 列顺序生成）：
    /// <c>[int32 rowCount]</c> + rowCount ×
    /// { <c>int32 offerId</c>; <c>[int32 nameLen]</c> + UTF-8 name;
    /// <c>int32 itemId</c>; <c>int32 grantCount</c>; <c>int32 priceItemId</c>;
    /// <c>int32 originalPrice</c>; <c>int32 discountPercent</c>;
    /// <c>int32 startUnixSeconds</c>; <c>int32 endUnixSeconds</c>;
    /// <c>int32 dailyLimit</c>; <c>int32 totalLimit</c> }。
    /// </para>
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——"礼包表没打进包"必须当场暴露；
    /// 而 0 行是合法配置（<see cref="Count"/> == 0）：切片此时不展示任何礼包，购买统一返回
    /// <c>Unavailable</c>。列顺序即解析顺序，行顺序即快照顺序（不排序、不重排）。
    /// </para>
    /// </summary>
    public sealed class LimitedOfferTables
    {
        /// <summary>子表名（与 <c>tables/TbLimitedOffer.json</c> 的 <c>table</c> 字段一致）。</summary>
        private const string TableNameValue = "TbLimitedOffer";

        /// <summary>头部：<c>[int32 rowCount]</c>。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单个 int32 字段宽度（小端）。</summary>
        private const int Int32Bytes = 4;

        /// <summary>一行最少可读的字节数：10 个 int32 字段 + name 的长度前缀（即 11 个 int32）。</summary>
        private const int MinRowBytes = Int32Bytes * 11;

        /// <summary>实付百分比的合法下界（含）。</summary>
        private const int MinDiscountPercent = 1;

        /// <summary>实付百分比的合法上界（含）：100 = 原价。</summary>
        private const int MaxDiscountPercent = 100;

        private readonly LimitedOfferDef[] _rows;

        /// <summary>加载并解析 <c>TbLimitedOffer</c>；表缺失抛 <see cref="InvalidOperationException"/>，数据非法抛 <see cref="InvalidDataException"/>。</summary>
        /// <param name="loader">子表加载端口。</param>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">加载器返回 <c>null</c>，即子表缺失。</exception>
        /// <exception cref="InvalidDataException">截断、尾部残留、行数非法、id 非正或重复、名字为空、发放/原价非正、折扣越界、时间倒置、限购为负。</exception>
        public LimitedOfferTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("限时礼包子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数（礼包条数）；0 是合法配置。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按下标取行（行顺序 = 配置表行顺序）；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        /// <param name="index">0 起行下标。</param>
        public LimitedOfferDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "限时礼包子表下标越界，当前行数 " + _rows.Length + "。");
            }

            return _rows[index];
        }

        private static LimitedOfferDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("限时礼包子表头部长度不足 " + HeaderBytes + " 字节。");
            }

            PayloadReader reader = new PayloadReader(payload);
            int rowCount = reader.ReadInt32("rowCount");
            if (rowCount < 0)
            {
                throw new InvalidDataException("限时礼包子表 rowCount 为负：" + rowCount + "。");
            }

            // 先把"声明行数 × 单行最小字节"和剩余字节比一遍，避免畸形头部触发超大分配。
            if ((long)rowCount * MinRowBytes > reader.Remaining)
            {
                throw new InvalidDataException(
                    "限时礼包子表声明 " + rowCount + " 行，但只剩 " + reader.Remaining + " 字节，字节流被截断。");
            }

            LimitedOfferDef[] rows = new LimitedOfferDef[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                int offerId = reader.ReadInt32("offerId");
                string name = reader.ReadUtf8("name");
                int itemId = reader.ReadInt32("itemId");
                int grantCount = reader.ReadInt32("grantCount");
                int priceItemId = reader.ReadInt32("priceItemId");
                int originalPrice = reader.ReadInt32("originalPrice");
                int discountPercent = reader.ReadInt32("discountPercent");
                int startUnixSeconds = reader.ReadInt32("startUnixSeconds");
                int endUnixSeconds = reader.ReadInt32("endUnixSeconds");
                int dailyLimit = reader.ReadInt32("dailyLimit");
                int totalLimit = reader.ReadInt32("totalLimit");

                if (offerId <= 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 offerId 必须为正，第 " + i + " 行实际为 " + offerId + "。");
                }

                for (int k = 0; k < i; k++)
                {
                    if (rows[k].OfferId == offerId)
                    {
                        // 主键重复会让"限购计数"与"事件 id"无法一一对应：必须当成配置错误挡掉。
                        throw new InvalidDataException("限时礼包子表 offerId 重复：" + offerId + "。");
                    }
                }

                if (name.Length == 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 name 不能为空，offerId " + offerId + "。");
                }

                if (itemId <= 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 itemId 必须为正，offerId " + offerId + " 实际为 " + itemId + "。");
                }

                if (grantCount <= 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 grantCount 必须为正，offerId " + offerId + " 实际为 " + grantCount + "。");
                }

                if (priceItemId <= 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 priceItemId 必须为正，offerId " + offerId + " 实际为 " + priceItemId + "。");
                }

                if (originalPrice <= 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 originalPrice 必须为正，offerId " + offerId + " 实际为 " + originalPrice + "。");
                }

                if (discountPercent < MinDiscountPercent || discountPercent > MaxDiscountPercent)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 discountPercent 必须在 " + MinDiscountPercent + ".." + MaxDiscountPercent
                        + "，offerId " + offerId + " 实际为 " + discountPercent + "。");
                }

                if (endUnixSeconds <= startUnixSeconds)
                {
                    throw new InvalidDataException(
                        "限时礼包子表时间窗口必须满足 endUnixSeconds > startUnixSeconds，offerId " + offerId
                        + " 实际为 [" + startUnixSeconds + ", " + endUnixSeconds + ")。");
                }

                if (dailyLimit < 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 dailyLimit 不能为负，offerId " + offerId + " 实际为 " + dailyLimit + "。");
                }

                if (totalLimit < 0)
                {
                    throw new InvalidDataException(
                        "限时礼包子表 totalLimit 不能为负，offerId " + offerId + " 实际为 " + totalLimit + "。");
                }

                rows[i] = new LimitedOfferDef(
                    offerId,
                    name,
                    itemId,
                    grantCount,
                    priceItemId,
                    originalPrice,
                    discountPercent,
                    startUnixSeconds,
                    endUnixSeconds,
                    dailyLimit,
                    totalLimit);
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException(
                    "限时礼包子表尾部残留 " + reader.Remaining + " 字节，声明行数与字节流长度不符。");
            }

            return rows;
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
                        "限时礼包子表读取 " + fieldName + " 时字节流已截断：偏移 " + _offset
                        + "，剩余 " + Remaining + " 字节。");
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
                    throw new InvalidDataException(
                        "限时礼包子表 " + fieldName + " 长度为负：" + length + "。");
                }

                if (length > Remaining)
                {
                    throw new InvalidDataException(
                        "限时礼包子表读取 " + fieldName + " 时字节流已截断：声明 " + length
                        + " 字节，剩余 " + Remaining + " 字节。");
                }

                string text = Encoding.UTF8.GetString(_payload, _offset, length);
                _offset += length;
                return text;
            }
        }
    }
}
