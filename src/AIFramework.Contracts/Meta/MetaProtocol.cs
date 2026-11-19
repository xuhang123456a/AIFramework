#nullable enable
using System;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Contracts.Meta
{
    /// <summary>
    /// 通用道具与货币 ID。<b>Phase 1 常量表</b>：等 Luban 接入后由配置表提供，
    /// 届时删除本类并改为 `TbItem` 查询；在此之前所有切片与适配层统一引用这里，禁止各自写魔法数字。
    /// </summary>
    public static class MetaItemIds
    {
        /// <summary>货币：金币。</summary>
        public const int Gold = 1;

        /// <summary>补签卡（签到切片专用消耗品）。</summary>
        public const int RetroactiveCard = 9001;

        /// <summary>签到奖励道具 ID 的起点（第 N 天奖励 = 1000 + N），仅 Phase 1 夹具使用。</summary>
        public const int SignInRewardBase = 1000;
    }

    /// <summary>背包只读查询端口：切片只能读数量，写必须走 Command。</summary>
    public interface IInventoryQuery : Cqe.IQuery<InventorySnapshot>
    {
        int ItemId { get; }
    }

    /// <summary>
    /// 背包查询请求体。放在 Contracts 而不是某个切片里，因为**任何切片都可能查背包**；
    /// 若定义在切片内，其它切片一引用就触发 AIF001（跨切片互引）。
    /// </summary>
    public readonly struct InventoryQueryRequest : IInventoryQuery
    {
        public InventoryQueryRequest(int itemId)
        {
            ItemId = itemId;
        }

        public int ItemId { get; }
    }

    /// <summary>背包条目快照（契约层类型，不带切片内部结构）。</summary>
    public readonly struct InventoryEntrySnapshot
    {
        public InventoryEntrySnapshot(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }

        public int Count { get; }
    }

    /// <summary>背包整包查询端口：UI 陈列需要一次拿全，逐 ID 轮询既慢又容易不同步。</summary>
    public interface IInventoryListQuery : Cqe.IQuery<InventoryListSnapshot>
    {
    }

    /// <summary>背包整包查询请求体。</summary>
    public readonly struct InventoryListQueryRequest : IInventoryListQuery
    {
    }

    /// <summary>背包整包快照（按 ItemId 升序）。</summary>
    public sealed class InventoryListSnapshot
    {
        public InventoryListSnapshot(InventoryEntrySnapshot[] items)
        {
            Items = items ?? Array.Empty<InventoryEntrySnapshot>();
        }

        public InventoryEntrySnapshot[] Items { get; }
    }

    /// <summary>道具已消耗事件。</summary>
    public readonly struct ItemConsumedEvent : IEvent
    {
        public ItemConsumedEvent(int itemId, int count)
        {
            ItemId = itemId;
            Count = count;
        }

        public int ItemId { get; }

        public int Count { get; }
    }

    /// <summary>背包拒绝事件：数量不足或非法请求，用于红点/提示，不产生状态变更。</summary>
    public readonly struct ItemChangeRejectedEvent : IEvent
    {
        public ItemChangeRejectedEvent(int itemId, int count, int reasonTagId)
        {
            ItemId = itemId;
            Count = count;
            ReasonTagId = reasonTagId;
        }

        public int ItemId { get; }

        public int Count { get; }

        /// <summary>拒绝原因数值标签（如 7001 = 数量不足）。</summary>
        public int ReasonTagId { get; }
    }

    /// <summary>一局战斗结束事实：任务、活动、成就类切片据此推进进度，不需要知道战斗细节。</summary>
    public readonly struct GameFinishedEvent : IEvent
    {
        public GameFinishedEvent(GameplayKernelType kernelType, int levelId, bool isVictory)
        {
            KernelType = kernelType;
            LevelId = levelId;
            IsVictory = isVictory;
        }

        public GameplayKernelType KernelType { get; }

        public int LevelId { get; }

        public bool IsVictory { get; }
    }

    /// <summary>
    /// 动作原因标签常量：用于因果日志与审计的数值溯源。
    /// <b>集中在这里的唯一理由</b>：日志只存数值，若各切片各写一份私有常量，导出时无法把 tag 映射回来源。
    /// </summary>
    public static class MetaReasonTags
    {
        /// <summary>拒绝：道具数量不足。</summary>
        public const int NotEnoughItem = 7001;

        /// <summary>拒绝：请求参数非法（负数量、未知 ID 等）。</summary>
        public const int InvalidRequest = 7002;

        /// <summary>签到：当日领取。</summary>
        public const int SignInClaim = 5001;

        /// <summary>签到：补签消耗。</summary>
        public const int SignInRetroactive = 5002;

        /// <summary>战斗：战利品发放。</summary>
        public const int GameLoot = 6001;

        /// <summary>商店：扣款。</summary>
        public const int ShopConsume = 6101;

        /// <summary>商店：发货。</summary>
        public const int ShopGrant = 6102;

        /// <summary>任务：领奖发货。</summary>
        public const int QuestReward = 6201;

        /// <summary>邮件：附件领取发货。</summary>
        public const int MailClaim = 6301;
    }

    /// <summary>红点查询端口：UI 只问"这个节点亮不亮、亮几个"，不关心树结构。</summary>
    public interface IRedDotQuery : Cqe.IQuery<RedDotSnapshot>
    {
        /// <summary>节点 ID（由各切片自定义，如 100 = 签到、200 = 任务、300 = 邮件）。</summary>
        int NodeId { get; }
    }

    /// <summary>红点查询请求体。</summary>
    public readonly struct RedDotQueryRequest : IRedDotQuery
    {
        public RedDotQueryRequest(int nodeId)
        {
            NodeId = nodeId;
        }

        public int NodeId { get; }
    }

    /// <summary>红点快照。</summary>
    public readonly struct RedDotSnapshot
    {
        public RedDotSnapshot(int nodeId, int count)
        {
            NodeId = nodeId;
            Count = count;
        }

        public int NodeId { get; }

        /// <summary>0 表示不亮；大于 0 表示要显示的数字（具体展示由 UI 决定）。</summary>
        public int Count { get; }
    }

    /// <summary>红点节点 ID 常量（Phase 1 手写，Phase 2 由配置表生成）。</summary>
    public static class RedDotNodeIds
    {
        public const int SignIn = 100;
        public const int Quest = 200;
        public const int Mail = 300;
        public const int Shop = 400;
    }

    /// <summary>
    /// 红点增量指令：切片发现"有新东西"时发它，由主板（Core）统一维护红点树，
    /// 这样切片之间不需要互相引用，也不需要知道树长什么样。
    /// </summary>
    public readonly struct RedDotDeltaCommand : ICommand
    {
        public RedDotDeltaCommand(int nodeId, int delta)
        {
            NodeId = nodeId;
            Delta = delta;
        }

        public int NodeId { get; }

        /// <summary>增量，正数点亮、负数熄灭；最终值不允许小于 0（主板会夹到 0）。</summary>
        public int Delta { get; }
    }

    /// <summary>
    /// 红点绝对值指令：登录/断线重连/热重载这类"基线全丢"的场景必须用设定值而不是增量，
    /// 否则同一批红点会被重复累加。
    /// </summary>
    public readonly struct RedDotSetCommand : ICommand
    {
        public RedDotSetCommand(int nodeId, int value)
        {
            NodeId = nodeId;
            Value = value;
        }

        public int NodeId { get; }

        /// <summary>目标值，负数按 0 处理。</summary>
        public int Value { get; }
    }
}
