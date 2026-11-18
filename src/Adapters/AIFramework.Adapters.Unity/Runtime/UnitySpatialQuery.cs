#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 空间查询端口的 Unity 实现：查 Unity 物理，再用"碰撞体 → 句柄"映射把结果翻回纯逻辑句柄。
    /// <para>
    /// <b>为什么两类查询都用 <c>OverlapSphereNonAlloc</c></b>：<c>Physics</c>（3D 物理）里不存在
    /// 接收 <c>Vector2</c> + <c>Collider[]</c> 的圆形查询；带 <c>Vector2</c> 的是 <c>Physics2D</c>，
    /// 它只认 <c>Collider2D</c>，无法与本工程 3D 场景（Capsule 自带 <c>CapsuleCollider</c>）共用一套映射字典。
    /// 因此统一走球体查询，把 <see cref="Vec2"/> 铺在固定的战斗平面上。
    /// </para>
    /// <para>
    /// <b>战斗平面约定</b>：<c>Vec2(X, Y)</c> 映射到世界 <c>Vector3(X, 0, Y)</c>，
    /// 即战斗发生在 Unity 的 <b>X-Z 平面</b>，<see cref="Vec2"/> 的 Y 轴对应世界 Z 轴
    /// （与演示场景把单位摆在 z=0 / z=3 的做法一致）。俯视类玩法改平面只需要动 <see cref="PlaneHeight"/> 与
    /// <see cref="ToWorld"/>。
    /// </para>
    /// <para>
    /// <b>零分配</b>：命中缓冲在构造期一次分配并复用；写入调用方的 <see cref="Span{T}"/>，
    /// 容量不足按容量截断、不扩容、不抛异常。因为缓冲是复用的，本类<b>不可重入</b>：
    /// 一次查询没返回前不允许再发起查询（内核是串行调用，天然满足）。
    /// </para>
    /// </summary>
    public sealed class UnitySpatialQuery : ISpatialQuery
    {
        /// <summary>默认命中缓冲容量（一次查询最多认这么多碰撞体，超出的部分本帧丢弃）。</summary>
        public const int DefaultBufferCapacity = 64;

        /// <summary>默认层遮罩：全部层。</summary>
        public const int AllLayers = ~0;

        /// <summary>战斗平面高度（世界 Y）。单位与飞行物都摆在这个平面上，查询球心也在这里。</summary>
        private const float PlaneHeight = 0f;

        /// <summary>复用的命中缓冲；非重入、不扩容，见类注释。</summary>
        private readonly Collider[] _buffer;

        /// <summary>碰撞体 → 单位句柄。读取只在查询路径（不遍历），写入只在装配/绑定路径。</summary>
        private readonly Dictionary<Collider, UnitHandle> _units;

        /// <summary>碰撞体 → 飞行物句柄。与单位字典分开，避免同一次查询混出两类句柄。</summary>
        private readonly Dictionary<Collider, ProjectileHandle> _projectiles;

        /// <summary>查询层遮罩；默认全部层。为什么要能改：演示场景与正式场景常把装饰物放在独立层上，全层查询会白跑。</summary>
        private readonly int _layerMask;

        /// <summary>使用 <see cref="DefaultBufferCapacity"/> 与 <see cref="AllLayers"/> 构造。</summary>
        public UnitySpatialQuery()
            : this(DefaultBufferCapacity, AllLayers)
        {
        }

        /// <summary>指定命中缓冲容量与层遮罩。</summary>
        /// <param name="bufferCapacity">单次查询最多接收的碰撞体数量，必须大于 0。</param>
        /// <param name="layerMask">参与查询的层遮罩，默认全部层。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> 小于等于 0。</exception>
        public UnitySpatialQuery(int bufferCapacity, int layerMask)
        {
            if (bufferCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bufferCapacity),
                    bufferCapacity,
                    "命中缓冲容量必须大于 0。");
            }

            _buffer = new Collider[bufferCapacity];
            _units = new Dictionary<Collider, UnitHandle>(bufferCapacity);
            _projectiles = new Dictionary<Collider, ProjectileHandle>(bufferCapacity);
            _layerMask = layerMask;
        }

        /// <summary>单次查询的命中缓冲容量（诊断用）。</summary>
        public int BufferCapacity
        {
            get { return _buffer.Length; }
        }

        /// <summary>已登记的单位碰撞体数量。</summary>
        public int RegisteredUnitCount
        {
            get { return _units.Count; }
        }

        /// <summary>已登记的飞行物碰撞体数量。</summary>
        public int RegisteredProjectileCount
        {
            get { return _projectiles.Count; }
        }

        /// <summary>
        /// 登记一个碰撞体对应的单位句柄。<b>句柄只能由内核的 <c>SpawnUnit</c> 产出</b>，
        /// 本方法只做映射，不校验句柄是否仍然有效（世代号失效的句柄由内核自己丢弃）。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="collider"/> 为 <c>null</c>（含已被销毁的引用）。</exception>
        public void RegisterCollider(Collider collider, UnitHandle handle)
        {
            if (collider == null)
            {
                throw new ArgumentNullException(nameof(collider));
            }

            _units[collider] = handle;
        }

        /// <summary>登记一个碰撞体对应的飞行物句柄。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="collider"/> 为 <c>null</c>（含已被销毁的引用）。</exception>
        public void RegisterProjectileCollider(Collider collider, ProjectileHandle handle)
        {
            if (collider == null)
            {
                throw new ArgumentNullException(nameof(collider));
            }

            _projectiles[collider] = handle;
        }

        /// <summary>
        /// 注销碰撞体的全部映射（单位与飞行物两张表一起清）。单位被销毁、对象池回收时必须调用，
        /// 否则字典会随着场景反复加载一直变大。
        /// </summary>
        /// <remarks>键不存在时静默成功；传 <c>null</c> 直接返回（销毁路径上经常拿到伪空引用）。</remarks>
        public void Unregister(Collider collider)
        {
            if (collider == null)
            {
                return;
            }

            _units.Remove(collider);
            _projectiles.Remove(collider);
        }

        /// <summary>清空全部映射。开新一局前必须调用：上一局的句柄从 0 号槽位重新发放，旧映射会串到新单位身上。</summary>
        public void Clear()
        {
            _units.Clear();
            _projectiles.Clear();
        }

        /// <inheritdoc />
        public int QueryCircle(in Vec2 center, float radius, Span<UnitHandle> results)
        {
            if (results.Length == 0 || radius < 0f)
            {
                return 0;
            }

            int hitCount = Overlap(center, radius);
            int written = 0;
            for (int i = 0; i < hitCount; i++)
            {
                if (written >= results.Length)
                {
                    break;
                }

                Collider collider = _buffer[i];

                // 场景里没登记过的碰撞体（地面、装饰物）会被物理查到，这里必须过滤掉。
                // 同时 collider 可能已被销毁：UnityEngine.Object 的 == null 会识别伪空引用，跳过即可。
                if (collider == null)
                {
                    continue;
                }

                UnitHandle handle;
                if (!_units.TryGetValue(collider, out handle))
                {
                    continue;
                }

                results[written] = handle;
                written++;
            }

            return written;
        }

        /// <inheritdoc />
        public int QueryProjectilesCircle(in Vec2 center, float radius, Span<ProjectileHandle> results)
        {
            if (results.Length == 0 || radius < 0f)
            {
                return 0;
            }

            int hitCount = Overlap(center, radius);
            int written = 0;
            for (int i = 0; i < hitCount; i++)
            {
                if (written >= results.Length)
                {
                    break;
                }

                Collider collider = _buffer[i];
                if (collider == null)
                {
                    continue;
                }

                ProjectileHandle handle;
                if (!_projectiles.TryGetValue(collider, out handle))
                {
                    continue;
                }

                results[written] = handle;
                written++;
            }

            return written;
        }

        /// <summary>
        /// 执行一次球体查询，返回命中数量（≤ 缓冲容量）。
        /// 显式传 <see cref="QueryTriggerInteraction.Collide"/>：单位的判定体常被做成 Trigger，
        /// 默认的 <c>UseGlobal</c> 会被工程设置 <c>Physics.queriesHitTriggers</c> 悄悄改变，导致"编辑器能打、真机打不到"。
        /// </summary>
        private int Overlap(in Vec2 center, float radius)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(
                ToWorld(in center),
                radius,
                _buffer,
                _layerMask,
                QueryTriggerInteraction.Collide);

            // 物理 API 约定不会写超过缓冲长度，这里再夹一次：宁可少算，也不能越界读。
            if (hitCount > _buffer.Length)
            {
                hitCount = _buffer.Length;
            }

            return hitCount;
        }

        /// <summary><see cref="Vec2"/> → 世界坐标（战斗平面 X-Z，见类注释）。</summary>
        private static Vector3 ToWorld(in Vec2 position)
        {
            return new Vector3(position.X, PlaneHeight, position.Y);
        }
    }
}
