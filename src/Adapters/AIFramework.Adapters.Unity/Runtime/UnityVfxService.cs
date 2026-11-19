#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using UnityEngine;

// UnityEngine 自己有一个 ILogger，同时 using 两个命名空间时裸写 ILogger 会撞成 CS0104。
using ILogger = AIFramework.Contracts.Ports.ILogger;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 特效服务：定长对象池 + <c>Resources</c> 预制体加载，把 <see cref="IVfxService"/> 落到引擎侧。
    /// <para>
    /// <b>为什么是普通类而不是 MonoBehaviour</b>：本框架禁止依赖引擎生命周期回调（见 AGENTS.md 三·4），
    /// 装配层 <c>new</c> 出来即可用：构造期建好 <see cref="PoolSize"/> 个槽位，"谁创建、谁
    /// <see cref="Dispose"/> 销毁"边界清晰，也就不需要在场景里预放 16 个空物体。
    /// 代价是"到期回收"没有引擎回调可挂——见下一段。
    /// </para>
    /// <para>
    /// <b>到期回收怎么发生</b>：本服务不订阅任何 Unity 回调，而是用"惰性清扫 + 可选每帧钩子"：
    /// <see cref="Play"/> 与 <see cref="ActiveCount"/> 读时钟时会先把已到期的槽位还池，
    /// 装配层若想更及时，可以让宿主每帧调一次 <see cref="Tick"/>（不调也不会漏回收，
    /// 最坏情况是一个已播完的特效多留到下一次 <see cref="Play"/> 才被停掉）。
    /// </para>
    /// <para>
    /// <b>资源约定</b>：逻辑 ID → <c>"Vfx/" + id</c>，预制体必须位于某个 <c>Resources/Vfx/</c> 下。
    /// 找不到时<u>只 Warn 一次</u>并且<u>不生成对象</u>——不占池位、不计入 <see cref="ActiveCount"/>，
    /// 避免"漏打包一个特效"把整场战斗的池位慢慢吃光。
    /// </para>
    /// <para>
    /// <b>并发策略</b>：池满时复用<u>最老</u>的那一条（不扩容、不拒绝新请求）。
    /// 不扩容是硬约束：特效是爆发型负载，按峰值扩容会把池永久撑大（真机上表现为内存只涨不降）。
    /// </para>
    /// <para>线程模型：非线程安全。播放与帧推进同线程（Unity 主线程）。</para>
    /// </summary>
    public sealed class UnityVfxService : IVfxService, IDisposable
    {
        /// <summary>对象池容量（预分配，不扩容）。</summary>
        public const int PoolSize = 16;

        /// <summary>预制体逻辑键前缀，最终 key 形如 <c>Vfx/1001</c>。</summary>
        public const string PrefabKeyPrefix = "Vfx/";

        /// <summary>日志端口。</summary>
        private readonly ILogger _logger;

        /// <summary>槽位节点的父节点；<c>null</c> 表示放在场景根。</summary>
        private readonly Transform? _root;

        /// <summary>单调递增的秒表（默认读 <c>Time.realtimeSinceStartupAsDouble</c>，测试可注入）。</summary>
        private readonly Func<float> _clock;

        /// <summary>定长槽位池。</summary>
        private readonly Slot[] _slots;

        /// <summary>已解析的预制体缓存（<c>Resources.Load</c> 自身有缓存，这里省掉每次的字符串拼接与查表）。</summary>
        private readonly Dictionary<int, GameObject> _prefabCache;

        /// <summary>已诊断过缺失的 ID，用于"同一 ID 只 Warn 一次"。</summary>
        private readonly HashSet<int> _missingPrefabIds;

        /// <summary>是否已释放。</summary>
        private bool _disposed;

        /// <summary>构造特效服务并预建对象池。</summary>
        /// <param name="logger">日志端口（非 <c>null</c>）。</param>
        /// <param name="transform">槽位节点的父节点；<c>null</c> 时放场景根。</param>
        /// <param name="clock">时间源（返回单调递增秒数）；<c>null</c> 时用引擎时钟。</param>
        /// <exception cref="ArgumentNullException"><paramref name="logger"/> 为 <c>null</c>。</exception>
        public UnityVfxService(ILogger logger, Transform? transform = null, Func<float>? clock = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _root = transform;
            _clock = clock ?? EngineClock;
            _prefabCache = new Dictionary<int, GameObject>();
            _missingPrefabIds = new HashSet<int>();

            _slots = new Slot[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                _slots[i] = CreateSlot(i);
            }
        }

        /// <summary>当前正在播放的特效数量（已到期但尚未被清扫的槽位不会被计入）。</summary>
        public int ActiveCount
        {
            get
            {
                if (_disposed)
                {
                    return 0;
                }

                ExpireAll(_clock());

                int active = 0;
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].InUse)
                    {
                        active++;
                    }
                }

                return active;
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 坐标非法的请求由 <see cref="VfxRequestValidator"/> 在入口静默丢弃；预制体缺失时只 Warn 不生成对象。
        /// 池满时复用最老的一条槽位（见类注释）。
        /// </remarks>
        public void Play(in VfxRequest request)
        {
            if (_disposed)
            {
                return;
            }

            if (!VfxRequestValidator.IsValid(in request))
            {
                return;
            }

            float now = _clock();
            ExpireAll(now);

            GameObject? prefab = ResolvePrefab(request.EffectId);
            if (prefab == null)
            {
                // 资源缺失：不占池位、不计入 ActiveCount（否则一次漏打包就会悄悄吃掉并发上限）。
                return;
            }

            Slot slot = RentSlot();
            AttachVisual(slot, prefab, request.EffectId);

            slot.Host.transform.position = new Vector3(request.X, request.Y, 0f);
            slot.Host.transform.localScale = new Vector3(request.Scale, request.Scale, request.Scale);
            slot.Host.SetActive(true);

            slot.PlaySystem.Play();

            slot.EffectId = request.EffectId;
            slot.StartSeconds = now;
            slot.DurationSeconds = request.DurationSeconds;
            slot.InUse = true;
        }

        /// <inheritdoc />
        /// <remarks>把全部在播特效停掉并还池；槽位上的预制体实例保留，下次同 ID 播放可直接复用。</remarks>
        public void Clear()
        {
            if (_disposed)
            {
                return;
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (!slot.InUse)
                {
                    continue;
                }

                Recycle(slot);
            }
        }

        /// <summary>
        /// 可选每帧钩子：立刻把已到期的槽位还池。
        /// <para>装配层想让回收更及时就每帧调一次；不调也不会漏回收（<see cref="Play"/> / <see cref="ActiveCount"/>
        /// 会惰性清扫）。</para>
        /// </summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            ExpireAll(_clock());
        }

        /// <summary>销毁本服务创建的全部槽位节点；重复调用安全。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                slot.InUse = false;
                slot.Visual = null;
                UnityEngine.Object.Destroy(slot.Host);
            }

            _prefabCache.Clear();
            _missingPrefabIds.Clear();
        }

        /// <summary>默认时钟：不用 <c>Time.time</c> 是因为它受 <c>timeScale</c> 影响，暂停时特效永不回收。</summary>
        private static float EngineClock()
        {
            return (float)Time.realtimeSinceStartupAsDouble;
        }

        /// <summary>预建一个槽位：一个容器节点 + 一个兜底 <see cref="ParticleSystem"/>，初始隐藏。</summary>
        private Slot CreateSlot(int index)
        {
            var gameObject = new GameObject("AIFrameworkVfx_" + index);

            Transform? root = _root;
            if (root != null)
            {
                gameObject.transform.SetParent(root, false);
            }

            ParticleSystem shell = gameObject.AddComponent<ParticleSystem>();
            gameObject.SetActive(false);

            return new Slot(gameObject, shell);
        }

        /// <summary>
        /// 解析预制体：先查缓存 → 再查 <c>Resources</c> → 都没有则 Warn 一次并返回 <c>null</c>。
        /// </summary>
        private GameObject? ResolvePrefab(int effectId)
        {
            GameObject cached;
            if (_prefabCache.TryGetValue(effectId, out cached))
            {
                return cached;
            }

            if (_missingPrefabIds.Contains(effectId))
            {
                // 已诊断过：静默跳过，避免连击特效每帧触发时把日志刷爆。
                return null;
            }

            string key = PrefabKeyPrefix + effectId;
            GameObject? prefab = Resources.Load<GameObject>(key);
            if (prefab != null)
            {
                _prefabCache[effectId] = prefab;
                return prefab;
            }

            _missingPrefabIds.Add(effectId);
            _logger.Warn("特效预制体缺失：" + key + "（该 ID 后续播放将静默跳过，且不占用池位）");
            return null;
        }

        /// <summary>
        /// 把预制体实例挂进槽位并决定"这次要控制哪个粒子系统"。
        /// <para>
        /// <b>为什么要取预制体里的系统而不是直接用槽位自带那个</b>：槽位上的 <see cref="ParticleSystem"/>
        /// 是兜底件（预制体本身没挂粒子系统时才会被用到）。真机上玩家看到的是预制体实例，
        /// 控制错了对象就会"计数对、画面没有"。
        /// </para>
        /// </summary>
        private static void AttachVisual(Slot slot, GameObject prefab, int effectId)
        {
            if (slot.VisualEffectId == effectId && slot.Visual != null)
            {
                return;
            }

            GameObject? previous = slot.Visual;
            if (previous != null)
            {
                UnityEngine.Object.Destroy(previous);
            }

            GameObject visual = UnityEngine.Object.Instantiate(prefab, slot.Host.transform);
            visual.transform.localPosition = new Vector3(0f, 0f, 0f);
            visual.transform.localScale = new Vector3(1f, 1f, 1f);

            ParticleSystem? visualSystem = visual.GetComponentInChildren<ParticleSystem>();
            slot.Visual = visual;
            slot.VisualEffectId = effectId;
            slot.PlaySystem = visualSystem != null ? visualSystem : slot.Shell;
            slot.PlaySystem.Stop();
            slot.PlaySystem.Clear();
        }

        /// <summary>取一个槽位：有空位就用空位，全满则回收最老的一条后交出。</summary>
        private Slot RentSlot()
        {
            Slot? oldest = null;
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (!slot.InUse)
                {
                    return slot;
                }

                if (oldest == null || slot.StartSeconds < oldest.StartSeconds)
                {
                    oldest = slot;
                }
            }

            // 走到这里必然已满（上面只要有空位就 return 了），oldest 一定非 null。
            Slot victim = oldest!;
            Recycle(victim);
            return victim;
        }

        /// <summary>把到期（或需要被抢占）的槽位停掉并隐藏；槽位与预制体实例都保留复用。</summary>
        private static void Recycle(Slot slot)
        {
            slot.InUse = false;
            slot.EffectId = 0;
            slot.StartSeconds = 0f;
            slot.DurationSeconds = 0f;

            slot.PlaySystem.Stop();
            slot.PlaySystem.Clear();
            slot.Host.SetActive(false);
        }

        /// <summary>扫一遍池，回收所有 <c>起播 + 时长 &lt;= now</c> 的槽位。</summary>
        private void ExpireAll(float now)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                Slot slot = _slots[i];
                if (!slot.InUse)
                {
                    continue;
                }

                if (now >= slot.StartSeconds + slot.DurationSeconds)
                {
                    Recycle(slot);
                }
            }
        }

        /// <summary>
        /// 一个池槽位。<b>为什么是可变的 class 而不是 struct</b>：槽位状态（在播/起播时刻/当前视觉实例）
        /// 会被就地改写，用 struct 数组会退化成"取出→改→写回"，任何一处漏写回都变成难查的状态漂移。
        /// </summary>
        private sealed class Slot
        {
            public Slot(GameObject host, ParticleSystem shell)
            {
                Host = host;
                Shell = shell;
                PlaySystem = shell;
            }

            /// <summary>容器节点（同时承载缩放与位置）。</summary>
            public GameObject Host { get; }

            /// <summary>槽位自带的兜底粒子系统。</summary>
            public ParticleSystem Shell { get; }

            /// <summary>当前这次播放要控制的粒子系统（预制体自带 → 用它的；否则用 <see cref="Shell"/>）。</summary>
            public ParticleSystem PlaySystem { get; set; }

            /// <summary>当前挂着的预制体实例；<c>null</c> 表示还没实例化过。</summary>
            public GameObject? Visual { get; set; }

            /// <summary>当前实例对应的特效 ID（换 ID 时要销毁旧实例）。</summary>
            public int VisualEffectId { get; set; }

            /// <summary>是否在播。</summary>
            public bool InUse { get; set; }

            /// <summary>当前在播特效的 ID。</summary>
            public int EffectId { get; set; }

            /// <summary>起播时刻（秒）。</summary>
            public float StartSeconds { get; set; }

            /// <summary>请求的播放时长（秒）。</summary>
            public float DurationSeconds { get; set; }
        }
    }
}
