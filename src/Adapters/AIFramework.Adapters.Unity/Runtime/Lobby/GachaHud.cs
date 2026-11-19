#nullable enable
using System;
using AIFramework.Features.Gacha;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 抽卡面板的 uGUI 皮肤：金币文本 + 最近结果文本 + 单抽/十连按钮。
    /// <para>
    /// 只做两件事：把 <see cref="IGachaView"/> 的渲染调用画到控件上、把两次点击抛回事件。
    /// 抽不抽得起、抽到什么全部由 Presenter/处理器决定；本类<b>不</b>按余额禁用按钮——
    /// 余额判断属于业务规则，UI 只负责把玩家意图抛出去，拒绝由处理器静默完成后再刷新。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GachaHud : MonoBehaviour, IGachaView
    {
        /// <summary>金币文本。</summary>
        [SerializeField] private Text goldText = null!;

        /// <summary>最近结果文本（Presenter 已拼好，直接显示）。</summary>
        [SerializeField] private Text lastResultText = null!;

        /// <summary>单抽按钮。</summary>
        [SerializeField] private Button drawOnceButton = null!;

        /// <summary>十连按钮。</summary>
        [SerializeField] private Button drawTenButton = null!;

        /// <inheritdoc />
        public event Action? OnDrawOnceClicked;

        /// <inheritdoc />
        public event Action? OnDrawTenClicked;

        private void Start()
        {
            if (drawOnceButton != null)
            {
                drawOnceButton.onClick.AddListener(RaiseDrawOnceClicked);
            }

            if (drawTenButton != null)
            {
                drawTenButton.onClick.AddListener(RaiseDrawTenClicked);
            }
        }

        private void OnDestroy()
        {
            if (drawOnceButton != null)
            {
                drawOnceButton.onClick.RemoveListener(RaiseDrawOnceClicked);
            }

            if (drawTenButton != null)
            {
                drawTenButton.onClick.RemoveListener(RaiseDrawTenClicked);
            }
        }

        /// <inheritdoc />
        public void RenderGold(int value)
        {
            if (goldText != null)
            {
                goldText.text = "金币 " + value;
            }
        }

        /// <inheritdoc />
        public void RenderLastResult(string text)
        {
            if (lastResultText != null)
            {
                lastResultText.text = text;
            }
        }

        private void RaiseDrawOnceClicked()
        {
            OnDrawOnceClicked?.Invoke();
        }

        private void RaiseDrawTenClicked()
        {
            OnDrawTenClicked?.Invoke();
        }
    }
}
