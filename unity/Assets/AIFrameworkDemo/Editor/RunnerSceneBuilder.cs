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
    /// 一键生成跑酷场景：Camera + EventSystem + Canvas（垂直布局）+ 状态行 + 30 列字符跑道 +
    /// 跳跃/下蹲按钮 + "重新开始" + "返回大厅" + 挂控制器/皮肤的 <c>Runner</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能看能点"的壳，不含任何跑酷规则——速度、分数、生成节奏、
    /// 碰撞窗、清障高度全在内核里；场景脚本只负责把控件引用喂给 <see cref="RunnerController"/>
    /// 与 <see cref="RunnerHud"/>，字段名打错一个字母 Unity 不会报错（运行时静默拿到 null，
    /// 表现为"按钮点了没反应"），所以字段注入全部走 <see cref="SerializedObject"/> 并按字段名精确匹配。
    /// </para>
    /// <para>
    /// <b>为什么垂直布局直接挂在 Canvas 上</b>：本场景只有一列控件（状态行 → 跑道 → 按钮行 →
    /// 两个操作按钮），多套一层 <c>RunnerRoot</c> 只是多一个 RectTransform 要拉伸；挂 Canvas 上
    /// 少一层、也少一处"忘了 Stretch 导致 0 尺寸"的坑。
    /// </para>
    /// <para>
    /// <b>跑道文本为什么 20 号字、72 高</b>：一行 30 列字符在 1280 宽下用 20 号字约占 420px，
    /// 留出的对半余量让 ▲ / ▼ 这类宽字形不会互相挤在一起；72px 高足够容纳行高与下行间隙。
    /// 字符跑道用的是比例字体（与其它场景一致的 LegacyRuntime/Arial），<b>列对齐是近似的</b>，
    /// 需要严格等宽要换 TMP 等宽字体——这件事只能在真机上判断，脚本里不做。
    /// </para>
    /// </summary>
    public static class RunnerSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>跑酷场景路径。</summary>
        private const string ScenePath = SceneFolder + "/RunnerScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>状态行高度（像素）：两行 × 18 号字，留出行距余量。</summary>
        private const float StatusHeight = 56f;

        /// <summary>跑道文本高度（像素）：一行 20 号字 + 上下余量。</summary>
        private const float LaneHeight = 44f;

        /// <summary>跳跃/下蹲按钮行高度（像素）。</summary>
        private const float ActionRowHeight = 48f;

        /// <summary>普通按钮高度（像素）。</summary>
        private const float ButtonHeight = 38f;

        /// <summary>跳跃/下蹲按钮的首选宽度（像素）：两个横排共 220px，不会被布局组压成 0 宽。</summary>
        private const float ActionButtonWidth = 110f;

        /// <summary>跳跃/下蹲按钮的首选高度（像素）。</summary>
        private const float ActionButtonHeight = 44f;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 18;

        /// <summary>跑道文本字号。</summary>
        private const int LaneFontSize = 20;

        /// <summary>按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>默认随机种子（与其它玩法场景保持一致）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认自动演示（与 <c>RunnerController.autoPlay</c> 的默认值一致）。</summary>
        private const bool DefaultAutoPlay = true;

        /// <summary>生成跑酷场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/RunnerScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建跑酷场景", priority = 15)]
        public static void CreateRunnerScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();

            var layout = canvas.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text status = CreateLabel("Status", canvas.transform, "等待内核…");
            SetHeight(status.gameObject, StatusHeight);

            Text lane = CreateLaneText("Lane", canvas.transform);
            SetHeight(lane.gameObject, LaneHeight);

            // 跳跃/下蹲横排：两个按钮只抬事件，能不能跳/蹲由内核裁决（空中一律被拒）。
            RectTransform actionRow = CreateContainer("ActionRow", canvas.transform);
            SetHeight(actionRow.gameObject, ActionRowHeight);

            var actionLayout = actionRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionLayout.spacing = 6f;
            actionLayout.childAlignment = TextAnchor.MiddleCenter;

            Button jumpButton = CreateActionButton("JumpButton", actionRow, "跳跃");
            Button slideButton = CreateActionButton("SlideButton", actionRow, "下蹲");

            Button restartButton = CreateButton("RestartButton", canvas.transform, "重新开始");
            SetHeight(restartButton.gameObject, ButtonHeight);

            // 返回大厅：跳转逻辑走 SceneNavigator（同场景不重载 / 场景缺失不崩），不塞进控制器。
            Button backButton = CreateButton("BackToLobbyButton", canvas.transform, "返回大厅");
            SetHeight(backButton.gameObject, ButtonHeight);

            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", LobbySceneName);
            SetReference(navigator, "button", backButton);

            // 控制器与皮肤挂同一个物体：控制器在 Awake 里建宿主并首帧渲染，皮肤只被它调用。
            var runnerGo = new GameObject("Runner");
            var controller = runnerGo.AddComponent<RunnerController>();
            var hud = runnerGo.AddComponent<RunnerHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetBool(controller, "autoPlay", DefaultAutoPlay);

            SetReference(hud, "statusText", status);
            SetReference(hud, "laneText", lane);
            SetReference(hud, "restartButton", restartButton);
            SetReference(hud, "jumpButton", jumpButton);
            SetReference(hud, "slideButton", slideButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 跑酷场景已生成：" + ScenePath
                + "，按 Play 运行（默认自动演示：跳过 ▲、蹲过 ▼；"
                + "关掉 AutoPlay 后可点『跳跃/下蹲』手动操作）。");
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
        /// 按钮/牌面里的文字不拉伸就会按 0 宽排版，真机上等于没有文字。
        /// </summary>
        /// <param name="rect">要拉伸的子物体矩形。</param>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>建一个文字控件（内置字体、左对齐、18 号；跑道文本会在此基础上改字号）。</summary>
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
        /// 建跑道文本：20 号字、左对齐（30 列字符从同一条基线开始读）。
        /// <b>为什么初始内容是空串而不是提示文案</b>：首帧控制器就会写满一整行 30 列，
        /// 任何占位文案都会在同一帧被覆盖，只会让"没渲染"和"渲染了空跑道"更难区分。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <returns>跑道文本控件。</returns>
        private static Text CreateLaneText(string name, Transform parent)
        {
            Text text = CreateLabel(name, parent, string.Empty);
            text.fontSize = LaneFontSize;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            return text;
        }

        /// <summary>
        /// 建一个带文字标签的按钮。<b>文字必须是子物体</b>：同一物体上只能有一个 <c>Graphic</c>。
        /// <para>
        /// <b>给 Label 补四向拉伸锚点</b>：脚本新建的 <see cref="RectTransform"/> 默认是 0 尺寸的点锚点，
        /// 不拉伸的话 <see cref="Text"/> 会按 0 宽排版 —— 真机上按钮文字直接看不见。
        /// 仓库里 <c>RhythmSceneBuilder</c> / <c>ArenaSceneBuilder</c> 已经是拉伸写法，这里与它们对齐。
        /// </para>
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

        /// <summary>
        /// 建一个操作按钮（跳跃/下蹲）：与普通按钮同形，但额外给 <see cref="LayoutElement"/> 的首选宽高。
        /// <b>为什么必须给宽度</b>：横向布局组一旦不强制撑开宽度，没有首选尺寸的按钮会被压成 0 宽——
        /// 玩家能看到两个按钮"消失"，却找不到任何报错。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="label">按钮文字。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateActionButton(string name, Transform parent, string label)
        {
            Button button = CreateButton(name, parent, label);
            SetPreferredSize(button.gameObject, ActionButtonWidth, ActionButtonHeight);
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

        /// <summary>给物体挂 <see cref="LayoutElement"/> 并同时设首选宽高（横排按钮用）。</summary>
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

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段（种子等）。</summary>
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
