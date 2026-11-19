using AIFramework.Adapters.Unity.Lobby;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键生成大厅场景：Canvas + 签到/商店/任务/邮件四个面板，并把控件引用喂给 HUD。
    /// <para>
    /// <b>为什么用脚本建场景而不是手写 <c>.unity</c> YAML</b>：手写 YAML 要拼 fileID/GUID，
    /// 错一个就整场景打不开，且 AI 无法可靠修改；脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// 生成后按 Play 即可：控制台会打印装配状态，点击按钮走真实切片链路（签到/商店/任务/邮件）。
    /// </para>
    /// </summary>
    public static class LobbySceneBuilder
    {
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";
        private const string ScenePath = SceneFolder + "/LobbyScene.unity";

        private const int LabelHeight = 28;
        private const int RowHeight = 34;
        private const int ListHeight = 150;

        [MenuItem("AIFramework/创建大厅场景", priority = 1)]
        public static void CreateLobbyScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            RectTransform root = CreateContainer("LobbyRoot", canvas.transform);
            root.anchorMin = new Vector2(0.05f, 0.02f);
            root.anchorMax = new Vector2(0.95f, 0.98f);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;

            var layout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            Text status = CreateLabel("Status", root, "装载中…");
            SetHeight(status.gameObject, LabelHeight);

            // 签到面板
            Text signInTitle = CreateLabel("SignInTitle", root, "签到");
            SetHeight(signInTitle.gameObject, LabelHeight);

            RectTransform signInGrid = CreateContainer("SignInGrid", root);
            SetHeight(signInGrid.gameObject, ListHeight);
            var gridLayout = signInGrid.gameObject.AddComponent<VerticalLayoutGroup>();
            gridLayout.spacing = 2f;
            gridLayout.childControlHeight = true;
            gridLayout.childForceExpandHeight = false;

            GameObject slotTemplate = CreateRowTemplate("SignInSlotTemplate", signInGrid, "第1天 未开");
            slotTemplate.SetActive(false);

            Button claimButton = CreateButton("ClaimButton", root, "领取今天");
            SetHeight(claimButton.gameObject, RowHeight);

            // 商店面板
            Text shopTitle = CreateLabel("ShopTitle", root, "商店");
            SetHeight(shopTitle.gameObject, LabelHeight);

            Text goldText = CreateLabel("GoldText", root, "金币 0");
            SetHeight(goldText.gameObject, LabelHeight);

            RectTransform shopList = CreateContainer("ShopList", root);
            SetHeight(shopList.gameObject, ListHeight);
            GameObject shopItemTemplate = CreateRowTemplate("ShopItemTemplate", shopList, "商品 0 · 0金");
            shopItemTemplate.SetActive(false);

            // 任务面板
            Text questTitle = CreateLabel("QuestTitle", root, "任务");
            SetHeight(questTitle.gameObject, LabelHeight);

            RectTransform questList = CreateContainer("QuestList", root);
            SetHeight(questList.gameObject, ListHeight);
            GameObject questItemTemplate = CreateRowTemplate("QuestItemTemplate", questList, "任务 0 0/1");
            questItemTemplate.SetActive(false);

            // 邮件面板
            Text mailTitle = CreateLabel("MailTitle", root, "邮件");
            SetHeight(mailTitle.gameObject, LabelHeight);

            RectTransform mailList = CreateContainer("MailList", root);
            SetHeight(mailList.gameObject, ListHeight);
            GameObject mailItemTemplate = CreateRowTemplate("MailItemTemplate", mailList, "邮件 0 附件 0×0");
            mailItemTemplate.SetActive(false);

            Button battleButton = CreateButton("BattleButton", root, "打一场（Realtime 内核）");
            SetHeight(battleButton.gameObject, RowHeight);

            // 控制器与四个皮肤
            var lobbyGo = new GameObject("Lobby");
            var controller = lobbyGo.AddComponent<LobbyController>();
            var signInHud = lobbyGo.AddComponent<SignInHud>();
            var shopHud = lobbyGo.AddComponent<ShopHud>();
            var questHud = lobbyGo.AddComponent<QuestHud>();
            var mailHud = lobbyGo.AddComponent<MailHud>();

            SetReference(controller, "signInHud", signInHud);
            SetReference(controller, "shopHud", shopHud);
            SetReference(controller, "questHud", questHud);
            SetReference(controller, "mailHud", mailHud);
            SetReference(controller, "battleButton", battleButton);
            SetReference(controller, "statusText", status);

            SetReference(signInHud, "grid", signInGrid);
            SetReference(signInHud, "slotTemplate", slotTemplate);
            SetReference(signInHud, "claimButton", claimButton);

            SetReference(shopHud, "goldText", goldText);
            SetReference(shopHud, "list", shopList);
            SetReference(shopHud, "itemTemplate", shopItemTemplate);

            SetReference(questHud, "list", questList);
            SetReference(questHud, "itemTemplate", questItemTemplate);

            SetReference(mailHud, "list", mailList);
            SetReference(mailHud, "itemTemplate", mailItemTemplate);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 大厅场景已生成：" + ScenePath + "，按 Play 运行。");
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
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = label;
            text.fontSize = 16;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(1f, 1f, 1f, 1f);

            return button;
        }

        /// <summary>
        /// 列表行模板：一行 = 背景图 + 按钮 + 居中文字（HUD 用 GetComponentInChildren 找它们）。
        /// 模板保持非激活状态，运行时由 HUD 复制并激活。
        /// </summary>
        private static GameObject CreateRowTemplate(string name, Transform parent, string label)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = new Color(0.15f, 0.15f, 0.18f, 1f);

            go.AddComponent<Button>();

            // 文字必须是子物体：同一个 GameObject 上只能有一个 Graphic（Image 与 Text 都继承 Graphic），
            // 直接挂在根上 Unity 会报 "Can't add 'Text' ... because a 'Image' is already added"。
            AttachLabelChild(go.transform, label, TextAnchor.MiddleLeft);

            SetHeight(go, RowHeight);
            return go;
        }

        /// <summary>给容器挂一个撑满父级的文字子物体（Graphic 互斥规则要求文字单独成物体）。</summary>
        private static void AttachLabelChild(Transform parent, string label, TextAnchor alignment)
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
            text.alignment = alignment;
            text.color = new Color(0.9f, 0.9f, 0.9f, 1f);
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
