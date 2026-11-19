#nullable enable
using AIFramework.Features.DailySignIn;
using System;
using System.Collections.Generic;
using System.Text;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 无头签到界面：只做展示与点击模拟，所有判断都在 <c>SignInPresenter</c> 里，
    /// 这个类里没有一句业务条件分支。
    /// </summary>
    public sealed class ConsoleSignInView : ISignInView
    {
        private readonly IIOWriter _writer;

        public ConsoleSignInView(IIOWriter writer)
        {
            _writer = writer;
        }

        public event Action? OnClaimClicked;

        public event Action<int>? OnRetroactiveClicked;

        /// <summary>最近一次渲染结果，冒烟流程与测试可直接断言。</summary>
        public IReadOnlyList<SignInSlotViewData>? LastRendered { get; private set; }

        public void RenderSlots(IReadOnlyList<SignInSlotViewData> slots)
        {
            LastRendered = slots;

            var builder = new StringBuilder();
            builder.Append("签到面板：");
            for (int i = 0; i < slots.Count; i++)
            {
                SignInSlotViewData slot = slots[i];
                if (i > 0)
                {
                    builder.Append(" | ");
                }

                builder.Append('D').Append(slot.DayIndex).Append('=').Append(slot.State);
            }

            _writer.WriteLine(builder.ToString());
        }

        /// <summary>模拟玩家点"领取"。</summary>
        public void SimulateClaimClick()
        {
            OnClaimClicked?.Invoke();
        }

        /// <summary>模拟玩家点"补签"。</summary>
        public void SimulateRetroactiveClick(int dayIndex)
        {
            OnRetroactiveClicked?.Invoke(dayIndex);
        }
    }
}
