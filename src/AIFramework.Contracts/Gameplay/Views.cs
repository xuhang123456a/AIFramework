using AIFramework.Contracts.Numerics;
using System;

namespace AIFramework.Contracts.Gameplay
{
    /// <summary>单位只读视图：帧内只允许读这份快照，不得持有战场内部结构。</summary>
    public readonly struct UnitReadView
    {
        public UnitReadView(
            UnitHandle handle,
            Vec2 position,
            float radius,
            int armorLevel,
            int hp,
            GameplayTagMask tags)
        {
            Handle = handle;
            Position = position;
            Radius = radius;
            ArmorLevel = armorLevel;
            Hp = hp;
            Tags = tags;
        }

        public UnitHandle Handle { get; }

        public Vec2 Position { get; }

        /// <summary>判定半径，空间查询按中心距 + 半径判断。</summary>
        public float Radius { get; }

        /// <summary>霸体等级：低于打断力的行为会被打断。</summary>
        public int ArmorLevel { get; }

        public int Hp { get; }

        public GameplayTagMask Tags { get; }
    }

    /// <summary>飞行物只读视图。</summary>
    public readonly struct ProjectileReadView
    {
        public ProjectileReadView(
            ProjectileHandle handle,
            UnitHandle owner,
            Vec2 position,
            Vec2 velocity,
            int damage,
            GameplayTagMask tags)
        {
            Handle = handle;
            Owner = owner;
            Position = position;
            Velocity = velocity;
            Damage = damage;
            Tags = tags;
        }

        public ProjectileHandle Handle { get; }

        /// <summary>当前归属者；反射后改变。</summary>
        public UnitHandle Owner { get; }

        public Vec2 Position { get; }

        public Vec2 Velocity { get; }

        public int Damage { get; }

        public GameplayTagMask Tags { get; }
    }

    /// <summary>单位世界只读视图：只允许 <c>out</c> 与值类型返回，禁止返回集合。</summary>
    public interface IUnitWorldReadView
    {
        int Count { get; }

        UnitReadView this[int index] { get; }

        bool TryGetUnit(UnitHandle handle, out UnitReadView view);
    }

    /// <summary>飞行物世界只读视图。</summary>
    public interface IProjectileWorldReadView
    {
        int Count { get; }

        ProjectileReadView this[int index] { get; }

        bool TryGetProjectile(ProjectileHandle handle, out ProjectileReadView view);
    }

    /// <summary>
    /// 零分配空间查询端口：结果写入调用方提供的 <see cref="Span{T}"/>，禁止返回数组或 <c>List&lt;T&gt;</c>。
    /// </summary>
    public interface ISpatialQuery
    {
        /// <summary>查询圆内单位，返回值是实际写入数量；容量不足按容量截断，不扩容、不抛异常。</summary>
        int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results);

        /// <summary>查询圆内飞行物，语义同上。</summary>
        int QueryProjectilesCircle(in Vec2 center, float radius, Span<ProjectileHandle> results);
    }
}
