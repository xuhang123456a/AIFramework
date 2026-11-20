#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 一个好友的只读快照：玩家 id + 展示名 + 最近活跃时间 + "今天还能不能被送/被领"。
    /// <para>
    /// <b>为什么是快照而不是好友对象</b>：好友关系归服务端所有，客户端只应该看到"这一刻列表长什么样"。
    /// 暴露可变对象，UI 就能改本地状态并假装服务端已经同意（例如把 <see cref="CanGift"/> 改成 <c>true</c>
    /// 再送一次），这是社交系统最典型的作弊面。因此 <see cref="ISocialService.GetFriends"/> 每次返回
    /// <b>新数组</b>，调用方改数组不会影响服务内部状态（与 <c>LeaderboardTable</c> 的"只读副本"口径一致）。
    /// </para>
    /// <para>
    /// <b><see cref="PlayerId"/> 是唯一键</b>：赠送/领取都按它定位。<see cref="DisplayName"/> 来自对方昵称，
    /// 会变、会重名、可能含表情与敏感词，<b>禁止</b>用于任何归属判定（与 <see cref="AccountInfo.PlayerId"/>
    /// 同一约定）。
    /// </para>
    /// <para>
    /// <b>字符串在构造期归一化，读取期再兜底</b>：<c>default(FriendInfo)</c> 会绕过构造器，其字段是
    /// <c>null</c>；因此 getter 对 <c>null</c> 一律返回空串。读方永远不用判 <c>null</c>，
    /// 只需判 <see cref="PlayerId"/> 是否为空串（空串 = 没有这个好友）。
    /// </para>
    /// <para>
    /// <b><see cref="CanGift"/> / <see cref="CanClaim"/> 是"服务端口径"</b>：只表示"这个好友今天是否还能
    /// 被送/被领"，<b>不含</b>本地每日赠送总量上限——后者是体验策略，由 <c>FriendService.DailyGiftRemaining</c>
    /// 单独把关。两者必须分开："今天给这个人送过没有"是服务端事实，"今天还能总共送几次"是客户端额度。
    /// </para>
    /// </summary>
    public readonly struct FriendInfo
    {
        /// <summary>玩家 id 的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _playerId;

        /// <summary>展示名的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _displayName;

        /// <summary>构造一个好友快照。</summary>
        /// <param name="playerId">好友的玩家 id（<c>null</c> 归一化为空串；空串表示"没有这个好友"）。</param>
        /// <param name="displayName">展示名（<c>null</c> 归一化为空串；不参与归属判定）。</param>
        /// <param name="lastActiveUnixSeconds">最近活跃时间（Unix 秒；0 = 未知/长期离线）。</param>
        /// <param name="canGift">服务端是否仍允许今天给该好友赠送体力。</param>
        /// <param name="canClaim">服务端是否仍有该好友的体力可领。</param>
        public FriendInfo(string playerId, string displayName, int lastActiveUnixSeconds, bool canGift, bool canClaim)
        {
            _playerId = playerId;
            _displayName = displayName;
            LastActiveUnixSeconds = lastActiveUnixSeconds;
            CanGift = canGift;
            CanClaim = canClaim;
        }

        /// <summary>好友的玩家 id（<b>唯一键</b>；空串 = 没有这个好友；永远不为 <c>null</c>）。</summary>
        public string PlayerId
        {
            get { return _playerId ?? string.Empty; }
        }

        /// <summary>展示名（可能为空串；会变、会重名，禁止用于归属判定）。</summary>
        public string DisplayName
        {
            get { return _displayName ?? string.Empty; }
        }

        /// <summary>
        /// 最近活跃时间（Unix 秒；0 = 未知）。
        /// <para>
        /// <b>契约锁定 <c>int</c></b>（与 <see cref="AccountInfo.CreatedAtUnixSeconds"/> 的 <c>long</c> 不同）：
        /// 它只用于"是否长期离线"这类排序/文案，不参与归属与结算。代价是 2038 年溢出——
        /// 届时需要扩宽并把 0 的语义一并迁移，属于已知技术债，不是本阶段的遗漏。
        /// </para>
        /// </summary>
        public int LastActiveUnixSeconds { get; }

        /// <summary>服务端是否仍允许今天给该好友赠送体力（<c>false</c> = 今日已送）。</summary>
        public bool CanGift { get; }

        /// <summary>服务端是否仍有该好友的体力可领（<c>false</c> = 今日已领 / 无体力）。</summary>
        public bool CanClaim { get; }
    }

    /// <summary>
    /// 一次社交操作（赠送/领取）的结果码。
    /// <para>
    /// <b>为什么赠送与领取共用一个枚举</b>：两者是同一类"每人每天一次"的幂等操作，失败面完全重叠
    /// （不在好友列表、今天已经做过、后端不可用）。拆成两个枚举会让 <see cref="FriendOperationOutcome"/>
    /// 变成两个结构体，而 UI 对这两类失败的处理是同一套（按钮置灰 + 提示"今日已完成"）。
    /// 领取侧的 <see cref="AlreadyGifted"/> 语义就是"今天已经领过"（枚举值复用，含义见各成员注释）。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="Cooldown"/> 与 <see cref="AlreadyGifted"/> 必须分开</b>：前者是"时间窗口没过"
    /// （本地每日总量额度用尽、或服务端冷却中，稍后/明天可能恢复），后者是"今天对这个人已经做过了"
    /// （同样的按钮再点一百次也不会成功）。合成一个码，UI 就无法区分"明天再来"与"等 30 秒"。
    /// </para>
    /// <para>数值顺序固定（会进日志/埋点），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum FriendGiftResult : byte
    {
        /// <summary>成功（本次操作真的改变了体力）。</summary>
        Success = 0,

        /// <summary>冷却中：本地每日赠送额度用尽，或服务端对该操作有冷却窗口。稍后/跨日可重试。</summary>
        Cooldown = 1,

        /// <summary>今天已经对<b>这个好友</b>做过同类操作（赠送侧=已送；领取侧=已领）。</summary>
        AlreadyGifted = 2,

        /// <summary>不是好友（不在列表、关系已解除、或 id 根本不存在）。重试同一 id 无意义。</summary>
        NotFriend = 3,

        /// <summary>后端未接线 / 不可用 / 实现违约抛异常：可降级继续玩，不是业务拒绝。</summary>
        Unavailable = 4
    }

    /// <summary>
    /// 一次社交操作的完整结果：成败 + 结果码 + 体力数量 + 人类可读原因，<b>只读</b>。
    /// <para>
    /// <b>业务判定只看 <see cref="Result"/>（或 <see cref="Success"/>）</b>：<see cref="Reason"/> 是诊断文本，
    /// 允许各实现给不同的可读描述，禁止拿字符串比较做分支（换一版文案就会静默走错分支）。
    /// </para>
    /// <para>
    /// <b><see cref="Success"/> 取"传参与结果码同时成立"</b>：只要 <see cref="Result"/> 不是
    /// <see cref="FriendGiftResult.Success"/>，无论调用方传了什么，一律算失败。这条归一化是为了防止
    /// 违约实现"填错结果码却声称成功"，把一次被服务端拒绝的操作当成已发放——那会直接印出体力。
    /// 宁可少报成功，不可多报成功。
    /// </para>
    /// <para>
    /// <b><see cref="Amount"/> 对负值按 0 处理</b>：体力不能倒扣，脏数据不应污染调用方账本；
    /// 失败时恒为 0（没有"领到 -1"这种东西）。
    /// </para>
    /// </summary>
    public readonly struct FriendOperationOutcome
    {
        /// <summary>原因的归一化副本（读取期兜底 <c>null</c>，见类型注释）。</summary>
        private readonly string? _reason;

        /// <summary>构造一次社交操作结果。</summary>
        /// <param name="success">调用方判定是否成功（与 <paramref name="result"/> 取与，见类型注释）。</param>
        /// <param name="result">结果码（业务判定唯一依据）。</param>
        /// <param name="amount">本次涉及的体力数量（负值按 0；失败应为 0）。</param>
        /// <param name="reason">诊断原因（<c>null</c> 归一化为空串；禁止作为业务判定依据）。</param>
        public FriendOperationOutcome(bool success, FriendGiftResult result, int amount, string reason)
        {
            Success = success && result == FriendGiftResult.Success;
            Result = result;
            Amount = amount > 0 ? amount : 0;
            _reason = reason;
        }

        /// <summary>是否"本次调用真的成功"：<b>只有传参为真且 Result 为 Success</b> 才为 <c>true</c>。</summary>
        public bool Success { get; }

        /// <summary>结果码（业务判定唯一依据）。</summary>
        public FriendGiftResult Result { get; }

        /// <summary>本次涉及的体力数量（失败恒 0；永远不为负）。</summary>
        public int Amount { get; }

        /// <summary>诊断原因（可能为空串；禁止作为业务判定依据）。</summary>
        public string Reason
        {
            get { return _reason ?? string.Empty; }
        }
    }

    /// <summary>
    /// 社交后端：好友列表 / 赠送体力 / 领取体力。真实实现走社交 SDK + 服务端校验；
    /// Phase 1 由适配层假实现（<c>ConsoleSocialService</c> / <c>UnitySocialService</c>）。
    /// <para>
    /// <b>为什么整块都是同步签名</b>：契约必须能被"纯内存假社交后端"直接实现，这样好友列表、
    /// 赠送、领取、跨日重置能在无头测试与断网真机上完整验收。真实 SDK 的异步（拉列表、发包、
    /// 好友申请弹窗）由适配层用"请求 + 回调/后台等待"包一层，把结果喂进本端口。
    /// 把 <c>Task</c> 写进契约会逼纯逻辑层依赖异步调度
    /// （与 <see cref="IAccountService"/>、<see cref="IIapService"/> 同一取舍）。
    /// </para>
    /// <para>
    /// <b>本接口只管"关系与每日一次"，不管"体力账本"</b>：体力加到哪里、上限多少、怎么落盘，
    /// 由宿主/背包切片负责。本接口的 <see cref="FriendOperationOutcome.Amount"/> 只是"本次该发多少"，
    /// 不是"玩家现在有多少"。混在一起会让社交端口反向绑架经济系统。
    /// </para>
    /// <para>
    /// <b>每日一次由实现方保证</b>（每人每天可送 1 次、可领 1 次，跨 UTC 日重置）：
    /// 客户端独占地记账是不可信的（改本地时间/重装即绕过），因此这条事实必须以服务端为准；
    /// 假实现用注入的 <c>ITimeProvider</c> 的 UTC 自然日模拟它。
    /// </para>
    /// <para>
    /// <b>禁止抛异常</b>：失败必须落成 <see cref="FriendGiftResult"/>。一次社交抖动不允许把正在
    /// 游玩的玩家打断（调用方仍会兜底捕获，但那是第二道防线）。
    /// </para>
    /// <para>线程模型：实现方自行声明；框架侧按单线程串行调用（列表/赠送/领取都在主线程）。</para>
    /// </summary>
    public interface ISocialService
    {
        /// <summary>
        /// 后端当前是否可用（SDK 未初始化/未接线/断网都必须如实返回 <c>false</c>，不得"假装成功"）。
        /// <para>调用方必须先判它：不可用时一律降级（<see cref="GetFriends"/> 返回空数组、
        /// 操作返回 <see cref="FriendGiftResult.Unavailable"/>），而不是反复弹失败。</para>
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>好友数量（不可用时为 0，与 <see cref="GetFriends"/> 的空数组保持一致）。</summary>
        int FriendCount { get; }

        /// <summary>
        /// 好友快照（列表页用；<b>只读副本</b>）。
        /// <para>每次返回新数组：调用方排序/改字段都不会影响服务内部状态。不可用时返回空数组，<b>不抛异常</b>。</para>
        /// </summary>
        /// <returns>好友快照数组（永不为 <c>null</c>）。</returns>
        FriendInfo[] GetFriends();

        /// <summary>给好友送体力（每日 1 次/好友）。</summary>
        /// <param name="friendId">好友的玩家 id（空/未知 → <see cref="FriendGiftResult.NotFriend"/>）。</param>
        /// <returns>操作结果（<b>永不抛异常</b>）。</returns>
        FriendOperationOutcome GiftStamina(string friendId);

        /// <summary>一键领取全部可领体力。</summary>
        /// <returns>本次领取总量（0 = 没有可领的，合法结果；<b>永不抛异常</b>）。</returns>
        int ClaimAllStamina();

        /// <summary>领取单个好友的体力。</summary>
        /// <param name="friendId">好友的玩家 id（空/未知 → <see cref="FriendGiftResult.NotFriend"/>）。</param>
        /// <returns>操作结果（<b>永不抛异常</b>）。</returns>
        FriendOperationOutcome ClaimStamina(string friendId);

        /// <summary>刷新（重新拉列表 + 重置跨日状态）。同一天内重复调用必须幂等。</summary>
        void Refresh();
    }
}
