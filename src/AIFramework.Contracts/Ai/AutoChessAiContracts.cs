#nullable enable
namespace AIFramework.Contracts.Ai
{
    /// <summary>
    /// 自走棋 AI 的原子动作：一次决策只产出一个动作，执行器做完再把新状态喂回来。
    /// <para>
    /// <b>为什么没有独立的 Place</b>：棋子上场在自走棋里有两种来源——"从商店买一张新牌"与
    /// "把已经拥有的牌从备战席交上去"。两者的<b>执行语义不同</b>（前者扣金币，后者不扣），
    /// 但落点都是"把某张牌放到某格"，因此共用 <see cref="Buy"/> 承载，
    /// 靠 <see cref="AutoChessDecision.Reason"/> 的 <c>"fill board"</c> / <c>"upgrade copy"</c> /
    /// <c>"place unit"</c> 区分。执行器必须先看 reason 再决定要不要扣钱。
    /// </para>
    /// </summary>
    public enum AutoChessAiAction : byte
    {
        /// <summary>等待：攒利息、或当前状态没有可执行的行动。</summary>
        Wait = 0,

        /// <summary>买棋 / 上场：执行语义由 <see cref="AutoChessDecision.Reason"/> 区分。</summary>
        Buy = 1,

        /// <summary>卖棋：当前策略只在"上场数超过人口上限"这种违例状态下用它修复。</summary>
        Sell = 2,

        /// <summary>升人口：花金币提高可上场棋子数上限。</summary>
        LevelUp = 3,

        /// <summary>刷新商店：低血量时花金币找翻盘点。</summary>
        Reroll = 4,
    }

    /// <summary>
    /// 棋盘上的一个己方棋子快照（只读值类型）。
    /// <para>
    /// <b>为什么坐标一起给</b>：摆放策略判断"哪一格空着"的唯一依据，就是棋盘上已有棋子占了哪些格；
    /// 坐标 <c>-1</c> 沿用内核"备战席 = 未上场"的约定，<c>GridX &lt; 0</c> 或 <c>GridY &lt; 0</c> 一律视为未上场，
    /// 不计入上场人数、也不占格。
    /// </para>
    /// </summary>
    public readonly struct AutoChessBoardUnit
    {
        public AutoChessBoardUnit(int unitId, int cost, int star, int gridX, int gridY, bool isFrontline)
        {
            UnitId = unitId;
            Cost = cost;
            Star = star;
            GridX = gridX;
            GridY = gridY;
            IsFrontline = isFrontline;
        }

        /// <summary>稳定单位 ID（同一 ID 在棋盘上最多一个实体）。</summary>
        public int UnitId { get; }

        /// <summary>购买价（正数）；负值视为"价格未知"，不参与任何买卖判断。</summary>
        public int Cost { get; }

        /// <summary>星级（1..3）：同前排、同价时优先卖掉星级更低的。</summary>
        public int Star { get; }

        /// <summary>棋盘格横坐标；<c>-1</c> 表示未上场（备战席）。</summary>
        public int GridX { get; }

        /// <summary>棋盘格纵坐标；<c>-1</c> 表示未上场（备战席）。</summary>
        public int GridY { get; }

        /// <summary>
        /// 是否站在前排：由调用方按自己的坐标轴方向标注（策略不推断 <see cref="GridY"/> 的方向）。
        /// 只在"超编该卖谁"时读取——先卖后排，保住前排肉盾。
        /// </summary>
        public bool IsFrontline { get; }
    }

    /// <summary>
    /// 商店一格（只读值类型）：要么是可以买的新棋子，要么是已经拥有的棋子的补星位。
    /// <para>
    /// <b>已拥有但没上场的棋子也放在这里</b>（<see cref="IsOwned"/> = <c>true</c>）：
    /// 这是契约里唯一能表达"我手上有这张牌"的字段，摆放规则靠"已拥有 + 棋盘上查不到同 ID"
    /// 两个条件共同识别备战席棋子。若调用方不把备战席棋子放进 <see cref="AutoChessState.Shop"/>，
    /// 这些棋子对 AI 来说就不存在。
    /// </para>
    /// </summary>
    public readonly struct AutoChessShopSlot
    {
        public AutoChessShopSlot(int unitId, int cost, bool isOwned, int copiesNeededToUpgrade)
        {
            UnitId = unitId;
            Cost = cost;
            IsOwned = isOwned;
            CopiesNeededToUpgrade = copiesNeededToUpgrade;
        }

        /// <summary>该格棋子的稳定单位 ID。</summary>
        public int UnitId { get; }

        /// <summary>购买价（正数）；负值视为"价格未知"，策略不会买它。</summary>
        public int Cost { get; }

        /// <summary>
        /// 是否已拥有（<c>true</c> = 手上至少有这张牌）。
        /// <para>
        /// <b>为什么"买"已拥有的牌仍然算 Buy</b>：自走棋的升星就是继续买同名牌，区别只在策略选择——
        /// "补满棋盘"只从 <c>false</c> 的格子里挑（买一张新牌才多一个身体），
        /// "凑星"才允许挑 <c>true</c> 的格子；两者共用 <see cref="AutoChessAiAction.Buy"/>，
        /// 但补齐人口与升星的执行结果不同。
        /// </para>
        /// </summary>
        public bool IsOwned { get; }

        /// <summary>
        /// 再买几张就能升星（调用方已把手上张数算进去）：<c>1</c> = 差一张即可升星，
        /// <c>0</c> = 已经能升，其他值 = 还差得更远。策略只在等于 <c>1</c> 时触发凑星。
        /// </summary>
        public int CopiesNeededToUpgrade { get; }
    }

