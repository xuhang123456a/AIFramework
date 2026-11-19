// 追加的 UnityEngine 形状桩（只新增，不改动已有桩）：俯视相机的旋转与 Inspector 右键菜单。
// 与其它桩一样，它不是真的 UnityEngine，只保证"符号与真实 API 同形"，不能替代真机验证。
using System;

namespace UnityEngine
{
    /// <summary>
    /// 四元数（形状桩）：<b>只覆盖建场景脚本实际用到的成员</b>——<see cref="Euler"/> 与 <see cref="identity"/>。
    /// <para>
    /// <b>为什么需要补这个桩</b>：弹幕射击是俯视玩法，相机必须绕 X 轴转 90° 才能从上往下看战斗平面（X-Z）；
    /// 没有旋转就只剩"相机平视 → 战场缩成一条线"这一种摆法，演示会完全看不出弹幕密度。
    /// 桩只保证 <c>transform.rotation = Quaternion.Euler(...)</c> 这一行能编过，<b>不计算任何旋转</b>。
    /// </para>
    /// </summary>
    public struct Quaternion
    {
        /// <summary>X 分量。</summary>
        public float x;

        /// <summary>Y 分量。</summary>
        public float y;

        /// <summary>Z 分量。</summary>
        public float z;

        /// <summary>W 分量。</summary>
        public float w;

        /// <summary>以四个分量构造（与真实 Unity 同名同序）。</summary>
        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        /// <summary>单位旋转（桩：返回全零分量，不做归一化）。</summary>
        public static Quaternion identity
        {
            get { return new Quaternion(0f, 0f, 0f, 1f); }
        }

        /// <summary>按欧拉角（度）构造（桩：原样塞进分量，不做转换）。</summary>
        /// <param name="x">绕 X 轴的角度（度）。</param>
        /// <param name="y">绕 Y 轴的角度（度）。</param>
        /// <param name="z">绕 Z 轴的角度（度）。</param>
        public static Quaternion Euler(float x, float y, float z)
        {
            return new Quaternion(x, y, z, 1f);
        }
    }

    /// <summary>
    /// Inspector 右键菜单特性（形状桩）：标在 MonoBehaviour 的公开方法上，
    /// 运行时右键组件即可触发该方法。弹幕演示靠它现场触发 <c>StopAutoPlay()</c> 的失败路径
    /// （HUD 的 Inspector 面被需求锁定为"状态行 + 重开按钮"，加不了第二个按钮）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ContextMenuAttribute : Attribute
    {
        /// <summary>菜单项名称。</summary>
        public string menuItem;

        /// <summary>菜单项名称 + 是否作为校验函数（真实 Unity 的第二重载）。</summary>
        public bool validate;

        /// <summary>菜单项名称。</summary>
        public ContextMenuAttribute(string itemName)
        {
            menuItem = itemName;
        }

        /// <summary>菜单项名称 + 校验函数标记。</summary>
        public ContextMenuAttribute(string itemName, bool isValidateFunction)
        {
            menuItem = itemName;
            validate = isValidateFunction;
        }
    }
}
