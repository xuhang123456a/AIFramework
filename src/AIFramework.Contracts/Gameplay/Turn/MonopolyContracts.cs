#nullable enable
using System;

namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 大富翁格子种类。枚举值同时是运行期棋盘数组的存储编码（<c>byte</c>），
    /// 也是存档/遥测的稳定协议值，<b>不要重排</b>。
    /// <para>
    /// <b>固定布局</b>（20 格环形，索引 0..19）：0 = <see cref="Go"/>；4 = <see cref="Jail"/>、
    /// 8 = <see cref="Chance"/>、12 = <see cref="Tax"/>、16 = <see cref="Bonus"/>；其余 15 格为
    /// <see cref="Property"/>，按索引顺序价格从 200 起每块 +20（200/220/…/480）。
    /// </para>
    /// <para>
    /// <b>为什么是 15 块地产而不是 10 块</b>：需求文字里"10 块地产"与"20 格中只有 0/4/8/12/16 五格非地产"
    /// 在算术上不自洽（20 - 5 = 15）；本卡带按"其余为 Property"的显式规则落地为 15 块。
    /// 如果上游最终确认必须恰好 10 块，需要重新定义布局与价格序列（契约里的格子种类不变）。
    /// </para>
    /// </summary>
    public enum MonopolyTileKind : byte
    {
        /// <summary>起点：经过或停在时发 200；<see cref="Jail"/> 的"跳到起点"简化不触发本奖励。</summary>
        Go = 0,

        /// <summary>地产：无主可自动买入、自己的可升级、他人的需付租金。</summary>
        Property = 1,

        /// <summary>税务格：固定支付 100（现金不足时支付到 0，不破产）。</summary>
        Tax = 2,

        /// <summary>机会格：由种子随机抽 4 选 1（+100 / +200 / -100 / 前进 3 格）。</summary>
        Chance = 3,

        /// <summary>监狱格：直接跳回起点，不发起点奖励（简化规则）。</summary>
        Jail = 4,

        /// <summary>奖励格：固定获得 150。</summary>
        Bonus = 5
    }

    /// <summary>
    /// 大富翁玩家快照（只读值类型）：HUD 与棋子表现层只经
    /// <see cref="IMonopolyReadView.GetPlayer"/> 读取，不碰内核内部数组。
    /// </summary>
    public readonly struct MonopolyPlayerView
    {
        public MonopolyPlayerView(int playerIndex, int cash, int position, bool isBankrupt, int propertyCount)
        {
            PlayerIndex = playerIndex;
            Cash = cash;
            Position = position;
            IsBankrupt = isBankrupt;
            PropertyCount = propertyCount;
        }

        /// <summary>玩家索引（0 基；0 = 本地玩家，1..3 = AI 对手）。</summary>
        public int PlayerIndex { get; }

        /// <summary>当前现金（下限 0；破产后恒为 0）。</summary>
        public int Cash { get; }

        /// <summary>当前所在格（0..19）；破产后停在最后一次落点不再移动。</summary>
        public int Position { get; }

        /// <summary>是否已破产出局：地产已全部归还无主，不再参与回合轮转。</summary>
        public bool IsBankrupt { get; }

        /// <summary>持有地产数（0..15；破产后恒为 0）。</summary>
        public int PropertyCount { get; }
    }

    /// <summary>
    /// 大富翁格子快照（只读值类型）：表现层按 <see cref="Kind"/> 切图标、按
    /// <see cref="Owner"/> / <see cref="Level"/> 染色，不需要知道经济规则。
    /// </summary>
    public readonly struct MonopolyTileView
    {
        public MonopolyTileView(int tileIndex, MonopolyTileKind kind, int owner, int level, int price)
        {
            TileIndex = tileIndex;
            Kind = kind;
            Owner = owner;
            Level = level;
            Price = price;
        }

        /// <summary>格子索引（0..19）。</summary>
        public int TileIndex { get; }

        /// <summary>格子种类。</summary>
        public MonopolyTileKind Kind { get; }

        /// <summary>地产所有者玩家索引；<c>-1</c> = 无主。非地产格恒为 <c>-1</c>。</summary>
        public int Owner { get; }

        /// <summary>地产等级（0..3，0 = 刚买入）；非地产格与无主地产恒为 0。</summary>
        public int Level { get; }

        /// <summary>地产买入价（200/220/…/480）；非地产格恒为 0。</summary>
        public int Price { get; }
    }

    /// <summary>
    /// 大富翁只读视图：Controller / HUD / 棋盘表现层每帧只读这里，不碰内核内部数组。
    /// <para>
    /// <b>回合口径</b>：<see cref="TurnIndex"/> 从 0 开始，每执行一次 <see cref="IMonopolyInput.TryRoll"/> +1；
    /// <see cref="CurrentPlayer"/> 为下一个该掷骰的存活玩家；<see cref="RoundNumber"/> =
    /// <c>TurnIndex / PlayerCount + 1</c>（1 基名义回合数，按配置玩家数换算）。
    /// </para>
    /// <para>
    /// <b>越界口径</b>：<see cref="GetPlayer"/> / <see cref="GetTile"/> 越界（含负数）抛
    /// <see cref="ArgumentOutOfRangeException"/>；表现层按 <see cref="PlayerCount"/> /
    /// <see cref="TileCount"/> 做循环即可，不必额外判界。
    /// </para>
    /// </summary>
    public interface IMonopolyReadView
    {
        /// <summary>棋盘格子总数（固定 20，环形成环）。</summary>
        int TileCount { get; }

        /// <summary>
        /// 参战玩家数（固定 1..4，由 <c>GameplayInitData.UnitCount</c> 夹取：&lt;= 0 → 默认 2、
        /// 1..4 原样、&gt; 4 → 4）。破产不会缩小本数值，破产位仍可用 <see cref="GetPlayer"/> 读出。
        /// </summary>
        int PlayerCount { get; }

        /// <summary>已执行的掷骰回合数（从 0 开始，每成功掷一次 +1；也是结算报告里的 StepCount 口径）。</summary>
        int TurnIndex { get; }

        /// <summary>下一个该掷骰的存活玩家索引（0..PlayerCount-1；对局结束后停在最后推进到的位置）。</summary>
        int CurrentPlayer { get; }

        /// <summary>名义回合数（1 基）：<c>TurnIndex / PlayerCount + 1</c>。</summary>
        int RoundNumber { get; }

        /// <summary>对局是否结束（只剩 1 名存活玩家、40 回合封顶、或结算报告已被取回）。</summary>
        bool IsFinished { get; }

        /// <summary>胜者玩家索引；<c>-1</c> = 未结束。</summary>
        int WinnerPlayer { get; }

        /// <summary>取第 <paramref name="index"/> 名玩家的快照；越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        MonopolyPlayerView GetPlayer(int index);

        /// <summary>取第 <paramref name="index"/> 个格子的快照；越界（含负数）抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        MonopolyTileView GetTile(int index);

        /// <summary>最近一次掷出的骰子点数（1..6）；尚未掷骰时为 0。</summary>
        int LastDice { get; }

        /// <summary>最近一次行动（掷骰）的玩家索引；尚未掷骰时为 <c>-1</c>。该玩家可能在本回合后破产。</summary>
        int LastMovedPlayer { get; }

        /// <summary>
        /// 最近一次行动结算完成后的最终落点（0..19）；尚未掷骰时为 <c>-1</c>。
        /// <b>注意</b>：最终落点已包含格效果位移——踩 <see cref="MonopolyTileKind.Jail"/> 后为 0（起点）、
        /// 抽到"前进 3 格"后为前进后的格子。
        /// </summary>
        int LastMovedTo { get; }

        /// <summary>累计抽到的机会卡张数（每次真正落在机会格并抽卡 +1，含"前进 3 格"触发的二次抽卡）。</summary>
        int ChanceEvents { get; }

        /// <summary>
        /// 累计"踩到无主地产但现金不足"而跳过购买的次数（会话级诊断量，规格清单外的补充字段；
        /// 升级地产现金不足而跳过不计入本计数）。
        /// </summary>
        int SkippedPurchaseCount { get; }
    }

    /// <summary>
    /// 大富翁输入端口：适配层/AI 驱动只能通过它提交意图，不能直接改内核状态。
    /// <para>
    /// <b>同步结算</b>：一次 <see cref="TryRoll"/> 内完成"掷骰 → 移动 → 结算格子 → 结束本回合"，
    /// 返回时所有读视图已是结算后的状态；表现层拿到返回后直接播动画即可，无需再调用 Step 推进逻辑。
    /// </para>
    /// <para>对局结束后（含结算报告已取回）所有方法一律返回 false / 空操作，不抛异常。</para>
    /// </summary>
    public interface IMonopolyInput
    {
        /// <summary>
        /// 当前玩家掷一次 1d6（种子随机）并完整结算本回合：移动、结算落点（买地/租金/升级/税/奖励/机会/监狱）、
        /// <see cref="IMonopolyReadView.TurnIndex"/> +1、回合交给下一名存活玩家。
        /// <para>
        /// <b>返回 false 的唯一条件</b>：对局已结束（<see cref="IMonopolyReadView.IsFinished"/> 为 true，含结算已取回）。
        /// 未结束时每次调用都必然成功执行一个完整回合；不存在"同一玩家连滚两次"（每次调用立即换手），
        /// 单人局（PlayerCount = 1）除外。
        /// </para>
        /// <para>破产玩家的回合会被内核自动跳过，调用方不需要自己判断 <see cref="MonopolyPlayerView.IsBankrupt"/>。</para>
        /// </summary>
        bool TryRoll();
    }
}
