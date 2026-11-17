using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>飞行物世界只读视图桩：只回放测试显式加入的视图。</summary>
    public sealed class StubProjectileWorld : IProjectileWorldReadView
    {
        private const int DefaultDamage = 10;

        private readonly List<ProjectileReadView> _views;
        private readonly Dictionary<ProjectileHandle, ProjectileReadView> _lookup;

        public StubProjectileWorld()
        {
            _views = new List<ProjectileReadView>();
            _lookup = new Dictionary<ProjectileHandle, ProjectileReadView>();
        }

        /// <inheritdoc />
        public int Count
        {
            get { return _views.Count; }
        }

        /// <inheritdoc />
        public ProjectileReadView this[int index]
        {
            get { return _views[index]; }
        }

        /// <summary>加入一个飞行物视图（位置原点、朝 +X 飞、伤害固定 10）。</summary>
        public StubProjectileWorld Add(ProjectileHandle handle, UnitHandle owner, GameplayTagMask tags)
        {
            ProjectileReadView view = new ProjectileReadView(
                handle,
                owner,
                Vec2.Zero,
                new Vec2(1f, 0f),
                DefaultDamage,
                tags);

            _views.Add(view);
            _lookup[handle] = view;
            return this;
        }

        /// <inheritdoc />
        public bool TryGetProjectile(ProjectileHandle handle, out ProjectileReadView view)
        {
            return _lookup.TryGetValue(handle, out view);
        }
    }
}
