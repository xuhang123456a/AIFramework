// 仅用于在无 Unity 的机器上做"形状级"编译校验：不是真的 UnityEngine，别进仓库。
// 追加段（T7-5 画质档位）：只补 QualitySettings / ShadowQuality 的形状，不含任何画质决策逻辑。

namespace UnityEngine
{
    /// <summary>
    /// 阴影质量（形状桩）：数值与真实 Unity 的 <c>UnityEngine.ShadowQuality</c> 一致
    /// （<c>Disable = 0</c> / <c>HardOnly = 1</c> / <c>All = 2</c>），
    /// 因为适配层把 <c>QualityProfile.ShadowQualityLevel</c> 直接强转成本枚举。
    /// </summary>
    public enum ShadowQuality
    {
        /// <summary>关闭阴影。</summary>
        Disable = 0,

        /// <summary>只有硬阴影（无 PCF 过滤）。</summary>
        HardOnly = 1,

        /// <summary>硬阴影 + 软阴影。</summary>
        All = 2
    }

    /// <summary>
    /// 画质设置（形状桩）：真实 Unity 里这些都是<b>可读写属性</b>，且内部会被 <c>QualitySettings.SetQualityLevel</c>
    /// 整体覆盖。桩做成自动属性：形状校验只需要"能读能写能强转"。
    /// <para>
    /// <b>桩不会真的改变任何东西</b>，所以离线校验只能证明"适配层的写入语句类型正确"；
    /// 真机上写进去之后帧率与阴影是否按预期变化，必须 PlayMode/真机验收。
    /// </para>
    /// </summary>
    public static class QualitySettings
    {
        /// <summary>垂直同步次数（0 = 关闭；1 = 每帧同步）。打开时 <c>Application.targetFrameRate</c> 在桌面/主机平台会被忽略。</summary>
        public static int vSyncCount { get; set; }

        /// <summary>阴影质量等级（与 <see cref="ShadowQuality"/> 对应）。</summary>
        public static ShadowQuality shadows { get; set; }

        /// <summary>阴影可见距离（世界单位，米）。</summary>
        public static float shadowDistance { get; set; }
    }
}
