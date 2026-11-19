#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Features.Mail;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>邮件面板的 uGUI 皮肤：附件文本 + 领取按钮（删除按钮接同一个 HUD 的删除事件）。</summary>
    [DisallowMultipleComponent]
    public sealed class MailHud : MonoBehaviour, IMailView
    {
        /// <summary>邮件列表容器。</summary>
        [SerializeField] private Transform list = null!;

        /// <summary>邮件列表项模板（未激活）。</summary>
        [SerializeField] private GameObject itemTemplate = null!;

        private readonly List<GameObject> _items = new List<GameObject>();

        /// <inheritdoc />
        public event Action<int>? OnClaimClicked;

        /// <inheritdoc />
#pragma warning disable CS0067 // 界面暂未放"删除"按钮：事件由接口要求，等 UI 加按钮后再接。
        public event Action<int>? OnDeleteClicked;
#pragma warning restore CS0067

        /// <inheritdoc />
        public void RenderMails(IReadOnlyList<MailItemViewData> mails)
        {
            if (list == null || itemTemplate == null)
            {
                return;
            }

            while (_items.Count < mails.Count)
            {
                GameObject item = Instantiate(itemTemplate, list);
                item.name = "Mail_" + _items.Count;
                _items.Add(item);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                bool active = i < mails.Count;
                _items[i].SetActive(active);
                if (!active)
                {
                    continue;
                }

                MailItemViewData data = mails[i];
                Text? label = _items[i].GetComponentInChildren<Text>();
                if (label != null)
                {
                    label.text = "邮件" + data.MailId + " 附件" + data.RewardItemId + "×" + data.RewardCount
                        + (data.Claimed ? "（已领）" : string.Empty);
                }

                Button? button = _items[i].GetComponentInChildren<Button>();
                if (button != null)
                {
                    int mailId = data.MailId;
                    button.interactable = data.CanClaim;
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => OnClaimClicked?.Invoke(mailId));
                }
            }
        }
    }
}
