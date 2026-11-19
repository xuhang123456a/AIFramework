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
    /// 一键生成解谜（推箱子）场景：Camera + EventSystem + Canvas + 状态行 + 字符网格 + 三个操作按钮
    /// + "返回大厅" + 挂控制器/皮肤的 <c>Puzzle</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能看能点"的壳，不含任何推箱子规则——规则全在内核里，
    /// 场景脚本只负责把控件引用喂给 <see cref="PuzzleController"/> 与 <see cref="PuzzleHud"/>。
    /// </para>
    /// <para>
    /// <b>为什么棋盘是一个 <see cref="Text"/> 而不是格子控件</b>：解谜的棋盘尺寸随关卡变化（7×5 / 7×7 / 8×8），
    /// 用控件铺格就要在运行时按尺寸重建层级；字符网格只有一个控件，换关零重建，且墙/目标/箱子/玩家的
    /// 叠加关系一眼可读。
    /// </para>
    /// </summary>
    public static class PuzzleSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>解谜场景路径。</summary>
        private const string ScenePath = SceneFolder + "/PuzzleScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>状态行高度（像素）。</summary>
        private const float StatusHeight = 30f;

        /// <summary>
        /// 网格文本高度（像素）。8 行 × 20 号字 ≈ 190px，留出余量给行距与上下的对齐留白，
        /// 避免最后一行被裁掉（Unity 的 <c>Text</c> 默认纵向是截断而不是撑开）。
        /// </summary>
        private const float GridHeight = 240f;

        /// <summary>按钮高度（像素）。</summary>
        private const float ButtonHeight = 38f;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 18;

        /// <summary>网格字号（需求固定为 20）。</summary>
        private const int GridFontSize = 20;

        /// <summary>按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>默认关卡 ID（与 <c>PuzzleController.levelId</c> 的默认值一致）。</summary>
        private const int DefaultLevelId = 1;

        /// <summary>默认随机种子（与其它玩法场景保持一致）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认自动解法间隔（秒），与 <c>PuzzleController.autoStepSeconds</c> 的默认值一致。</summary>
        private const float DefaultAutoStepSeconds = 0.35f;

        /// <summary>生成解谜场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/PuzzleScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建解谜场景", priority = 10)]
        public static void CreatePuzzleScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("PuzzleRoot", canvas.transform);
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

            Text grid = CreateGridText("Grid", root, string.Empty);
            SetHeight(grid.gameObject, GridHeight);

            Button autoSolveButton = CreateButton("AutoSolveButton", root, "自动解一关");
            SetHeight(autoSolveButton.gameObject, ButtonHeight);

            Button resetButton = CreateButton("ResetButton", root, "重置本关");
            SetHeight(resetButton.gameObject, ButtonHeight);

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
            var puzzleGo = new GameObject("Puzzle");
            var controller = puzzleGo.AddComponent<PuzzleController>();
            var hud = puzzleGo.AddComponent<PuzzleHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "levelId", DefaultLevelId);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetFloat(controller, "autoStepSeconds", DefaultAutoStepSeconds);

            SetReference(hud, "statusText", status);
            SetReference(hud, "gridText", grid);
            SetReference(hud, "autoSolveButton", autoSolveButton);
            SetReference(hud, "resetButton", resetButton);
            SetReference(hud, "nextLevelButton", nextLevelButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 解谜场景已生成：" + ScenePath
                + "，按 Play 运行（点『自动解一关』看内核按内置解法走完本关）。");
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

        /// <summary>建一个文字控件（内置字体、左对齐、18 号；网格会在此基础上改字号与对齐）。</summary>
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
        /// 建字符网格文本：字号 20、居中对齐。
        /// <b>为什么不设 <c>FontStyle</c></b>：需求只要求字号与对齐；字重对字符网格的对齐没有任何帮助，
        /// 多设一个属性只会增加与旧版 Unity 的兼容面。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="content">初始内容（空串，等控制器首帧渲染）。</param>
        /// <returns>网格文本控件。</returns>
        private static Text CreateGridText(string name, Transform parent, string content)
        {
            Text text = CreateLabel(name, parent, content);
            text.fontSize = GridFontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
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
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = ButtonFontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);

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

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">整数值。</param>
        private static void SetInt(Object target, string fieldName, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).intValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>float</c> 型 <c>[SerializeField]</c> 字段（自动解法间隔等）。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">浮点值。</param>
        private static void SetFloat(Object target, string fieldName, float value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).floatValue = value;
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
