// uGUI 布局与运行时系统的形状桩（Canvas / RectTransform / LayoutGroup / EventSystem）。
// 只覆盖编辑器建场景脚本用到的 API；不含任何渲染或事件实现。
using System;

namespace UnityEngine
{
    /// <summary>线段/矩形偏移（VerticalLayoutGroup.padding 用）。</summary>
    public struct RectOffset
    {
        public int left;
        public int right;
        public int top;
        public int bottom;

        public RectOffset(int left, int right, int top, int bottom)
        {
            this.left = left;
            this.right = right;
            this.top = top;
            this.bottom = bottom;
        }
    }

    /// <summary>文本对齐。</summary>
    public enum TextAnchor
    {
        UpperLeft = 0,
        UpperCenter = 1,
        UpperRight = 2,
        MiddleLeft = 3,
        MiddleCenter = 4,
        MiddleRight = 5,
        LowerLeft = 6,
        LowerCenter = 7,
        LowerRight = 8
    }

    /// <summary>画布渲染模式。</summary>
    public enum RenderMode
    {
        ScreenSpaceOverlay = 0,
        ScreenSpaceCamera = 1,
        WorldSpace = 2
    }

    /// <summary>UI 矩形变换（Transform 的 UI 版本）。</summary>
    public class RectTransform : Transform
    {
        /// <summary>锚点最小。</summary>
        public Vector2 anchorMin;

        /// <summary>锚点最大。</summary>
        public Vector2 anchorMax;

        /// <summary>轴心。</summary>
        public Vector2 pivot;

        /// <summary>锚点位置。</summary>
        public Vector2 anchoredPosition;

        /// <summary>尺寸。</summary>
        public Vector2 sizeDelta;

        /// <summary>相对锚点最小偏移。</summary>
        public Vector2 offsetMin;

        /// <summary>相对锚点最大偏移。</summary>
        public Vector2 offsetMax;
    }

    /// <summary>画布。</summary>
    public class Canvas : MonoBehaviour
    {
        /// <summary>渲染模式。</summary>
        public RenderMode renderMode;

        /// <summary>排序层。</summary>
        public int sortingOrder;
    }

    /// <summary>画布缩放。</summary>
    public class CanvasScaler : MonoBehaviour
    {
        /// <summary>参考分辨率。</summary>
        public Vector2 referenceResolution;

        /// <summary>屏幕匹配模式。</summary>
        public int screenMatchMode = 0;

        /// <summary>匹配权重。</summary>
        public float matchWidthOrHeight;
    }

    /// <summary>射线检测器。</summary>
    public class GraphicRaycaster : MonoBehaviour
    {
    }

    /// <summary>垂直布局组。</summary>
    public class VerticalLayoutGroup : MonoBehaviour
    {
        /// <summary>内边距。</summary>
        public RectOffset padding = new RectOffset(0, 0, 0, 0);

        /// <summary>间距。</summary>
        public float spacing;

        /// <summary>子项对齐。</summary>
        public TextAnchor childAlignment;

        /// <summary>是否控制子项宽度。</summary>
        public bool childControlWidth = true;

        /// <summary>是否控制子项高度。</summary>
        public bool childControlHeight = true;

        /// <summary>是否强制撑开子项宽度。</summary>
        public bool childForceExpandWidth = true;

        /// <summary>是否强制撑开子项高度。</summary>
        public bool childForceExpandHeight = false;
    }

    /// <summary>水平布局组。</summary>
    public class HorizontalLayoutGroup : MonoBehaviour
    {
        /// <summary>间距。</summary>
        public float spacing;

        /// <summary>子项对齐。</summary>
        public TextAnchor childAlignment;
    }

    /// <summary>内容尺寸适配。</summary>
    public class ContentSizeFitter : MonoBehaviour
    {
        /// <summary>横向适配模式（0 = 不限制，1 = 最小尺寸，2 = 首选尺寸）。</summary>
        public int horizontalFit;

        /// <summary>纵向适配模式。</summary>
        public int verticalFit;
    }

    /// <summary>布局尺寸。</summary>
    public class LayoutElement : MonoBehaviour
    {
        /// <summary>首选高度。</summary>
        public float preferredHeight;

        /// <summary>首选宽度。</summary>
        public float preferredWidth;

        /// <summary>最小高度。</summary>
        public float minHeight;

        /// <summary>是否忽略布局。</summary>
        public bool ignoreLayout;
    }
}

namespace UnityEngine.EventSystems
{
    /// <summary>事件系统。</summary>
    public class EventSystem : MonoBehaviour
    {
    }

    /// <summary>标准输入模块（按钮点击依赖它）。</summary>
    public class StandaloneInputModule : MonoBehaviour
    {
    }
}

namespace UnityEngine.UI
{
    /// <summary>
    /// 网格布局组（形状桩）：三消棋盘靠它把格子排成"宽×高"的表。
    /// <para>
    /// <b>为什么必须补这个桩</b>：棋盘容器要在场景里挂 <c>GridLayoutGroup</c> 设 cellSize/constraint，
    /// 没有桩就编不过建场景脚本；桩只保证符号与真实 API 同形，不含任何排版算法。
    /// </para>
    /// </summary>
    public class GridLayoutGroup : MonoBehaviour
    {
        /// <summary>约束模式（与 Unity 同名同序：FixedColumnCount = 固定列数）。</summary>
        public enum Constraint
        {
            Flexible = 0,
            FixedColumnCount = 1,
            FixedRowCount = 2
        }

        /// <summary>起始角（第一个格子的位置）。</summary>
        public enum Corner
        {
            UpperLeft = 0,
            UpperRight = 1,
            LowerLeft = 2,
            LowerRight = 3
        }

        /// <summary>主轴方向。</summary>
        public enum Axis
        {
            Horizontal = 0,
            Vertical = 1
        }

        /// <summary>单元格尺寸。</summary>
        public Vector2 cellSize = new Vector2(100f, 100f);

        /// <summary>单元格间距。</summary>
        public Vector2 spacing = new Vector2(0f, 0f);

        /// <summary>内边距。</summary>
        public RectOffset padding = new RectOffset(0, 0, 0, 0);

        /// <summary>约束模式。</summary>
        public Constraint constraint;

        /// <summary>约束数量（固定列数/行数时生效）。</summary>
        public int constraintCount = 2;

        /// <summary>起始角。</summary>
        public Corner startCorner;

        /// <summary>主轴方向。</summary>
        public Axis startAxis;

        /// <summary>子项对齐。</summary>
        public TextAnchor childAlignment;
    }
}

