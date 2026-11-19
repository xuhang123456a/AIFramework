#nullable enable
using System;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Features.HeroC_Shield
{
    /// <summary>
    /// 护盾反射技能的交互阶段：护盾单位在自身判定圈内收集飞行物，按标签过滤后逐条提交反射意图。
    /// 本类只认标签与句柄，<b>不含任何英雄专名与具体类型判断</b>；<see cref="Execute"/> 零堆分配。
    /// </summary>
    public sealed class ShieldReflectSkillStep : ISkillInteractionPhase
    {
        /// <summary>单帧最多处理 8 个飞行物（栈上定长缓冲，超出按容量截断）。</summary>
        private const int MaxProjectilesPerStep = 8;

        private readonly float _queryRadiusPadding;
        private int _lastSubmittedCount;

        public ShieldReflectSkillStep(float queryRadiusPadding = 0.5f)
        {
            _queryRadiusPadding = queryRadiusPadding;
        }

        /// <summary>上一次 <see cref="Execute"/> 实际提交的反射意图数量。</summary>
        public int LastSubmittedCount
        {
            get { return _lastSubmittedCount; }
        }

        /// <inheritdoc />
        public void Execute(in PhaseContext context, IIntentWriter writer)
        {
            _lastSubmittedCount = 0;

            // 规则 1：没有护盾标签直接退出。
            if (!context.Owner.Tags.Has(GameplayTags.UnitShieldActive))
            {
                return;
            }

            Span<ProjectileHandle> buffer = stackalloc ProjectileHandle[MaxProjectilesPerStep];
            int found = context.Spatial.QueryProjectilesCircle(
                context.Owner.Position,
                context.Owner.Radius + _queryRadiusPadding,
                buffer);

            // 端口约定按容量截断，这里再夹一次，避免异常实现越界写入。
            int limit = found < buffer.Length ? found : buffer.Length;
            UnitHandle self = context.Owner.Handle;
            IProjectileWorldReadView projectiles = context.Projectiles;
            int submitted = 0;
            for (int i = 0; i < limit; i++)
            {
                ProjectileHandle handle = buffer[i];
                if (!projectiles.TryGetProjectile(handle, out ProjectileReadView view))
                {
                    continue;
                }

                // 规则 2：不打自己的飞行物。
                if (view.Owner == self)
                {
                    continue;
                }

                // 规则 3：不可反射标签。
                if (view.Tags.Has(GameplayTags.ProjUnreflectable))
                {
                    continue;
                }

                // 规则 4：已反射过，防二次反射。
                if (view.Tags.Has(GameplayTags.ProjReflected))
                {
                    continue;
                }

                writer.Submit(new ReflectProjectileIntent(self, handle));
                submitted++;
            }

            _lastSubmittedCount = submitted;
        }
    }
}
