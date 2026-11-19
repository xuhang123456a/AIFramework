#nullable enable
using System;
using System.Text;
using AIFramework.Features.Merge;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 合成面板的 uGUI 皮肤：状态行（金币 / 已用格 / 合成次数）+ 6×6 字符盘 + 生成 / 合成 / 重开三颗按钮。
    /// <para>
    /// 只做两件事：把 <see cref="MergeGridSnapshot"/> 画成字符盘、把点击意图抛回事件。
    /// 能不能生成（有没有空格）、能不能合成（相邻同等级且未满级）全部由 <c>MergeHandler</c> 判定并静默拒绝，
    /// 本类<b>不</b>据此禁用按钮——"该不该禁用"是第二套业务规则，面板只负责"点完让控制器刷新"。
    /// </para>
    /// <para>
    /// <b>为什么棋盘用一段文本而不是 36 个格子控件</b>：合成演示要验证的是切片规则（相邻 / 同等级 / 等级 +1 / 金币），
    /// 不是格子美术；一段等宽字符盘能让"哪格变成了几级"在一屏内看全，也不用 36 份场景引用注入。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MergeHud : MonoBehaviour
    {
        /// <summary>空格字符：等级 0 不占用子表任何一行，必须与"等级 1"在视觉上区分开。</summary>
        private const char EmptyCellChar = '·';

        /// <summary>同一行内相邻两格的分隔符（单空格，保证 6 行、每行 11 个字符）。</summary>
        private const char CellSeparator = ' ';

        /// <summary>单位数等级的上限：再往上就用多位数字，宁可撑宽一格也不画错等级。</summary>
        private const int MaxSingleDigitLevel = 9;

        /// <summary>状态行：金币 / 已用格 / 合成次数。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>6×6 字符盘：等级数字，空用 <c>·</c>。</summary>
        [SerializeField] private Text gridText = null!;

        /// <summary>生成一件 1 级物品（落在第一个空格）。</summary>
        [SerializeField] private Button spawnButton = null!;

        /// <summary>合成第一对相邻同等级格。</summary>
        [SerializeField] private Button mergeButton = null!;

        /// <summary>重新开始（控制器重建宿主与切片，回到空棋盘）。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>
        /// 最近一次渲染的金币数。<b>为什么留一份</b>：面板约定只有 <see cref="Render(MergeGridSnapshot, int)"/>
        /// 一个渲染入口，而金币存在背包账本里、不在棋盘快照里，所以两参重载沿用上一次的金币，
        /// 由控制器用三参重载把最新金币一起传进来。
        /// </summary>
        private int _gold;

        /// <summary>点"生成"。</summary>
        public event Action? OnSpawnClicked;

        /// <summary>点"合成"。</summary>
        public event Action? OnMergeClicked;

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        private void Start()
        {
            if (spawnButton != null)
            {
                spawnButton.onClick.AddListener(HandleSpawnClicked);
            }

            if (mergeButton != null)
            {
                mergeButton.onClick.AddListener(HandleMergeClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(HandleRestartClicked);
            }
        }

        private void OnDestroy()
        {
            // 与 Start 严格配对：场景重载后残留监听会让一次点击触发两次命令。
            if (spawnButton != null)
            {
                spawnButton.onClick.RemoveListener(HandleSpawnClicked);
            }

            if (mergeButton != null)
            {
                mergeButton.onClick.RemoveListener(HandleMergeClicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(HandleRestartClicked);
            }
        }

        /// <summary>按棋盘快照重画状态行与字符盘；金币沿用上一次渲染的值。</summary>
        /// <param name="snapshot">解码后的棋盘快照（行优先，0 = 空）。</param>
        /// <param name="mergeCount">处理器累计的成功合成次数。</param>
        public void Render(MergeGridSnapshot snapshot, int mergeCount)
        {
            Render(snapshot, mergeCount, _gold);
        }

        /// <summary>
        /// 按棋盘快照重画状态行与字符盘，并记下当前金币。
        /// <b>为什么不在这里算任何数值</b>：已用格、合成次数都来自处理器，金币来自背包账本，
        /// 皮肤只做字符串拼接，多算一次就是第二套公式。
        /// </summary>
        /// <param name="snapshot">解码后的棋盘快照（行优先，0 = 空）。</param>
        /// <param name="mergeCount">处理器累计的成功合成次数。</param>
        /// <param name="gold">当前金币（装配层查背包得到）。</param>
        public void Render(MergeGridSnapshot snapshot, int mergeCount, int gold)
        {
            _gold = gold;

            if (statusText != null)
            {
                statusText.text = "金币 " + gold
                    + " · 已用格 " + snapshot.UsedCellCount + "/" + (snapshot.Width * snapshot.Height)
                    + " · 合成 " + mergeCount + " 次";
            }

            if (gridText != null)
            {
                gridText.text = FormatGrid(snapshot);
            }
        }

        /// <summary>
        /// 字符盘：行优先逐行拼接，等级 0 画成 <c>·</c>，其余画成等级数字。
        /// 越界坐标（<c>LevelAt</c> 返回 -1）在这里走不到——行列都由快照自身的 Width/Height 驱动。
        /// </summary>
        /// <param name="snapshot">棋盘快照。</param>
        /// <returns>可直接赋给 <see cref="Text.text"/> 的多行文本。</returns>
        private static string FormatGrid(MergeGridSnapshot snapshot)
        {
            var builder = new StringBuilder((snapshot.Width * 2) * snapshot.Height);
            for (int y = 0; y < snapshot.Height; y++)
            {
                if (y > 0)
                {
                    builder.Append('\n');
                }

                for (int x = 0; x < snapshot.Width; x++)
                {
                    if (x > 0)
                    {
                        builder.Append(CellSeparator);
                    }

                    int level = snapshot.LevelAt(x, y);
                    if (level <= 0)
                    {
                        builder.Append(EmptyCellChar);
                    }
                    else if (level <= MaxSingleDigitLevel)
                    {
                        builder.Append((char)('0' + level));
                    }
                    else
                    {
                        // 两位以上等级：文字上没错，只是那一行会宽一格（演示表最高 5 级）。
                        builder.Append(level);
                    }
                }
            }

            return builder.ToString();
        }

        private void HandleSpawnClicked()
        {
            OnSpawnClicked?.Invoke();
        }

        private void HandleMergeClicked()
        {
            OnMergeClicked?.Invoke();
        }

        private void HandleRestartClicked()
        {
            OnRestartClicked?.Invoke();
        }
    }
}
