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
    /// 一键生成战棋场景：Camera + EventSystem + Canvas + 状态行 + 8×8 字符盘 +
    /// 8×8 格子按钮（<see cref="GridLayoutGroup"/>）+ "结束回合" / "重新开始" / "返回大厅" +
    /// 挂控制器/皮肤的 <c>Tactics</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能看能点"的壳，不含任何战棋规则——移动合法性、攻击仲裁、敌方 AI、
    /// 胜负与平局全在内核里，场景脚本只负责把控件引用喂给 <see cref="TacticsController"/> 与
    /// <see cref="TacticsHud"/>。
    /// </para>
    /// <para>
    /// <b>为什么字符盘和按钮网格都留着</b>：字符盘是"一眼看全盘"的读面（也是无头环境可断言的那份数据），
    /// 按钮网格是"点得下去"的写面。两者用同一套行序（第一行 = <c>y = Height-1</c>，即敌方底线），
    /// 因此玩家看到的第几行就是点到的那一行（换算在 <see cref="TacticsHud"/> 里，本脚本不复制）。
    /// </para>
    /// <para>
    /// <b>为什么 <c>cellButtons</c> 必须按行优先注入</b>：<see cref="GridLayoutGroup"/> 按子物体顺序
    /// 从左到右、从上到下摆格子；<c>cellButtons</c> 是普通数组，HUD 按下标取按钮。
    /// 只有"注入顺序 = 摆放顺序"两者才对得上，否则点 A 格会落到 B 格。
    /// </para>
    /// </summary>
    public static class TacticsSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>战棋场景路径。</summary>
        private const string ScenePath = SceneFolder + "/TacticsScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>棋盘边长（内核固定 8×8；同时是网格列数与字符盘行数）。</summary>
        private const int BoardSize = 8;

        /// <summary>
        /// 格子按钮的边长（像素）：8 × 30 + 7 × 2 + 16 = 270px。
        /// <b>为什么不是 40+</b>：根容器只占屏高 90%（1280×720 下约 648px），
        /// 8×8 网格 + 8 行字符盘 + 状态行 + 三个按钮必须一起塞进去；比 32 再大会超编，
        /// <see cref="VerticalLayoutGroup"/> 会按最小高度把字符盘一起压扁（真机上表现为整屏裁切）。
        /// </summary>
        private const float CellSize = 30f;

        /// <summary>格子间距（像素）。</summary>
        private const float CellSpacing = 2f;

        /// <summary>网格内边距（像素，四边一致）。</summary>
        private const int GridPadding = 8;

        /// <summary>状态行高度（像素）：两行 × 18 号字，留出行距余量。</summary>
        private const float StatusHeight = 52f;

        /// <summary>字符盘高度（像素）：8 行 × 16 号字、行距 0.85 约 110px，留余量避免最后一行被裁掉。</summary>
        private const float BoardTextHeight = 150f;

        /// <summary>
        /// 根容器里各行的间距（像素）。<b>为什么压到 4</b>：屏高 90%（1280×720 下 648px）要装
        /// 状态行 52 + 字符盘 150 + 网格 270 + 三个按钮 114 + 5 道间距 20 + 上下内边距 24 = 630px，
        /// 只剩 18px 余量；间距取 6 就会超编，最后那个"返回大厅"会被挤出屏幕。
        /// </summary>
        private const float RowSpacing = 4f;

        /// <summary>普通按钮高度（像素）。</summary>
        private const float ButtonHeight = 38f;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 18;

        /// <summary>字符盘字号。</summary>
        private const int BoardFontSize = 16;

        /// <summary>格子按钮标题字号（30px 的格子里放一个字母刚好）。</summary>
        private const int CellFontSize = 18;

        /// <summary>普通按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>字符盘行距倍率（1 = 默认；压到 0.85 让 8 行稳稳落在 <see cref="BoardTextHeight"/> 内）。</summary>
        private const float BoardLineSpacing = 0.85f;

        /// <summary>默认随机种子（与其它玩法场景保持一致；战棋内核不使用它，只进日志与场次口径）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认玩家单位数（3 个：离线探针验证过这是唯一能赢的档位）。</summary>
        private const int DefaultUnitCount = 3;

        /// <summary>默认自动演示（与 <c>TacticsController.autoPlay</c> 的默认值一致）。</summary>
        private const bool DefaultAutoPlay = true;

        /// <summary>默认自动演示节拍（秒/步，与 <c>TacticsController.autoStepSeconds</c> 的默认值一致）。</summary>
        private const float DefaultAutoStepSeconds = 0.5f;

        /// <summary>普通按钮底色。</summary>
        private static readonly Color ButtonColor = new Color(0.22f, 0.3f, 0.45f, 1f);

        /// <summary>格子按钮底色（比功能按钮更暗，避免整屏都在抢视线）。</summary>
        private static readonly Color CellColor = new Color(0.16f, 0.2f, 0.28f, 1f);

        /// <summary>生成战棋场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/TacticsScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建战棋场景", priority = 20)]
        public static void CreateTacticsScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("TacticsRoot", canvas.transform);
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

            Button[] cellButtons = CreateCellGrid(root);

            Button endTurnButton = CreateButton("EndTurnButton", root, "结束回合", ButtonColor, ButtonFontSize);
            SetHeight(endTurnButton.gameObject, ButtonHeight);

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
            var tacticsGo = new GameObject("Tactics");
            var controller = tacticsGo.AddComponent<TacticsController>();
            var hud = tacticsGo.AddComponent<TacticsHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetInt(controller, "unitCount", DefaultUnitCount);
            SetBool(controller, "autoPlay", DefaultAutoPlay);
            SetFloat(controller, "autoStepSeconds", DefaultAutoStepSeconds);

            SetReference(hud, "statusText", status);
            SetReference(hud, "boardText", boardText);
            SetReference(hud, "restartButton", restartButton);
            SetReference(hud, "endTurnButton", endTurnButton);

            // 数组字段必须走 SerializedProperty：SetReference 只能写单个对象引用，
            // 写法与 BoardGameSceneBuilder 注入 cellButtons 的完全一致（arraySize + 逐元素）。
            SetReferenceArray(hud, "cellButtons", cellButtons);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 战棋场景已生成：" + ScenePath
                + "，按 Play 运行（默认自动演示：每 0.5 秒替玩家走一步，3 个玩家单位对 3 个敌方；"
                + "想自己下就把 Tactics 上的 autoPlay 关掉，点棋子选中、点空格移动、点敌人攻击）。");
        }

        /// <summary>
        /// 建 8×8 按钮网格，返回<b>行优先</b>的按钮数组（下标 = 显示行 × Size + x，
        /// 显示行 0 是字符盘第一行 = 内核的 <c>y = Height-1</c>）。
        /// <para>
        /// <b>为什么用 <see cref="GridLayoutGroup"/> 而不是 8 个 <c>HorizontalLayoutGroup</c></b>：
        /// 一个布局组就能把 64 个按钮摆成固定 8 列，行高由 <c>cellSize</c> 决定；
        /// 嵌套 8 层横排会让 64 个物体各多一层 RectTransform，真机上完全是白付的开销。
        /// </para>
        /// <para>
        /// <b>为什么容器要显式给高度</b>：<see cref="GridLayoutGroup"/> 在
        /// <see cref="VerticalLayoutGroup"/> 里不会自动把"8 行格子"折成首选高度，
        /// 不显式设 <see cref="LayoutElement.preferredHeight"/> 会被压成 0 高——按钮全部叠在一起。
        /// </para>
        /// </summary>
        /// <param name="parent">父级（挂 <see cref="VerticalLayoutGroup"/> 的根）。</param>
        /// <returns>长度 64、行优先排列的按钮数组。</returns>
        private static Button[] CreateCellGrid(RectTransform parent)
        {
            RectTransform gridRect = CreateContainer("CellGrid", parent);
            SetHeight(gridRect.gameObject, GridPixelHeight());

            var grid = gridRect.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(CellSize, CellSize);
            grid.spacing = new Vector2(CellSpacing, CellSpacing);
            grid.padding = new RectOffset(GridPadding, GridPadding, GridPadding, GridPadding);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = BoardSize;
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;

            var buttons = new Button[BoardSize * BoardSize];

            // 生成顺序就是摆放顺序，也是注入顺序：三层必须一致，否则点 A 格落到 B 格。
            for (int row = 0; row < BoardSize; row++)
            {
                for (int x = 0; x < BoardSize; x++)
                {
                    int index = (row * BoardSize) + x;
                    buttons[index] = CreateCellButton("Cell" + index, gridRect);
                }
            }

            return buttons;
        }

        /// <summary>网格容器的高度（像素）：8 行格子 + 7 道间距 + 上下内边距。</summary>
        /// <returns>首选高度。</returns>
        private static float GridPixelHeight()
        {
            return (BoardSize * CellSize) + ((BoardSize - 1) * CellSpacing) + (GridPadding * 2f);
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

        /// <summary>建一个文字控件（内置字体、左对齐、18 号；字符盘会在此基础上改字号与行距）。</summary>
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
        /// 建字符盘文本：字号 16、左对齐（8 行字符从同一条基线开始读）、行距压到 0.85。
        /// <para>
        /// <b>为什么左对齐而不是居中</b>：棋盘每行长度固定（8 个字符），左对齐时 64 个字符天然排成矩阵；
        /// 居中会让"每行宽度略不同"的字体把列错开。
        /// </para>
        /// <para>
        /// <b>已知显示风险</b>：<c>·</c>（U+00B7，半角）与字母（半角）宽度不同，内置 LegacyRuntime 下
        /// 空格列可能比字母列窄，字符盘看起来会有点锯齿。这不影响点击与判定（按钮网格才是准的），
        /// 但真机上是否要换等宽字体<b>尚未验证</b>。
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
            text.alignment = TextAnchor.MiddleLeft;
            text.lineSpacing = BoardLineSpacing;
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
        /// <param name="label">按钮文字（格子按钮传空串，由 HUD 每帧刷成 A/a/·）。</param>
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

        /// <summary>建一个格子按钮（底色更暗，标题由 HUD 填充）。</summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">网格容器。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateCellButton(string name, Transform parent)
        {
            return CreateButton(name, parent, string.Empty, CellColor, CellFontSize);
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
        /// 这套写法与 <c>BoardGameSceneBuilder</c> / <c>DemoSceneBuilder</c> 注入数组字段的完全一致。
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

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段（种子/单位数等）。</summary>
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

        /// <summary>设置一个 <c>float</c> 型 <c>[SerializeField]</c> 字段（演示节拍等）。</summary>
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
