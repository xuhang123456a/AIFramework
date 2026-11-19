#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Features.TowerDefense;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 塔防棋盘的 uGUI 皮肤：状态行 + 8×8 池化格子 + 两个选塔按钮。
    /// <para>
    /// <b>为什么渲染要自己查总线</b>：布局是切片状态，皮肤不该被控制器"喂"一份镜像副本——
    /// 一旦两份状态不同步，玩家就会看到"点下去建成了但格子没变色"。这里每次刷新都走
    /// <see cref="TowerLayoutQueryRequest"/> 取最新快照，控制器只负责告诉它"该刷新了"。
    /// </para>
    /// <para>
    /// <b>为什么格子必须池化</b>：建造、开战、结算都会触发整盘刷新，若每次
    /// <c>Instantiate</c>/销毁，一小时就是几万次创建与 GC；池子按"宽×高"一次建够，
    /// 之后只改 <c>Image.color</c>、<c>interactable</c> 与点击回调。
    /// </para>
    /// <para>
    /// <b>为什么点击只抛坐标</b>：能不能建、扣多少钱、塔 ID 认不认识都属于切片规则，
    /// 皮肤只负责把"玩家点了哪一格"翻译成 <c>(x, y)</c>。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TowerDefenseHud : MonoBehaviour
    {
        /// <summary>第一个选塔按钮对应的塔 ID（对应 <c>TbTower</c> 的 1 号行）。</summary>
        public const int FirstTowerId = 1;

        /// <summary>第二个选塔按钮对应的塔 ID（对应 <c>TbTower</c> 的 2 号行）。</summary>
        public const int SecondTowerId = 2;

        /// <summary>空格底色。</summary>
        private static readonly Color EmptyCellColor = new Color(0.10f, 0.10f, 0.12f, 1f);

        /// <summary>1 号塔的格子色。</summary>
        private static readonly Color FirstTowerColor = new Color(0.90f, 0.32f, 0.24f, 1f);

        /// <summary>2 号塔的格子色。</summary>
        private static readonly Color SecondTowerColor = new Color(0.28f, 0.55f, 0.95f, 1f);

        /// <summary>表里其它塔（未来配置）的兜底色。</summary>
        private static readonly Color OtherTowerColor = new Color(0.45f, 0.82f, 0.36f, 1f);

        /// <summary>被选中的选塔按钮底色。</summary>
        private static readonly Color SelectedTowerButtonColor = new Color(0.95f, 0.82f, 0.20f, 1f);

        /// <summary>未选中的选塔按钮底色。</summary>
        private static readonly Color IdleTowerButtonColor = new Color(0.22f, 0.22f, 0.26f, 1f);

        /// <summary>格子被点击（参数为棋盘坐标 x, y）；建造语义由控制器决定。</summary>
        public event Action<int, int>? OnCellClicked;

        /// <summary>状态行：金币 / 塔数 / 波次 / 帧进度。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>棋盘格容器（场景里挂 <c>GridLayoutGroup</c> 排版，本类只管往里塞格子）。</summary>
        [SerializeField] private Transform boardRoot = null!;

        /// <summary>格子按钮模板（未激活，内含 Image + Button）；运行时被复制成池子。</summary>
        [SerializeField] private GameObject cellTemplate = null!;

        /// <summary>1 号塔的选塔按钮。</summary>
        [SerializeField] private Button firstTowerButton = null!;

        /// <summary>2 号塔的选塔按钮。</summary>
        [SerializeField] private Button secondTowerButton = null!;

        /// <summary>1 号塔按钮的底色图（选中态靠它变色）。</summary>
        [SerializeField] private Image firstTowerImage = null!;

        /// <summary>2 号塔按钮的底色图。</summary>
        [SerializeField] private Image secondTowerImage = null!;

        /// <summary>格子池：下标 = <c>y * 宽 + x</c>（与 <c>GridLayoutGroup</c> 的行优先顺序一致）。</summary>
        private readonly List<GameObject> _cells = new List<GameObject>();

        /// <summary>与 <see cref="_cells"/> 同序的图片组件缓存（避免每次 Refresh 都 GetComponent）。</summary>
        private readonly List<Image?> _images = new List<Image?>();

        /// <summary>与 <see cref="_cells"/> 同序的按钮组件缓存；模板缺 Button 时该项为 <c>null</c>。</summary>
        private readonly List<Button?> _buttons = new List<Button?>();

        /// <summary>总线：布局与金币都从它查；未绑定时整盘不可点。</summary>
        private ICqeBus? _bus;

        /// <summary>当前选中的塔 ID。</summary>
        private int _selectedTowerId = FirstTowerId;

        /// <summary>最近一次渲染出的状态行文本（结算后在它后面追加胜负，而不是重新拼一遍）。</summary>
        private string _status = string.Empty;

        /// <summary>结算后缀（"胜利"/"失败"）；新一局开始时由控制器清空。</summary>
        private string _resultSuffix = string.Empty;

        /// <summary>布局是否可读（塔表缺失/处理器未注册时为 <c>false</c>，整盘禁用）。</summary>
        private bool _layoutReadable;

        /// <summary>当前选中的塔 ID（控制器建造时用它填 <c>BuildTowerCommand.TowerId</c>）。</summary>
        public int SelectedTowerId
        {
            get { return _selectedTowerId; }
        }

        /// <summary>
        /// 绑定总线并挂上选塔按钮回调。可重复调用（先解绑旧的）。
        /// 传 <c>null</c> 表示解绑：之后整盘不可点、状态行显示"总线未绑定"。
        /// </summary>
        /// <param name="bus">CQE 总线。</param>
        public void Bind(ICqeBus? bus)
        {
            Unbind();
            _bus = bus;

            if (firstTowerButton != null)
            {
                firstTowerButton.onClick.AddListener(HandleFirstTowerClicked);
            }

            if (secondTowerButton != null)
            {
                secondTowerButton.onClick.AddListener(HandleSecondTowerClicked);
            }

            UpdateSelectionVisual();
        }

        /// <summary>解绑总线与按钮回调（场景卸载时调用，避免 lambda 留住已销毁对象）。</summary>
        public void Unbind()
        {
            if (firstTowerButton != null)
            {
                firstTowerButton.onClick.RemoveAllListeners();
            }

            if (secondTowerButton != null)
            {
                secondTowerButton.onClick.RemoveAllListeners();
            }

            _bus = null;
        }

        /// <summary>
        /// 按切片的最新布局刷新整盘与状态行。<paramref name="wave"/> 与 <paramref name="stepCount"/>
        /// 由控制器给出（棋皮肤不持有战斗会话，也不该知道内核帧号从哪来）。
        /// </summary>
        /// <param name="wave">当前波次/局号，从 1 开始。</param>
        /// <param name="stepCount">本局已推进的逻辑帧数（让"正在打"这件事可见）。</param>
        public void Render(int wave, int stepCount)
        {
            ICqeBus? bus = _bus;
            if (bus == null)
            {
                _layoutReadable = false;
                DisableAllCells();
                SetStatus("总线未绑定：塔防 HUD 无法查询布局");
                return;
            }

            TowerLayoutSnapshot layout;
            int gold;
            try
            {
                layout = bus.Query<TowerLayoutQueryRequest, TowerLayoutSnapshot>(new TowerLayoutQueryRequest());
                InventorySnapshot inventory = bus.Query<InventoryQueryRequest, InventorySnapshot>(
                    new InventoryQueryRequest(MetaItemIds.Gold));
                gold = inventory.Count;
            }
            catch (InvalidOperationException)
            {
                // 典型成因：TbTower.bin 没打进包，切片被装配层关掉。明确说出来，别让玩家猜"为什么点不动"。
                _layoutReadable = false;
                DisableAllCells();
                SetStatus("塔表缺失（TbTower.bin）：塔防切片未装配，无法建造");
                return;
            }

            _layoutReadable = true;

            int width = layout.Width;
            int height = layout.Height;
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
                int towerId = layout.TowerIdAt(x, y);

                Image? image = _images[i];
                if (image != null)
                {
                    image.color = CellColor(towerId);
                }

                BindCell(_buttons[i], x, y, true);
            }

            _status = "金币 " + gold + " · 塔数 " + layout.TowerCount
                + " · 波次 " + wave + " · 帧 " + stepCount;
            SetStatus(ComposeStatus());
        }

        /// <summary>
        /// 显示结算：在状态行后追加"胜利/失败"。<b>刻意不锁盘</b>：塔防的下一步动作就是继续建塔，
        /// 锁定会逼玩家重进场景才能再建一座（与三消"一盘定输赢"的语义不同）。
        /// </summary>
        /// <param name="result">出门结算报告。</param>
        public void ShowResult(GameplayResult result)
        {
            _resultSuffix = result.IsVictory ? "胜利" : "失败";
            string prefix = _status.Length == 0 ? "对局结束" : _status;
            SetStatus(prefix + " · " + _resultSuffix);
        }

        /// <summary>清掉结算后缀（新一局开始时由控制器调用）。</summary>
        public void ClearResult()
        {
            _resultSuffix = string.Empty;
        }

        /// <summary>选中某座塔（不传表里存在的 ID 时按钮仍会亮，但建造会被切片静默拒绝）。</summary>
        /// <param name="towerId">塔 ID。</param>
        public void SelectTower(int towerId)
        {
            _selectedTowerId = towerId;
            UpdateSelectionVisual();
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

        /// <summary>把第 (x, y) 格绑定成"点击即抛事件"。每次 Render 重绑是刻意的：坐标与可点状态都在变。</summary>
        private void BindCell(Button? button, int x, int y, bool interactable)
        {
            if (button == null)
            {
                return;
            }

            button.interactable = interactable && _layoutReadable;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(() =>
            {
                if (!_layoutReadable)
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

        /// <summary>塔表不可读时把整盘按钮锁死（否则玩家会对着"点了没反应"反复点）。</summary>
        private void DisableAllCells()
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                Button? button = _buttons[i];
                if (button != null)
                {
                    button.interactable = false;
                }
            }
        }

        /// <summary>刷新两个选塔按钮的底色。</summary>
        private void UpdateSelectionVisual()
        {
            if (firstTowerImage != null)
            {
                firstTowerImage.color = _selectedTowerId == FirstTowerId
                    ? SelectedTowerButtonColor
                    : IdleTowerButtonColor;
            }

            if (secondTowerImage != null)
            {
                secondTowerImage.color = _selectedTowerId == SecondTowerId
                    ? SelectedTowerButtonColor
                    : IdleTowerButtonColor;
            }
        }

        private void HandleFirstTowerClicked()
        {
            SelectTower(FirstTowerId);
        }

        private void HandleSecondTowerClicked()
        {
            SelectTower(SecondTowerId);
        }

        /// <summary>塔 ID → 格子颜色；0 视为空格，未知 ID 用兜底色（不隐藏，"有东西但没配颜色"必须看得见）。</summary>
        private static Color CellColor(int towerId)
        {
            if (towerId <= 0)
            {
                return EmptyCellColor;
            }

            if (towerId == FirstTowerId)
            {
                return FirstTowerColor;
            }

            return towerId == SecondTowerId ? SecondTowerColor : OtherTowerColor;
        }

        /// <summary>拼状态行：基础信息 + 可选的结算后缀。</summary>
        private string ComposeStatus()
        {
            return _resultSuffix.Length == 0 ? _status : _status + " · " + _resultSuffix;
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
