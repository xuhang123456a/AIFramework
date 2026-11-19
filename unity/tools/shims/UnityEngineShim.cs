// 仅用于在无 Unity 的机器上做"形状级"编译校验：不是真的 UnityEngine，别进仓库。
using System;

namespace UnityEngine
{
    public class Object
    {
        public string name = string.Empty;

        public static bool operator ==(Object? a, Object? b) { return ReferenceEquals(a, b); }

        public static bool operator !=(Object? a, Object? b) { return !ReferenceEquals(a, b); }

        public override bool Equals(object o) { return ReferenceEquals(this, o); }

        public override int GetHashCode() { return base.GetHashCode(); }

        /// <summary>与 UnityEngine.Object.Instantiate(Object, Transform) 同形；派生类的非限定调用靠它解析。</summary>
        public static T Instantiate<T>(T original, Transform parent) where T : Object { return original; }

        /// <summary>销毁对象（形状桩）：真实 Unity 里是延迟销毁，帧末才真正移除。</summary>
        public static void Destroy(Object target)
        {
        }
    }

    public class Component : Object
    {
        private Transform? _transform;
        private GameObject? _gameObject;

        /// <summary>所属 Transform（形状桩）：**懒初始化**。
        /// 之前写成 `= new Transform()` 会与 <c>Transform : Component</c> 形成构造递归，
        /// 任何 `new GameObject()` 都会 StackOverflow——离线夹具因此不得不绕路。</summary>
        public Transform transform
        {
            get { return _transform ?? (_transform = new Transform()); }
        }

        /// <summary>所属 GameObject（形状桩）：懒初始化，避免无参构造的递归。</summary>
        public GameObject gameObject
        {
            get { return _gameObject ?? (_gameObject = new GameObject()); }
        }

        /// <summary>取自身或子节点上的组件（形状桩）。</summary>
        public T GetComponent<T>() where T : Component
        {
            return null!;
        }

        /// <summary>取子节点上的组件（形状桩）。</summary>
        public T GetComponentInChildren<T>() where T : Component
        {
            return null!;
        }
    }

    public class GameObject : Object
    {
        public Transform transform = new Transform();

        public bool activeSelf { get; private set; } = true;

        public GameObject()
        {
        }

        public GameObject(string name)
        {
            this.name = name;
        }

        public GameObject(string name, params Type[] components)
        {
            this.name = name;
        }

        /// <summary>创建内置图元（形状桩：返回空对象，没有任何网格与碰撞体）。</summary>
        public static GameObject CreatePrimitive(PrimitiveType type)
        {
            return new GameObject(type.ToString());
        }


        public void SetActive(bool value) { activeSelf = value; }

        /// <summary>取自身或子节点上的组件（形状桩，运行期返回 null）。Unity 自己不标注可空，这里照抄其表面。</summary>
        public T GetComponent<T>() where T : Component
        {
            return null!;
        }

        /// <summary>取子节点上的组件（形状桩）。</summary>
        public T GetComponentInChildren<T>() where T : Component
        {
            return null!;
        }

        /// <summary>加组件（形状桩）。</summary>
        public T AddComponent<T>() where T : Component, new()
        {
            return new T();
        }

        /// <summary>取子物体上的组件（含未激活，形状桩）。</summary>
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component
        {
            return Array.Empty<T>();
        }
    }

    public class Transform : Component
    {
        public Vector3 position;

        /// <summary>本地位置。</summary>
        public Vector3 localPosition;

        /// <summary>本地缩放。</summary>
        public Vector3 localScale = new Vector3(1f, 1f, 1f);

        /// <summary>
        /// 世界旋转（<b>追加段</b>，类型见 <c>UnitySceneRigShim.cs</c>）：弹幕射击是俯视玩法，
        /// 相机必须绕 X 轴转 90° 才能从上往下看战斗平面（X-Z）；没有它战场会缩成一条横线。
        /// </summary>
        public Quaternion rotation;

        /// <summary>挂到父节点下（形状桩）。</summary>
        public void SetParent(Transform parent)
        {
        }

        /// <summary>挂到父节点下（UI 用法：不保留世界坐标）。</summary>
        public void SetParent(Transform parent, bool worldPositionStays)
        {
        }
    }

