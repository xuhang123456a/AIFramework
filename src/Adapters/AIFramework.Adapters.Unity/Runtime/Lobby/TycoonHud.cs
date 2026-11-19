#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Meta;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 模拟经营面板的 uGUI 皮肤：状态行 + 建筑列表文本 + 三个"建造"按钮 + 升级 / 雇员工 / 结算一次 / 重新开始。
    /// <para>
    /// 只做两件事：把 <see cref="TycoonSnapshot"/> 画到控件上、把点击意图抛回事件。
    /// 建不建得起、升不升得动、员工是否超编全部由 <c>TycoonHandler</c> 判定并静默拒绝，
    /// 本类<b>不</b>据此禁用按钮——"该不该禁用"是第二套业务规则，面板只负责"点完让控制器刷新"。
    /// </para>
    /// <para>
    /// <b>为什么升级 / 雇佣只有一颗按钮</b>：建筑列表是一段文本而不是动态行控件，
    /// 所以两条意图统一作用于列表第一栋已建造建筑（见 <see cref="_targetBuildingId"/>）；
    /// 一栋都没建时回落到默认 id 1，点下去由处理器静默拒绝。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TycoonHud : MonoBehaviour
    {
        /// <summary>没有已建造建筑时，升级 / 雇佣目标的回落值（处理器会静默拒绝未知目标）。</summary>
        private const int DefaultTargetBuildingId = 1;

        /// <summary>状态行：金币 / 每 tick 总收入 / 已结算 tick 数。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>建筑列表文本（每行一栋已建造建筑）。</summary>
        [SerializeField] private Text buildingListText = null!;

        /// <summary>建造建筑 1（摊位）。</summary>
        [SerializeField] private Button build1Button = null!;

        /// <summary>建造建筑 2（商店）。</summary>
        [SerializeField] private Button build2Button = null!;

        /// <summary>建造建筑 3（餐厅）。</summary>
        [SerializeField] private Button build3Button = null!;

        /// <summary>升级当前目标建筑。</summary>
        [SerializeField] private Button upgradeButton = null!;

        /// <summary>给当前目标建筑雇一名员工。</summary>
        [SerializeField] private Button hireButton = null!;

        /// <summary>结算一次经营 tick。</summary>
        [SerializeField] private Button tickButton = null!;

        /// <summary>重新开始（控制器重建宿主与切片，回到初始金币与空场地）。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>升级 / 雇佣的目标建筑 id；每次 <see cref="Render"/> 按快照刷新。</summary>
        private int _targetBuildingId = DefaultTargetBuildingId;

        /// <summary>点某一栋的"建造"；参数是子表 <c>TbTycoonBuilding.buildingId</c>。</summary>
        public event Action<int>? OnBuildClicked;

        /// <summary>点"升级"；参数是当前目标建筑 id。</summary>
        public event Action<int>? OnUpgradeClicked;

        /// <summary>点"雇员工"；参数是当前目标建筑 id。</summary>
        public event Action<int>? OnHireClicked;

        /// <summary>点"结算一次"。</summary>
        public event Action? OnTickClicked;

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        private void Start()
        {
            if (build1Button != null)
            {
                build1Button.onClick.AddListener(HandleBuild1Clicked);
            }

            if (build2Button != null)
            {
                build2Button.onClick.AddListener(HandleBuild2Clicked);
            }

            if (build3Button != null)
            {
                build3Button.onClick.AddListener(HandleBuild3Clicked);
            }

            if (upgradeButton != null)
            {
                upgradeButton.onClick.AddListener(HandleUpgradeClicked);
            }

            if (hireButton != null)
            {
                hireButton.onClick.AddListener(HandleHireClicked);
            }

            if (tickButton != null)
            {
                tickButton.onClick.AddListener(HandleTickClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(HandleRestartClicked);
            }
        }

        private void OnDestroy()
        {
            // 与 Start 严格配对：场景重载后残留监听会让一次点击触发两次命令。
            if (build1Button != null)
            {
                build1Button.onClick.RemoveListener(HandleBuild1Clicked);
            }

            if (build2Button != null)
            {
                build2Button.onClick.RemoveListener(HandleBuild2Clicked);
            }

            if (build3Button != null)
            {
                build3Button.onClick.RemoveListener(HandleBuild3Clicked);
            }

            if (upgradeButton != null)
            {
                upgradeButton.onClick.RemoveListener(HandleUpgradeClicked);
            }

            if (hireButton != null)
            {
                hireButton.onClick.RemoveListener(HandleHireClicked);
            }

            if (tickButton != null)
            {
                tickButton.onClick.RemoveListener(HandleTickClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(HandleRestartClicked);
            }
        }

        /// <summary>
        /// 按快照重画状态行与建筑列表，并顺手把升级 / 雇佣目标对齐到列表第一栋。
        /// <b>为什么不在这里算任何数值</b>：金币、收入、等级、员工数都已经由处理器算好，
        /// 皮肤只做字符串拼接，多算一次就是第二套公式。
        /// </summary>
        /// <param name="snapshot">处理器返回的经营快照。</param>
        public void Render(TycoonSnapshot snapshot)
        {
            _targetBuildingId = snapshot.Buildings.Length > 0
                ? snapshot.Buildings[0].BuildingId
                : DefaultTargetBuildingId;

            if (statusText != null)
            {
                statusText.text = "金币 " + snapshot.Gold
                    + " · 总收入 " + snapshot.TotalIncomePerTick + "/tick"
                    + " · 已结算 " + snapshot.TickCount + " tick";
            }

            if (buildingListText != null)
            {
                buildingListText.text = FormatBuildings(snapshot);
            }
        }

        /// <summary>建筑列表文案：首行标注升级 / 雇佣目标，其后每栋一行。</summary>
        /// <param name="snapshot">处理器返回的经营快照。</param>
        /// <returns>可直接赋给 <see cref="Text.text"/> 的多行文本。</returns>
        private string FormatBuildings(TycoonSnapshot snapshot)
        {
            if (snapshot.Buildings.Length == 0)
            {
                return "尚无建筑：点『建造 1/2/3』开工（建筑按 tick 产出金币）";
            }

            var builder = new StringBuilder();
            builder.Append("建筑列表（升级 / 雇佣作用于第一栋：建筑 ").Append(_targetBuildingId).Append("）");
            for (int i = 0; i < snapshot.Buildings.Length; i++)
            {
                TycoonBuildingState state = snapshot.Buildings[i];
                builder.Append('\n')
                    .Append("建筑 ").Append(state.BuildingId)
                    .Append(" · Lv").Append(state.Level)
                    .Append(" · 员工 ").Append(state.StaffCount)
                    .Append(" · 产出 ").Append(state.IncomePerTick).Append("/tick");
            }

            return builder.ToString();
        }

        private void HandleBuild1Clicked()
        {
            OnBuildClicked?.Invoke(1);
        }

        private void HandleBuild2Clicked()
        {
            OnBuildClicked?.Invoke(2);
        }

        private void HandleBuild3Clicked()
        {
            OnBuildClicked?.Invoke(3);
        }

        private void HandleUpgradeClicked()
        {
            OnUpgradeClicked?.Invoke(_targetBuildingId);
        }

        private void HandleHireClicked()
        {
            OnHireClicked?.Invoke(_targetBuildingId);
        }

        private void HandleTickClicked()
        {
            OnTickClicked?.Invoke();
        }

        private void HandleRestartClicked()
        {
            OnRestartClicked?.Invoke();
        }
    }
}
