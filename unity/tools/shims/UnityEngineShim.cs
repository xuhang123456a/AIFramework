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
    }

    public class Component : Object
    {
        public Transform transform = new Transform();

        public GameObject gameObject = new GameObject();

        /// <summary>取自身或子节点上的组件（形状桩）。</summary>
        public T? GetComponent<T>() where T : Component
        {
            return null;
        }

        /// <summary>取子节点上的组件（形状桩）。</summary>
        public T? GetComponentInChildren<T>() where T : Component
        {
            return null;
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

        /// <summary>取自身或子节点上的组件（形状桩，永远返回 null，只保证能编译）。</summary>
        public T? GetComponent<T>() where T : Component
        {
            return null;
        }

        /// <summary>取子节点上的组件（形状桩）。</summary>
        public T? GetComponentInChildren<T>() where T : Component
        {
            return null;
        }

        /// <summary>加组件（形状桩）。</summary>
        public T AddComponent<T>() where T : Component, new()
        {
            return new T();
        }
    }

    public class Transform : Component
    {
        public Vector3 position;

        /// <summary>本地位置。</summary>
        public Vector3 localPosition;

        /// <summary>本地缩放。</summary>
        public Vector3 localScale = new Vector3(1f, 1f, 1f);

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
    }

    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2
    }

    public static class Physics
    {
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










