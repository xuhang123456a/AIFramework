#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;

namespace AIFramework.Features.AsyncPvp
{
    /// <summary>
    /// 异步 PVP（T2-14）：挑战离线对手快照 → 确定性算分 → 结算段位/连胜 → 发奖并广播事实。
    /// <para>
    /// <b>它不驱动实时战斗</b>：本切片只读 <see cref="DefenseSnapshot"/> 里的战力与随机种子，
    /// 用纯算术复算一场"录像结果"，因此没有网络、没有定时器、也没有 <c>System.Random</c>。
    /// 实时内核怎么打，本切片完全不知道；两者之间只通过 Contracts 里的快照与结果交接。
    /// </para>
    /// <para>
    /// <b>为什么所有拒绝都是静默的</b>：异步挑战由防守快照驱动，快照可能来自旧缓存或第三方下发的脏数据。
    /// 把"快照非法"报成事件会迫使 UI 为每种脏数据写提示，而这些提示没有一条对玩家有意义；
    /// 直接丢弃这一场、状态与因果日志都不动，是 Phase 1 的既定口径（与商店/塔防一致）。
    /// </para>
    /// </summary>
    public sealed class AsyncPvpHandler : ICommandHandler<AsyncDuelCommand>,
                                          IQueryHandler<PvpProfileQueryRequest, PvpProfileSnapshot>,
                                          IQueryHandler<PvpOpponentsQueryRequest, PvpOpponentList>
    {
        /// <summary>胜利发金币的原因标签（因果日志用；6801 属于异步 PVP 段）。</summary>
        private const int VictoryReasonTagId = 6801;

        /// <summary>连胜里程碑发补签卡的原因标签。</summary>
        private const int StreakMilestoneReasonTagId = 6802;

        /// <summary>每场胜利发放的金币数量。</summary>
        private const int VictoryGoldCount = 20;

        /// <summary>每达到一次连胜里程碑额外发放的补签卡数量。</summary>
        private const int StreakMilestoneCardCount = 1;

        /// <summary>连胜 / 连败的里程碑长度（每 3 连胜发一次补签卡）。</summary>
        private const int StreakMilestoneLength = 3;

        /// <summary>单场得分上限，防止高战力直接碾压数值面板。</summary>
        private const int ScoreCap = 9999;

        /// <summary>进攻方战力合法上限；超过即视为脏请求。</summary>
        private const int MaxAttackerPower = 1000;

        /// <summary>无连胜时的胜场段位分。</summary>
        private const int BaseWinRating = 20;

        /// <summary>连胜达到里程碑后的胜场段位分。</summary>
        private const int StreakWinRating = 30;

        /// <summary>无连败时的败场段位分（负数）。</summary>
        private const int BaseLossRating = -15;

        /// <summary>连败达到里程碑后的败场段位分（负数）。</summary>
        private const int StreakLossRating = -20;

        /// <summary>对手池固定人数。</summary>
        private const int OpponentCount = 3;

        /// <summary>对手玩家 ID 基数。</summary>
        private const int OpponentPlayerIdBase = 100;

        /// <summary>对手玩家 ID 的循环范围。</summary>
        private const int OpponentPlayerIdRange = 3;

        /// <summary>对手战力基数。</summary>
        private const int OpponentPowerBase = 50;

        /// <summary>每个槽位的战力步长。</summary>
        private const int OpponentPowerStep = 7;

        /// <summary>对手战力循环范围。</summary>
        private const int OpponentPowerRange = 50;

        /// <summary>对手种子基数（固定日期常量，不是读系统时间）。</summary>
        private const int OpponentSeedBase = 20261120;

        /// <summary>每个槽位的种子步长。</summary>
        private const int OpponentSeedStep = 13;

        /// <summary>对手布局 ID 的循环范围。</summary>
        private const int OpponentLayoutRange = 4;

        /// <summary>总线注入引用；只用于发奖与广播，绝不直接改背包。</summary>
        private readonly ICqeBus _bus;

        /// <summary>档案归属玩家 ID（构造期固定，之后不变）。</summary>
        private readonly int _playerId;

        private int _rating;
        private int _wins;
        private int _losses;
        private int _streak;
        private int _duelCount;
        private AsyncDuelResult _lastResult;

        /// <summary>注入总线并建立初始档案。</summary>
        /// <param name="bus">CQE 总线（发奖、广播事件）。</param>
        /// <param name="playerId">档案归属玩家 ID。</param>
        /// <param name="initialRating">初始段位分；负数按 0 处理（段位下限 0）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public AsyncPvpHandler(ICqeBus bus, int playerId = 1, int initialRating = 1000)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            _bus = bus;
            _playerId = playerId;
            _rating = initialRating > 0 ? initialRating : 0;
            _wins = 0;
            _losses = 0;
            _streak = 0;
            _duelCount = 0;
            _lastResult = default;
        }

