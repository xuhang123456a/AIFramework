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
    /// 一键生成肉鸽卡牌场景：Camera + EventSystem + Canvas + 状态行 + 地图行 + 手牌文本 +
    /// "重开 / 前进 / 结束回合" + 5 个手牌按钮 + "返回大厅" + 挂控制器/皮肤的 <c>Roguelike</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能看能点"的壳，不含任何肉鸽规则——地图形状、能量与费用、伤害与格挡、
    /// 商店与营地、胜负全在内核里，场景脚本只负责把控件引用喂给 <see cref="RoguelikeController"/> 与
    /// <see cref="RoguelikeHud"/>。
    /// </para>
    /// <para>
    /// <b>为什么地图只有一行</b>：地图是 8 层线性结构，一行字符就能表达"哪层是战斗/精英/商店/营地/Boss、
    /// 哪几层已经走过"（已访问用 <c>·</c> 覆盖），不需要 8 个图片节点与一套连线。
    /// </para>
    /// <para>
    /// <b>5 个手牌按钮为什么横排</b>：内核每回合抽 5 张，横排让"第几张牌"与按钮顺序一眼对齐；
    /// 纵向排会占掉 5×40px，把 5 行手牌文本挤出常见 Game 视图的高度。
    /// </para>
    /// </summary>
    public static class RoguelikeSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>肉鸽场景路径。</summary>
        private const string ScenePath = SceneFolder + "/RoguelikeScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>状态行高度（像素）：两行 × 18 号字，留出行距余量。</summary>
        private const float StatusHeight = 56f;

        /// <summary>地图行高度（像素）：一行 × 20 号字（<c>⚔✦$♥☠·</c> 都是单宽字符）。</summary>
        private const float MapHeight = 32f;

        /// <summary>手牌文本高度（像素）：5 行 × 16 号字约 95px，留出余量避免最后一行被裁掉。</summary>
        private const float HandHeight = 112f;

        /// <summary>按钮行高度（像素）。</summary>
        private const float RowHeight = 44f;

        /// <summary>手牌按钮的首选宽度（像素）：横排 5 个共 574px，不会被布局组压成 0 宽。</summary>
        private const float CardButtonWidth = 110f;

        /// <summary>手牌按钮的首选高度（像素）。</summary>
        private const float CardButtonHeight = 44f;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 18;

        /// <summary>地图行字号（符号要够大才分辨得出种类）。</summary>
        private const int MapFontSize = 20;

        /// <summary>手牌文本字号。</summary>
        private const int HandFontSize = 16;

        /// <summary>按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>默认随机种子（与 <c>RoguelikeController.randomSeed</c> 的默认值一致）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认自动演示（与 <c>RoguelikeController.autoPlay</c> 的默认值一致）。</summary>
        private const bool DefaultAutoPlay = true;

        /// <summary>生成肉鸽卡牌场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/RoguelikeScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建肉鸽卡牌场景", priority = 16)]
        public static void CreateRoguelikeScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("RoguelikeRoot", canvas.transform);
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

            Text status = CreateLabel("Status", root, "等待内核…", StatusFontSize);
            SetHeight(status.gameObject, StatusHeight);

            Text map = CreateLabel("Map", root, string.Empty, MapFontSize);
            map.alignment = TextAnchor.MiddleLeft;
            SetHeight(map.gameObject, MapHeight);

            // 5 行手牌：一个 Text 画满 5 个槽位，行距略压一点，保证 112px 里 5 行都被看见。
            Text hand = CreateLabel("Hand", root, string.Empty, HandFontSize);
            hand.alignment = TextAnchor.MiddleLeft;
            hand.lineSpacing = 0.9f;
            SetHeight(hand.gameObject, HandHeight);

            // 三个操作横排：前进（地图）/ 结束回合（战斗）/ 重开（任何时刻）。
            RectTransform actionRow = CreateContainer("ActionButtons", root);
            SetHeight(actionRow.gameObject, RowHeight);
            var actionLayout = actionRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionLayout.spacing = 6f;
            actionLayout.childAlignment = TextAnchor.MiddleCenter;

            Button enterNodeButton = CreateButton("EnterNodeButton", actionRow, "前进");
            SetPreferredSize(enterNodeButton.gameObject, CardButtonWidth, CardButtonHeight);

            Button endTurnButton = CreateButton("EndTurnButton", actionRow, "结束回合");
            SetPreferredSize(endTurnButton.gameObject, CardButtonWidth, CardButtonHeight);

            Button restartButton = CreateButton("RestartButton", actionRow, "重开这一轮");
            SetPreferredSize(restartButton.gameObject, CardButtonWidth, CardButtonHeight);

            // 5 个手牌按钮横排：按钮只发"第几张牌"，出不出得去、掉多少血全部由内核决定。
            RectTransform cardRow = CreateContainer("CardButtons", root);
            SetHeight(cardRow.gameObject, RowHeight);
            var cardLayout = cardRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            cardLayout.spacing = 6f;
            cardLayout.childAlignment = TextAnchor.MiddleCenter;

            Button card0 = CreateCardButton("Card0Button", cardRow, "第 1 张");
            Button card1 = CreateCardButton("Card1Button", cardRow, "第 2 张");
            Button card2 = CreateCardButton("Card2Button", cardRow, "第 3 张");
            Button card3 = CreateCardButton("Card3Button", cardRow, "第 4 张");
            Button card4 = CreateCardButton("Card4Button", cardRow, "第 5 张");

            // 返回大厅：跳转逻辑走 SceneNavigator（同场景不重载 / 场景缺失不崩），不塞进控制器。
            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅");
            SetHeight(backButton.gameObject, RowHeight);

            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", LobbySceneName);
            SetReference(navigator, "button", backButton);

            // 控制器与皮肤挂同一个物体：控制器在 Awake 里建宿主并首帧渲染，皮肤只被它调用。
            var roguelikeGo = new GameObject("Roguelike");
            var controller = roguelikeGo.AddComponent<RoguelikeController>();
            var hud = roguelikeGo.AddComponent<RoguelikeHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetBool(controller, "autoPlay", DefaultAutoPlay);

            SetReference(hud, "statusText", status);
            SetReference(hud, "mapText", map);
            SetReference(hud, "handText", hand);
            SetReference(hud, "restartButton", restartButton);
            SetReference(hud, "enterNodeButton", enterNodeButton);
            SetReference(hud, "endTurnButton", endTurnButton);
            SetReference(hud, "card0Button", card0);
            SetReference(hud, "card1Button", card1);
            SetReference(hud, "card2Button", card2);
            SetReference(hud, "card3Button", card3);
            SetReference(hud, "card4Button", card4);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 肉鸽卡牌场景已生成：" + ScenePath
                + "，按 Play 运行（默认自动演示：每 0.4 秒推进一个动作；"
                + "关掉 controller 的 autoPlay 后可手动点前进/出牌/结束回合）。");
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
        /// 建一个带文字标签的按钮。<b>文字必须是子物体</b>：同一物体上只能有一个 <c>Graphic</c>。
        /// <para>
        /// <b>为什么给 Label 补四向拉伸锚点</b>：脚本新建的 <see cref="RectTransform"/> 默认是 0 尺寸的点锚点，
        /// 不拉伸的话 <see cref="Text"/> 会按 0 宽排版——真机上按钮文字直接看不见。
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
        /// 建一个手牌按钮：与普通按钮同形，但额外给 <see cref="LayoutElement"/> 的首选宽高。
        /// <b>为什么给首选尺寸</b>：横排布局组一旦关掉"强制撑开宽度"（或画布变窄），没有首选尺寸的按钮
        /// 会被压到 0 宽——玩家能看到 5 个按钮"消失"，却找不到任何报错；给了首选宽高，
        /// 无论布局组怎么配置，每个按钮都至少有手指点得到的尺寸。
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="label">按钮文字。</param>
        /// <returns>按钮组件。</returns>
        private static Button CreateCardButton(string name, Transform parent, string label)
        {
            Button button = CreateButton(name, parent, label);
            SetPreferredSize(button.gameObject, CardButtonWidth, CardButtonHeight);
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

        /// <summary>设置一个 <c>int</c> 型 <c>[SerializeField]</c> 字段（随机种子等）。</summary>
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
