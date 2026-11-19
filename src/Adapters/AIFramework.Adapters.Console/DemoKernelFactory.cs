using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Ports;
using AIFramework.Features.HeroC_Shield;
using AIFramework.Kernel.Realtime;
using AIFramework.Kernel.Turn;
using AIFramework.Kernel.Turn.CardBattle;
using AIFramework.Kernel.Turn.Match3;
using System.Collections.Generic;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 装配层的内核工厂：唯一允许 <c>new</c> 具体内核的地方。
    /// 通过 <see cref="LastRealtime"/> 暴露刚创建的战局，供冒烟流程注入单位与飞行物。
    /// </summary>
    public sealed class DemoKernelFactory : IGameplayKernelFactory
    {
        private readonly ISpatialQuery _spatial;
        private readonly IReadOnlyList<ISkillInteractionPhase> _phases;
        private readonly IBinaryTableLoader? _cardTable;

        public DemoKernelFactory(
            ISpatialQuery spatial,
            IReadOnlyList<ISkillInteractionPhase> phases,
            IBinaryTableLoader? cardTable = null)
        {
            _spatial = spatial;
            _phases = phases;
            _cardTable = cardTable;
        }

        public BattleWorld? LastRealtime { get; private set; }

        public IGameplaySession Create(GameplayKernelType type, in GameplayInitData init)
        {
            if (type == GameplayKernelType.Realtime)
            {
                var world = new BattleWorld(in init, _spatial, _phases);
                LastRealtime = world;
                return world;
            }

            LastRealtime = null;

            if (type == GameplayKernelType.CardBattle)
            {
                // 有子表就按配置组牌，没有就用内核内置牌库（冒烟/单测形态）。
                return _cardTable == null
                    ? new CardBattleWorld(in init)
                    : new CardBattleWorld(in init, _cardTable);
            }

            if (type == GameplayKernelType.Match3)
            {
                return new Match3World(in init);
            }

            return new TurnWorld(in init);
        }

        /// <summary>默认战斗阶段：盾牌反射技能切片。</summary>
        public static IReadOnlyList<ISkillInteractionPhase> DefaultPhases()
        {
            ISkillInteractionPhase[] phases = new ISkillInteractionPhase[]
            {
                new ShieldReflectSkillStep()
            };
            return phases;
        }
    }
}
