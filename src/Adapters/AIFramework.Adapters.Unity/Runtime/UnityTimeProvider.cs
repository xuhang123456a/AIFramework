#nullable enable
using System;
using AIFramework.Contracts.Ports;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 时间源：单调时钟取引擎启动后的真实秒数，墙钟取系统 UTC。
    /// <para>
    /// 逻辑层只认 <see cref="ITimeProvider"/>，于是"读系统时间"这件事被收敛在适配壳内部：
    /// <c>AIF003</c> 禁止纯逻辑程序集直接写 <c>DateTime.UtcNow</c>，而适配层本来就要落在具体时钟上，
    /// 所以系统时钟读取只允许出现在这里。
    /// </para>
    /// <para>
    /// <b>为什么是 <c>realtimeSinceStartupAsDouble</c></b>：<c>Time.time</c> 受 <c>Time.timeScale</c> 影响，
    /// 暂停/慢放会把超时判定一起冻住；<c>Time.timeSinceLevelLoad</c> 又在每个场景加载时归零。
    /// 逻辑帧推进与超时判定要的是"真实流逝时间"，因此用引擎启动至今的真实秒数。
    /// 该属性自 Unity 2020.2 起提供；若目标工程更老，退回 <c>(double)Time.realtimeSinceStartup</c>，
    /// 代价是进程运行数天后 float 精度明显退化（秒级跳变）。
    /// </para>
    /// </summary>
    public sealed class UnityTimeProvider : ITimeProvider
    {
        /// <summary>
        /// 编辑器/演示用的 UTC 覆盖值。<b>为什么做成字段而不是每次现算</b>：跨天、补签、活动周期
        /// 都需要把时间钉死才能复现，改系统时钟既不可接受也不可重复。
        /// </summary>
        private DateTime? _utcNowOverride;

        /// <inheritdoc />
        /// <remarks>单调递增、不受 <c>Time.timeScale</c> 影响；单位是秒。</remarks>
        public double ElapsedSeconds
        {
            get { return Time.realtimeSinceStartupAsDouble; }
        }

        /// <inheritdoc />
        public DateTime UtcNow
        {
            get
            {
                DateTime? overridden = _utcNowOverride;
                if (overridden.HasValue)
                {
                    return overridden.Value;
                }

                return DateTime.UtcNow;
            }
        }

        /// <summary>当前是否处于"时间被钉住"的调试状态。</summary>
        public bool HasUtcOverride
        {
            get { return _utcNowOverride.HasValue; }
        }

        /// <summary>
        /// 钉住或解除 <see cref="UtcNow"/>：验证跨天刷新、签到补签等时间敏感逻辑时只能用这种方式。
        /// </summary>
        /// <param name="utcNow">要伪造成的时间；传 <c>null</c> 恢复跟随系统时钟。</param>
        public void SetUtcOverride(DateTime? utcNow)
        {
            _utcNowOverride = utcNow;
        }
    }
}
