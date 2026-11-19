#nullable enable
using AIFramework.Adapters.Unity.Lobby;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键生成异步 PVP 场景（<c>Feature.AsyncPvp</c> 的场景化）：Camera + EventSystem + Canvas + 段位状态行
    /// + 对手池文本 + 挑战/刷新/重新开始三颗按钮 + "返回大厅"
    /// + 挂 <see cref="AsyncPvpPanelController"/> / <see cref="AsyncPvpHud"/> 的物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与切片的边界</b>：本文件不含任何算分规则——得分公式、胜负判定、段位增减、连胜里程碑发奖全在
    /// <c>AsyncPvpHandler</c> 里，场景脚本只负责把控件引用喂给 HUD 与控制器。
    /// </para>
    /// </summary>
    public static class AsyncPvpSceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/AsyncPvpScene.unity";

        private const int LabelHeight = 28;
        private const int OpponentHeight = 96;
        private const int ButtonHeight = 34;

        [MenuItem("AIFramework/创建异步PVP场景", priority = 22)]
        public static void CreateAsyncPvpScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("AsyncPvpRoot", canvas.transform);
            root.anchorMin = new Vector2(0.05f, 0.02f);
            root.anchorMax = new Vector2(0.95f, 0.98f);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text hint = CreateLabel("Hint", root, "异步 PVP：挑战打对手池第 1 个（战力 100），确定性算分；刷新换池；重开重置段位");
            SetHeight(hint.gameObject, LabelHeight);

            Text statusText = CreateLabel("StatusText", root, "段位 1000 · 胜 0 / 负 0 · 未开打");
            SetHeight(statusText.gameObject, LabelHeight);

            Text opponentText = CreateLabel("OpponentText", root, "对手池（挑战作用于第 1 个）\n1. id=100 · 战力 50 · 种子 20261120");
            opponentText.alignment = TextAnchor.UpperLeft;
            SetHeight(opponentText.gameObject, OpponentHeight);

            Button fightButton = CreateButton("FightButton", root, "挑战");
            SetHeight(fightButton.gameObject, ButtonHeight);

            Button refreshButton = CreateButton("RefreshButton", root, "刷新对手");
            SetHeight(refreshButton.gameObject, ButtonHeight);

            Button restartButton = CreateButton("RestartButton", root, "重新开始");
            SetHeight(restartButton.gameObject, ButtonHeight);

            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅");
            SetHeight(backButton.gameObject, ButtonHeight);

            var pvpGo = new GameObject("AsyncPvp");
            var controller = pvpGo.AddComponent<AsyncPvpPanelController>();
            var hud = pvpGo.AddComponent<AsyncPvpHud>();

            SetReference(controller, "hud", hud);

            SetReference(hud, "statusText", statusText);
            SetReference(hud, "opponentText", opponentText);
            SetReference(hud, "fightButton", fightButton);
            SetReference(hud, "refreshButton", refreshButton);
            SetReference(hud, "restartButton", restartButton);

            // 返回大厅：SceneNavigator 统一处理"同场景不重载 / 场景缺失不崩"。
            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", "LobbyScene");
            SetReference(navigator, "button", backButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 异步 PVP 场景已生成：" + ScenePath
                + "，按 Play 运行（点『挑战』结算一场，点『刷新对手』换一批）。");
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
            text.fontSize = 18;
            text.color = new Color(1f, 1f, 1f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

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
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);

            return button;
        }

        private static void SetHeight(GameObject target, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 对象引用赋值（不依赖字段可见性）。</summary>
        private static void SetReference(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 字符串字段赋值。</summary>
        private static void SetString(Object target, string fieldName, string value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).stringValue = value;
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
