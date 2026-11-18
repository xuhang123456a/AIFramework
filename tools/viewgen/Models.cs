using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIFramework.ViewGen
{
    /// <summary>绑定项类型：决定生成接口成员与 Binder 承接方式。</summary>
    public enum BindingKind
    {
        /// <summary>无参点击事件（按钮）。</summary>
        Event = 0,

        /// <summary>带一个 int 参数的事件（如按格子序号补签）。</summary>
        EventWithInt = 1,

        /// <summary>列表渲染（传入只读列表）。</summary>
        List = 2,

        /// <summary>文本渲染。</summary>
        Text = 3,

        /// <summary>显隐渲染。</summary>
        Visible = 4
    }

    /// <summary>单个绑定项。</summary>
    public sealed class BindingSpec
    {
        /// <summary>绑定类型原始文本（JSON 字段 kind，大小写不敏感；校验后填入 <see cref="ResolvedKind"/>）。</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>解析后的绑定类型；<see cref="BindingSpecReader"/> 校验阶段填充。</summary>
        [JsonIgnore]
        public BindingKind ResolvedKind { get; internal set; }

        /// <summary>语义名（如 ClaimClicked、Slots），决定生成的成员名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>列表元素类型（仅 <see cref="BindingKind.List"/> 必填）。</summary>
        public string? ItemType { get; set; }

        /// <summary>事件参数名（仅 <see cref="BindingKind.EventWithInt"/>，默认 dayIndex）。</summary>
        public string? Parameter { get; set; }

        /// <summary>XML 注释文本（可选）。</summary>
        public string? Summary { get; set; }
    }

    /// <summary>一个面板的绑定描述。</summary>
    public sealed class PanelSpec
    {
        /// <summary>面板名（如 SignIn），用于拼 <c>ISignInView</c> / <c>SignInViewBinder</c>。</summary>
        public string Panel { get; set; } = string.Empty;

        /// <summary>视图契约所在命名空间（纯逻辑侧）。</summary>
        public string ViewNamespace { get; set; } = string.Empty;

        /// <summary>接口 XML 注释。</summary>
        public string? Summary { get; set; }

        /// <summary>绑定项，顺序即生成顺序（刻意保持：便于 diff 与 review）。</summary>
        public List<BindingSpec> Bindings { get; set; } = new List<BindingSpec>();

        /// <summary>接口名：<c>I{Panel}View</c>。</summary>
        [JsonIgnore]
        public string InterfaceName
        {
            get { return "I" + Panel + "View"; }
        }

        /// <summary>Binder 名：<c>{Panel}ViewBinder</c>。</summary>
        [JsonIgnore]
        public string BinderName
        {
            get { return Panel + "ViewBinder"; }
        }
    }

    /// <summary>描述文件本身写错（缺字段、重名、未知 kind 等），必须带定位信息抛出。</summary>
    public sealed class ViewGenException : Exception
    {
        public ViewGenException(string message)
            : base(message)
        {
        }
    }

    /// <summary>绑定描述 JSON 的读写入口。</summary>
    public static class BindingSpecReader
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>解析并校验绑定描述；任何不合规都抛 <see cref="ViewGenException"/>。</summary>
        public static PanelSpec Parse(string json)
        {
            PanelSpec? spec;
            try
            {
                spec = JsonSerializer.Deserialize<PanelSpec>(json, Options);
            }
            catch (JsonException ex)
            {
                throw new ViewGenException("绑定描述不是合法 JSON：" + ex.Message);
            }

            if (spec == null)
            {
                throw new ViewGenException("绑定描述为空。");
            }

            Validate(spec);
            return spec;
        }

        private static void Validate(PanelSpec spec)
        {
            if (string.IsNullOrWhiteSpace(spec.Panel))
            {
                throw new ViewGenException("缺少 panel 字段（面板名，如 SignIn）。");
            }

            if (!Identifiers.IsValidTypeName(spec.Panel))
            {
                throw new ViewGenException("panel 必须是合法类型名（字母开头、只含字母数字下划线）：" + spec.Panel);
            }

            if (string.IsNullOrWhiteSpace(spec.ViewNamespace))
            {
                throw new ViewGenException("缺少 viewNamespace 字段（如 AIFramework.Features.DailySignIn）。");
            }

            if (!Identifiers.IsValidNamespace(spec.ViewNamespace))
            {
                throw new ViewGenException("viewNamespace 不是合法命名空间：" + spec.ViewNamespace);
            }

            if (spec.Bindings == null || spec.Bindings.Count == 0)
            {
                throw new ViewGenException("bindings 不能为空。");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < spec.Bindings.Count; i++)
            {
                BindingSpec binding = spec.Bindings[i];
                string where = "bindings[" + i + "]";

                if (binding == null)
                {
                    throw new ViewGenException(where + " 为 null。");
                }

                if (string.IsNullOrWhiteSpace(binding.Name))
                {
                    throw new ViewGenException(where + " 缺少 name。");
                }

                if (!Identifiers.IsValidTypeName(binding.Name))
                {
                    throw new ViewGenException(where + ".name 必须是合法标识符：" + binding.Name);
                }

                if (!seen.Add(binding.Name))
                {
                    throw new ViewGenException(where + ".name 重复：" + binding.Name);
                }

                if (string.IsNullOrWhiteSpace(binding.Kind))
                {
                    throw new ViewGenException(where + " 缺少 kind（可选：Event / EventWithInt / List / Text / Visible）。");
                }

                if (!char.IsLetter(binding.Kind[0])
                    || !Enum.TryParse(binding.Kind, ignoreCase: true, out BindingKind resolved))
                {
                    throw new ViewGenException(
                        where + ".kind 未知：" + binding.Kind + "（可选：Event / EventWithInt / List / Text / Visible）");
                }

                binding.ResolvedKind = resolved;

                if (binding.ResolvedKind == BindingKind.List)
                {
                    if (string.IsNullOrWhiteSpace(binding.ItemType))
                    {
                        throw new ViewGenException(where + " 是 list，必须给 itemType。");
                    }

                    if (!Identifiers.IsValidTypeName(binding.ItemType))
                    {
                        throw new ViewGenException(where + ".itemType 必须是合法类型名：" + binding.ItemType);
                    }
                }

                if (binding.ResolvedKind == BindingKind.EventWithInt
                    && !string.IsNullOrWhiteSpace(binding.Parameter)
                    && !Identifiers.IsValidLocalName(binding.Parameter))
                {
                    throw new ViewGenException(where + ".parameter 必须是合法参数名：" + binding.Parameter);
                }
            }
        }
    }

    internal static class Identifiers
    {
        internal static bool IsValidTypeName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (!char.IsLetter(value[0]) && value[0] != '_')
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

        internal static bool IsValidNamespace(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string[] parts = value.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (!IsValidTypeName(parts[i]))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsValidLocalName(string value)
        {
            return IsValidTypeName(value) && !char.IsUpper(value[0]);
        }

        /// <summary>把 PascalCase 语义名转成 camelCase 参数/字段名（Slots -> slots）。</summary>
        internal static string ToCamel(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            if (char.IsLower(value[0]))
            {
                return value;
            }

            return char.ToLowerInvariant(value[0]) + value.Substring(1);
        }
    }
}
