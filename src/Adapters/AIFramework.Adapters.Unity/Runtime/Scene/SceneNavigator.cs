#nullable enable
using System;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Scene
{
    /// <summary>
    /// 场景跳转按钮：把"点这个按钮换场景"这件小事收成一个可复用组件。
    /// <para>
    /// <b>为什么不让每个场景各写一遍</b>：场景切换要处理"同场景不重载""场景不在 Build Settings 时不要崩"，
    /// 这些细节散落在多个场景脚本里必然走样；统一走 <see cref="UnitySceneDirector"/> 才有一致的日志与兜底。
    /// </para>
    /// <para>
    /// 纪律：跳转前不需要手动 Dispose 宿主——<c>LoadScene</c> 是同步的，旧场景对象（含宿主）先被销毁，
    /// 各场景的 <c>OnDestroy</c> 会自然执行 Dispose。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SceneNavigator : MonoBehaviour
    {
        /// <summary>目标场景名（必须在 Build Settings 里）。</summary>
        [SerializeField] private string targetScene = string.Empty;

        /// <summary>触发跳转的按钮。</summary>
        [SerializeField] private Button button = null!;

        private UnitySceneDirector? _director;

        /// <summary>目标场景名（可在运行时改，便于一个按钮跳不同场景）。</summary>
        public string TargetScene
        {
            get { return targetScene; }
            set { targetScene = value; }
        }

        private void Start()
        {
            if (button != null)
            {
                button.onClick.AddListener(Load);
            }
        }

        private void OnDestroy()
        {
            if (button != null)
            {
                button.onClick.RemoveListener(Load);
            }
        }

        /// <summary>跳转；失败只记日志，不抛异常（场景配置问题不该让玩家崩）。</summary>
        public void Load()
        {
            if (string.IsNullOrWhiteSpace(targetScene))
            {
                Debug.LogError("[AIFramework/Nav] 未配置目标场景名。");
                return;
            }

            _director ??= new UnitySceneDirector(new UnityLogger("[AIFramework/Nav] "));
            if (!_director.Load(targetScene))
            {
                Debug.LogError("[AIFramework/Nav] 场景加载失败：" + targetScene
                    + "（请确认 AIFramework/把场景加入 Build Settings 已执行）");
            }
        }
    }
}
