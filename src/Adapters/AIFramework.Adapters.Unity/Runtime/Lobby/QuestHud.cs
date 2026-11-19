#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Features.Quest;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>任务面板的 uGUI 皮肤：进度文本 + 领取按钮。</summary>
    [DisallowMultipleComponent]
    public sealed class QuestHud : MonoBehaviour, IQuestView
    {
        /// <summary>任务列表容器。</summary>
        [SerializeField] private Transform list = null!;

        /// <summary>任务列表项模板（未激活）。</summary>
        [SerializeField] private GameObject itemTemplate = null!;

        private readonly List<GameObject> _items = new List<GameObject>();

        /// <inheritdoc />
        public event Action<int>? OnClaimClicked;

        /// <inheritdoc />
        public void RenderQuests(IReadOnlyList<QuestItemViewData> quests)
        {
            if (list == null || itemTemplate == null)
            {
                return;
            }

            while (_items.Count < quests.Count)
            {
                GameObject item = Instantiate(itemTemplate, list);
                item.name = "Quest_" + _items.Count;
                _items.Add(item);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                bool active = i < quests.Count;
                _items[i].SetActive(active);
                if (!active)
                {
                    continue;
                }

                QuestItemViewData data = quests[i];
                Text? label = _items[i].GetComponentInChildren<Text>();
                if (label != null)
                {
                    string suffix = data.Claimed ? "（已领）" : data.Claimable ? "（可领）" : string.Empty;
                    label.text = "任务" + data.QuestId + " " + data.Progress + "/" + data.TargetCount + suffix;
                }

                Button? button = _items[i].GetComponentInChildren<Button>();
                if (button != null)
                {
                    int questId = data.QuestId;
                    button.interactable = data.Claimable;
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => OnClaimClicked?.Invoke(questId));
                }
            }
        }
    }
}
