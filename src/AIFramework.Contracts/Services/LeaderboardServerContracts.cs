#nullable enable
using System;

namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 排行榜的赛季视图：当前季（可写）/ 上一季（只读归档）。
    /// <para>
    /// <b>为什么给的是"视图"而不是赛季号</b>：客户端真正需要回答的只有两个问题——"现在还能刷的榜"
    /// 与"上季结算结果长什么样"。赛季编号规则（S1/S2/自然月/赛季时长、跨服合并、结算时区）全部由服务端
    /// 裁决，写进客户端契约只会多出一份必然漂移的真相：服务端换编号策略时，客户端契约一个字都不用改。
    /// </para>
    /// <para>
    /// <b>显式赋值 + <see cref="byte"/> 底层类型</b>：这两个值会进网络包与存档，因此数值锁定、
    /// 只允许追加不允许重排；用 <see cref="byte"/> 而不是默认的 <c>int</c> 是让序列化层按单字节写，
    /// 也顺手把这个枚举钉死在 256 个取值以内（赛季视图永远只有个位数个）。
    /// </para>
    /// <para>
    /// <b>只保留两季是刻意的取舍</b>：更早的赛季属于"历史榜单/荣誉墙"，它的查询粒度、保留时长、
    /// 成本模型与当前季完全不同，应该另开一个只读的历史端口，而不是把本枚举扩成
    /// <c>Season1..SeasonN</c>——那会让每个实现都被迫维护一张"第几季"的映射表。
    /// </para>
    /// </summary>
    public enum LeaderboardSeason : byte
    {
        /// <summary>
        /// 当前季：<see cref="ILeaderboardServer.TrySubmit"/> 的分数落在这里，也是 <c>default</c> 取值
        /// （未显式指定赛季的请求天然读当前季）。
        /// </summary>
        Current = 0,

        /// <summary>
        /// 上一季：最近一次 <see cref="ILeaderboardServer.RotateSeason"/> 归档下来的只读榜单。
        /// 从未切换过赛季时它为空榜（<see cref="LeaderboardPage.TotalCount"/> 为 0），不是错误。
        /// </summary>
        Previous = 1
    }

    /// <summary>
    /// 一次分页拉取的请求参数（<c>readonly struct</c>：可安全按值传递，<c>in</c> 传参避免拷贝）。
    /// <para>
    /// <b>构造期不做任何校验</b>：<c>offset</c> 为负、<c>limit</c> 为 0、<c>leaderboardId</c> 非法
    /// 都允许构造出来。理由是"调用方算错了参数"必须能在无头测试里被构造并断言，
    /// 而不是靠构造器抛异常把正在游玩的 UI 打崩；归一化（<c>offset ≥ 0</c>、
    /// <c>limit ∈ [1, maxLimit]</c>、非法 <c>leaderboardId</c> 直接空页）统一在
    /// <c>LeaderboardPager</c> 里做一次，服务端实现只需面对"已归一化"的入参。
    /// </para>
    /// <para>
    /// <b><see cref="PlayerId"/> 只用于"我是谁"，不参与过滤</b>：服务端不能因为传了玩家 id 就只回他那一行
    /// ——分页结果对所有人必须一致（否则同一个 offset 在不同玩家那里返回不同内容，翻页会跳行）。
    /// 它只决定 <see cref="LeaderboardPage.SelfRow"/> 填谁的排名。
    /// </para>
    /// </summary>
    public readonly struct LeaderboardPageRequest
    {
        /// <summary>构造一次分页请求（不校验；非法入参由 <c>LeaderboardPager</c> 归一化）。</summary>
        /// <param name="leaderboardId">榜单 id（≤ 0 视为非法，调用方会拿到空页且不触达后端）。</param>
        /// <param name="season">要读的赛季视图（未知枚举值会被归一化为当前季）。</param>
        /// <param name="offset">从第几条开始（0 起；负值会被夹到 0）。</param>
        /// <param name="limit">本页最多几条（会被夹到 <c>[1, maxLimit]</c>）。</param>
        /// <param name="playerId">读榜的玩家 id（决定 <see cref="LeaderboardPage.SelfRow"/>；≤ 0 允许，按 0 处理）。</param>
        public LeaderboardPageRequest(int leaderboardId, LeaderboardSeason season, int offset, int limit, long playerId)
        {
            LeaderboardId = leaderboardId;
            Season = season;
            Offset = offset;
            Limit = limit;
            PlayerId = playerId;
        }

        /// <summary>榜单 id（分服/分玩法的榜各自一个；≤ 0 为非法）。</summary>
        public int LeaderboardId { get; }

        /// <summary>赛季视图（默认 <see cref="LeaderboardSeason.Current"/>）。</summary>
        public LeaderboardSeason Season { get; }

        /// <summary>起始下标（0 起；越界返回空页但总人数有效）。</summary>
        public int Offset { get; }

        /// <summary>本页条数上限（由 <c>LeaderboardPager</c> 按 <c>maxLimit</c> 夹紧后再发给后端）。</summary>
        public int Limit { get; }

        /// <summary>发起请求的玩家 id（只影响 <see cref="LeaderboardPage.SelfRow"/>，不影响分页内容）。</summary>
        public long PlayerId { get; }
    }

    /// <summary>
    /// 榜单上的一行：玩家 + 分数 + 名次 + 展示名 + 更新时间，只读快照。
    /// <para>
    /// <b><see cref="Rank"/> 由服务端给出，客户端不得自行推算</b>：它已经包含了同分裁决
    /// （见 <see cref="LeaderboardRow"/> 的排序约定说明），本地重算名次与服务端一旦不同，
    /// "我的排名"就会在翻页时抖动。名次 <b>1 起</b>，<c>0</c> 只出现在
    /// <c>default(LeaderboardRow)</c> 里（= 没有这一行）。
    /// </para>
    /// <para>
    /// <b><see cref="DisplayName"/> 只是展示串</b>：会改、会重名、可能含表情与敏感词，
    /// <b>禁止</b>用于任何归属判定——归属只认 <see cref="PlayerId"/>
    /// （与 <see cref="FriendInfo.PlayerId"/>、<see cref="AccountInfo.PlayerId"/> 同一约定）。
    /// </para>
    /// <para>
    /// <b><see cref="Score"/> 用 <c>int</c>，而老端口 <see cref="LeaderboardEntry.Score"/> 用 <c>long</c></b>：
    /// 服务端榜单天然按赛季重置，单赛季 32 位（±21 亿）足够覆盖所有"分数/战力/伤害"量级，
    /// 且定宽 32 位让分页包更小、与多数后端 SDK 的类型映射一致。累计型数值（总金币等）不属于排行榜，
    /// 应继续走 <c>long</c> 端口。<b>换算在两端口之间显式收窄并断言区间</b>，不做隐式转换。
    /// </para>
    /// </summary>
    public readonly struct LeaderboardRow
    {
        /// <summary>展示名的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _displayName;

        /// <summary>构造一行榜单快照。</summary>
        /// <param name="playerId">玩家 id（排行榜里的唯一键）。</param>
        /// <param name="displayName">展示名（<c>null</c> 归一化为空串；不参与归属判定）。</param>
        /// <param name="score">分数（服务端权威值；允许为负）。</param>
        /// <param name="rank">名次（1 起；0 = 没有这一行）。</param>
        /// <param name="updatedAtUnixSeconds">该分数最后一次刷新的服务端时间（Unix 秒；仅用于展示）。</param>
        public LeaderboardRow(long playerId, string displayName, int score, int rank, long updatedAtUnixSeconds)
        {
            PlayerId = playerId;
            _displayName = displayName;
            Score = score;
            Rank = rank;
            UpdatedAtUnixSeconds = updatedAtUnixSeconds;
        }

        /// <summary>玩家 id（<b>唯一键</b>；排序同分裁决与"我的排名"都按它）。</summary>
        public long PlayerId { get; }

        /// <summary>展示名（<c>null</c> 归一化为空串；可能为空、会变、会重名，禁止用于归属判定）。</summary>
        public string DisplayName
        {
            get { return _displayName ?? string.Empty; }
        }

        /// <summary>分数（服务端权威值；允许为负——负分是合法成绩，按降序排在 0 分之后）。</summary>
        public int Score { get; }

        /// <summary>名次（1 起；0 = 没有这一行；同分不并列，见 <see cref="LeaderboardRow"/> 的排序约定）。</summary>
        public int Rank { get; }

        /// <summary>
        /// 该分数最后一次被刷新的服务端时间（Unix 秒；0 = 未知）。
        /// <para>
        /// <b>它不参与排序</b>：同分裁决固定按玩家 id（见类型注释），时间只用于 UI 展示
        /// "刚刚/3 天前"。把时间写进排序规则会引入"两次查询结果不同"的幻觉 bug，
        /// 也会让同分玩家在客户端与服务端的相对顺序不一致。
        /// </para>
        /// </summary>
        public long UpdatedAtUnixSeconds { get; }
    }

    /// <summary>
    /// 一页榜单：总人数 + 本页行 + "我自己"那一行 + 诊断原因，只读快照。
    /// <para>
    /// <b><see cref="Rows"/> 永不为 <c>null</c></b>：构造期传 <c>null</c> 会被归一化成空数组，
    /// 读方永远可以直接取 <c>.Length</c>，不必判空（与 <see cref="ISocialService.GetFriends"/>
    /// 的"永不为 null"口径一致）。数组每次都是新数组，调用方排序/改元素不会影响服务端状态。
    /// </para>
    /// <para>
    /// <b><see cref="TotalCount"/> 是"榜上一共多少人"，不是"本页几条"</b>：越界页（<c>offset ≥ TotalCount</c>）
    /// 会返回空 <see cref="Rows"/> 但 <see cref="TotalCount"/> 仍然有效——UI 的总页数、进度条全靠它，
    /// 用 <c>Rows.Length</c> 推总人数是分页系统最常见的一类错。
    /// </para>
    /// <para>
    /// <b><see cref="SelfRow"/> 为 <c>null</c> 表示"这个玩家不在榜上"</b>，而不是"这次没查到"：
    /// 不在榜（从没提交 / 分数被挤出榜尾 / 读的是上一季且他没参加过）都归为 <c>null</c>，
    /// 调用方的处理完全一致（隐藏"我的排名"卡片）。
    /// </para>
    /// <para>
    /// <b><see cref="Reason"/> 是诊断文本，禁止用于业务分支</b>：只有失败页才非空
    /// （"invalid leaderboard" / "leaderboard unavailable" / "player not ranked" /
    /// "leaderboard service threw: …"），各实现的措辞允许不同，分支只许看
    /// <see cref="TotalCount"/>、<see cref="Rows"/>、<see cref="SelfRow"/> 的组合。
    /// </para>
    /// </summary>
    public readonly struct LeaderboardPage
    {
        /// <summary>行的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly LeaderboardRow[]? _rows;

        /// <summary>原因的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _reason;

        /// <summary>构造一页榜单。</summary>
        /// <param name="leaderboardId">榜单 id（回显请求值）。</param>
        /// <param name="season">赛季视图（回显请求值）。</param>
        /// <param name="totalCount">榜上总人数（负值归一化为 0）。</param>
        /// <param name="offset">本页起始下标（负值归一化为 0）。</param>
        /// <param name="limit">本页条数上限（负值归一化为 0）。</param>
        /// <param name="rows">本页的行（<c>null</c> 归一化为空数组）。</param>
        /// <param name="selfRow">发起请求的玩家所在行（不在榜为 <c>null</c>）。</param>
        /// <param name="reason">诊断原因（<c>null</c> 归一化为空串；禁止作为业务判定依据）。</param>
        public LeaderboardPage(
            int leaderboardId,
            LeaderboardSeason season,
            int totalCount,
            int offset,
            int limit,
            LeaderboardRow[] rows,
            LeaderboardRow? selfRow,
            string reason)
        {
            LeaderboardId = leaderboardId;
            Season = season;
            TotalCount = totalCount < 0 ? 0 : totalCount;
            Offset = offset < 0 ? 0 : offset;
            Limit = limit < 0 ? 0 : limit;
            _rows = rows;
            SelfRow = selfRow;
            _reason = reason;
        }

        /// <summary>榜单 id（回显请求值，便于调用方把并发回来的多页对上号）。</summary>
        public int LeaderboardId { get; }

        /// <summary>赛季视图（回显请求值）。</summary>
        public LeaderboardSeason Season { get; }

        /// <summary>榜上总人数（≥ 0；越界页也有效）。</summary>
        public int TotalCount { get; }

        /// <summary>本页起始下标（≥ 0）。</summary>
        public int Offset { get; }

        /// <summary>本页条数上限（≥ 0；实际条数可能更少，见 <see cref="Rows"/>）。</summary>
        public int Limit { get; }

        /// <summary>本页的行（永不为 <c>null</c>；越界页为空数组；名次是全局名次，不是页内序号）。</summary>
        public LeaderboardRow[] Rows
        {
            get { return _rows ?? Array.Empty<LeaderboardRow>(); }
        }

        /// <summary>发起请求的玩家所在行（<c>null</c> = 不在榜；与 <see cref="Rows"/> 里的同一行一致）。</summary>
        public LeaderboardRow? SelfRow { get; }

        /// <summary>诊断原因（失败页非空、成功页为空串；禁止作为业务判定依据）。</summary>
        public string Reason
        {
            get { return _reason ?? string.Empty; }
        }
    }

    /// <summary>
    /// 排行榜服务端端口：分页读榜 + 提交分数 + 赛季切换。
    /// <para>
    /// <b>为什么排行榜必须服务端化</b>：客户端本地榜（<c>LeaderboardTable</c>）只能当离线兜底，
    /// 它既不能跨端一致（两台设备的本地榜必然不同），也完全不可信（改内存就能把自己改成第一名）。
    /// 真正参与发奖、称号、赛季结算的榜单必须以服务端记录为准，客户端只读快照。
    /// </para>
    /// <para>
    /// <b>排序约定（跨端唯一口径）</b>：分数降序；<b>同分按玩家 id 升序</b>；名次在排序后按序数给出
    /// （1、2、3…，同分不并列）。<b>同分"按玩家 id"是为了可复现，而不是按提交时间</b>——
    /// 提交时间会随网络重试、补传、服务端时区漂移而变，一旦用它裁决同分，
    /// "同一个榜两次查询顺序不同"就会被当成玄学 bug；玩家 id 是稳定且唯一的，任何一端重算都得到同一结果。
    /// 同分不并列（而不是 1、1、3）也是同一原因：名次要能唯一反解出"从第几条开始翻页"，
    /// 并列名次会让 <c>offset = rank - radius</c> 这类定位公式失去唯一解。
    /// </para>
    /// <para>
    /// <b>分页约定</b>：<c>offset</c> 从 0 起；<c>offset ≥ TotalCount</c> 返回空 <see cref="LeaderboardPage.Rows"/>
    /// 但 <see cref="LeaderboardPage.TotalCount"/> 有效；<c>limit</c> 由服务端夹到自己的上限
    /// （客户端侧的 <c>maxLimit</c> 只是自我保护，不是服务端承诺）。
    /// </para>
    /// <para>
    /// <b>提交约定</b>：服务端只接受<b>更高</b>的分数（个人最好成绩），同分视为没有刷新；
    /// 只有榜单配置允许覆盖时才接受更低分（例如"最近一次成绩"型榜单）。返回值就是"是否刷新了个人最好"，
    /// 客户端不得自行推断。
    /// </para>
    /// <para>
    /// <b><see cref="RotateSeason"/> 的调用方是运维/定时任务，不是客户端</b>：赛季何时切换必须由服务端裁决
    /// （结算发奖、封榜时刻、跨时区），真实实现应把它做成"仅管理员可用的空操作/受控入口"；
    /// 假实现用它做归档。客户端代码<b>禁止</b>调用它来"刷新赛季"——那会让每个客户端都能重置全服榜单。
    /// </para>
    /// <para>
    /// <b>整块同步签名</b>：契约必须能被纯内存假后端直接实现，从而在无头测试里逐条钉死分页、
    /// 同分裁决、赛季边界；真实后端的分页请求/网络等待/重试由适配层用"请求 + 回调"包一层，
    /// 把结果喂进本端口。把 <c>Task</c> 写进契约会逼纯逻辑层依赖异步调度
    /// （与 <see cref="ISocialService"/>、<see cref="IAccountService"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>实现方不应抛异常</b>：失败必须落成空页 + <see cref="LeaderboardPage.Reason"/>、
    /// 提交失败落成 <c>false</c>。调用方（<c>LeaderboardPager</c>）仍会兜底捕获异常，
    /// 但那是第二道防线，不是让你随便抛的许可。
    /// </para>
    /// <para>线程模型：同步、非线程安全，由调用方串行在主线程发起。</para>
    /// </summary>
    public interface ILeaderboardServer
    {
        /// <summary>
        /// 后端当前是否可用（未接线 / 断网 / SDK 未初始化都必须如实返回 <c>false</c>，不得"假装成功"）。
        /// <para>调用方必须先判它：不可用时一律降级为空页，而不是反复发请求等超时。</para>
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 拉一页榜单快照。
        /// <para>
        /// <b>offset 越界不是错误</b>：返回空 <see cref="LeaderboardPage.Rows"/> + 有效
        /// <see cref="LeaderboardPage.TotalCount"/>，让 UI 能区分"翻过头了"与"榜是空的"。
        /// 拉取失败（未接线/断网）返回空页 + <see cref="LeaderboardPage.Reason"/>，<b>不抛异常</b>。
        /// </para>
        /// </summary>
        /// <param name="request">已归一化的请求（<c>in</c> 传参避免拷贝）。</param>
        /// <returns>一页快照（永不为 <c>null</c>，因为它是值类型）。</returns>
        LeaderboardPage FetchPage(in LeaderboardPageRequest request);

        /// <summary>
        /// 提交分数（服务端只接受更高分，除非该榜配置允许覆盖）。
        /// </summary>
        /// <param name="leaderboardId">榜单 id（≤ 0 应返回 <c>false</c>）。</param>
        /// <param name="playerId">玩家 id（判定归属的唯一依据）。</param>
        /// <param name="score">本次成绩（允许为负；负分是合法成绩）。</param>
        /// <returns>是否刷新了个人最好成绩（同分/更低分/后端失败一律 <c>false</c>）。</returns>
        bool TrySubmit(int leaderboardId, long playerId, int score);

        /// <summary>
        /// 赛季切换：<see cref="LeaderboardSeason.Current"/> → <see cref="LeaderboardSeason.Previous"/>，
        /// 然后清空当前季（上一季旧归档被本次覆盖）。
        /// <para>
        /// <b>时机由服务端决定</b>：真实实现应由定时任务/管理员触发，客户端调用应被忽略；
        /// 假实现用它模拟"封榜 + 归档 + 新赛季从零开始"。重复调用是幂等的安全兜底
        /// （不会把归档弄成空档，但会覆盖上一次归档，只保留最近一季）。
        /// </para>
        /// </summary>
        void RotateSeason();

        /// <summary>
        /// 当前提交目标赛季（提交永远进当前季；上一季是只读归档）。
        /// <para>
        /// 因此在"两桶模型"里它恒为 <see cref="LeaderboardSeason.Current"/>；它的意义是让调用方
        /// 拿到"该往哪个视图提交/读当前数据"，而不是暴露赛季编号（编号策略归服务端）。
        /// </para>
        /// </summary>
        LeaderboardSeason CurrentSeason { get; }
    }
}
