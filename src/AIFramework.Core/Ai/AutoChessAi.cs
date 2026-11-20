#nullable enable
using System;
using AIFramework.Contracts.Ai;
using AIFramework.Contracts.Ports;

namespace AIFramework.Core.Ai
{
    /// <summary>
    /// 自走棋 AI 对手的策略大脑：吃一份 <see cref="AutoChessState"/> 快照，吐<b>一个</b> <see cref="AutoChessDecision"/>。
    /// <para>
    /// <b>一次只做一个动作</b>：<see cref="Decide"/> 不批量操作、不推进回合，执行器做完动作把新状态喂回来即可。
    /// 这条约束换来三件事——回放能逐条复现、单测能逐条断言、玩家能看到 AI"一步一步想"的过程；
    /// 批量执行则会把"为什么卖了这个"埋进一次调用里，出问题只能靠整局对拍。
    /// </para>
    /// <para>
    /// <b>优先级（从高到低，与 <see cref="Decide"/> 的判定顺序逐条对应）</b>：
    /// <list type="number">
    /// <item><description>违例修复：上场数超出人口上限 → <see cref="AutoChessAiAction.Sell"/>（reason <c>"over capacity"</c>）。</description></item>
    /// <item><description>凑星：商店有"差一张即可升星"且买得起的牌 → <see cref="AutoChessAiAction.Buy"/>（<c>"upgrade copy"</c>）。</description></item>
    /// <item><description>补满棋盘：还有空位且有买得起的新棋子 → 买最便宜的（<c>"fill board"</c>）。</description></item>
    /// <item><description>升人口：金币达标 + 棋盘已满 + 等级 &lt; 6 → <see cref="AutoChessAiAction.LevelUp"/>（<c>"level up"</c>）。</description></item>
    /// <item><description>攒钱：金币低于经济下限且不属于紧急情况 → <see cref="AutoChessAiAction.Wait"/>（<c>"economy hold"</c>）。</description></item>
    /// <item><description>低血搜牌：血量 &lt;= 30 且金币 &gt;= 20 → <see cref="AutoChessAiAction.Reroll"/>（<c>"desperate reroll"</c>）。</description></item>
    /// <item><description>上场：拥有但没上场的棋子在棋盘还有空位时 → <see cref="AutoChessAiAction.Buy"/>（<c>"place unit"</c>，不扣金币）。</description></item>
    /// <item><description>无事可做 → <see cref="AutoChessAiAction.Wait"/>（<c>"noop"</c>）。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为什么违例修复排在最前</b>：上场数大于人口上限时，棋盘已经是一份违反规则的状态，
    /// 此时再买、再升人口都只会把错误放大；先卖掉一个最该卖的把状态拉回合法，其余策略才有意义。
    /// 正常状态下这条分支永远不会命中（上场数由执行器保证不超编）。
    /// </para>
    /// <para>
    /// <b>经济下限与"紧急情况"</b>：第 5 条把"血量 &lt;= 30 且金币 &gt;= 20"视为紧急需求并主动让路，
    /// 否则低血时会被攒钱规则永远按在 Wait 上——这正是"低血量狂搜"要避免的死局。
    /// 金币不够 20 时不算紧急：连刷新都不值得做的时候，攒钱等下一波才是正解。
    /// </para>
    /// <para>
    /// <b>确定性</b>：同一份状态 + 同一条随机流 → 同一个决策。<see cref="IRandomSource"/> 只用于
    /// "同价候选"的挑选，且只在同类候选多于一个时消耗随机数；没有随机源时固定取 unitId 最小的候选，
    /// 因此"不注入随机源"也是一种可复现配置（回放/单测默认走这条）。
    /// 策略本身<b>不持有跨决策状态</b>（只累加诊断计数 <see cref="DecisionsMade"/>），
    /// 同一个状态重复喂进来，除随机流推进外结果稳定。
    /// </para>
    /// <para>
    /// <b>零分配</b>：<see cref="Decide"/> 不用 LINQ、不建临时集合、不拼字符串（reason 全是编译期常量），
    /// 预热后连续决策的托管堆增量应为 0；候选挑选是两遍线性扫描 + 一次 <see cref="IRandomSource.NextInt"/>。
    /// </para>
    /// <para>
    /// <b>线程</b>：实例会写 <see cref="DecisionsMade"/>，不支持多线程并发决策；每个 AI 对手各持一份实例
    /// （它们本来就该有不同的随机流）。
    /// </para>
    /// <para>
    /// <b>摆放坐标约定</b>：落点按"己方半场 8 列 × 4 行、从前往后、从左到右"扫描第一个空格
    /// （与 <c>Kernel.Turn</c> 自走棋内核 <c>TryPlace</c> 的扫描顺序一致）。该约定只是<b>建议落点</b>：
    /// 内核若不开放"指定落点"，执行器按自己的规则摆即可；棋盘不是 8×4 时坐标可能落在界外，
    /// 执行器需要自行夹取或忽略。
    /// </para>
    /// </summary>
    public sealed class AutoChessAi
    {
        /// <summary>升人口的上限等级：到了这一级就不再花金币升人口。</summary>
        private const int MaxLevel = 6;

