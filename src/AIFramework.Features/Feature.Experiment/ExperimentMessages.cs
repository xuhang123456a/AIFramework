#nullable enable
using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Experiment
{
    /// <summary>
    /// 让切片"按某个玩家 id 做一次确定性分桶并记录曝光"的命令：由适配层在登录/进大厅时发一次。
    /// <para>
    /// <b>幂等</b>：同一 <paramref name="playerId"/> 重复发送不改变分配结果、不重复广播曝光事件、
    /// 也不增加 <c>ExperimentSnapshot.ExposedCount</c>；换一个玩家 id 才会为新玩家补发曝光。
    /// </para>
    /// <para>
    /// 本命令不带时间、不带随机种子：分桶只由"配置表 key + 玩家 id"决定（见 <c>ExperimentBucketing</c>），
    /// 同样的输入在任何进程、任何机器、任何时刻都必须落在同一组。
    /// </para>
    /// </summary>
    public readonly struct ExperimentEvaluateCommand : ICommand
    {
        public ExperimentEvaluateCommand(int playerId)
        {
            PlayerId = playerId;
        }

        /// <summary>参与分桶的玩家 id（任意 int 都可用；负数/0 只在联调与匿名场景出现，不做校验）。</summary>
        public int PlayerId { get; }
    }

    /// <summary>
    /// 曝光事实：某个玩家第一次被分进某个实验时广播一次（同一 <c>(experimentId, playerId)</c> 全局只发一次）。
    /// <para>
    /// 只有 <c>enabled=1</c> 的实验才曝光：被禁用的实验不参与分桶，也不会产生曝光事实
    /// （它的分配结果只在快照里以 <c>Enabled=false</c> 出现）。
    /// </para>
    /// <para>
    /// 事件顺序 = 配置表行顺序。订阅方（适配层）可把它翻译成埋点上报；本切片不直接依赖
    /// <c>IAnalyticsService</c>，避免在 <c>Contracts</c> 之外私造事件号。
    /// </para>
    /// </summary>
    public readonly struct ExperimentExposedEvent : IEvent
    {
        public ExperimentExposedEvent(int experimentId, int variant, int playerId)
        {
            ExperimentId = experimentId;
            Variant = variant;
            PlayerId = playerId;
        }

        /// <summary>被曝光的实验 id（配置主键）。</summary>
        public int ExperimentId { get; }

        /// <summary>该玩家落到的那一组的取值（<c>variantB</c> 或 <c>variantA</c>，由分桶决定）。</summary>
        public int Variant { get; }

        /// <summary>被曝光的玩家 id。</summary>
        public int PlayerId { get; }
    }
}
