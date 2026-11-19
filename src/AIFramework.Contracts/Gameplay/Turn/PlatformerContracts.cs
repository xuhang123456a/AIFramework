#nullable enable
namespace AIFramework.Contracts.Gameplay.Turn
{
    /// <summary>
    /// 单块平台的只读快照（不可变值类型）：渲染层按帧取一次就能画，不必持有内核引用。
    /// <para>
    /// <b>坐标约定</b>：<see cref="X"/>/<see cref="Y"/> 是平台矩形的<b>左下角</b>，<c>x</c> 向右为正、<c>y</c> 向上为正；
    /// 平台顶面高度恒为 <c>Y + Height</c>。玩家落到平台顶时，其脚底（<c>PlayerY</c>）会被贴到这个值上。
    /// </para>
    /// <para>
    /// <b><see cref="PlatformId"/> 从 1 开始</b>（0 永远不是合法平台），因此 <c>default(PlatformerView)</c>
    /// 天然表示"没有这块平台"，越界查询不会和真平台混淆。
    /// </para>
    /// <para>
    /// <b>单向平台</b>：本卡带的平台只做"从上方落到顶面"的碰撞，左右与下方都不阻挡；
    /// 视图不暴露"是否可穿越"这类规则位，因为所有平台口径一致。
    /// </para>
    /// </summary>
    public readonly struct PlatformerView
    {
        public PlatformerView(int platformId, float x, float y, float width, float height)
        {
            PlatformId = platformId;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>本关内自增的平台编号（从 1 开始；<c>0</c> 表示"没有这块平台"）。</summary>
        public int PlatformId { get; }

        /// <summary>矩形左下角的世界横坐标（米）。</summary>
        public float X { get; }

        /// <summary>矩形左下角的世界纵坐标（米）；顶面是 <c>Y + Height</c>。</summary>
        public float Y { get; }

        /// <summary>矩形宽度（米，&gt; 0）。</summary>
        public float Width { get; }

        /// <summary>矩形高度（米，&gt; 0）。</summary>
        public float Height { get; }
    }

    /// <summary>
    /// 单个收集品的只读快照（不可变值类型）。
    /// <para>
    /// <b>坐标是中心点</b>（与 <see cref="PlatformerView"/> 的左下角口径不同，因为收集品是"点状目标"）：
    /// 内核给每个收集品配一个 <c>0.5 × 0.5</c> 米的判定盒，中心就是 <see cref="X"/>/<see cref="Y"/>，
    /// 玩家 AABB 与它相交即视为收集。
    /// </para>
    /// <para>
    /// <b><see cref="CollectibleId"/> 从 1 开始</b>（0 表示"没有这个收集品"）。<see cref="Collected"/>
    /// 一旦为 true 就<b>不会</b>因为来回走动而再次计分，只有 <c>Restart</c> 才复位。
    /// </para>
    /// </summary>
    public readonly struct CollectibleView
    {
        public CollectibleView(int collectibleId, float x, float y, bool collected)
        {
            CollectibleId = collectibleId;
            X = x;
            Y = y;
            Collected = collected;
        }

        /// <summary>本关内自增的收集品编号（从 1 开始；<c>0</c> 表示"没有这个收集品"）。</summary>
        public int CollectibleId { get; }

        /// <summary>收集品中心点的世界横坐标（米）。</summary>
        public float X { get; }

        /// <summary>收集品中心点的世界纵坐标（米）。</summary>
        public float Y { get; }

        /// <summary>是否已被收集（收集后不再计分，<c>Restart</c> 复位为 false）。</summary>
        public bool Collected { get; }
    }

    /// <summary>
    /// 平台跳跃只读视图：渲染层每帧只读这里，不碰内核内部平台/收集品数组。
    /// <para>
    /// <b>坐标口径</b>：<see cref="PlayerX"/>/<see cref="PlayerY"/> 是玩家 AABB 的<b>左下角</b>
    /// （AABB 固定 <c>0.8 × 1.6</c> 米），<c>y</c> 向上为正；<see cref="PlayerY"/> 等于脚下平台顶面高度，
    /// 掉出世界时它会一直是负值。
    /// </para>
    /// <para>
    /// <b>为什么平台与收集品要按索引逐条取</b>：同一关最多 8 块平台、8 个收集品，一次 <c>Get</c>
    /// 只复制一个值类型快照，不产生数组、不产生堆分配，也不存在"整帧快照"被误缓存的问题。
    /// 越界索引抛 <see cref="System.ArgumentOutOfRangeException"/>（这是"数据读错了"，不是正常的游戏状态）。
    /// </para>
    /// <para>
    /// <b><see cref="IsFinished"/> 与 <see cref="IsVictory"/> 的区别</b>：前者只管"这一局是否已经结束"
    /// （到达终点、掉出世界或结算报告已取回），后者只回答"结束方式是不是胜利"；未结束时
    /// <see cref="IsVictory"/> 恒为 <c>false</c>。
    /// </para>
    /// </summary>
    public interface IPlatformerReadView
    {
        /// <summary>玩家 AABB 左下角的世界横坐标（米）。</summary>
        float PlayerX { get; }

        /// <summary>玩家 AABB 左下角的世界纵坐标（米）；站在平台上时等于该平台顶面高度。</summary>
        float PlayerY { get; }

        /// <summary>玩家垂直速度（米/秒，向上为正；在地面时恒为 0）。</summary>
        float PlayerVelocityY { get; }

        /// <summary>玩家是否站在平台上（起跳当帧即变为 false，落地当帧变为 true）。</summary>
        bool IsOnGround { get; }

        /// <summary>当前分数：每收集一个收集品 <c>+10</c>（<c>Restart</c> 归零）。</summary>
        int Score { get; }

        /// <summary>已收集数量（<c>0..CollectibleCount</c>）。</summary>
        int CollectedCount { get; }

        /// <summary>本关收集品总数（<c>5..8</c>）。</summary>
        int CollectibleCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 个收集品的快照（下标即关卡数据顺序，0 起）。
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException">下标不在 <c>0..CollectibleCount-1</c> 内。</exception>
        CollectibleView GetCollectible(int index);

        /// <summary>本关平台总数（<c>5..8</c>；第 0 块是出生平台）。</summary>
        int PlatformCount { get; }

        /// <summary>
        /// 取第 <paramref name="index"/> 块平台的快照（0 起，第 0 块是出生平台）。
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException">下标不在 <c>0..PlatformCount-1</c> 内。</exception>
        PlatformerView GetPlatform(int index);

        /// <summary>对局是否结束（到达终点、掉出世界，或结算报告已取回）。</summary>
        bool IsFinished { get; }

        /// <summary>结束方式是否为胜利（未结束时恒为 false；掉出世界后恒为 false）。</summary>
        bool IsVictory { get; }
    }

    /// <summary>
    /// 平台跳跃输入端口：适配层只能通过它提交意图，不能直接改状态。
    /// <para>所有方法在对局结束后一律为空操作 / 返回 <c>false</c>，不抛异常。</para>
    /// </summary>
    public interface IPlatformerInput
    {
        /// <summary>左右移动意图，-1..1（按住式：每帧调用设置当前值）。</summary>
        void SetMove(float direction);

        /// <summary>跳跃；只有在地面才能起跳，空中返回 false。</summary>
        bool TryJump();

        /// <summary>推进物理（重力、平台碰撞、收集品、终点判定）。</summary>
        void Tick(double deltaSeconds);
    }
}
