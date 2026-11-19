#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Tactics
{
    /// <summary>
    /// 战棋沙盒（<see cref="GameplayKernelType.Turn"/>）：Kernel.Turn 的第四套内容，
    /// 验证"网格 + 移动范围 + 攻击仲裁"能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么不新增 <c>GameplayKernelType</c> 枚举值</b>：战棋复用 Turn 卡带——它对主板而言只是
    /// "另一个回合制会话"，胜负、战利品与流程完全一致；新增枚举值会强逼装配层与存档协议同步扩容。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：棋盘 8×8；玩家单位数 = <c>init.UnitCount</c> 夹在 1..3，放在
    /// <c>(0..2, 0)</c>，敌方固定 3 个放在 <c>(0..2, 7)</c>；玩家 HP 20 / 攻 6 / 移 4 / 攻程 1，敌方 HP 15 / 攻 5 / 移 3 / 攻程 1。
    /// <see cref="TryMove"/> 只收当前阵营单位，且要求目标格界内、为空、曼哈顿距离不超移动力、四邻路径不被占格堵死；
    /// <see cref="TryAttack"/> 要求互为敌对阵营且在攻程内，扣攻方攻击力，HP &lt;= 0 立即移除（后续 index 前移）；
    /// <see cref="EndTurn"/> = 切敌方 → AI 同步行动（攻程内就打，否则向最近玩家走一格）→ 切回玩家 → 回合 +1 并清空选中；
    /// 任一方全灭即 <see cref="IsFinished"/>，回合序号超过 50 判平局。
    /// </para>
    /// <para>
    /// <b>平局口径</b>：回合上限触发时 <see cref="VictoryTeam"/> 置为 1（敌方），
    /// 但 <see cref="GameplayResult.IsVictory"/> 仍为 false、不发战利品——"占优"与"胜利"必须分开，
    /// 否则平局会被主板当成通关发奖。注释即契约：这是刻意行为，不是笔误。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：没有待结算动画队列，<see cref="Step(float)"/> 只累加 <see cref="ProcessedSteps"/>
    /// （<c>deltaSeconds</c> 被忽略），对局结束或结算已取回后为空操作。<b>零分配</b>：棋盘、占位表、BFS 缓冲与
    /// 敌方行动顺序缓冲都在构造期一次分配，<see cref="Step(float)"/> / <see cref="TryMove"/> / <see cref="TryAttack"/> /
    /// <see cref="EndTurn"/> 不申请托管堆内存；只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组。
    /// </para>
    /// </summary>
    public sealed class TacticsWorld : IGameplaySession, ITacticsReadView, ITacticsInput
    {
        private readonly TacticsBoard _board;

        /// <summary>敌方一轮行动的 UnitId 顺序缓冲（构造期分配，<c>EndTurn</c> 内复用）。</summary>
        private readonly int[] _enemyTurnOrder;

        private int _turnIndex;

        private int _currentTeam;

        private int _processedSteps;

        private int _selectedUnitId;

        private int _victoryTeam;

        private bool _finished;

        private bool _resultCollected;

        public TacticsWorld(in GameplayInitData init)
        {
            _board = new TacticsBoard(TacticsRules.BoardWidth, TacticsRules.BoardHeight, TacticsRules.MaxUnits);
            _enemyTurnOrder = new int[TacticsRules.EnemyUnitCount];

            int playerCount = init.UnitCount;
            if (playerCount < 1)
            {
                playerCount = 1;
            }
            else if (playerCount > TacticsRules.MaxPlayerUnits)
            {
                playerCount = TacticsRules.MaxPlayerUnits;
            }

            for (int i = 0; i < playerCount; i++)
            {
                _board.TryAdd(
                    TacticsRules.PlayerUnitIdBase + i,
                    TacticsRules.PlayerTeam,
                    i,
                    0,
                    TacticsRules.PlayerHp,
                    TacticsRules.PlayerAttack,
                    TacticsRules.PlayerMoveRange,
                    TacticsRules.PlayerAttackRange);
            }

            for (int j = 0; j < TacticsRules.EnemyUnitCount; j++)
            {
                _board.TryAdd(
                    TacticsRules.EnemyUnitIdBase + j,
                    TacticsRules.EnemyTeam,
                    j,
                    TacticsRules.BoardHeight - 1,
                    TacticsRules.EnemyHp,
                    TacticsRules.EnemyAttack,
                    TacticsRules.EnemyMoveRange,
                    TacticsRules.EnemyAttackRange);
            }

            _turnIndex = 1;
            _currentTeam = TacticsRules.PlayerTeam;
            _processedSteps = 0;
            _selectedUnitId = -1;
            _victoryTeam = -1;
            _finished = false;
            _resultCollected = false;
        }

        /// <summary>棋盘列数。</summary>
        public int Width
        {
            get { return _board.Width; }
        }

        /// <summary>棋盘行数。</summary>
        public int Height
        {
            get { return _board.Height; }
        }

        /// <summary>当前回合序号（从 1 开始，每次 <see cref="EndTurn"/> +1）。</summary>
        public int TurnIndex
        {
            get { return _turnIndex; }
        }

        /// <summary>当前行动阵营：0 = 玩家，1 = 敌方（<see cref="EndTurn"/> 返回后恒为 0）。</summary>
        public int CurrentTeam
        {
            get { return _currentTeam; }
        }

        /// <summary>存活单位总数（双方合计）。</summary>
        public int UnitCount
        {
            get { return _board.UnitCount; }
        }

        /// <summary>当前选中的单位 ID；无选中为 <c>-1</c>。<see cref="EndTurn"/> 会清空它。</summary>
        public int SelectedUnitId
        {
            get { return _selectedUnitId; }
        }

        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>胜方阵营：<c>-1</c> 未分胜负，0 玩家胜，1 敌方胜（含回合上限平局）。</summary>
        public int VictoryTeam
        {
            get { return _victoryTeam; }
        }

        /// <summary>对局是否结束：任一方全灭、回合数超过上限，或结算报告已被取回。</summary>
        public bool IsFinished
        {
            get { return _resultCollected || _finished; }
        }

        /// <summary>
        /// 推演一步：<c>deltaSeconds</c> 被忽略（回合制不按时间推进），每调用一次计一步。
        /// 已结束（含结算已取回）时为空操作，不累加。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            _processedSteps++;
        }

        /// <summary>
        /// 选中己方存活单位；传当前已选中的 ID 表示取消选中（返回 true）。
        /// 找不到 / 非当前阵营 / 已结束返回 false，且不改变已有选中。
        /// </summary>
        public bool TrySelect(int unitId)
        {
            if (IsFinished)
            {
                return false;
            }

            if (!_board.TryFindIndex(unitId, out int index))
            {
                return false;
            }

            if (_board.GetUnit(index).Team != _currentTeam)
            {
                return false;
            }

            _selectedUnitId = _selectedUnitId == unitId ? -1 : unitId;
            return true;
        }

        /// <summary>
        /// 移动到 (x, y)：只接受当前阵营（玩家）单位，且目标格在界内、为空、曼哈顿距离在移动力内、
        /// 存在一条四邻不被占用的路径。任一条件不满足返回 false 且不改任何状态。
        /// <para>移动不要求事先 <see cref="TrySelect"/>，也不改变选中（选中只是 UI 提示）。</para>
        /// </summary>
        public bool TryMove(int unitId, int x, int y)
        {
            if (IsFinished || _currentTeam != TacticsRules.PlayerTeam)
            {
                return false;
            }

            if (!_board.TryFindIndex(unitId, out int index))
            {
                return false;
            }

            TacticsUnit unit = _board.GetUnit(index);
            if (unit.Team != _currentTeam)
            {
                return false;
            }

            if (!_board.InBounds(x, y) || _board.UnitAt(x, y) >= 0)
            {
                return false;
            }

            if (TacticsBoard.Manhattan(unit.X, unit.Y, x, y) > unit.MoveRange)
            {
                return false;
            }

            if (!_board.IsReachable(unit.X, unit.Y, x, y, unit.MoveRange))
            {
                return false;
            }

            return _board.MoveUnit(index, x, y);
        }

        /// <summary>
        /// 用 <paramref name="attackerUnitId"/> 攻击 <paramref name="targetUnitId"/>：要求攻方是当前阵营、双方存活、
        /// 互为敌对阵营、曼哈顿距离不超过攻方攻击距离。命中扣攻方攻击力，目标 HP &lt;= 0 立即移除。
        /// </summary>
        public bool TryAttack(int attackerUnitId, int targetUnitId)
        {
            if (IsFinished || _currentTeam != TacticsRules.PlayerTeam)
            {
                return false;
            }

            if (attackerUnitId == targetUnitId)
            {
                return false;
            }

            if (!_board.TryFindIndex(attackerUnitId, out int attackerIndex))
            {
                return false;
            }

            if (!_board.TryFindIndex(targetUnitId, out int targetIndex))
            {
                return false;
            }

            TacticsUnit attacker = _board.GetUnit(attackerIndex);
            TacticsUnit target = _board.GetUnit(targetIndex);

            if (attacker.Team != _currentTeam || target.Team == attacker.Team)
            {
                return false;
            }

            if (TacticsBoard.Manhattan(attacker.X, attacker.Y, target.X, target.Y) > attacker.AttackRange)
            {
                return false;
            }

            DealDamage(targetIndex, attacker.Attack);
            RefreshOutcome();
            return true;
        }

        /// <summary>
        /// 玩家结束回合：清空选中 → 切到敌方 → 敌方 AI 同步行动 → 切回玩家 → 回合序号 +1。
        /// 序号超过 <see cref="TacticsRules.MaxTurns"/> 时判平局（<see cref="VictoryTeam"/> 记 1，但不发战利品）。
        /// 对局已结束时为空操作。
        /// </summary>
        public void EndTurn()
        {
            if (IsFinished)
            {
                return;
            }

            _selectedUnitId = -1;
            _currentTeam = TacticsRules.EnemyTeam;

            TacticsAi.RunEnemyTurn(_board, _enemyTurnOrder);
            RefreshOutcome();

            _currentTeam = TacticsRules.PlayerTeam;
            _turnIndex++;

            if (!_finished && _turnIndex > TacticsRules.MaxTurns)
            {
                // 回合上限：按"平局记敌方占优"处理，IsVictory 仍为 false。
                _finished = true;
                _victoryTeam = TacticsRules.EnemyTeam;
            }
        }

        /// <summary>取第 <paramref name="index"/> 个存活单位的只读快照；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public TacticsUnitView GetUnit(int index)
        {
            if (index < 0 || index >= _board.UnitCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "单位 index 越界。");
            }

            return _board.GetUnit(index).ToView();
        }

        /// <summary>取 (x, y) 上的单位 index；空格或越界返回 <c>-1</c>。</summary>
        public int UnitAt(int x, int y)
        {
            return _board.UnitAt(x, y);
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 胜利战利品为 <c>new LootItem(1003, 1)</c>，失败/平局为空数组；
        /// <see cref="GameplayResult.RemainingUnits"/> 取玩家存活单位数。
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_finished)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;
            bool isVictory = _victoryTeam == TacticsRules.PlayerTeam;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(TacticsRules.VictoryLootItemId, TacticsRules.VictoryLootCount) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _processedSteps,
                _board.CountTeam(TacticsRules.PlayerTeam),
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>扣血并同步选中：选中的单位被打死时立刻取消选中，避免 UI 拿着已阵亡的 ID。</summary>
        private void DealDamage(int index, int damage)
        {
            int unitId = _board.GetUnit(index).UnitId;
            bool removed = _board.ApplyDamage(index, damage);

            if (removed && unitId == _selectedUnitId)
            {
                _selectedUnitId = -1;
            }
        }

        /// <summary>按存活数刷新胜负：敌方全灭玩家胜，玩家全灭敌方胜（先判胜，避免同回合互灭时判反）。</summary>
        private void RefreshOutcome()
        {
            if (_finished)
            {
                return;
            }

            if (_board.CountTeam(TacticsRules.EnemyTeam) == 0)
            {
                _finished = true;
                _victoryTeam = TacticsRules.PlayerTeam;
                return;
            }

            if (_board.CountTeam(TacticsRules.PlayerTeam) == 0)
            {
                _finished = true;
                _victoryTeam = TacticsRules.EnemyTeam;
            }
        }
    }
}
