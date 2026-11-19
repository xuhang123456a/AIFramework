#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Features.DailySignIn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 签到面板的 uGUI 皮肤：实现 <see cref="ISignInView"/>，只做"把数据画到控件"和"把点击抛出去"。
    /// <para>
    /// 四个面板各自独立成类，是因为 <c>ISignInView</c> 与 <c>IQuestView</c> 都有 `OnClaimClicked` 成员，
    /// 一个类同时实现会撞名；拆开后每个类只面对一个视图契约，职责也更干净。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SignInHud : MonoBehaviour, ISignInView
    {
        /// <summary>格子容器。</summary>
        [SerializeField] private Transform grid = null!;

        /// <summary>格子模板（未激活）。</summary>
        [SerializeField] private GameObject slotTemplate = null!;

        /// <summary>"领取今天"按钮：点击只抛事件，能不能领由 Presenter 判定。</summary>
        [SerializeField] private Button claimButton = null!;

        private readonly List<GameObject> _slots = new List<GameObject>();

        /// <inheritdoc />
        public event Action? OnClaimClicked;

        /// <inheritdoc />
        public event Action<int>? OnRetroactiveClicked;

        private void Start()
        {
            if (claimButton != null)
            {
                claimButton.onClick.AddListener(RaiseClaimClicked);
            }
        }

        private void OnDestroy()
        {
            if (claimButton != null)
            {
                claimButton.onClick.RemoveListener(RaiseClaimClicked);
            }
        }

        /// <summary>把"点了领取"翻译成视图事件；业务判定不在 UI 层。</summary>
        private void RaiseClaimClicked()
        {
            OnClaimClicked?.Invoke();
        }

        /// <inheritdoc />
        public void RenderSlots(IReadOnlyList<SignInSlotViewData> slots)
        {
            if (grid == null || slotTemplate == null)
            {
                return;
            }

            while (_slots.Count < slots.Count)
            {
                GameObject item = Instantiate(slotTemplate, grid);
                item.name = "Slot_" + _slots.Count;
                _slots.Add(item);
            }

            for (int i = 0; i < _slots.Count; i++)
            {
                bool active = i < slots.Count;
                _slots[i].SetActive(active);
                if (!active)
                {
                    continue;
                }

                SignInSlotViewData data = slots[i];
                Text? label = _slots[i].GetComponentInChildren<Text>();
                if (label != null)
                {
                    label.text = "第" + data.DayIndex + "天 " + StateText(data.State);
                }

                Button? button = _slots[i].GetComponentInChildren<Button>();
                if (button != null)
                {
                    int dayIndex = data.DayIndex;
                    bool canClick = data.State == SignInSlotState.Claimable;
                    button.interactable = canClick;
                    button.onClick.RemoveAllListeners();
                    if (canClick)
                    {
                        button.onClick.AddListener(() => OnRetroactiveClicked?.Invoke(dayIndex));
                    }
                }
            }
        }

        private static string StateText(SignInSlotState state)
        {
            switch (state)
            {
                case SignInSlotState.Claimable:
                    return "可领";
                case SignInSlotState.Claimed:
                    return "已领";
                case SignInSlotState.Missed:
                    return "错过";
                default:
                    return "未开";
            }
        }
    }
}
