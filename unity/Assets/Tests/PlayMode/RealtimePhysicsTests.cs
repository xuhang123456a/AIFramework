using System.Collections;
using AIFramework.Adapters.Unity;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIFramework.Tests.PlayMode
{
    /// <summary>
    /// 实时战斗的物理链路验收：只有把单位与飞行物变成**已登记碰撞体**，
    /// <see cref="ISpatialQuery"/> 才能查出命中，护盾反射才会真正发生。
    /// <para>
    /// 这是 EditMode 测试覆盖不到的部分——EditMode 下没有物理帧，碰撞体不会参与重叠查询。
    /// 用例直接跑内核 + <see cref="RealtimeSceneBinder"/>，不需要预制场景。
    /// </para>
    /// </summary>
    public sealed class RealtimePhysicsTests
    {
        [UnityTest]
        public IEnumerator Binder_RegistersColliders_SoProjectileReflectsOffShield()
        {
            var host = new AIFrameworkUnityHost(storageSubDirectory: "aifw-playmode-tests");
            host.Initialize();

            try
            {
                var init = new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 2);
                IGameplaySession session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);

                IRealtimeSandbox sandbox = host.Sessions.Current as IRealtimeSandbox;
                Assert.IsNotNull(sandbox, "Realtime 会话未实现 IRealtimeSandbox。");

                IUnitWorldReadView units = session as IUnitWorldReadView;
                IProjectileWorldReadView projectiles = session as IProjectileWorldReadView;
                Assert.IsNotNull(units, "会话未实现 IUnitWorldReadView。");
                Assert.IsNotNull(projectiles, "会话未实现 IProjectileWorldReadView。");

                using var binder = new RealtimeSceneBinder(host, units, projectiles);

                // 护盾单位在原点；攻击者在 +Z 3 处朝 -Z 发射。
                GameplayTagMask shieldTags = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
                UnitHandle shield = sandbox.SpawnUnit(new Vec2(0f, 0f), 1f, 5, 100, shieldTags);
                UnitHandle attacker = sandbox.SpawnUnit(new Vec2(0f, 3f), 1f, 1, 5, GameplayTagMask.Empty);
                ProjectileHandle projectile = sandbox.SpawnProjectile(
                    attacker, new Vec2(0f, 1.4f), new Vec2(0f, -6f), 10, GameplayTagMask.Empty);

                binder.AddUnit(shield, new Vec2(0f, 0f), 1f, "ShieldUnit");
                binder.AddUnit(attacker, new Vec2(0f, 3f), 1f, "AttackerUnit");
                binder.AddProjectile(projectile, new Vec2(0f, 1.4f), 0.2f, "Projectile");

                // 等一帧让新碰撞体真正进入物理世界（Unity 在新建 Collider 后需要一次同步）。
                yield return null;

                int frames = 0;
                while (!session.IsFinished && frames < 600)
                {
                    session.Step(1f / 60f);
                    binder.Sync();
                    frames++;
                    yield return null;
                }

                Assert.IsTrue(session.IsFinished, "对局在 600 帧内没有结束。");
                Assert.GreaterOrEqual(
                    sandbox.ReflectedCount,
                    1,
                    "护盾反射没有发生：说明碰撞体没有被 ISpatialQuery 查到（Bind 链路断了）。");

                GameplayResult result = host.Sessions.EndAndCollect();
                Assert.IsTrue(result.IsVictory, "护盾方应获胜。");
            }
            finally
            {
                host.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Binder_SyncsProjectileTransforms()
        {
            var host = new AIFrameworkUnityHost(storageSubDirectory: "aifw-playmode-tests");
            host.Initialize();

            try
            {
                var init = new GameplayInitData(levelId: 1, randomSeed: 777, unitCount: 1);
                IGameplaySession session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);

                IRealtimeSandbox sandbox = host.Sessions.Current as IRealtimeSandbox;
                IUnitWorldReadView units = session as IUnitWorldReadView;
                IProjectileWorldReadView projectiles = session as IProjectileWorldReadView;
                Assert.IsNotNull(sandbox);
                Assert.IsNotNull(units);
                Assert.IsNotNull(projectiles);

                using var binder = new RealtimeSceneBinder(host, units, projectiles);

                UnitHandle shooter = sandbox.SpawnUnit(new Vec2(0f, 0f), 1f, 1, 10, GameplayTagMask.Empty);
                ProjectileHandle shot = sandbox.SpawnProjectile(
                    shooter, new Vec2(0f, 0f), new Vec2(0f, 5f), 5, GameplayTagMask.Empty);

                binder.AddUnit(shooter, new Vec2(0f, 0f), 1f, "Shooter");
                GameObject bullet = binder.AddProjectile(shot, new Vec2(0f, 0f), 0.2f, "Bullet");

                float startZ = bullet.transform.position.z;
                for (int i = 0; i < 10; i++)
                {
                    session.Step(1f / 60f);
                    binder.Sync();
                    yield return null;
                }

                Assert.Greater(bullet.transform.position.z, startZ, "飞行物 Transform 没有随内核位置同步。");
                Assert.GreaterOrEqual(binder.LiveCount, 1, "绑定对象被意外清空。");

                host.Sessions.Release();
            }
            finally
            {
                host.Dispose();
            }
        }
    }
}