        /// <summary>低血搜牌的血量门槛（含）。</summary>
        private const int LowHpThreshold = 30;

        /// <summary>低血搜牌要求的最低金币（含）：低于这个数连刷新都不值得做。</summary>
        private const int LowHpRerollGold = 20;

        /// <summary>默认经济下限：金币少于它时优先攒利息，而不是花钱。</summary>
        private const int DefaultEconomyFloor = 50;

        /// <summary>建议落点的列数（自走棋标准棋盘 8 列，与回合内核的自走棋棋盘一致）。</summary>
        private const int PlacementColumns = 8;

        /// <summary>建议落点的行数（己方半场 4 行，逐行从前往后填）。</summary>
        private const int PlacementRows = 4;

        /// <summary>原因串：上场数超编，卖一个修复。</summary>
        private const string ReasonOverCapacity = "over capacity";

        /// <summary>原因串：差一张即可升星，买下这张。</summary>
        private const string ReasonUpgradeCopy = "upgrade copy";

        /// <summary>原因串：棋盘有空位，买一张最便宜的新棋子。</summary>
        private const string ReasonFillBoard = "fill board";

        /// <summary>原因串：金币达标且棋盘已满，升人口。</summary>
        private const string ReasonLevelUp = "level up";

        /// <summary>原因串：金币低于经济下限，攒利息。</summary>
        private const string ReasonEconomyHold = "economy hold";

        /// <summary>原因串：低血量花金币刷新商店。</summary>
        private const string ReasonReroll = "desperate reroll";

        /// <summary>原因串：把已拥有的棋子从备战席放到棋盘上（不扣金币）。</summary>
        private const string ReasonPlaceUnit = "place unit";

        /// <summary>原因串：当前状态没有可执行的行动。</summary>
        private const string ReasonNoop = "noop";

        /// <summary>同价候选的随机挑选源；<c>null</c> 表示固定取 unitId 最小的候选。</summary>
        private readonly IRandomSource? _random;

        /// <summary>经济下限：低于它时除非紧急，否则不花钱。</summary>
        private readonly int _economyFloor;

        /// <summary>累计决策次数（含 Wait），诊断/回放用。</summary>
        private int _decisionsMade;

        /// <summary>
        /// 构造策略大脑。
        /// </summary>
        /// <param name="random">同价候选的随机源；<c>null</c> 时固定取 unitId 最小的候选（可复现）。</param>
        /// <param name="economyFloor">经济下限：金币低于它时优先攒利息；<c>0</c> 表示不攒钱（没有经济系统的宿主用）。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="economyFloor"/> 为负数。</exception>
        public AutoChessAi(IRandomSource? random = null, int economyFloor = DefaultEconomyFloor)
        {
            if (economyFloor < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(economyFloor), economyFloor, "经济下限不能为负：金币本身按 >= 0 处理，负下限等于取消攒钱规则。");
            }

            _random = random;
            _economyFloor = economyFloor;
            _decisionsMade = 0;
        }

        /// <summary>累计决策次数（含 <see cref="AutoChessAiAction.Wait"/>）；每次 <see cref="Decide"/> +1。</summary>
        public int DecisionsMade
        {
            get { return _decisionsMade; }
        }

