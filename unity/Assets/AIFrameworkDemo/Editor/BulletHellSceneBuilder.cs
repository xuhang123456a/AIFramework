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
    /// 一键生成弹幕射击场景（T2-5）：俯视相机 + EventSystem + Canvas（状态行 + 重开按钮）
    /// + 战场边框 + 挂控制器/皮肤的 <c>BulletHell</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>相机为什么是俯视正交</b>：战斗平面是 Unity 的 X-Z（<c>RealtimeSceneBinder</c> 把 <c>Vec2</c>
    /// 铺在 y=0 上），不从上往下看就只能看到"所有物体缩成一条横线"。相机摆在高处绕 X 轴转 90°，
    /// 正交尺寸用默认值 5（可见半高 5 世界单位），正好罩住 Z∈[-4.5, 4.5] 的战场；
    /// 屏幕上方对应世界 +Z，也就是弹幕的出生边。
    /// </para>
    /// <para>
    /// <b>场上没有任何战斗物体</b>：单位与每一颗弹幕都在运行时由 <see cref="BulletHellController"/>
    /// 经 <c>IRealtimeSandbox</c> 布场、再由 <see cref="RealtimeSceneBinder"/> 变成已登记碰撞体的图元。
    /// 本脚本只摆"不会变"的东西：相机、UI、边框。
    /// </para>
    /// <para>
    /// <b>边框常量与控制器重复</b>：<see cref="FieldHalfWidth"/> / <see cref="FieldHalfDepth"/> 必须与
    /// <see cref="BulletHellController"/> 里的同名常量一致，否则会出现"玩家能走出边框"或"边框画在战场外"。
    /// 两者都是 <c>private const</c>（公开面被需求锁死），只能靠这行注释约束。
    /// </para>
    /// </summary>
    public static class BulletHellSceneBuilder
    {
        /// <summary>场景输出目录。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>场景输出路径。</summary>
        private const string ScenePath = SceneFolder + "/BulletHellScene.unity";

        /// <summary>状态行高度（像素）。</summary>
        private const int LabelHeight = 32;

        /// <summary>按钮行高度（像素）。</summary>
        private const int RowHeight = 44;

        /// <summary>相机高度（世界单位）：正交投影下只影响远近裁剪，不影响成像大小。</summary>
        private const float CameraHeight = 20f;

        /// <summary>战场半宽（X 方向），与控制器常量一致。</summary>
        private const float FieldHalfWidth = 6f;

        /// <summary>战场半深（Z 方向），与控制器常量一致。</summary>
        private const float FieldHalfDepth = 4.5f;

        /// <summary>边框的高度：压在战斗平面下方一点，弹幕与单位会浮在它上面。</summary>
        private const float BorderY = -0.2f;

        /// <summary>边框的厚度（Y 方向）。</summary>
        private const float BorderThickness = 0.02f;

        /// <summary>边框的粗细（横向）。</summary>
        private const float BorderWidth = 0.08f;

        /// <summary>默认随机种子：与控制器字段默认值一致，保证"生成即复现"。</summary>
        private const int DefaultSeed = 20261120;

        /// <summary>默认弹幕间隔（秒），与控制器字段默认值一致。</summary>
        private const float DefaultSpawnInterval = 0.15f;

        /// <summary>默认每组弹幕发数，与控制器字段默认值一致。</summary>
        private const int DefaultVolley = 4;

        /// <summary>生成弹幕射击场景到 <c>Assets/AIFrameworkDemo/Scenes/BulletHellScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建弹幕射击场景", priority = 9)]
        public static void CreateBulletHellScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("BulletHellRoot", canvas.transform);

            // UI 拉满父级：四向锚定到整块画布，子元素由 VerticalLayoutGroup 负责撑宽。
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.padding = new RectOffset(16, 16, 16, 16);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text status = CreateLabel("Status", root, "HP 100 · 存活弹幕 0 · 帧 0 · 弹幕中");
            SetHeight(status.gameObject, LabelHeight);

            Button restartButton = CreateButton("RestartButton", root, "重开一局");
            SetHeight(restartButton.gameObject, RowHeight);

            CreateFieldBorders();

            // 控制器与皮肤挂同一个物体：控制器 Awake 里要拿到 hud 才能订阅重开事件。
            var bulletHellGo = new GameObject("BulletHell");
            var controller = bulletHellGo.AddComponent<BulletHellController>();
            var hud = bulletHellGo.AddComponent<BulletHellHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "randomSeed", DefaultSeed);
            SetFloat(controller, "spawnIntervalSeconds", DefaultSpawnInterval);
            SetInt(controller, "projectilesPerVolley", DefaultVolley);

            SetReference(hud, "statusText", status);
            SetReference(hud, "restartButton", restartButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 弹幕射击场景已生成：" + ScenePath
                + "，按 Play 运行（自动走位躲弹幕；活到 1200 帧即生存成功，HP 归零则失败）。"
                + "演示失败路径：Play 中右键 BulletHell 组件的『停止自动走位（演示失败）』。");
        }

        /// <summary>
        /// 俯视正交相机：高度 <see cref="CameraHeight"/>，绕 X 轴 90° 正对战斗平面（X-Z）。
        /// 正交尺寸刻意用默认值（5），战场半深 4.5 因此正好落在画面内，不需要额外设置。
        /// </summary>
        private static void CreateCamera()
        {
            var cameraGo = new GameObject("Main Camera");
            cameraGo.transform.position = new Vector3(0f, CameraHeight, 0f);
            cameraGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
        }

        /// <summary>EventSystem 是 uGUI 按钮能点的前提（缺了它按钮只会显示不能点）。</summary>
        private static void EnsureEventSystem()
        {
            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<EventSystem>();
            eventSystemGo.AddComponent<StandaloneInputModule>();
        }

        /// <summary>覆盖式画布（本场景是纯 2D 演示，不需要相机投影）。</summary>
        private static Canvas CreateCanvas()
        {
            var canvasGo = new GameObject("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        /// <summary>
        /// 战场四边：细长立方体拼出的矩形框。<b>它们只是视觉参照</b>——
        /// 内核没有边界概念，玩家可达范围由控制器自己夹；
        /// 这四个立方体上的 BoxCollider 没有登记进 <c>UnitySpatialQuery</c> 的"碰撞体 → 句柄"映射表，
        /// 因此永远不会被当成命中目标（查询会先过滤掉未登记的碰撞体）。
        /// </summary>
        private static void CreateFieldBorders()
        {
            var field = new GameObject("Field");

            CreateBorder(field.transform, "BorderNorth",
                new Vector3(0f, BorderY, FieldHalfDepth), new Vector3((FieldHalfWidth * 2f) + BorderWidth, BorderThickness, BorderWidth));
            CreateBorder(field.transform, "BorderSouth",
                new Vector3(0f, BorderY, -FieldHalfDepth), new Vector3((FieldHalfWidth * 2f) + BorderWidth, BorderThickness, BorderWidth));
            CreateBorder(field.transform, "BorderEast",
                new Vector3(FieldHalfWidth, BorderY, 0f), new Vector3(BorderWidth, BorderThickness, FieldHalfDepth * 2f));
            CreateBorder(field.transform, "BorderWest",
                new Vector3(-FieldHalfWidth, BorderY, 0f), new Vector3(BorderWidth, BorderThickness, FieldHalfDepth * 2f));
        }

        /// <summary>造一根边框条：内置 Cube 图元 + 缩放 + 颜色。</summary>
        /// <param name="parent">父节点。</param>
        /// <param name="name">对象名（Hierarchy 排查用）。</param>
        /// <param name="position">世界坐标。</param>
        /// <param name="scale">本地缩放（立方体默认 1 单位边长，所以缩放即尺寸）。</param>
        private static void CreateBorder(Transform parent, string name, Vector3 position, Vector3 scale)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = name;
            bar.transform.SetParent(parent, false);
            bar.transform.position = position;
            bar.transform.localScale = scale;

            Renderer? renderer = bar.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = new Color(0.15f, 0.45f, 0.75f, 1f);
            }
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
            text.fontSize = 20;
            text.color = new Color(1f, 1f, 1f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        /// <summary>
        /// 按钮：<c>Image</c>（底色）+ <c>Button</c> + 子物体 <c>Text</c>（文案）。
        /// 子 <c>Text</c> 必须四向拉伸，否则它只有 0 尺寸、文字完全看不见。
        /// </summary>
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
            Stretch(textRect);

            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = 18;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);

            return button;
        }

        /// <summary>把 RectTransform 四向锚定到父物体（UI 子元素不拉伸就是 0 尺寸）。</summary>
        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void SetHeight(GameObject target, float height)
        {
            var element = target.AddComponent<LayoutElement>();
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

        /// <summary>设置一个 <c>float</c> 型 <c>[SerializeField]</c> 字段。</summary>
        private static void SetFloat(Object target, string fieldName, float value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).floatValue = value;
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
