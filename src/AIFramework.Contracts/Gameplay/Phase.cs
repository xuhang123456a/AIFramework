#nullable enable
namespace AIFramework.Contracts.Gameplay
{
    /// <summary>
    /// 管线阶段上下文：全部为只读视图与端口引用，按 <c>in</c> 传递避免拷贝。
    /// 切片只能通过它读取世界状态，写入一律走 <see cref="IIntentWriter"/>。
    /// </summary>
    public readonly struct PhaseContext
    {
        public PhaseContext(
            int frameId,
            in UnitReadView owner,
            IUnitWorldReadView world,
            IProjectileWorldReadView projectiles,
            ISpatialQuery spatial)
        {
            FrameId = frameId;
            Owner = owner;
            World = world;
            Projectiles = projectiles;
            Spatial = spatial;
        }

        /// <summary>当前逻辑帧号，从 0 开始。</summary>
        public int FrameId { get; }

        /// <summary>本阶段施法者快照。</summary>
        public UnitReadView Owner { get; }

        public IUnitWorldReadView World { get; }

        public IProjectileWorldReadView Projectiles { get; }

        /// <summary>空间查询端口：技能切片按半径找飞行物/单位，零分配。</summary>
        public ISpatialQuery Spatial { get; }
    }

    /// <summary>
    /// 技能交互阶段。由业务切片实现（如 <c>ShieldReflectSkillStep</c>），
    /// 由强实时内核在固定管线内按帧驱动；实现方必须是纯逻辑代码，禁止持有时序/渲染引用。
    /// </summary>
    public interface ISkillInteractionPhase
    {
        void Execute(in PhaseContext context, IIntentWriter writer);
    }
}
