#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.IdleRpg
{
    /// <summary>
    /// 挂机 RPG 玩法内核（T2-12）：自动战斗结算、关卡推进、英雄等级/战力成长。
    /// <para>
    /// <b>与放置经营（<c>Feature.IdleTycoon</c>）的分界</b>：那个切片按时间累计金币产出，
    /// 本切片按"战斗 tick"结算胜负——英雄每 tick 打敌人 <c>power</c> 点，敌人每 3 tick 反击一次
    /// （<b>不扣英雄血</b>，只累计 <see cref="DamageTaken"/>），敌人血量归零即通关发奖并自动进入下一关。
    /// 因此本切片<b>不读时间</b>，只认 <see cref="IdleBattleTickCommand"/> 带来的确定性推进。
    /// </para>
    /// <para>
    /// 金币只走总线（<see cref="GrantItemCommand"/>），处理器内部不缓存余额；
    /// 所有拒绝都是静默的（状态不变、不发指令、不发事件），UI 只能通过快照观察结果。
    /// </para>
    /// </summary>
    public sealed class IdleRpgHandler : ICommandHandler<IdleBattleTickCommand>,
                                         ICommandHandler<AdvanceStageCommand>,
                                         ICommandHandler<LevelUpHeroCommand>,
                                         IQueryHandler<IdleRpgSnapshotQueryRequest, IdleRpgSnapshot>
    {
        /// <summary>通关发金币的原因标签（因果日志用；6911 属于挂机 RPG 段）。</summary>
        private const int GoldGrantReasonTagId = 6911;

        /// <summary>1 级战力基数。</summary>
        private const int BasePower = 10;

        /// <summary>每升 1 级增加的战力。</summary>
        private const int PowerPerLevel = 6;

        /// <summary>通关经验系数：第 N 关通关得 <c>10 × N</c> 点经验。</summary>
        private const int ExpPerStage = 10;

        /// <summary>升级经验系数：从 L 级升到 L+1 级需要 <c>L × 100</c> 点经验。</summary>
        private const int ExpCostPerLevel = 100;

        /// <summary>敌人反击间隔：每 3 个战斗 tick 打一次。</summary>
        private const int CounterAttackIntervalTicks = 3;

        /// <summary>初始关卡序号。</summary>
        private const int InitialStageId = 1;

        /// <summary>最低等级：构造参数 <c>initialHeroLevel</c> 小于此值时按 1 处理。</summary>
        private const int MinHeroLevel = 1;

        private readonly ICqeBus _bus;
        private readonly IdleRpgTables _tables;

        private int _heroLevel;
        private int _heroExp;
        private int _highestStage;
        private int _currentStage;
        private bool _inBattle;
        private int _enemyHp;
        private int _battleTick;

        /// <summary>当前关是否已通关但尚未离开（正常推进时由自动进关立即消费，仅最后一关会停留为 <c>true</c>）。</summary>
        private bool _stageCleared;

        /// <summary>本次会话是否已经执行过至少一个战斗 tick：用于界定"首次进入"的跳关窗口。</summary>
        private bool _hasFoughtTick;

        private int _stageClearCount;
        private int _damageTaken;

        /// <summary>
        /// 构造即进入第 1 关并开始自动战斗（挂机玩法不等待玩家点"开始"）；
        /// 空关卡表则进入"无战斗"退化态，所有命令静默无效。
        /// </summary>
        /// <param name="bus">CQE 总线，用于发金币指令。</param>
        /// <param name="loader">二进制表加载器，只用本切片子表 <c>TbIdleStage</c>。</param>
        /// <param name="initialHeroLevel">初始英雄等级，小于 1 时按 1 处理。</param>
        public IdleRpgHandler(ICqeBus bus, IBinaryTableLoader loader, int initialHeroLevel = 1)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            _bus = bus;
            _tables = new IdleRpgTables(loader);

            _heroLevel = initialHeroLevel < MinHeroLevel ? MinHeroLevel : initialHeroLevel;
            _heroExp = 0;
            _highestStage = InitialStageId;
            _currentStage = InitialStageId;
            _stageClearCount = 0;
            _damageTaken = 0;
            _hasFoughtTick = false;
            _stageCleared = false;

            IdleStageDef firstStage;
            if (_tables.TryFind(InitialStageId, out firstStage))
            {
                EnterStage(firstStage);
            }
            else
            {
                // 空表合法：没有关卡可打，快照如实报告"不在战斗中"。
                _inBattle = false;
                _enemyHp = 0;
                _battleTick = 0;
            }
        }

        /// <summary>当前关卡序号。</summary>
        public int CurrentStage
        {
            get { return _currentStage; }
        }

        /// <summary>英雄当前等级。</summary>
        public int HeroLevel
        {
            get { return _heroLevel; }
        }

        /// <summary>英雄当前战力 = 10 + (level - 1) × 6，每次访问实时计算。</summary>
        public int HeroPower
        {
            get { return ComputePower(); }
        }

        /// <summary>英雄每秒金币 = 英雄等级（Phase 1 只做数值展示，本切片不结算时间收益）。</summary>
        public int GoldPerSecond
        {
            get { return _heroLevel; }
        }

        /// <summary>当前这场战斗已执行的 tick 数：进入关卡时归零。</summary>
        public int BattleTickCount
        {
            get { return _battleTick; }
        }

        /// <summary>累计通关次数（每次敌人血量归零 +1，含重复刷同一关）。</summary>
        public int StageClearCount
        {
            get { return _stageClearCount; }
        }

        /// <summary>累计承受的反击伤害（只累计，不扣血；每次反击加当前关的 <c>enemyAttack</c>）。</summary>
        public int DamageTaken
        {
            get { return _damageTaken; }
        }

        /// <summary>注册三条命令与一条查询处理器（构造里不自动注册，装配层负责调用）。</summary>
        public void Register(ICqeBus bus)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            bus.RegisterCommandHandler<IdleBattleTickCommand>(this);
            bus.RegisterCommandHandler<AdvanceStageCommand>(this);
            bus.RegisterCommandHandler<LevelUpHeroCommand>(this);
            bus.RegisterQueryHandler<IdleRpgSnapshotQueryRequest, IdleRpgSnapshot>(this);
        }

        /// <summary>
        /// 推进战斗：<c>ticks</c> 再夹一次到 <c>1..10</c>（兜底绕过构造函数的 <c>default</c> 结构体），
        /// 逐 tick 结算；一旦战斗结束（最后一关通关）就立即停止，剩余 tick 不再生效。
        /// </summary>
        public void Handle(in IdleBattleTickCommand command)
        {
            int ticks = ClampTicks(command.Ticks);
            for (int i = 0; i < ticks; i++)
            {
                if (!_inBattle)
                {
                    return;
                }

                ExecuteTick();
            }
        }

        /// <summary>
        /// 手动进入下一关：当前关已通关，或本次会话首关尚未执行任何 tick（"首次进入"）时生效；
        /// 目标关卡不在表里则静默拒绝（状态不变）。
        /// </summary>
        public void Handle(in AdvanceStageCommand command)
        {
            if (!_stageCleared && _hasFoughtTick)
            {
                // 正在打且没打赢：不允许越过当前关。
                return;
            }

            IdleStageDef next;
            if (!_tables.TryFind(_currentStage + 1, out next))
            {
                // 目标关卡不存在（已到最后一关 / 空表）：静默拒绝。
                return;
            }

            EnterStage(next);
        }

        /// <summary>
        /// 升一级：经验 &gt;= 当前等级 × 100 时升级并扣除该值，否则静默拒绝。
        /// 消耗用<b>升级前的等级</b>计算，因此经验永不为负；一次命令最多升 1 级。
        /// </summary>
        public void Handle(in LevelUpHeroCommand command)
        {
            long cost = (long)_heroLevel * ExpCostPerLevel;
            if (_heroExp < cost)
            {
                return;
            }

            _heroExp -= (int)cost;
            _heroLevel++;
        }

        /// <summary>返回当前状态快照；查询无副作用（不推进战斗、不发指令）。</summary>
        public IdleRpgSnapshot Handle(in IdleRpgSnapshotQueryRequest query)
        {
            return new IdleRpgSnapshot(
                new IdleHeroState(_heroLevel, _heroExp, ComputePower(), GoldPerSecond, _highestStage),
                _currentStage,
                _inBattle,
                _enemyHp,
                CurrentStageMaxHp(),
                _battleTick);
        }

        /// <summary>执行 1 个战斗 tick：英雄先出手，敌人未死才在每第 3 个 tick 反击。</summary>
        private void ExecuteTick()
        {
            IdleStageDef current;
            if (!_tables.TryFind(_currentStage, out current))
            {
                // 只有"表被换掉"这类装配事故才会走到这里：退出战斗而不是空转。
                _inBattle = false;
                return;
            }

            _hasFoughtTick = true;
            _battleTick++;

            _enemyHp -= ComputePower();
            if (_enemyHp <= 0)
            {
                _enemyHp = 0;
                ClearCurrentStage(current);
                return;
            }

            if (_battleTick % CounterAttackIntervalTicks == 0)
            {
                _damageTaken += current.EnemyAttack;
            }
        }

        /// <summary>
        /// 通关结算：先发金币，再累计通关数与经验、刷新最高关卡，最后自动进入下一关；
        /// 没有下一关则停在最后一关并清空战斗（之后再 tick 不产生任何指令）。
        /// </summary>
        private void ClearCurrentStage(in IdleStageDef cleared)
        {
            _bus.Send(new GrantItemCommand(MetaItemIds.Gold, cleared.GoldReward, GoldGrantReasonTagId));
            _stageClearCount++;

            long gainedExp = (long)ExpPerStage * cleared.StageId;
            long totalExp = _heroExp + gainedExp;
            _heroExp = totalExp > int.MaxValue ? int.MaxValue : (int)totalExp;

            if (cleared.StageId > _highestStage)
            {
                _highestStage = cleared.StageId;
            }

            _stageCleared = true;

            IdleStageDef next;
            if (_tables.TryFind(cleared.StageId + 1, out next))
            {
                EnterStage(next);
                return;
            }

            // 最后一关：留在原地，战斗结束。
            _inBattle = false;
            _enemyHp = 0;
            _battleTick = 0;
        }

        /// <summary>进入一关：血量回满、战斗 tick 归零、清掉"已通关"标记。</summary>
        private void EnterStage(in IdleStageDef def)
        {
            _currentStage = def.StageId;
            _enemyHp = def.EnemyHp;
            _battleTick = 0;
            _inBattle = true;
            _stageCleared = false;
        }

        /// <summary>当前关卡满血值（来自子表）；关卡不在表里返回 0。</summary>
        private int CurrentStageMaxHp()
        {
            IdleStageDef current;
            return _tables.TryFind(_currentStage, out current) ? current.EnemyHp : 0;
        }

        /// <summary>战力按等级实时推导，并夹在 <c>int</c> 上限内，避免极端等级溢出成负数伤害。</summary>
        private int ComputePower()
        {
            long raw = BasePower + ((long)(_heroLevel - MinHeroLevel) * PowerPerLevel);
            return raw > int.MaxValue ? int.MaxValue : (int)raw;
        }

        /// <summary>把 tick 数夹到 <c>1..10</c>。</summary>
        private static int ClampTicks(int ticks)
        {
            if (ticks < IdleBattleTickCommand.MinTicks)
            {
                return IdleBattleTickCommand.MinTicks;
            }

            if (ticks > IdleBattleTickCommand.MaxTicks)
            {
                return IdleBattleTickCommand.MaxTicks;
            }

            return ticks;
        }
    }
}
