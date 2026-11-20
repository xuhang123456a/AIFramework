#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 侧社交服务：<b>不联网、不接任何社交 SDK</b>，用本地内存 + 可选落盘模拟一个社交后端
    /// （好友列表 / 赠送 / 领取 / 每人每天一次 / 跨日重置 / 不可用降级）。
    /// <para>
    /// <b>为什么这里没有微信/QQ 好友</b>：真实好友关系链必须由平台 SDK 授权、服务端拉取并做防作弊，
    /// 一旦写进适配层，无头测试与断网演示就都用不上这份代码。因此 Phase 1 的边界是：本类只模拟
    /// <b>社交语义</b>（好友列表、每日一次、跨日重置、领取结算），真实 SDK 由后续独立实现替换——
    /// 切过去时 <see cref="ISocialService"/> 的调用方（<see cref="FriendService"/> 与 UI）一行都不用改。
    /// </para>
    /// <para>
    /// <b><see cref="IsAvailable"/> 默认 <c>false</c></b>：没接线的后端必须如实报不可用，
    /// 让上层走"社交入口置灰、游戏照常玩"的降级路径，而不是在真机上"假装送成功"然后让玩家以为体力已到账。
    /// 演示/验收时用 <c>new UnitySocialService(storage, time, available: true)</c> 打开本地模拟。
    /// </para>
    /// <para>
    /// <b>落盘 = "重启后同一天不重复领"</b>：注入 <see cref="IStoragePort"/> 后，每次成功操作写一个 1 字节标记
    /// <c>gift-&lt;friendId&gt;-&lt;day&gt;</c> / <c>claim-&lt;friendId&gt;-&lt;day&gt;</c>（day 是 UTC 自然日序号），
    /// 构造期与跨日时按当天序号读回，因此杀进程重进游戏，今天送过/领过的好友依然是"已完成"。
    /// <b>无时间源时永远停在第 0 日</b>（标记固定为 <c>...-0</c>）——真实宿主必须注入
    /// <c>UnityTimeProvider</c>，否则跨日永不发生；跨日验收请注入可拨动的 <see cref="ITimeProvider"/>。
    /// </para>
    /// <para>
    /// <b>键是扁平逻辑键</b>（<see cref="IStoragePort"/> 的约定）：本类不落盘任何玩家 id/昵称，只写布尔标记。
    /// 宿主应为本服务传<b>独立子目录</b>的存储端口（例如 <c>new UnityStoragePort("social")</c>），
    /// 避免与账号/存档的键混在同一命名空间。旧日期的标记<b>不主动删除</b>：单个 key 只有 1 字节，
    /// 真实后端上线后这批文件会整体废弃（由服务端接管每日状态），这里不做有数据风险的清理。
    /// </para>
    /// <para>
    /// <b>不使用 UnityEngine</b>：本类只依赖 <c>Contracts</c> 的端口与 <c>System</c>，
    /// 因此可以在没有 Unity 的环境里做形状级编译校验（见 <c>unity/tools/check-adapter-compile.ps1</c>）。
    /// </para>
    /// <para>
    /// <b>永不抛异常</b>：落盘失败（磁盘满/权限）、键非法、时间源违约都被收敛成"本次标记没写进/没读到"的降级结果
    /// ——IO 异常绝不允许打断一次普通的"赠送/领取"点击。
    /// </para>
    /// <para>线程模型：非线程安全。列表/赠送/领取都在 Unity 主线程串行调用。</para>
    /// </summary>
    public sealed class UnitySocialService : ISocialService
    {
        /// <summary>日志环形容量（最近 32 条，诊断用）。</summary>
        public const int LogCapacity = 32;

        /// <summary>默认好友数（3 人，覆盖"全领/单人领/重复领"三种路径）。</summary>
        public const int DefaultFriendCount = 3;

        /// <summary>好友玩家 id 前缀（<c>friend-1..N</c>）。</summary>
        public const string FriendIdPrefix = "friend-";

        /// <summary>好友展示名前缀（<c>好友1..N</c>）。</summary>
        public const string FriendNamePrefix = "好友";

        /// <summary>赠送标记的键前缀（完整键 <c>gift-&lt;friendId&gt;-&lt;day&gt;</c>）。</summary>
        public const string GiftKeyPrefix = "gift-";

        /// <summary>领取标记的键前缀（完整键 <c>claim-&lt;friendId&gt;-&lt;day&gt;</c>）。</summary>
        public const string ClaimKeyPrefix = "claim-";

        /// <summary>一次赠送的体力数量（假实现固定值；真实数值由后端配置下发）。</summary>
        public const int GiftStaminaAmount = 10;

        /// <summary>一次领取的体力数量（假实现固定值；真实数值由后端配置下发）。</summary>
        public const int ClaimStaminaAmount = 10;

        /// <summary>失败原因：未启用本地模拟（<c>available: false</c>）。</summary>
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

        /// <summary>标记写入的内容（1 字节，仅表示"存在"；不存数量/时间，避免过期语义漂移）。</summary>
        private static readonly byte[] MarkerBytes = new byte[] { 1 };

        /// <summary>相邻好友的"最近活跃"间隔（秒），只用于让列表具备可断言的排序位次。</summary>
        private const int LastActiveStepSeconds = 300;

        /// <summary>可选存档端口；<c>null</c> = 不落盘（杀进程即忘记今天已送/已领）。</summary>
        private readonly IStoragePort? _storage;

        /// <summary>可选时间源（跨日判定；<c>null</c> 时固定第 0 日）。</summary>
        private readonly ITimeProvider? _time;

        /// <summary>是否启用本地模拟（构造后不可变，见类注释）。</summary>
        private readonly bool _available;

        /// <summary>好友玩家 id（下标即内部索引）。</summary>
        private readonly string[] _friendIds;

        /// <summary>好友展示名（与 <see cref="_friendIds"/> 同下标）。</summary>
        private readonly string[] _friendNames;

        /// <summary>当日已赠送标记。</summary>
        private readonly bool[] _giftedToday;

        /// <summary>当日已领取标记。</summary>
        private readonly bool[] _claimedToday;

        /// <summary>当前标记所属的 UTC 天序号；<see cref="int.MinValue"/> = 尚未结算/尚未从存档读回。</summary>
        private int _dayIndex = int.MinValue;

        /// <summary>日志环形缓冲。</summary>
        private readonly string[] _log = new string[LogCapacity];

        /// <summary>日志里当前有效条数。</summary>
        private int _logCount;

        /// <summary>日志的下一个写入位置。</summary>
        private int _logNext;

        /// <summary>创建 Unity 社交服务。</summary>
        /// <param name="storage">
        /// 可选存档端口（建议传独立子目录实例，例如 <c>new UnityStoragePort("social")</c>；
        /// <c>null</c> = 只在内存里记当日标记）。
        /// </param>
        /// <param name="time">可选时间源（跨日与好友最近活跃时间；<c>null</c> 时固定第 0 日）。</param>
        /// <param name="available">是否启用本地模拟；默认 <c>false</c>（未接线必须如实报不可用）。</param>
        /// <param name="friendCount">好友数量（负值按 0 处理）。</param>
        public UnitySocialService(
            IStoragePort? storage = null,
            ITimeProvider? time = null,
            bool available = false,
            int friendCount = DefaultFriendCount)
        {
            _storage = storage;
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

            // 启用模拟时构造期就把"今天已经送过/领过"恢复出来：否则重启后列表会短暂显示"可送"，
            // 玩家点下去才被自己拦住（体验与真实服务端不一致）。
            if (_available)
            {
                EnsureDay();
            }
        }

        /// <inheritdoc />
        /// <remarks>只有"被显式启用"时才为 <c>true</c>：没接线的后端必须如实报告不可用。</remarks>
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
        /// 好友列表与"今天还能不能被送/被领"全部来自本地标记（含落盘恢复），不触达网络。
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
        /// 每人每天 1 次（<b>重启后同一天仍然拦住</b>：标记已落盘）。成功后写 <c>gift-&lt;id&gt;-&lt;day&gt;</c>；
        /// 写盘失败只影响"重启后的拦截"，本次会话照常记住（见类注释的降级约定）。
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
            SaveMarker(GiftKey(_friendIds[index], _dayIndex));
            AppendLog("gift " + _friendIds[index] + " -> Success(+" + GiftStaminaAmount.ToString(CultureInfo.InvariantCulture) + ")");
            return new FriendOperationOutcome(true, FriendGiftResult.Success, GiftStaminaAmount, ReasonGifted);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 逐个结算"今日未领"的好友并累加数量；没有可领的返回 0（合法结果）。每个成功领取的好友都会落盘标记，
        /// 因此"一键领取后杀进程"，重启也不会再领一次。
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
                SaveMarker(ClaimKey(_friendIds[i], _dayIndex));
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
        /// 每人每天 1 次（重启后同一天仍然拦住）。重复领取返回
        /// <see cref="FriendGiftResult.AlreadyGifted"/>（= 今日已领，枚举值复用见契约）。
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
            SaveMarker(ClaimKey(_friendIds[index], _dayIndex));
            AppendLog("claim " + _friendIds[index] + " -> Success(+" + ClaimStaminaAmount.ToString(CultureInfo.InvariantCulture) + ")");
            return new FriendOperationOutcome(true, FriendGiftResult.Success, ClaimStaminaAmount, ReasonClaimed);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 本地模拟没有网络可刷：这里只做跨日结算（按 UTC 日序号读回新一天的标记），并记一条日志。
        /// 同一天内重复调用幂等。
        /// </remarks>
        public void Refresh()
        {
            EnsureDay();
            AppendLog("refresh(day " + _dayIndex.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>
        /// 若已跨 UTC 自然日则清空当日标记并<b>按新日序号读回存档</b>（首次调用时 <see cref="_dayIndex"/>
        /// 是 <see cref="int.MinValue"/>，因此构造期必定加载一次）。
        /// <para>
        /// 只由操作入口/构造期调用，读属性不触发 IO：避免"UI 每帧读列表"变成每帧磁盘访问。
        /// </para>
        /// </summary>
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
            LoadDay(today);
            AppendLog("daily settle(day " + today.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>
        /// 今天的 UTC 日序号；无时钟时固定 0，时钟违约时返回当前标记日（不跨日）。
        /// <para>fail-closed：宁可少送一次，也不因为时间源异常把"今天已送过的"放出来。</para>
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
                return _dayIndex < 0 ? 0 : _dayIndex;
            }
        }

        /// <summary>按当天序号读回每个好友的落盘标记（读失败/无存档一律按"未操作"处理，并记日志）。</summary>
        /// <param name="dayIndex">当天 UTC 日序号。</param>
        private void LoadDay(int dayIndex)
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            for (int i = 0; i < _friendIds.Length; i++)
            {
                _giftedToday[i] = HasMarker(storage, GiftKey(_friendIds[i], dayIndex));
                _claimedToday[i] = HasMarker(storage, ClaimKey(_friendIds[i], dayIndex));
            }

            AppendLog("markers loaded(day " + dayIndex.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>读一个标记；无存档/读失败返回 <c>false</c>（读失败等价于"今天还没做过"）。</summary>
        /// <param name="storage">存档端口。</param>
        /// <param name="key">标记键。</param>
        /// <returns>标记是否存在且非空。</returns>
        private bool HasMarker(IStoragePort storage, string key)
        {
            try
            {
                byte[]? bytes = storage.Read(key);
                return bytes != null && bytes.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>写一个标记；失败只影响"重启后的拦截"，本次会话状态已由内存标记守住。</summary>
        /// <param name="key">标记键。</param>
        private void SaveMarker(string key)
        {
            IStoragePort? storage = _storage;
            if (storage == null)
            {
                return;
            }

            try
            {
                storage.Write(key, MarkerBytes);
            }
            catch (Exception)
            {
                AppendLog("save marker failed(" + key + ")");
            }
        }

        /// <summary>赠送标记键：<c>gift-&lt;friendId&gt;-&lt;day&gt;</c>（字符集满足 <see cref="IStoragePort"/> 的键约定）。</summary>
        /// <param name="friendId">好友 id。</param>
        /// <param name="dayIndex">UTC 日序号。</param>
        /// <returns>逻辑键。</returns>
        private static string GiftKey(string friendId, int dayIndex)
        {
            return GiftKeyPrefix + friendId + "-" + dayIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>领取标记键：<c>claim-&lt;friendId&gt;-&lt;day&gt;</c>。</summary>
        /// <param name="friendId">好友 id。</param>
        /// <param name="dayIndex">UTC 日序号。</param>
        /// <returns>逻辑键。</returns>
        private static string ClaimKey(string friendId, int dayIndex)
        {
            return ClaimKeyPrefix + friendId + "-" + dayIndex.ToString(CultureInfo.InvariantCulture);
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
