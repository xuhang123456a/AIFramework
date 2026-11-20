// UnityEditor / IMGUI 形状桩（T5-9 网格关卡编辑器）：只补 EditorWindow / EditorPrefs / GUILayout /
// EditorGUILayout 的**符号与签名**，不含任何窗口管理、控件绘制或 EditorPrefs 落盘。
//
// 因此离线形状校验只能证明"编辑器脚本编译得过、调用的 API 形状存在"；
// **窗口观感、点击循环、EditorPrefs 跨会话、导出后 AssetDatabase 刷新都必须真机验证。**
using System;
using UnityEngine;

namespace UnityEngine
{
    /// <summary>
    /// GUILayout 选项（形状桩）。真实类型是 <c>sealed</c> 且构造器为 <c>internal</c>，
    /// 实例只能由 <see cref="GUILayout"/> 的静态工厂产出——这里保持同一形状。
    /// </summary>
    public sealed class GUILayoutOption
    {
        /// <summary>同程序集内可见（真实 Unity 也是 internal）。</summary>
        internal GUILayoutOption()
        {
        }
    }

    /// <summary>
    /// 即时模式 GUI 的全局状态（形状桩）：真机上这三个属性直接决定下一次绘制，
    /// 桩里只是可读写字段，对任何渲染都无影响。
    /// </summary>
    public static class GUI
    {
        /// <summary>控件前景色。</summary>
        public static Color color { get; set; }

        /// <summary>控件背景色（关卡编辑器靠它给格子上色）。</summary>
        public static Color backgroundColor { get; set; }

        /// <summary>是否响应交互。</summary>
        public static bool enabled { get; set; }
    }

    /// <summary>
    /// 自动布局容器（形状桩）：真机上 <c>Begin/End</c> 成对改变当前布局组，
    /// 桩里全是空实现，因此"控件排布是否错位"离线无法判定。
    /// </summary>
    public static class GUILayout
    {
        /// <summary>开始一个水平布局组。</summary>
        public static void BeginHorizontal(params GUILayoutOption[] options)
        {
        }

        /// <summary>结束水平布局组。</summary>
        public static void EndHorizontal()
        {
        }

        /// <summary>开始一个垂直布局组。</summary>
        public static void BeginVertical(params GUILayoutOption[] options)
        {
        }

        /// <summary>结束垂直布局组。</summary>
        public static void EndVertical()
        {
        }

        /// <summary>固定空白。</summary>
        public static void Space(float pixels)
        {
        }

        /// <summary>可伸缩空白。</summary>
        public static void FlexibleSpace()
        {
        }

        /// <summary>按钮（桩：永远返回 false，即"没被点"）。</summary>
        public static bool Button(string text, params GUILayoutOption[] options)
        {
            return false;
        }

        /// <summary>纯文本标签。</summary>
        public static void Label(string text, params GUILayoutOption[] options)
        {
        }

        /// <summary>单行文本输入框（桩：原样返回输入）。</summary>
        public static string TextField(string text, params GUILayoutOption[] options)
        {
            return text;
        }

        /// <summary>固定宽度选项。</summary>
        public static GUILayoutOption Width(float width)
        {
            return new GUILayoutOption();
        }

        /// <summary>固定高度选项。</summary>
        public static GUILayoutOption Height(float height)
        {
            return new GUILayoutOption();
        }

        /// <summary>水平方向是否填充剩余空间。</summary>
        public static GUILayoutOption ExpandWidth(bool expand)
        {
            return new GUILayoutOption();
        }

        /// <summary>垂直方向是否填充剩余空间。</summary>
        public static GUILayoutOption ExpandHeight(bool expand)
        {
            return new GUILayoutOption();
        }
    }
}

namespace UnityEditor
{
    /// <summary>
    /// 编辑器窗口（形状桩）：真实基类是 <c>UnityEngine.ScriptableObject</c>。
    /// <para>
    /// <b>桩不会创建真实窗口</b>：<c>GetWindow</c> 只是反射构造一个对象，
    /// <c>Show/Repaint/Close</c> 全是空实现，Unity 的 <c>OnEnable/OnGUI/OnDisable</c> 消息机制
    /// 也不会被触发——这些回调的时序只能真机验证。
    /// </para>
    /// </summary>
    public class EditorWindow : ScriptableObject
    {
        /// <summary>窗口最小尺寸（真机生效）。</summary>
        public Vector2 minSize { get; set; }