        /// <summary>
        /// 依据状态快照给出<b>下一步</b>动作。纯读输入：不修改 <paramref name="state"/>、不推进回合、不抛异常。
        /// <para>
        /// 判定顺序即优先级：超编卖出 → 凑星 → 补满棋盘 → 升人口 → 攒钱 → 低血搜牌 → 上场 → 等待。
        /// 具体条件与 reason 见类型注释。
        /// </para>
        /// </summary>
        /// <param name="state">本回合的只读状态快照；<c>Board</c> / <c>Shop</c> 为 <c>null</c> 时按空集合处理。</param>
        /// <returns>本步要执行的动作；没有可行动作时返回 <see cref="AutoChessAiAction.Wait"/> + <c>"noop"</c>。</returns>
        public AutoChessDecision Decide(in AutoChessState state)
        {
            _decisionsMade++;

            AutoChessBoardUnit[] board = state.Board;
            if (board == null)
            {
                board = Array.Empty<AutoChessBoardUnit>();
            }

            AutoChessShopSlot[] shop = state.Shop;
            if (shop == null)
            {
                shop = Array.Empty<AutoChessShopSlot>();
            }

            int deployed = 0;
            for (int i = 0; i < board.Length; i++)
            {
                if (IsDeployed(in board[i]))
                {
                    deployed++;
                }
            }

            int capacity = state.BoardCapacity;
            if (capacity < 0)
            {
                capacity = 0;
            }

            int freeSlots = capacity - deployed;
            if (freeSlots < 0)
            {
                freeSlots = 0;
            }

            // ① 违例修复：上场数超过人口上限，先卖一个把棋盘拉回合法状态。
            if (deployed > capacity)
            {
                int sellIndex = FindSellCandidate(board);
                if (sellIndex >= 0)
                {
                    return new AutoChessDecision(
                        AutoChessAiAction.Sell, board[sellIndex].UnitId, -1, -1, ReasonOverCapacity);
                }
            }

            // ② 凑星：差一张即可升星且买得起，优先补这张（升星的战力/经济收益最高）。
            int upgradeIndex = FindUpgradeCopy(shop, state.Gold);
            if (upgradeIndex >= 0)
            {
                return new AutoChessDecision(
                    AutoChessAiAction.Buy, shop[upgradeIndex].UnitId, -1, -1, ReasonUpgradeCopy);
            }

            // ③ 补满棋盘：还有空位就买一张最便宜的新棋子（只挑 IsOwned = false，买新牌才多一个身体）。
            if (freeSlots > 0)
            {
                int buyIndex = FindCheapestNewUnit(shop, state.Gold);
                if (buyIndex >= 0)
                {
                    TryFindFreeCell(board, out int gridX, out int gridY);
                    return new AutoChessDecision(
                        AutoChessAiAction.Buy, shop[buyIndex].UnitId, gridX, gridY, ReasonFillBoard);
                }
            }

            // ④ 升人口：经济达标、棋盘已满、等级未封顶时，把金币换成上场位。
            if (freeSlots == 0 && state.Level < MaxLevel && state.Gold >= _economyFloor)
            {
                return new AutoChessDecision(AutoChessAiAction.LevelUp, -1, -1, -1, ReasonLevelUp);
            }

            // ⑤ 攒钱：低血搜牌是唯一会打断攒钱的紧急需求（见类型注释）。
            bool lowHp = state.Hp <= LowHpThreshold && state.Gold >= LowHpRerollGold;
            if (state.Gold < _economyFloor && !lowHp)
            {
                return new AutoChessDecision(AutoChessAiAction.Wait, -1, -1, -1, ReasonEconomyHold);
            }

            // ⑥ 低血搜牌：血量见底时不要再攒，花 20 金以上找翻盘点。
            if (lowHp)
            {
                return new AutoChessDecision(AutoChessAiAction.Reroll, -1, -1, -1, ReasonReroll);
            }

            // ⑦ 上场：把"已拥有但棋盘上查不到"的棋子放到空位（不花钱，只是把战力摆出来）。
            if (freeSlots > 0)
            {
                int placeIndex = FindOwnedBenchUnit(board, shop);
                if (placeIndex >= 0)
                {
                    TryFindFreeCell(board, out int gridX, out int gridY);
                    return new AutoChessDecision(
                        AutoChessAiAction.Buy, shop[placeIndex].UnitId, gridX, gridY, ReasonPlaceUnit);
                }
            }

            // ⑧ 无事可做。
            return new AutoChessDecision(AutoChessAiAction.Wait, -1, -1, -1, ReasonNoop);
        }

        /// <summary>
        /// 复位诊断计数。<see cref="Decide"/> 本身没有跨决策状态，因此复位<b>不会改变</b>后续决策结果；
        /// 它只用于"新一局开始"时把 <see cref="DecisionsMade"/> 清零。
        /// </summary>
        public void Reset()
        {
            _decisionsMade = 0;
        }

