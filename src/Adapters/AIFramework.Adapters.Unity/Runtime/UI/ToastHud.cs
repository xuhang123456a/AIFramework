#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Ui
{
    /// <summary>
    /// 轻提示（Toast）：一条会自动消失的文字，<b>自己就是自己的显示根节点</b>
    /// （显示 → <c>SetActive(true)</c>，到期 → 清文本 + <c>SetActive(false)</c>）。
    /// <para>
    /// <b>为什么不用协程</b>：协程在 <c>SetActive(false)</c> 时会被连同物体一起停掉，
    /// "文字还没到点就被关掉"这种 bug 只会出现在真机上；<see cref="Update"/> 里减一个 float 是零分配的，
    /// 停止条件与显示状态严格一一对应（见 AGENTS.md 三·4 禁用协程的纪律）。
    /// </para>
    /// <para>
    /// <b>为什么不在 Update 里拼字符串</b>：每帧 <c>text = ...</c> 会触发 UGUI 重建（Canvas 重批 + 网格重建），
    /// 一个常驻 Toast 能把整帧的 Canvas 批次打散。这里只在 <see cref="Show"/> 里赋值一次，
    /// 到期时清成 <c>string.Empty</c>（不销毁、不清引用，下次复用零分配）。
    /// </para>
    /// <para>
    /// <b>初始状态</b>：场景生成器会把这个物体存成 inactive，因此进入场景时不会有一条空底条挂在屏幕上。
    /// 调用 <see cref="Show"/> 会自行激活——即使调用方拿到的引用是在物体未激活时拿到的，也照样有效。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ToastHud : MonoBehaviour
    {
        /// <summary>文本控件（必须注入；为空时只计时不显示，绝不抛异常）。</summary>
        [SerializeField] private Text messageText = null!;

        /// <summary>默认显示时长（秒）；<see cref="Show"/> 的 <c>seconds</c> 传 0 时用它。</summary>
        [SerializeField] private float defaultSeconds = 2f;

        /// <summary>剩余显示时间（秒）；&gt; 0 即在显示中。</summary>
        private float _remainingSeconds;

        /// <summary>最近一次的文案（测试/调试用；清空后仍保留"刚才说过什么"）。</summary>
        private string _lastMessage = string.Empty;

        /// <summary>当前是否正在显示。</summary>
        /// <remarks>
        /// 同时看计时与物体激活状态：有人从外部把这个物体关掉时，返回值必须诚实地变 <c>false</c>，
        /// 否则"计时器还在走"会被误读成"玩家还看得到"。
        /// </remarks>
        public bool IsShowing
        {
            get { return _remainingSeconds > 0f && gameObject.activeSelf; }
        }

        /// <summary>最近一次的文案；从未显示过时为空串。</summary>
        public string LastMessage
        {
            get { return _lastMessage; }
        }

        /// <summary>显示一条轻提示（重复调用即"换文案并重新计时"）。</summary>
        /// <param name="message">文案；<c>null</c> 按空串处理。</param>
        /// <param name="seconds">显示时长（秒）；<c>&lt;= 0</c> 时用 <c>defaultSeconds</c>。</param>
        public void Show(string message, float seconds = 0f)
        {
            _lastMessage = message ?? string.Empty;

            Text? text = messageText;
            if (text != null)
            {
                text.text = _lastMessage;
            }

            float duration = seconds > 0f ? seconds : defaultSeconds;
            if (!(duration > 0f))
            {
                // 默认时长也被配成 0/负值：与其让文字永久钉在屏幕上，不如按"不显示"处理。
                Hide();
                return;
            }

            _remainingSeconds = duration;
            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }
        }

        private void Update()
        {
            if (_remainingSeconds <= 0f)
            {
                return;
            }

            // deltaTime 受 timeScale 影响是**刻意的**：暂停菜单里跳出的 toast 不应该继续倒计时。
            _remainingSeconds -= Time.deltaTime;
            if (_remainingSeconds <= 0f)
            {
                Hide();
            }
        }

        /// <summary>结束显示：清文本 + 关物体（幂等）。</summary>
        private void Hide()
        {
            _remainingSeconds = 0f;

            Text? text = messageText;
            if (text != null)
            {
                text.text = string.Empty;
            }

            if (gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }
    }
}
