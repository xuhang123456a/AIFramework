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
    /// 一键生成放置场景（T1-7 的场景化）：Camera + EventSystem + Canvas + 金币/收入文本
    /// + 建筑列表（模板行带升级按钮）+ "返回大厅" + 挂 <see cref="IdlePanelController"/> / <see cref="IdleHud"/> 的物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与切片的边界</b>：本文件不含任何放置规则——升级扣多少钱、能不能升全在
    /// <c>IdleTycoonHandler</c> 里，场景脚本只负责把控件引用喂给 HUD 与控制器。
    /// </para>
    /// </summary>
    public static class IdleSceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/IdleScene.unity";

        private const int LabelHeight = 28;
        private const int RowHeight = 34;
        private const int ListHeight = 200;

        [MenuItem("AIFramework/创建放置场景", priority = 8)]
        public static void CreateIdleScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("IdleRoot", canvas.transform);
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

            Text hint = CreateLabel("Hint", root, "放置经营：升级建筑提高每秒收入（离线收益由内核按时间源结算）");
            SetHeight(hint.gameObject, LabelHeight);

            Text goldText = CreateLabel("GoldText", root, "金币 0");
            SetHeight(goldText.gameObject, LabelHeight);

            Text incomeText = CreateLabel("IncomeText", root, "收入 0/秒");
            SetHeight(incomeText.gameObject, LabelHeight);

            RectTransform buildingList = CreateContainer("BuildingList", root);
            SetHeight(buildingList.gameObject, ListHeight);
            var listLayout = buildingList.gameObject.AddComponent<VerticalLayoutGroup>();
            listLayout.spacing = 2f;
            listLayout.childControlWidth = true;
            listLayout.childControlHeight = true;
            listLayout.childForceExpandWidth = true;
            listLayout.childForceExpandHeight = false;

            GameObject buildingTemplate = CreateRowTemplate("BuildingTemplate", buildingList, "建筑 1 Lv0/3 · 产出 0/秒 · 升级 100金");
            buildingTemplate.SetActive(false);

            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅");
            SetHeight(backButton.gameObject, RowHeight);

            var idleGo = new GameObject("Idle");
            var controller = idleGo.AddComponent<IdlePanelController>();
            var hud = idleGo.AddComponent<IdleHud>();

            SetReference(controller, "hud", hud);

            SetReference(hud, "goldText", goldText);
            SetReference(hud, "incomeText", incomeText);
            SetReference(hud, "list", buildingList);
            SetReference(hud, "itemTemplate", buildingTemplate);

            // 返回大厅：SceneNavigator 统一处理"同场景不重载 / 场景缺失不崩"。
            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", "LobbyScene");
            SetReference(navigator, "button", backButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 放置场景已生成：" + ScenePath + "，按 Play 运行（点建筑行的升级按钮）。");
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

        /// <summary>
        /// 建筑行模板：一行 = 背景图 + 升级按钮 + 居中文字（HUD 用 GetComponentInChildren 找它们）。
        /// 模板保持非激活状态，运行时由 HUD 复制并激活。
        /// </summary>
        private static GameObject CreateRowTemplate(string name, Transform parent, string label)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.15f, 0.15f, 0.18f, 1f);

            go.AddComponent<Button>();

            // 文字必须是子物体：同一个 GameObject 上只能有一个 Graphic（Image 与 Text 都继承 Graphic）。
            AttachLabelChild(go.transform, label, TextAnchor.MiddleLeft);

            SetHeight(go, RowHeight);
            return go;
        }

        /// <summary>给容器挂一个撑满父级的文字子物体（Graphic 互斥规则要求文字单独成物体）。</summary>
        private static void AttachLabelChild(Transform parent, string label, TextAnchor alignment)
        {
            var textGo = new GameObject("Label");
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.SetParent(parent, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = 16;
            text.alignment = alignment;
            text.color = new Color(0.9f, 0.9f, 0.9f, 1f);
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
