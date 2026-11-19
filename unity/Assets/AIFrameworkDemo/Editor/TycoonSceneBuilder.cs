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
    /// 一键生成模拟经营场景：Camera + EventSystem + Canvas + 状态行 + 建筑列表 + "建造 1/2/3"
    /// + "升级 / 雇员工 / 结算一次" + 两个"返回大厅" + 挂 <see cref="TycoonPanelController"/> / <see cref="TycoonHud"/> 的物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与切片的边界</b>：本文件不含任何经营规则——建造扣多少钱、升级贵不贵、员工上限多少、
    /// 一 tick 发多少金币全在 <c>TycoonHandler</c> 里，场景脚本只负责把控件引用喂给 HUD 与控制器。
    /// </para>
    /// <para>
    /// <b>为什么返回大厅有两个按钮</b>：面板控件纵向堆叠，Game 视图较矮时底部按钮可能出屏；
    /// 顶部一个保证"随时能退"，底部一个保证"点完最后一个动作就近返回"。两者各挂一个
    /// <see cref="AIFramework.Adapters.Unity.Scene.SceneNavigator"/>（该组件一次只管一个按钮）。
    /// </para>
    /// </summary>
    public static class TycoonSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>模拟经营场景路径。</summary>
        private const string ScenePath = SceneFolder + "/TycoonScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>提示行高度（像素）。</summary>
        private const float HintHeight = 28f;

        /// <summary>单行文本高度（像素）。</summary>
        private const float LabelHeight = 28f;

        /// <summary>建筑列表高度（像素）：标题行 + 3 栋建筑，16 号字共 4 行约 76px，留出余量。</summary>
        private const float ListHeight = 104f;

        /// <summary>按钮行高度（像素）。</summary>
        private const float RowHeight = 40f;

        /// <summary>行内按钮首选宽度（像素）：横排 3 个共 486px，不会被布局组压成 0 宽。</summary>
        private const float RowButtonWidth = 150f;

        /// <summary>行内按钮首选高度（像素）。</summary>
        private const float RowButtonHeight = 38f;

        /// <summary>提示行字号。</summary>
        private const int HintFontSize = 16;

        /// <summary>状态行字号。</summary>
        private const int LabelFontSize = 18;

        /// <summary>建筑列表字号。</summary>
        private const int ListFontSize = 16;

        /// <summary>按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>生成模拟经营场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/TycoonScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建模拟经营场景", priority = 18)]
        public static void CreateTycoonScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("TycoonRoot", canvas.transform);
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

            Text hint = CreateLabel("Hint", root, "模拟经营：建造 → 升级 / 雇员工 → 每 tick 结算收入（金币走背包账本）", HintFontSize);
            SetHeight(hint.gameObject, HintHeight);

            // 返回入口 1：紧跟提示行，任何滚动状态下都点得到。
            CreateBackButton(root, "BackToLobbyButton", "SceneNavigator");

            Text status = CreateLabel("StatusText", root, "金币 0 · 总收入 0/tick · 已结算 0 tick", LabelFontSize);
            SetHeight(status.gameObject, LabelHeight);

            Text buildingList = CreateLabel("BuildingListText", root, "尚无建筑：点『建造 1/2/3』开工", ListFontSize);
            buildingList.alignment = TextAnchor.UpperLeft;
            buildingList.lineSpacing = 0.95f;
            SetHeight(buildingList.gameObject, ListHeight);

            // 三个建造按钮横排：按钮只发建筑 id，建不建得起由处理器判定。
            RectTransform buildRow = CreateContainer("BuildButtons", root);
            SetHeight(buildRow.gameObject, RowHeight);
            var buildLayout = buildRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            buildLayout.spacing = 6f;
            buildLayout.childAlignment = TextAnchor.MiddleCenter;

            Button build1Button = CreateRowButton("Build1Button", buildRow, "建造 1");
            Button build2Button = CreateRowButton("Build2Button", buildRow, "建造 2");
            Button build3Button = CreateRowButton("Build3Button", buildRow, "建造 3");

            // 四个经营动作横排：升级 / 雇员工作用于列表第一栋，结算按当前总收入发金币，重开换一整套宿主。
            RectTransform actionRow = CreateContainer("ActionButtons", root);
            SetHeight(actionRow.gameObject, RowHeight);
            var actionLayout = actionRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionLayout.spacing = 6f;
            actionLayout.childAlignment = TextAnchor.MiddleCenter;

            Button upgradeButton = CreateRowButton("UpgradeButton", actionRow, "升级");
            Button hireButton = CreateRowButton("HireStaffButton", actionRow, "雇员工");
            Button tickButton = CreateRowButton("TickButton", actionRow, "结算一次");
            Button restartButton = CreateRowButton("RestartButton", actionRow, "重新开始");

            // 返回入口 2：放在最后一个动作之后，点完就近返回。
            CreateBackButton(root, "BackToLobbyButtonBottom", "SceneNavigatorBottom");

            // 控制器与皮肤挂同一个物体：控制器在 Awake 里建宿主、订阅点击并首帧渲染，皮肤只被它调用。
            var tycoonGo = new GameObject("Tycoon");
            var controller = tycoonGo.AddComponent<TycoonPanelController>();
            var hud = tycoonGo.AddComponent<TycoonHud>();

            SetReference(controller, "hud", hud);

            SetReference(hud, "statusText", status);
            SetReference(hud, "buildingListText", buildingList);
            SetReference(hud, "build1Button", build1Button);
            SetReference(hud, "build2Button", build2Button);
            SetReference(hud, "build3Button", build3Button);
            SetReference(hud, "upgradeButton", upgradeButton);
            SetReference(hud, "hireButton", hireButton);
            SetReference(hud, "tickButton", tickButton);
            SetReference(hud, "restartButton", restartButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 模拟经营场景已生成：" + ScenePath
                + "，按 Play 运行（先点『建造 1』，再点『结算一次』看金币上涨）。");
        }

        /// <summary>建一个"返回大厅"按钮，并在场景根下配一个只管这个按钮的 <c>SceneNavigator</c>。</summary>
        /// <param name="parent">按钮的父级（面板根）。</param>
        /// <param name="buttonName">按钮物体名。</param>
        /// <param name="navigatorName">导航器物体名（每个按钮一个，避免互相覆盖）。</param>
        private static void CreateBackButton(RectTransform parent, string buttonName, string navigatorName)
        {
            Button button = CreateButton(buttonName, parent, "返回大厅");
            SetHeight(button.gameObject, RowHeight);

            // 跳转逻辑走 SceneNavigator（同场景不重载 / 场景缺失不崩），不塞进控制器。
            var navGo = new GameObject(navigatorName);
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", LobbySceneName);
            SetReference(navigator, "button", button);
        }

        /// <summary>主相机（位置 <c>(0, 0, -10)</c>、正交，与其它场景保持一致）。</summary>
        private static void CreateCamera()
        {
            var cameraGo = new GameObject("Main Camera");
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
        }

        /// <summary>事件系统（按钮点击依赖它；一个场景只能有一个）。</summary>
        private static void EnsureEventSystem()
        {
            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            eventSystemGo.AddComponent<StandaloneInputModule>();
        }

        /// <summary>屏幕空间覆盖式画布 + 射线检测器（没有它按钮点不到）。</summary>
        private static Canvas CreateCanvas()
        {
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>建一个空的 UI 容器（自带 <see cref="RectTransform"/> 并挂到父级下）。</summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <returns>容器矩形。</returns>
        private static RectTransform CreateContainer(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>建一个文字控件（内置字体、左对齐、指定字号）。</summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="content">初始内容。</param>
        /// <param name="fontSize">字号。</param>
        /// <returns>文本控件。</returns>
        private static Text CreateLabel(string name, Transform parent, string content, int fontSize)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = content;
            text.fontSize = fontSize;
            text.color = new Color(1f, 1f, 1f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        /// <summary>
        /// 建一个带文字标签的按钮。<b>文字必须是子物体</b>：同一物体上只能有一个 <c>Graphic</c>（Image 与 Text 都继承它）。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="label">按钮文字。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateButton(string name, Transform parent, string label)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.22f, 0.3f, 0.45f, 1f);

            var button = go.AddComponent<Button>();

            var textGo = new GameObject("Label");
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.SetParent(go.transform, false);
            Stretch(textRect);

            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = ButtonFontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);

            return button;
        }

        /// <summary>建一个横排行内按钮（固定首选尺寸，避免被布局组压成 0 宽）。</summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">行容器。</param>
        /// <param name="label">按钮文字。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateRowButton(string name, Transform parent, string label)
        {
            Button button = CreateButton(name, parent, label);
            SetPreferredSize(button.gameObject, RowButtonWidth, RowButtonHeight);
            return button;
        }

        /// <summary>
        /// 把 <see cref="RectTransform"/> 四向锚定到父物体（等价于 Anchors = 全屏、Offset = 0）。
        /// <b>为什么必须做</b>：脚本新建的 <see cref="RectTransform"/> 默认是 0 尺寸的点锚点，
        /// 按钮里的文字不拉伸就会按 0 宽排版，真机上等于没有文字。
        /// </summary>
        /// <param name="rect">要拉伸的子物体矩形。</param>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>给控件固定首选高度（纵向布局组按它排版）。</summary>
        /// <param name="target">目标物体。</param>
        /// <param name="height">首选高度（像素）。</param>
        private static void SetHeight(GameObject target, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
        }

        /// <summary>给控件固定首选尺寸（横向布局组按它排版）。</summary>
        /// <param name="target">目标物体。</param>
        /// <param name="width">首选宽度（像素）。</param>
        /// <param name="height">首选高度（像素）。</param>
        private static void SetPreferredSize(GameObject target, float width, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.preferredHeight = height;
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 对象引用赋值（不依赖字段可见性）。</summary>
        /// <param name="target">目标组件。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">要注入的引用。</param>
        private static void SetReference(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 字符串字段赋值。</summary>
        /// <param name="target">目标组件。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">要写入的字符串。</param>
        private static void SetString(Object target, string fieldName, string value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>逐级创建资产目录（AssetDatabase 不会自动补父目录）。</summary>
        /// <param name="path">资产路径。</param>
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
