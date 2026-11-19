#nullable enable
using UnityEngine;

namespace AIFramework.Adapters.Unity.UI
{
    /// <summary>
    /// 面板描述：挂在面板根对象上，给 <c>UIBindExporter</c> 提供「这个面板叫什么、视图契约在哪个命名空间」。
    /// <para>
    /// <b>为什么不让导出器从字段里猜命名空间</b>：命名空间属于纯逻辑侧的视图契约归属，
    /// 场景对象名与层级都不可靠地表达它；显式写在一个组件上，导出器才能报出可执行的错误
    /// （缺命名空间时 viewgen 会直接拒绝整份描述）。
    /// </para>
    /// <para>
    /// 面板名优先取本组件的 <see cref="PanelName"/>；为空时退回「根对象名去掉后缀 <c>Panel</c>」
    /// （<c>SignInPanel</c> → <c>SignIn</c>）。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIViewDescriptor : MonoBehaviour
    {
        /// <summary>面板名（如 SignIn），决定 <c>ISignInView</c> / <c>SignInViewBinder</c> 的文件名。</summary>
        [SerializeField] private string panelName = string.Empty;

        /// <summary>视图契约命名空间（如 AIFramework.Features.DailySignIn）。</summary>
        [SerializeField] private string viewNamespace = string.Empty;

        /// <summary>接口 XML 注释（可选）。</summary>
        [SerializeField] private string summary = string.Empty;

        /// <summary>面板名；为空时由导出器从根对象名推断。</summary>
        public string PanelName
        {
            get { return panelName; }
        }

        /// <summary>视图契约命名空间。</summary>
        public string ViewNamespace
        {
            get { return viewNamespace; }
        }

        /// <summary>接口 XML 注释（可为空）。</summary>
        public string Summary
        {
            get { return summary; }
        }

        /// <summary>编辑器/场景构建脚本用：一次性写入三个序列化字段。</summary>
        /// <param name="panel">面板名（如 SignIn）。</param>
        /// <param name="namespaceName">视图契约命名空间。</param>
        /// <param name="panelSummary">接口 XML 注释，可为空。</param>
        public void Configure(string panel, string namespaceName, string panelSummary)
        {
            panelName = panel;
            viewNamespace = namespaceName;
            summary = panelSummary;
        }
    }
}
