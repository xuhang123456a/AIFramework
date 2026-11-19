#nullable enable

namespace AIFramework.Adapters.Unity.Ui
{
    /// <summary>安全区锚点：相对父矩形的 0..1 归一化坐标。</summary>
    public readonly struct SafeAreaAnchors
    {
        /// <summary>全屏锚点（左下 0,0 → 右上 1,1）；任何非法输入都退回到它。</summary>
        public static readonly SafeAreaAnchors FullScreen = new SafeAreaAnchors(0f, 0f, 1f, 1f);

        /// <summary>构造一组归一化锚点（本类型不做校验，校验在 <see cref="SafeAreaMath.Compute"/> 里）。</summary>
        /// <param name="minX">左锚点（0..1）。</param>
        /// <param name="minY">下锚点（0..1）。</param>
        /// <param name="maxX">右锚点（0..1）。</param>
        /// <param name="maxY">上锚点（0..1）。</param>
        public SafeAreaAnchors(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        /// <summary>左锚点（0..1）。</summary>
        public float MinX { get; }

        /// <summary>下锚点（0..1）。</summary>
        public float MinY { get; }

        /// <summary>右锚点（0..1）。</summary>
        public float MaxX { get; }

        /// <summary>上锚点（0..1）。</summary>
        public float MaxY { get; }

        /// <summary>调试输出：一眼看出四条边（真机上出问题时不至于只看到一串 float）。</summary>
        public override string ToString()
        {
            return "SafeAreaAnchors(min " + MinX + ", " + MinY + " / max " + MaxX + ", " + MaxY + ")";
        }
    }

    /// <summary>
    /// 安全区换算（纯逻辑，零引擎依赖）：把"屏幕尺寸 + 屏幕安全区（像素，左下原点）"换算成
    /// RectTransform 的归一化锚点。
    /// <para>
    /// <b>为什么单独抽出来</b>：刘海/挖孔/手势条这些只在真机上出现，编辑器里常年是全屏，
    /// 留在 <c>MonoBehaviour</c> 里就只能靠真机肉眼验；抽成静态函数后可以在 <c>dotnet test</c>
    /// 里把所有边界（0 尺寸、越界、NaN）跑一遍，Unity 侧只剩"喂参数 + 写 RectTransform"。
    /// </para>
    /// </summary>
    public static class SafeAreaMath
    {
        /// <summary>浮点容差：安全区与屏幕边缘理论相等时（Unity 侧是像素整数，一般严格相等），
        /// 允许 1 毫像素以内的误差，避免理论上的相等被 float 除法判成越界。</summary>
        private const float Tolerance = 0.001f;

        /// <summary>把"屏幕安全区 + 屏幕尺寸"换算成父矩形锚点；非法输入（0 尺寸/安全区超出屏幕）退回全屏。</summary>
        /// <param name="screenWidth">屏幕宽度（像素）。</param>
        /// <param name="screenHeight">屏幕高度（像素）。</param>
        /// <param name="safeX">安全区左下角 x（像素，屏幕坐标系）。</param>
        /// <param name="safeY">安全区左下角 y（像素，屏幕坐标系）。</param>
        /// <param name="safeWidth">安全区宽度（像素）。</param>
        /// <param name="safeHeight">安全区高度（像素）。</param>
        /// <returns>归一化锚点；输入不合法时返回 <see cref="SafeAreaAnchors.FullScreen"/>。</returns>
        public static SafeAreaAnchors Compute(
            float screenWidth,
            float screenHeight,
            float safeX,
            float safeY,
            float safeWidth,
            float safeHeight)
        {
            if (!IsFinite(screenWidth) || !IsFinite(screenHeight)
                || !IsFinite(safeX) || !IsFinite(safeY)
                || !IsFinite(safeWidth) || !IsFinite(safeHeight))
            {
                return SafeAreaAnchors.FullScreen;
            }

            if (screenWidth <= 0f || screenHeight <= 0f)
            {
                return SafeAreaAnchors.FullScreen;
            }

            if (safeWidth <= 0f || safeHeight <= 0f)
            {
                return SafeAreaAnchors.FullScreen;
            }

            // 安全区必须完全落在屏幕内：负的原点或越过右/上边缘都视为"数据不可信"，直接全屏。
            if (safeX < 0f || safeY < 0f)
            {
                return SafeAreaAnchors.FullScreen;
            }

            if (safeX + safeWidth > screenWidth + Tolerance || safeY + safeHeight > screenHeight + Tolerance)
            {
                return SafeAreaAnchors.FullScreen;
            }

            float minX = Clamp01(safeX / screenWidth);
            float minY = Clamp01(safeY / screenHeight);
            float maxX = Clamp01((safeX + safeWidth) / screenWidth);
            float maxY = Clamp01((safeY + safeHeight) / screenHeight);

            // 归一化后退化成一个点/一条线同样没有意义，退回全屏比给个 0 尺寸矩形好排查。
            if (minX >= maxX || minY >= maxY)
            {
                return SafeAreaAnchors.FullScreen;
            }

            return new SafeAreaAnchors(minX, minY, maxX, maxY);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            return value > 1f ? 1f : value;
        }
    }
}
