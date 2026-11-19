using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using AIFramework.Kernel.Realtime;

namespace AIFramework.Core.Tests.Replay
{
    /// <summary>
    /// 测试用空间查询：按"已登记单位 + 当前坐标"做线性圆查询，严格遵守
    /// <see cref="ISpatialQuery"/> 的零扩容 / 容量截断语义。
    /// <para>
    /// <c>tests/AIFramework.Core.Tests</c> 原本没有可复用的空间查询桩（只有 Features.Tests 里那个
    /// 只回放飞行物句柄的桩），因此按任务允许在本目录自建，并额外记录查询次数用于证明
    /// 飞行物命中链路真的被驱动过。
    /// </para>
    /// </summary>
    internal sealed class MockSpatialQuery : ISpatialQuery
    {
        private readonly List<UnitHandle> _spawnedUnits;

        private BattleWorld? _world;

        internal MockSpatialQuery()
        {
            _spawnedUnits = new List<UnitHandle>();
        }

        /// <summary>单位查询被调用次数（证明 Phase 4 的命中仲裁确实走的是本桩）。</summary>
        internal int UnitQueryCount { get; private set; }

        /// <summary>绑定到同一个战场的只读查询入口；必须先绑定再布场。</summary>
        internal void Bind(BattleWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            _world = world;
        }

        /// <summary>经由本桩布场：登记句柄，后续查询才能看到它。</summary>
        internal UnitHandle SpawnUnit(Vec2 position, float radius, int armorLevel, int hp, GameplayTagMask tags)
        {
            if (_world == null)
            {
                throw new InvalidOperationException("MockSpatialQuery 尚未绑定战场，禁止布场。");
            }

            UnitHandle handle = _world.SpawnUnit(position, radius, armorLevel, hp, tags);
            if (handle.IsValid)
            {
                _spawnedUnits.Add(handle);
            }

            return handle;
        }

        /// <inheritdoc />
        public int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results)
        {
            UnitQueryCount++;

            BattleWorld world = _world ?? throw new InvalidOperationException("MockSpatialQuery 尚未绑定战场。");
            int written = 0;
            float radiusSqr = radius * radius;

            for (int i = 0; i < _spawnedUnits.Count && written < results.Length; i++)
            {
                if (!world.TryGetUnit(_spawnedUnits[i], out UnitReadView view))
                {
                    continue;
                }

                Vec2 offset = view.Position - center;
                if (offset.SqrLength > radiusSqr)
                {
                    continue;
                }

                results[written] = view.Handle;
                written++;
            }

            return written;
        }

        /// <inheritdoc />
        /// <remarks>Phase 1 的固定管线只用单位查询做飞行物命中；技能切片若查飞行物由具体测试另建桩。</remarks>
        public int QueryProjectilesCircle(in Vec2 center, float radius, Span<ProjectileHandle> results)
        {
            return 0;
        }
    }
}