        /// <summary>当前段位分（下限 0）。</summary>
        public int Rating
        {
            get { return _rating; }
        }

        /// <summary>累计胜场。</summary>
        public int Wins
        {
            get { return _wins; }
        }

        /// <summary>累计负场。</summary>
        public int Losses
        {
            get { return _losses; }
        }

        /// <summary>当前连胜/连败：正数=连胜、负数=连败、0=未打过。</summary>
        public int Streak
        {
            get { return _streak; }
        }

        /// <summary>最近一场结算结果；未打过时为 <c>default</c>。</summary>
        public AsyncDuelResult LastResult
        {
            get { return _lastResult; }
        }

        /// <summary>已成功结算的场次（被静默拒绝的请求不计）。</summary>
        public int DuelCount
        {
            get { return _duelCount; }
        }

        /// <summary>
        /// 注册 1 个命令处理器 + 2 个查询处理器。<b>构造里不自动注册</b>：
        /// 装配层的注册顺序就是因果日志的顺序，不应被构造函数偷偷改掉。
        /// </summary>
        /// <param name="bus">目标总线。</param>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 为 <c>null</c>。</exception>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<AsyncDuelCommand>(this);
            bus.RegisterQueryHandler<PvpProfileQueryRequest, PvpProfileSnapshot>(this);
            bus.RegisterQueryHandler<PvpOpponentsQueryRequest, PvpOpponentList>(this);
        }

        /// <summary>
        /// 结算一场异步挑战：非法输入静默拒绝；合法输入按纯算术算分，
        /// 依次落地段位/连胜/胜负计数，再发奖、广播事实并推进场次。
        /// </summary>
        /// <param name="command">挑战意图（防守快照 + 进攻战力）。</param>
        public void Handle(in AsyncDuelCommand command)
        {
            DefenseSnapshot defender = command.Defender;
            if (command.AttackerPower < 0
                || command.AttackerPower > MaxAttackerPower
                || defender.Power < 0)
            {
                // 非法输入：静默拒绝。段位、连胜、场次、事件全部不动。
                return;
            }

            int attackerScore = ComputeScore(command.AttackerPower, defender.RandomSeed);
            int defenderScore = ComputeScore(defender.Power, defender.RandomSeed);
            bool attackerWon = ResolveWinner(
                attackerScore,
                defenderScore,
                command.AttackerPower,
                defender.Power);

            // 先更新连胜再定段位分：第 3 连胜这一场就吃 +30 加成，与"每 3 连胜"的里程碑口径对齐。
            int streakAfter = attackerWon ? Math.Max(1, _streak + 1) : Math.Min(-1, _streak - 1);
            int ratingDelta = ResolveRatingDelta(attackerWon, streakAfter);

            int ratingAfter = _rating + ratingDelta;
            if (ratingAfter < 0)
            {
                ratingAfter = 0;
            }

            // RatingDelta 必须报"实际变化"：触底时 -15 只能记 -10，
            // 否则客户端按事件里的 delta 回放会显示凭空多掉的段位。
            int actualDelta = ratingAfter - _rating;

            _rating = ratingAfter;
            _streak = streakAfter;
            if (attackerWon)
            {
                _wins++;
            }
            else
            {
                _losses++;
            }

            AsyncDuelResult result = new AsyncDuelResult(
                _playerId,
                defender.PlayerId,
                attackerScore,
                defenderScore,
                attackerWon,
                actualDelta);

            // 顺序契约：先发奖、再广播事实。订阅方收到事件时，奖励指令已按因果顺序进了总线。
            if (attackerWon)
            {
                _bus.Send(new GrantItemCommand(
                    MetaItemIds.Gold,
                    VictoryGoldCount,
                    VictoryReasonTagId));

                if (streakAfter % StreakMilestoneLength == 0)
                {
                    // 每 3 连胜（3、6、9…）额外补 1 张补签卡，作为连胜里程碑奖励。
                    _bus.Send(new GrantItemCommand(
                        MetaItemIds.RetroactiveCard,
                        StreakMilestoneCardCount,
                        StreakMilestoneReasonTagId));
                }
            }

            AsyncDuelResolvedEvent resolved = new AsyncDuelResolvedEvent(in result);
            _bus.Publish(resolved);

            _duelCount++;
            _lastResult = result;
        }

        /// <summary>段位档案查询：每次返回新建快照，调用方改不到内部状态。</summary>
        /// <param name="query">空查询体。</param>
        /// <returns>当前档案快照。</returns>
        public PvpProfileSnapshot Handle(in PvpProfileQueryRequest query)
        {
            return new PvpProfileSnapshot(_playerId, _rating, _wins, _losses, _streak);
        }

        /// <summary>
        /// 对手池查询：固定返回 3 个离线对手。
        /// <para>
        /// 真实环境由服务端下发（按隐藏分匹配真人防守录像），这里只按 <see cref="DuelCount"/> 的
        /// 纯算术生成确定性对手池，保证同一进度下离线可复现、单测无需联网。
        /// </para>
        /// </summary>
        /// <param name="query">空查询体。</param>
        /// <returns>3 个对手的新快照。</returns>
        public PvpOpponentList Handle(in PvpOpponentsQueryRequest query)
        {
            DefenseSnapshot[] opponents = new DefenseSnapshot[OpponentCount];
            for (int i = 0; i < opponents.Length; i++)
            {
                // 槽位随场次推进：同一 DuelCount 必得同一批对手，换进度就换一批。
                int slot = _duelCount + i;
                opponents[i] = new DefenseSnapshot(
                    OpponentPlayerIdBase + (slot % OpponentPlayerIdRange),
                    OpponentPowerBase + ((slot * OpponentPowerStep) % OpponentPowerRange),
                    OpponentSeedBase + (slot * OpponentSeedStep),
                    slot % OpponentLayoutRange);
            }

            return new PvpOpponentList(opponents);
        }

        /// <summary>
        /// 确定性得分：<c>Min(9999, power * 10 + seed % 100)</c>。
        /// 中间量用 <c>long</c> 只为防快照战力过大时 int 溢出，合法区间结果与公式完全一致。
        /// </summary>
        private static int ComputeScore(int power, int seed)
        {
            long raw = ((long)power * 10L) + (seed % 100);
            if (raw > ScoreCap)
            {
                return ScoreCap;
            }

            return (int)raw;
        }

        /// <summary>
        /// 胜负判定：分高者胜；平分按战力高者胜；分数与战力全相同则进攻方胜。
        /// <para>
        /// 平局口径说明：异步挑战是进攻方主动发起的单局结算，若平局判负，
        /// 镜像阵容下挑战者永远拿不到胜利反馈；因此沿用"平局进攻方略优"。
        /// </para>
        /// </summary>
        private static bool ResolveWinner(
            int attackerScore,
            int defenderScore,
            int attackerPower,
            int defenderPower)
        {
            if (attackerScore != defenderScore)
            {
                return attackerScore > defenderScore;
            }

            if (attackerPower != defenderPower)
            {
                return attackerPower > defenderPower;
            }

            return true;
        }

        /// <summary>按"结算后连胜/连败"决定段位分：达到里程碑吃加成，否则吃基础值。</summary>
        private static int ResolveRatingDelta(bool attackerWon, int streakAfter)
        {
            if (attackerWon)
            {
                return streakAfter >= StreakMilestoneLength ? StreakWinRating : BaseWinRating;
            }

            return streakAfter <= -StreakMilestoneLength ? StreakLossRating : BaseLossRating;
        }
    }
}
