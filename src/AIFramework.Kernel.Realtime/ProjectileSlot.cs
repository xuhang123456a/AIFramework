using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 飞行物槽位：纯数据，预分配在 <see cref="BattleWorld"/> 内部数组里。
    /// 命中后立即释放，槽位复用通过 <see cref="Generation"/> 自增使旧句柄失效。
    /// </summary>
    internal struct ProjectileSlot
    {
        internal bool Active;

        /// <summary>世代号：槽位每次复用自增。</summary>
        internal int Generation;

        /// <summary>当前归属者；反射后改为反射者。</summary>
        internal UnitHandle Owner;

        internal Vec2 Position;

        internal Vec2 Velocity;

        internal int Damage;

        internal GameplayTagMask Tags;

        /// <summary>本帧积分前的坐标，供扫掠命中判定使用（避免大 dt 穿透）。</summary>
        internal Vec2 PreviousPosition;

        /// <summary>占用槽位并重置全部运行期状态。</summary>
        internal void Occupy(
            int generation,
            UnitHandle owner,
            in Vec2 position,
            in Vec2 velocity,
            int damage,
            GameplayTagMask tags)
        {
            Active = true;
            Generation = generation;
            Owner = owner;
            Position = position;
            PreviousPosition = position;
            Velocity = velocity;
            Damage = damage;
            Tags = tags;
        }

        /// <summary>释放槽位，保留 <see cref="Generation"/> 以便下次复用自增。</summary>
        internal void Release()
        {
            Active = false;
            Owner = default;
            Velocity = Vec2.Zero;
            PreviousPosition = Vec2.Zero;
            Damage = 0;
            Tags = GameplayTagMask.Empty;
        }

        /// <summary>生成句柄（无分配）。</summary>
        internal ProjectileHandle ToHandle(int slotIndex)
        {
            return new ProjectileHandle(slotIndex, Generation);
        }

        /// <summary>生成只读视图快照（无分配）。</summary>
        internal ProjectileReadView ToView(int slotIndex)
        {
            return new ProjectileReadView(ToHandle(slotIndex), Owner, Position, Velocity, Damage, Tags);
        }
    }
}
