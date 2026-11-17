using AIFramework.Contracts.Gameplay;
using AIFramework.Features.HeroC_Shield;
using AIFramework.Kernel.Realtime;
using AIFramework.Kernel.Turn;
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

        public DemoKernelFactory(ISpatialQuery spatial, IReadOnlyList<ISkillInteractionPhase> phases)
        {
            _spatial = spatial;
            _phases = phases;
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
