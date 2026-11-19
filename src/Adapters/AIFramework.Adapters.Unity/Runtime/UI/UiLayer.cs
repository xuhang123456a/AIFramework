#nullable enable

using UnityEngine;

namespace AIFramework.Adapters.Unity.Ui
{
    /// <summary>
    /// UI 层级常量：<c>Canvas.sortingOrder</c> 的取值表，留出 100 的步长方便以后插层。
    /// <para>
    /// <b>为什么要固定成"魔法数字表"而不是每个场景随手写 sortingOrder</b>：同一个层级的所有界面
    /// （HUD / 面板 / 弹窗）必须相对彼此谁在上面是稳定的，否则"弹窗被 HUD 盖住"这种问题只能在真机上碰运气。
    /// </para>
    /// </summary>
    public enum UiLayerKind : int
    {
        /// <summary>背景层：纯装饰、不拦点击。</summary>
        Background = 0,

        /// <summary>场景层：3D/2D 场景内的世界空间 UI。</summary>
        Scene = 100,

        /// <summary>HUD 层：常驻状态条、货币、按钮。</summary>
        Hud = 200,

        /// <summary>面板层：背包/商店这类可开关的大面板。</summary>
        Panel = 300,

        /// <summary>弹窗层：模态弹窗，必须盖住面板与 HUD。</summary>
        Popup = 400,

        /// <summary>Toast 层：轻提示，盖在弹窗之上。</summary>
        Toast = 500,

        /// <summary>调试层：性能/日志面板，永远最上。</summary>
        Debug = 900
    }

    /// <summary>
    /// 给 Canvas 按 <see cref="UiLayerKind"/> 定排序值：<c>Awake</c> 里写一次自身的
    /// <c>Canvas.sortingOrder</c>，并让嵌套子 Canvas 逐个 +1，避免同层内部互相覆盖。
    /// <para>
    /// <b>为什么子 Canvas 要 +1</b>：Unity 里嵌套 Canvas 的 <c>sortingOrder</c> 默认是 0，
    /// 在没有 <c>sortingLayer</c> 区分时会与父 Canvas 抢顺序；把子 Canvas 排在父级之后，
    /// "面板里再开弹窗"这种结构就不需要每个子 Canvas 单独配层。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CanvasSortOrder : MonoBehaviour
    {
        /// <summary>所属层级；默认 HUD（大厅这类常驻界面就是 HUD）。</summary>
        [SerializeField] private UiLayerKind layer = UiLayerKind.Hud;

        /// <summary>最近一次写进 Canvas 的排序值（调试/测试用；自身没有 Canvas 时仍是本层值）。</summary>
        public int AppliedSortOrder { get; private set; } = (int)UiLayerKind.Hud;

        /// <summary>按当前层级重算并写入 Canvas（子 Canvas 递归 +1）。</summary>
        public void Apply()
        {
            int baseOrder = (int)layer;
            AppliedSortOrder = baseOrder;

            Canvas? canvas = GetComponent<Canvas>();
            if (canvas == null)
            {
                // 挂错物体（不是 Canvas）时没有 sortingOrder 可写：值本身仍记下来，
                // 让 PlayMode 断言"AppliedSortOrder 对了但物体上没有 Canvas"这类装配错误能被发现。
                return;
            }

            canvas.sortingOrder = baseOrder;
            ApplyToChildCanvases(canvas.transform, baseOrder);
        }

        private void Awake()
        {
            Apply();
        }

        /// <summary>
        /// 深度优先遍历子节点：每遇到一个子 Canvas 就排在父级之后（+1），
        /// 中间夹着的普通 UI 节点不改变层级基数，保证"同一父 Canvas 下的子 Canvas"互不重叠。
        /// </summary>
        private static void ApplyToChildCanvases(Transform parent, int parentOrder)
        {
            int childCount = parent.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = parent.GetChild(i);
                int childOrder = parentOrder;

                Canvas? childCanvas = child.GetComponent<Canvas>();
                if (childCanvas != null)
                {
                    childOrder = parentOrder + 1;
                    childCanvas.sortingOrder = childOrder;
                }

                ApplyToChildCanvases(child, childOrder);
            }
        }
    }
}
