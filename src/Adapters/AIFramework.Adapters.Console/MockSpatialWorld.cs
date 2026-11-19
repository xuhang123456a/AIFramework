#nullable enable
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using System;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 无头空间查询实现：真机换成 <c>Physics.OverlapCircleNonAlloc</c>，签名与语义保持一致。
    /// 先构造，再 <see cref="Bind"/> 到战局只读视图（避免构造期循环依赖）。
    /// </summary>
    public sealed class MockSpatialWorld : ISpatialQuery
    {
        private IUnitWorldReadView? _units;
        private IProjectileWorldReadView? _projectiles;

        public void Bind(IUnitWorldReadView units, IProjectileWorldReadView projectiles)
        {
            _units = units;
            _projectiles = projectiles;
        }

        public int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results)
        {
            IUnitWorldReadView? units = _units;
            if (units == null)
            {
                return 0;
            }

            int written = 0;
            for (int i = 0; i < units.Count; i++)
            {
                if (written >= results.Length)
                {
                    break;
                }

                UnitReadView view = units[i];
                if (Vec2.Distance(view.Position, center) <= radius + view.Radius)
                {
                    results[written] = view.Handle;
                    written++;
                }
            }

            return written;
        }

        public int QueryProjectilesCircle(in Vec2 center, float radius, Span<ProjectileHandle> results)
        {
            IProjectileWorldReadView? projectiles = _projectiles;
            if (projectiles == null)
            {
                return 0;
            }

            int written = 0;
            for (int i = 0; i < projectiles.Count; i++)
            {
                if (written >= results.Length)
                {
                    break;
                }

                ProjectileReadView view = projectiles[i];
                if (Vec2.Distance(view.Position, center) <= radius)
                {
                    results[written] = view.Handle;
                    written++;
                }
            }

            return written;
        }
    }
}
