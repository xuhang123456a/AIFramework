#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Features.Guide;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 新手引导高亮条：把当前引导步显示在大厅，并在目标达成时高亮"去完成"。
    /// <para>
    /// <b>它解决的问题</b>：引导内核只负责"现在第几步、该做什么"，但玩家在界面上看不到这条信息——
    /// 没有高亮条时，新手引导等于只有数据没有表现。这里订阅 <see cref="GuideStepChangedEvent"/>，
    /// 把步骤标题与提示渲染出来，并在"条件已满足、等待玩家确认"时点亮高亮（<see cref="Reached"/>）。
    /// </para>
    /// <para>
    /// <b>为什么只在事件里刷新、不每帧查询</b>：引导是低频状态（一步几秒到几十秒），每帧查一次 CQE
    /// 既浪费也会让"刷新时机"变得不可解释；事件驱动后，刷新与步骤变化严格一一对应。
    /// </para>
    /// </summary>
    public sealed class GuideHighlightHud : MonoBehaviour
    {
        [SerializeField] private Text titleText = null!;
        [SerializeField] private Text hintText = null!;
        [SerializeField] private Image highlightBadge = null!;

        private AIFrameworkUnityHost? _host;
        private GuideObserver? _observer;
        private bool _completed;

        /// <summary>最近一次渲染的步骤号（0 = 引导未激活/已完成）。</summary>
        public int StepId { get; private set; }

        /// <summary>高亮是否点亮（"条件已达成、可以去点确认"）。</summary>
        public bool HighlightVisible
        {
            get { return highlightBadge != null && highlightBadge.gameObject.activeSelf; }
        }

        /// <summary>最近一次渲染的标题（测试断言用）。</summary>
        public string LastTitle { get; private set; } = string.Empty;

        /// <summary>绑定宿主并订阅引导事件；宿主没装配引导时降级为空显示，不抛。</summary>
        /// <param name="host">已初始化的宿主。</param>
        public void Bind(AIFrameworkUnityHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            _host = host;

            if (!host.GuideAvailable)
            {
                SetText("引导：表缺失（TbGuideStep.bin）", string.Empty);
                SetBadge(false);
                return;
            }

            if (_observer == null)
            {
                _observer = new GuideObserver(this);
                host.Bus.Subscribe<GuideStepChangedEvent>(_observer);
                host.Bus.Subscribe<GuideCompletedEvent>(_observer);
            }

            Render();
        }

        /// <summary>玩家点"完成"：把当前步显式上报给内核（"看到了"不等于"做到了"）。</summary>
        public void ConfirmCurrentStep()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || !host.GuideAvailable || _completed || StepId <= 0)
            {
                return;
            }

            var command = new GuideAdvanceCommand(StepId);
            host.Bus.Send(in command);
        }

        private void OnDestroy()
        {
            _observer = null;
            _host = null;
        }

        private void Render()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || !host.GuideAvailable)
            {
                return;
            }

            var request = new GuideQueryRequest();
            GuideSnapshot snapshot = host.Bus.Query<GuideQueryRequest, GuideSnapshot>(in request);

            _completed = snapshot.IsCompleted || snapshot.IsSkippedAll;
            StepId = _completed ? 0 : snapshot.StepId;

            if (_completed)
            {
                SetText("引导完成", "已完成 " + snapshot.CompletedCount + " 步");
                SetBadge(false);
                return;
            }

            SetText(
                "引导 " + snapshot.StepId + "/" + snapshot.TotalCount + "：" + snapshot.Title,
                snapshot.Reached ? "条件已达成，点「完成」继续" : snapshot.Hint);
            SetBadge(snapshot.Reached);
        }

        private void SetText(string title, string hint)
        {
            LastTitle = title;

            if (titleText != null)
            {
                titleText.text = title;
            }

            if (hintText != null)
            {
                hintText.text = hint;
            }
        }

        private void SetBadge(bool visible)
        {
            if (highlightBadge == null)
            {
                return;
            }

            highlightBadge.gameObject.SetActive(visible);
        }

        /// <summary>引导事件订阅者：步骤变化与完成各触发一次重渲染。</summary>
        private sealed class GuideObserver : IEventHandler<GuideStepChangedEvent>, IEventHandler<GuideCompletedEvent>
        {
            private readonly GuideHighlightHud _owner;

            public GuideObserver(GuideHighlightHud owner)
            {
                _owner = owner;
            }

            public void Handle(in GuideStepChangedEvent evt)
            {
                _owner.Render();
            }

            public void Handle(in GuideCompletedEvent evt)
            {
                _owner.Render();
            }
        }
    }
}