    /// <summary>
    /// 一次决策的完整只读输入：金币、等级、回合、血量、人口上限、棋盘、商店、连胜。
    /// <para>
    /// <b>为什么整包传值，而不是让 AI 持有一个世界引用</b>：策略必须是纯函数——同一份快照 + 同一条随机流
    /// 必然得到同一个决策，回放、单测与人机对拍才有共同口径；AI 若直接读内核，规则一改就再也对不上。
    /// </para>
    /// <para>
    /// <b>null 与空数组等价</b>：<see cref="Board"/> / <see cref="Shop"/> 允许为 <c>null</c>
    /// （<c>default(AutoChessState)</c> 就是这种形态），策略按空棋盘/空商店处理，不抛异常。
    /// </para>
    /// <para>
    /// <b>不写回</b>：策略只读本结构，绝不修改数组内容；执行器可以放心把同一份快照喂给多次决策。
    /// </para>
    /// </summary>
    public readonly struct AutoChessState
    {
        public AutoChessState(
            int gold,
            int level,
            int round,
            int hp,
            int boardCapacity,
            AutoChessBoardUnit[] board,
            AutoChessShopSlot[] shop,
            int streak)
        {
            Gold = gold;
            Level = level;
            Round = round;
            Hp = hp;
            BoardCapacity = boardCapacity;
            Board = board;
            Shop = shop;
            Streak = streak;
        }

        /// <summary>金币；负值按"一分钱都没有"处理（任何正价棋子都买不起）。</summary>
        public int Gold { get; }

        /// <summary>当前等级（1 起）；只有低于 6 级才会升人口。</summary>
        public int Level { get; }

        /// <summary>回合序号（0 基）；当前策略不读它，保留给"前期/后期"分层策略与回放对齐。</summary>
        public int Round { get; }

        /// <summary>玩家血量；<c>&lt;= 30</c> 且金币 <c>&gt;= 20</c> 时触发低血搜牌。</summary>
        public int Hp { get; }

        /// <summary>人口上限（可上场棋子数）；与棋盘上"坐标非负"的条目数之差就是空位数。</summary>
        public int BoardCapacity { get; }

        /// <summary>已上场的己方棋子；坐标 <c>-1</c> 的条目视为备战席，不计入上场数。</summary>
        public AutoChessBoardUnit[] Board { get; }

        /// <summary>
        /// 当前商店；<b>已拥有但没上场的棋子也必须以 <see cref="AutoChessShopSlot.IsOwned"/> = <c>true</c>
        /// 的槽位出现在这里</b>，否则摆放规则看不到它们。
        /// </summary>
        public AutoChessShopSlot[] Shop { get; }

        /// <summary>连胜（正）/ 连败（负）计数；当前策略不读它，保留给连败保底与连胜利息规则。</summary>
        public int Streak { get; }
    }

    /// <summary>
    /// 一次决策的输出：一个动作 + 目标单位 + 建议落点 + 稳定原因串。
    /// <para>
    /// <b>执行器协议</b>（本契约唯一的硬约定）：
    /// <list type="bullet">
    /// <item><description><c>reason = "fill board"</c>：支付该棋子的 <c>Cost</c> 买下 <see cref="TargetUnitId"/>，
    /// 落点参考 <see cref="GridX"/> / <see cref="GridY"/>。</description></item>
    /// <item><description><c>reason = "upgrade copy"</c>：支付 <c>Cost</c> 买下同一张牌完成升星（不需要落点）。</description></item>
    /// <item><description><c>reason = "place unit"</c>：目标<b>已经在手</b>（备战席），只上场、<b>不扣金币</b>。</description></item>
    /// <item><description>其余动作：<see cref="TargetUnitId"/> 与落点均为 <c>-1</c>。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为什么 reason 不是本地化文案</b>：它是策略与执行器之间的稳定标识（也进日志与回放），
    /// 改文案等于改协议；玩家可见的说明由 UI 层按动作自己翻译。
    /// </para>
    /// </summary>
    public readonly struct AutoChessDecision
    {
        public AutoChessDecision(
            AutoChessAiAction action,
            int targetUnitId,
            int gridX,
            int gridY,
            string reason)
        {
            Action = action;
            TargetUnitId = targetUnitId;
            GridX = gridX;
            GridY = gridY;
            Reason = reason;
        }

        /// <summary>动作码。</summary>
        public AutoChessAiAction Action { get; }

        /// <summary>目标单位 ID；<c>-1</c> 表示该动作不针对具体棋子。</summary>
        public int TargetUnitId { get; }

        /// <summary>建议落点横坐标；<c>-1</c> 表示不涉及落点。</summary>
        public int GridX { get; }

        /// <summary>建议落点纵坐标；<c>-1</c> 表示不涉及落点。</summary>
        public int GridY { get; }

        /// <summary>稳定原因串（见类型说明的执行器协议），不可为 <c>null</c>。</summary>
        public string Reason { get; }
    }
}
