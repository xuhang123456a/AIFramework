#nullable enable
using AIFramework.Contracts.Services;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 画质端口（Unity 实现）：把 <see cref="QualityProfile"/> 落到 <c>QualitySettings</c> 与
    /// <c>Application.targetFrameRate</c> 上。
    /// <para>
    /// <b>写了哪几项、为什么</b>：
    /// <list type="bullet">
    /// <item><c>Application.targetFrameRate</c>：帧率上限。移动端它只是"上限"，负载高时照样掉帧，
    /// 所以它用来<b>省电/控发热</b>（低档限 30fps）而不是"保证流畅"。</item>
    /// <item><c>QualitySettings.vSyncCount = 0</c>：<b>只在目标帧率 &gt; 0 时写</b>。桌面/主机平台上一旦开垂直同步，
    /// <c>targetFrameRate</c> 会被直接忽略（表现为"改了没反应"）；移动端 <c>vSyncCount</c> 本身无效，写 0 无副作用。
    /// 档位变化是低频事件（冷却 ≥ 5 秒），不会造成每帧抖动。</item>
    /// <item><c>QualitySettings.shadows</c>：0/1/2 直接映射 <c>ShadowQuality.Disable/HardOnly/All</c>
    /// （数值与 Unity 枚举一致，见 <c>QualityProfile.ShadowQualityLevel</c> 的契约注释）。</item>
    /// <item><c>QualitySettings.shadowDistance</c>：阴影等级给一条收敛的可见距离曲线（0/30/60 米）。
    /// <b>为什么不读项目 QualitySettings 里的原值再改</b>：原值取决于工程档次（本项目是 15~150 米），
    /// 而"档位 → 距离"必须是确定的，否则同一个档位在不同工程里表现不同，回归对比失去意义。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>刻意没写 <c>QualitySettings.SetQualityLevel</c></b>：那个 API 会整体覆盖工程里 6 档画质设置
    /// （纹理、AA、各向异性、LOD……），而"哪一档对应哪个 index"是工程私有知识。用一个 3 档枚举去驱动
    /// 6 档工程设置，等于把两套语义硬绑在一起，换工程/换管线就错位。需要联动时，应在装配处把
    /// "档位 → 工程质量 index"的映射显式写出来，而不是由本端口猜。
    /// </para>
    /// <para>
    /// <b>粒子预算不在这里落地</b>：Unity 没有"全局粒子上限"API（<c>QualitySettings.particleRaycastBudget</c>
    /// 是"粒子射线检测预算"，不是粒子数上限，硬套会把语义写歪）。预算值随
    /// <c>AIFramework.Core.Services.QualityTierSelector.CurrentProfile</c> 一起发布，由特效层
    /// （<c>UnityVfxService</c>/特效池）自己读取并限制 <c>ParticleSystem.maxParticles</c> 或池容量。
    /// </para>
    /// <para>
    /// <b>幂等</b>：除<b>首次</b> <see cref="Apply"/> 外，同档位重复调用直接返回。首次必须真实落地，
    /// 因为构造期的 <see cref="CurrentTier"/> 只是"尚未收到配置"的占位值（Medium），并不代表工程里的
    /// <c>QualitySettings</c> 已经是 Medium——项目默认档可以是任何一档，甚至被别的脚本改过。
    /// </para>
    /// <para>
    /// <b>线程</b>：Unity 的 <c>QualitySettings</c>/<c>Application</c> 只能在主线程访问；
    /// 本类全同步、不加锁，也不做任何延迟应用。
    /// </para>
    /// </summary>
    public sealed class UnityQualitySettingsPort : IQualitySettingsPort
    {
        /// <summary>阴影关闭时的可见距离（米）：关阴影时该值不参与渲染，写 0 表明"没有阴影"。</summary>
        private const float ShadowsDisabledDistance = 0f;

        /// <summary>硬阴影档的可见距离（米）：够看清单位脚下投影，又不至于让远景阴影铺满整屏。</summary>
        private const float HardShadowsDistance = 30f;

        /// <summary>完整阴影档的可见距离（米）：旗舰机/编辑器基线。</summary>
        private const float FullShadowsDistance = 60f;

        /// <summary>平台支持的最大阴影等级（<c>ShadowQuality.All</c>），超过的值夹到它。</summary>
        private const int MaxShadowQualityLevel = (int)ShadowQuality.All;

        /// <summary>是否已经落地过至少一份配置（首次 Apply 不按档位短路，见类注释）。</summary>
        private bool _hasApplied;

        /// <summary>最近一次成功落地的档位。</summary>
        private QualityTier _currentTier = QualityTier.Medium;

        /// <inheritdoc />
        /// <remarks>
        /// 首次 <see cref="Apply"/> 之前返回 Medium（与选择器的初始档一致），
        /// 它表达的是"端口还没有收到任何配置"，不是"引擎当前是 Medium"。
        /// </remarks>
        public QualityTier CurrentTier
        {
            get { return _currentTier; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 只做四件事：写目标帧率、关垂直同步（仅当有帧率上限）、写阴影等级与阴影距离，最后更新记账。
        /// 全程无分配、无异常处理——这几个 Unity API 在所有平台上都不会抛，包 <c>try/catch</c>
        /// 只会把真实的编程错误吞掉。
        /// </remarks>
        public void Apply(in QualityProfile profile)
        {
            if (_hasApplied && profile.Tier == _currentTier)
            {
                // 幂等短路：冷启动重装、场景重载、设置面板重复提交都会走到这里。
                return;
            }

            _currentTier = profile.Tier;
            _hasApplied = true;

            Application.targetFrameRate = profile.TargetFrameRate;

            if (profile.TargetFrameRate > 0)
            {
                // 垂直同步会压掉 targetFrameRate（桌面/主机），必须让路；目标帧率为 0（不限制）时
                // 保持工程原有的 vSync 设置，避免把"不限帧"变成"撕裂+满载"。
                QualitySettings.vSyncCount = 0;
            }

            int shadowLevel = ClampShadowLevel(profile.ShadowQualityLevel);
            QualitySettings.shadows = (ShadowQuality)shadowLevel;
            QualitySettings.shadowDistance = ShadowDistanceFor(shadowLevel);
        }

        /// <summary>阴影等级夹到 <c>[0, MaxShadowQualityLevel]</c>：契约只保证非负，上界是平台知识。</summary>
        private static int ClampShadowLevel(int shadowQualityLevel)
        {
            if (shadowQualityLevel < 0)
            {
                return 0;
            }

            return shadowQualityLevel > MaxShadowQualityLevel ? MaxShadowQualityLevel : shadowQualityLevel;
        }

        /// <summary>阴影等级 → 可见距离（米）。未覆盖的等级按"最高档"处理，宁可多画阴影也不出现"档位很高却没阴影"。</summary>
        private static float ShadowDistanceFor(int shadowLevel)
        {
            switch (shadowLevel)
            {
                case 0:
                    return ShadowsDisabledDistance;
                case 1:
                    return HardShadowsDistance;
                default:
                    return FullShadowsDistance;
            }
        }
    }
}
