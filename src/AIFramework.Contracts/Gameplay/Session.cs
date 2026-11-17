using System.Collections.Generic;

namespace AIFramework.Contracts.Gameplay
{
    /// <summary>玩法卡带类型。两套内核运行期完全隔离，唯一共享进出门 DTO。</summary>
    public enum GameplayKernelType
    {
        Turn = 0,
        Realtime = 1
    }

    /// <summary>
    /// 进门前数据快照：主板把战前所需数据打包为不可变结构体传入内核。
    /// 局内运行期间，内核严禁跨出沙盒读取背包、商店或玩家全局存档。
    /// </summary>
    public readonly struct GameplayInitData
    {
        public GameplayInitData(int levelId, int randomSeed, int unitCount)
        {
            LevelId = levelId;
            RandomSeed = randomSeed;
            UnitCount = unitCount;
        }

        public int LevelId { get; }

        public int RandomSeed { get; }

        /// <summary>出战单位数量，Phase 1 用于内核自建棋子/战场单位。</summary>
        public int UnitCount { get; }
    }

    /// <summary>战利品条目：纯数据，供主板派发发奖指令。</summary>
    public readonly struct LootItem
    {
        public LootItem(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }

        public int Count { get; }
    }

    /// <summary>
    /// 出门结算报告。不可变 DTO，<b>只允许在 <c>CollectResult()</c> 内构造一次</b>，
    /// <c>Step()</c> 内禁止构造或刷新它（否则帧内必然产生堆分配）。
    /// </summary>
    public sealed class GameplayResult
    {
        public GameplayResult(
            GameplayKernelType kernelType,
            bool isVictory,
            int stepCount,
            int remainingUnits,
            IReadOnlyList<GameplayTag> triggeredTags,
            IReadOnlyList<LootItem> loots)
        {
            KernelType = kernelType;
            IsVictory = isVictory;
            StepCount = stepCount;
            RemainingUnits = remainingUnits;
            TriggeredTags = triggeredTags;
            Loots = loots;
        }

        public GameplayKernelType KernelType { get; }

        public bool IsVictory { get; }

        /// <summary>推演步数（回合数或逻辑帧数）。</summary>
        public int StepCount { get; }

        public int RemainingUnits { get; }

        /// <summary>本局触发过的事件标签（用于局外任务/成就判定）。</summary>
        public IReadOnlyList<GameplayTag> TriggeredTags { get; }

        /// <summary>请求发放的战利品清单。</summary>
        public IReadOnlyList<LootItem> Loots { get; }
    }

    /// <summary>
    /// 玩法卡带沙盒会话。内核实现，主板只按此接口驱动。
    /// </summary>
    public interface IGameplaySession
    {
        /// <summary>推进一个逻辑步。推演期间禁止调用 CQE 总线，只允许写 <c>IIntentWriter</c>。</summary>
        void Step(float deltaSeconds);

        /// <summary>对局是否结束。</summary>
        bool IsFinished { get; }

        /// <summary>
        /// 取回结算报告。<c>IsFinished == false</c> 或第二次调用都必须抛 <see cref="System.InvalidOperationException"/>；
        /// 成功调用一次即交还会话所有权。
        /// </summary>
        GameplayResult CollectResult();
    }

    /// <summary>
    /// 会话工厂。唯一实现方是装配层（Console / Unity 适配壳）；
    /// <c>Core</c> 与 <c>Features</c> 一律不得实现，因为二者只依赖 Contracts。
    /// </summary>
    public interface IGameplayKernelFactory
    {
        IGameplaySession Create(GameplayKernelType type, in GameplayInitData init);
    }
}
