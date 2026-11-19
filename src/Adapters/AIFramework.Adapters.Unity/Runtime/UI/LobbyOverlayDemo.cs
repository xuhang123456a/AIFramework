#nullable enable
using UnityEngine;

namespace AIFramework.Adapters.Unity.Ui
{
    /// <summary>
    /// 大厅的 Toast / Loading 演示器：进场景后自动演一遍"提示 2 秒 → 加载遮罩 1 秒"，
    /// 用来证明这两个 UI 组件真的被装配进场景、而不是只有几个没人调的 <c>MonoBehaviour</c>。
    /// <para>
    /// <b>为什么单独一个 MonoBehaviour 而不是塞进 <see cref="Lobby.LobbyController"/></b>：Toast 与 Loading
    /// 是 UI 框架能力，跟大厅业务（签到/商店/任务/邮件）没有关系；往 <c>LobbyController</c> 里加字段
    /// 会让每个接入 Toast 的场景都去改业务控制器，正是本项目要避免的"爆炸半径"。
    /// </para>
    /// <para>
    /// <b>为什么用 Update 里的状态机而不是协程</b>：同 <see cref="ToastHud"/> 的注释——协程会被
    /// <c>SetActive(false)</c> 静默打断，且异常难在无头测试里复现；这里只有"两个计时器 + 三个状态"，
    /// 用 Update 反而更短、更可预测（AGENTS.md 三·4）。
    /// </para>
    /// <para>
    /// <b>它不参与业务</b>：演示结束就停在 <see cref="Phase.Done"/>，不再碰任何 UI；
    /// 正式项目可以直接删掉本组件，或者换成"登录回调里 Show、返回后 Hide"的真实调用点。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LobbyOverlayDemo : MonoBehaviour
    {
        /// <summary>Toast 演示时长（秒）。</summary>
        public const float DemoToastSeconds = 2f;

        /// <summary>Loading 演示时长（秒）。</summary>
        public const float DemoLoadingSeconds = 1f;

        /// <summary>进场景时说的那句话。</summary>
        public const string DemoToastMessage = "已装载完成";

        /// <summary>加载遮罩上的文案。</summary>
        public const string DemoLoadingLabel = "加载中…";

        /// <summary>Toast 组件（场景生成器注入；未注入时 <c>Awake</c> 会在子节点里找）。</summary>
        [SerializeField] private ToastHud toastHud = null!;

        /// <summary>Loading 组件（同上）。</summary>
        [SerializeField] private LoadingOverlay loadingOverlay = null!;

        /// <summary>演示所处阶段。</summary>
        private Phase _phase = Phase.Idle;

        /// <summary>当前阶段已过去的秒数。</summary>
        private float _elapsedSeconds;

        /// <summary>演示阶段。<b>公开</b>是为了让 PlayMode 验收能直接断言"演示走到了哪一段"。</summary>
        public enum Phase
        {
            /// <summary>还没开始（等 <c>Start</c>）。</summary>
            Idle = 0,

            /// <summary>Toast 显示中。</summary>
            Toast = 1,

            /// <summary>Loading 显示中。</summary>
            Loading = 2,

            /// <summary>演完了，不再动 UI。</summary>
            Done = 3
        }

        /// <summary>当前阶段（调试/测试用）。</summary>
        public Phase CurrentPhase
        {
            get { return _phase; }
        }

        /// <summary>
        /// 找到两个 UI 组件。<b>为什么先看注入再看层级</b>：序列化注入是场景生成器的正常路径；
        /// 手搭场景/临时子节点时用层级查找兜底，省掉"忘了拖引用就整页不动"这类无声失败。
        /// 两者都没有时保持 <c>null</c>，<see cref="Start"/> 会安静地不演示。
        /// <para>查找走 <c>gameObject.GetComponentsInChildren(..., true)</c>：Toast 物体在场景里是
        /// inactive 的（避免进场景先闪空底条），默认不包含未激活对象的查找会漏掉它。</para>
        /// </summary>
        private void Awake()
        {
            ToastHud? toast = toastHud;
            if (toast == null)
            {
                ToastHud[] found = gameObject.GetComponentsInChildren<ToastHud>(true);
                if (found.Length > 0)
                {
                    toastHud = found[0];
                }
            }

            LoadingOverlay? loading = loadingOverlay;
            if (loading == null)
            {
                LoadingOverlay[] found = gameObject.GetComponentsInChildren<LoadingOverlay>(true);
                if (found.Length > 0)
                {
                    loadingOverlay = found[0];
                }
            }
        }

        private void Start()
        {
            ToastHud? toast = toastHud;
            if (toast == null)
            {
                _phase = Phase.Done;
                return;
            }

            toast.Show(DemoToastMessage, DemoToastSeconds);
            _phase = Phase.Toast;
            _elapsedSeconds = 0f;
        }

        private void Update()
        {
            if (_phase == Phase.Idle || _phase == Phase.Done)
            {
                return;
            }

            _elapsedSeconds += Time.deltaTime;

            if (_phase == Phase.Toast)
            {
                if (_elapsedSeconds < DemoToastSeconds)
                {
                    return;
                }

                // Toast 自己会在到期时关掉自己；这里只负责排下一段演出。
                LoadingOverlay? loading = loadingOverlay;
                if (loading == null)
                {
                    _phase = Phase.Done;
                    return;
                }

                loading.Show(DemoLoadingLabel);
                _phase = Phase.Loading;
                _elapsedSeconds = 0f;
                return;
            }

            if (_elapsedSeconds >= DemoLoadingSeconds)
            {
                LoadingOverlay? loading = loadingOverlay;
                if (loading != null)
                {
                    loading.Hide();
                }

                _phase = Phase.Done;
            }
        }
    }
}
