#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Roguelike
{
    /// <summary>
    /// <see cref="RoguelikeWorld"/> 的"会话进出"半边：<see cref="IGameplaySession"/> 的
    /// <see cref="Step(float)"/> / <see cref="CollectResult"/>、run 复位，以及三个累计诊断计数。
    /// <para>
    /// <b>为什么拆 partial</b>：单文件守住 400 行上限（AI 上下文自洽优先），而拆分口径必须是
    /// "一个语义半边一整块"——这里只装会话生命周期，战斗/地图流程留在主文件，两边字段无重叠。
    /// </para>
    /// </summary>
    public sealed partial class RoguelikeWorld
    {
        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>累计打赢的战斗场数（含精英与 Boss；<see cref="Restart"/> 不清零）。</summary>
        public int BattlesWon
        {
            get { return _battlesWon; }
        }

        /// <summary>
        /// 累计通过"战斗奖励"加入卡组的牌数（<see cref="Restart"/> 不清零）。
        /// 商店购买只改卡组、不计入本计数——按契约字面口径，本计数是战斗成长的度量。
        /// </summary>
        public int CardsAdded
        {
            get { return _cardsAdded; }
        }

        /// <summary>对局是否结束：run 已结束（阵亡/通关），或结算报告已被取回。</summary>
        public bool IsFinished
        {
            get { return _runOver || _resultCollected; }
        }

        /// <summary>
        /// 推演一步：<c>deltaSeconds</c> 被忽略（回合制不按时间推进），每调用一次计一步。
        /// run 结束（含结算已取回）时为空操作，不累加。
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
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 通关战利品为 <c>LootItem(1003, 1)</c> + <c>LootItem(1001, 50)</c>，阵亡为空数组；
        /// <see cref="GameplayResult.RemainingUnits"/> 取累计胜场数 <see cref="BattlesWon"/>。
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_runOver)
            {
                throw new InvalidOperationException("run 尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;

            IReadOnlyList<LootItem> loots = _victory
                ? new LootItem[]
                {
                    new LootItem(RoguelikeRules.ClearLootItemId, RoguelikeRules.ClearLootCount),
                    new LootItem(RoguelikeRules.ClearGoldLootItemId, RoguelikeRules.ClearGoldLootCount)
                }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                _victory,
                _processedSteps,
                _battlesWon,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>
        /// 把本轮 run 的状态整体复位。构造期与 <see cref="Restart"/> 共用同一条路径，
        /// 保证"新开一局"与"重开一局"不可能出现字段遗漏。
        /// </summary>
        private void ResetRunState()
        {
            _map.Regenerate();
            _random.Reset();
            _deck.ResetStarterDeck();

            _currentNodeIndex = -1;
            _depth = 0;
            _playerHp = RoguelikeRules.PlayerMaxHp;
            _gold = 0;
            _energy = 0;
            _playerBlock = 0;
            _enemyHp = 0;
            _enemyMaxHp = 0;
            _battleKind = RoguelikeNodeKind.Battle;
            _inBattle = false;
            _runOver = false;
            _victory = false;

            // _processedSteps / _battlesWon / _cardsAdded / _resultCollected 是累计值，重开不清零。
        }
    }
}