        /// <summary>坐标两项都非负才算"在棋盘上"；任一为 <c>-1</c> 都按备战席处理。</summary>
        private static bool IsDeployed(in AutoChessBoardUnit unit)
        {
            return unit.GridX >= 0 && unit.GridY >= 0;
        }

        /// <summary>棋盘上是否存在该 unitId 的<b>已上场</b>实体（备战席条目不算）。</summary>
        private static bool IsDeployedUnit(AutoChessBoardUnit[] board, int unitId)
        {
            for (int i = 0; i < board.Length; i++)
            {
                if (IsDeployed(in board[i]) && board[i].UnitId == unitId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>该格是否被棋盘上任何一个已上场单位占着。</summary>
        private static bool IsOccupied(AutoChessBoardUnit[] board, int gridX, int gridY)
        {
            for (int i = 0; i < board.Length; i++)
            {
                if (IsDeployed(in board[i]) && board[i].GridX == gridX && board[i].GridY == gridY)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 找建议落点：己方半场 8 列 × 4 行，从前往后、从左到右取第一个空格。
        /// 32 格全满（理论上只有人口上限 &gt; 32 才可能）时返回 <c>(-1, -1)</c>。
        /// </summary>
        private static void TryFindFreeCell(AutoChessBoardUnit[] board, out int gridX, out int gridY)
        {
            for (int row = 0; row < PlacementRows; row++)
            {
                for (int column = 0; column < PlacementColumns; column++)
                {
                    if (!IsOccupied(board, column, row))
                    {
                        gridX = column;
                        gridY = row;
                        return;
                    }
                }
            }

            gridX = -1;
            gridY = -1;
        }

        /// <summary>差一张即可升星、且金币买得起（价格未知的负数不算）。</summary>
        private static bool IsUpgradeCopy(in AutoChessShopSlot slot, int gold)
        {
            return slot.CopiesNeededToUpgrade == 1 && slot.Cost >= 0 && slot.Cost <= gold;
        }

        /// <summary>可买的新棋子（未拥有、价格已知且买得起）。</summary>
        private static bool IsNewUnit(in AutoChessShopSlot slot, int gold)
        {
            return !slot.IsOwned && slot.Cost >= 0 && slot.Cost <= gold;
        }

        /// <summary>
        /// 选凑星目标：先取买得起的<b>最贵</b>那张（升星价值随费用走），同价时
        /// 有随机源就随机挑、没有就取 unitId 最小的。返回商店下标，找不到返回 <c>-1</c>。
        /// </summary>
        private int FindUpgradeCopy(AutoChessShopSlot[] shop, int gold)
        {
            int bestCost = -1;
            int tied = 0;
            for (int i = 0; i < shop.Length; i++)
            {
                if (!IsUpgradeCopy(in shop[i], gold))
                {
                    continue;
                }

                int cost = shop[i].Cost;
                if (cost > bestCost)
                {
                    bestCost = cost;
                    tied = 1;
                }
                else if (cost == bestCost)
                {
                    tied++;
                }
            }

            if (tied == 0)
            {
                return -1;
            }

            return PickSlot(shop, bestCost, tied, true, gold);
        }

        /// <summary>
        /// 选补人口目标：买得起的<b>最便宜</b>的新棋子，同价时随机/最小 unitId。
        /// 返回商店下标，找不到返回 <c>-1</c>。
        /// </summary>
        private int FindCheapestNewUnit(AutoChessShopSlot[] shop, int gold)
        {
            int bestCost = int.MaxValue;
            int tied = 0;
            for (int i = 0; i < shop.Length; i++)
            {
                if (!IsNewUnit(in shop[i], gold))
                {
                    continue;
                }

                int cost = shop[i].Cost;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    tied = 1;
                }
                else if (cost == bestCost)
                {
                    tied++;
                }
            }

            if (tied == 0)
            {
                return -1;
            }

            return PickSlot(shop, bestCost, tied, false, gold);
        }

        /// <summary>
        /// 在"同价候选"里挑一个：没有随机源时取 unitId 最小的；有随机源时按数组顺序取随机第 k 个。
        /// <para>
        /// <b>候选只有一个时不消耗随机数</b>：随机流只由"真的需要挑一个"的决策推进，
        /// 这样商店里只有一张买得起的牌时，后续决策序列不会因为商店大小而漂移。
        /// </para>
        /// <para>
        /// <b>为什么随机按数组顺序而不是按 unitId 排序</b>：商店槽位的顺序由调用方给定且同一状态下稳定，
        /// 按它取随机序号既省一次排序（零分配），又保证"同状态 + 同随机流"必然同结果。
        /// </para>
        /// </summary>
        /// <param name="shop">商店数组。</param>
        /// <param name="cost">已确定的同价档位。</param>
        /// <param name="tied">该档位下的候选数量（&gt; 0）。</param>
        /// <param name="upgradeCopy"><c>true</c> 按凑星条件过滤，<c>false</c> 按补人口条件过滤。</param>
        /// <param name="gold">当前金币（与 <paramref name="upgradeCopy"/> 一起决定候选集）。</param>
        private int PickSlot(AutoChessShopSlot[] shop, int cost, int tied, bool upgradeCopy, int gold)
        {
            if (_random == null || tied == 1)
            {
                // 唯一候选也走这条：不消耗随机数，随机流只由"真正需要挑一个"的决策推进。
                int bestIndex = -1;
                int bestUnitId = int.MaxValue;
                for (int i = 0; i < shop.Length; i++)
                {
                    if (!Matches(shop[i], cost, upgradeCopy, gold))
                    {
                        continue;
                    }

                    if (shop[i].UnitId < bestUnitId)
                    {
                        bestUnitId = shop[i].UnitId;
                        bestIndex = i;
                    }
                }

                return bestIndex;
            }

            int pick = _random.NextInt(0, tied);
            for (int i = 0; i < shop.Length; i++)
            {
                if (!Matches(shop[i], cost, upgradeCopy, gold))
                {
                    continue;
                }

                if (pick == 0)
                {
                    return i;
                }

                pick--;
            }

            // 理论上不可达（tied 已统计过候选数）；返回 -1 让调用方安全降级。
            return -1;
        }

        /// <summary>候选是否属于给定档位（凑星 / 补人口两套条件之一）。</summary>
        private static bool Matches(AutoChessShopSlot slot, int cost, bool upgradeCopy, int gold)
        {
            if (slot.Cost != cost)
            {
                return false;
            }

            return upgradeCopy ? IsUpgradeCopy(in slot, gold) : IsNewUnit(in slot, gold);
        }

        /// <summary>
        /// 选"该卖谁"：优先卖非前排，再卖星级低的，再卖便宜的，最后按 unitId 小的。
        /// 返回棋盘下标，没有已上场单位时返回 <c>-1</c>。
        /// </summary>
        private static int FindSellCandidate(AutoChessBoardUnit[] board)
        {
            int bestIndex = -1;
            for (int i = 0; i < board.Length; i++)
            {
                if (!IsDeployed(in board[i]))
                {
                    continue;
                }

                if (bestIndex < 0 || CompareSellPriority(in board[i], in board[bestIndex]) < 0)
                {
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>卖出优先级：返回值 &lt; 0 表示 <paramref name="left"/> 更该卖。</summary>
        private static int CompareSellPriority(in AutoChessBoardUnit left, in AutoChessBoardUnit right)
        {
            if (left.IsFrontline != right.IsFrontline)
            {
                return left.IsFrontline ? 1 : -1;
            }

            if (left.Star != right.Star)
            {
                return left.Star < right.Star ? -1 : 1;
            }

            if (left.Cost != right.Cost)
            {
                return left.Cost < right.Cost ? -1 : 1;
            }

            if (left.UnitId != right.UnitId)
            {
                return left.UnitId < right.UnitId ? -1 : 1;
            }

            return 0;
        }

        /// <summary>
        /// 选要上场的备战席棋子：已拥有、棋盘上查不到同 ID，取 unitId 最小的一个。
        /// 返回商店下标，找不到返回 <c>-1</c>。
        /// </summary>
        private static int FindOwnedBenchUnit(AutoChessBoardUnit[] board, AutoChessShopSlot[] shop)
        {
            int bestIndex = -1;
            int bestUnitId = int.MaxValue;
            for (int i = 0; i < shop.Length; i++)
            {
                if (!shop[i].IsOwned)
                {
                    continue;
                }

                int unitId = shop[i].UnitId;
                if (unitId >= bestUnitId || IsDeployedUnit(board, unitId))
                {
                    continue;
                }

                bestUnitId = unitId;
                bestIndex = i;
            }

            return bestIndex;
        }
    }
}
