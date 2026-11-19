// UnityEngine.UI 形状桩：只覆盖适配层实际用到的 API（Text / Button / UnityEvent）。
// 这不是 UI 实现，只用于在没有 Unity 的机器上编译校验适配层。
using System;

namespace UnityEngine
{
    /// <summary>UnityEngine.Events.UnityEvent 的精简桩（本仓只用无参 onClick）。</summary>
    public class UnityEvent
    {
        public void AddListener(Action call)
        {
        }

        public void RemoveListener(Action call)
        {
        }

        public void RemoveAllListeners()
        {
        }
    }
}

namespace UnityEngine.UI
{
    /// <summary>所有可绘制控件的基类。</summary>
    public class Graphic : MonoBehaviour
    {
        /// <summary>颜色。</summary>
        public Color color;

        /// <summary>是否参与射线检测。</summary>
        public bool raycastTarget = true;
    }

    /// <summary>文本控件。</summary>
    public class Text : Graphic
    {
        /// <summary>显示内容。</summary>
        public string text = string.Empty;

        /// <summary>字号。</summary>
        public int fontSize = 14;

        /// <summary>字体。</summary>
        public Font font = null!;

        /// <summary>对齐方式。</summary>
        public TextAnchor alignment;
    }

    /// <summary>图片控件。</summary>
    public class Image : Graphic
    {
        /// <summary>精灵（未使用，保留形状）。</summary>
        public UnityEngine.Object? sprite;
    }

    /// <summary>可交互控件基类。</summary>
    public class Selectable : MonoBehaviour
    {
        /// <summary>是否可点。</summary>
        public bool interactable = true;
    }

    /// <summary>按钮控件。</summary>
    public class Button : Selectable
    {
        /// <summary>点击回调集合。</summary>
        public UnityEvent onClick = new UnityEvent();
    }
}

