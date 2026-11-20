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
    /// 一键生成视觉小说场景：Camera + EventSystem + Canvas + 根容器（<see cref="VerticalLayoutGroup"/>）+
    /// 说话人 / 正文 / 状态行三个文本 + "继续"按钮 + 3 个选项按钮 + "返回大厅" +
    /// 挂控制器/皮肤的 <c>VisualNovel</c> 物体。
    /// <para>
    /// <b>为什么不手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个整场景打不开，AI 也无法可靠改；
    /// 脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>与内核的分工</b>：这里只搭"能读能点"的壳，不含任何剧情规则——当前节点、可用选项
    /// （条件是否满足）、推进语义、结局判定全在内核 <c>VisualNovelWorld</c> 里；
    /// 场景脚本只负责把控件引用喂给 <see cref="VisualNovelController"/> 与 <see cref="VisualNovelHud"/>。
    /// </para>
    /// <para>
    /// <b>为什么选项按钮是 3 个固定槽位</b>：内置演示剧本的 Choice 节点最多 3 个可用选项
    /// （开局 3 选 1；汇合点在"已拿到钥匙"时也是 3 个）。HUD 只按下标 <c>0..ChoiceCount-1</c> 点亮，
    /// 用不到的槽位自己 <c>SetActive(false)</c>，因此场景里不需要池化也不需要动态 <c>Instantiate</c>。
    /// </para>
    /// <para>
    /// <b>高度预算</b>：根容器只占屏高 90%（1280×720 下约 648px）。说话人 44 + 正文 260 + 状态行 44 +
    /// 推进按钮 40 + 3 个选项 120 + 返回 40 + 间距/内边距 ≈ 614px，刚好留出余量；
    /// 再往上加就会让 <see cref="VerticalLayoutGroup"/> 按最小高度把正文压扁（真机上表现为整屏裁切）。
    /// </para>
    /// </summary>
    public static class VisualNovelSceneBuilder
    {
        /// <summary>场景目录（与其它场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>视觉小说场景路径。</summary>
        private const string ScenePath = SceneFolder + "/VisualNovelScene.unity";

        /// <summary>返回大厅的目标场景名（必须与 Build Settings 里的文件名一致，不带后缀）。</summary>
        private const string LobbySceneName = "LobbyScene";

        /// <summary>根容器物体名（与控制器挂载物体分开，方便在 Hierarchy 里分辨"壳"与"脑"）。</summary>
        private const string RootObjectName = "VisualNovelRoot";

        /// <summary>控制器与皮肤的挂载物体名（场景装配校验按组件类型找，不依赖名字，这里只为可读性）。</summary>
        private const string ControllerObjectName = "VisualNovel";

        /// <summary>选项按钮数量（= HUD 的槽位数，见类型注释）。</summary>
        private const int ChoiceButtonCount = 3;

        /// <summary>说话人行高度（像素）：一行 22 号字 + 行距余量。</summary>
        private const float SpeakerHeight = 44f;

        /// <summary>正文高度（像素）：18 号字约 12 行；演示剧本每句都短，留这么多是给换行长句的余量。</summary>
        private const float BodyHeight = 260f;

        /// <summary>状态行高度（像素）：一行 16 号字。</summary>
        private const float StatusHeight = 44f;

        /// <summary>按钮高度（像素）：与其它玩法场景的 38~40 一致。</summary>
        private const float ButtonHeight = 40f;

        /// <summary>根容器里各行的间距（像素）。</summary>
        private const float RowSpacing = 6f;

        /// <summary>说话人字号。</summary>
        private const int SpeakerFontSize = 22;

        /// <summary>正文字号。</summary>
        private const int BodyFontSize = 18;

        /// <summary>状态行字号。</summary>
        private const int StatusFontSize = 16;

        /// <summary>按钮文字字号。</summary>
        private const int ButtonFontSize = 16;

        /// <summary>默认随机种子（与其它玩法场景保持一致）。</summary>
        private const int DefaultRandomSeed = 20261120;

        /// <summary>默认不自动播放（与 <c>VisualNovelController.autoPlay</c> 的默认值一致：剧情要自己点）。</summary>
        private const bool DefaultAutoPlay = false;

        /// <summary>
        /// 默认自动播放间隔（与 <c>VisualNovelController.autoAdvanceSeconds</c> 的默认值一致）。
        /// <b>Unity 序列化的是字段当前值</b>，所以这里必须显式注入——否则字段默认值将来改了，
        /// 已生成的场景仍停在旧数字上。
        /// </summary>
        private const float DefaultAutoAdvanceSeconds = 2f;

        /// <summary>功能按钮底色（继续 / 返回 / 选项以外的按钮）。</summary>
        private static readonly Color ButtonColor = new Color(0.22f, 0.3f, 0.45f, 1f);

        /// <summary>选项按钮底色（比功能按钮更暗，避免整屏都在抢视线）。</summary>
        private static readonly Color ChoiceColor = new Color(0.16f, 0.2f, 0.28f, 1f);

        /// <summary>生成视觉小说场景并保存到 <c>Assets/AIFrameworkDemo/Scenes/VisualNovelScene.unity</c>。</summary>
        [MenuItem("AIFramework/创建视觉小说场景", priority = 24)]
        public static void CreateVisualNovelScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer(RootObjectName, canvas.transform);
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

            Text speaker = CreateLabel("SpeakerText", root, "…", SpeakerFontSize);
            speaker.alignment = TextAnchor.MiddleLeft;
            SetHeight(speaker.gameObject, SpeakerHeight);

            // 正文用左上对齐：多行旁白从同一条基线往下读，居中会让长短句各占一行中心。
            Text body = CreateLabel("BodyText", root, "等待内核…", BodyFontSize);
            body.alignment = TextAnchor.UpperLeft;
            SetHeight(body.gameObject, BodyHeight);

            Text status = CreateLabel("StatusText", root, "节点 - · 已进入 0 步", StatusFontSize);
            status.alignment = TextAnchor.MiddleLeft;
            SetHeight(status.gameObject, StatusHeight);

            Button advanceButton = CreateButton("AdvanceButton", root, "继续", ButtonColor, ButtonFontSize);
            SetHeight(advanceButton.gameObject, ButtonHeight);

            // 3 个选项按钮按下标注入：HUD 的 choiceButtons[i] 就是内核第 i 个"可用"选项。
            var choiceButtons = new Button[ChoiceButtonCount];
            for (int i = 0; i < ChoiceButtonCount; i++)
            {
                choiceButtons[i] = CreateButton(
                    "ChoiceButton" + i, root, "选项 " + (i + 1), ChoiceColor, ButtonFontSize);
                SetHeight(choiceButtons[i].gameObject, ButtonHeight);
            }

            // 返回大厅：跳转逻辑走 SceneNavigator（同场景不重载 / 场景缺失不崩），不塞进控制器。
            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅", ButtonColor, ButtonFontSize);
            SetHeight(backButton.gameObject, ButtonHeight);

            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", LobbySceneName);
            SetReference(navigator, "button", backButton);

            // 控制器与皮肤挂同一个物体：控制器在 Awake 里建宿主并首帧渲染，皮肤只被它调用。
            var novelGo = new GameObject(ControllerObjectName);
            var controller = novelGo.AddComponent<VisualNovelController>();
            var hud = novelGo.AddComponent<VisualNovelHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "randomSeed", DefaultRandomSeed);
            SetBool(controller, "autoPlay", DefaultAutoPlay);
            SetFloat(controller, "autoAdvanceSeconds", DefaultAutoAdvanceSeconds);

            SetReference(hud, "speakerText", speaker);
            SetReference(hud, "bodyText", body);
            SetReference(hud, "statusText", status);
            SetReference(hud, "advanceButton", advanceButton);

            // 数组字段必须走 SerializedProperty：SetReference 只能写单个对象引用，
            // 写法与 BoardGameSceneBuilder 注入 cellButtons 的完全一致（arraySize + 逐元素）。
            SetReferenceArray(hud, "choiceButtons", choiceButtons);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 视觉小说场景已生成：" + ScenePath
                + "，按 Play 运行（默认手动点『继续』推进；勾上 VisualNovel 的 autoPlay 会自动播放台词，"
                + "但选项永远等你自己选）。");
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

        /// <summary>
        /// 建一个文字控件（内置字体、左对齐）。
        /// <para>
        /// <b>已知显示风险（真机未验证）</b>：正文没有挂 <see cref="ContentSizeFitter"/>，也不设
        /// <c>horizontalOverflow</c>，因此超长文本会在 <see cref="BodyHeight"/> 内被裁掉；
        /// 内置演示剧本每句都不超过 2 行，1280×720 下不会触发，但换成小说级长段落时必须改成
        /// "滚动视图 / 自动撑高"，那属于表现层的下一步工作。
        /// </para>
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="content">初始内容。</param>
        /// <param name="fontSize">字号。</param>
        /// <returns>文字控件。</returns>
        private static Text CreateLabel(string name, Transform parent, string content, int fontSize)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = content;
            text.fontSize = fontSize;
            text.color = new Color(0.94f, 0.96f, 0.99f, 1f);
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        /// <summary>
        /// 建一个带文字标签的按钮。<b>文字必须是子物体</b>：同一物体上只能有一个 <c>Graphic</c>。
        /// <para>
        /// <b>Label 必须四向拉伸</b>：脚本新建的 <see cref="RectTransform"/> 默认是 0 尺寸的点锚点，
        /// 不拉伸的话 <see cref="Text"/> 会按 0 宽排版 —— 真机上按钮文字直接看不见。
        /// </para>
        /// <para>
        /// <b>选项按钮的文案由 HUD 写</b>：这里的"选项 1/2/3"只是建场景时的占位，
        /// <see cref="VisualNovelHud.Render"/> 会把它换成内核给的 <c>GetChoice(i).Text</c>。
        /// </para>
        /// </summary>
        /// <param name="name">物体名。</param>
        /// <param name="parent">父级。</param>
        /// <param name="label">按钮文字。</param>
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
        /// 给 <c>[SerializeField]</c> 的对象引用<b>数组</b>字段注入元素（按下标顺序写入）。
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

        /// <summary>设置一个 <c>bool</c> 型 <c>[SerializeField]</c> 字段（自动播放开关等）。</summary>
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
        /// 设置一个 <c>float</c> 型 <c>[SerializeField]</c> 字段（自动播放间隔）。
        /// <b>为什么单独一个方法</b>：<c>SerializedProperty.floatValue</c> 与 <c>intValue</c> 是两套读写，
        /// 把 2 写进 <c>intValue</c> 字段会静默变成 0。
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
