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
    /// 一键生成塔防场景：Camera + EventSystem + Canvas + 状态行 + 8×8 网格容器 + 格子模板
    /// + 两个选塔按钮 + 挂控制器/皮肤的 <c>TowerDefense</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与三消场景的关键差别</b>：三消的棋盘由内核持有，塔防的棋盘由 Meta 切片持有，
    /// 战斗是"按当前布局开一局"。因此这里只搭"能点能看"的壳，不预置任何塔——
    /// 空盘进 Play 后 HUD 会提示"先建一座塔"，点格子即建成并自动开打。
    /// </para>
    /// </summary>
    public static class TowerDefenseSceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/TowerDefenseScene.unity";

        /// <summary>棋盘列数（同时是 <c>GridLayoutGroup.constraintCount</c>，必须与切片常量一致）。</summary>
        private const int BoardColumns = 8;

        /// <summary>棋盘行数。</summary>
        private const int BoardRows = 8;

        /// <summary>单元格边长（像素）。</summary>
        private const float CellSize = 56f;

        /// <summary>单元格间距（像素）。</summary>
        private const float CellSpacing = 4f;

        /// <summary>棋盘内边距（像素）。</summary>
        private const int BoardPadding = 8;

        /// <summary>状态行高度。</summary>
        private const int LabelHeight = 32;

        /// <summary>选塔按钮宽（像素）。</summary>
        private const float TowerButtonWidth = 220f;

        /// <summary>选塔按钮高（像素）。</summary>
        private const float TowerButtonHeight = 44f;

        /// <summary>选塔按钮间距（像素）。</summary>
        private const float TowerButtonSpacing = 8f;

        /// <summary>选塔按钮行高（按钮 + 内边距）。</summary>
        private const float TowerButtonRowHeight = 56f;

        /// <summary>生成塔防场景到 <c>Assets/AIFrameworkDemo/Scenes/TowerDefenseScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建塔防场景", priority = 6)]
        public static void CreateTowerDefenseScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("TowerDefenseRoot", canvas.transform);
            root.anchorMin = new Vector2(0.05f, 0.05f);
            root.anchorMax = new Vector2(0.95f, 0.95f);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text status = CreateLabel("Status", root, "金币 0 · 塔数 0 · 波次 1 · 帧 0");
            SetHeight(status.gameObject, LabelHeight);

            RectTransform board = CreateContainer("Board", root);
            SetPreferredSize(board.gameObject, BoardPixelWidth(), BoardPixelHeight());

            var grid = board.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(CellSize, CellSize);
            grid.spacing = new Vector2(CellSpacing, CellSpacing);
            grid.padding = new RectOffset(BoardPadding, BoardPadding, BoardPadding, BoardPadding);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = BoardColumns;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;

            // 模板必须是容器子物体且保持非激活：HUD 复制它、命名成 Cell_n 再激活。
            GameObject cellTemplate = CreateCellTemplate("CellTemplate", board);
            cellTemplate.SetActive(false);

            RectTransform towerBar = CreateContainer("TowerBar", root);
            SetPreferredSize(
                towerBar.gameObject,
                (TowerButtonWidth * 2f) + TowerButtonSpacing,
                TowerButtonRowHeight);

            var towerGrid = towerBar.gameObject.AddComponent<GridLayoutGroup>();
            towerGrid.cellSize = new Vector2(TowerButtonWidth, TowerButtonHeight);
            towerGrid.spacing = new Vector2(TowerButtonSpacing, 0f);
            towerGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            towerGrid.constraintCount = 2;
            towerGrid.childAlignment = TextAnchor.MiddleLeft;
            towerGrid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            towerGrid.startAxis = GridLayoutGroup.Axis.Horizontal;

            Button firstButton = CreateTowerButton("TowerButton1", towerBar, "塔1 · 箭塔 100 金", out Image firstImage);
            Button secondButton = CreateTowerButton("TowerButton2", towerBar, "塔2 · 炮塔 250 金", out Image secondImage);

            var towerDefenseGo = new GameObject("TowerDefense");
            var controller = towerDefenseGo.AddComponent<TowerDefenseController>();
            var hud = towerDefenseGo.AddComponent<TowerDefenseHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "levelId", 1);
            SetInt(controller, "randomSeed", 20261120);

            SetReference(hud, "statusText", status);
            SetReference(hud, "boardRoot", board);
            SetReference(hud, "cellTemplate", cellTemplate);
            SetReference(hud, "firstTowerButton", firstButton);
            SetReference(hud, "secondTowerButton", secondButton);
            SetReference(hud, "firstTowerImage", firstImage);
            SetReference(hud, "secondTowerImage", secondImage);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 塔防场景已生成：" + ScenePath
                + "，按 Play 运行（先点格子建塔，建成即自动开打一局实时战斗）。"
                + "注意：需要 StreamingAssets/AIFrameworkDemo/TbTower.bin，由 tools/fixtures 生成。");
        }

        /// <summary>棋盘像素高：行数×格子 + 行间距 + 上下内边距。</summary>
        private static float BoardPixelHeight()
        {
            return (BoardRows * CellSize) + ((BoardRows - 1) * CellSpacing) + (BoardPadding * 2f);
        }

        /// <summary>棋盘像素宽：列数×格子 + 列间距 + 左右内边距。</summary>
        private static float BoardPixelWidth()
        {
            return (BoardColumns * CellSize) + ((BoardColumns - 1) * CellSpacing) + (BoardPadding * 2f);
        }

        private static void CreateCamera()
        {
            var cameraGo = new GameObject("Main Camera");
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
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

        /// <summary>
        /// 格子模板：<c>Image</c>（HUD 改它的 color 表现空格/塔）+ <c>Button</c>（HUD 绑点击）。
        /// 不含文字——塔靠颜色区分，多一个 <c>Text</c> 只会让每个格子多一次重建。
        /// </summary>
        private static GameObject CreateCellTemplate(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.1f, 0.1f, 0.12f, 1f);

            go.AddComponent<Button>();
            return go;
        }

        /// <summary>
        /// 选塔按钮：<c>Image</c>（选中态底色）+ <c>Button</c> + 子物体 <c>Text</c>（按钮文案）。
        /// 子 <c>Text</c> 必须四向拉伸，否则它只有 0 尺寸、文字完全看不见。
        /// </summary>
        private static Button CreateTowerButton(string name, Transform parent, string caption, out Image image)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            image = go.AddComponent<Image>();
            image.color = new Color(0.22f, 0.22f, 0.26f, 1f);

            var button = go.AddComponent<Button>();

            var labelGo = new GameObject("Label");
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.SetParent(go.transform, false);
            Stretch(labelRect);

            var label = labelGo.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = caption;
            label.fontSize = 16;
            label.color = new Color(0.05f, 0.05f, 0.05f, 1f);
            label.alignment = TextAnchor.MiddleCenter;

            return button;
        }

        /// <summary>把 RectTransform 四向锚定到父物体（UI 子元素不拉伸就是 0 尺寸）。</summary>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetHeight(GameObject target, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
        }

        /// <summary>一次挂好 <c>LayoutElement</c> 并同时设首选宽高（同一物体只能有一个 LayoutElement）。</summary>
        private static void SetPreferredSize(GameObject target, float width, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.preferredHeight = height;
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 字段赋值（不依赖字段可见性）。</summary>
        private static void SetReference(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段。</summary>
        private static void SetInt(Object target, string fieldName, int value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).intValue = value;
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
