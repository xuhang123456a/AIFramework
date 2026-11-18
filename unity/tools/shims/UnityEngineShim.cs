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
    }

    public class Component : Object
    {
        public Transform transform = new Transform();

        public GameObject gameObject = new GameObject();
    }

    public class GameObject : Object
    {
        public Transform transform = new Transform();
    }

    public class Transform : Component
    {
        public Vector3 position;
    }

    public class Collider : Component
    {
    }

    public class MonoBehaviour : Object
    {
        public bool enabled = true;
    }

    public struct Vector2
    {
        public float x;
        public float y;

        public Vector2(float x, float y) { this.x = x; this.y = y; }
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
}

