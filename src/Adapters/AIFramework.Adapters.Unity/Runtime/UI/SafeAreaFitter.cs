#nullable enable

using UnityEngine;

namespace AIFramework.Adapters.Unity.Ui
{
    /// <summary>
    /// 把 <see cref="Screen.safeArea"/> 贴到自身 <see cref="RectTransform"/> 的四个锚点上，
    /// 让挂在它下面的 UI 自动避开刘海、挖孔与底部手势条。
    /// <para>
    /// <b>为什么用锚点而不是改尺寸/位置</b>：锚点是相对父矩形的比例，父 Canvas 被
    /// <c>CanvasScaler</c> 缩放、分辨率横竖屏切换时都不用重算像素；子物体原有的
    /// <c>0.05..0.95</c> 这类相对布局也原样保留。
    /// </para>
    /// <para>
    /// <b>触发时机</b>：<c>OnEnable</c>（首次）、<c>OnRectTransformDimensionsChange</c>（父级尺寸变）、
    /// <c>Update</c>（只在 <see cref="Screen.safeArea"/> 真变了才重算，即横竖屏/分屏/折叠屏切换）。
    /// 稳态下每帧只做 4 个 float 比较，不产生任何分配。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        /// <summary>是否应用顶部安全区；关掉时上边固定贴 1（全屏顶），用于"顶部装饰条故意画到刘海下"的场景。</summary>
        [SerializeField] private bool applyTop = true;

        /// <summary>是否应用底部安全区；关掉时下边固定贴 0。</summary>
        [SerializeField] private bool applyBottom = true;

        private Rect _lastSafeArea;
        private bool _hasLastSafeArea;

        /// <summary>最近一次写进 RectTransform 的锚点（调试/测试用；未 Apply 过是全屏）。</summary>
        public SafeAreaAnchors CurrentAnchors { get; private set; } = SafeAreaAnchors.FullScreen;

        /// <summary>立即重算一次并写到自身 RectTransform；自身没有 RectTransform 时只更新 <see cref="CurrentAnchors"/>。</summary>
        public void Apply()
        {
            Rect safeArea = Screen.safeArea;
            _lastSafeArea = safeArea;
            _hasLastSafeArea = true;

            SafeAreaAnchors anchors = SafeAreaMath.Compute(
                Screen.width,
                Screen.height,
                safeArea.x,
                safeArea.y,
                safeArea.width,
                safeArea.height);

            float minY = applyBottom ? anchors.MinY : 0f;
            float maxY = applyTop ? anchors.MaxY : 1f;

            CurrentAnchors = new SafeAreaAnchors(anchors.MinX, minY, anchors.MaxX, maxY);

            RectTransform? rect = GetComponent<RectTransform>();
            if (rect == null)
            {
                return;
            }

            var anchorMin = new Vector2(anchors.MinX, minY);
            var anchorMax = new Vector2(anchors.MaxX, maxY);

            // 值没变就不写：写锚点会把布局标脏，而 OnRectTransformDimensionsChange → Apply 的回路
            // 会让"每帧都写一遍"变成持续重排。
            if (!SameVector(rect.anchorMin, anchorMin))
            {
                rect.anchorMin = anchorMin;
            }

            if (!SameVector(rect.anchorMax, anchorMax))
            {
                rect.anchorMax = anchorMax;
            }

            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void OnEnable()
        {
            Apply();
        }

        private void OnRectTransformDimensionsChange()
        {
            // 父 Canvas 尺寸变了（分辨率/横竖屏/分屏），安全区可能跟着变。
            Apply();
        }

        private void Update()
        {
            if (!_hasLastSafeArea || !SameRect(Screen.safeArea, _lastSafeArea))
            {
                Apply();
            }
        }

        private static bool SameVector(Vector2 a, Vector2 b)
        {
            return a.x == b.x && a.y == b.y;
        }

        private static bool SameRect(Rect a, Rect b)
        {
            return a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
        }
    }
}
