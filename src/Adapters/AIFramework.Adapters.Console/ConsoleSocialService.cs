#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 控制台社交服务：<b>内存假社交后端</b>，用来在无头环境验收 <see cref="ISocialService"/> 的全部语义
    /// （好友列表 / 赠送 / 领取 / 每人每天一次 / 跨日重置 / 不可用降级 / 一键领取）。
    /// <para>
    /// <b>它不是空实现</b>：它维护一份"服务端好友台账"——固定的 <c>friend-1..N</c> 与"每个好友每天可送 1 次、
    /// 可领 1 次"的标记。真实后端（微信/QQ/自建社交服）只是把这些标记换成服务端存储 + 关系校验，
    /// 语义必须与这里完全一致——否则无头测试绿、真机上"送过的还能再送"就直接变成体力通胀。
    /// </para>
    /// <para>
    /// <b>为什么每日标记由本类（后端侧）持有，而不是交给 <see cref="FriendService"/></b>：
    /// "这个好友今天被送过没有"是服务端事实，客户端记账可以被改时间/重装绕过。
    /// 因此假后端自己按 UTC 自然日重置，业务层只如实透传它的回答。
    /// </para>
    /// <para>
    /// <b>两套跨日入口</b>：
    /// <list type="bullet">
    /// <item>注入了 <see cref="ITimeProvider"/> 时，每次操作前按 UTC 日序号自动跨日（可测的"自然跨日"）；</item>
    /// <item><see cref="ExpireDaily"/> 强制清空当日标记，让"上线测试/冒烟脚本"不依赖等待或拨时钟；
    /// 没有时钟（<c>time == null</c>）时这是唯一能模拟跨日的手段。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>日志是排空的环形缓冲</b>：只服务冒烟与排障（<see cref="DrainLog"/> 取走即清空），
    /// 不参与业务判定，因此允许在这里拼字符串（正式逻辑路径禁止动态拼接）。
    /// </para>
    /// <para>线程模型：非线程安全。好友列表/赠送/领取都在主线程串行调用（社交不是高频路径）。</para>
    /// </summary>
    public sealed class ConsoleSocialService : ISocialService
    {
        /// <summary>日志环形容量（最近 32 条）。</summary>
        public const int LogCapacity = 32;

        /// <summary>默认好友数（3 人，够覆盖"全领/单人领/重复领"三种路径）。</summary>
        public const int DefaultFriendCount = 3;

        /// <summary>好友玩家 id 前缀（<c>friend-1..N</c>）。</summary>
        public const string FriendIdPrefix = "friend-";

        /// <summary>好友展示名前缀（<c>好友1..N</c>）。</summary>
        public const string FriendNamePrefix = "好友";

        /// <summary>一次赠送的体力数量（假实现固定值；真实数值由后端配置下发）。</summary>
        public const int GiftStaminaAmount = 10;

        /// <summary>一次领取的体力数量（假实现固定值；真实数值由后端配置下发）。</summary>
        public const int ClaimStaminaAmount = 10;

        /// <summary>失败原因：构造/<see cref="SetUnavailable"/> 配置为不可用。</summary>
        public const string ReasonUnavailable = "social service unavailable";

        /// <summary>失败原因：今天已经给该好友送过。</summary>
        public const string ReasonAlreadyGifted = "already gifted today";

        /// <summary>失败原因：今天已经从该好友领过。</summary>
        public const string ReasonAlreadyClaimed = "already claimed today";

        /// <summary>失败原因：该玩家不是好友（或 id 为空）。</summary>
        public const string ReasonNotFriend = "not a friend";

        /// <summary>成功原因：赠送成功。</summary>
        public const string ReasonGifted = "gift sent";

        /// <summary>成功原因：领取成功。</summary>
        public const string ReasonClaimed = "stamina claimed";

        /// <summary>相邻好友的"最近活跃"间隔（秒），只用于让列表看起来不像同一时刻。</summary>
        private const int LastActiveStepSeconds = 300;

        /// <summary>好友玩家 id（下标即内部索引）。</summary>
        private readonly string[] _friendIds;

        /// <summary>好友展示名（与 <see cref="_friendIds"/> 同下标）。</summary>
        private readonly string[] _friendNames;

        /// <summary>当日已赠送标记（<c>true</c> = 今天已给该好友送过）。</summary>
        private readonly bool[] _giftedToday;

        /// <summary>当日已领取标记（<c>true</c> = 今天已从该好友领过）。</summary>
        private readonly bool[] _claimedToday;

        /// <summary>可选时间源（<c>null</c> = 永不自动跨日，只能靠 <see cref="ExpireDaily"/>）。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>是否可用（可被 <see cref="SetUnavailable"/> / <see cref="SetAvailable"/> 拨动）。</summary>
        private bool _available;

        /// <summary>当前标记所属的 UTC 天序号。</summary>
        private int _dayIndex;

        /// <summary>日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置；满员时指向最旧的一条。</summary>
        private int _logNext;

        /// <summary>创建控制台社交服务。</summary>
        /// <param name="time">
        /// 可选时间源（用于 UTC 自然日跨日与好友"最近活跃"时间；<c>null</c> 时固定第 0 日、最近活跃记 0）。
        /// </param>
        /// <param name="available">是否可用（默认 <c>true</c>；验收离线降级时传 <c>false</c> 或用 <see cref="SetUnavailable"/>）。</param>
        /// <param name="friendCount">好友数量（负值按 0 处理，即空列表）。</param>
        public ConsoleSocialService(ITimeProvider? time = null, bool available = true, int friendCount = DefaultFriendCount)
        {
            _time = time;
            _available = available;

            int count = friendCount < 0 ? 0 : friendCount;
            _friendIds = new string[count];
            _friendNames = new string[count];
            _giftedToday = new bool[count];
            _claimedToday = new bool[count];

            for (int i = 0; i < count; i++)
            {
                int sequence = i + 1;
                _friendIds[i] = FriendIdPrefix + sequence.ToString(CultureInfo.InvariantCulture);
                _friendNames[i] = FriendNamePrefix + sequence.ToString(CultureInfo.InvariantCulture);
            }

            _dayIndex = TodayIndex();
        }

        /// <inheritdoc />
        public bool IsAvailable
        {
            get { return _available; }
        }

        /// <inheritdoc />
        /// <remarks>不可用时为 0，与 <see cref="GetFriends"/> 的空数组保持一致。</remarks>
        public int FriendCount
        {
            get { return _available ? _friendIds.Length : 0; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 每次返回新数组（改数组不影响内部状态）。不可用时返回空数组，<b>不抛异常</b>。
        /// <see cref="FriendInfo.LastActiveUnixSeconds"/> 以"当前时间 - 序号 × 5 分钟"填充，
        /// 只为让列表具备可断言的排序位次，不代表真实在线状态。
        /// </remarks>
        public FriendInfo[] GetFriends()
        {
            EnsureDay();

            if (!_available)
            {
                return Array.Empty<FriendInfo>();
            }

            int now = NowUnixSeconds();
            var friends = new FriendInfo[_friendIds.Length];

            for (int i = 0; i < friends.Length; i++)
            {
                int lastActive = now - (i * LastActiveStepSeconds);
                if (lastActive < 0)
                {
                    lastActive = 0;
                }

                friends[i] = new FriendInfo(_friendIds[i], _friendNames[i], lastActive, !_giftedToday[i], !_claimedToday[i]);
            }

            return friends;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 每人每天 1 次；重复赠送返回 <see cref="FriendGiftResult.AlreadyGifted"/>（不重复记日志以外任何状态）。
        /// 不可用时一律 <see cref="FriendGiftResult.Unavailable"/>（连"是不是好友"都不评价：请求根本没发出去）。
        /// </remarks>
        public FriendOperationOutcome GiftStamina(string friendId)
        {
            EnsureDay();

            if (!_available)
            {
                AppendLog("gift " + Describe(friendId) + " -> Unavailable");
                return Failure(FriendGiftResult.Unavailable, ReasonUnavailable);
            }

            int index = IndexOfFriend(friendId);
            if (index < 0)
            {
                AppendLog("gift " + Describe(friendId) + " -> NotFriend");
                return Failure(FriendGiftResult.NotFriend, ReasonNotFriend);
            }

            if (_giftedToday[index])
            {
                AppendLog("gift " + _friendIds[index] + " -> AlreadyGifted");
                return Failure(FriendGiftResult.AlreadyGifted, ReasonAlreadyGifted);
            }

            _giftedToday[index] = true;
            AppendLog("gift " + _friendIds[index] + " -> Success(+" + GiftStaminaAmount.ToString(CultureInfo.InvariantCulture) + ")");
            return new FriendOperationOutcome(true, FriendGiftResult.Success, GiftStaminaAmount, ReasonGifted);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 逐个结算所有"今日未领"的好友并累加数量；没有可领的返回 0（合法结果，不记错误日志）。
        /// 不可用时返回 0 且不改任何标记。
        /// </remarks>
        public int ClaimAllStamina()
        {
            EnsureDay();

            if (!_available)
            {
                AppendLog("claim-all -> Unavailable");
                return 0;
            }

            int claimed = 0;
            for (int i = 0; i < _claimedToday.Length; i++)
            {
                if (_claimedToday[i])
                {
                    continue;
                }

                _claimedToday[i] = true;
                claimed++;
            }

            if (claimed == 0)
            {
                AppendLog("claim-all -> nothing to claim");
                return 0;
            }

            int total = claimed * ClaimStaminaAmount;
            AppendLog("claim-all -> " + claimed.ToString(CultureInfo.InvariantCulture)
                + " friends, +" + total.ToString(CultureInfo.InvariantCulture));
            return total;
        }

        /// <inheritdoc />
        /// <remarks>
        /// 每人每天 1 次；重复领取返回 <see cref="FriendGiftResult.AlreadyGifted"/>（语义为"今日已领"，
        /// 枚举值复用见 <see cref="FriendGiftResult"/> 文档）。
        /// </remarks>
        public FriendOperationOutcome ClaimStamina(string friendId)
        {
            EnsureDay();

            if (!_available)
            {
                AppendLog("claim " + Describe(friendId) + " -> Unavailable");
                return Failure(FriendGiftResult.Unavailable, ReasonUnavailable);
            }

            int index = IndexOfFriend(friendId);
            if (index < 0)
            {
                AppendLog("claim " + Describe(friendId) + " -> NotFriend");
                return Failure(FriendGiftResult.NotFriend, ReasonNotFriend);
            }

            if (_claimedToday[index])
            {
                AppendLog("claim " + _friendIds[index] + " -> AlreadyGifted");
                return Failure(FriendGiftResult.AlreadyGifted, ReasonAlreadyClaimed);
            }

            _claimedToday[index] = true;
            AppendLog("claim " + _friendIds[index] + " -> Success(+" + ClaimStaminaAmount.ToString(CultureInfo.InvariantCulture) + ")");
            return new FriendOperationOutcome(true, FriendGiftResult.Success, ClaimStaminaAmount, ReasonClaimed);
        }

        /// <inheritdoc />
        /// <remarks>按 UTC 日序号结算跨日；同一天内重复调用只记日志、不改标记。</remarks>
        public void Refresh()
        {
            EnsureDay();
            AppendLog("refresh(day " + _dayIndex.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>把后端切成"不可用"（模拟断网 / SDK 未接线）。已建立的标记不受影响。</summary>
        public void SetUnavailable()
        {
            if (_available)
            {
                _available = false;
                AppendLog("available -> false");
            }
        }

        /// <summary>把后端切回"可用"（模拟网络恢复 / SDK 初始化完成）。</summary>
        public void SetAvailable()
        {
            if (!_available)
            {
                _available = true;
                AppendLog("available -> true");
            }
        }

        /// <summary>
        /// 强制清空当日赠送/领取标记（模拟"过了一个自然日"），让冒烟与上线测试不依赖等待或拨时钟。
        /// <para>
        /// 它与"按时间自动跨日"共用同一份标记：清空后所有人都恢复成"今天可送、可领"。
        /// <b>不删除任何持久化数据</b>（本实现本来就不落盘）。
        /// </para>
        /// </summary>
        public void ExpireDaily()
        {
            Array.Clear(_giftedToday, 0, _giftedToday.Length);
            Array.Clear(_claimedToday, 0, _claimedToday.Length);
            AppendLog("daily state expired (forced)");
        }

        /// <summary>取走并清空日志（冒烟/排障用；取走即清空，避免同一批事件被重复打印）。</summary>
        /// <returns>按发生顺序排列的日志条目。</returns>
        public IReadOnlyList<string> DrainLog()
        {
            var entries = new List<string>(_logCount);
            int start = _logCount < LogCapacity ? 0 : _logNext;
            for (int i = 0; i < _logCount; i++)
            {
                int index = start + i;
                if (index >= LogCapacity)
                {
                    index -= LogCapacity;
                }

                entries.Add(_log[index]);
            }

            Array.Clear(_log, 0, _log.Length);
            _logCount = 0;
            _logNext = 0;
            return entries;
        }

        /// <summary>若已跨 UTC 自然日则清空当日标记（只由操作入口调用，保证读接口语义简单）。</summary>
        private void EnsureDay()
        {
            int today = TodayIndex();
            if (today == _dayIndex)
            {
                return;
            }

            _dayIndex = today;
            Array.Clear(_giftedToday, 0, _giftedToday.Length);
            Array.Clear(_claimedToday, 0, _claimedToday.Length);
            AppendLog("daily reset(day " + today.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>
        /// 今天的 UTC 日序号；无时钟或时钟违约时返回当前标记所属日（即"不跨日"）。
        /// <para>方向刻意 fail-closed：跨日判定失败时保留旧标记，宁可少送一次，也不放行"今天已送过的"。</para>
        /// </summary>
        /// <returns>UTC 日序号。</returns>
        private int TodayIndex()
        {
            ITimeProvider? time = _time;
            if (time == null)
            {
                return 0;
            }

            try
            {
                return FriendService.ToUtcDayIndex(time.UtcNow);
            }
            catch (Exception)
            {
                return _dayIndex;
            }
        }

        /// <summary>按 id 找好友下标（找不到返回 -1；空 id 直接视为没有）。</summary>
        /// <param name="friendId">好友玩家 id。</param>
        /// <returns>内部下标；-1 = 不是好友。</returns>
        private int IndexOfFriend(string friendId)
        {
            if (string.IsNullOrEmpty(friendId))
            {
                return -1;
            }

            return Array.IndexOf(_friendIds, friendId);
        }

        /// <summary>当前 UTC 时间的 Unix 秒（无时钟/违约时 0）；超出 <c>int</c> 上限时夹住（契约字段是 int）。</summary>
        /// <returns>Unix 秒。</returns>
        private int NowUnixSeconds()
        {
            ITimeProvider? time = _time;
            if (time == null)
            {
                return 0;
            }

            long seconds;
            try
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                seconds = (long)(time.UtcNow - epoch).TotalSeconds;
            }
            catch (Exception)
            {
                return 0;
            }

            if (seconds <= 0)
            {
                return 0;
            }

            return seconds > int.MaxValue ? int.MaxValue : (int)seconds;
        }

        /// <summary>日志里描述一个 id（区分 <c>null</c> 与空串，便于定位"UI 传了 null"这类事故）。</summary>
        /// <param name="friendId">好友 id。</param>
        /// <returns>可读描述。</returns>
        private static string Describe(string friendId)
        {
            if (friendId == null)
            {
                return "<null>";
            }

            return friendId.Length == 0 ? "<empty>" : friendId;
        }

        /// <summary>构造一个失败结果（数量为 0）。</summary>
        /// <param name="result">失败结果码。</param>
        /// <param name="reason">诊断原因。</param>
        /// <returns>失败结果。</returns>
        private static FriendOperationOutcome Failure(FriendGiftResult result, string reason)
        {
            return new FriendOperationOutcome(false, result, 0, reason);
        }

        /// <summary>写入一条日志；满员时覆盖最旧的一条（仅诊断路径，允许分配）。</summary>
        /// <param name="entry">日志文本。</param>
        private void AppendLog(string entry)
        {
            _log[_logNext] = entry;

            _logNext++;
            if (_logNext == LogCapacity)
            {
                _logNext = 0;
            }

            if (_logCount < LogCapacity)
            {
                _logCount++;
            }
        }
    }
}
