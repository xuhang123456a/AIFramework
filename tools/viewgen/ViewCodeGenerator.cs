using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AIFramework.ViewGen
{
    /// <summary>生成目标与命名空间的配置。</summary>
    public sealed class ViewGenOptions
    {
        /// <summary>接口输出路径（纯逻辑侧，<c>.g.cs</c>）。</summary>
        public string InterfacePath { get; set; } = string.Empty;

        /// <summary>Binder 输出路径（引擎适配侧，<c>.g.cs</c>）。</summary>
        public string BinderPath { get; set; } = string.Empty;

        /// <summary>Binder 所在命名空间。</summary>
        public string BinderNamespace { get; set; } = "AIFramework.Adapters.Unity.Views";

        /// <summary>描述文件路径（仅用于写进生成物头部注释）。</summary>
        public string SourcePath { get; set; } = string.Empty;
    }

    /// <summary>一次生成的两份产物。</summary>
    public sealed class GenerationResult
    {
        public GenerationResult(PanelSpec spec, string interfaceContent, string binderContent)
        {
            Spec = spec;
            InterfaceContent = interfaceContent;
            BinderContent = binderContent;
        }

        public PanelSpec Spec { get; }

        public string InterfaceContent { get; }

        public string BinderContent { get; }
    }

    /// <summary>把绑定描述翻译成左右两半代码。纯函数：同样输入永远得到同样输出。</summary>
    public static class ViewCodeGenerator
    {
        /// <summary>生成纯 C# 视图接口（左半边）。</summary>
        public static string GenerateInterface(PanelSpec spec, string sourcePath)
        {
            bool hasEvent = false;
            bool hasList = false;
            for (int i = 0; i < spec.Bindings.Count; i++)
            {
                hasEvent |= spec.Bindings[i].ResolvedKind == BindingKind.Event || spec.Bindings[i].ResolvedKind == BindingKind.EventWithInt;
                hasList |= spec.Bindings[i].ResolvedKind == BindingKind.List;
            }

            var sb = new StringBuilder();
            AppendHeader(sb, sourcePath, spec.InterfaceName);
            sb.Append("#nullable enable\n");

            if (hasEvent)
            {
                sb.Append("using System;\n");
            }

            if (hasList)
            {
                sb.Append("using System.Collections.Generic;\n");
            }

            if (hasEvent || hasList)
            {
                sb.Append('\n');
            }

            sb.Append("namespace ").Append(spec.ViewNamespace).Append('\n');
            sb.Append("{\n");
            sb.Append("    /// <summary>")
              .Append(string.IsNullOrWhiteSpace(spec.Summary) ? spec.Panel + " 面板视图接口：只负责展示与把交互抛给 Presenter。" : spec.Summary)
              .Append("</summary>\n");
            sb.Append("    public interface ").Append(spec.InterfaceName).Append('\n');
            sb.Append("    {\n");

            for (int i = 0; i < spec.Bindings.Count; i++)
            {
                BindingSpec binding = spec.Bindings[i];
                if (i > 0)
                {
                    sb.Append('\n');
                }

                AppendSummary(sb, binding);
                sb.Append("        ").Append(DescribeMember(binding)).Append('\n');
            }

            sb.Append("    }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>生成引擎侧 Binder（右半边）。不含任何业务分支，只做事件透传与渲染转发。</summary>
        public static string GenerateBinder(PanelSpec spec, ViewGenOptions options)
        {
            bool hasEvent = false;
            bool hasList = false;
            for (int i = 0; i < spec.Bindings.Count; i++)
            {
                hasEvent |= spec.Bindings[i].ResolvedKind == BindingKind.Event || spec.Bindings[i].ResolvedKind == BindingKind.EventWithInt;
                hasList |= spec.Bindings[i].ResolvedKind == BindingKind.List;
            }

            var sb = new StringBuilder();
            AppendHeader(sb, options.SourcePath, spec.BinderName);
            sb.Append("#nullable enable\n");

            if (hasEvent)
            {
                sb.Append("using System;\n");
            }

            if (hasList)
            {
                sb.Append("using System.Collections.Generic;\n");
            }

            sb.Append("using ").Append(spec.ViewNamespace).Append(";\n");
            sb.Append("using UnityEngine;\n\n");

            sb.Append("namespace ").Append(options.BinderNamespace).Append('\n');
            sb.Append("{\n");
            sb.Append("    /// <summary>\n");
            sb.Append("    /// ").Append(spec.BinderName).Append("：自动生成的绑定皮肤。\n");
            sb.Append("    /// 只做三件事：把控件交互抛给 Presenter、承接 Presenter 的渲染调用、把细节转发给 partial 钩子。\n");
            sb.Append("    /// 禁止在这里写业务分支（<c>AIFramework.Analyzer</c> 之外的又一道红线，靠 review 守）。\n");
            sb.Append("    /// </summary>\n");
            sb.Append("    [DisallowMultipleComponent]\n");
            sb.Append("    public sealed partial class ").Append(spec.BinderName)
              .Append(" : MonoBehaviour, ").Append(spec.InterfaceName).Append('\n');
            sb.Append("    {\n");

            for (int i = 0; i < spec.Bindings.Count; i++)
            {
                BindingSpec binding = spec.Bindings[i];
                if (i > 0)
                {
                    sb.Append('\n');
                }

                AppendBinderMembers(sb, binding);
            }

            sb.Append("    }\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>生成并落盘（UTF-8 无 BOM、LF 换行）。返回写出的两个绝对路径。</summary>
        public static IReadOnlyList<string> Emit(PanelSpec spec, ViewGenOptions options)
        {
            GenerationResult result = Generate(spec, options);

            WriteFile(options.InterfacePath, result.InterfaceContent);
            WriteFile(options.BinderPath, result.BinderContent);

            return new[] { options.InterfacePath, options.BinderPath };
        }

        /// <summary>只生成不落盘。</summary>
        public static GenerationResult Generate(PanelSpec spec, ViewGenOptions options)
        {
            return new GenerationResult(
                spec,
                GenerateInterface(spec, options.SourcePath),
                GenerateBinder(spec, options));
        }

        /// <summary>
        /// 校验磁盘上的生成物是否与当前描述一致；返回不一致的文件路径列表（空表示无需更新）。
        /// <para>供 CI 使用：有人手改 <c>.g.cs</c> 或改了描述却忘记重新生成时，这里会爆。</para>
        /// </summary>
        public static IReadOnlyList<string> FindStale(PanelSpec spec, ViewGenOptions options)
        {
            GenerationResult result = Generate(spec, options);
            var stale = new List<string>();

            if (!IsSame(options.InterfacePath, result.InterfaceContent))
            {
                stale.Add(options.InterfacePath);
            }

            if (!IsSame(options.BinderPath, result.BinderContent))
            {
                stale.Add(options.BinderPath);
            }

            return stale;
        }

        private static bool IsSame(string path, string expected)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            string actual = File.ReadAllText(path);
            return Normalize(actual) == Normalize(expected);
        }

        /// <summary>换行符归一：仓库里不同机器可能检出 CRLF，生成的永远是 LF。</summary>
        internal static string Normalize(string text)
        {
            return text.Replace("\r\n", "\n");
        }

        private static void WriteFile(string path, string content)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void AppendHeader(StringBuilder sb, string sourcePath, string generatedName)
        {
            sb.Append("// <auto-generated>\n");
            sb.Append("// 本文件由 tools/viewgen 生成，禁止手改（改了会被 CI 的 --check 打回）。\n");
            sb.Append("// 源：" ).Append(string.IsNullOrEmpty(sourcePath) ? "(未提供)" : sourcePath).Append('\n');
            sb.Append("// 重新生成：dotnet run --project tools/viewgen -- \"").Append(sourcePath).Append("\"\n");
            sb.Append("// 生成物：").Append(generatedName).Append('\n');
            sb.Append("// </auto-generated>\n");
        }

        private static void AppendSummary(StringBuilder sb, BindingSpec binding)
        {
            if (string.IsNullOrWhiteSpace(binding.Summary))
            {
                return;
            }

            sb.Append("        /// <summary>").Append(binding.Summary).Append("</summary>\n");
        }

        private static string DescribeMember(BindingSpec binding)
        {
            switch (binding.ResolvedKind)
            {
                case BindingKind.Event:
                    return "event Action? On" + binding.Name + ";";

                case BindingKind.EventWithInt:
                    return "event Action<int>? On" + binding.Name + ";";

                case BindingKind.List:
                    return "void Render" + binding.Name + "(IReadOnlyList<" + binding.ItemType + "> " + Identifiers.ToCamel(binding.Name) + ");";

                case BindingKind.Text:
                    return "void Render" + binding.Name + "(string text);";

                case BindingKind.Number:
                    return "void Render" + binding.Name + "(int value);";

                case BindingKind.Visible:
                    return "void Render" + binding.Name + "(bool visible);";

                default:
                    throw new ViewGenException("未知绑定类型：" + binding.Kind);
            }
        }

        private static void AppendBinderMembers(StringBuilder sb, BindingSpec binding)
        {
            string camel = Identifiers.ToCamel(binding.Name);

            switch (binding.ResolvedKind)
            {
                case BindingKind.Event:
                    sb.Append("        /// <summary>点击事件：由场景把 Button 的 onClick 连到 <c>Raise").Append(binding.Name).Append("</c>。</summary>\n");
                    sb.Append("        public event Action? On").Append(binding.Name).Append(";\n\n");
                    sb.Append("        /// <summary>供 UnityEvent 连接的无参转发方法。</summary>\n");
                    sb.Append("        public void Raise").Append(binding.Name).Append("()\n");
                    sb.Append("        {\n");
                    sb.Append("            On").Append(binding.Name).Append("?.Invoke();\n");
                    sb.Append("        }\n");
                    break;

                case BindingKind.EventWithInt:
                    string parameter = string.IsNullOrWhiteSpace(binding.Parameter) ? "dayIndex" : binding.Parameter;
                    sb.Append("        /// <summary>带序号的点击事件：由场景把控件参数（如格子序号）传进 <c>Raise").Append(binding.Name).Append("</c>。</summary>\n");
                    sb.Append("        public event Action<int>? On").Append(binding.Name).Append(";\n\n");
                    sb.Append("        /// <summary>供 UnityEvent 连接的转发方法。</summary>\n");
                    sb.Append("        public void Raise").Append(binding.Name).Append("(int ").Append(parameter).Append(")\n");
                    sb.Append("        {\n");
                    sb.Append("            On").Append(binding.Name).Append("?.Invoke(").Append(parameter).Append(");\n");
                    sb.Append("        }\n");
                    break;

                case BindingKind.List:
                    sb.Append("        /// <summary>列表容器：池化实例挂载点。</summary>\n");
                    sb.Append("        [SerializeField] private Transform ").Append(camel).Append("Content = null!;\n\n");
                    sb.Append("        /// <summary>列表项预制体（只做容器与显隐，内容由 partial 钩子绑定）。</summary>\n");
                    sb.Append("        [SerializeField] private GameObject ").Append(camel).Append("ItemPrefab = null!;\n\n");
                    sb.Append("        /// <summary>已实例化的列表项池（只增不减，随最大长度稳定）。</summary>\n");
                    sb.Append("        private readonly List<GameObject> _").Append(camel).Append("Items = new List<GameObject>();\n\n");
                    sb.Append("        /// <inheritdoc />\n");
                    sb.Append("        public void Render").Append(binding.Name).Append("(IReadOnlyList<").Append(binding.ItemType).Append("> ").Append(camel).Append(")\n");
                    sb.Append("        {\n");
                    sb.Append("            Ensure").Append(binding.Name).Append("Items(").Append(camel).Append(".Count);\n\n");
                    sb.Append("            for (int i = 0; i < ").Append(camel).Append(".Count; i++)\n");
                    sb.Append("            {\n");
                    sb.Append("                GameObject item = _").Append(camel).Append("Items[i];\n");
                    sb.Append("                if (!item.activeSelf)\n");
                    sb.Append("                {\n");
                    sb.Append("                    item.SetActive(true);\n");
                    sb.Append("                }\n\n");
                    sb.Append("                OnBind").Append(binding.Name).Append("Item(item, ").Append(camel).Append("[i]);\n");
                    sb.Append("            }\n\n");
                    sb.Append("            for (int i = ").Append(camel).Append(".Count; i < _").Append(camel).Append("Items.Count; i++)\n");
                    sb.Append("            {\n");
                    sb.Append("                GameObject item = _").Append(camel).Append("Items[i];\n");
                    sb.Append("                if (item.activeSelf)\n");
                    sb.Append("                {\n");
                    sb.Append("                    item.SetActive(false);\n");
                    sb.Append("                }\n");
                    sb.Append("            }\n");
                    sb.Append("        }\n\n");
                    sb.Append("        /// <summary>\n");
                    sb.Append("        /// 列表项绑定钩子：项目侧用同名 partial 方法实现（控件的文字/图标/可点状态都在那里设）。\n");
                    sb.Append("        /// 没人实现时编译器会把这行调用消掉，方便先跑通再补皮肤。\n");
                    sb.Append("        /// </summary>\n");
                    sb.Append("        partial void OnBind").Append(binding.Name).Append("Item(GameObject item, ").Append(binding.ItemType).Append(" data);\n\n");
                    sb.Append("        private void Ensure").Append(binding.Name).Append("Items(int count)\n");
                    sb.Append("        {\n");
                    sb.Append("            while (_").Append(camel).Append("Items.Count < count)\n");
                    sb.Append("            {\n");
                    sb.Append("                GameObject item = Instantiate(").Append(camel).Append("ItemPrefab, ").Append(camel).Append("Content);\n");
                    sb.Append("                _").Append(camel).Append("Items.Add(item);\n");
                    sb.Append("            }\n");
                    sb.Append("        }\n");
                    break;

                case BindingKind.Text:
                    sb.Append("        /// <inheritdoc />\n");
                    sb.Append("        public void Render").Append(binding.Name).Append("(string text)\n");
                    sb.Append("        {\n");
                    sb.Append("            OnRender").Append(binding.Name).Append("Text(text);\n");
                    sb.Append("        }\n\n");
                    sb.Append("        /// <summary>文本渲染钩子，项目侧 partial 实现。</summary>\n");
                    sb.Append("        partial void OnRender").Append(binding.Name).Append("Text(string text);\n");
                    break;

                case BindingKind.Number:
                    sb.Append("        /// <inheritdoc />\n");
                    sb.Append("        public void Render").Append(binding.Name).Append("(int value)\n");
                    sb.Append("        {\n");
                    sb.Append("            OnRender").Append(binding.Name).Append("Number(value);\n");
                    sb.Append("        }\n\n");
                    sb.Append("        /// <summary>整数渲染钩子，项目侧 partial 实现。</summary>\n");
                    sb.Append("        partial void OnRender").Append(binding.Name).Append("Number(int value);\n");
                    break;

                case BindingKind.Visible:
                    sb.Append("        /// <inheritdoc />\n");
                    sb.Append("        public void Render").Append(binding.Name).Append("(bool visible)\n");
                    sb.Append("        {\n");
                    sb.Append("            OnRender").Append(binding.Name).Append("Visible(visible);\n");
                    sb.Append("        }\n\n");
                    sb.Append("        /// <summary>显隐渲染钩子，项目侧 partial 实现。</summary>\n");
                    sb.Append("        partial void OnRender").Append(binding.Name).Append("Visible(bool visible);\n");
                    break;

                default:
                    throw new ViewGenException("未知绑定类型：" + binding.Kind);
            }
        }
    }
}
