#nullable enable
using System;
using AIFramework.Contracts.Input;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Inputs
{
    /// <summary>
    /// 触屏输入源：把单指触摸翻译成统一动作（上滑跳、下滑蹲、点左右半屏 = 主要/次要交互）。
    /// <para>
    /// <b>手势判定口径（锁死）</b>：从手指按下开始累计总位移（不用 <c>Touch.deltaPosition</c> 的逐帧增量，
    /// 因为增量累加会受掉帧影响，且 <c>Ended</c> 那帧的 delta 通常是 0）。纵向总位移 ≥ 屏高 6% 且明显大于
    /// 横向位移 → 上滑 <see cref="InputAction.Jump"/> / 下滑 <see cref="InputAction.Slide"/>；总位移 &lt; 屏高 3%
    /// → 视为点击，按<b>按下点</b>所在半屏给 <see cref="InputAction.Primary"/>（右）/ <see cref="InputAction.Secondary"/>（左）；
    /// 介于两者之间（拖拽）不产生任何动作，避免"滑到一半松手"被误判成跳跃。
    /// </para>
    /// <para>
    /// <b>为什么阈值取屏高比例而不是固定像素</b>：<c>Touch</c> 的坐标是像素，同一手势在 720p / 1080p / 2K
    /// 设备上的像素数差一倍以上；按屏高比例归一化后，手感在不同分辨率下一致（6% ≈ 拇指滑动一指节）。
    /// </para>
    /// <para>
    /// <b>为什么滑动在 <c>Moved</c> 就触发、不等到抬手</b>：跳跃/下蹲要的是即时响应；等到 <c>Ended</c>
    /// 才发的输入会晚 100~200ms，跑酷里足够撞死。抬手只负责"没滑成"的点击判定。
    /// </para>
    /// <para>
    /// <b>只跟单指</b>：需求是单指手势；多指时只跟踪第一根按下的手指，其余触摸完全忽略（它们的
    /// <c>Ended</c> 也不会被误判成点击）。
    /// </para>
    /// <para>
    /// <b>真机备注（本轮未验证）</b>：Editor 里 <c>Input.touchCount</c> 恒 0，触屏分支只能在真机或
    /// Unity Remote 上验收；另外旧版 EventSystem 会把触摸当鼠标喂给 HUD 按钮，所以"点按钮"同时也会产生
    /// 一次 <see cref="InputAction.Primary"/>/<see cref="InputAction.Secondary"/> 动作——本演示场景的路由
    /// 不消费这两个动作，故无冲突；将来要把 Primary 接进玩法，必须先判"指针是否落在 UI 上"再做。
    /// </para>
    /// </summary>
    public sealed class TouchInputSource : IInputSource
    {
        /// <summary>滑动判定阈值（屏高比例）：6% ≈ 1080p 下 115px。</summary>
        private const float SwipeThresholdScreenRatio = 0.06f;

        /// <summary>点击判定阈值（屏高比例）：总位移小于 3% 才算点击，否则算拖拽（不产生动作）。</summary>
        private const float TapMovementScreenRatio = 0.03f;

        /// <summary>半屏分割比例（点击左半 = Secondary，右半 = Primary）。</summary>
        private const float HalfScreenRatio = 0.5f;

        /// <summary>当前跟踪的手指 id（-1 = 没有活跃触摸）。</summary>
        private int _activeFingerId = -1;

        /// <summary>按下点的屏幕 x（像素，左下原点）——点击按"按下点"判半屏，抬手抖动不改判定。</summary>
        private float _startX;

        /// <summary>按下点的屏幕 y（像素，左下原点）。</summary>
        private float _startY;

        /// <summary>本次触摸是否已经产出过滑动动作（产出后就绝不再判点击）。</summary>
        private bool _swipeConsumed;

        /// <summary>
        /// 拉取本帧触摸手势。帧内最多产出一个滑动动作 + 一个点击动作（同一根手指二者互斥）。
        /// </summary>
        /// <returns>本帧快照（触屏不提供轴，Horizontal/Vertical 恒 0）。</returns>
        public InputFrame Poll()
        {
            int mask = 0;
            int touchCount = Input.touchCount;

            // 活跃手指在未上报 Ended 的情况下消失（系统取消 / 应用切后台丢触摸）时，按"没发生过"复位，
            // 否则下一次按下会被当成"第二根手指"永久忽略。
            if (_activeFingerId >= 0 && !ContainsFinger(touchCount, _activeFingerId))
            {
                ResetActiveTouch();
            }

            for (int i = 0; i < touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);

                if (touch.phase == TouchPhase.Began)
                {
                    // 单指语义：已有活跃触摸时，新按下的手指不抢焦点。
                    if (_activeFingerId >= 0)
                    {
                        continue;
                    }

                    _activeFingerId = touch.fingerId;
                    _startX = touch.position.x;
                    _startY = touch.position.y;
                    _swipeConsumed = false;
                    continue;
                }

                if (touch.fingerId != _activeFingerId)
                {
                    continue;
                }

                float dx = touch.position.x - _startX;
                float dy = touch.position.y - _startY;

                if (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
                {
                    if (!_swipeConsumed && IsVerticalSwipe(dx, dy))
                    {
                        mask |= BitOf(dy > 0f ? InputAction.Jump : InputAction.Slide);
                        _swipeConsumed = true;
                    }

                    continue;
                }

                if (touch.phase == TouchPhase.Canceled)
                {
                    ResetActiveTouch();
                    continue;
                }

                // Ended（一次触摸的最后一帧仍然会上报，所以点击在抬手这帧判定）。
                if (!_swipeConsumed)
                {
                    if (IsVerticalSwipe(dx, dy))
                    {
                        // 极快的一甩可能只有 Began + Ended 两帧、没有任何 Moved，这里补判一次滑动。
                        mask |= BitOf(dy > 0f ? InputAction.Jump : InputAction.Slide);
                    }
                    else if (IsTap(dx, dy))
                    {
                        mask |= BitOf(_startX >= Screen.width * HalfScreenRatio
                            ? InputAction.Primary
                            : InputAction.Secondary);
                    }
                }

                ResetActiveTouch();
            }

            return new InputFrame(mask, 0f, 0f);
        }

        /// <summary>
        /// 触屏没有"按住某个动作"的语义：上滑/点击都是瞬时手势，抬手即结束。
        /// 将来若有"按住蓄力"类玩法，应由业务在 <c>Poll</c> 之外自行计时，而不是把 Touch 的持续态
        /// 硬映射成某个动作的 held（否则"按住屏幕"会等价于"一直按住跳跃"，语义完全错位）。
        /// </summary>
        /// <param name="action">待查询动作。</param>
        /// <returns>恒为 <c>false</c>。</returns>
        public bool IsHeld(InputAction action)
        {
            return false;
        }

        /// <summary>当前活跃触摸数中是否有指定手指（用于检测"手指无声消失"）。</summary>
        /// <param name="touchCount">本帧触摸数。</param>
        /// <param name="fingerId">要找的手指 id。</param>
        /// <returns>找到返回 <c>true</c>。</returns>
        private static bool ContainsFinger(int touchCount, int fingerId)
        {
            for (int i = 0; i < touchCount; i++)
            {
                if (Input.GetTouch(i).fingerId == fingerId)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>是否构成纵向滑动：纵向总位移达阈值且明显大于横向位移（斜滑不误判）。</summary>
        /// <param name="dx">相对按下点的横向位移（像素）。</param>
        /// <param name="dy">相对按下点的纵向位移（像素，向上为正）。</param>
        /// <returns>构成纵向滑动返回 <c>true</c>。</returns>
        private static bool IsVerticalSwipe(float dx, float dy)
        {
            float threshold = Screen.height * SwipeThresholdScreenRatio;
            float vertical = Math.Abs(dy);
            return vertical >= threshold && vertical > Math.Abs(dx);
        }

        /// <summary>是否构成点击：总位移小于点击阈值（两个方向都要小）。</summary>
        /// <param name="dx">相对按下点的横向位移（像素）。</param>
        /// <param name="dy">相对按下点的纵向位移（像素）。</param>
        /// <returns>构成点击返回 <c>true</c>。</returns>
        private static bool IsTap(float dx, float dy)
        {
            float limit = Screen.height * TapMovementScreenRatio;
            return Math.Abs(dx) <= limit && Math.Abs(dy) <= limit;
        }

        /// <summary>复位到"没有活跃触摸"状态（下一次 Began 重新开始跟踪）。</summary>
        private void ResetActiveTouch()
        {
            _activeFingerId = -1;
            _startX = 0f;
            _startY = 0f;
            _swipeConsumed = false;
        }

        /// <summary>动作 → 位掩码（<c>1 &lt;&lt; (int)action</c>）。</summary>
        /// <param name="action">动作。</param>
        /// <returns>位掩码。</returns>
        private static int BitOf(InputAction action)
        {
            return 1 << (int)action;
        }
    }
}
