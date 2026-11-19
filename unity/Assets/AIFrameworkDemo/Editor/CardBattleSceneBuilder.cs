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
    /// 一键生成卡牌战斗场景：Camera + EventSystem + Canvas（状态行 / 手牌容器 / 手牌模板 / 结束回合按钮）
    /// + 挂好 <see cref="CardBattleController"/> 与 <see cref="CardBattleHud"/> 的 <c>CardBattle</c> 对象。
    /// <para>
    /// <b>为什么用脚本建场景而不是手写 <c>.unity</c> YAML</b>：手写要拼 fileID/GUID，错一个就整场景打不开，
    /// 且 AI 无法可靠修改；脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="SerializedObject"/> 注入</b>：HUD/控制器的字段都是 <c>private</c>，
    /// 场景资产只认序列化数据。用序列化 API 赋值既不需要把字段改成 <c>public</c>，也不需要反射。
    /// </para>
    /// </summary>
    public static class CardBattleSceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/CardBattleScene.unity";

        /// <summary>状态行高度（像素，Canvas 为默认的 ConstantPixelSize 模式）。</summary>
        private const int StatusHeight = 32;

        /// <summary>手牌行高度。</summary>
        private const int HandHeight = 120;

        /// <summary>功能按钮高度。</summary>
        private const int ButtonHeight = 44;

        /// <summary>手牌模板的首选宽度（池化克隆都继承它，保证牌面尺寸一致）。</summary>
        private const int CardWidth = 120;

        /// <summary>手牌模板的首选高度。</summary>
        private const int CardHeight = 88;

        /// <summary>
        /// 生成 <c>Assets/AIFrameworkDemo/Scenes/CardBattleScene.unity</c>。
        /// 生成后按 Play 即可：控制器建宿主、开一局 <c>CardBattle</c>、每帧固定步推进并把状态推给 HUD。
        /// </summary>
        [MenuItem("AIFramework/创建卡牌战斗场景", priority = 4)]
        public static void CreateCardBattleScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("BattleRoot", canvas.transform);
            root.anchorMin = new Vector2(0.05f, 0.05f);
            root.anchorMax = new Vector2(0.95f, 0.95f);
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

            // 状态行：回合 / 能量 / 血量 / 护甲，一行显示。
            Text status = CreateLabel("Status", root, "等待内核…");
            SetHeight(status.gameObject, StatusHeight);

            Text handTitle = CreateLabel("HandTitle", root, "手牌");
            SetHeight(handTitle.gameObject, StatusHeight);

            // 手牌容器：横向排布，模板作为它的子对象（未激活，运行时由 HUD 克隆）。
            RectTransform handRoot = CreateContainer("HandRoot", root);
            SetHeight(handRoot.gameObject, HandHeight);
            var handLayout = handRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
            handLayout.spacing = 8f;
            handLayout.childAlignment = TextAnchor.MiddleLeft;

            GameObject cardTemplate = CreateCardTemplate("CardTemplate", handRoot, "1费 6伤");
            cardTemplate.SetActive(false);

            Button endTurnButton = CreateButton("EndTurnButton", root, "结束回合");
            SetHeight(endTurnButton.gameObject, ButtonHeight);

            // 控制器与 HUD 同挂一个 GameObject：控制器经序列化字段持有 HUD；
            // HUD 侧不加反向序列化字段（契约只列了 4 个字段，且未读取的私有字段会触发 CS0414 = 编译失败），
            // 运行期如需反查控制器，用 GetComponent<CardBattleController>() 即可。
            var battleGo = new GameObject("CardBattle");
            var controller = battleGo.AddComponent<CardBattleController>();
            var hud = battleGo.AddComponent<CardBattleHud>();

            SetReference(controller, "hud", hud);
            SetInt(controller, "levelId", 1);
            SetInt(controller, "randomSeed", 20261120);
            SetInt(controller, "enemyUnitCount", 2);

            SetReference(hud, "statusText", status);
            SetReference(hud, "handRoot", handRoot);
            SetReference(hud, "cardTemplate", cardTemplate);
            SetReference(hud, "endTurnButton", endTurnButton);

            // 返回大厅：SceneNavigator 统一处理"同场景不重载 / 场景缺失不崩"
            Button backButton = CreateButton("BackToLobbyButton", root, "返回大厅");
            SetHeight(backButton.gameObject, 34);
            var navGo = new GameObject("SceneNavigator");
            var navigator = navGo.AddComponent<AIFramework.Adapters.Unity.Scene.SceneNavigator>();
            SetString(navigator, "targetScene", "LobbyScene");
            SetReference(navigator, "button", backButton);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 卡牌战斗场景已生成：" + ScenePath + "，按 Play 运行。");
        }

        private static void CreateCamera()
        {
            var cameraGo = new GameObject("Main Camera");
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            cameraGo.AddComponent<Camera>();
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

        /// <summary>一行文本（状态行、小标题都用它）。</summary>
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

        /// <summary>带背景的按钮（按钮自身的文字是它的子节点）。</summary>
        private static Button CreateButton(string name, Transform parent, string label)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.2f, 0.35f, 0.6f, 1f);

            var button = go.AddComponent<Button>();

            CreateStretchedLabel(go.transform, label);
            return button;
        }

        /// <summary>
        /// 手牌模板：一行 = 背景图 + 按钮 + 撑满自身的居中文字 + 布局尺寸。
        /// 模板保持非激活，运行时由 <see cref="CardBattleHud"/> 克隆并激活。
        /// </summary>
        private static GameObject CreateCardTemplate(string name, Transform parent, string label)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.15f, 0.18f, 0.15f, 1f);

            go.AddComponent<Button>();

            var element = go.AddComponent<LayoutElement>();
            element.preferredWidth = CardWidth;
            element.preferredHeight = CardHeight;

            CreateStretchedLabel(go.transform, label);
            return go;
        }

        /// <summary>
        /// 建一个"撑满父级"的居中文字。<b>为什么显式设锚点</b>：新建的 RectTransform 默认贴在一个点上，
        /// 不设锚点文字会缩在左下角；撑满父级才能让按钮/牌面文字居中。
        /// </summary>
        private static Text CreateStretchedLabel(Transform parent, string label)
        {
            var textGo = new GameObject("Label");
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.SetParent(parent, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);
            return text;
        }

        private static void SetHeight(GameObject target, float height)
        {
            var element = target.AddComponent<LayoutElement>();
            element.preferredHeight = height;
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 对象引用赋值（不依赖字段可见性）。</summary>
        /// <summary>用 SerializedObject 给私有字符串字段赋值。</summary>
        private static void SetString(Object target, string fieldName, string value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetReference(Object target, string fieldName, Object value)
        {
            var serialized = new SerializedObject(target);
            serialized.FindProperty(fieldName).objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>用 SerializedObject 给私有 <c>[SerializeField]</c> 整数字段赋值。</summary>
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
