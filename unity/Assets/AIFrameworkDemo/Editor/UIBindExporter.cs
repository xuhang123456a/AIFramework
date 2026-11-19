#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AIFramework.Adapters.Unity.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UObject = UnityEngine.Object;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 把场景/预制体里的 <see cref="UIBindNode"/> 语义标记导出成 <c>*.bindings.json</c>，
    /// 供 <c>tools/viewgen</c> 生成 <c>I&lt;Panel&gt;View.g.cs</c> 与 <c>&lt;Panel&gt;ViewBinder.g.cs</c>。
    /// <para>
    /// <b>这一步解决什么问题</b>：在它之前，绑定描述要人手写，控件改名/加按钮时场景与描述各改一遍，
    /// 很容易漂移；现在场景是唯一事实源，描述由它导出，<c>--all</c> 还能反过来拦住生成物漂移。
    /// </para>
    /// <para>
    /// <b>输出</b>：<c>Assets/AIFrameworkDemo/Bindings/&lt;Panel&gt;.bindings.json</c>（UTF-8 无 BOM、2 空格缩进、<c>\n</c> 换行、
    /// 按语义名排序），写完 <c>AssetDatabase.Refresh()</c>；再按控制台提示拷进切片目录跑 viewgen。
    /// </para>
    /// <para>
    /// <b>List 的 itemType 为什么不从控件推断</b>：元素类型是纯逻辑侧的 C# 类型（如 <c>SignInSlotViewData</c>），
    /// 场景里没有任何可靠来源；导出器从同名旧描述（切片目录里的 <c>&lt;Panel&gt;.bindings.json</c>）继承
    /// <c>itemType</c> / <c>parameter</c> / 每条 <c>summary</c>，这样重复导出不会丢注释、也不会生成 viewgen 拒绝的描述。
    /// 全新列表第一次接入时，需先在旧描述里给同名绑定补一条 <c>itemType</c>。
    /// </para>
    /// </summary>
    public static class UIBindExporter
    {
        /// <summary>导出目录（相对 Unity 工程根，即 <c>Assets/</c> 的相对路径基准）。</summary>
        private const string BindingsFolder = "Assets/AIFrameworkDemo/Bindings";

        /// <summary>根对象名后缀：<c>SignInPanel</c> → <c>SignIn</c>。</summary>
        private const string PanelSuffix = "Panel";

        /// <summary>
        /// 导出当前选中的面板；未选中任何对象时，导出当前场景里所有带 <see cref="UIViewDescriptor"/> 的面板。
        /// <para>面板根对象必须有 <see cref="UIViewDescriptor"/>；面板名下所有 <see cref="UIBindNode"/>（含未激活）都会被收集。</para>
        /// </summary>
        [MenuItem("AIFramework/导出 UI 绑定描述", priority = 12)]
        public static void ExportSelectedPanel()
        {
            var payloads = new List<PanelPayload>();
            var usedPanels = new HashSet<string>(StringComparer.Ordinal);
            GameObject? selected = Selection.activeGameObject;

            if (selected != null)
            {
                AddPayload(payloads, usedPanels, selected);
            }
            else
            {
                ExportEveryPanelInScene(payloads, usedPanels);
            }

            string projectRoot = GetProjectRoot();
            for (int i = 0; i < payloads.Count; i++)
            {
                PanelPayload payload = payloads[i];
                WritePayload(payload, projectRoot);

                if (payload.InheritedFrom.Length > 0)
                {
                    Debug.Log("[UIBindExporter] 已从 " + payload.InheritedFrom + " 继承 itemType/parameter/summary（控件本身不承载这些字段）。");
                }

                Debug.Log("[UIBindExporter] 已导出 " + payload.AssetPath + "（" + payload.BindingCount + " 条绑定，面板 " + payload.Panel + "）。");
            }

            AssetDatabase.Refresh();
            Debug.Log("[UIBindExporter] 下一步：把 JSON 拷到切片目录并跑 dotnet run --project tools/viewgen -- <json>");
        }

        /// <summary>未选中对象时的路径：从场景每个根向下找描述组件，一个面板一份描述。</summary>
        private static void ExportEveryPanelInScene(List<PanelPayload> payloads, HashSet<string> usedPanels)
        {
            Scene scene = SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();

            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                UIViewDescriptor[] descriptors = root.GetComponentsInChildren<UIViewDescriptor>(true);
                if (descriptors.Length == 0)
                {
                    if (root.GetComponentsInChildren<UIBindNode>(true).Length > 0)
                    {
                        throw new InvalidOperationException(
                            "根对象 '" + root.name + "' 下有 UIBindNode，但没有任何 UIViewDescriptor：请在面板根对象上挂 UIViewDescriptor 并填写 panelName / viewNamespace（summary 可选）。");
                    }

                    continue;
                }

                for (int k = 0; k < descriptors.Length; k++)
                {
                    AddPayload(payloads, usedPanels, descriptors[k].gameObject);
                }
            }

            if (payloads.Count == 0)
            {
                throw new InvalidOperationException(
                    "当前场景没有可导出的面板：请给面板根对象挂 UIViewDescriptor，或在 Hierarchy 里选中面板根对象后重试。");
            }
        }

        /// <summary>构建并登记一份面板描述；同一面板名只能出现一次（否则会互相覆盖输出文件）。</summary>
        private static void AddPayload(List<PanelPayload> payloads, HashSet<string> usedPanels, GameObject panelRoot)
        {
            PanelPayload payload = BuildPayload(panelRoot);
            if (!usedPanels.Add(payload.Panel))
            {
                throw new InvalidOperationException(
                    "有两个面板都会导出到 " + payload.AssetPath + "：请检查 UIViewDescriptor.panelName 是否重复。");
            }

            payloads.Add(payload);
        }

        /// <summary>把一个面板根对象翻译成待写入的描述（校验全部在此完成，写盘前不会留下半份文件）。</summary>
        private static PanelPayload BuildPayload(GameObject panelRoot)
        {
            UIViewDescriptor descriptor = panelRoot.GetComponent<UIViewDescriptor>();
            if (descriptor == null)
            {
                throw new InvalidOperationException(
                    "对象 '" + panelRoot.name + "' 上没有 UIViewDescriptor：导出器要求面板根对象带 UIViewDescriptor（panelName / viewNamespace / summary）。");
            }

            string panel = ResolvePanelName(panelRoot.name, descriptor.PanelName);
            if (!IsIdentifier(panel))
            {
                throw new InvalidOperationException(
                    "面板名 '" + panel + "' 不是合法类型名（字母或下划线开头，只含字母数字下划线）：" +
                    (string.IsNullOrWhiteSpace(descriptor.PanelName)
                        ? "根对象名 '" + panelRoot.name + "' 去掉后缀 '" + PanelSuffix + "' 后无法作为面板名，请在 UIViewDescriptor.panelName 里显式填写（如 SignIn）。"
                        : "请修改 UIViewDescriptor.panelName。"));
            }

            string viewNamespace = descriptor.ViewNamespace.Trim();
            if (!IsNamespace(viewNamespace))
            {
                throw new InvalidOperationException(
                    "面板 '" + panel + "' 的 UIViewDescriptor.viewNamespace 不合法：'" + descriptor.ViewNamespace + "'，应形如 AIFramework.Features.DailySignIn。");
            }

            UIBindNode[] nodes = panelRoot.GetComponentsInChildren<UIBindNode>(true);
            if (nodes.Length == 0)
            {
                throw new InvalidOperationException(
                    "面板 '" + panel + "' 下没有任何 UIBindNode：请给按钮/文本/列表容器挂 UIBindNode 并填写语义名。");
            }

            Dictionary<string, List<UIBindNode>> nodesByName = GroupByName(panel, nodes);
            var sortedNames = new List<string>(nodesByName.Keys);
            sortedNames.Sort(StringComparer.Ordinal);
            ThrowOnDuplicateNames(panel, sortedNames, nodesByName);

            InheritedDescription inherited = LoadInheritedDescription(panel);
            string summary = descriptor.Summary.Trim();
            if (summary.Length == 0)
            {
                summary = inherited.PanelSummary;
            }

            var bindings = new List<ExportBinding>(sortedNames.Count);
            for (int i = 0; i < sortedNames.Count; i++)
            {
                string name = sortedNames[i];
                UIBindNode node = nodesByName[name][0];
                ValidateTarget(panel, name, node);

                InheritedBinding old = inherited.Find(name);
                string itemType = string.Empty;
                if (node.Kind == UIBindKind.List)
                {
                    itemType = old.ItemType;
                    if (itemType.Length == 0)
                    {
                        throw new InvalidOperationException(
                            "面板 '" + panel + "' 的 List 绑定 '" + name + "' 缺少 itemType：viewgen 要求 List 必填元素类型，而控件推断不出 C# 类型。请在同名旧描述（src 下的 " + panel + ".bindings.json）里给 '" + name + "' 补一条 itemType 后重试。");
                    }

                    if (!IsIdentifier(itemType))
                    {
                        throw new InvalidOperationException(
                            "面板 '" + panel + "' 的 List 绑定 '" + name + "' 的 itemType '" + itemType + "' 不是合法类型名。");
                    }
                }

                string parameter = string.Empty;
                if (node.Kind == UIBindKind.EventWithInt && old.Parameter.Length > 0)
                {
                    parameter = old.Parameter;
                    if (!IsIdentifier(parameter) || char.IsUpper(parameter[0]))
                    {
                        throw new InvalidOperationException(
                            "面板 '" + panel + "' 的 EventWithInt 绑定 '" + name + "' 的参数名 '" + parameter + "' 不合法（小写字母或下划线开头的标识符）。");
                    }
                }

                bindings.Add(new ExportBinding(node.Kind, name, itemType, parameter, old.Summary));
            }

            string assetPath = BindingsFolder + "/" + panel + ".bindings.json";
            string json = BuildJson(panel, viewNamespace, summary, bindings);
            return new PanelPayload(panel, assetPath, json, bindings.Count, inherited.SourcePath);
        }

        /// <summary>面板名：显式字段优先，否则根对象名去掉后缀 <c>Panel</c>。</summary>
        private static string ResolvePanelName(string rootName, string explicitName)
        {
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                return explicitName.Trim();
            }

            string trimmed = rootName.Trim();
            if (trimmed.Length > PanelSuffix.Length && trimmed.EndsWith(PanelSuffix, StringComparison.Ordinal))
            {
                return trimmed.Substring(0, trimmed.Length - PanelSuffix.Length);
            }

            return trimmed;
        }

        /// <summary>按语义名分组；空名/非法名在这里就报错（不带病进入排序与重名检查）。</summary>
        private static Dictionary<string, List<UIBindNode>> GroupByName(string panel, UIBindNode[] nodes)
        {
            var nodesByName = new Dictionary<string, List<UIBindNode>>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Length; i++)
            {
                UIBindNode node = nodes[i];
                string name = node.SemanticName.Trim();
                if (name.Length == 0)
                {
                    throw new InvalidOperationException(
                        "面板 '" + panel + "' 里节点 '" + node.gameObject.name + "' 的 UIBindNode.semanticName 为空：请填写语义名（如 ClaimClicked）。");
                }

                if (!IsIdentifier(name))
                {
                    throw new InvalidOperationException(
                        "面板 '" + panel + "' 的语义名 '" + name + "' 不合法（字母或下划线开头，只含字母数字下划线）。");
                }

                List<UIBindNode>? bucket = null;
                if (!nodesByName.TryGetValue(name, out bucket) || bucket == null)
                {
                    bucket = new List<UIBindNode>();
                    nodesByName.Add(name, bucket);
                }

                bucket.Add(node);
            }

            return nodesByName;
        }

        /// <summary>重名直接中止：同名会生成两个同名成员，viewgen 与 diff 都会失真。</summary>
        private static void ThrowOnDuplicateNames(
            string panel, List<string> sortedNames, Dictionary<string, List<UIBindNode>> nodesByName)
        {
            var duplicates = new List<string>();
            for (int i = 0; i < sortedNames.Count; i++)
            {
                List<UIBindNode> bucket = nodesByName[sortedNames[i]];
                if (bucket.Count < 2)
                {
                    continue;
                }

                var owners = new StringBuilder();
                for (int k = 0; k < bucket.Count; k++)
                {
                    if (k > 0)
                    {
                        owners.Append('、');
                    }

                    owners.Append(bucket[k].gameObject.name);
                }

                duplicates.Add(sortedNames[i] + "（" + bucket.Count + " 个：" + owners + "）");
            }

            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    "面板 '" + panel + "' 的 UIBindNode 语义名重复，无法生成稳定描述：" + string.Join("；", duplicates));
            }
        }

        /// <summary>校验 kind 与 target 的真实控件类型是否匹配（Runtime 侧不引用 UGUI，所以判定放在这里）。</summary>
        private static void ValidateTarget(string panel, string semanticName, UIBindNode node)
        {
            UObject? target = node.Target;
            if (target == null)
            {
                throw new InvalidOperationException(
                    "面板 '" + panel + "' 的绑定 '" + semanticName + "' 没有 target：请在 Inspector 里把控件拖给 UIBindNode.target。");
            }

            switch (node.Kind)
            {
                case UIBindKind.Event:
                case UIBindKind.EventWithInt:
                    if (!HasComponent<Button>(target))
                    {
                        throw new InvalidOperationException(
                            "面板 '" + panel + "' 的绑定 '" + semanticName + "' 是 " + node.Kind + "，target 必须是 Button（或挂着 Button 的物体）；当前是 " + Describe(target) + "。");
                    }

                    break;

                case UIBindKind.Text:
                case UIBindKind.Number:
                    if (!HasComponent<Text>(target))
                    {
                        throw new InvalidOperationException(
                            "面板 '" + panel + "' 的绑定 '" + semanticName + "' 是 " + node.Kind + "，target 必须是 Text（或挂着 Text 的物体）；当前是 " + Describe(target) + "。");
                    }

                    break;

                case UIBindKind.List:
                    if (!(target is Transform))
                    {
                        throw new InvalidOperationException(
                            "面板 '" + panel + "' 的绑定 '" + semanticName + "' 是 List，target 必须是列表容器 Transform（RectTransform 亦可）；当前是 " + Describe(target) + "。");
                    }

                    break;

                case UIBindKind.Visible:
                    // 显隐对任何目标都成立：只要求 target 非空（上面已检查）。
                    break;

                default:
                    throw new InvalidOperationException(
                        "面板 '" + panel + "' 的绑定 '" + semanticName + "' 使用了未知 UIBindKind：" + node.Kind);
            }
        }

        /// <summary>目标本身或它所在 GameObject 上是否有 <typeparamref name="T"/>（兼容直接拖组件与拖物体两种标注习惯）。</summary>
        private static bool HasComponent<T>(UObject target) where T : Component
        {
            if (target is T)
            {
                return true;
            }

            if (target is GameObject gameObject)
            {
                return gameObject.GetComponent<T>() != null;
            }

            if (target is Component component)
            {
                return component.GetComponent<T>() != null;
            }

            return false;
        }

        /// <summary>给错误信息用的目标描述（不要 ToString：Unity 对象 ToString 只有名字，分不清类型）。</summary>
        private static string Describe(UObject target)
        {
            if (target is GameObject gameObject)
            {
                return "GameObject '" + gameObject.name + "'";
            }

            if (target is Component component)
            {
                return component.GetType().Name + "（挂在 '" + component.gameObject.name + "'）";
            }

            return target.GetType().Name;
        }

        /// <summary>按固定字段顺序、2 空格缩进拼 JSON；空字段省略，保证同样的场景永远得到同样的文件。</summary>
        private static string BuildJson(string panel, string viewNamespace, string summary, List<ExportBinding> bindings)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"panel\": ").Append(JsonString(panel)).Append(",\n");
            sb.Append("  \"viewNamespace\": ").Append(JsonString(viewNamespace)).Append(",\n");
            if (summary.Length > 0)
            {
                sb.Append("  \"summary\": ").Append(JsonString(summary)).Append(",\n");
            }

            sb.Append("  \"bindings\": [\n");
            for (int i = 0; i < bindings.Count; i++)
            {
                ExportBinding binding = bindings[i];
                sb.Append("    {\n");
                sb.Append("      \"kind\": ").Append(JsonString(binding.Kind.ToString())).Append(",\n");
                sb.Append("      \"name\": ").Append(JsonString(binding.Name));
                if (binding.ItemType.Length > 0)
                {
                    sb.Append(",\n      \"itemType\": ").Append(JsonString(binding.ItemType));
                }

                if (binding.Parameter.Length > 0)
                {
                    sb.Append(",\n      \"parameter\": ").Append(JsonString(binding.Parameter));
                }

                if (binding.Summary.Length > 0)
                {
                    sb.Append(",\n      \"summary\": ").Append(JsonString(binding.Summary));
                }

                sb.Append("\n    }");
                if (i < bindings.Count - 1)
                {
                    sb.Append(',');
                }

                sb.Append('\n');
            }

            sb.Append("  ]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>JSON 字符串转义（只覆盖 UTF-8 文本里可能出现的字符；不吃 &lt; &gt; &amp;）。</summary>
        private static string JsonString(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 读同名旧描述，继承场景推断不出的字段：<c>itemType</c>（List 必填）、<c>parameter</c>、每条与面板 <c>summary</c>。
        /// 查找顺序：<c>src/**/&lt;Panel&gt;.bindings.json</c>（切片的正式描述）→ <c>Assets/AIFrameworkDemo/Bindings/</c>（上次导出）。
        /// </summary>
        private static InheritedDescription LoadInheritedDescription(string panel)
        {
            string[] sliceDescriptions = FindSliceDescriptions(panel);
            for (int i = 0; i < sliceDescriptions.Length; i++)
            {
                InheritedDescription? parsed = TryParseDescription(sliceDescriptions[i]);
                if (parsed != null)
                {
                    return parsed;
                }
            }

            string staged = Path.Combine(GetProjectRoot(), BindingsFolder, panel + ".bindings.json");
            InheritedDescription? stagedParsed = TryParseDescription(staged);
            return stagedParsed ?? new InheritedDescription(string.Empty, string.Empty);
        }

        /// <summary>在仓库 <c>src/</c> 下按面板名找旧描述（同名面板只应有一份，排序只为结果确定）。</summary>
        private static string[] FindSliceDescriptions(string panel)
        {
            string? repoRoot = FindRepoRoot(GetProjectRoot());
            if (repoRoot == null)
            {
                return Array.Empty<string>();
            }

            string srcRoot = Path.Combine(repoRoot, "src");
            if (!Directory.Exists(srcRoot))
            {
                return Array.Empty<string>();
            }

            try
            {
                string[] matches = Directory.GetFiles(srcRoot, panel + ".bindings.json", SearchOption.AllDirectories);
                Array.Sort(matches, StringComparer.Ordinal);
                return matches;
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>解析旧描述；文件不存在/不合法返回 <c>null</c>（继承是可选增强，不能反过来卡住导出）。</summary>
        private static InheritedDescription? TryParseDescription(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            List<string> bindingObjects = ExtractBindingObjects(json);
            if (bindingObjects.Count == 0)
            {
                return null;
            }

            int bindingsIndex = json.IndexOf("\"bindings\"", StringComparison.Ordinal);
            string head = bindingsIndex > 0 ? json.Substring(0, bindingsIndex) : string.Empty;
            var description = new InheritedDescription(path, ReadStringField(head, "summary"));

            for (int i = 0; i < bindingObjects.Count; i++)
            {
                string objectText = bindingObjects[i];
                string name = ReadStringField(objectText, "name");
                if (name.Length == 0)
                {
                    continue;
                }

                description.Add(new InheritedBinding(
                    name,
                    ReadStringField(objectText, "itemType"),
                    ReadStringField(objectText, "parameter"),
                    ReadStringField(objectText, "summary")));
            }

            return description;
        }

        /// <summary>
        /// 取出根对象下第一层的对象文本（即 bindings 数组里的每一项）。
        /// 用括号深度扫描而不是正则：<c>summary</c> 里出现 <c>{</c> <c>}</c> 时正则会把对象切碎。
        /// </summary>
        private static List<string> ExtractBindingObjects(string json)
        {
            var objects = new List<string>();
            int depth = 0;
            int start = -1;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                    if (depth == 2)
                    {
                        start = i;
                    }

                    continue;
                }

                if (c == '}')
                {
                    if (depth == 2 && start >= 0)
                    {
                        objects.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }

                    depth--;
                }
            }

            return objects;
        }

        /// <summary>读一个 JSON 字符串字段；缺失或不是字符串时返回空串。</summary>
        private static string ReadStringField(string text, string field)
        {
            string pattern = @"""" + Regex.Escape(field) + @"""\s*:\s*""((?:\\.|[^""\\])*)""";
            Match match = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
            return match.Success ? UnescapeJson(match.Groups[1].Value) : string.Empty;
        }

        /// <summary>还原 JSON 字符串转义（旧描述是我们自己写的中文摘要，别把 <c>\u</c> 原样带到新文件）。</summary>
        private static string UnescapeJson(string raw)
        {
            if (raw.IndexOf('\\') < 0)
            {
                return raw;
            }

            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char next = raw[++i];
                switch (next)
                {
                    case '"':
                        sb.Append('"');
                        break;
                    case '\\':
                        sb.Append('\\');
                        break;
                    case '/':
                        sb.Append('/');
                        break;
                    case 'b':
                        sb.Append('\b');
                        break;
                    case 'f':
                        sb.Append('\f');
                        break;
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 < raw.Length
                            && int.TryParse(raw.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else
                        {
                            sb.Append("\\u");
                        }

                        break;
                    default:
                        sb.Append('\\').Append(next);
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>写盘：目录用 <c>AssetDatabase</c> 建（让 Unity 立刻认识它），文件用 UTF-8 无 BOM。</summary>
        private static void WritePayload(PanelPayload payload, string projectRoot)
        {
            EnsureFolder(BindingsFolder);
            string absolutePath = Path.Combine(projectRoot, payload.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(absolutePath, payload.Json, new UTF8Encoding(false));
        }

        /// <summary>逐级创建 <c>Assets/</c> 下的目录（<c>AssetDatabase.CreateFolder</c> 不会递归）。</summary>
        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string parent = Path.GetDirectoryName(folder) ?? "Assets";
            parent = parent.Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>Unity 工程根：<c>Application.dataPath</c> 是 <c>&lt;project&gt;/Assets</c>，取父目录。</summary>
        private static string GetProjectRoot()
        {
            string? parent = Path.GetDirectoryName(Application.dataPath);
            return string.IsNullOrEmpty(parent) ? Directory.GetCurrentDirectory() : parent;
        }

        /// <summary>仓库根：向上找含 <c>AIFramework.Pure.sln</c> 的目录（与 viewgen 的 RepoLayout 同一判据）。</summary>
        private static string? FindRepoRoot(string startDirectory)
        {
            DirectoryInfo? current = new DirectoryInfo(startDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "AIFramework.Pure.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return null;
        }

        /// <summary>与 <c>tools/viewgen</c> 的 <c>Identifiers.IsValidTypeName</c> 同规则。</summary>
        private static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            char first = value[0];
            if (!char.IsLetter(first) && first != '_')
            {
                return false;
            }

            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>与 viewgen 的 <c>Identifiers.IsValidNamespace</c> 同规则：点分标识符。</summary>
        private static bool IsNamespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string[] parts = value.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (!IsIdentifier(parts[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>一份面板的待写内容（先全部构建并校验，再统一写盘）。</summary>
        private sealed class PanelPayload
        {
            public PanelPayload(string panel, string assetPath, string json, int bindingCount, string inheritedFrom)
            {
                Panel = panel;
                AssetPath = assetPath;
                Json = json;
                BindingCount = bindingCount;
                InheritedFrom = inheritedFrom;
            }

            public string Panel { get; }

            public string AssetPath { get; }

            public string Json { get; }

            public int BindingCount { get; }

            public string InheritedFrom { get; }
        }

        /// <summary>一条导出的绑定；空串表示该字段不写入。</summary>
        private sealed class ExportBinding
        {
            public ExportBinding(UIBindKind kind, string name, string itemType, string parameter, string summary)
            {
                Kind = kind;
                Name = name;
                ItemType = itemType;
                Parameter = parameter;
                Summary = summary;
            }

            public UIBindKind Kind { get; }

            public string Name { get; }

            public string ItemType { get; }

            public string Parameter { get; }

            public string Summary { get; }
        }

        /// <summary>从旧描述继承来的一条绑定元数据。</summary>
        private sealed class InheritedBinding
        {
            public InheritedBinding(string name, string itemType, string parameter, string summary)
            {
                Name = name;
                ItemType = itemType;
                Parameter = parameter;
                Summary = summary;
            }

            public string Name { get; }

            public string ItemType { get; }

            public string Parameter { get; }

            public string Summary { get; }
        }

        /// <summary>旧描述里能用得上的字段；按名字查找，找不到给空值。</summary>
        private sealed class InheritedDescription
        {
            private static readonly InheritedBinding Empty = new InheritedBinding(string.Empty, string.Empty, string.Empty, string.Empty);
            private readonly Dictionary<string, InheritedBinding> _bindings = new Dictionary<string, InheritedBinding>(StringComparer.Ordinal);

            public InheritedDescription(string sourcePath, string panelSummary)
            {
                SourcePath = sourcePath;
                PanelSummary = panelSummary;
            }

            public string SourcePath { get; }

            public string PanelSummary { get; }

            public void Add(InheritedBinding binding)
            {
                _bindings[binding.Name] = binding;
            }

            public InheritedBinding Find(string name)
            {
                InheritedBinding? found;
                _bindings.TryGetValue(name, out found);
                return found ?? Empty;
            }
        }
    }
}
