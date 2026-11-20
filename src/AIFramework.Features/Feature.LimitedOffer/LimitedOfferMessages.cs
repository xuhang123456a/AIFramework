#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.LimitedOffer
{
    /// <summary>
    /// 购买限时礼包：先校验"礼包存在 / 在时间窗口内 / 限购未用尽"，通过后再发货并记账。
    /// <para>
    /// <b>不起草交易，也不碰余额</b>：本切片不做货币扣减判定（归属专属经济系统），
    /// 命中失败条件时什么都不发；命中成功时只发 <c>GrantItemCommand</c>（发货）与
    /// <see cref="LimitedOfferPurchasedEvent"/>（事实），货币侧由装配层在同一命令前后桥接。
    /// </para>
    /// <para>
    /// 命令不带时间参数：窗口与跨日判定只认构造注入的 <c>ITimeProvider</c>（AIF003）。
    /// 命令处理器返回 <c>void</c>（CQE 契约），需要拿到逐次结果时用
    /// <see cref="LimitedOfferHandler.Purchase"/> 这个同步入口。
    /// </para>
    /// </summary>
    public readonly struct LimitedOfferPurchaseCommand : ICommand
    {
        public LimitedOfferPurchaseCommand(int offerId)
        {
            OfferId = offerId;
        }

        /// <summary>要购买的礼包 id（配置主键）。</summary>
        public int OfferId { get; }
    }

    /// <summary>一次购买尝试的结果码。除 <see cref="Success"/> 外都不会产生任何副作用。</summary>
    public enum LimitedOfferPurchaseResult : byte
    {
        /// <summary>购买成功：已发 <c>GrantItemCommand</c>、已广播 <see cref="LimitedOfferPurchasedEvent"/>、限购计数已 +1。</summary>
        Success = 0,

        /// <summary>不在时间窗口内（<c>start &lt;= now &lt; end</c> 不成立：已结束或未开始）。</summary>
        NotOpen = 1,

        /// <summary>限购已用尽（当日限购或总限购任一命中）。</summary>
        LimitReached = 2,

        /// <summary>礼包 id 不在表里。</summary>
        UnknownOffer = 3,

        /// <summary>切片当前无法完成交易：本拍没有任何可售礼包（子表 0 行），即运营配置整体为空。</summary>
        Unavailable = 4
    }

    /// <summary>
    /// 一次购买尝试的完整结果：结果码 + 报价 + 失败原因（人读，成功时为空串）。
    /// <para>
    /// 失败时 <see cref="ItemId"/> / <see cref="GrantCount"/> / <see cref="FinalPrice"/> 仍按配置填出，
    /// 方便 UI 直接展示"这个礼包为什么买不了、原价多少"；只有 <see cref="UnknownOffer"/> 与
    /// <see cref="LimitedOfferPurchaseResult.Unavailable"/> 三项为 0。
    /// </para>
    /// </summary>
    public readonly struct LimitedOfferPurchaseOutcome
    {
        public LimitedOfferPurchaseOutcome(
            LimitedOfferPurchaseResult result,
            int offerId,
            int itemId,
            int grantCount,
            int finalPrice,
            string reason)
        {
            Result = result;
            OfferId = offerId;
            ItemId = itemId;
            GrantCount = grantCount;
            FinalPrice = finalPrice;
            Reason = reason ?? string.Empty;
        }

        /// <summary>结果码：只有 <see cref="LimitedOfferPurchaseResult.Success"/> 代表交易已成交。</summary>
        public LimitedOfferPurchaseResult Result { get; }

        /// <summary>本次尝试的礼包 id（回显入参）。</summary>
        public int OfferId { get; }

        /// <summary>发放道具 id（失败且未命中配置时为 0）。</summary>
        public int ItemId { get; }

        /// <summary>发放数量（失败且未命中配置时为 0）。</summary>
        public int GrantCount { get; }

        /// <summary>本次实付价（按折扣向下取整、至少 1；失败且未命中配置时为 0）。</summary>
        public int FinalPrice { get; }

        /// <summary>失败原因（人读，成功时为空串）；仅用于 UI 文案与日志，不做业务分支。</summary>
        public string Reason { get; }

        /// <summary>是否成交（等价于 <c>Result == Success</c>，给 UI 少写一次比较）。</summary>
        public bool IsSuccess
        {
            get { return Result == LimitedOfferPurchaseResult.Success; }
        }
    }

    /// <summary>
    /// 购买成功事实：每次成交广播一次，失败的尝试一条都不发。
    /// <para>
    /// 广播时机在"限购计数已 +1、发货指令已发出"之后、本次命令返回之前，
    /// 因此订阅方在回调里查询快照拿到的是**成交后**的剩余次数。
    /// </para>
    /// <para>
    /// 事件只描述"这笔礼包卖掉了"，不描述货币怎么扣的：扣款在契约外由装配层桥接，
    /// 本切片不持有余额，也不发 <c>ConsumeItemCommand</c>（避免与专属经济系统重复扣款）。
    /// </para>
    /// </summary>
    public readonly struct LimitedOfferPurchasedEvent : IEvent
    {
        public LimitedOfferPurchasedEvent(int offerId, int itemId, int grantCount, int finalPrice)
        {
            OfferId = offerId;
            ItemId = itemId;
            GrantCount = grantCount;
            FinalPrice = finalPrice;
        }

        /// <summary>成交的礼包 id（配置主键）。</summary>
        public int OfferId { get; }

        /// <summary>本次发放的道具 id。</summary>
        public int ItemId { get; }

        /// <summary>本次发放的道具数量。</summary>
        public int GrantCount { get; }

        /// <summary>本次实际结算价（已打折，向下取整、至少 1）。</summary>
        public int FinalPrice { get; }
    }
}
