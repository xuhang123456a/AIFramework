using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 强实时固定帧战斗沙盒（<see cref="GameplayKernelType.Realtime"/>）。
    /// <para>
    /// <see cref="Step(float)"/> 严格按固定 6 阶段推进，每帧顺序写死：
    /// 1 输入采集 → 2 Buff 步进 → 3 技能时间轴 → 4 空间碰撞与伤害仲裁 →
    /// 5 移动与控制权仲裁 → 6 清理与快照输出。
    /// </para>
    /// <para>
    /// <b>零分配契约</b>：单位槽位、飞行物槽位、四类意图缓冲、触发标签表、阶段数组全部在构造期一次分配；
    /// <see cref="Step(float)"/> 内不出现 LINQ、闭包 / lambda、<c>params</c>、装箱、字符串、
    /// <c>foreach over IEnumerable</c>，空间查询统一走 <c>stackalloc</c> + <see cref="Span{T}"/>。
    /// 预热后连续 1000 帧 <c>GC.GetAllocatedBytesForCurrentThread()</c> 增量严格为 0 字节。
    /// </para>
    /// </summary>
    public sealed partial class BattleWorld
        : IGameplaySession, IRealtimeSandbox, IUnitWorldReadView, IProjectileWorldReadView, IIntentWriter
    {
        /// <summary>单位槽位下限容量（构造期一次分配）。</summary>
        private const int MinUnitCapacity = 64;

        /// <summary>单位槽位上限容量，防止异常的 <see cref="GameplayInitData.UnitCount"/> 撑爆内存。</summary>
        private const int MaxUnitCapacity = 1024;

        /// <summary>单次空间查询的栈上命中缓冲容量（容量不足按容量截断，不扩容）。</summary>
        private const int MaxUnitsPerQuery = 32;

        /// <summary>
        /// 逻辑帧上限：<see cref="FrameId"/> 触达该值即视为对局结束。
        /// 必须大于 <c>Doc/03</c> 要求的"预热后连续 Step 1000 帧"测量窗口，否则测量会被截断成 no-op。
        /// </summary>
        private const int MaxFrames = 1200;

        /// <summary>飞行物判定半径（命中条件：中心距 &lt;= 单位半径 + 本半径）。</summary>
        private const float ProjectileRadius = 0.1f;

        /// <summary>被打断后施加的硬直帧数（60 fps 下约 1/6 秒）。</summary>
        private const int InterruptControlFrames = 10;

        /// <summary>控制位 - 定身。</summary>
        private const int ControlFlagRooted = 1;

        /// <summary>控制位 - 沉默。</summary>
        private const int ControlFlagSilenced = 2;

        /// <summary>打断时施加的控制位组合。</summary>
        private const int InterruptControlFlags = ControlFlagRooted | ControlFlagSilenced;

        /// <summary>本局触发标签上限。</summary>
        private const int MaxTriggeredTags = 8;

        private readonly UnitSlot[] _units;

        private readonly ProjectileSlot[] _projectiles;

        /// <summary>构造期注入的阶段数组（由 <see cref="IReadOnlyList{T}"/> 拷贝而来，帧内用下标 for 遍历）。</summary>
        private readonly ISkillInteractionPhase[] _phases;

        private readonly ISpatialQuery _spatial;

        /// <summary>控制意图缓冲（构造期一次分配）。</summary>
        private IntentBuffer<ControlIntent> _controlIntents;

        /// <summary>位移意图缓冲（构造期一次分配）。</summary>
        private IntentBuffer<MoveIntent> _moveIntents;

        /// <summary>伤害意图缓冲（构造期一次分配）。</summary>
        private IntentBuffer<DamageIntent> _damageIntents;

        /// <summary>反射意图缓冲（构造期一次分配）。</summary>
        private IntentBuffer<ReflectProjectileIntent> _reflectIntents;

        /// <summary>本局触发标签表（构造期一次分配）。</summary>
        private TriggeredTagBuffer _triggeredTags;

        /// <summary>已消费的控制意图游标：Phase 1 / Phase 5 各自续读，Phase 6 归零。</summary>
        private int _controlCursor;

        /// <summary>已消费的位移意图游标。</summary>
        private int _moveCursor;

        /// <summary>已消费的伤害意图游标。</summary>
        private int _damageCursor;

        /// <summary>已消费的反射意图游标。</summary>
        private int _reflectCursor;

        private int _frameId;

        private int _unitCount;

        private int _projectileCount;

        private int _interruptedCount;

        private int _reflectedCount;

        /// <summary>所有已占用单位里的最大半径，用于给空间查询算外包圆。</summary>
        private float _maxUnitRadius;

        /// <summary>主角槽位：首个成功 <see cref="SpawnUnit"/> 的单位；-1 表示尚无单位。</summary>
        private int _protagonistSlot;

        /// <summary>Phase 1 本帧结算的控制意图数（诊断计数，不参与玩法）。</summary>
        private int _appliedControlIntentCount;

        /// <summary>Phase 1 本帧暂存的位移意图数（诊断计数）。</summary>
        private int _stagedMoveIntentCount;

        /// <summary>Phase 5 本帧落位的挂起位移数（诊断计数）。</summary>
        private int _committedMoveCount;

        /// <summary>Phase 4 本帧结算的显式伤害意图数（诊断计数）。</summary>
        private int _settledDamageIntentCount;

        private bool _resultCollected;

        public BattleWorld(
            in GameplayInitData init,
            ISpatialQuery spatial,
            IReadOnlyList<ISkillInteractionPhase> phases)
        {
            int unitCapacity = ResolveUnitCapacity(init.UnitCount);
            int projectileCapacity = unitCapacity * 2;

            _units = new UnitSlot[unitCapacity];
            _projectiles = new ProjectileSlot[projectileCapacity];
            _phases = CopyPhases(phases);
            _spatial = spatial != null ? spatial : (ISpatialQuery)new LinearSpatialQuery(this);

            _controlIntents = new IntentBuffer<ControlIntent>(ControlIntentCapacity);
            _moveIntents = new IntentBuffer<MoveIntent>(MoveIntentCapacity);
            _damageIntents = new IntentBuffer<DamageIntent>(DamageIntentCapacity);
            _reflectIntents = new IntentBuffer<ReflectProjectileIntent>(ReflectIntentCapacity);
            _triggeredTags = new TriggeredTagBuffer(MaxTriggeredTags);

            _controlCursor = 0;
            _moveCursor = 0;
            _damageCursor = 0;
            _reflectCursor = 0;
            _frameId = 0;
            _unitCount = 0;
            _projectileCount = 0;
            _interruptedCount = 0;
            _reflectedCount = 0;
            _maxUnitRadius = 0f;
            _protagonistSlot = -1;
            _appliedControlIntentCount = 0;
            _stagedMoveIntentCount = 0;
            _committedMoveCount = 0;
            _settledDamageIntentCount = 0;
            _resultCollected = false;
        }

        /// <summary>当前逻辑帧号，从 0 开始，每个 <see cref="Step(float)"/> 的 Phase 6 递增。</summary>
        public int FrameId
        {
            get { return _frameId; }
        }

        /// <summary>已占用单位槽位数（含已阵亡但仍占位的单位）。</summary>
        public int UnitCount
        {
            get { return _unitCount; }
        }

        /// <summary>存活飞行物槽位数。</summary>
        public int ProjectileCount
        {
            get { return _projectileCount; }
        }

        /// <summary>累计打断次数（<see cref="SkillInterruptResolver.ShouldInterrupt"/> 命中即计）。</summary>
        public int InterruptedCount
        {
            get { return _interruptedCount; }
        }

        /// <summary>累计成功反射次数（已反射过的飞行物不重复计数）。</summary>
        public int ReflectedCount
        {
            get { return _reflectedCount; }
        }

        /// <summary>单位槽位数组（仅内核内部与内置线性查询可见）。</summary>
        internal UnitSlot[] UnitSlots
        {
            get { return _units; }
        }

        /// <summary>飞行物槽位数组（仅内核内部与内置线性查询可见）。</summary>
        internal ProjectileSlot[] ProjectileSlots
        {
            get { return _projectiles; }
        }

        /// <summary>四类意图缓冲的溢出总数（诊断用，缓冲满则丢弃并累加）。</summary>
        internal int IntentOverflowCount
        {
            get
            {
                return _controlIntents.OverflowCount
                    + _moveIntents.OverflowCount
                    + _damageIntents.OverflowCount
                    + _reflectIntents.OverflowCount;
            }
        }

        /// <summary>
        /// 生成一个战场单位。槽位耗尽时返回无效句柄（<c>default</c>），不扩容、不抛异常。
        /// </summary>
        public UnitHandle SpawnUnit(Vec2 position, float radius, int armorLevel, int hp, GameplayTagMask tags)
        {
            for (int i = 0; i < _units.Length; i++)
            {
                if (_units[i].Active)
                {
                    continue;
                }

                float safeRadius = radius > 0f ? radius : 0f;
                int generation = _units[i].Generation + 1;
                _units[i].Occupy(generation, in position, safeRadius, armorLevel, hp, tags);
                if (_protagonistSlot < 0)
                {
                    _protagonistSlot = i;
                }

                _unitCount++;

                if (safeRadius > _maxUnitRadius)
                {
                    _maxUnitRadius = safeRadius;
                }

                return _units[i].ToHandle(i);
            }

            return default;
        }

        /// <summary>
        /// 生成一个飞行物。槽位耗尽时返回无效句柄（<c>default</c>），不扩容、不抛异常。
        /// </summary>
        public ProjectileHandle SpawnProjectile(
            UnitHandle owner,
            Vec2 position,
            Vec2 velocity,
            int damage,
            GameplayTagMask tags)
        {
            for (int i = 0; i < _projectiles.Length; i++)
            {
                if (_projectiles[i].Active)
                {
                    continue;
                }

                int generation = _projectiles[i].Generation + 1;
                _projectiles[i].Occupy(generation, owner, in position, in velocity, damage, tags);
                _projectileCount++;
                return _projectiles[i].ToHandle(i);
            }

            return default;
        }

        /// <summary>
        /// 推进一个逻辑帧，严格按 6 阶段固定顺序执行。
        /// 已结束的会话再调用为空操作（不推进 <see cref="FrameId"/>）。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            float stepSeconds = deltaSeconds > 0f ? deltaSeconds : 0f;

            StageInputCollect();
            StageBuffTick();
            StageSkillTimeline();
            StageCollisionAndDamage(stepSeconds);
            StageMovementArbitration();
            StageCleanupAndSnapshot();
        }

        /// <summary>任意单位 HP &lt;= 0，或 <see cref="FrameId"/> 触达 600 帧上限。</summary>
        public bool IsFinished
        {
            get
            {
                if (_frameId >= MaxFrames)
                {
                    return true;
                }

                for (int i = 0; i < _units.Length; i++)
                {
                    if (_units[i].Active && _units[i].Hp <= 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 堆数组只在这里按结果分配一次。
        /// <para>
        /// 胜负判定：Phase 1 契约里没有阵营概念，因此约定 **0 号单位（装配层第一个 <c>SpawnUnit</c> 的单位）为主角，主角存活即胜利**；
        /// 胜利发放 <c>LootItem(2001, 1)</c>。这样"护盾反射打死来袭敌人"能被正确读成一次胜利，而不是因为有人阵亡就判负。
        /// 空战场（从未生成过单位）没有主角，一律判负、不发战利品。
        /// </para>
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!IsFinished)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;

            int aliveUnits = 0;
            for (int i = 0; i < _units.Length; i++)
            {
                if (_units[i].Active && _units[i].Hp > 0)
                {
                    aliveUnits++;
                }
            }

            // Phase 1 约定：0 号单位（首个 SpawnUnit 的单位）为主角，主角存活即胜利；无单位则无主角、判负。
            bool isVictory = _protagonistSlot >= 0
                && _units[_protagonistSlot].Active
                && _units[_protagonistSlot].Hp > 0;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(2001, 1) }
                : Array.Empty<LootItem>();

            IReadOnlyList<GameplayTag> triggeredTags = BuildTriggeredTagArray();

            return new GameplayResult(
                GameplayKernelType.Realtime,
                isVictory,
                _frameId,
                aliveUnits,
                triggeredTags,
                loots);
        }

        /// <summary>把触发标签表按实际条数落成出门口 DTO 需要的数组（仅在 CollectResult 内调用）。</summary>
        private GameplayTag[] BuildTriggeredTagArray()
        {
            int count = _triggeredTags.Count;
            if (count <= 0)
            {
                return Array.Empty<GameplayTag>();
            }

            GameplayTag[] tags = new GameplayTag[count];
            for (int i = 0; i < count; i++)
            {
                tags[i] = _triggeredTags.At(i);
            }

            return tags;
        }

        /// <summary>由 <see cref="GameplayInitData.UnitCount"/> 推导单位槽位容量（有上下限保护）。</summary>
        private static int ResolveUnitCapacity(int declaredUnitCount)
        {
            int capacity = MinUnitCapacity;
            if (declaredUnitCount > 0)
            {
                int doubled = declaredUnitCount >= MaxUnitCapacity ? MaxUnitCapacity : declaredUnitCount * 2;
                if (doubled > capacity)
                {
                    capacity = doubled;
                }
            }

            return capacity > MaxUnitCapacity ? MaxUnitCapacity : capacity;
        }

        /// <summary>把注入的阶段列表拷贝进定长数组，帧内不再触碰 <see cref="IReadOnlyList{T}"/>。</summary>
        private static ISkillInteractionPhase[] CopyPhases(IReadOnlyList<ISkillInteractionPhase> phases)
        {
            if (phases == null || phases.Count == 0)
            {
                return Array.Empty<ISkillInteractionPhase>();
            }

            ISkillInteractionPhase[] copy = new ISkillInteractionPhase[phases.Count];
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = phases[i];
            }

            return copy;
        }

        /// <summary>句柄 → 槽位下标；句柄失效（越界 / 空槽 / 世代不符）返回 -1。</summary>
        private int ResolveUnitSlot(UnitHandle handle)
        {
            int index = handle.Index;
            if (index < 0 || index >= _units.Length)
            {
                return -1;
            }

            if (!_units[index].Active)
            {
                return -1;
            }

            return _units[index].Generation == handle.Generation ? index : -1;
        }

        /// <summary>句柄 → 槽位下标；句柄失效返回 -1。</summary>
        private int ResolveProjectileSlot(ProjectileHandle handle)
        {
            int index = handle.Index;
            if (index < 0 || index >= _projectiles.Length)
            {
                return -1;
            }

            if (!_projectiles[index].Active)
            {
                return -1;
            }

            return _projectiles[index].Generation == handle.Generation ? index : -1;
        }

        /// <summary>回收飞行物槽位（世代号保留，下次复用自增使旧句柄失效）。</summary>
        private void RecycleProjectile(int slotIndex)
        {
            if (!_projectiles[slotIndex].Active)
            {
                return;
            }

            _projectiles[slotIndex].Release();
            _projectileCount--;
        }

        /// <summary>点到线段的最短距离（扫掠命中判定，零分配）。</summary>
        private static float DistancePointToSegment(in Vec2 point, in Vec2 start, in Vec2 end)
        {
            float segmentX = end.X - start.X;
            float segmentY = end.Y - start.Y;
            float segmentLengthSqr = (segmentX * segmentX) + (segmentY * segmentY);

            if (segmentLengthSqr <= 0f)
            {
                return Vec2.Distance(in point, in start);
            }

            float t = (((point.X - start.X) * segmentX) + ((point.Y - start.Y) * segmentY)) / segmentLengthSqr;
            if (t < 0f)
            {
                t = 0f;
            }
            else if (t > 1f)
            {
                t = 1f;
            }

            float offsetX = point.X - (start.X + (segmentX * t));
            float offsetY = point.Y - (start.Y + (segmentY * t));
            return (float)Math.Sqrt((offsetX * offsetX) + (offsetY * offsetY));
        }
    }
}
