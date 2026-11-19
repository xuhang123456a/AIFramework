#nullable enable
using System;

namespace AIFramework.Contracts.Gameplay
{
    /// <summary>战场单位句柄：纯值类型，禁止用引用包装做键。</summary>
    public readonly struct UnitHandle : IEquatable<UnitHandle>
    {
        public UnitHandle(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        public int Index { get; }

        /// <summary>世代号，用于识别槽位复用后的失效句柄。</summary>
        public int Generation { get; }

        public bool IsValid
        {
            get { return Index >= 0 && Generation > 0; }
        }

        public bool Equals(UnitHandle other)
        {
            return Index == other.Index && Generation == other.Generation;
        }

        public override bool Equals(object? obj)
        {
            return obj is UnitHandle other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Index * 397) ^ Generation;
        }

        public static bool operator ==(UnitHandle a, UnitHandle b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(UnitHandle a, UnitHandle b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return "Unit(" + Index + "#" + Generation + ")";
        }
    }

    /// <summary>飞行物句柄：纯值类型，供反射/打断协议传递。</summary>
    public readonly struct ProjectileHandle : IEquatable<ProjectileHandle>
    {
        public ProjectileHandle(int index, int generation)
        {
            Index = index;
            Generation = generation;
        }

        public int Index { get; }

        public int Generation { get; }

        public bool IsValid
        {
            get { return Index >= 0 && Generation > 0; }
        }

        public bool Equals(ProjectileHandle other)
        {
            return Index == other.Index && Generation == other.Generation;
        }

        public override bool Equals(object? obj)
        {
            return obj is ProjectileHandle other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (Index * 397) ^ Generation;
        }

        public static bool operator ==(ProjectileHandle a, ProjectileHandle b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(ProjectileHandle a, ProjectileHandle b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return "Proj(" + Index + "#" + Generation + ")";
        }
    }

    /// <summary>玩法标签：纯数值标识，跨英雄/跨切片的仲裁唯一依据。</summary>
    public readonly struct GameplayTag : IEquatable<GameplayTag>
    {
        public static readonly GameplayTag None = new GameplayTag(0);

        public GameplayTag(int id)
        {
            Id = id;
        }

        public int Id { get; }

        public bool IsNone
        {
            get { return Id == 0; }
        }

        public bool Equals(GameplayTag other)
        {
            return Id == other.Id;
        }

        public override bool Equals(object? obj)
        {
            return obj is GameplayTag other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Id;
        }

        public static bool operator ==(GameplayTag a, GameplayTag b)
        {
            return a.Id == b.Id;
        }

        public static bool operator !=(GameplayTag a, GameplayTag b)
        {
            return a.Id != b.Id;
        }

        public override string ToString()
        {
            return "Tag(" + Id + ")";
        }
    }

    /// <summary>
    /// 64 位标签掩码。标签按 <c>Id &amp; 63</c> 落位，够 Phase 1 使用；
    /// 全部为位运算，零分配。
    /// </summary>
    public readonly struct GameplayTagMask : IEquatable<GameplayTagMask>
    {
        public static readonly GameplayTagMask Empty = new GameplayTagMask(0UL);

        public GameplayTagMask(ulong bits)
        {
            Bits = bits;
        }

        public ulong Bits { get; }

        public bool IsEmpty
        {
            get { return Bits == 0UL; }
        }

        public static ulong ToBit(GameplayTag tag)
        {
            return tag.IsNone ? 0UL : 1UL << (tag.Id & 63);
        }

        public bool Has(GameplayTag tag)
        {
            ulong bit = ToBit(tag);
            return bit != 0UL && (Bits & bit) == bit;
        }

        public GameplayTagMask Add(GameplayTag tag)
        {
            return new GameplayTagMask(Bits | ToBit(tag));
        }

        public GameplayTagMask Remove(GameplayTag tag)
        {
            return new GameplayTagMask(Bits & ~ToBit(tag));
        }

        public bool HasAll(GameplayTagMask other)
        {
            return (Bits & other.Bits) == other.Bits;
        }

        public bool HasAny(GameplayTagMask other)
        {
            return (Bits & other.Bits) != 0UL;
        }

        public bool Equals(GameplayTagMask other)
        {
            return Bits == other.Bits;
        }

        public override bool Equals(object? obj)
        {
            return obj is GameplayTagMask other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Bits.GetHashCode();
        }

        public static bool operator ==(GameplayTagMask a, GameplayTagMask b)
        {
            return a.Bits == b.Bits;
        }

        public static bool operator !=(GameplayTagMask a, GameplayTagMask b)
        {
            return a.Bits != b.Bits;
        }
    }

    /// <summary>
    /// 全局标签常量。<b>禁止在技能代码里按具体英雄类名写特判</b>，一律按标签仲裁。
    /// </summary>
    public static class GameplayTags
    {
        /// <summary>不可被反弹的飞行物。</summary>
        public static readonly GameplayTag ProjUnreflectable = new GameplayTag(101);

        /// <summary>已被反射过的飞行物（防止二次反射）。</summary>
        public static readonly GameplayTag ProjReflected = new GameplayTag(102);

        /// <summary>单位处于护盾状态。</summary>
        public static readonly GameplayTag UnitShieldActive = new GameplayTag(201);

        /// <summary>本局发生过护盾反射（写进结算标签列表）。</summary>
        public static readonly GameplayTag EventShieldReflected = new GameplayTag(301);
    }
}
