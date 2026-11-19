#nullable enable
using AIFramework.Contracts.Numerics;

namespace AIFramework.Contracts.Gameplay
{
    /// <summary>控制权意图：定身、缴械、沉默等按位组合。</summary>
    public readonly struct ControlIntent
    {
        public ControlIntent(UnitHandle unit, int controlFlags, int durationFrames)
        {
            Unit = unit;
            ControlFlags = controlFlags;
            DurationFrames = durationFrames;
        }

        public UnitHandle Unit { get; }

        public int ControlFlags { get; }

        public int DurationFrames { get; }
    }

    /// <summary>位移意图：只提交意图，由管线仲裁器统一结算。</summary>
    public readonly struct MoveIntent
    {
        public MoveIntent(UnitHandle unit, Vec2 deltaPosition)
        {
            Unit = unit;
            DeltaPosition = deltaPosition;
        }

        public UnitHandle Unit { get; }

        public Vec2 DeltaPosition { get; }
    }

    /// <summary>伤害意图。</summary>
    public readonly struct DamageIntent
    {
        public DamageIntent(UnitHandle source, UnitHandle target, int amount, int breakPower)
        {
            Source = source;
            Target = target;
            Amount = amount;
            BreakPower = breakPower;
        }

        public UnitHandle Source { get; }

        public UnitHandle Target { get; }

        public int Amount { get; }

        /// <summary>打断力：与目标霸体等级比较，决定是否被打断。</summary>
        public int BreakPower { get; }
    }

    /// <summary>反弹飞行物意图：切片只能提交意图，改归属与反向由仲裁器执行。</summary>
    public readonly struct ReflectProjectileIntent
    {
        public ReflectProjectileIntent(UnitHandle reflector, ProjectileHandle projectile)
        {
            Reflector = reflector;
            Projectile = projectile;
        }

        /// <summary>施加反射的单位（成为新的归属者）。</summary>
        public UnitHandle Reflector { get; }

        public ProjectileHandle Projectile { get; }
    }

    /// <summary>
    /// 帧内意图写入通道。<b>必须是四个显式重载</b>，不能写成泛型 <c>Submit&lt;T&gt;</c>，
    /// 否则内核要再做一次类型分发，且接口参数会造成装箱。
    /// </summary>
    public interface IIntentWriter
    {
        void Submit(in ControlIntent intent);

        void Submit(in MoveIntent intent);

        void Submit(in DamageIntent intent);

        void Submit(in ReflectProjectileIntent intent);
    }
}
