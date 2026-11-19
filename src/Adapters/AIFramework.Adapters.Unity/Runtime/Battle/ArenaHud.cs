#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 竞技场的 uGUI 皮肤：一行状态文本 + 一个重开按钮。
    /// <para>
    /// <b>为什么状态全部从只读视图现算</b>：皮肤不持有任何战斗状态，只把
    /// <see cref="IUnitWorldReadView"/> / <see cref="IProjectileWorldReadView"/> 里的当前值拼成一句话。
    /// 一旦皮肤自己缓存一份"主角还剩多少血"，两份状态就会不同步，玩家会看到"人已经死了血还是满的"。
    /// </para>
    /// <para>
    /// <b>为什么主角靠标签而不是下标认</b>：视图里没有阵营字段，"0 号单位是主角"是内核实现细节；
    /// 带 <see cref="GameplayTags.UnitShieldActive"/> 的单位就是主角，这是契约级判定。
    /// </para>
    /// <para>
    /// <b>为什么只在文本变化时写 <c>Text.text</c></b>：每帧无脑赋值会让 uGUI 每帧重建一次网格，
    /// 白白吃 CPU；状态行的内容只在数值变化时才需要变。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArenaHud : MonoBehaviour
    {
        /// <summary>重开按钮的固定文案（只用于提示语，不参与逻辑）。</summary>
        private const string RestartCaption = "重开";

        /// <summary>玩家点了"重开"：能不能重开、重开后从第几波开始由控制器决定（皮肤不做规则判断）。</summary>
        public event Action? OnRestartClicked;

        /// <summary>状态行：波次 / 主角 HP / 存活敌人 / 反射次数 / 来袭飞行物。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>重开按钮：只有一局收场后才可点（打一半重开会让结算报告对不上）。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>最近一次拼出的状态行（结算时在它后面追加胜负，而不是重新拼一遍）。</summary>
        private string _status = string.Empty;

        /// <summary>挂上重开按钮回调并把它锁成不可点（未开局时没有可重开的东西）。</summary>
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
        /// 按当前战场刷新状态行，并把重开按钮锁住（"还在打"语义）。
        /// 控制器每帧推一次；结束后改由 <see cref="ShowResult"/> 接手。
        /// </summary>
        /// <param name="controller">竞技场控制器（波次与累计反射次数从它读）。</param>
        /// <param name="units">单位只读视图；内核未实现该接口时为 <c>null</c>（此时显示占位符）。</param>
        /// <param name="projectiles">飞行物只读视图；同上，可为 <c>null</c>。</param>
        public void Render(
            ArenaController controller,
            IUnitWorldReadView? units,
            IProjectileWorldReadView? projectiles)
        {
            if (controller == null)
            {
                SetStatus("竞技场控制器缺失：没有可显示的对局");
                return;
            }

            int protagonistHp = ReadProtagonistHp(units, out int aliveEnemies);
            int incoming = CountIncomingProjectiles(projectiles);

            _status = "波次 " + controller.CurrentWave + "/" + controller.TotalWaves
                + " · 主角 HP " + (protagonistHp >= 0 ? protagonistHp.ToString() : "--")
                + " · 存活敌人 " + aliveEnemies
                + " · 反射 " + controller.ReflectedCount
                + " · 来袭 " + incoming;

            SetStatus(_status);

            if (restartButton != null)
            {
                restartButton.interactable = false;
            }
        }

        /// <summary>
        /// 显示结算：在最后一行状态后面追加"胜利/失败"，并放开重开按钮。
        /// <b>刻意保留上一行状态</b>：玩家需要看到"打到最后是什么状况"（波次/血量/反射），
        /// 而不只是一个孤零零的结论。
        /// </summary>
        /// <param name="isVictory">本场是否胜利（与 <c>GameplayResult.IsVictory</c> 一致）。</param>
        public void ShowResult(bool isVictory)
        {
            string prefix = _status.Length == 0 ? "竞技场结束" : _status;
            SetStatus(prefix + " · " + (isVictory ? "胜利" : "失败") + "（点" + RestartCaption + "再来一场）");

            if (restartButton != null)
            {
                restartButton.interactable = true;
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

        /// <summary>
        /// 从单位视图里取主角 HP 与存活敌人数。主角按护盾标签识别（契约判定），
        /// 找不到主角时返回 -1，让状态行显示 <c>--</c> 而不是假装还有血。
        /// </summary>
        /// <param name="units">单位只读视图，可为 <c>null</c>。</param>
        /// <param name="aliveEnemies">输出：HP &gt; 0 的敌人数。</param>
        /// <returns>主角当前 HP；不可读时为 -1。</returns>
        private static int ReadProtagonistHp(IUnitWorldReadView? units, out int aliveEnemies)
        {
            aliveEnemies = 0;
            if (units == null)
            {
                return -1;
            }

            int protagonistHp = -1;
            int count = units.Count;
            for (int i = 0; i < count; i++)
            {
                UnitReadView view = units[i];
                if (view.Tags.Has(GameplayTags.UnitShieldActive))
                {
                    if (protagonistHp < 0)
                    {
                        protagonistHp = view.Hp;
                    }

                    continue;
                }

                if (view.Hp > 0)
                {
                    aliveEnemies++;
                }
            }

            return protagonistHp;
        }

        /// <summary>数还在飞来的飞行物（未带反射标签的那些）；已被反射回去的不算"来袭"。</summary>
        /// <param name="projectiles">飞行物只读视图，可为 <c>null</c>。</param>
        /// <returns>来袭飞行物数量。</returns>
        private static int CountIncomingProjectiles(IProjectileWorldReadView? projectiles)
        {
            if (projectiles == null)
            {
                return 0;
            }

            int incoming = 0;
            int count = projectiles.Count;
            for (int i = 0; i < count; i++)
            {
                if (!projectiles[i].Tags.Has(GameplayTags.ProjReflected))
                {
                    incoming++;
                }
            }

            return incoming;
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
