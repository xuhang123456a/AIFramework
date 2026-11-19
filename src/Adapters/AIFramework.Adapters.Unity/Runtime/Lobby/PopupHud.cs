#nullable enable
using System;
using AIFramework.Contracts.Ui;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 弹窗队列的 Unity 表现层：把 <see cref="IPopupQueue"/> 的"排队/出队"变成可见的演示。
    /// <para>
    /// <b>为什么做成独立 HUD 而不是塞进 LobbyController</b>：弹窗栈属于 UI 框架能力，
    /// 与大厅业务无关；这样任何场景挂上它 + 传一个队列端口就能演示，也便于后续换成带动效的真弹窗栈。
    /// </para>
    /// </summary>
    public sealed class PopupHud : MonoBehaviour
    {
        [SerializeField] private Text statusText = null!;
        [SerializeField] private Text topText = null!;
        [SerializeField] private Button pushButton = null!;
        [SerializeField] private Button closeButton = null!;

        private IPopupQueue? _queue;
        private int _lastShown = -1;

        /// <summary>队列里当前的弹窗数（调试/测试用）。</summary>
        public int QueueCount
        {
            get { return _queue == null ? 0 : _queue.Count; }
        }

        /// <summary>最近一次关闭的弹窗 id（调试/测试用；未关闭过为 -1）。</summary>
        public int LastShown
        {
            get { return _lastShown; }
        }

        /// <summary>绑定队列端口（由 LobbyController 在装配完成后调用）。</summary>
        /// <param name="queue">弹窗队列。</param>
        public void Bind(IPopupQueue queue)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));

            if (pushButton != null)
            {
                pushButton.onClick.AddListener(PushThree);
            }

            if (closeButton != null)
            {
                closeButton.onClick.AddListener(CloseTop);
            }

            Render();
        }

        /// <summary>演示用：一次推三个不同优先级的弹窗（模拟"活动/公告/引导"同时到达）。</summary>
        public void PushThree()
        {
            IPopupQueue? queue = _queue;
            if (queue == null)
            {
                return;
            }

            queue.Enqueue(101, 10);
            queue.Enqueue(102, 30);
            queue.Enqueue(103, 20);
            Render();
        }

        /// <summary>关掉当前展示的弹窗（出队下一个）。</summary>
        public void CloseTop()
        {
            IPopupQueue? queue = _queue;
            if (queue == null)
            {
                return;
            }

            if (queue.TryDequeue(out int popupId))
            {
                _lastShown = popupId;
            }

            Render();
        }

        private void OnDestroy()
        {
            if (pushButton != null)
            {
                pushButton.onClick.RemoveListener(PushThree);
            }

            if (closeButton != null)
            {
                closeButton.onClick.RemoveListener(CloseTop);
            }
        }

        private void Render()
        {
            IPopupQueue? queue = _queue;
            if (queue == null)
            {
                return;
            }

            if (statusText != null)
            {
                statusText.text = "弹窗队列：" + queue.Count + " 个"
                    + (queue.Contains(101) ? " [101]" : string.Empty)
                    + (queue.Contains(102) ? " [102]" : string.Empty)
                    + (queue.Contains(103) ? " [103]" : string.Empty);
            }

            if (topText != null)
            {
                topText.text = _lastShown < 0 ? "最近关闭：无" : "最近关闭：#" + _lastShown;
            }
        }
    }
}