    public class Collider : Component
    {
    }

    /// <summary>MonoBehaviour 继承链：Object → Component → Behaviour → MonoBehaviour（Unity 真实层级）。</summary>
    public class MonoBehaviour : Component
    {
        public bool enabled = true;
    }

    /// <summary>颜色（RGBA 0~1）。</summary>
    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    /// <summary>内置图元类型。</summary>
    public enum PrimitiveType
    {
        Sphere = 0,
        Capsule = 1,
        Cylinder = 2,
        Cube = 3,
        Plane = 4,
        Quad = 5
    }

    /// <summary>相机（形状桩）。</summary>
    public class Camera : MonoBehaviour
    {
        /// <summary>清屏标志（桩）。</summary>
        public int clearFlags;

        /// <summary>是否正交。</summary>
        public bool orthographic;
    }

    /// <summary>字体资产（形状桩）。</summary>
    public class Font : Object
    {
    }

    /// <summary>材质（形状桩）。</summary>
    public class Material : Object
    {
        /// <summary>颜色。</summary>
        public Color color;
    }

    /// <summary>渲染器（形状桩）。</summary>
    public class Renderer : Component
    {
        /// <summary>材质实例。</summary>
        public Material material = new Material();
    }

    /// <summary>资源加载（桩：只支持内置资源，返回空对象）。</summary>
    public static class Resources
    {
        /// <summary>取内置资源。</summary>
        public static T Load<T>(string path) where T : Object, new()
        {
            return new T();
        }

        /// <summary>取内置资源（泛型）。</summary>
        public static T GetBuiltinResource<T>(string path) where T : Object, new()
        {
            return new T();
        }
    }

    public struct Vector2
    {
        public float x;
        public float y;

        public Vector2(float x, float y) { this.x = x; this.y = y; }

        /// <summary>零向量。</summary>
        public static Vector2 zero
        {
            get { return new Vector2(0f, 0f); }
        }

        /// <summary>单位向量。</summary>
        public static Vector2 one
        {
            get { return new Vector2(1f, 1f); }
        }
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public static class Time
    {
        public static float deltaTime;

        public static double realtimeSinceStartupAsDouble;
    }

    public static class Application
    {
        public static string streamingAssetsPath = string.Empty;

        public static string persistentDataPath = string.Empty;

        /// <summary>Assets 目录的绝对路径（桩：空串；真实 Unity 里是 &lt;project&gt;/Assets）。</summary>
        public static string dataPath = string.Empty;
    }

    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2
    }

    public static class Physics
    {
        /// <summary>把 Transform 变更同步进物理世界（形状桩）。</summary>
        public static void SyncTransforms()
        {
        }

        public static int OverlapSphereNonAlloc(
            Vector3 position, float radius, Collider[] results, int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            return 0;
        }
    }

    // 与 Contracts.Ports.ILogger 撞名：适配层必须显式别名或全限定，否则 Unity 里就是 CS0104。
    public interface ILogger
    {
    }

    // 与 System.Random 撞名：局外随机必须显式写 System.Random。
    public static class Random
    {
        public static int Range(int minInclusive, int maxExclusive) { return minInclusive; }
    }

    public static class Debug
    {
        public static void Log(object message) { }

        public static void LogWarning(object message) { }

        public static void LogError(object message) { }

        public static void LogException(Exception exception) { }
    }

