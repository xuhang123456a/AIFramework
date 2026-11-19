#nullable enable
using System;
using AIFramework.Contracts.Meta;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 挂机 RPG 面板的 uGUI 皮肤：状态行 + "打 10 tick" / "升一级" / "进下一关" / "重新开始"。
    /// <para>
    /// 只做两件事：把 <see cref="IdleRpgSnapshot"/> 画到控件上、把点击意图抛回事件。
    /// 能不能升、能不能进下一关、这一 tick 会不会打死敌人全部由 <c>IdleRpgHandler</c> 判定，
    /// 本类<b>不</b>据此禁用按钮——禁用属于第二套规则，面板只负责"点完让控制器刷新"。
    /// </para>
    /// <para>
    /// <b>为什么状态行只显示经验值、不显示"升级需要多少"</b>：升级消耗 = 升级前等级 × 100 是切片内部规则，
    /// 快照里没有该字段；在皮肤里复算一次就等于把规则抄成两份，等切片调整系数时两边必然走样。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class IdleRpgHud : MonoBehaviour
    {
        /// <summary>状态行：英雄等级 / 经验 / 战力 / 每秒金币 / 当前关 / 敌人 HP / 战斗状态。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>推进 10 个战斗 tick。</summary>
        [SerializeField] private Button battleTickButton = null!;

        /// <summary>用经验升一级。</summary>
        [SerializeField] private Button levelUpButton = null!;

        /// <summary>手动进入下一关。</summary>
        [SerializeField] private Button nextStageButton = null!;

        /// <summary>重新开始（控制器重建宿主与切片，回到第 1 关、1 级）。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>点"打 10 tick"。</summary>
        public event Action? OnBattleTickClicked;

        /// <summary>点"升一级"。</summary>
        public event Action? OnLevelUpClicked;

        /// <summary>点"进下一关"。</summary>
        public event Action? OnAdvanceStageClicked;

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        private void Start()
        {
            if (battleTickButton != null)
            {
                battleTickButton.onClick.AddListener(HandleBattleTickClicked);
            }

            if (levelUpButton != null)
            {
                levelUpButton.onClick.AddListener(HandleLevelUpClicked);
            }

            if (nextStageButton != null)
            {
                nextStageButton.onClick.AddListener(HandleAdvanceStageClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(HandleRestartClicked);
            }
        }

        private void OnDestroy()
        {
            // 与 Start 严格配对：场景重载后残留监听会让一次点击触发两次命令。
            if (battleTickButton != null)
            {
                battleTickButton.onClick.RemoveListener(HandleBattleTickClicked);
            }

            if (levelUpButton != null)
            {
                levelUpButton.onClick.RemoveListener(HandleLevelUpClicked);
            }

            if (nextStageButton != null)
            {
                nextStageButton.onClick.RemoveListener(HandleAdvanceStageClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(HandleRestartClicked);
            }
        }

        /// <summary>
        /// 按快照重画状态行。<b>为什么不在这里算任何数值</b>：等级、经验、战力、每秒金币、敌人血量
        /// 都已经由处理器算好，皮肤只做字符串拼接，多算一次就是第二套公式。
        /// </summary>
        /// <param name="snapshot">处理器返回的挂机 RPG 快照。</param>
        public void Render(IdleRpgSnapshot snapshot)
        {
            if (statusText == null)
            {
                return;
            }

            IdleHeroState hero = snapshot.Hero;
            statusText.text = "英雄 Lv" + hero.Level
                + " · 经验 " + hero.Exp
                + " · 战力 " + hero.Power
                + " · 金币 " + hero.GoldPerSecond + "/秒"
                + " · 第 " + snapshot.CurrentStage + " 关（最高 " + hero.HighestStage + "）"
                + " · 敌人 HP " + snapshot.EnemyHp + "/" + snapshot.EnemyMaxHp
                + " · " + FormatBattleState(snapshot);
        }

        /// <summary>战斗状态文案：战斗中标出本场 tick 数，否则如实报告"不在战斗"。</summary>
        /// <param name="snapshot">处理器返回的挂机 RPG 快照。</param>
        /// <returns>状态行末段文本。</returns>
        private static string FormatBattleState(IdleRpgSnapshot snapshot)
        {
            return snapshot.InBattle ? "战斗中 tick " + snapshot.BattleTick : "不在战斗";
        }

        private void HandleBattleTickClicked()
        {
            OnBattleTickClicked?.Invoke();
        }

        private void HandleLevelUpClicked()
        {
            OnLevelUpClicked?.Invoke();
        }

        private void HandleAdvanceStageClicked()
        {
            OnAdvanceStageClicked?.Invoke();
        }

        private void HandleRestartClicked()
        {
            OnRestartClicked?.Invoke();
        }
    }
}
