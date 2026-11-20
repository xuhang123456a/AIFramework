#nullable enable
using AIFramework.Adapters.Unity.Battle;
using AIFramework.Adapters.Unity.Inputs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键生成平台跳跃场景：Camera + EventSystem + Canvas + 状态行 + 20×10 字符简图 +
    /// 左移/右移/跳跃三个按钮 + "重新开始" + "下一关" + "返回大厅" + 挂控制器/皮肤的 <c>Platformer</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能看能点"的壳，不含任何平台跳跃规则——重力、跳跃、单向平台落地、
    /// 收集品、终点与胜负全在内核里，场景脚本只负责把控件引用喂给 <see cref="PlatformerController"/>
    /// 与 <see cref="PlatformerHud"/>。
    /// </para>
    /// <para>
    /// <b>为什么地形是一个 <see cref="Text"/> 而不是一堆 <see cref="Image"/> 方块</b>：内置三关的平台数是
    /// 6/7/8、收集品是 6/7/8，用控件拼就要每关重建层级；字符简图只有一个控件、换关零重建，
    /// 而且"实心方块 vs 空心圆 vs @"的密度对比正好是平台跳跃最需要一眼看到的量。
    /// </para>
    /// <para>
    /// <b>为什么简图要压行距</b>：10 行 × 16 号字，默认行距约 168px，超出常见 Game 视图的可用高度后
    /// <see cref="Text"/> 会按"纵向截断"裁掉最后几行（Unity 的 <see cref="Text"/> 默认不撑开父级）。
    /// 这里把 <c>lineSpacing</c> 压到 0.85（约 136px）并给 170px 的固定高度，10 行才全部可见。
    /// </para>
    /// </summary>
    public static class PlatformerSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>平台跳跃场景路径。</summary>
        private const string ScenePath = SceneFolder + "/PlatformerScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>状态行高度（像素）：两行 × 18 号字，留出行距余量。</summary>
        private const float StatusHeight = 56f;

        /// <summary>简图高度（像素）：10 行 × 16 号字在 0.85 行距下约 136px，留出余量避免最后一行被裁掉。</summary>
        private const float MapHeight = 170f;

        /// <summary>操作按钮行高度（像素）。</summary>
        private const float MoveRowHeight = 48f;

        /// <summary>操作按钮的首选宽度（像素）：横排 3 个共 330px，不会被布局组压成 0 宽。</summary>
        private const float MoveButtonWidth = 110f;

        /// <summary>操作按钮的首选高度（像素）。</summary>
        private const float MoveButtonHeight = 44f;

        /// <summary>普通按钮高度（像素）。</summary>
        private const float ButtonHeight = 38f;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 18;

        /// <summary>简图字号。</summary>
        private const int MapFontSize = 16;

        /// <summary>按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>简图行距倍率（1 = 默认；小于 1 即"行高紧凑"）。</summary>
        private const float MapLineSpacing = 0.85f;

        /// <summary>默认关卡 ID（与 <c>PlatformerController.levelId</c> 的默认值一致）。</summary>
        private const int DefaultLevelId = 1;

        /// <summary>默认随机种子（与其它玩法场景保持一致）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认自动演示（与 <c>PlatformerController.autoPlay</c> 的默认值一致）。</summary>
        private const bool DefaultAutoPlay = true;

        /// <summary>生成平台跳跃场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/PlatformerScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建平台跳跃场景", priority = 14)]
        public static void CreatePlatformerScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("PlatformerRoot", canvas.transform);
            root.anchorMin = new Vector2(0.05f, 0.05f);
            root.anchorMax = new Vector2(0.95f, 0.95f);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text status = CreateLabel("Status", root, "等待内核…");
            SetHeight(status.gameObject, StatusHeight);

            Text map = CreateMapText("Map", root, string.Empty);
            SetHeight(map.gameObject, MapHeight);

            // 左/右/跳三个按钮横排：按钮只发"哪个方向"或"跳"，位移与落地全部由内核决定。
            RectTransform moveRow = CreateContainer("MoveButtons", root);
            SetHeight(moveRow.gameObject, MoveRowHeight);

            var moveLayout = moveRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            moveLayout.spacing = 6f;
            moveLayout.childAlignment = TextAnchor.MiddleCenter;

            Button leftButton = CreateMoveButton("LeftButton", moveRow, "左移");
            Button jumpButton = CreateMoveButton("JumpButton", moveRow, "跳跃");
            Button rightButton = CreateMoveButton("RightButton", moveRow, "右移");

            Button restartButton = CreateButton("RestartButton", root, "重新开始");
            SetHeight(restartButton.gameObject, ButtonHeight);

            Button nextLevelButton = CreateButton("NextLevelButton", root, "下一关");
            SetHeight(nextLevelButton.gameObject, ButtonHeight);

            // 返回大厅：跳转逻辑走 SceneNavigator（同场景不重载 / 场景缺失不崩），不塞进控制器。
            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅");
            SetHeight(backButton.gameObject, ButtonHeight);

            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", LobbySceneName);
            SetReference(navigator, "button", backButton);

            // 控制器与皮肤挂同一个物体：控制器在 Awake 里建宿主并首帧渲染，皮肤只被它调用。
            var platformerGo = new GameObject("Platformer");
            var controller = platformerGo.AddComponent<PlatformerController>();
            var hud = platformerGo.AddComponent<PlatformerHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "levelId", DefaultLevelId);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetBool(controller, "autoPlay", DefaultAutoPlay);

            SetReference(hud, "statusText", status);
            SetReference(hud, "mapText", map);
            SetReference(hud, "restartButton", restartButton);
            SetReference(hud, "nextLevelButton", nextLevelButton);
            SetReference(hud, "leftButton", leftButton);
            SetReference(hud, "rightButton", rightButton);
            SetReference(hud, "jumpButton", jumpButton);

            // 输入路由（T4-7）：独立物体挂 PlatformerSceneInputRouter（SceneInputRouter 的平台跳跃子类），
            // 把键鼠/触屏动作转发给控制器。为什么拆子类（而不是一个类挂两个控制器引用）见 SceneInputRouter 类注释：
            // 场景生成末尾的 SceneVerifier 会把"可为空的序列化引用"判成漏注入，而本场景没有 RunnerController。
            // 控制器 autoPlay 保持默认 true（场景一进来就是演示），路由在演示期间也照常转发按键——
            // 玩家按键不会被静默吞掉；关掉 AutoPlay 后 A/D（或 ←/→）即可直接控制左右移动。
            var inputRouterGo = new GameObject("InputRouter");
            var inputRouter = inputRouterGo.AddComponent<PlatformerSceneInputRouter>();
            SetReference(inputRouter, "platformer", controller);
            SetBool(inputRouter, "enableKeyboard", true);
            SetBool(inputRouter, "enableTouch", true);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 平台跳跃场景已生成：" + ScenePath
                + "，按 Play 运行（默认自动演示：角色一路向右、在坑边起跳，能自动通关；"
                + "键盘 A/D 或 ←/→=左右移动、Space=跳，触屏上滑=跳、下滑=蹲、点击左右半屏=主要/次要交互；"
                + "关掉 AutoPlay 后用『左移/右移/跳跃』或键盘/触屏手动操作，『下一关』循环 1→2→3→1）。");
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
        private static RectTransform CreateContainer(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>
        /// 把 <see cref="RectTransform"/> 四向锚定到父物体（等价于 Anchors = 全屏、Offset = 0）。
        /// <b>为什么必须做</b>：脚本 <c>AddComponent&lt;RectTransform&gt;</c> 默认是 0 尺寸的点锚点，
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

        /// <summary>建一个文字控件（内置字体、左对齐、18 号；简图会在此基础上改字号/行距）。</summary>
        private static Text CreateLabel(string name, Transform parent, string content)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = content;
            text.fontSize = StatusFontSize;
            text.color = new Color(1f, 1f, 1f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        /// <summary>
        /// 建字符简图：字号 16、左对齐（10 行 20 列从同一条基线开始读）、行距压到 0.85（10 行才塞得下）。
        /// <b>为什么不设 <c>FontStyle</c></b>：需求只要求字号与行高；字重对"■ / ○ / @ / ·"的密度对比没有帮助，
        /// 多设一个属性只会增加与旧版 Unity 的兼容面。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="content">初始内容（空串，等控制器首帧渲染）。</param>
        /// <returns>简图文本控件。</returns>
        private static Text CreateMapText(string name, Transform parent, string content)
        {
            Text text = CreateLabel(name, parent, content);
            text.fontSize = MapFontSize;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            text.lineSpacing = MapLineSpacing;
            return text;
        }

        /// <summary>建一个带文字标签的按钮。<b>文字必须是子物体</b>：同一物体上只能有一个 <c>Graphic</c>。</summary>
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

        /// <summary>
        /// 建一个操作按钮：与普通按钮同形，但额外给 <see cref="LayoutElement"/> 的首选宽高。
        /// <b>为什么必须给宽度</b>：横向布局组一旦不强制撑开宽度，没有首选尺寸的按钮会被压成 0 宽——
        /// 玩家能看到按钮"消失"，却找不到任何报错。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="label">按钮文字。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateMoveButton(string name, Transform parent, string label)
        {
            Button button = CreateButton(name, parent, label);
            SetPreferredSize(button.gameObject, MoveButtonWidth, MoveButtonHeight);
            return button;
        }

        /// <summary>给物体挂 <see cref="LayoutElement"/> 并设首选高度（<c>VerticalLayoutGroup</c> 按它排版）。</summary>
        /// <param name="target">目标物体。</param>
        /// <param name="height">首选高度（像素）。</param>
        private static void SetHeight(GameObject target, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
        }

        /// <summary>给物体挂 <see cref="LayoutElement"/> 并同时设首选宽高（横排操作按钮用）。</summary>
        /// <param name="target">目标物体。</param>
        /// <param name="width">首选宽度（像素）。</param>
        /// <param name="height">首选高度（像素）。</param>
        private static void SetPreferredSize(GameObject target, float width, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.preferredHeight = height;
        }

        /// <summary>用 <see cref="SerializedObject"/> 给私有字符串字段赋值（不依赖字段可见性）。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">字符串值。</param>
        private static void SetString(Object target, string fieldName, string value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>用 <see cref="SerializedObject"/> 给私有 <c>[SerializeField]</c> 字段赋值（不依赖字段可见性）。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">引用值。</param>
        private static void SetReference(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段（关卡/种子等）。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">整数值。</param>
        private static void SetInt(Object target, string fieldName, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>bool</c> 型 <c>[SerializeField]</c> 字段（自动演示开关等）。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">布尔值。</param>
        private static void SetBool(Object target, string fieldName, bool value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>确保目录存在（父目录不存在时递归创建）。</summary>
        /// <param name="path">资产相对路径，例如 <c>Assets/AIFrameworkDemo/Scenes</c>。</param>
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
