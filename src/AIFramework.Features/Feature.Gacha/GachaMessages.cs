using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Gacha
{
    /// <summary>
    /// 抽卡请求。<see cref="Count"/> 只接受 1（单抽）或 10（十连），
    /// 其余值（0、2、11、负数）一律静默拒绝：不查余额、不扣费、不发指令、不发事件。
    /// </summary>
    public readonly struct DrawGachaCommand : ICommand
    {
        public DrawGachaCommand(int count)
        {
            Count = count;
        }

        /// <summary>抽卡次数；Phase 1 只支持 1 或 10。</summary>
        public int Count { get; }
    }

    /// <summary>
    /// 抽中一项的事实。<b>每抽一项发一条</b>，十连按抽卡顺序发 10 条；
    /// 只有真正完成扣费并抽卡后才广播，被拒绝的请求不发任何事件。
    /// </summary>
    public readonly struct GachaDrawnEvent : IEvent
    {
        public GachaDrawnEvent(int itemId, bool isRare, bool pityTriggered)
        {
            ItemId = itemId;
            IsRare = isRare;
            PityTriggered = pityTriggered;
        }

        /// <summary>本次抽中的道具 ID。</summary>
        public int ItemId { get; }

        /// <summary>抽中项是否来自稀有池（自然命中或被保底改判后的结果都按改判后状态标记）。</summary>
        public bool IsRare { get; }

        /// <summary>本次单项是否由保底触发（第 10 次仍非稀有则强制改判为稀有）。</summary>
        public bool PityTriggered { get; }
    }
}
