using AIFramework.Adapters.Unity.Ui;
using Xunit;

namespace AIFramework.Adapters.Tests.Ui
{
    /// <summary>
    /// <see cref="SafeAreaMath"/> 的行为契约验收：屏幕安全区（像素，左下原点）→ RectTransform 归一化锚点。
    /// <para>
    /// <b>为什么这层要能在 <c>dotnet test</c> 里跑</b>：刘海、挖孔、底部手势条只在真机上出现，
    /// 编辑器 Game 视图常年是全屏；把换算抽成纯函数后，"越界/0 尺寸/NaN 必须退回全屏"这类
    /// 断言不必等真机就能在毫秒级守住。Unity 侧（<c>SafeAreaFitter</c>）只负责"读 Screen + 写锚点"。
    /// </para>
    /// <para>
    /// 约定：<c>SafeAreaMathTests</c> 直接链接 <c>Runtime/UI/SafeAreaMath.cs</c> 源码编译
    /// （Unity 适配器没有 <c>.csproj</c>，只能以源码形式共享这一份纯逻辑，避免两份实现漂移）。
    /// </para>
    /// </summary>
    public sealed class SafeAreaMathTests
    {
        private const int Precision = 5;

        [Fact]
        public void Compute_FullScreenSafeArea_ReturnsUnitAnchors()
        {
            SafeAreaAnchors anchors = SafeAreaMath.Compute(1080f, 1920f, 0f, 0f, 1080f, 1920f);

            Assert.Equal(0f, anchors.MinX, Precision);
            Assert.Equal(0f, anchors.MinY, Precision);
            Assert.Equal(1f, anchors.MaxX, Precision);
            Assert.Equal(1f, anchors.MaxY, Precision);
        }

        [Fact]
        public void Compute_BottomInset_ShrinksOnlyTopEdge()
        {
            // iPhone 竖屏：屏幕 1080×1920，底部手势条占 120px（Unity 的 y 轴原点在左下）。
            SafeAreaAnchors anchors = SafeAreaMath.Compute(1080f, 1920f, 0f, 0f, 1080f, 1800f);

            Assert.Equal(0f, anchors.MinX, Precision);
            Assert.Equal(0f, anchors.MinY, Precision);
            Assert.Equal(1f, anchors.MaxX, Precision);
            Assert.Equal(0.9375f, anchors.MaxY, Precision);
        }

        [Fact]
        public void Compute_TopInset_ShrinksOnlyBottomEdge()
        {
            // 顶部刘海 120px：下边不动，上边按 (safeY + safeHeight) / screenHeight 算。
            SafeAreaAnchors anchors = SafeAreaMath.Compute(1080f, 1920f, 0f, 120f, 1080f, 1800f);

            Assert.Equal(0.0625f, anchors.MinY, Precision);
            Assert.Equal(1f, anchors.MaxY, Precision);
        }

        [Fact]
        public void Compute_LeftAndRightInsets_MapToNormalizedWidth()
        {
            // 横屏左右挖孔：2340 宽，两侧各 120px。
            SafeAreaAnchors anchors = SafeAreaMath.Compute(2340f, 1080f, 120f, 0f, 2100f, 1080f);

            Assert.Equal(120f / 2340f, anchors.MinX, Precision);
            Assert.Equal(0f, anchors.MinY, Precision);
            Assert.Equal(2220f / 2340f, anchors.MaxX, Precision);
            Assert.Equal(1f, anchors.MaxY, Precision);
        }

        [Fact]
        public void Compute_NotchOnAllFourSides_NormalizesEachEdgeIndependently()
        {
            // 1080×1920，上 80 / 下 120 / 左右各 30：四条边各自换算，互不影响。
            SafeAreaAnchors anchors = SafeAreaMath.Compute(1080f, 1920f, 30f, 120f, 1020f, 1720f);

            Assert.Equal(30f / 1080f, anchors.MinX, Precision);
            Assert.Equal(120f / 1920f, anchors.MinY, Precision);
            Assert.Equal(1050f / 1080f, anchors.MaxX, Precision);
            Assert.Equal(1840f / 1920f, anchors.MaxY, Precision);
        }

        [Fact]
        public void Compute_ZeroScreenWidth_FallsBackToFullScreen()
        {
            AssertFullScreen(SafeAreaMath.Compute(0f, 1920f, 0f, 0f, 1080f, 1920f));
        }

        [Fact]
        public void Compute_ZeroScreenHeight_FallsBackToFullScreen()
        {
            AssertFullScreen(SafeAreaMath.Compute(1080f, 0f, 0f, 0f, 1080f, 1920f));
        }

        [Fact]
        public void Compute_NegativeScreenSize_FallsBackToFullScreen()
        {
            // 切场景/Editor 退出播放时偶尔读到 -1 之类的脏值，必须整屏兜底而不是算出负锚点。
            AssertFullScreen(SafeAreaMath.Compute(-1080f, -1920f, 0f, 0f, 1080f, 1920f));
        }

        [Fact]
        public void Compute_ZeroSafeWidth_FallsBackToFullScreen()
        {
            AssertFullScreen(SafeAreaMath.Compute(1080f, 1920f, 0f, 0f, 0f, 1920f));
        }

        [Fact]
        public void Compute_ZeroSafeHeight_FallsBackToFullScreen()
        {
            AssertFullScreen(SafeAreaMath.Compute(1080f, 1920f, 0f, 0f, 1080f, 0f));
        }

