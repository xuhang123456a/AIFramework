#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 实时战斗的"场景表现绑定器"：把内核布场出来的单位与飞行物变成场景里的碰撞体对象，
    /// 并逐帧把位置同步到 Transform。
    /// <para>
    /// <b>为什么必须存在</b>：内核的出伤判定走注入的 <see cref="Contracts.Ports.ISpatialQuery"/>，
    /// 而 Unity 侧的 <see cref="UnitySpatialQuery"/> 只认**登记过的 Collider**。
    /// 以往各个战斗场景只做数据布场、不建碰撞体，结果是飞行物穿过目标、既不反射也不掉血——
    /// 场景"能跑"但"不打架"。本类把这层粘合收敛到一处。
    /// </para>
    /// <para>
    /// 用法：布场后 <see cref="AddUnit"/>/<see cref="AddProjectile"/> 建对象，
    /// 每帧 <c>Step</c> 之后调用 <see cref="Sync"/>。
    /// </para>
    /// </summary>
    public sealed class RealtimeSceneBinder : IDisposable
    {
        private const float MinScale = 0.05f;

        private readonly AIFrameworkUnityHost _host;
        private readonly IUnitWorldReadView _units;
        private readonly IProjectileWorldReadView _projectiles;
        private readonly Transform _root;
        private readonly List<Binding> _bindings = new List<Binding>();

        private bool _disposed;

        /// <summary>构造。</summary>
        /// <param name="host">宿主（用于取空间查询端口）。</param>
        /// <param name="units">单位只读视图。</param>
        /// <param name="projectiles">飞行物只读视图。</param>
        /// <param name="root">生成对象的父节点；为 <c>null</c> 时放在场景根。</param>
        public RealtimeSceneBinder(
            AIFrameworkUnityHost host,
            IUnitWorldReadView units,
            IProjectileWorldReadView projectiles,
            Transform? root = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _units = units ?? throw new ArgumentNullException(nameof(units));
            _projectiles = projectiles ?? throw new ArgumentNullException(nameof(projectiles));
            _root = root!;
        }

        /// <summary>当前存活绑定数量（单位 + 飞行物）。</summary>
        public int LiveCount
        {
            get { return _bindings.Count; }
        }

        /// <summary>登记一个单位：建胶囊体、缩放成半径、登记碰撞体。</summary>
        /// <param name="handle">单位句柄。</param>
        /// <param name="position">初始位置。</param>
        /// <param name="radius">半径（决定胶囊缩放）。</param>
        /// <param name="name">对象名（排查用）。</param>
        public GameObject AddUnit(UnitHandle handle, in Vec2 position, float radius, string name)
        {
            GameObject go = CreateBody(name, PrimitiveType.Capsule, radius, new Color(0.35f, 0.75f, 1f, 1f));
            go.transform.position = ToWorld(position);

            Collider collider = go.GetComponent<Collider>();
            _host.Spatial.RegisterCollider(collider, handle);

            _bindings.Add(new Binding(go, collider, isProjectile: false, handle, projectileHandle: default));
            return go;
        }

        /// <summary>登记一个飞行物：建小球、缩放成半径、登记飞行物碰撞体。</summary>
        /// <param name="handle">飞行物句柄。</param>
        /// <param name="position">初始位置。</param>
        /// <param name="radius">半径。</param>
        /// <param name="name">对象名（排查用）。</param>
        public GameObject AddProjectile(ProjectileHandle handle, in Vec2 position, float radius, string name)
        {
            GameObject go = CreateBody(name, PrimitiveType.Sphere, radius, new Color(1f, 0.6f, 0.2f, 1f));
            go.transform.position = ToWorld(position);

            Collider collider = go.GetComponent<Collider>();
            _host.Spatial.RegisterProjectileCollider(collider, handle);

            _bindings.Add(new Binding(go, collider, isProjectile: true, unitHandle: default, projectileHandle: handle));
            return go;
        }

        /// <summary>
        /// 逐帧同步：把视图里的最新位置写到 Transform；已经从内核消失的对象销毁并注销。
        /// 位置同步必须发生在 <c>Step</c> 之后、物理查询之前（Unity 在下一次物理查询前会自动同步 Transform）。
        /// </summary>
        public void Sync()
        {
            if (_disposed)
            {
                return;
            }

            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                Binding binding = _bindings[i];

                if (binding.IsProjectile)
                {
                    if (_projectiles.TryGetProjectile(binding.ProjectileHandle, out ProjectileReadView view))
                    {
                        binding.GameObject.transform.position = ToWorld(view.Position);
                        continue;
                    }
                }
                else
                {
                    if (_units.TryGetUnit(binding.UnitHandle, out UnitReadView view))
                    {
                        binding.GameObject.transform.position = ToWorld(view.Position);
                        continue;
                    }
                }

                RemoveAt(i);
            }
        }

        /// <summary>移除全部绑定、注销碰撞体并销毁对象。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                RemoveAt(i);
            }

            _bindings.Clear();
        }

        private void RemoveAt(int index)
        {
            Binding binding = _bindings[index];
            _bindings.RemoveAt(index);

            if (binding.Collider != null)
            {
                _host.Spatial.Unregister(binding.Collider);
            }

            if (binding.GameObject != null)
            {
                UnityEngine.Object.Destroy(binding.GameObject);
            }
        }

        private GameObject CreateBody(string name, PrimitiveType type, float radius, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);

            Transform? parent = _root;
            if (parent != null)
            {
                go.transform.SetParent(parent, false);
            }

            float scale = MathfMax(MinScale, radius * 2f);
            go.transform.localScale = new Vector3(scale, scale, scale);
            go.name = name;

            Renderer? renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }

            return go;
        }

        private static float MathfMax(float a, float b)
        {
            return a > b ? a : b;
        }

        private static Vector3 ToWorld(in Vec2 planar)
        {
            // 战斗平面是 X-Z；Unity 的 y 轴向上。
            return new Vector3(planar.X, 0f, planar.Y);
        }

        private struct Binding
        {
            internal Binding(GameObject go, Collider collider, bool isProjectile, UnitHandle unitHandle, ProjectileHandle projectileHandle)
            {
                GameObject = go;
                Collider = collider;
                IsProjectile = isProjectile;
                UnitHandle = unitHandle;
                ProjectileHandle = projectileHandle;
            }

            internal GameObject GameObject { get; }

            internal Collider Collider { get; }

            internal bool IsProjectile { get; }

            internal UnitHandle UnitHandle { get; }

            internal ProjectileHandle ProjectileHandle { get; }
        }
    }
}
