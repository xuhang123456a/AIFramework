#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;
using AIFramework.Features.Localization;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Localization
{
    /// <summary>
    /// 可热刷新的本地化面板：面板实现本接口，切语言时由 <see cref="LocalizationHotSwitcher"/> 统一刷新。
    /// <para>
    /// <b>为什么要有这个接口</b>：切语言最容易被漏掉的是"已经打开的面板"——它们只在 <c>Awake</c> 里查过一次文案，
    /// 切语言后仍显示旧文本，直到玩家关掉重开。统一登记后，切语言只走一条链路，新面板实现接口即自动生效。
    /// </para>
    /// <para>
    /// <b>为什么刷新是同步的</b>：实现方应当只重查本面板的文案并重新赋值，不做加载；需要异步资源的（图标/语音）
    /// 由实现方自己决定策略。同步刷新保证"切语言命令返回时，画面已经是新语言"，便于截图与测试断言。
    /// </para>
    /// </summary>
    public interface ILocalizedPanel
    {
        /// <summary>重新查询并应用当前语言下的全部文案；实现方不得抛异常（见 <see cref="LocalizationHotSwitcher"/> 的隔离口径）。</summary>
        void RefreshLocalizedText();
    }

    /// <summary>
    /// 语言热切换器：订阅 <see cref="LanguageChangedEvent"/>，切语言时刷新所有已登记面板。
    /// <para>
    /// <b>为什么不在每个面板里各自订阅</b>：每个面板自带订阅会导致：①重复绑定/解绑容易泄漏；
    /// ②某个面板刷新抛异常会影响其它面板；③排查"哪个面板没刷新"要看 N 个组件。
    /// 收口到本类后，注册/激活开关/异常隔离只在这里维护一次。
    /// </para>
    /// <para>
    /// <b>异常隔离</b>：单个面板刷新失败只记错误，继续刷新其余面板，并计数（<see cref="FailureCount"/>），
    /// 保证"一个坏面板不会让整屏文案停在旧语言"。
    /// </para>
    /// <para>
    /// <b>为什么用 active 开关而不是退订</b>：<c>CqeBus</c> 目前只提供 <c>Subscribe</c>、没有 <c>Unsubscribe</c>；
    /// 切场景时宿主与会话一起销毁，事件表随宿主回收，所以这里用 <c>_active</c> 把旧实例变成空操作，
    /// 等总线补上退订能力后只需把 <see cref="Unbind"/> 里的一行换掉。
    /// </para>
    /// </summary>
    public sealed class LocalizationHotSwitcher : MonoBehaviour
    {
        /// <summary>
        /// Inspector 里显式登记的面板（实现 <see cref="ILocalizedPanel"/>）。
        /// <para>
        /// <b>为什么不加 <c>[SerializeField]</c></b>：同一 GameObject 上的面板已由 <see cref="Awake"/> 自动收集，
        /// 再要求场景里序列化一份数组只会带来"新增面板忘记拖进去"的维护事故，而场景装配校验也会把它当漏注入拦下。
        /// 需要跨物体登记时走 <see cref="Register"/>。
        /// </para>
        /// </summary>
        private MonoBehaviour[] panels = Array.Empty<MonoBehaviour>();

        private readonly List<ILocalizedPanel> _runtimeRegistered = new List<ILocalizedPanel>(4);
        private AIFrameworkUnityHost? _host;
        private LanguageChangeObserver? _observer;
        private bool _active;
        private int _refreshCount;
        private int _failureCount;
        private int _skippedCount;

        /// <summary>已刷新过的次数（每次语言变化 +1；测试断言"确实响应了切换"）。</summary>
        public int RefreshCount
        {
            get { return _refreshCount; }
        }

        /// <summary>刷新过程中抛异常的面板次数（异常被隔离，不影响其它面板）。</summary>
        public int FailureCount
        {
            get { return _failureCount; }
        }

        /// <summary>Inspector 里登记了但没有实现 <see cref="ILocalizedPanel"/> 的条目数（配置错误）。</summary>
        public int SkippedCount
        {
            get { return _skippedCount; }
        }

        /// <summary>当前是否处于激活监听状态。</summary>
        public bool IsActive
        {
            get { return _active; }
        }

        /// <summary>
        /// 自动登记同一 GameObject 上实现 <see cref="ILocalizedPanel"/> 的组件。
        /// <para>这省掉了"每加一个面板就要回编辑器拖一次数组"的维护成本，也让场景构建脚本只加组件、不连线。</para>
        /// </summary>
        private void Awake()
        {
            MonoBehaviour[] candidates = GetComponents<MonoBehaviour>();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] is ILocalizedPanel panel && !_runtimeRegistered.Contains(panel))
                {
                    _runtimeRegistered.Add(panel);
                }
            }
        }

        /// <summary>绑定宿主并订阅语言变化事件；重复绑定只更新宿主，不重复订阅。</summary>
        /// <param name="host">已初始化的宿主。</param>
        public void Bind(AIFrameworkUnityHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            _host = host;

            if (_observer == null)
            {
                _observer = new LanguageChangeObserver(this);
                host.Bus.Subscribe(_observer);
            }

            _active = true;
        }

        /// <summary>登记一个面板（可重复调用；null 忽略）。</summary>
        /// <param name="panel">面板实现。</param>
        public void Register(ILocalizedPanel? panel)
        {
            if (panel == null || _runtimeRegistered.Contains(panel))
            {
                return;
            }

            _runtimeRegistered.Add(panel);
        }

        /// <summary>立即刷新全部已登记面板（不改变语言，仅重查文案；切场景后手工补偿时用）。</summary>
        public void RefreshAll()
        {
            _refreshCount++;

            ILocalizedPanel[] registered = CollectPanels();
            for (int i = 0; i < registered.Length; i++)
            {
                try
                {
                    registered[i].RefreshLocalizedText();
                }
                catch (Exception exception)
                {
                    // 表现层失败不能挡住其它面板、更不能冒泡到总线（否则切换语言的调用方会收到异常）。
                    _failureCount++;
                    AIFrameworkUnityHost? host = _host;
                    if (host != null)
                    {
                        host.Log.Error("[Localization] 面板刷新失败：" + exception.GetType().Name + "：" + exception.Message);
                    }
                }
            }
        }

        /// <summary>停止响应语言变化（切场景/销毁时调用；可重复调用）。</summary>
        public void Unbind()
        {
            _active = false;
            _host = null;
        }

        private void OnDestroy()
        {
            Unbind();
        }

        private ILocalizedPanel[] CollectPanels()
        {
            var result = new List<ILocalizedPanel>(panels.Length + _runtimeRegistered.Count);
            for (int i = 0; i < panels.Length; i++)
            {
                MonoBehaviour? candidate = panels[i];
                if (candidate == null)
                {
                    continue;
                }

                if (candidate is ILocalizedPanel panel)
                {
                    result.Add(panel);
                }
                else
                {
                    _skippedCount++;
                }
            }

            result.AddRange(_runtimeRegistered);
            return result.ToArray();
        }

        /// <summary>语言变化事件订阅者：把事件转成本类的一次刷新（单独类型便于将来退订）。</summary>
        private sealed class LanguageChangeObserver : IEventHandler<LanguageChangedEvent>
        {
            private readonly LocalizationHotSwitcher _owner;

            public LanguageChangeObserver(LocalizationHotSwitcher owner)
            {
                _owner = owner;
            }

            public void Handle(in LanguageChangedEvent evt)
            {
                if (_owner._active)
                {
                    _owner.RefreshAll();
                }
            }
        }
    }
}
