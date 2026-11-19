#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.AutoChess
{
    /// <summary>
    /// 自走棋沙盒（<see cref="GameplayKernelType.AutoChess"/>）：Kernel.Turn 的第六套内容，
    /// 验证"确定性模拟 + 阵容构筑"能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么在 <see cref="GameplayKernelType"/> 里有专属值</b>：装配层只认识枚举、不认识内核具体类型，
    /// 必须靠它把会话派给 <see cref="AutoChessWorld"/> 并在结算时区分卡带；内核本体仍然只依赖
    /// <see cref="IGameplaySession"/> 那一组进出门契约，多一个枚举值不会让内核多知道任何东西。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：棋盘 8×8，玩家半场 <c>y = 0..3</c>、敌方半场 <c>y = 4..7</c>；
    /// 玩家最多 3 个单位（unitId 1..3），开局全部在备战席；敌方每回合固定 3 个单位（unitId 11..13），
    /// 生成在敌方半场的头三个空格 <c>(0,4)(1,4)(2,4)</c>。
    /// </para>
    /// <para>
    /// <b>回合流程</b>：布阵（<see cref="TryPlace"/> / <see cref="TryMoveUnit"/> / <see cref="TrySell"/>）
    /// → <see cref="StartBattle"/> 同步推演 30 个 tick 并立即结算基地血量 → <see cref="EndRound"/> 清场、
    /// 回合序号 +1、敌方重新生成。一赢一负各自扣掉对手 10 点基地血量，<b>基地血量归零在对局结束时收口</b>。
    /// </para>
    /// <para>
    /// <b>阵亡与清场</b>：战斗中被击杀的单位只清占位、保留槽位与阵亡坐标，直到 <see cref="EndRound"/>
    /// 才真正出局（玩家单位永久失去，敌方每回合满血重建）。因此一轮之内 <c>GetUnit</c> 的 index 稳定，
    /// 但 <see cref="PlayerUnitCount"/> / <see cref="EnemyUnitCount"/> 报的是<b>槽位数</b>而非存活数，
    /// 存活数要逐槽看 <see cref="ChessUnitView.Alive"/>。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：战斗由 <see cref="StartBattle"/> 同步推完，<see cref="Step(float)"/> 不参与推演，
    /// 只累加 <see cref="ProcessedSteps"/>（<c>deltaSeconds</c> 被忽略）；对局结束或结算已取回后为空操作。
    /// </para>
    /// <para>
    /// <b>零分配</b>：单位槽位数组与占位表在构造期一次分配；<see cref="TryPlace"/>、<see cref="TryMoveUnit"/>、
    /// <see cref="TrySell"/>、<see cref="StartBattle"/>、<see cref="EndRound"/>、<see cref="Step(float)"/> 均不申请
    /// 托管堆内存；只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组（<see cref="GameplayResult"/> 契约要求一次构造）。
    /// </para>
    /// </summary>
    public sealed class AutoChessWorld : IGameplaySession, IAutoChessReadView, IAutoChessInput
    {
        /// <summary>战场（槽位 + 占位表，构造期一次分配，运行期只改字段）。</summary>
        private readonly AutoChessBoard _board;

        /// <summary>走位随机流：由 <see cref="GameplayInitData.RandomSeed"/> 派生，跨回合连续消耗。</summary>
        private AutoChessRandom _random;

        private int _roundIndex;

        private int _playerHp;

        private int _enemyHp;

        private int _wins;

        private int _losses;

        private int _processedSteps;

        private bool _battleResolved;

        private bool _finished;

        private bool _victory;

        private bool _resultCollected;

        public AutoChessWorld(in GameplayInitData init)
        {
            int playerCount = init.UnitCount;
            if (playerCount < 1)
            {
                playerCount = 1;
            }
            else if (playerCount > AutoChessRules.MaxPlayerUnits)
            {
                playerCount = AutoChessRules.MaxPlayerUnits;
            }

            _board = new AutoChessBoard(playerCount);
            _random = new AutoChessRandom(init.RandomSeed);
            _roundIndex = 0;
            _playerHp = AutoChessRules.InitialPlayerHp;
            _enemyHp = AutoChessRules.InitialEnemyHp;
            _wins = 0;
            _losses = 0;
            _processedSteps = 0;
            _battleResolved = false;
            _finished = false;
            _victory = false;
            _resultCollected = false;
        }

        /// <summary>棋盘列数（固定 8）。</summary>
        public int Width
        {
            get { return _board.Width; }
        }

        /// <summary>棋盘行数（固定 8；<c>y = 0..3</c> 是玩家半场）。</summary>
        public int Height
        {
            get { return _board.Height; }
        }

        /// <summary>当前回合序号（从 0 开始，每次 <see cref="EndRound"/> +1）。</summary>
        public int RoundIndex
        {
            get { return _roundIndex; }
        }

        /// <summary>玩家基地血量（初值 100；每输一轮 -10，允许为负）。</summary>
        public int PlayerHp
        {
            get { return _playerHp; }
        }

        /// <summary>敌方基地血量（初值 100；每赢一轮 -10，允许为负）。</summary>
        public int EnemyHp
        {
            get { return _enemyHp; }
        }

        /// <summary>玩家阵营槽位数（含本轮阵亡待清场的尸体；出售会立即减少）。</summary>
        public int PlayerUnitCount
        {
            get { return _board.CountOwner(AutoChessRules.PlayerOwner); }
        }

        /// <summary>敌方阵营槽位数（每回合固定 3 个；阵亡尸体保留到 <see cref="EndRound"/>）。</summary>
        public int EnemyUnitCount
        {
            get { return _board.CountOwner(AutoChessRules.EnemyOwner); }
        }

        /// <summary>本轮胜场数（每次玩家赢下战斗 +1）。</summary>
        public int Wins
        {
            get { return _wins; }
        }

        /// <summary>本轮败场数（每次玩家输掉战斗 +1）。</summary>
        public int Losses
        {
            get { return _losses; }
        }

        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>对局是否结束：基地血量归零已被 <see cref="EndRound"/> 收口，或结算报告已取回。</summary>
        public bool IsFinished
        {
            get { return _resultCollected || _finished; }
        }

        /// <summary>本轮战斗是否已结算（<see cref="StartBattle"/> 返回后恒为 true，直到 <see cref="EndRound"/> 复位）。</summary>
        public bool IsBattleResolved
        {
            get { return _battleResolved; }
        }

        /// <summary>
        /// 推演一步：<c>deltaSeconds</c> 被忽略（战斗由 <see cref="StartBattle"/> 同步推完），每调用一次计一步。
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
        /// 取第 <paramref name="index"/> 个槽位的单位快照；越界抛 <see cref="ArgumentOutOfRangeException"/>。
        /// 一轮战斗期间 index 稳定（阵亡尸体保留槽位），出售会让后续 index 前移。
        /// </summary>
        public ChessUnitView GetUnit(int index)
        {
            if (index < 0 || index >= _board.SlotCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "单位 index 越界。");
            }

            return _board.GetUnit(index).ToView();
        }

        /// <summary>取 (x, y) 上已上场单位的槽位 index；空格、备战席与越界返回 <c>-1</c>。</summary>
        public int UnitAt(int x, int y)
        {
            return _board.UnitAt(x, y);
        }

        /// <summary>
        /// 把备战席单位放到己方半场第一个空格（扫描顺序 <c>y</c> 0→3、每行 <c>x</c> 0→7）。
        /// 单位不存在 / 非己方 / 已在场 / 已阵亡 / 无空格 / 对局已结束返回 false，且不改任何状态。
        /// <para>
        /// 契约没有"开战后禁止布阵"这一条，因此本轮结算后到 <see cref="EndRound"/> 之前仍可调用；
        /// 但 <see cref="EndRound"/> 会把所有存活单位收回备战席，残留布阵不会带进下一轮，也就不构成优势。
        /// </para>
        /// </summary>
        public bool TryPlace(int unitId)
        {
            if (IsFinished)
            {
                return false;
            }

            return _board.TryPlace(unitId);
        }

        /// <summary>
        /// 把己方在场单位挪到 (x, y)：要求目标格在己方半场（<c>y = 0..3</c>）且为空。
        /// 越界、跨半场、被占、敌方单位、未放置（在备战席）一律返回 false 且不改坐标。
        /// </summary>
        public bool TryMoveUnit(int unitId, int x, int y)
        {
            if (IsFinished)
            {
                return false;
            }

            return _board.TryMoveUnit(unitId, x, y);
        }

        /// <summary>出售己方单位：占位立即释放、槽位压缩。单位不存在 / 非己方 / 已阵亡返回 false。</summary>
        public bool TrySell(int unitId)
        {
            if (IsFinished)
            {
                return false;
            }

            return _board.TrySell(unitId);
        }

        /// <summary>
        /// 开始自动战斗：同步推演 30 个 tick，返回时 <see cref="IsBattleResolved"/> 已为 true。
        /// 结果立即反映到 <see cref="Wins"/> / <see cref="Losses"/> 与双方基地血量（胜方扣对手 10 点）。
        /// 本轮已结算或对局已结束时为空操作。
        /// </summary>
        public void StartBattle()
        {
            if (IsFinished || _battleResolved)
            {
                return;
            }

            AutoChessBattle.Resolve(_board, ref _random);

            int winnerOwner = AutoChessBattle.ResolveWinnerOwner(_board);
            if (winnerOwner == AutoChessRules.PlayerOwner)
            {
                _wins++;
                _enemyHp -= AutoChessRules.BaseHpDamagePerRound;
            }
            else
            {
                _losses++;
                _playerHp -= AutoChessRules.BaseHpDamagePerRound;
            }

            _battleResolved = true;
        }

        /// <summary>
        /// 结束本轮：清场（阵亡单位出局、存活玩家单位治疗满血回备战席）、<see cref="RoundIndex"/> +1、
        /// 重新生成敌方 3 单位；随后按基地血量收口对局结束（玩家血归零 = 失败，敌方血归零 = 胜利）。
        /// 未结算过战斗或对局已结束时为空操作。
        /// </summary>
        public void EndRound()
        {
            if (IsFinished || !_battleResolved)
            {
                return;
            }

            _board.ResetForNextRound();
            _roundIndex++;
            _battleResolved = false;

            if (_playerHp <= 0)
            {
                _finished = true;
                _victory = false;
                return;
            }

            if (_enemyHp <= 0)
            {
                _finished = true;
                _victory = true;
            }
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 胜利战利品为 <c>new LootItem(1003, 1)</c> + <c>new LootItem(1001, 20)</c>，失败为空数组；
        /// <see cref="GameplayResult.RemainingUnits"/> 取玩家剩余槽位数。
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

            IReadOnlyList<LootItem> loots = _victory
                ? new LootItem[]
                {
                    new LootItem(AutoChessRules.VictoryLootItemId, AutoChessRules.VictoryLootCount),
                    new LootItem(AutoChessRules.VictoryLootItemIdSecond, AutoChessRules.VictoryLootCountSecond)
                }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.AutoChess,
                _victory,
                _processedSteps,
                _board.CountOwner(AutoChessRules.PlayerOwner),
                Array.Empty<GameplayTag>(),
                loots);
        }
    }
}
