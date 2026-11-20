#nullable enable
using System;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 排行榜分页大脑：把 <see cref="ILeaderboardServer"/>（服务端分页 + 提交 + 赛季归档）
    /// 包成"入参一定合法、失败一定降级、名次一定可复现"的查询端口。
    /// <para>
    /// <b>为什么这段逻辑必须在 Core 而不是某个适配器里</b>：参数归一化（offset/limit/赛季/榜单 id）、
    /// 不可用降级、"我的排名"两次请求的定位公式，这三条在 Console 与 Unity、以及未来的真实后端上
    /// 必须<b>逐字一致</b>。写进任一适配器，另一端就会漂移出"某个 limit 能翻页、另一个不能"的差异，
    /// 而这类差异在真机上表现为"榜翻到一半变成空的"，极难复现。Core 是唯一能被两个装配入口共用、
    /// 又能被无头单测逐条钉死的位置（与 <see cref="FriendService"/>、<see cref="AccountSessionManager"/>
    /// 同一取舍）。
    /// </para>
    /// <para>
    /// <b>排序口径（跨端唯一真相）</b>：分数降序；<b>同分按玩家 id 升序</b>；名次在排序后按序数给出
    /// （1、2、3…，同分不并列）。<b>同分按玩家 id 是为了可复现，而不是按提交时间</b>——
    /// 提交时间会被网络重试/补传/服务端时区漂移改写，用它裁决同分会让"同一个榜两次查询顺序不同"
    /// 变成玄学 bug；玩家 id 稳定且唯一，任何一端重算都得到同一结果。
    /// <see cref="CompareRows"/> 是这条规则的唯一实现，假后端与真实适配层都应复用它，
    /// 而不是各写一份比较器。
    /// </para>
    /// <para>
    /// <b>本类不改写服务端给的 <see cref="LeaderboardPage.SelfRow"/>. 唯一的例外是 <see cref="FetchAroundSelf"/>
    /// 在"服务端不填 selfRow、但这一页里确实能看到自己"时补一份</b>：该方法的全部职责就是"定位自己"，
    /// 拿不到 selfRow 就退化成本页扫描（见方法注释）；普通 <see cref="Fetch"/> 则是服务端字段的透明副本。
    /// </para>
    /// <para>
    /// <b>失败一律降级为空页 + <see cref="LeaderboardPage.Reason"/>，绝不抛异常</b>：
    /// 排行榜是外围展示系统，一次抖动不该把正在游玩的玩家打断。只有装配期的编程错误
    /// （<c>server</c> 为 <c>null</c>）立刻响。
    /// </para>
    /// <para>
    /// <b>不做的事</b>：不缓存任何一页（缓存会让"刚提交完立刻刷新"看到旧名次，且要多维护一份失效逻辑）；
    /// 不重排服务端返回的 <see cref="LeaderboardPage.Rows"/>（名次是服务端权威字段，客户端重排会让
    /// <see cref="LeaderboardRow.Rank"/> 与实际位置不一致）；不做重试与限流（属于适配层）。
    /// </para>
    /// <para>线程模型：<b>全同步、非线程安全</b>。所有调用必须串行在主线程；本类不加锁。</para>
    /// </summary>
    public sealed class LeaderboardPager
    {
        /// <summary>默认单页上限（100 条：一屏列表 10~20 条，翻页预取 5 页足够）。</summary>
        public const int DefaultMaxLimit = 100;

        /// <summary>失败原因：榜单 id 非法（≤ 0），请求根本不会打到后端。</summary>
        public const string ReasonInvalidLeaderboard = "invalid leaderboard";

        /// <summary>失败原因：后端未接线 / 断网 / 实现违约抛异常（可降级继续玩）。</summary>
        public const string ReasonUnavailable = "leaderboard unavailable";

        /// <summary>失败原因：玩家不在榜上（从没提交 / 被挤出榜尾 / 不在本季）。</summary>
        public const string ReasonNotRanked = "player not ranked";

        /// <summary>失败原因前缀：后端实现违约抛异常（后接异常类型名，不含堆栈）。</summary>
        public const string ReasonServiceThrew = "leaderboard service threw";

        /// <summary>排行榜后端；构造后不可变。</summary>
        private readonly ILeaderboardServer _server;

        /// <summary>单页上限（已夹到 ≥ 1；构造后不可变）。</summary>
        private readonly int _maxLimit;

        /// <summary>创建分页大脑。</summary>
        /// <param name="server">排行榜后端（非 <c>null</c>）。</param>
        /// <param name="maxLimit">
        /// 单页上限（默认 <see cref="DefaultMaxLimit"/>）。
        /// <para>
        /// <b>≤ 0 按 1 处理（fail-closed）</b>：上限为 0/负数是配置事故，按 1 处理意味着"一页只回一行"
        /// ——仍然能翻页、仍然能定位自己，而不是"永远空页"或"不设上限把后端打爆"
        /// （与 <see cref="FriendService"/> 的负配额口径一致）。上限只可能被夹小、绝不放大。
        /// </para>
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="server"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public LeaderboardPager(ILeaderboardServer server, int maxLimit = DefaultMaxLimit)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _maxLimit = maxLimit < 1 ? 1 : maxLimit;
        }

        /// <summary>
        /// 排行榜后端（只读暴露）：赛季切换与"当前赛季"读数不在本类包装范围内，
        /// 装配入口可经此直接访问，避免本类为了转发而重复一遍 <see cref="ILeaderboardServer"/> 的公开面。
        /// </summary>
        public ILeaderboardServer Server
        {
            get { return _server; }
        }

        /// <summary>实际生效的单页上限（≥ 1；构造时已夹紧）。</summary>
        public int MaxLimit
        {
            get { return _maxLimit; }
        }

        /// <summary>
        /// 归一化并请求一页：<c>offset</c> 夹到 ≥ 0、<c>limit</c> 夹到 <c>[1, MaxLimit]</c>、
        /// <c>leaderboardId ≤ 0</c> 直接返回空页（<see cref="ReasonInvalidLeaderboard"/>，不触达后端）、
        /// 后端不可用返回空页（<see cref="ReasonUnavailable"/>）。<b>永不抛异常</b>。
        /// <para>
        /// <b><see cref="LeaderboardPage.Offset"/> 用请求侧的归一化值，<see cref="LeaderboardPage.Limit"/>
        /// 用"服务端实际应用"的值</b>：服务端有权把上限夹得比请求更小（例如它自己的上限是 50 而我们请求 100），
        /// 这时若仍回显 100，调用方按 <c>offset += Limit</c> 翻页就会<b>跳过</b> 50 条
        /// （排行榜分页最隐蔽的一类错：不报错、只是少了一批人）。因此本页 Limit 取
        /// <c>min(请求 limit, 服务端回显 limit)</c>，服务端回显 0/负数（违约）时退回请求值。
        /// 总人数、行、selfRow、原因则原样透传服务端（它们才是数据）。
        /// </para>
        /// <para>
        /// 后端实现违约返回 <c>null</c> 行数组或负总人数时，由 <see cref="LeaderboardPage"/> 的构造期
        /// 归一化兜底（空数组 / 0），本方法不需要额外判空。
        /// </para>
        /// <para>未知赛季枚举值（例如反序列化出的 <c>7</c>）按当前季处理：归档数据不该被一个坏字节读出来。</para>
        /// </summary>
        /// <param name="request">原始请求（非法值由本方法归一化，不校验构造）。</param>
        /// <returns>一页快照（永不为 <c>null</c>；失败为空页 + 稳定 reason）。</returns>
        public LeaderboardPage Fetch(in LeaderboardPageRequest request)
        {
            int offset = request.Offset < 0 ? 0 : request.Offset;
            int limit = ClampLimit(request.Limit);
            LeaderboardSeason season = NormalizeSeason(request.Season);

            if (request.LeaderboardId <= 0)
            {
                return EmptyPage(request.LeaderboardId, season, offset, limit, ReasonInvalidLeaderboard);
            }

            if (!SafeIsAvailable())
            {
                return EmptyPage(request.LeaderboardId, season, offset, limit, ReasonUnavailable);
            }

            LeaderboardPage page = RequestPage(
                request.LeaderboardId, season, offset, limit, request.PlayerId, out bool failed);
            if (failed)
            {
                // RequestPage 已经把这页建成了带异常类型名的空页，直接透出（含 offset/limit）。
                return page;
            }

            return new LeaderboardPage(
                request.LeaderboardId,
                season,
                page.TotalCount,
                offset,
                EffectiveLimit(page, limit),
                page.Rows,
                page.SelfRow,
                page.Reason);
        }

        /// <summary>
        /// 取玩家自己所在页（"我的排名"入口）：以玩家名次为中心，返回最多 <c>2 × radius + 1</c> 条的一页。
        /// <para>
        /// <b>为什么不是"先查名次再查页"两条独立命令</b>：名次与页内容之间会漂移（别的玩家刚好提交），
        /// 两条命令拿到的 rank 与 rows 可能对不上，UI 就会显示"第 37 名"却在一屏第 40 名之后的行里找不到自己。
        /// 本方法返回的页里 <see cref="LeaderboardPage.SelfRow"/> 与 <see cref="LeaderboardPage.Rows"/> 来自
        /// <b>同一次</b>服务端应答，二者必然自洽。
        /// </para>
        /// <para>
        /// <b>请求流程（最多 2 次请求，不会打出无界翻页）</b>：
        /// <list type="number">
        /// <item>取 <c>offset = 0</c>、<c>limit = 2 × radius + 1</c> 的首页；<b>本页能看到自己就直接返回</b>
        /// （窗口覆盖榜首起的 <c>2 × radius + 1</c> 名，足够画出一屏带前文的"我的排名"卡片，
        /// 这也是"排名靠前的玩家"最常见的情形）。</item>
        /// <item>看不到自己、但服务端给了 <see cref="LeaderboardPage.SelfRow"/>（有名次）：
        /// 以 <c>offset = rank - radius</c> 再取一次（<b>公式按契约固定</b>，rank 是 1 起，
        /// 因此自己落在本页下标 <c>radius - 1</c> 处），命中就返回，仍看不到则返回空页
        /// （<see cref="ReasonNotRanked"/>）——服务端说自己在第 N 名却两次都给不出那一行，
        /// 属于服务端自相矛盾，宁可显示"暂无排名"也不凭空造一行。</item>
        /// <item>看不到自己、且服务端不填 <c>SelfRow</c>（该后端不支持"我的排名"）：名次无从得知，
        /// <c>rank - radius</c> 公式退化不可用，改为<b>向下一页线性探测一次</b>
        /// （<c>offset = 首页条数</c>）；命中就返回并补一份 selfRow，仍没有则空页。
        /// 这是本实现对契约的显式解释：有界探测只救得回"差一页"的中游玩家，
        /// 更深的玩家在无 selfRow 的后端上无法定位——发现这种后端应推动它实现 selfRow，
        /// 而不是让客户端无限翻页。</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>永远读当前季</b>：上一季是只读归档，"我的排名"要看的是还能继续刷的那个榜；
        /// 想看历史成绩请用 <see cref="Fetch"/> + <see cref="LeaderboardSeason.Previous"/>。
        /// </para>
        /// <para>
        /// <b>翻页以服务端实际应用的上限为准</b>：服务端可能把 <c>limit</c> 夹得比请求更小（它有权这么做），
        /// 此时本页的 <see cref="LeaderboardPage.Limit"/> 取"请求上限与服务端回显上限的较小值"，
        /// 线性探测的起点也用首页<b>实际返回条数</b>而不是请求窗口，避免"按请求窗口推进"跳行。
        /// </para>
        /// <para>
        /// <b><paramref name="radius"/> 夹到 <c>[1, MaxLimit]</c></b>：0/负数是"只想看自己一行"的写法，
        /// 但一页只回一行会让上下文的相邻名次全部丢失（UI 画不出前后各是谁），因此按 1 处理。
        /// </para>
        /// </summary>
        /// <param name="leaderboardId">榜单 id（≤ 0 返回空页 + <see cref="ReasonInvalidLeaderboard"/>，不触达后端）。</param>
        /// <param name="playerId">玩家 id（≤ 0 允许；由后端按 0 处理，本类不抛）。</param>
        /// <param name="radius">自己上下各取多少名（夹到 <c>[1, MaxLimit]</c>）。</param>
        /// <returns>以自己为中心的页（失败/不在榜为空页；<b>永不抛异常</b>）。</returns>
        public LeaderboardPage FetchAroundSelf(int leaderboardId, long playerId, int radius)
        {
            int clampedRadius = ClampRadius(radius);
            int window = WindowFor(clampedRadius);
            const LeaderboardSeason season = LeaderboardSeason.Current;

            if (leaderboardId <= 0)
            {
                return EmptyPage(leaderboardId, season, 0, window, ReasonInvalidLeaderboard);
            }

            if (!SafeIsAvailable())
            {
                return EmptyPage(leaderboardId, season, 0, window, ReasonUnavailable);
            }

            LeaderboardPage first = RequestPage(leaderboardId, season, 0, window, playerId, out bool failed);
            if (failed)
            {
                return first;
            }

            if (TryFindRow(first.Rows, playerId, out LeaderboardRow found))
            {
                return Rebuild(first, leaderboardId, season, 0, window, in found);
            }

            if (first.SelfRow.HasValue && first.SelfRow.Value.Rank > 0)
            {
                int centeredOffset = first.SelfRow.Value.Rank - clampedRadius;
                if (centeredOffset < 0)
                {
                    centeredOffset = 0;
                }

                LeaderboardPage centered = RequestPage(
                    leaderboardId, season, centeredOffset, window, playerId, out failed);
                if (failed)
                {
                    return centered;
                }

                if (TryFindRow(centered.Rows, playerId, out found))
                {
                    return Rebuild(centered, leaderboardId, season, centeredOffset, window, in found);
                }

                return EmptyPage(
                    leaderboardId, season, centeredOffset, window, ReasonNotRanked, centered.TotalCount);
            }

            // 服务端不填 selfRow：只能有界线性探测一次（见方法注释第 3 条）。
            // 探测起点用首页"实际返回条数"而不是请求窗口：服务端可能把上限夹小，按窗口推进会跳行。
            int probedOffset = first.Rows.Length;
            if (probedOffset > 0 && probedOffset >= EffectiveLimit(first, window))
            {
                LeaderboardPage probed = RequestPage(
                    leaderboardId, season, probedOffset, window, playerId, out failed);
                if (failed)
                {
                    return probed;
                }

                if (TryFindRow(probed.Rows, playerId, out found))
                {
                    return Rebuild(probed, leaderboardId, season, probedOffset, window, in found);
                }
            }

            return EmptyPage(leaderboardId, season, 0, window, ReasonNotRanked, first.TotalCount);
        }

        /// <summary>
        /// 提交分数，返回是否刷新了个人最好成绩（服务端拒绝 / 不可用 / 违约抛异常一律 <c>false</c>）。
        /// <para>
        /// <b>负分不进后端</b>：负分本身是合法成绩（见 <see cref="LeaderboardRow.Score"/>），
        /// 但"提交一个负数"几乎一定是调用方传错了（未初始化的分数变量、把扣分当成得分），
        /// 真放进榜里会让 UI 出现"-2147483648 分"这种无法解释的行。因此本方法在本地直接拒绝负分
        /// ——这是<b>调用方编程错误的一道本地闸</b>，不是服务端规则；真实成绩的负数校验以服务端为准。
        /// </para>
        /// <para>
        /// <b>榜单 id ≤ 0 不进后端</b>：与 <see cref="Fetch"/> 同一口径，非法 id 不该换来一次网络往返。
        /// </para>
        /// <para>
        /// <b><paramref name="playerId"/> 不做校验</b>：≤ 0 的 id 由后端处理（假实现归一为 0），
        /// 本类不因为一个奇怪的 id 就把提交打回去——归属判定是服务端的事
        /// （与 <see cref="FetchAroundSelf"/> 的"非正 playerId 允许"口径一致）。
        /// </para>
        /// </summary>
        /// <param name="leaderboardId">榜单 id（≤ 0 直接 <c>false</c>）。</param>
        /// <param name="playerId">玩家 id（≤ 0 允许，交由后端处理）。</param>
        /// <param name="score">本次成绩（负分直接 <c>false</c>，不进后端）。</param>
        /// <returns>是否刷新了个人最好成绩（<b>永不抛异常</b>）。</returns>
        public bool TrySubmit(int leaderboardId, long playerId, int score)
        {
            if (leaderboardId <= 0 || score < 0)
            {
                return false;
            }

            if (!SafeIsAvailable())
            {
                return false;
            }

            try
            {
                return _server.TrySubmit(leaderboardId, playerId, score);
            }
            catch (Exception)
            {
                // 契约要求后端不抛；实现违约时按"这次没提交成功"处理，绝不让一次榜单抖动打断结算。
                return false;
            }
        }

        /// <summary>
        /// 榜单行的<b>唯一</b>排序规则：分数降序；同分按玩家 id 升序（名次由调用方在排序后按序数给出）。
        /// <para>
        /// <b>同分按玩家 id，而不是按提交时间</b>：提交时间会被网络重试、补传、服务端时区漂移改写，
        /// 用它裁决同分会让"同一个榜两次查询顺序不同"变成玄学 bug；玩家 id 稳定且唯一，
        /// 任何一端重算都得到同一结果。
        /// </para>
        /// <para>
        /// <b>为什么公开成静态方法</b>：假后端、Console 假服务、Unity 本地兜底榜都要产出"跨端一致"的顺序，
        /// 各写一份比较器必然漂移（最常见的是有人按提交时间补了一条 tie-break）。把规则收敛到一处，
        /// 谁实现后端都复用这里，测试也能直接钉死这条规则本身。
        /// </para>
        /// <para><see cref="LeaderboardRow.Rank"/> 不参与比较（它是排序的结果，不是输入）。</para>
        /// </summary>
        /// <param name="left">左行。</param>
        /// <param name="right">右行。</param>
        /// <returns>负数 = <paramref name="left"/> 排在前面；0 = 两者等价（同分同 id，正常榜单不会出现）。</returns>
        public static int CompareRows(in LeaderboardRow left, in LeaderboardRow right)
        {
            if (left.Score != right.Score)
            {
                // 降序：分高的在前。
                return left.Score > right.Score ? -1 : 1;
            }

            if (left.PlayerId != right.PlayerId)
            {
                // 同分升序：id 小的在前（可复现，见方法注释）。
                return left.PlayerId < right.PlayerId ? -1 : 1;
            }

            return 0;
        }

        /// <summary>把单页上限夹到 <c>[1, MaxLimit]</c>（0/负数是"只想看一行"的写法，按 1 处理）。</summary>
        /// <param name="limit">原始 limit。</param>
        /// <returns>归一化后的 limit。</returns>
        private int ClampLimit(int limit)
        {
            if (limit < 1)
            {
                return 1;
            }

            return limit > _maxLimit ? _maxLimit : limit;
        }

        /// <summary>把 radius 夹到 <c>[1, MaxLimit]</c>（理由见 <see cref="FetchAroundSelf"/>）。</summary>
        /// <param name="radius">原始 radius。</param>
        /// <returns>归一化后的 radius。</returns>
        private int ClampRadius(int radius)
        {
            if (radius < 1)
            {
                return 1;
            }

            return radius > _maxLimit ? _maxLimit : radius;
        }

        /// <summary>
        /// 以自己为中心的一页最多几条 = <c>2 × radius + 1</c>，再被 <see cref="MaxLimit"/> 夹住。
        /// <para>用 <c>long</c> 算中间值：<c>radius</c> 可以等于 <see cref="MaxLimit"/>（可达 <c>int.MaxValue</c>），
        /// <c>2 × radius + 1</c> 在 <c>int</c> 上会静默溢出成负数。</para>
        /// </summary>
        /// <param name="radius">已夹紧的 radius（≥ 1）。</param>
        /// <returns>本页条数上限（≥ 1）。</returns>
        private int WindowFor(int radius)
        {
            long window = (2L * radius) + 1L;
            return window > _maxLimit ? _maxLimit : (int)window;
        }

        /// <summary>
        /// 未知赛季枚举值按当前季处理（归档数据不该被一个坏字节读出来）；已知值原样透传。
        /// </summary>
        /// <param name="season">原始赛季视图。</param>
        /// <returns>归一化后的赛季视图。</returns>
        private static LeaderboardSeason NormalizeSeason(LeaderboardSeason season)
        {
            return season == LeaderboardSeason.Previous ? LeaderboardSeason.Previous : LeaderboardSeason.Current;
        }

        /// <summary>
        /// 向后端要一页（含异常兜底）。
        /// <para>
        /// 失败时返回的是<b>带稳定 reason 的空页</b>（已经带上本次 offset/limit），
        /// 调用方无需再判断一次异常；<paramref name="failed"/> 只是为了让两步流程能立刻中止。
        /// </para>
        /// </summary>
        /// <param name="leaderboardId">榜单 id（调用方已保证 &gt; 0）。</param>
        /// <param name="season">已归一化的赛季。</param>
        /// <param name="offset">已归一化的起始下标。</param>
        /// <param name="limit">已归一化的条数上限。</param>
        /// <param name="playerId">玩家 id（原样透传）。</param>
        /// <param name="failed">是否因后端违约抛异常而失败。</param>
        /// <returns>服务端返回的页，或失败空页。</returns>
        private LeaderboardPage RequestPage(
            int leaderboardId,
            LeaderboardSeason season,
            int offset,
            int limit,
            long playerId,
            out bool failed)
        {
            var request = new LeaderboardPageRequest(leaderboardId, season, offset, limit, playerId);
            try
            {
                failed = false;
                return _server.FetchPage(in request);
            }
            catch (Exception ex)
            {
                failed = true;
                return EmptyPage(
                    leaderboardId,
                    season,
                    offset,
                    limit,
                    ReasonServiceThrew + ": " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// 在已取回的一页里线性找自己（"服务端不填 selfRow"时的退化路径）。
        /// <para>只扫本页 <c>≤ MaxLimit</c> 行，不做任何分配。</para>
        /// </summary>
        /// <param name="rows">本页的行（永不为 <c>null</c>）。</param>
        /// <param name="playerId">玩家 id（原样比较，不做归一化）。</param>
        /// <param name="row">找到的行。</param>
        /// <returns>是否找到。</returns>
        private static bool TryFindRow(LeaderboardRow[] rows, long playerId, out LeaderboardRow row)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].PlayerId == playerId)
                {
                    row = rows[i];
                    return true;
                }
            }

            row = default;
            return false;
        }

        /// <summary>
        /// 用"实际请求的 offset、服务端实际应用的 limit"重建一页，并把本页定位到的行当作
        /// <see cref="LeaderboardPage.SelfRow"/>。
        /// <para>
        /// <b>以定位到的行为准而不是服务端的 selfRow</b>：二者理论上一致，不一致时本页的行才是
        /// UI 真正会画出来的内容，让 selfRow 跟着行走可以保证"卡片上的名次"与"列表里的位置"不打架。
        /// </para>
        /// </summary>
        /// <param name="page">服务端原始应答（提供总人数、行、原因、实际上限）。</param>
        /// <param name="leaderboardId">榜单 id。</param>
        /// <param name="season">赛季视图。</param>
        /// <param name="offset">本次实际请求的起始下标。</param>
        /// <param name="requestedLimit">本次请求的条数上限（服务端实际上限比它小时以服务端为准）。</param>
        /// <param name="found">本页定位到的自己那一行。</param>
        /// <returns>重建后的页。</returns>
        private static LeaderboardPage Rebuild(
            in LeaderboardPage page,
            int leaderboardId,
            LeaderboardSeason season,
            int offset,
            int requestedLimit,
            in LeaderboardRow found)
        {
            return new LeaderboardPage(
                leaderboardId,
                season,
                page.TotalCount,
                offset,
                EffectiveLimit(page, requestedLimit),
                page.Rows,
                found,
                page.Reason);
        }

        /// <summary>
        /// 本页"实际生效的条数上限" = <c>min(请求上限, 服务端回显上限)</c>。
        /// <para>
        /// <b>为什么不能一律回显请求值</b>：服务端把上限夹得比请求更小时（它有权这么做），
        /// 调用方按 <c>offset += Limit</c> 翻页会静默跳过一批人——这类错不报异常，只是榜单少了几行。
        /// </para>
        /// <para>
        /// <b>为什么也不能一律信服务端</b>：违约实现可能回显一个比请求更大的上限（甚至负数），
        /// 直接采信又会让调用方一次跳过头。取两者较小值，并对非正值退回请求值，
        /// 是唯一同时挡住这两类错的读法。
        /// </para>
        /// </summary>
        /// <param name="page">服务端应答。</param>
        /// <param name="requestedLimit">请求侧已归一化的上限（≥ 1）。</param>
        /// <returns>实际生效的条数上限（≥ 1）。</returns>
        private static int EffectiveLimit(in LeaderboardPage page, int requestedLimit)
        {
            int reported = page.Limit;
            if (reported < 1 || reported > requestedLimit)
            {
                return requestedLimit;
            }

            return reported;
        }

        /// <summary>
        /// 构造一页空榜（失败/不在榜的所有出口都走这里，保证 offset/limit/总人数口径一致）。
        /// </summary>
        /// <param name="leaderboardId">榜单 id。</param>
        /// <param name="season">赛季视图。</param>
        /// <param name="offset">起始下标。</param>
        /// <param name="limit">条数上限。</param>
        /// <param name="reason">诊断原因（必须是本类的公开 reason 常量之一）。</param>
        /// <param name="totalCount">榜上总人数（能确定时透出服务端值，否则 0）。</param>
        /// <returns>空页。</returns>
        private static LeaderboardPage EmptyPage(
            int leaderboardId,
            LeaderboardSeason season,
            int offset,
            int limit,
            string reason,
            int totalCount = 0)
        {
            return new LeaderboardPage(
                leaderboardId,
                season,
                totalCount,
                offset,
                limit,
                Array.Empty<LeaderboardRow>(),
                null,
                reason);
        }

        /// <summary>读后端可用性；实现违约抛异常时按"不可用"处理（降级总比崩溃好）。</summary>
        /// <returns>后端是否可用。</returns>
        private bool SafeIsAvailable()
        {
            try
            {
                return _server.IsAvailable;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
