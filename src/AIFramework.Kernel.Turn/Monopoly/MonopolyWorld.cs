#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Monopoly
{
    /// <summary>
    /// 大富翁沙盒（<see cref="GameplayKernelType.Turn"/>）：Kernel.Turn 的又一套内容，
    /// 补 T2-8 未做的"<b>骰子 + 事件格 + 资产</b>"分支，验证"资产经营 + 随机事件 + 淘汰赛"
    /// 能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么不新增 <c>GameplayKernelType</c> 枚举值</b>：对主板而言本卡带只是"又一个回合制会话"——
    /// 进门拿 <see cref="GameplayInitData"/>、出门交 <see cref="GameplayResult"/>，
    /// <c>CollectResult</c> 固定报 <see cref="GameplayKernelType.Turn"/>。新增枚举值会强逼装配层与
    /// 存档协议同步扩容（与黑白棋/战棋/肉鸽/跑酷同一条理由）。
    /// </para>
    /// <para>
    /// <b>棋盘</b>：20 格环形，索引 0..19（0 = 起点 GO）。固定布局：0 GO、4 Jail、8 Chance、12 Tax、
    /// 16 Bonus，其余 15 格为地产（价格按索引顺序 200/220/…/480，见 <see cref="MonopolyBoard"/>）。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证；数值集中在 <see cref="MonopolyRules"/>）</b>：
    /// <list type="bullet">
    /// <item>玩家数 = <c>GameplayInitData.UnitCount</c> 夹取：≤ 0 → 默认 2、1..4 原样、&gt; 4 → 4；
    /// 每人起始现金 1500、位置 0、互不持有地产；玩家 0 = 本地玩家，其余为 AI 对手（本卡带不做内置 AI，
    /// 每个存活玩家都由外部按 <see cref="TryRoll"/> 驱动）。</item>
    /// <item>每个 <see cref="TryRoll"/> 掷 1 个 d6（种子随机），按点数前进；经过或停在起点发 200。
    /// <b>不做双数再掷</b>：一次掷骰即一个完整回合（简化，见下）。</item>
    /// <item>无主地产：现金 ≥ 地价则<b>自动买入</b>（不交互，直接买）；现金不足则跳过并计入
    /// <see cref="SkippedPurchaseCount"/>。</item>
    /// <item>他人地产：付租金 = <c>地价 × (10% + 15% × 等级)</c> 向下取整，转给所有者；
    /// 付不起则<b>破产</b>（现金归 0、全部地产归还无主、等级复位 0），剩余玩家继续。</item>
    /// <item>自己地产：等级 0→1→2→3 升级，费用 = <c>地价 × 50%</c>；满级或现金不足则跳过（不计数）。</item>
    /// <item>Tax 付 100、Bonus 得 150；Chance 用种子随机 4 选 1（+100 / +200 / -100 / 前进 3 格，
    /// 前进后对新落点照常结算）；Jail 直接跳到起点且<b>不发起点奖励</b>（简化，注释见
    /// <see cref="MonopolyRules.JailTileIndex"/>）。</item>
    /// <item>回合结束即 <see cref="TurnIndex"/> +1，回合交给下一名<b>存活</b>玩家（破产位自动跳过）。
    /// 名义回合数 <see cref="RoundNumber"/> = <c>TurnIndex / PlayerCount + 1</c>。</item>
    /// <item><b>结束条件</b>：只剩 1 名存活玩家（胜者为该幸存者），或名义回合数到达
    /// <see cref="MonopolyRules.MaxRounds"/>（= <c>TurnIndex</c> 达到 <c>40 × PlayerCount</c>，
    /// 现金最高者胜、平局取玩家索引小者）。单人局（PlayerCount = 1）不会因"只剩 1 人"立即结束，
    /// 只会在 40 回合封顶收口。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>刻意简化（与需求原文一致或为补全歧义，测试逐条钉住）</b>：
    /// <list type="number">
    /// <item>单骰、无"掷到双数再掷"；没有"入狱停一回合"——踩 Jail 只是回起点，本回合正常结束。</item>
    /// <item>只有"付不起租金"会破产；缴税/机会卡损失现金不足时只扣到 0，不出局。</item>
    /// <item>破产者欠的租金<b>不转给</b>地主（现金直接归 0），避免出现"负资产转移"这类 Phase 1 不需要的口径。</item>
    /// <item>破产归还的地产等级复位 0，新买家不继承旧升级（"归还无主"的自然解释）。</item>
    /// <item>起点奖励只在掷骰移动（含机会卡前进 3 格）越环时发一次；Jail 传送不发。</item>
    /// <item>名义回合数的分母始终是配置玩家数：中途有人破产后 <see cref="RoundNumber"/> 只是
    /// "按开局人数换算的进度读数"，不重新压缩（契约公式保持稳定，表现层可自行裁剪）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>确定性</b>：所有随机只走内核内的 xorshift32（<see cref="MonopolyRandom"/>，由
    /// <see cref="GameplayInitData.RandomSeed"/> 播种）；构造期不抽随机，每次 TryRoll 先抽骰子、
    /// 落在机会格才再抽卡。因此"同种子 + 同 TryRoll 序列 ⇒ 棋盘、现金、事件序列完全一致"，
    /// 测试用双 world 对拍逐字段验证。
    /// </para>
    /// <para>
    /// <b>Step / 结算语义</b>：回合制不按时间推进逻辑，<see cref="Step(float)"/> 只累加
    /// <see cref="ProcessedSteps"/>（<c>deltaSeconds</c> 被忽略）；对局结束（含结算已取回）后为空操作。
    /// <see cref="CollectResult"/> 只在结束后可调用一次：返回固定 <see cref="GameplayKernelType.Turn"/>，
    /// <see cref="GameplayResult.StepCount"/> = <see cref="TurnIndex"/>（需求锁定的"回合数"口径），
    /// <see cref="GameplayResult.RemainingUnits"/> = 玩家 0 现金（需求锁定为 score 口径），
    /// 玩家 0 胜则发 <c>LootItem(1003, 1)</c>、否则空数组。
    /// </para>
    /// <para>
    /// <b>零分配</b>：棋盘数组、现金/位置/破产数组都在构造期一次分配；<see cref="TryRoll"/> /
    /// <see cref="Step(float)"/> 全程只做整数运算与定长数组就地改写，不申请托管堆内存；
    /// 只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组（出门 DTO 只允许构造一次）。
    /// </para>
    /// <para>
    /// <b>文件拆分</b>：本文件只放状态、构造与回合/淘汰流程；格子结算（买地/租金/升级/机会卡）在
    /// <c>MonopolyWorld.Resolve.cs</c>；只读视图与两个取数入口在 <c>MonopolyWorld.Views.cs</c>；
    /// 会话进出门（Step/CollectResult/ProcessedSteps）在 <c>MonopolyWorld.Session.cs</c>。
    /// 四者是同一个 <c>partial</c> 类型，对外只有一个 <c>MonopolyWorld</c>。
    /// </para>
    /// </summary>
    public sealed partial class MonopolyWorld : IGameplaySession, IMonopolyReadView, IMonopolyInput
    {
        /// <summary>固定棋盘（种类/价格/所有者/等级，构造期一次分配）。</summary>
        private readonly MonopolyBoard _board;

        /// <summary>配置玩家数（1..4，见 <see cref="MonopolyRules.ClampPlayerCount"/>），局中不变。</summary>
        private readonly int _playerCount;

        /// <summary>各玩家现金（长度 = <see cref="_playerCount"/>，下标即玩家索引）。</summary>
        private readonly int[] _cash;

        /// <summary>各玩家所在格（0..19）。</summary>
        private readonly int[] _positions;

        /// <summary>各玩家是否已破产（破产位仍占数组下标，只是不再轮转）。</summary>
        private readonly bool[] _bankrupt;

        /// <summary>确定性随机流：先抽骰子、再按需抽机会卡。</summary>
        private MonopolyRandom _random;

        /// <summary>已执行的掷骰回合数（结算报告的 StepCount 口径）。</summary>
        private int _turnIndex;

        /// <summary>下一个该掷骰的存活玩家索引。</summary>
        private int _currentPlayer;

        /// <summary>存活玩家数（有人破产即减 1；用于"只剩 1 人"收口）。</summary>
        private int _aliveCount;

        /// <summary>最近一次骰子点数（1..6；0 = 尚未掷骰）。</summary>
        private int _lastDice;

        /// <summary>最近一次行动的玩家索引（-1 = 尚未掷骰）。</summary>
        private int _lastMovedPlayer;

        /// <summary>最近一次行动结算完成后的最终落点（-1 = 尚未掷骰）。</summary>
        private int _lastMovedTo;

        /// <summary>累计抽到的机会卡张数（会话级诊断量）。</summary>
        private int _chanceEvents;

        /// <summary>累计"无主地产但现金不足"的跳过次数（会话级诊断量）。</summary>
        private int _skippedPurchaseCount;

        /// <summary>胜者索引；-1 = 未结束。</summary>
        private int _winnerPlayer;

        /// <summary><see cref="Step(float)"/> 的有效调用次数（会话级诊断量）。</summary>
        private int _processedSteps;

        /// <summary>对局是否已收口（破产淘汰或回合封顶）。</summary>
        private bool _finished;

        /// <summary>结算报告是否已取回（取回后会话所有权交还，所有输入失效）。</summary>
        private bool _resultCollected;

        /// <summary>
        /// 构造一局大富翁：玩家数由 <paramref name="init"/> 的 <see cref="GameplayInitData.UnitCount"/> 夹取，
        /// 每人 1500 现金、位置 0，随机流由 <see cref="GameplayInitData.RandomSeed"/> 播种。
        /// </summary>
        public MonopolyWorld(in GameplayInitData init)
        {
            _playerCount = MonopolyRules.ClampPlayerCount(init.UnitCount);
            _board = new MonopolyBoard();
            _cash = new int[_playerCount];
            _positions = new int[_playerCount];
            _bankrupt = new bool[_playerCount];
            _random = new MonopolyRandom(init.RandomSeed);

            for (int index = 0; index < _playerCount; index++)
            {
                _cash[index] = MonopolyRules.StartingCash;
                _positions[index] = MonopolyRules.GoTileIndex;
                _bankrupt[index] = false;
            }

            _turnIndex = 0;
            _currentPlayer = 0;
            _aliveCount = _playerCount;
            _lastDice = 0;
            _lastMovedPlayer = -1;
            _lastMovedTo = -1;
            _chanceEvents = 0;
            _skippedPurchaseCount = 0;
            _winnerPlayer = -1;
            _processedSteps = 0;
            _finished = false;
            _resultCollected = false;
        }

        /// <summary>
        /// 当前玩家掷一次 1d6 并<b>同步结算</b>整个回合：移动 → 结算落点 → <see cref="TurnIndex"/> +1 →
        /// 回合交给下一名存活玩家 → 收口判定。
        /// <para>
        /// 返回 false 的唯一条件是对局已结束（含结算报告已取回）；否则必然执行一个完整回合并返回 true。
        /// 破产玩家的回合由内核自动跳过，调用方不需要自己判存活。
        /// </para>
        /// </summary>
        public bool TryRoll()
        {
            if (IsFinished)
            {
                return false;
            }

            int player = EnsureAliveCurrent();
            _lastDice = _random.NextIndex(MonopolyRules.DiceSides) + 1;
            _lastMovedPlayer = player;

            int landedTile = MoveForward(player, _lastDice);
            ResolveTile(player, landedTile, 0);

            _turnIndex++;
            _lastMovedTo = _positions[player];
            _currentPlayer = NextAlive(player);
            CheckEnd();
            return true;
        }

        /// <summary>
        /// 防御性兜底：当前玩家指针若指向破产位（正常流程不会发生），先推进到下一名存活玩家。
        /// </summary>
        private int EnsureAliveCurrent()
        {
            if (!_bankrupt[_currentPlayer])
            {
                return _currentPlayer;
            }

            _currentPlayer = NextAlive(_currentPlayer);
            return _currentPlayer;
        }

        /// <summary>
        /// 前进 <paramref name="steps"/> 格：越环（<c>from + steps ≥ 20</c>）时按环取模并发 200 起点奖励。
        /// 返回落点索引；本方法只负责"移动 + 起点奖励"，格子效果由 <see cref="ResolveTile"/> 处理。
        /// </summary>
        private int MoveForward(int player, int steps)
        {
            int raw = _positions[player] + steps;
            if (raw >= MonopolyRules.TileCount)
            {
                raw %= MonopolyRules.TileCount;
                _cash[player] += MonopolyRules.GoReward;
            }

            _positions[player] = raw;
            return raw;
        }

        /// <summary>
        /// 从 <paramref name="from"/> 之后按环形顺序找下一名存活玩家；找不到（理论不可达）时返回自身。
        /// </summary>
        private int NextAlive(int from)
        {
            for (int step = 1; step <= _playerCount; step++)
            {
                int candidate = (from + step) % _playerCount;
                if (!_bankrupt[candidate])
                {
                    return candidate;
                }
            }

            return from;
        }

        /// <summary>
        /// 破产收口：现金归 0、标记出局、全部地产归还无主（等级复位）。调用方负责后续收口判定。
        /// </summary>
        private void Bankrupt(int player)
        {
            _cash[player] = 0;
            _bankrupt[player] = true;
            _aliveCount--;
            _board.ReleaseAll(player);
        }

        /// <summary>
        /// 回合末收口判定：优先"只剩 1 名存活玩家"（幸存者胜）；其次名义回合数达到
        /// <see cref="MonopolyRules.MaxRounds"/>（<c>TurnIndex ≥ 40 × PlayerCount</c>，现金最高者胜）。
        /// 单人局跳过第一条，只可能在回合封顶收口。
        /// </summary>
        private void CheckEnd()
        {
            if (_playerCount > 1 && _aliveCount <= 1)
            {
                FinishWithSoleSurvivor();
                return;
            }

            if (_turnIndex >= MonopolyRules.MaxRounds * _playerCount)
            {
                FinishByCash();
            }
        }

        /// <summary>只剩 1 人：幸存者直接获胜。理论不可达（全员破产）时保守退回现金最高者收口。</summary>
        private void FinishWithSoleSurvivor()
        {
            for (int index = 0; index < _playerCount; index++)
            {
                if (!_bankrupt[index])
                {
                    _winnerPlayer = index;
                    _finished = true;
                    return;
                }
            }

            FinishByCash();
        }

        /// <summary>
        /// 回合封顶按现金结算：存活玩家中现金最高者胜；相等时取玩家索引小者（严格大于才替换）。
        /// 破产玩家不参与比较（现金恒为 0，不能靠"大家都 0"捡漏）。
        /// </summary>
        private void FinishByCash()
        {
            int best = -1;
            int bestCash = int.MinValue;

            for (int index = 0; index < _playerCount; index++)
            {
                if (_bankrupt[index])
                {
                    continue;
                }

                if (_cash[index] > bestCash)
                {
                    bestCash = _cash[index];
                    best = index;
                }
            }

            _winnerPlayer = best;
            _finished = true;
        }
    }
}
