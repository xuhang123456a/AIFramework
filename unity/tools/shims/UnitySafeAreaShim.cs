// 安全区形状桩（T3-5）：只补 UnityEngine.Screen / UnityEngine.Rect 的符号，与真实 API 同形。
// 不含任何屏幕尺寸探测、刘海计算或分辨率缩放——这些在真机上由引擎提供。
namespace UnityEngine
{
    /// <summary>
    /// 矩形（形状桩）：真机里是"左下角原点 + 像素尺寸"的值类型。
    /// 桩只保留 <c>x/y/width/height</c> 与四参构造（适配层只用到这些）。
    /// </summary>
    public struct Rect
    {
        /// <summary>左下角 x（像素）。</summary>
        public float x;

        /// <summary>左下角 y（像素）。</summary>
        public float y;

        /// <summary>宽度（像素）。</summary>
        public float width;

        /// <summary>高度（像素）。</summary>
        public float height;

        /// <summary>按左下角 + 尺寸构造。</summary>
        public Rect(float x, float y, float width, float height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }
    }

    /// <summary>
    /// 屏幕信息（形状桩）：桩里给一台"全屏无刘海"的假设备，因此离线形状校验只能证明
    /// 代码编译得过，<b>不能</b>证明真机上的安全区换算正确——那需要 <c>SafeAreaMathTests</c>
    /// 的纯逻辑用例 + PlayMode 真机用例。
    /// </summary>
    public static class Screen
    {
        /// <summary>屏幕宽度（像素）。</summary>
        public static int width
        {
            get { return 1080; }
        }

        /// <summary>屏幕高度（像素）。</summary>
        public static int height
        {
            get { return 1920; }
        }

        /// <summary>安全区（像素，左下原点）；桩里等于整个屏幕。</summary>
        public static Rect safeArea
        {
            get { return new Rect(0f, 0f, 1080f, 1920f); }
        }
    }
}
