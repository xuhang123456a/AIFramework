using System;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Core.Replay
{
    /// <summary>
    /// 状态指纹：把"可观测状态"压成一个 <see cref="long"/>，用于回放比对（避免逐字段比较）。
    /// <para>
    /// 折叠顺序完全按视图的稠密下标（<c>0..Count-1</c>）稳定遍历：
    /// 单位 → 飞行物；每个单位折入句柄（下标 + 世代号）、量化位置、半径、霸体、HP、标签掩码；
    /// 每个飞行物折入句柄、归属者、量化位置、量化速度、伤害、标签掩码。
    /// 最后把两段计数也折进去，因此"少一个单位"与"某个字段变化"都会改变指纹。
    /// </para>
    /// <para>
    /// <b>位置/速度量化到 1/1000</b>：回放比对关心的是"逻辑上是否一致"，
    /// 不把浮点末位噪声当成状态差异；量化后仍对真实位移敏感（<c>0.001</c> 量级即可区分）。
    /// </para>
    /// <para>
    /// <b>零分配</b>：只用栈上的 <see cref="ulong"/> 累加器与按值返回的只读视图，
    /// 不建集合、不装箱、不拼字符串；单位/飞行物数量由内核槽位上限兜底。
    /// </para>
    /// </summary>
    public static class StateFingerprint
    {
        /// <summary>FNV-1a 64 位偏移基数。</summary>
        private const ulong FnvOffsetBasis = 14695981039346656037UL;

        /// <summary>FNV-1a 64 位质数。</summary>
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>单位段起始标记（"UNIT"），避免"单位段/飞行物段"两段内容被错位折叠后撞哈希。</summary>
        private const ulong UnitSectionMarker = 0x554E4954UL;

        /// <summary>飞行物段起始标记（"PROJ"）。</summary>
        private const ulong ProjectileSectionMarker = 0x50524F4AUL;

        /// <summary>单个被观察条目的类型标记。</summary>
        private const ulong UnitItemMarker = 1UL;

        /// <summary>单个被观察条目的类型标记。</summary>
        private const ulong ProjectileItemMarker = 2UL;

        /// <summary>量化上下限：远大于任何赛场地形坐标，同时避免 <see cref="double"/> 转 <see cref="long"/> 的平台相关溢出。</summary>
        private const long QuantizeLimit = 9000000000000000L;

        /// <summary>把单位/飞行物只读视图压成 64 位状态指纹。</summary>
        /// <param name="units">单位世界只读视图。</param>
        /// <param name="projectiles">飞行物世界只读视图。</param>
        /// <exception cref="ArgumentNullException">任一视图为 <c>null</c>。</exception>
        public static long Of(IUnitWorldReadView units, IProjectileWorldReadView projectiles)
        {
            if (units == null)
            {
                throw new ArgumentNullException(nameof(units));
            }

            if (projectiles == null)
            {
                throw new ArgumentNullException(nameof(projectiles));
            }

            ulong hash = FnvOffsetBasis;

            Fold(ref hash, UnitSectionMarker);
            Fold(ref hash, (ulong)units.Count);
            for (int i = 0; i < units.Count; i++)
            {
                UnitReadView unit = units[i];
                Fold(ref hash, UnitItemMarker);
                Fold(ref hash, (ulong)(long)unit.Handle.Index);
                Fold(ref hash, (ulong)(long)unit.Handle.Generation);
                Fold(ref hash, (ulong)Quantize(unit.Position.X));
                Fold(ref hash, (ulong)Quantize(unit.Position.Y));
                Fold(ref hash, (ulong)Quantize(unit.Radius));
                Fold(ref hash, (ulong)(long)unit.ArmorLevel);
                Fold(ref hash, (ulong)(long)unit.Hp);
                Fold(ref hash, unit.Tags.Bits);
            }

            Fold(ref hash, ProjectileSectionMarker);
            Fold(ref hash, (ulong)projectiles.Count);
            for (int i = 0; i < projectiles.Count; i++)
            {
                ProjectileReadView projectile = projectiles[i];
                Fold(ref hash, ProjectileItemMarker);
                Fold(ref hash, (ulong)(long)projectile.Handle.Index);
                Fold(ref hash, (ulong)(long)projectile.Handle.Generation);
                Fold(ref hash, (ulong)(long)projectile.Owner.Index);
                Fold(ref hash, (ulong)(long)projectile.Owner.Generation);
                Fold(ref hash, (ulong)Quantize(projectile.Position.X));
                Fold(ref hash, (ulong)Quantize(projectile.Position.Y));
                Fold(ref hash, (ulong)Quantize(projectile.Velocity.X));
                Fold(ref hash, (ulong)Quantize(projectile.Velocity.Y));
                Fold(ref hash, (ulong)(long)projectile.Damage);
                Fold(ref hash, projectile.Tags.Bits);
            }

            return unchecked((long)hash);
        }

        /// <summary>把坐标/速度量化到 1/1000（四舍五入、负数远离零），NaN / 无穷归一为 0。</summary>
        private static long Quantize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 0L;
            }

            double scaled = (double)value * 1000.0;
            if (scaled > QuantizeLimit)
            {
                return QuantizeLimit;
            }

            if (scaled < -QuantizeLimit)
            {
                return -QuantizeLimit;
            }

            return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }

        /// <summary>FNV-1a 64：把一个 64 位值按小端逐字节折进哈希（无分配）。</summary>
        private static void Fold(ref ulong hash, ulong value)
        {
            unchecked
            {
                for (int byteIndex = 0; byteIndex < 8; byteIndex++)
                {
                    hash ^= (byte)(value >> (byteIndex * 8));
                    hash *= FnvPrime;
                }
            }
        }
    }
}
