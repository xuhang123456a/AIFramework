#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Monopoly
{
    /// <summary>
    /// <see cref="MonopolyWorld"/> 的"格子结算"半边：落点效果、地产买卖/升级/租金、机会卡与破产触发。
    /// <para>
    /// <b>结算顺序</b>（测试逐条钉住）：移动（含起点奖励）→ 按落点种类分派 → 地产先看所有者
    /// （无主买、自己升、他人付租）→ 机会卡可能再移动一次并递归结算新落点（深度上限
    /// <see cref="MonopolyRules.MaxChanceChainDepth"/>，正常布局不可达）。
    /// </para>
    /// <para>
    /// <b>为什么拆 partial</b>：经济规则是"另一个语义半边"，回合流程（谁动、谁被淘汰、何时收口）
    /// 留在主文件；这里只关心"某玩家踩到某格之后资产怎么变"。
    /// </para>
    /// </summary>
    public sealed partial class MonopolyWorld
    {
        /// <summary>
        /// 结算 <paramref name="player"/> 踩到 <paramref name="tile"/> 的效果。
        /// <paramref name="chainDepth"/> 只在机会卡"前进 3 格"递归时递增，用于防御性断开无限连锁。
        /// </summary>
        private void ResolveTile(int player, int tile, int chainDepth)
        {
            switch (_board.KindAt(tile))
            {
                case MonopolyTileKind.Go:
                    // 起点奖励已在 MoveForward 越环时发放；停在起点不再重复发。
                    break;

                case MonopolyTileKind.Property:
                    ResolveProperty(player, tile);
                    break;

                case MonopolyTileKind.Tax:
                    // 现金不足时只扣到 0，不破产（只有付不起租金才出局，见类注释的简化清单）。
                    _cash[player] -= MonopolyRules.TaxAmount;
                    if (_cash[player] < 0)
                    {
                        _cash[player] = 0;
                    }

                    break;

                case MonopolyTileKind.Bonus:
                    _cash[player] += MonopolyRules.BonusAmount;
                    break;

                case MonopolyTileKind.Chance:
                    ResolveChance(player, chainDepth);
                    break;

                case MonopolyTileKind.Jail:
                    // 简化：监狱 = 直接回到起点，不发起点奖励，也不额外停回合（本回合正常结束）。
                    _positions[player] = MonopolyRules.GoTileIndex;
                    break;

                default:
                    // 枚举是 byte 存储的稳定协议值；新增种类时必须同步补分支，这里保守不结算。
                    break;
            }
        }

        /// <summary>
        /// 地产结算：无主 → 尝试自动买入；自己的 → 尝试升级；他人的 → 付租金（付不起即破产）。
        /// </summary>
        private void ResolveProperty(int player, int tile)
        {
            int owner = _board.OwnerAt(tile);

            if (owner < 0)
            {
                TryBuyProperty(player, tile);
                return;
            }

            if (owner == player)
            {
                TryUpgradeProperty(player, tile);
                return;
            }

            PayRent(player, owner, tile);
        }

        /// <summary>
        /// 无主地产自动买入：现金 ≥ 地价才成交（扣现金、置所有者、等级 0）；
        /// 现金不足则跳过并计入 <see cref="SkippedPurchaseCount"/>，不改任何棋盘状态。
        /// </summary>
        private void TryBuyProperty(int player, int tile)
        {
            int price = _board.PriceAt(tile);
            if (_cash[player] < price)
            {
                _skippedPurchaseCount++;
                return;
            }

            _cash[player] -= price;
            _board.Buy(tile, player);
        }

        /// <summary>
        /// 自己的地产升级：已满级或现金不足则跳过（不计数）；否则扣 <c>地价 × 50%</c>、等级 +1。
        /// </summary>
        private void TryUpgradeProperty(int player, int tile)
        {
            if (_board.LevelAt(tile) >= MonopolyRules.MaxPropertyLevel)
            {
                return;
            }

            int cost = MonopolyRules.UpgradeCostFor(_board.PriceAt(tile));
            if (_cash[player] < cost)
            {
                return;
            }

            _cash[player] -= cost;
            _board.Upgrade(tile);
        }

        /// <summary>
        /// 付租金给他人：<c>租金 = 地价 × (10% + 15% × 等级)</c> 向下取整。
        /// 现金足够则扣款并转给所有者；不足则当场破产（现金归 0、地产归还无主、欠款不转移）。
        /// </summary>
        private void PayRent(int player, int owner, int tile)
        {
            int rent = MonopolyRules.RentFor(_board.PriceAt(tile), _board.LevelAt(tile));
            if (_cash[player] < rent)
            {
                Bankrupt(player);
                return;
            }

            _cash[player] -= rent;
            _cash[owner] += rent;
        }

        /// <summary>
        /// 机会卡：累计 <see cref="ChanceEvents"/> 后由种子随机 4 选 1——
        /// <c>+100 / +200 / -100（扣到 0 为止）/ 前进 3 格</c>。
        /// "前进 3 格"会照常发越环起点奖励，并对新落点递归结算（深度上限见
        /// <see cref="MonopolyRules.MaxChanceChainDepth"/>）。
        /// </summary>
        private void ResolveChance(int player, int chainDepth)
        {
            _chanceEvents++;

            int card = _random.NextIndex(MonopolyRules.ChanceCardCount);
            switch (card)
            {
                case MonopolyRules.ChanceCardGainSmall:
                    _cash[player] += MonopolyRules.ChanceGainSmall;
                    break;

                case MonopolyRules.ChanceCardGainBig:
                    _cash[player] += MonopolyRules.ChanceGainBig;
                    break;

                case MonopolyRules.ChanceCardLose:
                    // 损失同缴税：只扣到 0，不触发破产。
                    _cash[player] -= MonopolyRules.ChanceLose;
                    if (_cash[player] < 0)
                    {
                        _cash[player] = 0;
                    }

                    break;

                case MonopolyRules.ChanceCardMove:
                    int tile = MoveForward(player, MonopolyRules.ChanceMoveSteps);
                    if (chainDepth < MonopolyRules.MaxChanceChainDepth)
                    {
                        ResolveTile(player, tile, chainDepth + 1);
                    }

                    break;

                default:
                    // 抽取范围由 ChanceCardCount 锁定，理论不可达；保守不发任何效果。
                    break;
            }
        }
    }
}
