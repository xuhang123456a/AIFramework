namespace AIFramework.Features.Gacha
{
    /// <summary>
    /// 抽卡面板的一次渲染快照：金币 + 最近一次抽卡结果的结构化描述。
    /// <para>
    /// 视图接口 <c>IGachaView</c> 由 <c>tools/viewgen</c> 从本切片的 <c>Gacha.bindings.json</c>
    /// 生成为 <c>IGachaView.g.cs</c>（禁止手改）；面板没有列表，所以本结构不参与接口签名，
    /// 只作为"刚才到底推了什么给视图"的可断言副本（与 <c>NullSignInView.LastRendered</c> 同一定位）。
    /// </para>
    /// <para>
    /// <b>刻意不包含"能不能抽"</b>：单抽/十连的费用是 <see cref="GachaHandler"/> 的私有规则，
    /// 面板若自行换算就会和处理器漂移；能否抽永远由处理器判定，被拒绝时它静默返回。
    /// </para>
    /// </summary>
    public readonly struct GachaViewData
    {
        public GachaViewData(int gold, string lastResultText, int lastItemCount, bool pityTriggered)
        {
            Gold = gold;
            LastResultText = lastResultText;
            LastItemCount = lastItemCount;
            PityTriggered = pityTriggered;
        }

        /// <summary>本次渲染的金币（来自 <c>InventoryQueryRequest(Gold)</c>，不是本地账本）。</summary>
        public int Gold { get; }

        /// <summary>推给视图的最近结果文本（未抽过时为占位文案）。</summary>
        public string LastResultText { get; }

        /// <summary>最近一次结果包含的项数：未抽过 0、单抽 1、十连 10。</summary>
        public int LastItemCount { get; }

        /// <summary>最近一次结果是否由保底触发（十连中任一项触发即为 <c>true</c>）。</summary>
        public bool PityTriggered { get; }
    }
}
