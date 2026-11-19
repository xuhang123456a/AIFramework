#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Meta;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 异步 PVP 面板的 uGUI 皮肤：状态行（段位 / 胜负 / 连胜 + 最近一场）+ 对手池文本 + 挑战 / 刷新 / 重开三颗按钮。
    /// <para>
    /// 只做两件事：把 <see cref="PvpProfileSnapshot"/> 与 <see cref="PvpOpponentList"/> 画到控件上、把点击意图抛回事件。
    /// 能不能打（快照非法 / 战力越界）、算多少分、掉不掉段全部由 <c>AsyncPvpHandler</c> 判定并静默拒绝，
    /// 本类<b>不</b>据此禁用按钮——"该不该禁用"是第二套业务规则，面板只负责"点完让控制器刷新"。
    /// </para>
    /// <para>
    /// <b>为什么对手池只列 id / 战力 / 种子</b>：这三项就是复算一场异步对局所需的全部输入
    /// （种子进得分公式、战力进得分公式、id 进结算结果），布局 id 在 Phase 1 只做展示、不参与算分，
    /// 列出来只会让面板多一行没人验证的信息。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AsyncPvpHud : MonoBehaviour
    {
        /// <summary>状态行：段位分 / 胜负 / 连胜（附最近一场结算）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>对手池文本（3 个离线对手，每行一个）。</summary>
        [SerializeField] private Text opponentText = null!;

        /// <summary>挑战对手池第一个。</summary>
        [SerializeField] private Button fightButton = null!;

        /// <summary>重新查一次对手池。</summary>
        [SerializeField] private Button refreshButton = null!;

        /// <summary>重新开始（控制器重建宿主与切片，段位回到初值）。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>点"挑战"。</summary>
        public event Action? OnFightClicked;

        /// <summary>点"刷新"。</summary>
        public event Action? OnRefreshClicked;

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        private void Start()
        {
            if (fightButton != null)
            {
                fightButton.onClick.AddListener(HandleFightClicked);
            }

            if (refreshButton != null)
            {
                refreshButton.onClick.AddListener(HandleRefreshClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(HandleRestartClicked);
            }
        }

        private void OnDestroy()
        {
            // 与 Start 严格配对：场景重载后残留监听会让一次点击触发两次命令。
            if (fightButton != null)
            {
                fightButton.onClick.RemoveListener(HandleFightClicked);
            }

            if (refreshButton != null)
            {
                refreshButton.onClick.RemoveListener(HandleRefreshClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(HandleRestartClicked);
            }
        }

        /// <summary>
        /// 按档案 / 对手池 / 最近一场结果重画两段文本。
        /// <b>为什么不在这里算任何数值</b>：段位、胜负、连胜、双方得分与段位变化都已经由处理器算好，
        /// 皮肤只做字符串拼接，多算一次就是第二套公式。
        /// </summary>
        /// <param name="profile">段位档案快照。</param>
        /// <param name="opponents">当前手上这份对手池（挑战作用于第 1 个）。</param>
        /// <param name="lastResult">最近一场结算结果；一次都没打过时为 <c>null</c>。</param>
        public void Render(PvpProfileSnapshot profile, PvpOpponentList opponents, AsyncDuelResult? lastResult)
        {
            if (statusText != null)
            {
                statusText.text = "段位 " + profile.Rating
                    + " · 胜 " + profile.Wins + " / 负 " + profile.Losses
                    + " · " + FormatStreak(profile.Streak)
                    + FormatLastResult(lastResult);
            }

            if (opponentText != null)
            {
                opponentText.text = FormatOpponents(opponents);
            }
        }

        /// <summary>连胜口径：正数连胜、负数连败、0 未打过（与 <c>AsyncPvpHandler.Streak</c> 的约定一致）。</summary>
        /// <param name="streak">处理器给出的连胜/连败值。</param>
        /// <returns>可直接拼进状态行的片段。</returns>
        private static string FormatStreak(int streak)
        {
            if (streak > 0)
            {
                return "连胜 " + streak;
            }

            if (streak < 0)
            {
                return "连败 " + (-streak);
            }

            return "未开打";
        }

        /// <summary>最近一场结算片段：胜负 + 双方得分 + 段位实际变化；没打过时返回空串。</summary>
        /// <param name="result">最近一场结果，可为 <c>null</c>。</param>
        /// <returns>形如 <c>" · 上场 胜 1050:1030（+20）"</c> 的片段，或空串。</returns>
        private static string FormatLastResult(AsyncDuelResult? result)
        {
            if (result == null)
            {
                return string.Empty;
            }

            AsyncDuelResult value = result.Value;
            return " · 上场 " + (value.AttackerWon ? "胜" : "负")
                + " " + value.AttackerScore + ":" + value.DefenderScore
                + "（" + FormatDelta(value.RatingDelta) + "）";
        }

        /// <summary>段位变化带符号显示：负号已经在数值里，不重复拼。</summary>
        private static string FormatDelta(int delta)
        {
            return delta >= 0 ? "+" + delta : delta.ToString();
        }

        /// <summary>对手池文案：首行标注挑战目标，其后每个对手一行。</summary>
        /// <param name="opponents">对手池快照。</param>
        /// <returns>可直接赋给 <see cref="Text.text"/> 的多行文本。</returns>
        private static string FormatOpponents(PvpOpponentList opponents)
        {
            DefenseSnapshot[] pool = opponents.Opponents;
            if (pool.Length == 0)
            {
                return "对手池为空：点『刷新』重新匹配（真实环境由服务端下发）";
            }

            var builder = new StringBuilder();
            builder.Append("对手池（挑战作用于第 1 个）");
            for (int i = 0; i < pool.Length; i++)
            {
                DefenseSnapshot opponent = pool[i];
                builder.Append('\n')
                    .Append(i + 1).Append(". id=").Append(opponent.PlayerId)
                    .Append(" · 战力 ").Append(opponent.Power)
                    .Append(" · 种子 ").Append(opponent.RandomSeed);
            }

            return builder.ToString();
        }

        private void HandleFightClicked()
        {
            OnFightClicked?.Invoke();
        }

        private void HandleRefreshClicked()
        {
            OnRefreshClicked?.Invoke();
        }

        private void HandleRestartClicked()
        {
            OnRestartClicked?.Invoke();
        }
    }
}
