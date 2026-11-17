using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using AIFramework.Features.HeroC_Shield;
using AIFramework.Features.Tests.Fakes;
using Xunit;

namespace AIFramework.Features.Tests.HeroC_Shield
{
    /// <summary>
    /// 护盾反射阶段验收：只按标签与句柄仲裁，输出只有 <c>ReflectProjectileIntent</c>。
    /// </summary>
    public sealed class ShieldReflectSkillStepTests
    {
        private const float OwnerRadius = 2f;
        private const float QueryPadding = 0.25f;

        [Fact]
        public void ReflectsNormalProjectileAndSkipsUnreflectable()
        {
            UnitReadView owner = CreateOwner(shieldActive: true);
            ProjectileHandle normal = new ProjectileHandle(2, 1);
            ProjectileHandle unreflectable = new ProjectileHandle(3, 1);

            StubSpatialQuery spatial = new StubSpatialQuery(normal, unreflectable);
            StubProjectileWorld projectiles = new StubProjectileWorld()
                .Add(normal, new UnitHandle(9, 1), GameplayTagMask.Empty)
                .Add(unreflectable, new UnitHandle(9, 1), GameplayTagMask.Empty.Add(GameplayTags.ProjUnreflectable));

            PhaseContext context = CreateContext(owner, projectiles, spatial);
            RecordingIntentWriter writer = new RecordingIntentWriter();
            ShieldReflectSkillStep step = new ShieldReflectSkillStep(QueryPadding);

            step.Execute(in context, writer);

            ReflectProjectileIntent intent = Assert.Single(writer.ReflectIntents);
            Assert.Equal(owner.Handle, intent.Reflector);
            Assert.Equal(normal, intent.Projectile);

            Assert.Equal(1, writer.TotalSubmissions);
            Assert.Equal(1, step.LastSubmittedCount);

            // 查询半径 = 自身判定半径 + 构造时给的余量。
            Assert.Equal(1, spatial.ProjectileQueryCount);
            Assert.Equal(OwnerRadius + QueryPadding, spatial.LastRadius);
        }

        [Fact]
        public void WithoutShieldTag_SubmitsNothingAndSkipsSpatialQuery()
        {
            UnitReadView owner = CreateOwner(shieldActive: false);
            ProjectileHandle normal = new ProjectileHandle(2, 1);

            StubSpatialQuery spatial = new StubSpatialQuery(normal);
            StubProjectileWorld projectiles = new StubProjectileWorld()
                .Add(normal, new UnitHandle(9, 1), GameplayTagMask.Empty);

            PhaseContext context = CreateContext(owner, projectiles, spatial);
            RecordingIntentWriter writer = new RecordingIntentWriter();
            ShieldReflectSkillStep step = new ShieldReflectSkillStep();

            step.Execute(in context, writer);

            Assert.Empty(writer.ReflectIntents);
            Assert.Equal(0, writer.TotalSubmissions);
            Assert.Equal(0, step.LastSubmittedCount);
            Assert.Equal(0, spatial.ProjectileQueryCount);
        }

        [Fact]
        public void AlreadyReflectedProjectile_IsNotReflectedTwice()
        {
            UnitReadView owner = CreateOwner(shieldActive: true);
            ProjectileHandle reflected = new ProjectileHandle(4, 1);

            StubSpatialQuery spatial = new StubSpatialQuery(reflected);
            StubProjectileWorld projectiles = new StubProjectileWorld()
                .Add(reflected, new UnitHandle(9, 1), GameplayTagMask.Empty.Add(GameplayTags.ProjReflected));

            PhaseContext context = CreateContext(owner, projectiles, spatial);
            RecordingIntentWriter writer = new RecordingIntentWriter();
            ShieldReflectSkillStep step = new ShieldReflectSkillStep();

            step.Execute(in context, writer);

            Assert.Empty(writer.ReflectIntents);
            Assert.Equal(0, writer.TotalSubmissions);
            Assert.Equal(0, step.LastSubmittedCount);
            Assert.Equal(1, spatial.ProjectileQueryCount);
        }

        [Fact]
        public void OwnProjectile_IsSkipped()
        {
            UnitReadView owner = CreateOwner(shieldActive: true);
            ProjectileHandle own = new ProjectileHandle(5, 1);

            StubSpatialQuery spatial = new StubSpatialQuery(own);
            StubProjectileWorld projectiles = new StubProjectileWorld()
                .Add(own, owner.Handle, GameplayTagMask.Empty);

            PhaseContext context = CreateContext(owner, projectiles, spatial);
            RecordingIntentWriter writer = new RecordingIntentWriter();
            ShieldReflectSkillStep step = new ShieldReflectSkillStep();

            step.Execute(in context, writer);

            Assert.Empty(writer.ReflectIntents);
            Assert.Equal(0, writer.TotalSubmissions);
            Assert.Equal(0, step.LastSubmittedCount);
        }

        [Fact]
        public void MissingProjectileView_IsSkipped()
        {
            UnitReadView owner = CreateOwner(shieldActive: true);
            ProjectileHandle unknown = new ProjectileHandle(6, 1);

            StubSpatialQuery spatial = new StubSpatialQuery(unknown);
            StubProjectileWorld projectiles = new StubProjectileWorld();

            PhaseContext context = CreateContext(owner, projectiles, spatial);
            RecordingIntentWriter writer = new RecordingIntentWriter();
            ShieldReflectSkillStep step = new ShieldReflectSkillStep();

            step.Execute(in context, writer);

            Assert.Empty(writer.ReflectIntents);
            Assert.Equal(0, step.LastSubmittedCount);
        }

        private static UnitReadView CreateOwner(bool shieldActive)
        {
            GameplayTagMask tags = GameplayTagMask.Empty;
            if (shieldActive)
            {
                tags = tags.Add(GameplayTags.UnitShieldActive);
            }

            return new UnitReadView(new UnitHandle(1, 1), Vec2.Zero, OwnerRadius, 0, 100, tags);
        }

        private static PhaseContext CreateContext(
            UnitReadView owner,
            IProjectileWorldReadView projectiles,
            ISpatialQuery spatial)
        {
            return new PhaseContext(0, in owner, new StubUnitWorld(), projectiles, spatial);
        }
    }
}
