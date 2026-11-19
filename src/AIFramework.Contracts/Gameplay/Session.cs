using System.Collections.Generic;
using AIFramework.Contracts.Numerics;

namespace AIFramework.Contracts.Gameplay
{
    /// <summary>玩法卡带类型。两套内核运行期完全隔离，唯一共享进出门 DTO。</summary>
    public enum GameplayKernelType
    {
        Turn = 0,
        Realtime = 1,

        /// <summary>卡牌回合战斗：Kernel.Turn 的第二套内容，验证同一内核可承载不同规则。</summary>
        CardBattle = 2,

        /// <summary>三消棋盘：Kernel.Turn 的第三套内容，验证网格与级联同属回合流。</summary>
        Match3 = 3
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

    /// <summary>
    /// 强实时沙盒的"布场"能力：把参战单位与飞行物放进战场。
    /// <para>
    /// <b>为什么单独抽出来</b>：<see cref="IGameplaySession"/> 只管"推进与结算"，而宿主/关卡脚本还要能布场。
    /// 若只暴露具体 <c>BattleWorld</c>，装配层就会被迫引用内核具体类型（引脚泄漏到适配层）。
    /// 由 <c>BattleWorld</c> 实现本接口，宿主从 <see cref="IGameplaySession"/> 结果上 <c>as</c> 转过来即可。
    /// </para>
    /// <para>布场只允许在 <see cref="IGameplaySession.Step"/> 之外调用（装配期），帧内布场会破坏预分配假设。</para>
    /// </summary>
    public interface IRealtimeSandbox
    {
        /// <summary>本局累计反射次数（诊断用）。</summary>
        int ReflectedCount { get; }

        /// <summary>本局累计打断次数（诊断用）。</summary>
        int InterruptedCount { get; }

        /// <summary>放入一个单位，返回其句柄；槽位耗尽返回无效句柄。</summary>
        UnitHandle SpawnUnit(Vec2 position, float radius, int armorLevel, int hp, GameplayTagMask tags);

        /// <summary>放入一颗飞行物，返回其句柄；槽位耗尽返回无效句柄。</summary>
        ProjectileHandle SpawnProjectile(
            UnitHandle owner,
            Vec2 position,
            Vec2 velocity,
            int damage,
            GameplayTagMask tags);
    }
}
