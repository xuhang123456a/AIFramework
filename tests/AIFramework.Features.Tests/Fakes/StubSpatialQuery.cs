using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>
    /// 空间查询桩：只回放构造时给定的飞行物句柄，并按 <see cref="ISpatialQuery"/> 约定做容量截断。
    /// </summary>
    public sealed class StubSpatialQuery : ISpatialQuery
    {
        private readonly ProjectileHandle[] _projectiles;

        public StubSpatialQuery(params ProjectileHandle[] projectiles)
        {
            _projectiles = projectiles;
        }

        /// <summary>飞行物查询被调用次数：用于断言"没有护盾标签时连查询都不做"。</summary>
        public int ProjectileQueryCount { get; private set; }

        /// <summary>最近一次飞行物查询的圆心。</summary>
        public Vec2 LastCenter { get; private set; }

        /// <summary>最近一次飞行物查询的半径。</summary>
        public float LastRadius { get; private set; }

        /// <summary>单位查询：本切片用不到，固定返回 0。</summary>
        public int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results)
        {
            return 0;
        }

        /// <summary>飞行物查询：按容量截断写入。</summary>
        public int QueryProjectilesCircle(in Vec2 center, float radius, Span<ProjectileHandle> results)
        {
            ProjectileQueryCount++;
            LastCenter = center;
            LastRadius = radius;

            int written = 0;
            for (int i = 0; i < _projectiles.Length; i++)
            {
                if (written >= results.Length)
                {
                    break;
                }

                results[written] = _projectiles[i];
                written++;
            }

            return written;
        }
    }
}
