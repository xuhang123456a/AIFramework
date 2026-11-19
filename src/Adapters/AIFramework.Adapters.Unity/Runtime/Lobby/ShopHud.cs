#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Features.Shop;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>商店面板的 uGUI 皮肤：金币文本 + 商品列表。</summary>
    [DisallowMultipleComponent]
    public sealed class ShopHud : MonoBehaviour, IShopView
    {
        /// <summary>金币文本。</summary>
        [SerializeField] private Text goldText = null!;

        /// <summary>商品列表容器。</summary>
        [SerializeField] private Transform list = null!;

        /// <summary>商品列表项模板（未激活）。</summary>
        [SerializeField] private GameObject itemTemplate = null!;

        private readonly List<GameObject> _items = new List<GameObject>();

        /// <inheritdoc />
        public event Action<int>? OnBuyClicked;

        /// <inheritdoc />
        public void RenderGold(int gold)
        {
            if (goldText != null)
            {
                goldText.text = "金币 " + gold;
            }
        }

        /// <inheritdoc />
        public void RenderItems(IReadOnlyList<ShopItemViewData> items)
        {
            if (list == null || itemTemplate == null)
            {
                return;
            }

            while (_items.Count < items.Count)
            {
                GameObject item = Instantiate(itemTemplate, list);
                item.name = "ShopItem_" + _items.Count;
                _items.Add(item);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                bool active = i < items.Count;
                _items[i].SetActive(active);
                if (!active)
                {
                    continue;
                }

                ShopItemViewData data = items[i];
                Text? label = _items[i].GetComponentInChildren<Text>();
                if (label != null)
                {
                    label.text = "商品" + data.ItemId + " · " + data.PriceCount + "金";
                }

                Button? button = _items[i].GetComponentInChildren<Button>();
                if (button != null)
                {
                    int itemId = data.ItemId;
                    button.interactable = data.Affordable;
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => OnBuyClicked?.Invoke(itemId));
                }
            }
        }
    }
}
