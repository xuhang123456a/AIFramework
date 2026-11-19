#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Contracts.Meta
{
    /// <summary>
    /// 挂机 RPG 关卡配置行（来自本切片子表 <c>TbIdleStage</c>）。
    /// 只读值类型：表加载后即冻结，运行时状态（当前关、剩余血量）另存于处理器。
    /// </summary>
    public readonly struct IdleStageDef
    {
        public IdleStageDef(int stageId, int enemyHp, int enemyAttack, int goldReward, int requiredPower)
        {
            StageId = stageId;
            EnemyHp = enemyHp;
            EnemyAttack = enemyAttack;
            GoldReward = goldReward;
            RequiredPower = requiredPower;
        }

        /// <summary>关卡序号，子表内从 1 起连续且唯一（空表时没有该行）。</summary>
        public int StageId { get; }

        /// <summary>敌人满血值，必须 &gt; 0；同时充当战斗中的血量上限。</summary>
        public int EnemyHp { get; }

        /// <summary>敌人每 3 个战斗 tick 的反击伤害，必须 &gt;= 0；Phase 1 不扣英雄血，只累计受击量。</summary>
        public int EnemyAttack { get; }

        /// <summary>通关发放的金币数量，必须 &gt;= 0。</summary>
        public int GoldReward { get; }

        /// <summary>推荐战力（Phase 1 只做数值校验与展示，不参与战斗判定），必须 &gt;= 0。</summary>
        public int RequiredPower { get; }
    }

    /// <summary>
    /// 英雄只读状态：等级、经验、由等级推导的战力与每秒金币、历史最高通关关卡。
    /// 每次查询都按当前等级重算 <see cref="Power"/> 与 <see cref="GoldPerSecond"/>，不存在滞后缓存。
    /// </summary>
    public readonly struct IdleHeroState
    {
        public IdleHeroState(int level, int exp, int power, int goldPerSecond, int highestStage)
        {
            Level = level;
            Exp = exp;
            Power = power;
            GoldPerSecond = goldPerSecond;
            HighestStage = highestStage;
        }

        /// <summary>当前等级，从 1 起。</summary>
        public int Level { get; }

        /// <summary>当前经验；升级消耗 = 升级前等级 × 100，因此永不为负。</summary>
        public int Exp { get; }

        /// <summary>当前战力 = 10 + (level - 1) × 6。</summary>
        public int Power { get; }

        /// <summary>每秒金币 = 英雄等级。</summary>
        public int GoldPerSecond { get; }

        /// <summary>历史最高通关关卡（初始 1，通关时取最大值）。</summary>
        public int HighestStage { get; }
    }

    /// <summary>
    /// 挂机 RPG 只读快照：UI 与红点只依赖这份值，不接触切片内部状态。
    /// <see cref="EnemyHp"/> 是当前战斗的剩余血量，<see cref="EnemyMaxHp"/> 来自子表。
    /// </summary>
    public sealed class IdleRpgSnapshot
    {
        public IdleRpgSnapshot(IdleHeroState hero, int currentStage, bool inBattle, int enemyHp, int enemyMaxHp, int battleTick)
        {
            Hero = hero;
            CurrentStage = currentStage;
            InBattle = inBattle;
            EnemyHp = enemyHp;
            EnemyMaxHp = enemyMaxHp;
            BattleTick = battleTick;
        }

        /// <summary>英雄状态。</summary>
        public IdleHeroState Hero { get; }

        /// <summary>当前所在关卡序号（初始 1；空表时仍为 1 但没有战斗）。</summary>
        public int CurrentStage { get; }

        /// <summary>当前是否有战斗在打：最后一关通关后为 <c>false</c>，空表恒为 <c>false</c>。</summary>
        public bool InBattle { get; }

        /// <summary>当前战斗剩余血量；无战斗时为 0。</summary>
        public int EnemyHp { get; }

        /// <summary>当前关卡敌人满血值（来自子表）；关卡不在表里时为 0。</summary>
        public int EnemyMaxHp { get; }

        /// <summary>当前这场战斗已执行的 tick 数：进入关卡时归零，每 tick +1。</summary>
        public int BattleTick { get; }
    }

    /// <summary>
    /// 推进挂机战斗：一次最多结算 10 个 tick。
    /// 构造期把 <see cref="Ticks"/> 夹到 <c>1..10</c>，因此消息本身永远是合法值；
    /// 处理器仍会再夹一次，用于兜底 <c>default(IdleBattleTickCommand)</c>（绕过构造函数的 0）。
    /// </summary>
    public readonly struct IdleBattleTickCommand : ICommand
    {
        /// <summary>合法 tick 数下限。</summary>
        public const int MinTicks = 1;

        /// <summary>合法 tick 数上限（单次命令的结算上限，防止一次命令跑穿整张表）。</summary>
        public const int MaxTicks = 10;

        public IdleBattleTickCommand(int ticks)
        {
            Ticks = ticks < MinTicks ? MinTicks : (ticks > MaxTicks ? MaxTicks : ticks);
        }

        /// <summary>本次要结算的 tick 数，恒在 <c>1..10</c>。</summary>
        public int Ticks { get; }
    }

    /// <summary>手动进入下一关：仅当当前关已通关、或本次会话首关尚未开打时生效，否则静默拒绝。</summary>
    public readonly struct AdvanceStageCommand : ICommand
    {
    }

    /// <summary>用经验升一级：经验足够则升级并扣除，否则静默拒绝（一次命令最多升 1 级）。</summary>
    public readonly struct LevelUpHeroCommand : ICommand
    {
    }

    /// <summary>挂机 RPG 快照查询请求体：空结构体，只查调用方自己的状态。</summary>
    public readonly struct IdleRpgSnapshotQueryRequest : IQuery<IdleRpgSnapshot>
    {
    }
}
