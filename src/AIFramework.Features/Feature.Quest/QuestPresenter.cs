using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Quest
{
    /// <summary>
    /// 纯逻辑 UI 大脑：把 <see cref="QuestListSnapshot"/> 翻译成视图可直出的
    /// <see cref="QuestItemViewData"/> 列表（<see cref="QuestItemViewData.Claimable"/> 在这里算好），
    /// 并把视图点击转发成 <see cref="ClaimQuestRewardCommand"/>。
    /// <para>
    /// 这里不算奖励、不改任务状态、不碰背包：合法性与防重永远只由 <see cref="QuestHandler"/> 裁决，
    /// 未知 / 未完成 / 已领取的点击一律转发后静默刷新，Presenter 不做二次判断。
    /// </para>
    /// </summary>
    public sealed class QuestPresenter
    {
        private readonly ICqeBus _bus;
        private readonly IQuestView _view;

        /// <summary>装配 Presenter 并接管视图的领取点击。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="bus"/> 或 <paramref name="view"/> 为 <c>null</c>。</exception>
        public QuestPresenter(ICqeBus bus, IQuestView view)
        {
            if (bus == null)
            {
                throw new ArgumentNullException(nameof(bus));
            }

            if (view == null)
            {
                throw new ArgumentNullException(nameof(view));
            }

            _bus = bus;
            _view = view;
            _view.OnClaimClicked += HandleClaimClicked;
        }

        /// <summary>
        /// 查询任务列表并按 <c>!Claimed &amp;&amp; Progress &gt;= TargetCount</c> 计算可领标记后交给视图。
        /// 每次允许分配（非帧内热路径）。
        /// </summary>
        public void Refresh()
        {
            QuestListSnapshot snapshot = _bus.Query<QuestListQueryRequest, QuestListSnapshot>(new QuestListQueryRequest());
            IReadOnlyList<QuestViewData> source = snapshot.Quests;

            List<QuestItemViewData> quests = new List<QuestItemViewData>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                QuestViewData quest = source[i];
                bool claimable = !quest.Claimed && quest.Progress >= quest.TargetCount;
                quests.Add(new QuestItemViewData(
                    quest.QuestId,
                    quest.Progress,
                    quest.TargetCount,
                    quest.Claimed,
                    claimable,
                    quest.RewardItemId,
                    quest.RewardCount));
            }

            _view.RenderQuests(quests);
        }

        /// <summary>
        /// 领取指定任务奖励：只发 <see cref="ClaimQuestRewardCommand"/> 再重查一次。
        /// 未知任务 / 未完成 / 已领取由处理器静默拒绝，这里因此不会抛异常。
        /// </summary>
        /// <param name="questId">要领取的任务 ID。</param>
        public void Claim(int questId)
        {
            _bus.Send(new ClaimQuestRewardCommand(questId));
            Refresh();
        }

        private void HandleClaimClicked(int questId)
        {
            Claim(questId);
        }
    }
}
