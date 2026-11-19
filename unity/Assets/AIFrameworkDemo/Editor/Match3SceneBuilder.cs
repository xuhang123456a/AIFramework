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
    /// 一键生成三消场景：Camera + EventSystem + Canvas + 状态行 + 棋盘容器（<c>GridLayoutGroup</c>）
    /// + 格子模板 + 挂控制器/皮肤的 <c>Match3</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与大厅场景的分工</b>：这里只搭"能看能点"的壳，不含任何三消规则——规则全在内核里，
    /// 场景脚本只负责把控件引用喂给 <see cref="Match3Controller"/> 与 <see cref="Match3Hud"/>。
    /// </para>
    /// </summary>
    public static class Match3SceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/Match3Scene.unity";

        /// <summary>棋盘列数（同时是 <c>GridLayoutGroup.constraintCount</c>）。</summary>
        private const int BoardColumns = 8;

        /// <summary>棋盘行数。</summary>
        private const int BoardRows = 8;

        /// <summary>单元格边长（像素）。</summary>
        private const float CellSize = 64f;

        /// <summary>单元格间距（像素）。</summary>
        private const float CellSpacing = 4f;

        /// <summary>棋盘内边距（像素）。</summary>
        private const int BoardPadding = 8;

        /// <summary>状态行高度。</summary>
        private const int LabelHeight = 32;

        [MenuItem("AIFramework/创建三消场景", priority = 5)]
        public static void CreateMatch3Scene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("Match3Root", canvas.transform);
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

            Text status = CreateLabel("Status", root, "分数 0/目标 0 · 步数 0");
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

            var match3Go = new GameObject("Match3");
            var controller = match3Go.AddComponent<Match3Controller>();
            var hud = match3Go.AddComponent<Match3Hud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "levelId", 1);
            SetInt(controller, "randomSeed", 20261120);

            SetReference(hud, "statusText", status);
            SetReference(hud, "boardRoot", board);
            SetReference(hud, "cellTemplate", cellTemplate);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 三消场景已生成：" + ScenePath + "，按 Play 运行（点两格交换）。");
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
        /// 格子模板：<c>Image</c>（HUD 改它的 color 表现宝石）+ <c>Button</c>（HUD 绑点击）。
        /// 不含文字——三消靠颜色区分宝石，多一个 <c>Text</c> 只会让每个格子多一次重建。
        /// </summary>
        private static GameObject CreateCellTemplate(string name, Transform parent)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.24f, 1f);

            go.AddComponent<Button>();
            return go;
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
