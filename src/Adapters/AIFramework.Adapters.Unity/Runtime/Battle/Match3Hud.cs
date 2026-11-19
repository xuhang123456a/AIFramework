#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 三消棋盘的 uGUI 皮肤：状态行 + 池化格子按钮。
    /// <para>
    /// <b>为什么格子必须池化</b>：<see cref="Render"/> 每 0.25 秒就要刷一次全盘，若每次都
    /// <c>Instantiate</c>/销毁，一小时就是几万次创建与 GC；池子按"宽×高"一次建够，之后只改
    /// <c>Image.color</c>、<c>interactable</c> 与点击回调，不碰实例生命周期。
    /// </para>
    /// <para>
    /// <b>为什么点击只抛坐标、不做相邻/消除判断</b>：那属于内核规则，皮肤只负责把"玩家点了哪一格"
    /// 翻译成 <c>(x, y)</c> 事件；两套规则各自演化必然分叉。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Match3Hud : MonoBehaviour
    {
        /// <summary>空格/未开孔格子的底色（宝石 ID 为 0 时使用）。</summary>
        private static readonly Color EmptyCellColor = new Color(0.10f, 0.10f, 0.12f, 1f);

        /// <summary>5 种内置宝石色：ID 1..5 依次对应，超出范围按 5 取模回绕。</summary>
        private static readonly Color[] GemColors = new Color[]
        {
            new Color(0.90f, 0.25f, 0.25f, 1f),
            new Color(0.30f, 0.80f, 0.35f, 1f),
            new Color(0.25f, 0.55f, 0.95f, 1f),
            new Color(0.95f, 0.82f, 0.20f, 1f),
            new Color(0.70f, 0.35f, 0.90f, 1f)
        };

        /// <summary>格子被点击（参数为棋盘坐标 x, y）；交换语义由控制器决定。</summary>
        public event Action<int, int>? OnCellClicked;

        /// <summary>状态行：分数 / 目标 / 步数（结束时追加胜负）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>棋盘格容器（场景里挂 <c>GridLayoutGroup</c> 排版，本类只管往里塞格子）。</summary>
        [SerializeField] private Transform boardRoot = null!;

        /// <summary>格子按钮模板（未激活，内含 Image + Button）；运行时被复制成池子。</summary>
        [SerializeField] private GameObject cellTemplate = null!;

        /// <summary>格子池：下标 = <c>y * 宽 + x</c>（与 <c>GridLayoutGroup</c> 的行优先顺序一致）。</summary>
        private readonly List<GameObject> _cells = new List<GameObject>();

        /// <summary>与 <see cref="_cells"/> 同序的图片组件缓存（避免每次 Render 都 GetComponent）。</summary>
        private readonly List<Image?> _images = new List<Image?>();

        /// <summary>与 <see cref="_cells"/> 同序的按钮组件缓存；模板缺 Button 时该项为 <c>null</c>。</summary>
        private readonly List<Button?> _buttons = new List<Button?>();

        /// <summary>本局是否已出结果；为 true 后整盘不可点。</summary>
        private bool _finished;

        /// <summary>最近一次渲染出的状态行文本（结束时在它后面追加胜负，而不是重新拼一遍）。</summary>
        private string _status = string.Empty;

        /// <summary>
        /// 按只读视图刷新棋盘与状态行。<paramref name="view"/> 为 <c>null</c>（内核尚未接好）时原样返回，
        /// 不抛异常也不清屏，避免"装配没到位"看起来像"棋盘炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(IMatch3ReadView? view)
        {
            if (view == null)
            {
                return;
            }

            int width = view.Width;
            int height = view.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int needed = width * height;
            EnsureCells(needed);

            for (int i = 0; i < _cells.Count; i++)
            {
                GameObject cell = _cells[i];
                bool inside = i < needed;
                cell.SetActive(inside);
                if (!inside)
                {
                    continue;
                }

                int x = i % width;
                int y = i / width;
                int gem = view.GetGem(x, y);

                Image? image = _images[i];
                if (image != null)
                {
                    image.color = GemColor(gem);
                }

                BindCell(_buttons[i], x, y, gem != 0);
            }

            _status = "分数 " + view.Score + "/目标 " + view.TargetScore + " · 步数 " + view.MovesLeft;
            SetStatus(_status);
        }

        /// <summary>
        /// 显示结算：在当前状态行后追加"胜利/失败"，并锁死整盘。
        /// 重复调用只追加一次（控制器理论上只调一次，但场景重载/手点面板时不该出现两行后缀）。
        /// </summary>
        /// <param name="result">出门结算报告。</param>
        public void ShowResult(GameplayResult result)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;

            string suffix = result.IsVictory ? " · 胜利" : " · 失败";
            string prefix = _status.Length == 0 ? "对局结束" : _status;
            SetStatus(prefix + suffix);

            for (int i = 0; i < _buttons.Count; i++)
            {
                Button? button = _buttons[i];
                if (button != null)
                {
                    button.interactable = false;
                }
            }
        }

        /// <summary>
        /// 把格子池补到 <paramref name="needed"/> 个（只增不减，多出来的在 <see cref="Render"/> 里隐藏）。
        /// 组件引用在建池时取一次，渲染路径不再 <c>GetComponent</c>。
        /// </summary>
        private void EnsureCells(int needed)
        {
            if (boardRoot == null || cellTemplate == null)
            {
                return;
            }

            while (_cells.Count < needed)
            {
                GameObject cell = Instantiate(cellTemplate, boardRoot);
                cell.name = "Cell_" + _cells.Count;

                // 模板是非激活的，实例同样是非激活的；不主动激活的话 GridLayoutGroup 不会排它。
                cell.SetActive(true);

                _cells.Add(cell);
                _images.Add(cell.GetComponent<Image>());
                _buttons.Add(cell.GetComponent<Button>());
            }
        }

        /// <summary>
        /// 把第 (x, y) 格绑定成"点击即抛事件"。每次 Render 重绑是刻意的：
        /// 坐标随棋盘尺寸变化，重绑比维护"下标 → 坐标"的镜像状态更难出错；64 个小委托的开销远低于一次 Text 重建。
        /// </summary>
        private void BindCell(Button? button, int x, int y, bool hasGem)
        {
            if (button == null)
            {
                return;
            }

            // 空格不可点；已出结果后整盘不可点。
            button.interactable = !_finished && hasGem;

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() =>
            {
                if (_finished)
                {
                    return;
                }

                Action<int, int>? handler = OnCellClicked;
                if (handler != null)
                {
                    handler(x, y);
                }
            });
        }

        /// <summary>宝石 ID → 颜色；0 视为空格，其它按 5 色回绕。</summary>
        private static Color GemColor(int gemId)
        {
            if (gemId <= 0)
            {
                return EmptyCellColor;
            }

            return GemColors[(gemId - 1) % GemColors.Length];
        }

        /// <summary>写状态行（文本框未注入时静默跳过）。</summary>
        private void SetStatus(string message)
        {
            if (statusText == null)
            {
                return;
            }

            statusText.text = message;
        }
    }
}
