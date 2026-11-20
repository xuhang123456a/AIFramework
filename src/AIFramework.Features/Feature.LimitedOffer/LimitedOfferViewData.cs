#nullable enable
using System;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.LimitedOffer
{
    /// <summary>
    /// 单个礼包的展示状态：配置列 + 查询时刻折算的报价与剩余次数。
    /// <para>
    /// <see cref="IsOpen"/> 按**查询时刻**实时判定（<c>start &lt;= now &lt; end</c>），
    /// 不是"上次操作时的快照"：礼包到点后就该在 UI 上立刻变灰，不需要先买一次才刷新。
    /// </para>
    /// <para>
    /// <see cref="DailyRemaining"/> 在"今天没买过"时等于表里的 <c>dailyLimit</c>；
    /// 表里 <c>dailyLimit = 0</c> 表示不限当日，此时该字段固定为 <see cref="UnlimitedRemaining"/>
    /// （<c>int.MaxValue</c>），UI 应把它翻译成"不限购"而不是显示一个巨大数字。
    /// </para>
    /// <para>
    /// <see cref="SecondsRemaining"/> 只在 <see cref="IsOpen"/> 为 true 时有意义（<c>end - now</c>），
    /// 未开始与已结束恒为 0。
    /// </para>
    /// </summary>
    public readonly struct LimitedOfferState
    {
        /// <summary>"不限购"的哨兵值：<c>dailyLimit = 0</c> 时 <see cref="DailyRemaining"/> 取它。</summary>
        public const int UnlimitedRemaining = int.MaxValue;

        public LimitedOfferState(
            int offerId,
            string name,
            bool isOpen,
            int finalPrice,
            int originalPrice,
            int discountPercent,
            int dailyRemaining,
            int totalRemaining,
            long secondsRemaining)
        {
            OfferId = offerId;
            Name = name;
            IsOpen = isOpen;
            FinalPrice = finalPrice;
            OriginalPrice = originalPrice;
            DiscountPercent = discountPercent;
            DailyRemaining = dailyRemaining;
            TotalRemaining = totalRemaining;
            SecondsRemaining = secondsRemaining;
        }

        /// <summary>礼包 id（配置主键）。</summary>
        public int OfferId { get; }

        /// <summary>礼包显示名。</summary>
        public string Name { get; }

        /// <summary>查询时刻是否在可购买窗口内（左闭右开）。</summary>
        public bool IsOpen { get; }

        /// <summary>实付价：<c>floor(originalPrice * discountPercent / 100)</c>，至少 1。</summary>
        public int FinalPrice { get; }

        /// <summary>原价。</summary>
        public int OriginalPrice { get; }

        /// <summary>实付百分比（1..100）。</summary>
        public int DiscountPercent { get; }

        /// <summary>今日（UTC 自然日）还能买几次；<c>dailyLimit = 0</c> 时为 <see cref="UnlimitedRemaining"/>。</summary>
        public int DailyRemaining { get; }

        /// <summary>总计还能买几次（≥ 0）。</summary>
        public int TotalRemaining { get; }

        /// <summary>距关闭还剩多少秒（仅开启时有值）；已结束或未开始为 0。</summary>
        public long SecondsRemaining { get; }

        /// <summary>今日是否不限购（<c>dailyLimit = 0</c>）：UI 用它决定显示"不限"还是数字。</summary>
        public bool IsDailyUnlimited
        {
            get { return DailyRemaining == UnlimitedRemaining; }
        }
    }

    /// <summary>
    /// 限时礼包全量只读快照：顺序 = 配置表行顺序，构造后不可变。
    /// <para>
    /// 内容按**查询时刻**实时折算（窗口、报价、剩余次数都随当前时间源刷新），
    /// 因此同一份快照不必再 tick 一次就能反映"刚过零点、每日限购回满"。
    /// </para>
    /// <para>
    /// 退化约定：子表 0 行时 <see cref="Offers"/> 为空数组、<see cref="OpenCount"/> 为 0，
    /// 不抛异常也不发事件。
    /// </para>
    /// </summary>
    public sealed class LimitedOfferSnapshot
    {
        public LimitedOfferSnapshot(LimitedOfferState[] offers, int openCount, long nowUnixSeconds)
        {
            if (offers == null)
            {
                throw new ArgumentNullException(nameof(offers));
            }

            Offers = offers;
            OpenCount = openCount;
            NowUnixSeconds = nowUnixSeconds;
        }

        /// <summary>全部礼包状态，顺序 = 配置表行顺序；空表为空数组。</summary>
        public LimitedOfferState[] Offers { get; }

        /// <summary>查询时刻处于可购买窗口内的礼包条数。</summary>
        public int OpenCount { get; }

        /// <summary>生成快照时的 UTC Unix 秒（来自注入的 <c>ITimeProvider</c>，不是本机时钟）。</summary>
        public long NowUnixSeconds { get; }
    }

    /// <summary>限时礼包全量查询请求：无参数，返回按查询时刻折算的全量快照。</summary>
    public readonly struct LimitedOfferQueryRequest : IQuery<LimitedOfferSnapshot>
    {
    }
}
