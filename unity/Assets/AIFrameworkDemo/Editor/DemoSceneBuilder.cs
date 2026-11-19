#nullable enable
using AIFramework.Adapters.Unity;
using AIFramework.Adapters.Unity.Diagnostics;
using AIFramework.Adapters.Unity.Ui;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键生成演示场景：不手写 .unity YAML，避免占位 GUID 拼错。
    /// 生成的场景做三件事——挂一个 <see cref="AIFrameworkBehaviour"/>、摆两个 capsule 当单位、
    /// 再挂一套帧耗时基线（<see cref="FrameProfiler"/> + <see cref="PerformanceHud"/> + 状态行）。
    /// </summary>
    public static class DemoSceneBuilder
    {
        private const string ScenePath = "Assets/AIFrameworkDemo/Scenes/DemoScene.unity";

        /// <summary>采样容量：240 帧 ≈ 4 秒 @ 60fps（与 <see cref="FrameProfiler"/> 默认值一致，这里显式注入，方便场景里改）。</summary>
        private const int ProfilerSampleCapacity = 240;

        /// <summary>状态行高度（像素）。</summary>
        private const int HudHeight = 28;

        [MenuItem("AIFramework/创建演示场景", priority = 0)]
        public static void CreateDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder("Assets/AIFrameworkDemo/Scenes");

            var host = new GameObject("AIFrameworkBehaviour");
            var behaviour = host.AddComponent<AIFrameworkBehaviour>();

            GameObject shield = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            shield.name = "ShieldUnit";
            shield.transform.position = new Vector3(0f, 0f, 0f);

            GameObject attacker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            attacker.name = "AttackerUnit";
            attacker.transform.position = new Vector3(0f, 0f, 3f);

            GameObject projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            projectile.name = "IncomingProjectile";
            projectile.transform.position = new Vector3(0f, 0f, 1.4f);
            projectile.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);

            // 把两个胶囊的碰撞体喂给 Behaviour：没有这份清单，Play 时它会认为"没有可绑定单位"而空转。
            var units = new Collider[2];
            units[0] = shield.GetComponent<Collider>();
            units[1] = attacker.GetComponent<Collider>();
            var serialized = new SerializedObject(behaviour);
            SerializedProperty sceneUnits = serialized.FindProperty("_sceneUnits");
            sceneUnits.arraySize = units.Length;
            for (int i = 0; i < units.Length; i++)
            {
                sceneUnits.GetArrayElementAtIndex(i).objectReferenceValue = units[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            CreateProfilerHud();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 演示场景已生成：" + ScenePath
                + "，按 Play 运行（左上角状态行显示 FPS / 平均与峰值帧耗时 / 帧均分配 / 采样数）。");
        }

        /// <summary>
        /// 挂帧耗时基线：一个 <c>Profiler</c> 物体（<see cref="FrameProfiler"/> + <see cref="PerformanceHud"/>）
        /// 加画布上一条状态行，随后把两边的私有 <c>[SerializeField]</c> 字段全部注入。
        /// <para>
        /// <b>为什么要动 <see cref="SerializedObject"/> 而不是 <c>AddComponent</c> 后直接赋字段</b>：字段是
        /// <c>private</c>，而且值必须落进 <c>.unity</c> 资产——直接改内存字段在编辑器里不会写盘，Play 一次就丢。
        /// </para>
        /// </summary>
        private static void CreateProfilerHud()
        {
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // 诊断层永远最上：以后 DemoScene 再加别的 UI，性能行也不会被盖住。
            canvas.sortingOrder = (int)UiLayerKind.Debug;
            canvasGo.AddComponent<GraphicRaycaster>();

            Text status = CreateStatusLabel(canvas.transform);

            var profilerGo = new GameObject("Profiler");
            var profiler = profilerGo.AddComponent<FrameProfiler>();
            var hud = profilerGo.AddComponent<PerformanceHud>();

            SetInt(profiler, "sampleCapacity", ProfilerSampleCapacity);
            SetReference(hud, "statusText", status);
            SetReference(hud, "profiler", profiler);
        }

        /// <summary>
        /// 左上角单行状态文本：锚点与轴心都贴左上，尺寸只用来定宽高（不参与布局组，所以不需要 LayoutElement）。
        /// </summary>
        private static Text CreateStatusLabel(Transform parent)
        {
            var go = new GameObject("ProfilerStatus");
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(16f, -16f);
            rect.sizeDelta = new Vector2(720f, HudHeight);

            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = "性能采样启动中…";
            text.fontSize = 18;
            text.color = new Color(1f, 0.9f, 0.4f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 对象引用字段赋值（不依赖字段可见性）。</summary>
        private static void SetReference(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 的 <c>int</c> 字段赋值。</summary>
        private static void SetInt(Object target, string fieldName, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = System.IO.Path.GetDirectoryName(path) ?? "Assets";
            parent = parent.Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
