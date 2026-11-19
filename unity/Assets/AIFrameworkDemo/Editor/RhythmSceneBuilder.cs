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
    /// 一键生成音游场景：Camera + EventSystem + Canvas + 状态行 + 4 行轨道文本 + 4 个轨道按钮 +
    /// "重新开始" + "返回大厅" + 挂控制器/皮肤的 <c>Rhythm</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能看能点"的壳，不含任何音游规则——判定窗、漏打、连击、准确率、
    /// 胜负全在内核里，场景脚本只负责把控件引用喂给 <see cref="RhythmController"/> 与 <see cref="RhythmHud"/>。
    /// </para>
    /// <para>
    /// <b>为什么轨道文本要 4 行、字号 16</b>：一个 <see cref="Text"/> 画 4 条轨道（每条 20 列、每列 0.1 秒），
    /// 16 号字在 110px 高度里 4 行刚好不裁切；换成几十个 <see cref="Image"/> 反而每帧几十次脏标记。
    /// </para>
    /// <para>
    /// <b>4 个轨道按钮为什么横排</b>：音游的 4 条轨道是并排的，横排按钮让"哪根手指打哪条轨"一眼可读；
    /// 纵向排会占掉 4×38px，把轨道文本挤出常见 Game 视图的高度。
    /// </para>
    /// </summary>
    public static class RhythmSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>音游场景路径。</summary>
        private const string ScenePath = SceneFolder + "/RhythmScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>状态行高度（像素）：两行 × 18 号字，留出行距余量。</summary>
        private const float StatusHeight = 56f;

        /// <summary>轨道文本高度（像素）：4 行 × 16 号字约 90px，留出余量避免最后一行被裁掉。</summary>
        private const float NotesHeight = 110f;

        /// <summary>轨道按钮行高度（像素）。</summary>
        private const float LaneRowHeight = 48f;

        /// <summary>普通按钮高度（像素）。</summary>
        private const float ButtonHeight = 38f;

        /// <summary>轨道按钮的首选宽度（像素）：横排 4 个共 440px，不会被布局组压成 0 宽。</summary>
        private const float LaneButtonWidth = 110f;

        /// <summary>轨道按钮的首选高度（像素）。</summary>
        private const float LaneButtonHeight = 44f;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 18;

        /// <summary>轨道文本字号（需求固定为 16）。</summary>
        private const int NotesFontSize = 16;

        /// <summary>按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>默认场次/谱面 ID（1 = 谱面 1：30 音符 / 15 秒）。</summary>
        private const int DefaultLevelId = 1;

        /// <summary>默认随机种子（与其它玩法场景保持一致）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认自动演奏（与 <c>RhythmController.autoPlay</c> 的默认值一致）。</summary>
        private const bool DefaultAutoPlay = true;

        /// <summary>生成音游场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/RhythmScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建音游场景", priority = 13)]
        public static void CreateRhythmScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("RhythmRoot", canvas.transform);
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

            Text notes = CreateNotesText("Notes", root, string.Empty);
            SetHeight(notes.gameObject, NotesHeight);

            // 4 条轨道横排：按钮只发"哪条轨道"，判定时机与命中与否全部由内核决定。
            RectTransform laneRow = CreateContainer("LaneButtons", root);
            SetHeight(laneRow.gameObject, LaneRowHeight);

            var laneLayout = laneRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            laneLayout.spacing = 6f;
            laneLayout.childAlignment = TextAnchor.MiddleCenter;

            Button lane0 = CreateLaneButton("Lane0Button", laneRow, "轨道 1");
            Button lane1 = CreateLaneButton("Lane1Button", laneRow, "轨道 2");
            Button lane2 = CreateLaneButton("Lane2Button", laneRow, "轨道 3");
            Button lane3 = CreateLaneButton("Lane3Button", laneRow, "轨道 4");

            Button restartButton = CreateButton("RestartButton", root, "重新开始");
            SetHeight(restartButton.gameObject, ButtonHeight);

            // 返回大厅：跳转逻辑走 SceneNavigator（同场景不重载 / 场景缺失不崩），不塞进控制器。
            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅");
            SetHeight(backButton.gameObject, ButtonHeight);

            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", LobbySceneName);
            SetReference(navigator, "button", backButton);

            // 控制器与皮肤挂同一个物体：控制器在 Awake 里建宿主并首帧渲染，皮肤只被它调用。
            var rhythmGo = new GameObject("Rhythm");
            var controller = rhythmGo.AddComponent<RhythmController>();
            var hud = rhythmGo.AddComponent<RhythmHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "levelId", DefaultLevelId);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetBool(controller, "autoPlay", DefaultAutoPlay);

            SetReference(hud, "statusText", status);
            SetReference(hud, "notesText", notes);
            SetReference(hud, "lane0Button", lane0);
            SetReference(hud, "lane1Button", lane1);
            SetReference(hud, "lane2Button", lane2);
            SetReference(hud, "lane3Button", lane3);
            SetReference(hud, "restartButton", restartButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 音游场景已生成：" + ScenePath
                + "，按 Play 运行（默认自动演奏：连击会持续增长、全 Perfect；"
                + "关掉 AutoPlay 后可点 4 个轨道按钮手动打）。");
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

        /// <summary>建一个文字控件（内置字体、左对齐、18 号；轨道文本会在此基础上改字号）。</summary>
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
        /// 建轨道文本：字号 16、左对齐（4 行字符从同一条基线开始读）。
        /// <b>为什么不设 <c>FontStyle</c></b>：需求只要求字号；字重对"● / ·"的密度对比没有帮助，
        /// 多设一个属性只会增加与旧版 Unity 的兼容面。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="content">初始内容（空串，等控制器首帧渲染）。</param>
        /// <returns>轨道文本控件。</returns>
        private static Text CreateNotesText(string name, Transform parent, string content)
        {
            Text text = CreateLabel(name, parent, content);
            text.fontSize = NotesFontSize;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            return text;
        }

        /// <summary>
        /// 建一个带文字标签的按钮。<b>文字必须是子物体</b>：同一物体上只能有一个 <c>Graphic</c>。
        /// <para>
        /// <b>与 <c>TetrisSceneBuilder.CreateButton</c> 的唯一差异</b>：这里给 Label 补了四向拉伸锚点。
        /// 脚本新建的 <see cref="RectTransform"/> 默认是 0 尺寸的点锚点，不拉伸的话
        /// <see cref="Text"/> 会按 0 宽排版 —— 真机上按钮文字直接看不见。仓库里
        /// <c>ArenaSceneBuilder</c> / <c>CardBattleSceneBuilder</c> 已经是拉伸写法，这里跟它们对齐。
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
        /// 建一个轨道按钮：与普通按钮同形，但额外给 <see cref="LayoutElement"/> 的首选宽高。
        /// <b>为什么必须给宽度</b>：横向布局组一旦不强制撑开宽度，没有首选尺寸的按钮会被压成 0 宽——
        /// 玩家能看到四个按钮"消失"，却找不到任何报错。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="label">按钮文字。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateLaneButton(string name, Transform parent, string label)
        {
            Button button = CreateButton(name, parent, label);
            SetPreferredSize(button.gameObject, LaneButtonWidth, LaneButtonHeight);
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

        /// <summary>给物体挂 <see cref="LayoutElement"/> 并同时设首选宽高（横排轨道按钮用）。</summary>
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

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段（场次/种子等）。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">整数值。</param>
        private static void SetInt(Object target, string fieldName, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>bool</c> 型 <c>[SerializeField]</c> 字段（自动演奏开关等）。</summary>
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
