#nullable enable
using System;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Contracts.Meta
{
    /// <summary>
    /// 防守方快照：离线对手的"阵容 + 随机种子"，进攻方离线就能算分。
    /// <para>
    /// 它是异步 PVP 唯一的对手数据通道：本机拿到这份值就能完整复算一场对局，
    /// 因此既不需要服务端在线，也不需要实时内核参与。
    /// </para>
    /// </summary>
    public readonly struct DefenseSnapshot
    {
        public DefenseSnapshot(int playerId, int power, int randomSeed, int layoutId)
        {
            PlayerId = playerId;
            Power = power;
            RandomSeed = randomSeed;
            LayoutId = layoutId;
        }

        /// <summary>防守方玩家 ID（结算结果里的对手身份）。</summary>
        public int PlayerId { get; }

        /// <summary>防守阵容总战力；参与得分计算，&lt; 0 视为非法快照。</summary>
        public int Power { get; }

        /// <summary>
        /// 对局随机种子（由服务端在生成防守录像时固定）。
        /// 双方得分都用它偏移，保证同一快照在任何机器上复算结果一致。
        /// </summary>
        public int RandomSeed { get; }

        /// <summary>防守阵容布局 ID，Phase 1 只做展示，不参与算分。</summary>
        public int LayoutId { get; }
    }

    /// <summary>一次异步挑战的结果：纯数据，可进因果日志、可回放。</summary>
    public readonly struct AsyncDuelResult
    {
        public AsyncDuelResult(
            int attackerId,
            int defenderId,
            int attackerScore,
            int defenderScore,
            bool attackerWon,
            int ratingDelta)
        {
            AttackerId = attackerId;
            DefenderId = defenderId;
            AttackerScore = attackerScore;
            DefenderScore = defenderScore;
            AttackerWon = attackerWon;
            RatingDelta = ratingDelta;
        }

        /// <summary>发起挑战的玩家 ID。</summary>
        public int AttackerId { get; }

        /// <summary>被挑战的离线防守方玩家 ID。</summary>
        public int DefenderId { get; }

        /// <summary>进攻方得分。</summary>
        public int AttackerScore { get; }

        /// <summary>防守方（由快照复算）得分。</summary>
        public int DefenderScore { get; }

        /// <summary>是否进攻方获胜。</summary>
        public bool AttackerWon { get; }

        /// <summary>
        /// 本场段位实际变化量（已含连胜/连败加成与 0 分下限修正）；
        /// 触底时可能小于基础扣分（如 -10 而不是 -15）。
        /// </summary>
        public int RatingDelta { get; }
    }

    /// <summary>
    /// 发起一场异步挑战：拿一份离线防守快照 + 自己的战力即可结算，不需要对手在线。
    /// </summary>
    public readonly struct AsyncDuelCommand : ICommand
    {
        public AsyncDuelCommand(DefenseSnapshot defender, int attackerPower)
        {
            Defender = defender;
            AttackerPower = attackerPower;
        }

        /// <summary>被挑战的离线防守方快照。</summary>
        public DefenseSnapshot Defender { get; }

        /// <summary>进攻方当前战力，合法区间 0..1000，越界静默拒绝。</summary>
        public int AttackerPower { get; }

        /// <summary>便捷只读视图：等价于 <c>Defender.Power</c>，供结算代码避免重复展开快照。</summary>
        public int DefenderPower
        {
            get { return Defender.Power; }
        }
    }

    /// <summary>
    /// 一场异步挑战已结算的事实。只有通过输入校验、段位/连胜/发奖全部落地后才广播；
    /// 被静默拒绝的请求不发任何事件。
    /// </summary>
    public readonly struct AsyncDuelResolvedEvent : IEvent
    {
        public AsyncDuelResolvedEvent(in AsyncDuelResult result)
        {
            Result = result;
        }

        /// <summary>本场结算结果（含双方得分、胜负与段位实际变化）。</summary>
        public AsyncDuelResult Result { get; }
    }

    /// <summary>段位档案查询请求体：空结构体，只查调用方自己的档案。</summary>
    public readonly struct PvpProfileQueryRequest : IQuery<PvpProfileSnapshot>
    {
    }

    /// <summary>段位档案只读快照：UI 与红点只依赖这份值，不接触切片内部状态。</summary>
    public sealed class PvpProfileSnapshot
    {
        public PvpProfileSnapshot(int playerId, int rating, int wins, int losses, int streak)
        {
            PlayerId = playerId;
            Rating = rating;
            Wins = wins;
            Losses = losses;
            Streak = streak;
        }

        /// <summary>档案归属玩家。</summary>
        public int PlayerId { get; }

        /// <summary>当前段位分（下限 0）。</summary>
        public int Rating { get; }

        /// <summary>累计胜场。</summary>
        public int Wins { get; }

        /// <summary>累计负场。</summary>
        public int Losses { get; }

        /// <summary>当前连胜/连败：正数为连胜、负数为连败、0 表示未打过。</summary>
        public int Streak { get; }
    }

    /// <summary>对手池查询：返回当前位置可挑战的 3 个离线对手。</summary>
    public readonly struct PvpOpponentsQueryRequest : IQuery<PvpOpponentList>
    {
    }

    /// <summary>
    /// 对手池快照。构造时复制入参数组，查询方改自己的数组不会污染切片内部状态。
    /// </summary>
    public sealed class PvpOpponentList
    {
        public PvpOpponentList(DefenseSnapshot[] opponents)
        {
            DefenseSnapshot[] source = opponents ?? Array.Empty<DefenseSnapshot>();
            DefenseSnapshot[] copy = new DefenseSnapshot[source.Length];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = source[i];
            }

            Opponents = copy;
        }

        /// <summary>可挑战的离线对手，Phase 1 固定 3 个。</summary>
        public DefenseSnapshot[] Opponents { get; }
    }
}
