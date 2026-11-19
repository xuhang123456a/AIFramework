#nullable enable
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 单位槽位：纯数据（无引用成员、无类实例），预分配在 <see cref="BattleWorld"/> 内部数组里，
    /// 帧内只做原地读写与 <see cref="ToView"/> 值拷贝，不产生托管堆分配。
    /// </summary>
    internal struct UnitSlot
    {
        /// <summary>槽位是否被占用。</summary>
        internal bool Active;

        /// <summary>世代号：槽位每次复用自增，用于识别失效句柄。</summary>
        internal int Generation;

        internal Vec2 Position;

        internal float Radius;

        /// <summary>霸体等级。</summary>
        internal int ArmorLevel;

        internal int Hp;

        internal GameplayTagMask Tags;

        /// <summary>控制位（定身 / 沉默 / 缴械等按位组合），Phase 1 仅记录状态。</summary>
        internal int ControlFlags;

        /// <summary>控制剩余帧数，归零时清空 <see cref="ControlFlags"/>。</summary>
        internal int ControlRemainingFrames;

        /// <summary>Buff 剩余帧数（Phase 1 占位计时器，由 Phase 2 递减）。</summary>
        internal int BuffRemainingFrames;

        /// <summary>被打断后的硬直剩余帧数（由 Phase 2 递减）。</summary>
        internal int InterruptRemainingFrames;

        /// <summary>Phase 1 暂存的位移意图累加值，由 Phase 5 统一落位。</summary>
        internal Vec2 PendingMoveDelta;

        /// <summary>是否存在待结算的挂起位移（区分"没有位移意图"与"零位移意图"）。</summary>
        internal bool HasPendingMove;

        /// <summary>占用槽位并重置全部运行期状态。</summary>
        internal void Occupy(int generation, in Vec2 position, float radius, int armorLevel, int hp, GameplayTagMask tags)
        {
            Active = true;
            Generation = generation;
            Position = position;
            Radius = radius;
            ArmorLevel = armorLevel;
            Hp = hp;
            Tags = tags;
            ControlFlags = 0;
            ControlRemainingFrames = 0;
            BuffRemainingFrames = 0;
            InterruptRemainingFrames = 0;
            PendingMoveDelta = Vec2.Zero;
            HasPendingMove = false;
        }

        /// <summary>释放槽位，保留 <see cref="Generation"/> 以便下次复用自增。</summary>
        internal void Release()
        {
            Active = false;
            Hp = 0;
            Tags = GameplayTagMask.Empty;
            ControlFlags = 0;
            ControlRemainingFrames = 0;
            BuffRemainingFrames = 0;
            InterruptRemainingFrames = 0;
            PendingMoveDelta = Vec2.Zero;
            HasPendingMove = false;
        }

        /// <summary>生成句柄（无分配）。</summary>
        internal UnitHandle ToHandle(int slotIndex)
        {
            return new UnitHandle(slotIndex, Generation);
        }

        /// <summary>
        /// 生成只读视图快照（无分配）。阶段只能拿到这个值拷贝，
        /// 拿不到槽位本身，从物理上禁止帧内篡改状态。
        /// </summary>
        internal UnitReadView ToView(int slotIndex)
        {
            return new UnitReadView(ToHandle(slotIndex), Position, Radius, ArmorLevel, Hp, Tags);
        }
    }
}
