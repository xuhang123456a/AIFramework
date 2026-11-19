#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Features.IdleTycoon;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 放置面板的 uGUI 皮肤：金币文本 + 收入文本 + 建筑列表（每行一个升级按钮）。
    /// <para>
    /// 只做两件事：把 <see cref="IIdleView"/> 的渲染调用画到控件上、把升级点击抛回事件。
    /// 可不可升、要扣多少钱、升完是几级全部由 Presenter/处理器决定，本类不做任何判断。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IdleHud : MonoBehaviour, IIdleView
    {
        /// <summary>金币文本。</summary>
        [SerializeField] private Text goldText = null!;

        /// <summary>每秒收入文本（Presenter 已拼好，直接显示）。</summary>
        [SerializeField] private Text incomeText = null!;

        /// <summary>建筑列表容器。</summary>
        [SerializeField] private Transform list = null!;

        /// <summary>建筑列表项模板（未激活）。</summary>
        [SerializeField] private GameObject itemTemplate = null!;

        /// <summary>已实例化的列表项池（只增不减，随最大行数稳定）。</summary>
        private readonly List<GameObject> _items = new List<GameObject>();

        /// <inheritdoc />
        public event Action<int>? OnUpgradeClicked;

        /// <inheritdoc />
        public void RenderGold(int value)
        {
            if (goldText != null)
            {
                goldText.text = "金币 " + value;
            }
        }

        /// <inheritdoc />
        public void RenderIncome(string text)
        {
            if (incomeText != null)
            {
                incomeText.text = text;
            }
        }

        /// <inheritdoc />
        public void RenderBuildings(IReadOnlyList<BuildingViewData> buildings)
        {
            if (list == null || itemTemplate == null)
            {
                return;
            }

            while (_items.Count < buildings.Count)
            {
                GameObject item = Instantiate(itemTemplate, list);
                item.name = "Building_" + _items.Count;
                _items.Add(item);
            }

            for (int i = 0; i < _items.Count; i++)
            {
                bool active = i < buildings.Count;
                _items[i].SetActive(active);
                if (!active)
                {
                    continue;
                }

                BuildingViewData data = buildings[i];
                Text? label = _items[i].GetComponentInChildren<Text>();
                if (label != null)
                {
                    label.text = FormatRow(data);
                }

                Button? button = _items[i].GetComponentInChildren<Button>();
                if (button != null)
                {
                    int buildingId = data.BuildingId;
                    button.interactable = data.Affordable;
                    button.onClick.RemoveAllListeners();
                    button.onClick.AddListener(() => OnUpgradeClicked?.Invoke(buildingId));
                }
            }
        }

        /// <summary>行文案：等级 / 上限 / 本栋产出 / 升级费用；满级单独标注。</summary>
        private static string FormatRow(BuildingViewData data)
        {
            string text = "建筑" + data.BuildingId
                + " Lv" + data.Level + "/" + data.MaxLevel
                + " · 产出 " + data.IncomePerSecond + "/秒"
                + " · 升级 " + data.UpgradeCost + "金";
            return data.Level >= data.MaxLevel ? text + "（满级）" : text;
        }
    }
}
