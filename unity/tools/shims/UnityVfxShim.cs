// 仅用于在没有 Unity 的机器上做"形状级"编译校验：不是真的 UnityEngine 粒子系统，别进运行时工程。
// 只追加形状，不改既有 shim 文件（T3-16 特效端口）。
using System;

namespace UnityEngine
{
    /// <summary>
    /// 粒子系统（形状桩）。
    /// <para>
    /// <b>为什么需要它</b>：<c>UnityVfxService</c> 的对象池要求"每个槽位预挂一个 <see cref="ParticleSystem"/>"
    /// （预制体自己没带粒子系统时用它兜底），没有这个类型连编译都过不去。
    /// </para>
    /// <para>
    /// <b>桩里所有方法都是空实现、<see cref="isPlaying"/> 恒为 <c>false</c></b>：
    /// 因此离线校验只能证明"类型与调用形状对了"，<u>不能</u>证明特效真的播出来/到期真的收掉——
    /// 那必须真机 PlayMode 验证。
    /// </para>
    /// </summary>
    public class ParticleSystem : Component
    {
        /// <summary>是否正在播放（桩恒为 <c>false</c>）。</summary>
        public bool isPlaying
        {
            get { return false; }
        }

        /// <summary>是否已停止（桩恒为 <c>true</c>）。</summary>
        public bool isStopped
        {
            get { return true; }
        }

        /// <summary>开始播放（桩：空实现）。</summary>
        public void Play()
        {
        }

        /// <summary>开始播放（含子物体；桩：空实现）。</summary>
        public void Play(bool withChildren)
        {
        }

        /// <summary>停止发射（桩：空实现）。真机上已在飞的粒子会自然消亡。</summary>
        public void Stop()
        {
        }

        /// <summary>停止发射（含子物体；桩：空实现）。</summary>
        public void Stop(bool withChildren)
        {
        }

        /// <summary>清空已存在的粒子（桩：空实现）。</summary>
        public void Clear()
        {
        }

        /// <summary>清空已存在的粒子（含子物体；桩：空实现）。</summary>
        public void Clear(bool withChildren)
        {
        }
    }
}