        [Fact]
        public void Compute_NegativeSafeOrigin_FallsBackToFullScreen()
        {
            // 负原点 = 安全区戳到屏幕外，数据不可信。
            AssertFullScreen(SafeAreaMath.Compute(1080f, 1920f, -1f, 0f, 1000f, 1920f));
            AssertFullScreen(SafeAreaMath.Compute(1080f, 1920f, 0f, -1f, 1080f, 1900f));
        }

        [Fact]
        public void Compute_SafeAreaWiderThanScreen_FallsBackToFullScreen()
        {
            AssertFullScreen(SafeAreaMath.Compute(1080f, 1920f, 0f, 0f, 1081f, 1920f));
        }

        [Fact]
        public void Compute_SafeAreaTallerThanScreen_FallsBackToFullScreen()
        {
            AssertFullScreen(SafeAreaMath.Compute(1080f, 1920f, 0f, 120f, 1080f, 1900f));
        }

        [Fact]
        public void Compute_SafeAreaOffsetPastRightEdge_FallsBackToFullScreen()
        {
            // 尺寸本身合法，但平移后右边缘越界。
            AssertFullScreen(SafeAreaMath.Compute(1080f, 1920f, 100f, 0f, 1000f, 1920f));
        }

        [Theory]
        [InlineData(float.NaN, 1920f, 0f, 0f, 1080f, 1920f)]
        [InlineData(1080f, float.NaN, 0f, 0f, 1080f, 1920f)]
        [InlineData(1080f, 1920f, float.NaN, 0f, 1080f, 1920f)]
        [InlineData(1080f, 1920f, 0f, float.NaN, 1080f, 1920f)]
        [InlineData(1080f, 1920f, 0f, 0f, float.PositiveInfinity, 1920f)]
        [InlineData(1080f, 1920f, 0f, 0f, 1080f, float.NegativeInfinity)]
        public void Compute_NonFiniteInput_FallsBackToFullScreen(
            float screenWidth,
            float screenHeight,
            float safeX,
            float safeY,
            float safeWidth,
            float safeHeight)
        {
            // NaN 参与比较恒为 false，若不显式拦截，"越界检查"会被静默绕过并产出 NaN 锚点。
            AssertFullScreen(SafeAreaMath.Compute(screenWidth, screenHeight, safeX, safeY, safeWidth, safeHeight));
        }

        [Theory]
        [InlineData(1080f, 1920f, 30f, 120f, 1020f, 1720f)]
        [InlineData(1f, 1f, 0f, 0f, 1f, 1f)]
        [InlineData(1080f, 1920f, 0f, 0f, 1080f, 1800f)]
        [InlineData(2340f, 1080f, 120f, 0f, 2100f, 1080f)]
        public void Compute_AnyValidInput_AlwaysReturnsAnchorInsideUnitSquare(
            float screenWidth,
            float screenHeight,
            float safeX,
            float safeY,
            float safeWidth,
            float safeHeight)
        {
            SafeAreaAnchors anchors = SafeAreaMath.Compute(screenWidth, screenHeight, safeX, safeY, safeWidth, safeHeight);

            Assert.InRange(anchors.MinX, 0f, 1f);
            Assert.InRange(anchors.MinY, 0f, 1f);
            Assert.InRange(anchors.MaxX, 0f, 1f);
            Assert.InRange(anchors.MaxY, 0f, 1f);
            Assert.True(anchors.MinX <= anchors.MaxX);
            Assert.True(anchors.MinY <= anchors.MaxY);
        }

        [Fact]
        public void Compute_OnePixelSafeArea_StillReturnsPositiveAnchors()
        {
            // 极端小但合法：不能被当成"点"而退回全屏。
            SafeAreaAnchors anchors = SafeAreaMath.Compute(1080f, 1920f, 0f, 0f, 1f, 1f);

            Assert.Equal(0f, anchors.MinX, Precision);
            Assert.True(anchors.MaxX > 0f);
            Assert.True(anchors.MaxY > 0f);
            Assert.True(anchors.MaxX < 0.01f);
            Assert.True(anchors.MaxY < 0.01f);
        }

        [Fact]
        public void FullScreen_IsUnitSquare()
        {
            // 兜底常量本身也要被守住：所有非法输入都靠它，写错一位就整屏错位。
            Assert.Equal(0f, SafeAreaAnchors.FullScreen.MinX, Precision);
            Assert.Equal(0f, SafeAreaAnchors.FullScreen.MinY, Precision);
            Assert.Equal(1f, SafeAreaAnchors.FullScreen.MaxX, Precision);
            Assert.Equal(1f, SafeAreaAnchors.FullScreen.MaxY, Precision);
        }

        [Fact]
        public void Constructor_PreservesEdgesAndRendersDiagnostics()
        {
            // 结构体本身不做校验（校验在 Compute），但取值必须原样保留，ToString 要能读。
            var anchors = new SafeAreaAnchors(0.1f, 0.2f, 0.8f, 0.9f);

            Assert.Equal(0.1f, anchors.MinX, Precision);
            Assert.Equal(0.2f, anchors.MinY, Precision);
            Assert.Equal(0.8f, anchors.MaxX, Precision);
            Assert.Equal(0.9f, anchors.MaxY, Precision);
            Assert.Contains("0.1", anchors.ToString());
        }

        private static void AssertFullScreen(SafeAreaAnchors anchors)
        {
            Assert.Equal(0f, anchors.MinX, Precision);
            Assert.Equal(0f, anchors.MinY, Precision);
            Assert.Equal(1f, anchors.MaxX, Precision);
            Assert.Equal(1f, anchors.MaxY, Precision);
        }
    }
}