    /// <summary>Inspector 提示（形状桩）。</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : Attribute
    {
        /// <summary>提示文本。</summary>
        public TooltipAttribute(string tooltip)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute
    {
    }

    /// <summary>
    /// 引擎异常（形状桩）：真实 Unity 里由 <c>SceneManager.LoadScene</c> 等在"场景不在 Build Settings"时抛出。
    /// 桩只保证"类型存在、可被 catch、能读到 message"，不复制 Unity 的具体抛出点。
    /// </summary>
    public class UnityException : SystemException
    {
        public UnityException()
        {
        }

        public UnityException(string message)
            : base(message)
        {
        }

        public UnityException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}

// ---------------------------------------------------------------------------
// 追加段（T3-10 音频端口）：只补形状，不含任何混音/解码逻辑。
// 单独再开一个 namespace UnityEngine 块是刻意的——只追加，不改上文既有桩。
// ---------------------------------------------------------------------------
namespace UnityEngine
{
    /// <summary>
    /// Behaviour（形状桩）：Unity 真实层级为 Object → Component → Behaviour → MonoBehaviour。
    /// <para>既有的 <see cref="MonoBehaviour"/> 桩直接继承 <see cref="Component"/>（历史原因，未改动），
    /// 本类型只服务于新增的 <see cref="AudioSource"/> / <see cref="AudioListener"/> 这类"非可编程组件"。</para>
    /// </summary>
    public class Behaviour : Component
    {
        /// <summary>组件是否启用（桩：普通字段，真机上禁用会让组件停止接收回调）。</summary>
        public bool enabled = true;
    }

    /// <summary>
    /// 音频剪辑（形状桩）。真机上由导入的 wav/ogg 生成，无法从 <c>byte[]</c> 直接构造，
    /// 因此本桩只保证"类型存在、可被 <see cref="Resources.Load{T}(string)"/> 泛型约束接受"。
    /// </summary>
    public class AudioClip : Object
    {
        /// <summary>时长（秒）。</summary>
        public float length
        {
            get { return 0f; }
        }

        /// <summary>声道数。</summary>
        public int channels
        {
            get { return 1; }
        }

        /// <summary>采样率。</summary>
        public int frequency
        {
            get { return 44100; }
        }
    }

    /// <summary>
    /// 音频源（形状桩）。<b>桩里的 <see cref="isPlaying"/> 恒为 false</b>：
    /// 因此离线形状校验能证明"代码编译得过"，但**不能**证明并发音源计数正确——那需要真机。
    /// </summary>
    public class AudioSource : Behaviour
    {
        /// <summary>当前挂载的剪辑（真机默认 null）。</summary>
        public AudioClip? clip { get; set; }

        /// <summary>音量（0~1）。</summary>
        public float volume { get; set; } = 1f;

        /// <summary>音高。</summary>
        public float pitch { get; set; } = 1f;

        /// <summary>是否循环（BGM 用）。</summary>
        public bool loop { get; set; }

        /// <summary>是否唤醒即播（框架一律显式关闭，避免"没调用却出声"）。</summary>
        public bool playOnAwake { get; set; } = true;

        /// <summary>是否静音。</summary>
        public bool mute { get; set; }

        /// <summary>2D/3D 混合（0 = 纯 2D）。</summary>
        public float spatialBlend { get; set; }

        /// <summary>衰减起始距离。</summary>
        public float minDistance { get; set; } = 1f;

        /// <summary>衰减结束距离。</summary>
        public float maxDistance { get; set; } = 500f;

        /// <summary>是否正在播放（桩恒为 false）。</summary>
        public bool isPlaying
        {
            get { return false; }
        }

        /// <summary>播放当前 <see cref="clip"/>。</summary>
        public void Play()
        {
        }

        /// <summary>停止播放。</summary>
        public void Stop()
        {
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
        }

        /// <summary>叠加播放一次（不改变 <see cref="clip"/>）。</summary>
        public void PlayOneShot(AudioClip clip)
        {
        }

        /// <summary>按音量比例叠加播放一次。</summary>
        public void PlayOneShot(AudioClip clip, float volumeScale)
        {
        }
    }

    /// <summary>
    /// 音频监听器（形状桩）：场景里通常只有一个，<see cref="volume"/> 是全局总音量。
    /// 框架用它的静态音量承载 Master 通道，避免每条音源各乘一次 Master。
    /// </summary>
    public class AudioListener : Behaviour
    {
        /// <summary>全局音量（0~1）。</summary>
        public static float volume = 1f;

        /// <summary>全局暂停。</summary>
        public static bool pause;
    }

    /// <summary>音频设置（形状桩）：真机上只读，由平台/工程设置决定。</summary>
    public static class AudioSettings
    {
        /// <summary>输出采样率。</summary>
        public static int outputSampleRate
        {
            get { return 48000; }
        }
    }
}
















