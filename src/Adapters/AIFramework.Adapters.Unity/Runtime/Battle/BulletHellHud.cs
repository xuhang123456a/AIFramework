#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 弹幕射击场景的 uGUI 皮肤：一行状态文本 + 一个重开按钮。
    /// <para>
    /// <b>为什么状态全部由控制器传值而不是自己读视图</b>：这里是"压力演示"，每帧都有上百颗弹幕在飞，
    /// 皮肤再去遍历 <c>IProjectileWorldReadView</c>（<c>this[int]</c> 是线性扫描的稠密下标访问）会白烧 CPU；
    /// 控制器本来就要每帧读一次快照，顺手把三个数字传下来最省。
    /// </para>
    /// <para>
    /// <b>为什么只在文本变化时写 <c>Text.text</c></b>：每帧无脑赋值会让 uGUI 每帧重建一次网格。
    /// </para>
    /// <para>
    /// <b>胜负从哪来</b>：<see cref="Render"/> 只拿到 <c>playerHp</c> 与 <c>isFinished</c>，
    /// 这正好够用——本演示的胜负判据与内核一致："收场时玩家还活着 = 生存成功"。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BulletHellHud : MonoBehaviour
    {
        /// <summary>战斗中状态词。</summary>
        private const string StateFighting = "弹幕中";

        /// <summary>收场且玩家存活的状态词。</summary>
        private const string StateSurvived = "生存成功";

        /// <summary>收场且玩家阵亡的状态词。</summary>
        private const string StateDefeated = "阵亡";

        /// <summary>重开按钮的固定文案（只用于提示语，不参与逻辑）。</summary>
        private const string RestartCaption = "重开";

        /// <summary>玩家点了"重开"：重开语义由控制器决定（皮肤不做规则判断）。</summary>
        public event Action? OnRestartClicked;

        /// <summary>状态行：HP / 存活弹幕 / 帧数 / 状态。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>重开按钮：只有一局收场后才可点（打一半重开会让"演示失败"这条路径没法观察）。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>挂上重开按钮回调，并把它锁成不可点（还没开局时没有可重开的东西）。</summary>
        private void Awake()
        {
            if (restartButton != null)
            {
                restartButton.onClick.AddListener(HandleRestartClicked);
                restartButton.interactable = false;
            }
        }

        /// <summary>退订按钮回调（场景卸载时调用，避免监听器留住已销毁的组件）。</summary>
        private void OnDestroy()
        {
            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(HandleRestartClicked);
            }
        }

        /// <summary>
        /// 刷新状态行，并按对局状态开关重开按钮。
        /// 控制器每帧推一次（收场那一帧也会推一次，此时 <paramref name="isFinished"/> 为 <c>true</c>）。
        /// </summary>
        /// <param name="playerHp">玩家当前 HP（0 表示阵亡）。</param>
        /// <param name="aliveProjectiles">内核里当前存活的飞行物数量。</param>
        /// <param name="frameCount">已推进的逻辑帧数。</param>
        /// <param name="isFinished">本局是否已结束。</param>
        public void Render(int playerHp, int aliveProjectiles, int frameCount, bool isFinished)
        {
            string state = StateFighting;
            if (isFinished)
            {
                state = playerHp > 0 ? StateSurvived : StateDefeated;
            }

            string line = "HP " + playerHp
                + " · 存活弹幕 " + aliveProjectiles
                + " · 帧 " + frameCount
                + " · " + state;

            if (isFinished)
            {
                line += "（点" + RestartCaption + "再来一局）";
            }

            SetStatus(line);

            if (restartButton != null)
            {
                restartButton.interactable = isFinished;
            }
        }

        /// <summary>把"点了重开"翻译成视图事件；重开语义由控制器决定。</summary>
        private void HandleRestartClicked()
        {
            Action? handler = OnRestartClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>写状态行（文本框未注入时静默跳过；内容没变就不写，避免每帧重建 UI 网格）。</summary>
        /// <param name="message">要显示的文本。</param>
        private void SetStatus(string message)
        {
            if (statusText == null)
            {
                return;
            }

            if (statusText.text == message)
            {
                return;
            }

            statusText.text = message;
        }
    }
}
