#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Ports;
using AIFramework.Features.HeroC_Shield;
using AIFramework.Kernel.Realtime;
using AIFramework.Kernel.Turn;
using AIFramework.Kernel.Turn.CardBattle;
using AIFramework.Kernel.Turn.Match3;
using AIFramework.Kernel.Turn.Puzzle;
using AIFramework.Kernel.Turn.Tetris;
using AIFramework.Kernel.Turn.AutoChess;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 装配层的内核工厂：<b>全工程唯一执行 <c>new TurnWorld / new BattleWorld</c> 的地方</b>。
    /// <c>Core</c> 与 <c>Features</c> 都只依赖 <see cref="IGameplayKernelFactory"/>，因此"哪套卡带、带哪些阶段"
    /// 由本类这一处决定，换引擎只换本类。
    /// </summary>
    public sealed class UnityKernelFactory : IGameplayKernelFactory
    {
        /// <summary>空间查询端口；<see cref="BattleWorld"/> 每帧按它收集命中。</summary>
        private readonly ISpatialQuery _spatial;

        /// <summary>技能交互阶段列表：构造期传入，<see cref="BattleWorld"/> 会自己拷贝成定长数组，因此这里只借不改。</summary>
        private readonly IReadOnlyList<ISkillInteractionPhase> _phases;

        /// <summary>注入端口与阶段。</summary>
        /// <param name="spatial">空间查询端口，通常是 <see cref="UnitySpatialQuery"/>。</param>
        /// <param name="phases">技能交互阶段；传 <c>null</c> 时 <see cref="BattleWorld"/> 自己退化成空阶段列表。</param>
        /// <exception cref="ArgumentNullException"><paramref name="spatial"/> 为 <c>null</c>。</exception>
        /// <summary>卡牌子表加载器；为 <c>null</c> 时卡牌内核用内置牌库。</summary>
        private readonly IBinaryTableLoader? _cardTable;

        public UnityKernelFactory(ISpatialQuery spatial, IReadOnlyList<ISkillInteractionPhase> phases)
            : this(spatial, phases, null)
        {
        }

        /// <summary>注入端口、阶段与卡牌子表加载器。</summary>
        /// <param name="spatial">空间查询端口。</param>
        /// <param name="phases">技能交互阶段。</param>
        /// <param name="cardTable">卡牌子表加载器（可空）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="spatial"/> 为 <c>null</c>。</exception>
        public UnityKernelFactory(
            ISpatialQuery spatial,
            IReadOnlyList<ISkillInteractionPhase> phases,
            IBinaryTableLoader? cardTable)
        {
            if (spatial == null)
            {
                throw new ArgumentNullException(nameof(spatial));
            }

            _spatial = spatial;
            _phases = phases;
            _cardTable = cardTable;
        }

        /// <summary>
        /// 最近一次 <see cref="Create"/> 产出的实时战局。<b>为什么暴露它</b>：宿主必须能在会话里
        /// <c>SpawnUnit / SpawnProjectile</c> 并为它们登记碰撞体，而 <see cref="IGameplaySession"/> 只提供
        /// <c>Step / IsFinished / CollectResult</c> 三个成员，<b>契约里没有"往世界里塞单位"的入口</b>——
        /// 这是当前 Phase 1 的一个真实缺口（见交付报告的"契约缺口"一节）。
        /// 创建回合制会话时会被置回 <c>null</c>，避免宿主误用上一局的引用。
        /// </summary>
        public BattleWorld? LastRealtime { get; private set; }

        /// <inheritdoc />
        public IGameplaySession Create(GameplayKernelType type, in GameplayInitData init)
        {
            if (type == GameplayKernelType.Realtime)
            {
                var world = new BattleWorld(in init, _spatial, _phases);
                LastRealtime = world;
                return world;
            }

            // 回合制内核不需要空间查询，也没有可注入的运行时对象；这里清掉实时战局引用是刻意的。
            LastRealtime = null;

            if (type == GameplayKernelType.CardBattle)
            {
                return _cardTable == null
                    ? new CardBattleWorld(in init)
                    : new CardBattleWorld(in init, _cardTable);
            }

            if (type == GameplayKernelType.Match3)
            {
                return new Match3World(in init);
            }

            if (type == GameplayKernelType.Puzzle)
            {
                return new PuzzleWorld(in init);
            }

            if (type == GameplayKernelType.Tetris)
            {
                return new TetrisWorld(in init);
            }

            if (type == GameplayKernelType.AutoChess)
            {
                return new AutoChessWorld(in init);
            }

            return new TurnWorld(in init);
        }

        /// <summary>
        /// Phase 1 的默认战斗阶段集合：目前只有护盾反射。<b>为什么写成静态工厂而不是常量数组</b>：
        /// 阶段实例带内部状态（如 <c>ShieldReflectSkillStep.LastSubmittedCount</c>），
        /// 每局应当拿到新实例，不能跨局共享同一个对象。
        /// </summary>
        public static IReadOnlyList<ISkillInteractionPhase> CreateDefaultPhases()
        {
            ISkillInteractionPhase[] phases = new ISkillInteractionPhase[]
            {
                new ShieldReflectSkillStep()
            };

            return phases;
        }
    }
}
