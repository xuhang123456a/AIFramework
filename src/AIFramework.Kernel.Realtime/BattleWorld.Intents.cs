#nullable enable
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// <see cref="IIntentWriter"/> 实现与意图缓冲：帧内唯一写入口，
    /// 四个显式重载各自写入<b>定长</b>缓冲，满则丢弃并计溢出（不扩容、不抛异常）。
    /// </summary>
    public sealed partial class BattleWorld
    {
        /// <summary>控制意图缓冲容量。</summary>
        private const int ControlIntentCapacity = 32;

        /// <summary>位移意图缓冲容量。</summary>
        private const int MoveIntentCapacity = 32;

        /// <summary>伤害意图缓冲容量。</summary>
        private const int DamageIntentCapacity = 64;

        /// <summary>反射意图缓冲容量。</summary>
        private const int ReflectIntentCapacity = 32;

        /// <summary>提交控制意图（定身 / 沉默 / 缴械等），Phase 1 统一结算进单位状态。</summary>
        public void Submit(in ControlIntent intent)
        {
            _controlIntents.Add(intent);
        }

        /// <summary>提交位移意图，Phase 1 暂存、Phase 5 统一落位。</summary>
        public void Submit(in MoveIntent intent)
        {
            _moveIntents.Add(intent);
        }

        /// <summary>提交伤害意图，Phase 4 统一仲裁（含打断判定）。</summary>
        public void Submit(in DamageIntent intent)
        {
            _damageIntents.Add(intent);
        }

        /// <summary>
        /// 提交反射意图，Phase 4 统一仲裁：改归属、反向、打 <see cref="GameplayTags.ProjReflected"/> 标签。
        /// 已带该标签的飞行物不会被二次反射。
        /// </summary>
        public void Submit(in ReflectProjectileIntent intent)
        {
            _reflectIntents.Add(intent);
        }

        /// <summary>
        /// 把尚未消费的控制意图结算进单位状态（Phase 1 首轮 / Phase 5 补读 Phase 3 期间新提交的意图）。
        /// </summary>
        private void ApplyPendingControlIntents()
        {
            for (int i = _controlCursor; i < _controlIntents.Count; i++)
            {
                ref ControlIntent intent = ref _controlIntents.At(i);
                int slot = ResolveUnitSlot(intent.Unit);
                if (slot < 0)
                {
                    continue;
                }

                ref UnitSlot unit = ref _units[slot];
                unit.ControlFlags |= intent.ControlFlags;
                if (intent.DurationFrames > unit.ControlRemainingFrames)
                {
                    unit.ControlRemainingFrames = intent.DurationFrames;
                }

                _appliedControlIntentCount++;
            }

            _controlCursor = _controlIntents.Count;
        }

        /// <summary>
        /// 把尚未消费的位移意图暂存进单位的挂起位移（不直接改坐标），Phase 5 统一落位。
        /// </summary>
        private void StagePendingMoveIntents()
        {
            for (int i = _moveCursor; i < _moveIntents.Count; i++)
            {
                ref MoveIntent intent = ref _moveIntents.At(i);
                int slot = ResolveUnitSlot(intent.Unit);
                if (slot < 0)
                {
                    continue;
                }

                ref UnitSlot unit = ref _units[slot];
                unit.PendingMoveDelta = unit.PendingMoveDelta + intent.DeltaPosition;
                unit.HasPendingMove = true;
                _stagedMoveIntentCount++;
            }

            _moveCursor = _moveIntents.Count;
        }

        /// <summary>清空四类意图缓冲与游标（Phase 6）。</summary>
        private void ClearIntentBuffers()
        {
            _controlIntents.Clear();
            _moveIntents.Clear();
            _damageIntents.Clear();
            _reflectIntents.Clear();
            _controlCursor = 0;
            _moveCursor = 0;
            _damageCursor = 0;
            _reflectCursor = 0;
        }
    }
}
