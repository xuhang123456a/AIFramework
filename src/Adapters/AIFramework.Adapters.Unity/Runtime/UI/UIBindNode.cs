#nullable enable
using UnityEngine;

namespace AIFramework.Adapters.Unity.UI
{
    /// <summary>
    /// 控件语义类型：决定 <c>tools/viewgen</c> 生成哪种视图成员。
    /// <para>
    /// 数值与 <c>tools/viewgen/Models.cs</c> 的 <c>BindingKind</c> 保持同义（名字是唯一契约，数值仅便于阅读）：
    /// <c>Event=0 / EventWithInt=1 / List=2 / Text=3 / Number=5 / Visible=6</c>。
    /// </para>
    /// </summary>
    public enum UIBindKind
    {
        /// <summary>无参点击事件（按钮）。</summary>
        Event = 0,

        /// <summary>带一个 int 参数的事件（如按格子序号补签）。</summary>
        EventWithInt = 1,

        /// <summary>列表渲染（target 为行容器，通常挂行模板）。</summary>
        List = 2,

        /// <summary>文本渲染。</summary>
        Text = 3,

        /// <summary>整数渲染（金币/数量这类，避免视图层做 int→string 分配）。</summary>
        Number = 5,

        /// <summary>显隐渲染（任何控件都可）。</summary>
        Visible = 6
    }

    /// <summary>
    /// 控件语义标记：挂在按钮 / 文本 / 列表容器上，声明「这个控件在视图契约里叫什么、是哪种绑定」。
    /// <para>
    /// <b>为什么不在本文件里引用 <c>UnityEngine.UI.Button</c></b>：适配层 Runtime 程序集一旦 using UGUI，
    /// 就多了一条对 uGUI 包的编译期依赖（换 UI 方案或做无 UGUI 的宿主时直接编不过）。
    /// 这里只保存 <see cref="UnityEngine.Object"/> 引用，按钮/文本的类型判定放到编辑器导出器里做。
    /// </para>
    /// <para>
    /// 导出器见 <c>unity/Assets/AIFrameworkDemo/Editor/UIBindExporter.cs</c>：
    /// 它从选中对象（或场景每个根）向下收集本组件，生成 <c>*.bindings.json</c> 喂给 <c>tools/viewgen</c>。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIBindNode : MonoBehaviour
    {
        /// <summary>语义名（如 <c>ClaimClicked</c> / <c>Slots</c> / <c>Gold</c>），决定生成的成员名，面板内唯一。</summary>
        [SerializeField] private string semanticName = string.Empty;

        /// <summary>绑定类型。</summary>
        [SerializeField] private UIBindKind kind = UIBindKind.Event;

        /// <summary>目标控件：Button / Text / Transform（列表容器）等；具体类型由导出器校验。</summary>
        [SerializeField] private UnityEngine.Object? target;

        /// <summary>语义名，例如 ClaimClicked / Items / Gold。</summary>
        public string SemanticName
        {
            get { return semanticName; }
        }

        /// <summary>绑定类型。</summary>
        public UIBindKind Kind
        {
            get { return kind; }
        }

        /// <summary>目标控件引用。</summary>
        public UnityEngine.Object? Target
        {
            get { return target; }
        }

        /// <summary>编辑器/场景构建脚本用：一次性写入三个序列化字段（不依赖字段可见性）。</summary>
        /// <param name="name">语义名，面板内唯一。</param>
        /// <param name="bindKind">绑定类型。</param>
        /// <param name="bindTarget">目标控件引用。</param>
        public void Configure(string name, UIBindKind bindKind, UnityEngine.Object? bindTarget)
        {
            semanticName = name ?? string.Empty;
            kind = bindKind;
            target = bindTarget;
        }
    }
}
