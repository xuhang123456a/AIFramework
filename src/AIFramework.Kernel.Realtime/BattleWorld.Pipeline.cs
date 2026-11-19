#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 固定 6 阶段管线：<see cref="Step(float)"/> 的每一帧顺序在这里写死，禁止在别处重排或跳阶段。
    /// 全部方法内部只用 <c>for</c> + 下标，不碰 LINQ / 闭包 / <c>params</c> / 装箱 / 字符串。
    /// </summary>
    public sealed partial class BattleWorld
    {
        // ────────────────────────────────────────────────────────────────
        // Phase 1 — 输入采集
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 阶段 1：把外部 <see cref="ControlIntent"/> / <see cref="MoveIntent"/> 缓冲结算进单位状态。
        /// Phase 1 只保留缓冲与计数，不驱动真实玩法：控制位写到单位上，位移只暂存到挂起位移。
        /// </summary>
        private void StageInputCollect()
        {
            ApplyPendingControlIntents();
            StagePendingMoveIntents();
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 2 — Buff 步进
        // ────────────────────────────────────────────────────────────────

        /// <summary>阶段 2：递减控制 / Buff / 打断硬直计时器，控制计时归零时清空控制位。</summary>
        private void StageBuffTick()
        {
            for (int i = 0; i < _units.Length; i++)
            {
                if (!_units[i].Active)
                {
                    continue;
                }

                ref UnitSlot unit = ref _units[i];

                if (unit.InterruptRemainingFrames > 0)
                {
                    unit.InterruptRemainingFrames--;
                }

                if (unit.BuffRemainingFrames > 0)
                {
                    unit.BuffRemainingFrames--;
                }

                if (unit.ControlRemainingFrames > 0)
                {
                    unit.ControlRemainingFrames--;
                    if (unit.ControlRemainingFrames == 0)
                    {
                        unit.ControlFlags = 0;
                    }
                }
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 3 — 技能时间轴
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 阶段 3：遍历构造期注入的 <see cref="ISkillInteractionPhase"/>，逐个构造
        /// <see cref="PhaseContext"/> 并调用 <see cref="ISkillInteractionPhase.Execute"/>。
        /// owner 取「带 <see cref="GameplayTags.UnitShieldActive"/> 标签的第一个单位」，没有则取第 0 个单位（首个占用槽位）。
        /// </summary>
        private void StageSkillTimeline()
        {
            int ownerSlot = FindShieldOwnerSlot();
            UnitReadView ownerView = ownerSlot >= 0 ? _units[ownerSlot].ToView(ownerSlot) : default;

            for (int i = 0; i < _phases.Length; i++)
            {
                ISkillInteractionPhase phase = _phases[i];
                if (phase == null)
                {
                    continue;
                }

                PhaseContext context = new PhaseContext(_frameId, in ownerView, this, this, _spatial);
                phase.Execute(in context, this);
            }
        }

        /// <summary>取带护盾标签的首个单位槽位；没有则退回首个占用槽位；空战场返回 -1。</summary>
        private int FindShieldOwnerSlot()
        {
            for (int i = 0; i < _units.Length; i++)
            {
                if (_units[i].Active && _units[i].Tags.Has(GameplayTags.UnitShieldActive))
                {
                    return i;
                }
            }

            for (int i = 0; i < _units.Length; i++)
            {
                if (_units[i].Active)
                {
                    return i;
                }
            }

            return -1;
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 4 — 空间碰撞与伤害仲裁（反射仲裁也落在这一步）
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 阶段 4：飞行物积分前移 → 反射意图统一仲裁 → 空间命中与伤害结算 → 显式伤害意图结算。
        /// </summary>
        private void StageCollisionAndDamage(float deltaSeconds)
        {
            for (int i = 0; i < _projectiles.Length; i++)
            {
                ref ProjectileSlot projectile = ref _projectiles[i];
                if (!projectile.Active)
                {
                    continue;
                }

                projectile.PreviousPosition = projectile.Position;
                projectile.Position = projectile.Position + (projectile.Velocity * deltaSeconds);

                // 出界回收：避免打空的弹幕永久占槽（契约没有 despawn 通道，只能由内核按边界回收）。
                if (Math.Abs(projectile.Position.X) > ProjectileDespawnRadius
                    || Math.Abs(projectile.Position.Y) > ProjectileDespawnRadius)
                {
                    RecycleProjectile(i);
                }
            }

            ArbitrateReflectIntents();
            ResolveProjectileHits();
            ResolveDamageIntents();
        }

        /// <summary>
        /// 阶段 4 的反射统一仲裁：改归属 → 速度取反 → 打 <see cref="GameplayTags.ProjReflected"/> 标签 →
        /// <see cref="ReflectedCount"/> 自增 → 记录 <see cref="GameplayTags.EventShieldReflected"/>。
        /// 带 <see cref="GameplayTags.ProjReflected"/> 标签的飞行物不会被二次反射；
        /// 带 <see cref="GameplayTags.ProjUnreflectable"/> 标签的飞行物由内核兜底拒绝（切片侧仍应先过滤，双保险）。
        /// </summary>
        private void ArbitrateReflectIntents()
        {
            for (int i = _reflectCursor; i < _reflectIntents.Count; i++)
            {
                ref ReflectProjectileIntent intent = ref _reflectIntents.At(i);

                int projectileSlot = ResolveProjectileSlot(intent.Projectile);
                if (projectileSlot < 0)
                {
                    continue;
                }

                ref ProjectileSlot projectile = ref _projectiles[projectileSlot];
                if (projectile.Tags.Has(GameplayTags.ProjReflected)
                    || projectile.Tags.Has(GameplayTags.ProjUnreflectable))
                {
                    continue;
                }

                int reflectorSlot = ResolveUnitSlot(intent.Reflector);
                if (reflectorSlot < 0)
                {
                    continue;
                }

                projectile.Owner = _units[reflectorSlot].ToHandle(reflectorSlot);
                projectile.Velocity = new Vec2(-projectile.Velocity.X, -projectile.Velocity.Y);

                // 反射发生在积分之后：把扫掠起点拉到当前位置，避免这一帧用"反射前"的轨迹误判命中。
                projectile.PreviousPosition = projectile.Position;
                projectile.Tags = projectile.Tags.Add(GameplayTags.ProjReflected);

                _reflectedCount++;
                _triggeredTags.Add(GameplayTags.EventShieldReflected);
            }

            _reflectCursor = _reflectIntents.Count;
        }

        /// <summary>
        /// 阶段 4 的空间命中：对每个飞行物用 <see cref="ISpatialQuery.QueryCircle"/>（栈上缓冲）取候选单位，
        /// 再按「点到扫掠线段距离 &lt;= 单位半径 + 飞行物半径」精确判定。
        /// 命中最近的一个单位 → 结算伤害 → 立即回收飞行物槽位。
        /// </summary>
        private void ResolveProjectileHits()
        {
            Span<UnitHandle> hitBuffer = stackalloc UnitHandle[MaxUnitsPerQuery];

            for (int i = 0; i < _projectiles.Length; i++)
            {
                ref ProjectileSlot projectile = ref _projectiles[i];
                if (!projectile.Active)
                {
                    continue;
                }

                Vec2 start = projectile.PreviousPosition;
                Vec2 end = projectile.Position;
                float sweepDistance = Vec2.Distance(in start, in end);
                Vec2 center = new Vec2((start.X + end.X) * 0.5f, (start.Y + end.Y) * 0.5f);
                float queryRadius = ((sweepDistance * 0.5f) + _maxUnitRadius) + ProjectileRadius;

                int hitCount = _spatial.QueryCircle(in center, queryRadius, hitBuffer);
                if (hitCount > hitBuffer.Length)
                {
                    hitCount = hitBuffer.Length;
                }

                if (hitCount <= 0)
                {
                    continue;
                }

                int targetSlot = -1;
                float nearest = float.MaxValue;

                for (int h = 0; h < hitCount; h++)
                {
                    int unitSlot = ResolveUnitSlot(hitBuffer[h]);
                    if (unitSlot < 0)
                    {
                        continue;
                    }

                    ref UnitSlot unit = ref _units[unitSlot];
                    if (unit.Hp <= 0)
                    {
                        continue;
                    }

                    // 不伤发射者（反射后 Owner 已换成反射者，因此反射弹可以打回原施法者）。
                    if (unitSlot == projectile.Owner.Index && unit.Generation == projectile.Owner.Generation)
                    {
                        continue;
                    }

                    float distance = DistancePointToSegment(in unit.Position, in start, in end);
                    if (distance > unit.Radius + ProjectileRadius)
                    {
                        continue;
                    }

                    if (distance < nearest)
                    {
                        nearest = distance;
                        targetSlot = unitSlot;
                    }
                }

                if (targetSlot < 0)
                {
                    continue;
                }

                UnitHandle owner = projectile.Owner;
                int damage = projectile.Damage;
                RecycleProjectile(i);
                ApplyDamage(owner, targetSlot, damage, damage);
            }
        }

        /// <summary>阶段 4 的显式伤害意图结算（含打断仲裁）。</summary>
        private void ResolveDamageIntents()
        {
            for (int i = _damageCursor; i < _damageIntents.Count; i++)
            {
                ref DamageIntent intent = ref _damageIntents.At(i);

                int targetSlot = ResolveUnitSlot(intent.Target);
                if (targetSlot < 0)
                {
                    continue;
                }

                if (_units[targetSlot].Hp <= 0)
                {
                    continue;
                }

                ApplyDamage(intent.Source, targetSlot, intent.Amount, intent.BreakPower);
                _settledDamageIntentCount++;
            }

            _damageCursor = _damageIntents.Count;
        }

        /// <summary>
        /// 统一伤害结算：先用 <see cref="SkillInterruptResolver"/> 仲裁打断（打断则计 <see cref="InterruptedCount"/>），
        /// 再扣血并把 HP 截到 0。
        /// </summary>
        private void ApplyDamage(UnitHandle source, int targetSlot, int amount, int breakPower)
        {
            if (targetSlot < 0 || targetSlot >= _units.Length)
            {
                return;
            }

            ref UnitSlot target = ref _units[targetSlot];
            if (!target.Active)
            {
                return;
            }

            if (SkillInterruptResolver.ShouldInterrupt(target.ArmorLevel, breakPower))
            {
                target.InterruptRemainingFrames = InterruptControlFrames;
                target.ControlFlags |= InterruptControlFlags;
                if (target.ControlRemainingFrames < InterruptControlFrames)
                {
                    target.ControlRemainingFrames = InterruptControlFrames;
                }

                _interruptedCount++;
            }

            if (amount <= 0)
            {
                return;
            }

            int remainingHp = target.Hp - amount;
            target.Hp = remainingHp > 0 ? remainingHp : 0;
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 5 — 移动与控制权仲裁
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 阶段 5：补读 Phase 3 期间新提交的控制 / 位移意图，然后把所有挂起位移一次性落位。
        /// Phase 1 契约只要求"保留缓冲与计数"，因此控制位<b>不</b>阻断位移结算（真实定身规则留给后续阶段）。
        /// </summary>
        private void StageMovementArbitration()
        {
            ApplyPendingControlIntents();
            StagePendingMoveIntents();

            for (int i = 0; i < _units.Length; i++)
            {
                ref UnitSlot unit = ref _units[i];
                if (!unit.Active || !unit.HasPendingMove)
                {
                    continue;
                }

                unit.Position = unit.Position + unit.PendingMoveDelta;
                unit.PendingMoveDelta = Vec2.Zero;
                unit.HasPendingMove = false;
                _committedMoveCount++;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Phase 6 — 清理与快照输出
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 阶段 6：回收失效槽位（含飞行物计数一致性兜底）→ 清空意图缓冲计数 → 重置本帧诊断计数 → 递增 <see cref="FrameId"/>。
        /// 快照输出不落缓冲：<see cref="IUnitWorldReadView"/> / <see cref="IProjectileWorldReadView"/> 按需即时生成值拷贝视图，
        /// 因此"快照"这一环本身也是零分配的。
        /// </summary>
        private void StageCleanupAndSnapshot()
        {
            int aliveProjectiles = 0;
            for (int i = 0; i < _projectiles.Length; i++)
            {
                if (_projectiles[i].Active)
                {
                    aliveProjectiles++;
                }
            }

            _projectileCount = aliveProjectiles;

            ClearIntentBuffers();

            _appliedControlIntentCount = 0;
            _stagedMoveIntentCount = 0;
            _committedMoveCount = 0;
            _settledDamageIntentCount = 0;

            _frameId++;
        }
    }
}
