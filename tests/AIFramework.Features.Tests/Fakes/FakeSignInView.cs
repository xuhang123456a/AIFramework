using System;
using System.Collections.Generic;
using AIFramework.Features.DailySignIn;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>签到视图假实现：记录每次渲染的 7 格快照，并提供"人手点击"的触发入口。</summary>
    public sealed class FakeSignInView : ISignInView
    {
        public FakeSignInView()
        {
            LastSlots = Array.Empty<SignInSlotViewData>();
        }

        /// <inheritdoc />
        public event Action? OnClaimClicked;

        /// <inheritdoc />
        public event Action<int>? OnRetroactiveClicked;

        /// <summary>渲染次数。</summary>
        public int RenderCount { get; private set; }

        /// <summary>最近一次渲染的格子快照（拷贝副本，视图外不可回写）。</summary>
        public SignInSlotViewData[] LastSlots { get; private set; }

        /// <inheritdoc />
        public void RenderSlots(IReadOnlyList<SignInSlotViewData> slots)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            SignInSlotViewData[] copy = new SignInSlotViewData[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                copy[i] = slots[i];
            }

            RenderCount++;
            LastSlots = copy;
        }

        /// <summary>模拟点击"领取今日奖励"。</summary>
        public void RaiseClaimClicked()
        {
            Action? handler = OnClaimClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>模拟点击某格补签按钮；参数按视图契约回传 1..7 的格子序号。</summary>
        public void RaiseRetroactiveClicked(int slotDayIndex)
        {
            Action<int>? handler = OnRetroactiveClicked;
            if (handler != null)
            {
                handler(slotDayIndex);
            }
        }
    }
}
