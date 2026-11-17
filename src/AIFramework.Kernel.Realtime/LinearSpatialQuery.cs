using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 内核内置线性空间查询：只在装配方未注入 <see cref="ISpatialQuery"/>（传 null）时兜底使用，
    /// 构造期一次分配、查询期零分配。<b>有注入实现时永不启用</b>。
    /// </summary>
    internal sealed class LinearSpatialQuery : ISpatialQuery
    {
        private readonly BattleWorld _world;

        internal LinearSpatialQuery(BattleWorld world)
        {
            _world = world;
        }

        /// <inheritdoc />
        public int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results)
        {
            if (results.Length == 0)
            {
                return 0;
            }

            float radiusSqr = radius * radius;
            UnitSlot[] units = _world.UnitSlots;
            int written = 0;
            for (int i = 0; i < units.Length; i++)
            {
                if (!units[i].Active)
                {
                    continue;
                }

                Vec2 offset = units[i].Position - center;
                if (offset.SqrLength > radiusSqr)
                {
                    continue;
                }

                results[written] = units[i].ToHandle(i);
                written++;
                if (written >= results.Length)
                {
                    break;
                }
            }

            return written;
        }

        /// <inheritdoc />
        public int QueryProjectilesCircle(in Vec2 center, float radius, Span<ProjectileHandle> results)
        {
            if (results.Length == 0)
            {
                return 0;
            }

            float radiusSqr = radius * radius;
            ProjectileSlot[] projectiles = _world.ProjectileSlots;
            int written = 0;
            for (int i = 0; i < projectiles.Length; i++)
            {
                if (!projectiles[i].Active)
                {
                    continue;
                }

                Vec2 offset = projectiles[i].Position - center;
                if (offset.SqrLength > radiusSqr)
                {
                    continue;
                }

                results[written] = projectiles[i].ToHandle(i);
                written++;
                if (written >= results.Length)
                {
                    break;
                }
            }

            return written;
        }
    }
}
