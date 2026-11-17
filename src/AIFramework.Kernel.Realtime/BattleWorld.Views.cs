using System;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 只读视图实现：<see cref="IUnitWorldReadView"/> 与 <see cref="IProjectileWorldReadView"/>。
    /// <para>
    /// 两个接口都存在 <c>Count</c> 与 <c>this[int]</c> 且返回类型不同，只能显式实现，避免签名冲突；
    /// 显式实现也让"阶段拿不到写通道"这件事在编译期成立。
    /// </para>
    /// <para>全部成员只做值拷贝与下标扫描，取视图不产生任何托管堆分配。</para>
    /// </summary>
    public sealed partial class BattleWorld
    {
        int IUnitWorldReadView.Count
        {
            get { return _unitCount; }
        }

        UnitReadView IUnitWorldReadView.this[int index]
        {
            get
            {
                int slot = FindUnitSlotByDenseIndex(index);
                if (slot < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _units[slot].ToView(slot);
            }
        }

        int IProjectileWorldReadView.Count
        {
            get { return _projectileCount; }
        }

        ProjectileReadView IProjectileWorldReadView.this[int index]
        {
            get
            {
                int slot = FindProjectileSlotByDenseIndex(index);
                if (slot < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _projectiles[slot].ToView(slot);
            }
        }

        /// <summary>按句柄取单位只读视图；句柄失效返回 false 且输出 <c>default</c>。</summary>
        public bool TryGetUnit(UnitHandle handle, out UnitReadView view)
        {
            int slot = ResolveUnitSlot(handle);
            if (slot < 0)
            {
                view = default;
                return false;
            }

            view = _units[slot].ToView(slot);
            return true;
        }

        /// <summary>按句柄取飞行物只读视图；句柄失效返回 false 且输出 <c>default</c>。</summary>
        public bool TryGetProjectile(ProjectileHandle handle, out ProjectileReadView view)
        {
            int slot = ResolveProjectileSlot(handle);
            if (slot < 0)
            {
                view = default;
                return false;
            }

            view = _projectiles[slot].ToView(slot);
            return true;
        }

        /// <summary>稠密下标（0..Count-1）→ 单位槽位下标；越界返回 -1。</summary>
        private int FindUnitSlotByDenseIndex(int denseIndex)
        {
            if (denseIndex < 0)
            {
                return -1;
            }

            int seen = 0;
            for (int i = 0; i < _units.Length; i++)
            {
                if (!_units[i].Active)
                {
                    continue;
                }

                if (seen == denseIndex)
                {
                    return i;
                }

                seen++;
            }

            return -1;
        }

        /// <summary>稠密下标 → 飞行物槽位下标（槽位有空洞，必须扫描映射）；越界返回 -1。</summary>
        private int FindProjectileSlotByDenseIndex(int denseIndex)
        {
            if (denseIndex < 0)
            {
                return -1;
            }

            int seen = 0;
            for (int i = 0; i < _projectiles.Length; i++)
            {
                if (!_projectiles[i].Active)
                {
                    continue;
                }

                if (seen == denseIndex)
                {
                    return i;
                }

                seen++;
            }

            return -1;
        }
    }
}
