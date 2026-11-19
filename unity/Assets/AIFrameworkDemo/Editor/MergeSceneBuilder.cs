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
    /// 一键生成合成场景（<c>Feature.Merge</c> 的场景化）：Camera + EventSystem + Canvas + 状态行
    /// + 6×6 字符盘 + 生成/合成/重新开始三颗按钮 + "返回大厅"
    /// + 挂 <see cref="MergePanelController"/> / <see cref="MergeHud"/> 的物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与切片的边界</b>：本文件不含任何合成规则——相邻判定、等级 +1、满级拒绝、金币查表全在
    /// <c>MergeHandler</c> 里，场景脚本只负责把控件引用喂给 HUD 与控制器。
    /// </para>
    /// </summary>
    public static class MergeSceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/MergeScene.unity";

        private const int LabelHeight = 28;
        private const int GridHeight = 172;
        private const int ButtonHeight = 34;
        private const int GridFontSize = 22;

        [MenuItem("AIFramework/创建合成场景", priority = 21)]
        public static void CreateMergeScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("MergeRoot", canvas.transform);
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

            Text hint = CreateLabel("Hint", root, "合成：生成=首个空格落 1 级；合成=自动合第一对相邻同等级（等级+1，按新等级发金币）");
            SetHeight(hint.gameObject, LabelHeight);

            Text statusText = CreateLabel("StatusText", root, "金币 0 · 已用格 0/36 · 合成 0 次");
            SetHeight(statusText.gameObject, LabelHeight);

            // 字符盘：6 行 × 6 格，居中显示；单行高度由行距与字号撑开，容器给固定高度即可。
            Text gridText = CreateLabel("GridText", root, "· · · · · ·\n· · · · · ·\n· · · · · ·\n· · · · · ·\n· · · · · ·\n· · · · · ·");
            gridText.fontSize = GridFontSize;
            gridText.alignment = TextAnchor.MiddleCenter;
            SetHeight(gridText.gameObject, GridHeight);

            Button spawnButton = CreateButton("SpawnButton", root, "生成");
            SetHeight(spawnButton.gameObject, ButtonHeight);

            Button mergeButton = CreateButton("MergeButton", root, "合成");
            SetHeight(mergeButton.gameObject, ButtonHeight);

            Button restartButton = CreateButton("RestartButton", root, "重新开始");
            SetHeight(restartButton.gameObject, ButtonHeight);

            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅");
            SetHeight(backButton.gameObject, ButtonHeight);

            var mergeGo = new GameObject("Merge");
            var controller = mergeGo.AddComponent<MergePanelController>();
            var hud = mergeGo.AddComponent<MergeHud>();

            SetReference(controller, "hud", hud);

            SetReference(hud, "statusText", statusText);
            SetReference(hud, "gridText", gridText);
            SetReference(hud, "spawnButton", spawnButton);
            SetReference(hud, "mergeButton", mergeButton);
            SetReference(hud, "restartButton", restartButton);

            // 返回大厅：SceneNavigator 统一处理"同场景不重载 / 场景缺失不崩"。
            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", "LobbyScene");
            SetReference(navigator, "button", backButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 合成场景已生成：" + ScenePath
                + "，按 Play 运行（先点『生成』铺格子，再点『合成』）。");
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
