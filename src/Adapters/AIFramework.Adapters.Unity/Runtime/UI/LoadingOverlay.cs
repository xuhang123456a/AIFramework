#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Ui
{
    /// <summary>
    /// 加载遮罩（Loading）：整块半透明底板 + 一行说明文字，显示/隐藏<u>只切 <c>root</c> 的激活状态</u>。
    /// <para>
    /// <b>为什么不销毁/重建</b>：加载遮罩是全屏 UI，创建与销毁都会带来一次 Canvas 重批与一批
    /// <c>Graphic</c> 重建；游戏里切场景、拉配置、等支付回调都靠它，反复重建会直接变成卡顿尖峰。
    /// 组件常驻，只切换底板物体的激活状态。
    /// </para>
    /// <para>
    /// <b>为什么还没点进去就先隐藏</b>：底板在编辑器里被保存成 active，Unity 在场景加载时就会渲染一帧
    /// "全屏黑"，玩家看到的是闪一下。<see cref="Awake"/> 里先关掉底板，等于把"第一帧不该亮"固化进代码，
    /// 不依赖每次生成场景时有没有记得手动关。
    /// </para>
    /// <para>
    /// <b>不每帧重建字符串</b>：文案只在 <see cref="Show"/> 时赋值一次（见 <see cref="ToastHud"/> 同一套理由）。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LoadingOverlay : MonoBehaviour
    {
        /// <summary>底板根节点（全屏半透明底 + 文本都在它下面）；显示/隐藏只切它。</summary>
        [SerializeField] private GameObject root = null!;

        /// <summary>说明文字控件（可为空：只有底板没有文案也是一种合法表现）。</summary>
        [SerializeField] private Text labelText = null!;

        /// <summary>底板根节点（只读）。验收断言用；业务代码请走 <see cref="Show"/> / <see cref="Hide"/>。</summary>
        public GameObject Root
        {
            get { return root; }
        }

        /// <summary>当前是否可见（等价于底板被激活）。</summary>
        public bool IsVisible
        {
            get { return root != null && root.activeSelf; }
        }

        /// <summary>显示遮罩并把文案换成 <paramref name="label"/>。</summary>
        /// <param name="label">文案；<c>null</c> 按空串处理。</param>
        public void Show(string label)
        {
            Text? text = labelText;
            if (text != null)
            {
                text.text = label ?? string.Empty;
            }

            GameObject? panel = root;
            if (panel != null && !panel.activeSelf)
            {
                panel.SetActive(true);
            }
        }

        /// <summary>隐藏遮罩（幂等）。文案刻意<u>不</u>清空：下次 Show 不传文案时也看不出上一次的残影，
        /// 因为底板整体被关掉了，保留文字有利于排查"卡在哪个阶段"。</summary>
        public void Hide()
        {
            GameObject? panel = root;
            if (panel != null && panel.activeSelf)
            {
                panel.SetActive(false);
            }
        }

        private void Awake()
        {
            GameObject? panel = root;
            if (panel != null && panel.activeSelf)
            {
                panel.SetActive(false);
            }
        }
    }
}
