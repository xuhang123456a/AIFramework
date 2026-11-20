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
    /// 一键生成大富翁场景：Camera + EventSystem + Canvas + 根容器（<see cref="VerticalLayoutGroup"/>）+
    /// 状态行 + 4×5 字符盘 + 20 个格子按钮（<see cref="GridLayoutGroup"/> 5 列 × 4 行）+
    /// "掷骰子 / 重新开始 / 返回大厅"三个按钮 + 挂控制器/皮肤的 <c>Monopoly</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能看能点"的壳，不含任何大富翁规则——掷骰、移动、买地、租金、升级、
    /// 税、机会卡、监狱传送、破产淘汰与胜负全在内核（<c>MonopolyWorld</c>）里，
    /// 场景脚本只负责把控件引用喂给 <see cref="MonopolyController"/> 与 <see cref="MonopolyHud"/>。
    /// </para>
    /// <para>
    /// <b>为什么字符盘和按钮网格都留着</b>：字符盘是"一眼看全盘"的读面（也是无头环境可断言的那份数据），
    /// 按钮网格是"点得下去"的写面。两者用同一套顺序（索引 0→19 行优先：第一行 0..4、最后一行 15..19），
    /// 因此玩家看到的第几行第几列就是点到的那一行那一列（渲染换算在 <see cref="MonopolyHud"/> 里，
    /// 本脚本不复制）。
    /// </para>
    /// <para>
    /// <b>为什么 <c>tileButtons</c> 必须按行优先注入</b>：<see cref="GridLayoutGroup"/> 按子物体顺序
    /// 从左到右、从上到下摆格子；<c>tileButtons</c> 是普通数组，HUD 按下标取按钮。
    /// 只有"注入顺序 = 摆放顺序"两者才对得上，否则点 A 格会落到 B 格。
    /// </para>
    /// </summary>
    public static class MonopolySceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>大富翁场景路径。</summary>
        private const string ScenePath = SceneFolder + "/MonopolyScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>格子总数（内核固定 20）。</summary>
        private const int TileCount = 20;

        /// <summary>网格列数（20 格 = 4 行 × 5 列）。</summary>
        private const int GridColumns = 5;

        /// <summary>网格行数。</summary>
        private const int GridRows = TileCount / GridColumns;

        /// <summary>
        /// 格子按钮宽（像素）：5 × 150 + 4 × 6 + 16 = 790px，根容器宽 1152px（1280 的 90%），居中留白充足。
        /// </summary>
        private const float TileWidth = 150f;

        /// <summary>
        /// 格子按钮高（像素）：4 行 × 48 + 3 × 6 + 16 = 226px。
        /// <b>为什么高度卡得这么死</b>：根容器只占屏高 90%（1280×720 下约 648px），
        /// 4 行网格 + 4 行字符盘 + 4 行状态行 + 3 个按钮必须一起塞进去；
        /// 再高 <see cref="VerticalLayoutGroup"/> 就会按最小高度把它们连同字符盘一起压扁
        /// （真机上表现为整屏裁切）。
        /// </summary>
        private const float TileHeight = 48f;

        /// <summary>格子间距（像素）。</summary>
        private const float TileSpacing = 6f;

        /// <summary>网格内边距（像素，四边一致）。</summary>
        private const int GridPadding = 8;

        /// <summary>状态行高度（像素）：4 行 × 16 号字，留出行距余量。</summary>
        private const float StatusHeight = 96f;

        /// <summary>字符盘高度（像素）：4 行 × 26 号字约 120px，留余量避免最后一行被裁掉。</summary>
        private const float BoardTextHeight = 124f;

        /// <summary>根容器里各行的间距（像素）。</summary>
        private const float RowSpacing = 6f;

        /// <summary>普通按钮高度（像素）。</summary>
        private const float ButtonHeight = 40f;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 16;

        /// <summary>字符盘字号（每格 2 字符 × 5 列 = 10 字符一行，26 号字足够看清）。</summary>
        private const int BoardFontSize = 26;

        /// <summary>
        /// 字符盘行距倍率：<b>1 以下</b>才能让 4 行在 124px 内排完（默认行距是字号的约 1.2 倍）。
        /// </summary>
        private const float BoardLineSpacing = 0.9f;

        /// <summary>格子按钮标题字号。</summary>
        private const int TileFontSize = 20;

        /// <summary>普通按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>默认随机种子（与其它玩法场景保持一致）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认参战玩家数（与 <c>MonopolyController.playerCount</c> 的默认值一致）。</summary>
        private const int DefaultPlayerCount = 2;

        /// <summary>默认自动演示（与 <c>MonopolyController.autoPlay</c> 的默认值一致）。</summary>
        private const bool DefaultAutoPlay = true;

        /// <summary>
        /// 默认自动节拍（与 <c>MonopolyController.autoStepSeconds</c> 的默认值一致）。
        /// <b>Unity 序列化的是字段当前值</b>，所以这里必须显式注入——否则字段默认值将来改了，
        /// 已生成的场景仍停在旧数字上。
        /// </summary>
        private const float DefaultAutoStepSeconds = 0.6f;

        /// <summary>普通按钮底色。</summary>
        private static readonly Color ButtonColor = new Color(0.22f, 0.3f, 0.45f, 1f);

        /// <summary>格子按钮底色（比功能按钮更暗，避免整屏都在抢视线）。</summary>
        private static readonly Color TileColor = new Color(0.16f, 0.2f, 0.28f, 1f);

        /// <summary>
        /// 生成大富翁场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/MonopolyScene.unity</c>。
        /// </summary>
        [MenuItem("AIFramework/创建大富翁场景", priority = 23)]
        public static void CreateMonopolyScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("MonopolyRoot", canvas.transform);
            root.anchorMin = new Vector2(0.05f, 0.05f);
            root.anchorMax = new Vector2(0.95f, 0.95f);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = RowSpacing;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text status = CreateLabel("Status", root, "等待内核…");
            SetHeight(status.gameObject, StatusHeight);

            Text boardText = CreateBoardText("BoardText", root, string.Empty);
            SetHeight(boardText.gameObject, BoardTextHeight);

            Button[] tileButtons = CreateTileGrid(root);

            Button rollButton = CreateButton("RollButton", root, "掷骰子", ButtonColor, ButtonFontSize);
            SetHeight(rollButton.gameObject, ButtonHeight);

            Button restartButton = CreateButton("RestartButton", root, "重新开始", ButtonColor, ButtonFontSize);
            SetHeight(restartButton.gameObject, ButtonHeight);

            // 返回大厅：跳转逻辑走 SceneNavigator（同场景不重载 / 场景缺失不崩），不塞进控制器。
            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅", ButtonColor, ButtonFontSize);
            SetHeight(backButton.gameObject, ButtonHeight);

            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", LobbySceneName);
            SetReference(navigator, "button", backButton);

            // 控制器与皮肤挂同一个物体：控制器在 Awake 里建宿主并首帧渲染，皮肤只被它调用。
            var monopolyGo = new GameObject("Monopoly");
            var controller = monopolyGo.AddComponent<MonopolyController>();
            var hud = monopolyGo.AddComponent<MonopolyHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetInt(controller, "playerCount", DefaultPlayerCount);
            SetBool(controller, "autoPlay", DefaultAutoPlay);
            SetFloat(controller, "autoStepSeconds", DefaultAutoStepSeconds);

            SetReference(hud, "statusText", status);
            SetReference(hud, "boardText", boardText);
            SetReference(hud, "rollButton", rollButton);
            SetReference(hud, "restartButton", restartButton);

            // 数组字段必须走 SerializedProperty：SetReference 只能写单个对象引用，
            // 写法与 BoardGameSceneBuilder 注入 cellButtons 的完全一致（arraySize + 逐元素）。
            SetReferenceArray(hud, "tileButtons", tileButtons);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 大富翁场景已生成：" + ScenePath
                + "，按 Play 运行（默认自动演示：每 " + DefaultAutoStepSeconds + " 秒掷一次骰，"
                + DefaultPlayerCount + " 名玩家；想自己掷就把 Monopoly 上的 autoPlay 关掉，点『掷骰子』或任意格子）。");
        }

        /// <summary>
        /// 建 4×5 按钮网格，返回<b>行优先</b>的按钮数组（下标 = 字符盘索引 0..19：
        /// 第 0 行 0..4，第 3 行 15..19）。
        /// <para>
        /// <b>为什么用 <see cref="GridLayoutGroup"/> 而不是 4 个 <c>HorizontalLayoutGroup</c></b>：
        /// 一个布局组就能把 20 个按钮摆成固定 5 列，行高由 <c>cellSize</c> 决定；
        /// 嵌套 4 层横排会让 20 个物体各多一层 RectTransform，真机上完全是白付的开销。
        /// </para>
        /// <para>
        /// <b>为什么容器要显式给高度</b>：<see cref="GridLayoutGroup"/> 在
        /// <see cref="VerticalLayoutGroup"/> 里不会自动把"4 行格子"折成首选高度，
        /// 不显式设 <see cref="LayoutElement.preferredHeight"/> 会被压成 0 高——按钮全部叠在一起。
        /// </para>
        /// </summary>
        /// <param name="parent">父级（挂 <see cref="VerticalLayoutGroup"/> 的根）。</param>
        /// <returns>长度 20、行优先排列的按钮数组。</returns>
        private static Button[] CreateTileGrid(RectTransform parent)
        {
            RectTransform gridRect = CreateContainer("TileGrid", parent);
            SetHeight(gridRect.gameObject, GridPixelHeight());

            var grid = gridRect.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(TileWidth, TileHeight);
            grid.spacing = new Vector2(TileSpacing, TileSpacing);
            grid.padding = new RectOffset(GridPadding, GridPadding, GridPadding, GridPadding);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = GridColumns;
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;

            var buttons = new Button[TileCount];

            // 生成顺序就是摆放顺序，也是注入顺序：三层必须一致，否则点 A 格落到 B 格。
            // 行优先（0..4 一行、5..9 下一行）与内核棋盘索引顺序一致，不需要任何行序翻转。
            for (int index = 0; index < TileCount; index++)
            {
                buttons[index] = CreateTileButton("Tile" + index, gridRect);
            }

            return buttons;
        }

        /// <summary>网格容器的高度（像素）：4 行格子 + 3 道间距 + 上下内边距。</summary>
        /// <returns>首选高度。</returns>
        private static float GridPixelHeight()
        {
            return (GridRows * TileHeight) + ((GridRows - 1) * TileSpacing) + (GridPadding * 2f);
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
        /// <returns>画布组件。</returns>
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
        /// <returns>容器的矩形。</returns>
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

        /// <summary>建一个文字控件（内置字体、左对齐、16 号；字符盘会在此基础上改字号与行距）。</summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="content">初始内容。</param>
        /// <returns>文字控件。</returns>
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
        /// 建字符盘文本：字号 26、行距 0.9、左上对齐（4 行字符从同一条基线开始读）。
        /// <para>
        /// <b>已知显示风险</b>：<c>▣</c>/<c>★</c>/<c>·</c> 在中文字体里宽度不一致（<c>★</c> 几乎是全角、
        /// <c>·</c> 是半角），内置 LegacyRuntime 下 5 列可能看起来不齐。这不影响点击与判定
        /// （按钮网格才是准的），但真机上是否需要换等宽字体仍<b>未验证</b>。
        /// </para>
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="content">初始内容（空串，等控制器首帧渲染）。</param>
        /// <returns>字符盘控件。</returns>
        private static Text CreateBoardText(string name, Transform parent, string content)
        {
            Text text = CreateLabel(name, parent, content);
            text.fontSize = BoardFontSize;
            text.lineSpacing = BoardLineSpacing;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.92f, 0.95f, 0.98f, 1f);
            return text;
        }

        /// <summary>
        /// 建一个带文字标签的按钮。<b>文字必须是子物体</b>：同一物体上只能有一个 <c>Graphic</c>。
        /// <para>
        /// <b>Label 必须四向拉伸</b>：脚本新建的 <see cref="RectTransform"/> 默认是 0 尺寸的点锚点，
        /// 不拉伸的话 <see cref="Text"/> 会按 0 宽排版 —— 真机上按钮文字直接看不见。
        /// </para>
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="label">按钮文字（格子按钮传空串，由 HUD 渲染成类型字符）。</param>
        /// <param name="color">底色。</param>
        /// <param name="fontSize">标签字号。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateButton(string name, Transform parent, string label, Color color, int fontSize)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = color;

            var button = go.AddComponent<Button>();

            var textGo = new GameObject("Label");
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.SetParent(go.transform, false);
            Stretch(textRect);

            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = fontSize;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);

            return button;
        }

        /// <summary>建一个格子按钮（底色更暗、字号更大，标题由 HUD 填充成类型字符）。</summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">网格容器。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateTileButton(string name, Transform parent)
        {
            return CreateButton(name, parent, string.Empty, TileColor, TileFontSize);
        }

        /// <summary>给物体挂 <see cref="LayoutElement"/> 并设首选高度（<see cref="VerticalLayoutGroup"/> 按它排版）。</summary>
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

        /// <summary>
        /// 给 <c>[SerializeField]</c> 的对象引用<b>数组</b>字段注入元素（行优先，按下标顺序写入）。
        /// <para>
        /// <b>为什么不能直接 <c>SerializedProperty.objectReferenceValue</c></b>：数组本身不是对象引用，
        /// 必须先 <c>arraySize</c> 撑够长度，再逐元素取 <c>GetArrayElementAtIndex(i)</c> 写入；
        /// 这套写法与 <c>BoardGameSceneBuilder</c> 注入 <c>cellButtons</c> 的完全一致。
        /// 一次 <c>ApplyModifiedPropertiesWithoutUndo</c> 收尾，中间状态不会被序列化。
        /// </para>
        /// </summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">数组字段名。</param>
        /// <param name="values">按数组下标顺序排列的引用。</param>
        private static void SetReferenceArray(Object target, string fieldName, Object[] values)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            property.arraySize = values.Length;

            for (int i = 0; i < values.Length; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段（种子/玩家数等）。</summary>
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

        /// <summary>
        /// 设置一个 <c>float</c> 型 <c>[SerializeField]</c> 字段（自动节拍）。
        /// <b>为什么单独一个方法</b>：<c>SerializedProperty.floatValue</c> 与 <c>intValue</c> 是两套读写，
        /// 用错会静默把 0.6 写成 0。
        /// </summary>
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