        /// <summary>窗口最大尺寸（真机生效）。</summary>
        public Vector2 maxSize { get; set; }

        /// <summary>窗口屏幕矩形（真机生效）。</summary>
        public Rect position { get; set; }

        /// <summary>显示窗口（桩：空实现）。</summary>
        public void Show()
        {
        }

        /// <summary>以浮动工具窗口显示（桩：空实现）。</summary>
        public void ShowUtility()
        {
        }

        /// <summary>关闭窗口（桩：空实现）。</summary>
        public void Close()
        {
        }

        /// <summary>请求重绘（桩：空实现）。</summary>
        public void Repaint()
        {
        }

        /// <summary>聚焦窗口（桩：空实现）。</summary>
        public void Focus()
        {
        }

        /// <summary>取（不存在则创建）指定类型的窗口（桩：反射 new 一个）。</summary>
        public static T GetWindow<T>() where T : EditorWindow
        {
            return Activator.CreateInstance<T>();
        }

        /// <summary>取（不存在则创建）指定类型、带标题的窗口（桩：反射 new 一个，标题丢弃）。</summary>
        public static T GetWindow<T>(string title) where T : EditorWindow
        {
            return Activator.CreateInstance<T>();
        }
    }

    /// <summary>
    /// 编辑器偏好存储（形状桩）：<b>不落盘</b>，因此"同一次会话重开窗口状态不丢"
    /// 只能真机验证；桩只保证调用形状正确。
    /// </summary>
    public static class EditorPrefs
    {
        /// <summary>键是否存在（桩：永远 false）。</summary>
        public static bool HasKey(string key)
        {
            return false;
        }

        /// <summary>删除键（桩：空实现）。</summary>
        public static void DeleteKey(string key)
        {
        }

        /// <summary>取字符串（桩：空串）。</summary>
        public static string GetString(string key)
        {
            return string.Empty;
        }

        /// <summary>取字符串（桩：返回默认值）。</summary>
        public static string GetString(string key, string defaultValue)
        {
            return defaultValue;
        }

        /// <summary>写字符串（桩：空实现）。</summary>
        public static void SetString(string key, string value)
        {
        }

        /// <summary>取整数（桩：0）。</summary>
        public static int GetInt(string key)
        {
            return 0;
        }

        /// <summary>取整数（桩：返回默认值）。</summary>
        public static int GetInt(string key, int defaultValue)
        {
            return defaultValue;
        }

        /// <summary>写整数（桩：空实现）。</summary>
        public static void SetInt(string key, int value)
        {
        }

        /// <summary>取布尔（桩：false）。</summary>
        public static bool GetBool(string key)
        {
            return false;
        }

        /// <summary>取布尔（桩：返回默认值）。</summary>
        public static bool GetBool(string key, bool defaultValue)
        {
            return defaultValue;
        }

        /// <summary>写布尔（桩：空实现）。</summary>
        public static void SetBool(string key, bool value)
        {
        }
    }

    /// <summary>提示框级别（与真实 Unity 同名同值）。</summary>
    public enum MessageType
    {
        /// <summary>无。</summary>
        None = 0,

        /// <summary>信息。</summary>
        Info = 1,

        /// <summary>警告。</summary>
        Warning = 2,

        /// <summary>错误。</summary>
        Error = 3
    }

    /// <summary>编辑器自动布局控件（形状桩：不画任何东西）。</summary>
    public static class EditorGUILayout
    {
        /// <summary>标签。</summary>
        public static void LabelField(string label, params GUILayoutOption[] options)
        {
        }

        /// <summary>左右两栏标签。</summary>
        public static void LabelField(string label, string label2, params GUILayoutOption[] options)
        {
        }

        /// <summary>文本输入框（桩：原样返回输入）。</summary>
        public static string TextField(string text, params GUILayoutOption[] options)
        {
            return text;
        }

        /// <summary>带标签的文本输入框（桩：原样返回输入）。</summary>
        public static string TextField(string label, string text, params GUILayoutOption[] options)
        {
            return text;
        }

        /// <summary>提示框（桩：空实现）。</summary>
        public static void HelpBox(string message, MessageType type)
        {
        }

        /// <summary>标准间隔。</summary>
        public static void Space()
        {
        }

        /// <summary>指定像素间隔。</summary>
        public static void Space(float width)
        {
        }
    }
}
