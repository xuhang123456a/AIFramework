#nullable enable
using AIFramework.Adapters.Unity.Battle;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键生成实时竞技场场景（T1-5）：Camera + EventSystem + Canvas + 状态行 + 重开按钮
    /// + 挂控制器/皮肤的 <c>Arena</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>场景里没有任何战场物体</b>：竞技场的单位与飞行物由控制器通过 <c>IRealtimeSandbox</c> 在
    /// 逻辑层布场，"看得见的战场"要么后续做可视化层，要么用 <c>AIFrameworkBehaviour.BindUnit</c>
    /// 那套碰撞体登记把逻辑体映射到场景体。本场景只负责"能看状态、能重开"。
    /// </para>
    /// </summary>
    public static class ArenaSceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/ArenaScene.unity";

        /// <summary>状态行高度（像素）。</summary>
        private const int LabelHeight = 32;

        /// <summary>重开按钮高度（像素）。</summary>
        private const int RowHeight = 44;

        /// <summary>默认逻辑步长：1/60 秒，与强实时内核的帧长一致。</summary>
        private const float DefaultStepSeconds = 1f / 60f;

        /// <summary>生成竞技场场景到 <c>Assets/AIFrameworkDemo/Scenes/ArenaScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建竞技场场景", priority = 7)]
        public static void CreateArenaScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("ArenaRoot", canvas.transform);

            // UI 拉满父级：四向锚定到整块画布，子元素由 VerticalLayoutGroup 负责撑宽。
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.padding = new RectOffset(16, 16, 16, 16);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text status = CreateLabel("Status", root, "波次 1/3 · 主角 HP -- · 存活敌人 -- · 反射 0 · 来袭 0");
            SetHeight(status.gameObject, LabelHeight);

            Button restartButton = CreateButton("RestartButton", root, "重开一局");
            SetHeight(restartButton.gameObject, RowHeight);

            // 控制器与皮肤挂同一个物体：控制器 Awake 里要拿到 hud 才能订阅重开事件。
            var arenaGo = new GameObject("Arena");
            var controller = arenaGo.AddComponent<ArenaController>();
            var hud = arenaGo.AddComponent<ArenaHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "levelId", 1);
            SetInt(controller, "randomSeed", 20261120);
            SetFloat(controller, "fixedStepSeconds", DefaultStepSeconds);

            SetReference(hud, "statusText", status);
            SetReference(hud, "restartButton", restartButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 竞技场场景已生成：" + ScenePath
                + "，按 Play 运行（3 波敌人，清空即胜利；主角阵亡或 1200 帧触顶则失败）。");
        }

        private static void CreateCamera()
        {
            var cameraGo = new GameObject("Main Camera");
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            cameraGo.AddComponent<Camera>();
        }

        private static void EnsureEventSystem()
        {
            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            eventSystemGo.AddComponent<StandaloneInputModule>();
        }

        private static Canvas CreateCanvas()
        {
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private static RectTransform CreateContainer(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static Text CreateLabel(string name, Transform parent, string content)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = content;
            text.fontSize = 20;
            text.color = new Color(1f, 1f, 1f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        /// <summary>
        /// 按钮：<c>Image</c>（底色）+ <c>Button</c> + 子物体 <c>Text</c>（文案）。
        /// 子 <c>Text</c> 必须四向拉伸，否则它只有 0 尺寸、文字完全看不见。
        /// </summary>
        private static Button CreateButton(string name, Transform parent, string label)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.2f, 0.35f, 0.6f, 1f);

            var button = go.AddComponent<Button>();

            var textGo = new GameObject("Label");
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.SetParent(go.transform, false);
            Stretch(textRect);

            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = 18;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);

            return button;
        }

        /// <summary>把 RectTransform 四向锚定到父物体（UI 子元素不拉伸就是 0 尺寸）。</summary>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetHeight(GameObject target, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 字段赋值（不依赖字段可见性）。</summary>
        private static void SetReference(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段。</summary>
        private static void SetInt(Object target, string fieldName, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>float</c> 型 <c>[SerializeField]</c> 字段。</summary>
        private static void SetFloat(Object target, string fieldName, float value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).floatValue = value;
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
