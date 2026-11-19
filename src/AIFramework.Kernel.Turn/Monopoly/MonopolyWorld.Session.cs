#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Kernel.Turn.Monopoly
{
    /// <summary>
    /// <see cref="MonopolyWorld"/> 的"会话进出"半边：<see cref="IGameplaySession"/> 的
    /// <see cref="Step(float)"/> / <see cref="CollectResult"/> 与推演计数。
    /// <para>
    /// <b>为什么拆 partial</b>：会话生命周期与"棋盘 + 经济 + 回合"是两块可以独立阅读的语义；
    /// 这里只装进出门，主文件与 <c>MonopolyWorld.Resolve.cs</c> 不出现会话 DTO 的构造。
    /// </para>
    /// </summary>
    public sealed partial class MonopolyWorld
    {
        /// <summary>
        /// 推演步数（<see cref="Step(float)"/> 的有效调用次数，会话级诊断量；重开语义不存在，不归零）。
        /// <b>注意</b>：结算报告里的 <see cref="GameplayResult.StepCount"/> 按需求取 <see cref="TurnIndex"/>
        /// （回合数口径），不是本计数。
        /// </summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>
        /// 推演一步：回合制不按时间推进逻辑，<paramref name="deltaSeconds"/> 被忽略，每调用一次计一步。
        /// 对局结束（含结算报告已取回）时为空操作，不累加。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            _processedSteps++;
        }

        /// <summary>
        /// 取回结算报告：未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>（会话所有权只交还一次）。
        /// <para>
        /// 报告口径（需求锁定）：固定 <see cref="GameplayKernelType.Turn"/>；
        /// <see cref="GameplayResult.IsVictory"/> = 玩家 0 获胜（唯一幸存者或 40 回合后现金最高）；
        /// <see cref="GameplayResult.StepCount"/> = <see cref="TurnIndex"/>；
        /// <see cref="GameplayResult.RemainingUnits"/> = 玩家 0 现金（需求里的 score 口径）；
        /// 胜利发 <c>LootItem(1003, 1)</c>，失败/未胜为空数组；不触发任何 <see cref="GameplayTag"/>。
        /// </para>
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_finished)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;

            bool isVictory = _winnerPlayer == 0;
            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(MonopolyRules.VictoryLootItemId, MonopolyRules.VictoryLootCount) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _turnIndex,
                _cash[0],
                Array.Empty<GameplayTag>(),
                loots);
        }
    }
}
