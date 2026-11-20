#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 玩家指标的一帧快照：**累计口径**（Sessions…Retries）+ **活跃口径**（DAU / 连续天数）。
    /// <para>
    /// <b>为什么是 readonly struct 而不是 class</b>：快照是"某一刻的读数"，产生它的
    /// <see cref="PlayerMetrics"/> 还在继续收事件。用值类型把数字当场拷走，调用方（看板 UI、CSV 导出、
    /// 上报体）就不必担心后续事件把手里这份数据改掉，也不用为此加锁或 Clone。
    /// </para>
    /// <para>
    /// <b>字段全是整数</b>：秒数在聚合器内部按 <c>double</c> 累加，快照时才四舍五入成整秒；
    /// 货币同理。这样 CSV 的每一列都是纯整数，不受平台区域设置（逗号/点号小数点）影响，
    /// 后台直接 <c>long.Parse(InvariantCulture)</c> 即可，不会出现"德语区导出的小数点把列拆开"这类事故。
    /// </para>
    /// <para>
    /// <b>列序即契约</b>：<see cref="ToCsvRow"/> 的列序必须与 <see cref="CsvHeader"/> 一一对应，
    /// 两边任何一侧加列都必须同步改另一侧（<c>PlayerMetricsTests</c> 用"表头列数 = 数据列数"把这条钉死）。
    /// </para>
    /// </summary>
    public readonly struct PlayerMetricsSnapshot
    {
        /// <summary>
        /// CSV 表头（固定 12 列，列序 = <see cref="ToCsvRow"/> 的字段序）。
        /// <para>列名用 camelCase 而不是 SQL 风格：这份 CSV 的第一读者是表格软件与人眼，第二读者才是导入脚本。</para>
        /// </summary>
        public const string CsvHeader =
            "dateUtc,sessions,playSeconds,levelsStarted,levelsWon,purchases,adsWatched,"
            + "softSpent,softEarned,retries,dailyActiveDays,currentStreakDays";

        /// <summary>CSV 列数（表头与数据行共用，测试与导入脚本据此校验）。</summary>
        public const int CsvColumnCount = 12;

        /// <summary>构造快照（一般由 <see cref="PlayerMetrics.Snapshot"/> 产出，测试可直接构造）。</summary>
        /// <param name="sessions">累计会话数。</param>
        /// <param name="playSeconds">累计游玩秒数（整秒）。</param>
        /// <param name="levelsStarted">累计开始的关卡数。</param>
        /// <param name="levelsWon">累计胜利的关卡数。</param>
        /// <param name="purchases">累计购买完成数（软通货购买 + IAP）。</param>
        /// <param name="adsWatched">累计广告完播数。</param>
        /// <param name="softSpent">累计软通货支出。</param>
        /// <param name="softEarned">累计软通货收入。</param>
        /// <param name="retries">累计重开同一关卡的次数。</param>
        /// <param name="dailyActiveDays">累计活跃天数（DAU 的口径：去重后的 UTC 日数）。</param>
        /// <param name="currentStreakDays">当前连续活跃天数（含今天）。</param>
        public PlayerMetricsSnapshot(
            int sessions,
            long playSeconds,
            int levelsStarted,
            int levelsWon,
            int purchases,
            int adsWatched,
            long softSpent,
            long softEarned,
            int retries,
            int dailyActiveDays,
            int currentStreakDays)
        {
            Sessions = sessions;
            PlaySeconds = playSeconds;
            LevelsStarted = levelsStarted;
            LevelsWon = levelsWon;
            Purchases = purchases;
            AdsWatched = adsWatched;
            SoftSpent = softSpent;
            SoftEarned = softEarned;
            Retries = retries;
            DailyActiveDays = dailyActiveDays;
            CurrentStreakDays = currentStreakDays;
        }

        /// <summary>累计会话数（只增；重复 <c>BeginSession</c> 不会重复计数）。</summary>
        public int Sessions { get; }

        /// <summary>累计游玩秒数（只增；会话时长为负/时间源违约时不累加）。</summary>
        public long PlaySeconds { get; }

        /// <summary>累计开始的关卡数（<c>BattleStart</c> 条数）。</summary>
        public int LevelsStarted { get; }

        /// <summary>累计胜利关卡数（<c>BattleEnd</c> 且 value0 &gt; 0 的条数）。</summary>
        public int LevelsWon { get; }

        /// <summary>累计购买完成数（<c>Purchase</c> + <c>IapPurchased</c>）。</summary>
        public int Purchases { get; }

        /// <summary>累计广告完播数（<c>AdCompleted</c>）。</summary>
        public int AdsWatched { get; }

        /// <summary>累计软通货支出（<c>Purchase</c> 的 value1 之和）。</summary>
        public long SoftSpent { get; }

        /// <summary>累计软通货收入（<c>ItemGranted</c> 中软通货道具的数量之和）。</summary>
        public long SoftEarned { get; }

        /// <summary>累计重开次数（同一关卡 id 第二次及以后 <c>BattleStart</c> 的条数）。</summary>
        public int Retries { get; }

        /// <summary>累计活跃天数（按 UTC 日去重；不是"会话数"，一天多次进游戏只算 1）。</summary>
        public int DailyActiveDays { get; }

        /// <summary>当前连续活跃天数（含今天；断档后从 1 重新计）。</summary>
        public int CurrentStreakDays { get; }

        /// <summary>
        /// 生成 CSV 数据行（不含换行；列序 = <see cref="CsvHeader"/>）。
        /// <para>
        /// <b>为什么日期由参数注入</b>：本结构体不持有时间源，而 CSV 的日期列必须与"导出那一刻"一致。
        /// 由调用方（<see cref="PlayerMetrics.ToCsv"/> 或测试）给定日期，既避免结构体偷偷读时钟（AIF003），
        /// 也让"同一份指标导出成 2026-11-20 的行"这种断言不依赖真实时间。
        /// </para>
        /// <para>
        /// <b>不做 CSV 转义</b>：唯一的字符串列是日期，且必须是 <c>yyyy-MM-dd</c> 这类无逗号/引号/换行的短文本；
        /// 一旦含特殊字符就直接抛，而不是悄悄生成一行会被导入脚本拆错列的数据。
        /// </para>
        /// </summary>
        /// <param name="dateUtc">日期列文本（UTC 日期，如 <c>2026-11-20</c>）。</param>
        /// <returns>单行 CSV（无行尾换行）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="dateUtc"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="dateUtc"/> 为空白或含 <c>,</c>、<c>"</c>、CR/LF。</exception>
        public string ToCsvRow(string dateUtc)
        {
            string date = ValidateDate(dateUtc);
            var builder = new StringBuilder(CsvHeader.Length + 32);
            AppendField(builder, date);
            AppendField(builder, Sessions);
            AppendField(builder, PlaySeconds);
            AppendField(builder, LevelsStarted);
            AppendField(builder, LevelsWon);
            AppendField(builder, Purchases);
            AppendField(builder, AdsWatched);
            AppendField(builder, SoftSpent);
            AppendField(builder, SoftEarned);
            AppendField(builder, Retries);
            AppendField(builder, DailyActiveDays);
            AppendField(builder, CurrentStreakDays);
            return builder.ToString();
        }

        /// <summary>校验并裁剪日期列；非法输入在这里一次性拒绝，保证下游 CSV 永远列数正确。</summary>
        private static string ValidateDate(string dateUtc)
        {
            if (dateUtc == null)
            {
                throw new ArgumentNullException(nameof(dateUtc));
            }

            string date = dateUtc.Trim();
            if (date.Length == 0)
            {
                throw new ArgumentException("CSV 日期列不能为空白。", nameof(dateUtc));
            }

            for (int i = 0; i < date.Length; i++)
            {
                char c = date[i];
                if (c == ',' || c == '"' || c == '\r' || c == '\n')
                {
                    throw new ArgumentException(
                        "CSV 日期列不得包含逗号/引号/换行（本实现不做字段转义）。", nameof(dateUtc));
                }
            }

            return date;
        }

        /// <summary>追加一个整数字段（含前导逗号），全部走不变文化，避免区域设置污染。</summary>
        private static void AppendField(StringBuilder builder, long value)
        {
            builder.Append(',');
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>追加日期字段（首列，无前导逗号）。</summary>
        private static void AppendField(StringBuilder builder, string value)
        {
            builder.Append(value);
        }
    }

    /// <summary>
    /// 玩家数据看板的**本地指标聚合器**：把 <see cref="AnalyticsEvent"/> 事件流折叠成
    /// "累计窗口 + 当日窗口 + 活跃天数"三组读数，并可导出单行 CSV。
    /// <para>
    /// <b>它解决的不是"再存一遍埋点"</b>：埋点缓冲（<see cref="AnalyticsEventBuffer"/>）是给后台发的原始流水，
    /// 会丢（定长环形、网络失败、离线）。看板要回答的是"这个玩家今天/总共玩了多久、打到哪、花了多少"，
    /// 这些数字必须**本地只增不减**，否则离线玩家在后台看到的就是一片空白。
    /// </para>
    /// <para>
    /// <b>事件映射表（每一条都是口径，改了就等于改报表定义）</b>：
    /// <list type="table">
    /// <item><term><see cref="AnalyticsEventIds.AppLaunch"/> / <see cref="AnalyticsEventIds.SceneEnter"/></term>
    /// <description>不产生计数字段，但会隐式 <see cref="MarkActive"/>（进游戏 = 今天活跃过）。</description></item>
    /// <item><term><see cref="AnalyticsEventIds.BattleStart"/></term>
    /// <description><see cref="PlayerMetricsSnapshot.LevelsStarted"/>+1；同一关卡 id（value1）第二次及以后每次 <see cref="PlayerMetricsSnapshot.Retries"/>+1。</description></item>
    /// <item><term><see cref="AnalyticsEventIds.BattleEnd"/></term>
    /// <description>value0 &gt; 0（胜利）时 <see cref="PlayerMetricsSnapshot.LevelsWon"/>+1；战败不产生任何计数（本看板不收败场），也不计入未知事件。value1 是战斗时长，**不**加进 PlaySeconds（时长以会话口径为准，否则同一段时间会被记两次）。</description></item>
    /// <item><term><see cref="AnalyticsEventIds.Purchase"/></term>
    /// <description><see cref="PlayerMetricsSnapshot.Purchases"/>+1、<see cref="PlayerMetricsSnapshot.SoftSpent"/> += value1（花费）。</description></item>
    /// <item><term><see cref="AnalyticsEventIds.IapPurchased"/></term>
    /// <description>只计 <see cref="PlayerMetricsSnapshot.Purchases"/>+1；现实货币不进软通货支出（value1 是价格档，不是货币数量）。</description></item>
    /// <item><term><see cref="AnalyticsEventIds.AdCompleted"/></term>
    /// <description><see cref="PlayerMetricsSnapshot.AdsWatched"/>+1。</description></item>
    /// <item><term><see cref="AnalyticsEventIds.ItemGranted"/></term>
    /// <description>value0（itemId）等于 <see cref="SoftCurrencyItemId"/> 时 <see cref="PlayerMetricsSnapshot.SoftEarned"/> += value1；其它道具（皮肤、材料）不折算成货币。</description></item>
    /// <item><term><see cref="AnalyticsEventIds.Gacha"/></term>
    /// <description>已知事件但本看板没有对应指标，识别后直接丢弃（**不**计入 <see cref="UnknownEvents"/>，否则未知事件的告警会被正常抽卡刷爆）。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>时间口径（全部经 <see cref="ITimeProvider"/>，逻辑层禁止直接读系统时钟）</b>：
    /// <list type="bullet">
    /// <item><b>日序号</b> = <c>(UtcNow.Date - 1970-01-01 UTC).Days</c>。固定用 UTC，与
    /// <c>AdShowScheduler</c> / <c>MailHandler</c> 同一套"跨时区不多刷一天"的约定；
    /// 代价是 UTC+8 的玩家 DAU 在北京时间 08:00 翻页（见交付报告"未验证点"）。</item>
    /// <item><b>会话时长</b>读 <see cref="ITimeProvider.ElapsedSeconds"/>（单调钟）：改系统时间不能凭空刷时长。</item>
    /// <item><b>时间源违约</b>（抛异常）时：日相关记账跳过、窗口不切分，累计计数照记——宁可少记一天，
    /// 也不能让埋点聚合把业务调用点炸掉；<see cref="ToCsv"/> 的日期列退化写 <c>unknown</c>。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>三条只增规则</b>：累计字段永不回退（负数量按 0 处理，不做冲抵）；重复 <see cref="BeginSession"/>
    /// 不重复计会话；重复 <see cref="EndSession"/> 不重复计时长。
    /// </para>
    /// <para>
    /// <b>线程模型：非线程安全（全同步）</b>。聚合器被埋点上报点与结算逻辑从主线程串行调用；
    /// 无锁设计是有意的——热路径上加锁的代价（内核对撞 + 缓存行抖动）远高于这点聚合收益，
    /// 需要跨线程快照时请在调用侧串行化（或先 <see cref="Snapshot"/> 把值拷走再跨线程）。
    /// </para>
    /// </summary>
    public sealed class PlayerMetrics
    {
        /// <summary>CSV 表头（转发自快照，避免两处定义漂移）。</summary>
        public const string CsvHeader = PlayerMetricsSnapshot.CsvHeader;

        /// <summary>
        /// 默认软通货道具 id（1001）：与仓库现有唯一的金币身份 <c>ConsoleIapService.GoldItemId</c> 对齐
        /// （Core 不能引用适配层，所以这里只能写值 + 说明，不能写 <c>const</c> 转发）。
        /// <para>
        /// 这个约定本该由 <c>Contracts</c> 常量定义，但本任务的可写范围里不含契约文件，
        /// 因此先固定在此处并通过构造函数参数开放：接线时若项目里的金币 id 不是 1001，
        /// 用 <c>new PlayerMetrics(time, softCurrencyItemId: ConsoleIapService.GoldItemId)</c> 覆盖即可，
        /// 不需要改 Core。**接线时必须确认一次**，否则软通货收入会静默为 0。
        /// </para>
        /// </summary>
        public const int DefaultSoftCurrencyItemId = 1001;

        /// <summary>时间源不可用时 CSV 日期列的取值（宁可标注不可用，也不写一个假日期）。</summary>
        public const string UnknownDateText = "unknown";

        /// <summary>日序号纪元（UTC）：与 <c>AdShowScheduler</c> 同一口径。</summary>
        private static readonly DateTime DayEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>"日序号不可用"哨兵（<c>int.MinValue</c>：0 是 1970-01-01 这个合法日，不能当哨兵）。</summary>
        private const int UnknownDay = int.MinValue;

        /// <summary>时间源（跨天判定与会话时长都要经由此端口）。</summary>
        private readonly ITimeProvider _time;

        /// <summary>软通货道具 id；<c>&lt;= 0</c> 表示关闭"道具授予 → 软通货收入"这条口径。</summary>
        private readonly int _softCurrencyItemId;

        /// <summary>
        /// 已开始过的关卡 id 集合（判定"重开"）。关卡是有限内容集合（通常 &lt; 1000），
        /// 因此不做容量淘汰；调用方**不得**传入动态生成的关卡 id（那会让集合无界增长）。
        /// </summary>
        private readonly HashSet<int> _startedLevelIds;

        /// <summary>累计会话数。</summary>
        private int _sessions;

        /// <summary>累计游玩秒数（<c>double</c> 累加，快照时四舍五入；避免每段会话先截断造成的系统性低估）。</summary>
        private double _playSeconds;

        /// <summary>累计开始关卡数。</summary>
        private int _levelsStarted;

        /// <summary>累计胜利关卡数。</summary>
        private int _levelsWon;

        /// <summary>累计购买完成数。</summary>
        private int _purchases;

        /// <summary>累计广告完播数。</summary>
        private int _adsWatched;

        /// <summary>累计软通货支出。</summary>
        private long _softSpent;

        /// <summary>累计软通货收入。</summary>
        private long _softEarned;

        /// <summary>累计重开次数。</summary>
        private int _retries;

        /// <summary>未识别事件数（事件号不在 <see cref="AnalyticsEventIds"/> 内，或事件本身非法）。</summary>
        private long _unknownEvents;

        /// <summary>当前是否在会话中（重复 <see cref="BeginSession"/> 的判定依据）。</summary>
        private bool _inSession;

        /// <summary>本次会话开始的单调秒数（仅在 <see cref="_sessionStartValid"/> 为真时有意义）。</summary>
        private double _sessionStartSeconds;

        /// <summary>本次会话开始时刻是否读到了有效的单调秒数。</summary>
        private bool _sessionStartValid;

        /// <summary>当日窗口所属的日序号（<see cref="UnknownDay"/> = 时间源不可用，窗口不切分）。</summary>
        private int _windowDay = UnknownDay;

        /// <summary>当日窗口：开始关卡数。</summary>
        private int _dailyLevelsStarted;

        /// <summary>当日窗口：胜利关卡数。</summary>
        private int _dailyLevelsWon;

        /// <summary>当日窗口：购买完成数。</summary>
        private int _dailyPurchases;

        /// <summary>当日窗口：广告完播数。</summary>
        private int _dailyAdsWatched;

        /// <summary>当日窗口：软通货支出。</summary>
        private long _dailySoftSpent;

        /// <summary>当日窗口：软通货收入。</summary>
        private long _dailySoftEarned;

        /// <summary>当日窗口：重开次数。</summary>
        private int _dailyRetries;

        /// <summary>最近一次活跃的 UTC 日序号（<see cref="UnknownDay"/> = 从未活跃过）。</summary>
        private int _lastActiveDay = UnknownDay;

        /// <summary>累计活跃天数（按 UTC 日去重）。</summary>
        private int _dailyActiveDays;

        /// <summary>当前连续活跃天数（含今天）。</summary>
        private int _currentStreakDays;

        /// <summary>创建聚合器。</summary>
        /// <param name="time">时间源（非 <c>null</c>；为 <c>null</c> 属装配期编程错误，直接抛）。</param>
        /// <param name="softCurrencyItemId">
        /// 软通货道具 id（默认 <see cref="DefaultSoftCurrencyItemId"/>；<c>&lt;= 0</c> 表示关闭该口径，
        /// 此时任何 <c>ItemGranted</c> 都不会被折算成软通货收入）。
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="time"/> 为 <c>null</c>。</exception>
        public PlayerMetrics(ITimeProvider time, int softCurrencyItemId = DefaultSoftCurrencyItemId)
        {
            _time = time ?? throw new ArgumentNullException(nameof(time));
            _softCurrencyItemId = softCurrencyItemId;
            _startedLevelIds = new HashSet<int>();
        }

        /// <summary>未识别事件数：事件号不在 <see cref="AnalyticsEventIds"/> 内，或事件本身非法（id 0 / NaN / Inf）。</summary>
        public long UnknownEvents
        {
            get { return _unknownEvents; }
        }

        /// <summary>当前是否在会话中。</summary>
        public bool InSession
        {
            get { return _inSession; }
        }

        /// <summary>软通货道具 id（构造参数，<c>&lt;= 0</c> 表示该口径已关闭）。</summary>
        public int SoftCurrencyItemId
        {
            get { return _softCurrencyItemId; }
        }

        /// <summary>当日窗口：已开始的关卡数（跨天或 <see cref="ResetDaily"/> 后归零）。</summary>
        public int DailyLevelsStarted
        {
            get { return _dailyLevelsStarted; }
        }

        /// <summary>当日窗口：已胜利的关卡数。</summary>
        public int DailyLevelsWon
        {
            get { return _dailyLevelsWon; }
        }

        /// <summary>当日窗口：购买完成数。</summary>
        public int DailyPurchases
        {
            get { return _dailyPurchases; }
        }

        /// <summary>当日窗口：广告完播数。</summary>
        public int DailyAdsWatched
        {
            get { return _dailyAdsWatched; }
        }

        /// <summary>当日窗口：软通货支出。</summary>
        public long DailySoftSpent
        {
            get { return _dailySoftSpent; }
        }

        /// <summary>当日窗口：软通货收入。</summary>
        public long DailySoftEarned
        {
            get { return _dailySoftEarned; }
        }

        /// <summary>当日窗口：重开次数。</summary>
        public int DailyRetries
        {
            get { return _dailyRetries; }
        }

        /// <summary>
        /// 导出 CSV 的日期列（UTC <c>yyyy-MM-dd</c>）。
        /// <para>时间源违约或早于 1970 时返回 <see cref="UnknownDateText"/>：退出时导出指标绝不能因为
        /// 时钟异常而抛异常丢数据，也不能凭空写一个假日期冒充"今天"。</para>
        /// </summary>
        public string CurrentDateUtc
        {
            get
            {
                string? date = TryFormatUtcDate();
                return date ?? UnknownDateText;
            }
        }

        /// <summary>
        /// 喂一条埋点事件（按 <see cref="AnalyticsEventIds"/> 分类聚合）。
        /// <para>
        /// 不抛异常、不阻塞、不做 IO：埋点绝不能反向影响业务逻辑。事件号不认识时只累加
        /// <see cref="UnknownEvents"/>，不做任何猜测性归类。
        /// </para>
        /// </summary>
        /// <param name="evt">埋点事件（<c>in</c> 传入，避免 24 字节结构体拷贝）。</param>
        public void Track(in AnalyticsEvent evt)
        {
            if (!evt.IsValid)
            {
                // 非法事件（id 0、NaN/Inf）：无法归类，只能计入未知，绝不能让它污染任何指标。
                _unknownEvents++;
                return;
            }

            switch (evt.EventId)
            {
                case AnalyticsEventIds.AppLaunch:
                    // 启动即"今天活跃过"：这是最可靠的 DAU 信号（早于任何 UI 操作）。
                    MarkActive();
                    return;

                case AnalyticsEventIds.SceneEnter:
                    // 进场景同样代表活跃（热重启不进 AppLaunch 的宿主，这里兜底）。
                    MarkActive();
                    return;

                case AnalyticsEventIds.BattleStart:
                    EnsureWindowDay();
                    _levelsStarted++;
                    _dailyLevelsStarted++;
                    TrackLevelStart(evt.Value1);
                    return;

                case AnalyticsEventIds.BattleEnd:
                    // value0 = 是否胜利。战败是"已知事件但没有指标"，直接返回（不计未知）。
                    if (evt.Value0 <= 0f)
                    {
                        return;
                    }

                    EnsureWindowDay();
                    _levelsWon++;
                    _dailyLevelsWon++;
                    return;

                case AnalyticsEventIds.Purchase:
                    EarnOrSpendPurchase(in evt);
                    return;

                case AnalyticsEventIds.IapPurchased:
                    // 现实货币：只算一次"购买完成"，不进软通货支出（value1 是价格档，不是货币数量）。
                    EnsureWindowDay();
                    _purchases++;
                    _dailyPurchases++;
                    return;

                case AnalyticsEventIds.AdCompleted:
                    EnsureWindowDay();
                    _adsWatched++;
                    _dailyAdsWatched++;
                    return;

                case AnalyticsEventIds.ItemGranted:
                    TrackItemGranted(in evt);
                    return;

                case AnalyticsEventIds.Gacha:
                    // 已知事件、本看板无对应指标：识别后丢弃，不计入 UnknownEvents。
                    return;

                default:
                    _unknownEvents++;
                    return;
            }
        }

        /// <summary>
        /// 记录一次会话开始。**重复调用不会重复计数**（以"已在会话中"为准）：
        /// 断线重连、切场景、Unity 侧 OnApplicationPause 反复触发都不会把会话数刷高。
        /// </summary>
        public void BeginSession()
        {
            if (_inSession)
            {
                return;
            }

            _inSession = true;
            _sessions++;
            _sessionStartValid = TryReadElapsedSeconds(out _sessionStartSeconds);
        }

        /// <summary>
        /// 记录一次会话结束：把"当前单调秒 - 开始秒"累加进 <see cref="PlayerMetricsSnapshot.PlaySeconds"/>。
        /// <para>
        /// 未 <see cref="BeginSession"/> 就结束、以及重复结束一律忽略（幂等）；时长差为负、
        /// NaN/Inf 或时间源违约时**不累加时长但照常结束会话**——否则一个坏时钟会把玩家永久卡在
        /// "会话中"状态，之后所有 Begin 都被当成重复而失效。
        /// </para>
        /// </summary>
        public void EndSession()
        {
            if (!_inSession)
            {
                return;
            }

            _inSession = false;
            if (!_sessionStartValid)
            {
                return;
            }

            double now;
            if (!TryReadElapsedSeconds(out now))
            {
                return;
            }

            double delta = now - _sessionStartSeconds;
            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta <= 0d)
            {
                return;
            }

            _playSeconds += delta;
        }

        /// <summary>
        /// 记录"此刻活跃"，用于 DAU 与连续活跃天数。
        /// <para>
        /// 同一天重复调用不涨；跨到新的一天（UTC）<see cref="PlayerMetricsSnapshot.DailyActiveDays"/>+1，
        /// 且当天正好是上次活跃日的次日则 <see cref="PlayerMetricsSnapshot.CurrentStreakDays"/>+1，
        /// 否则重置为 1。时钟回拨（今天早于上次活跃日）只把连续天数重置为 1，**不**重复累加活跃天数
        /// （同一天被数两次比少一天更糟：看板会凭空多出 DAU）。
        /// </para>
        /// <para>时间源违约时不抛、不记活跃（算不出是哪一天，就不该动这两个数字）。</para>
        /// </summary>
        public void MarkActive()
        {
            int today = SafeDayIndex();
            if (today == UnknownDay)
            {
                return;
            }

            EnsureWindowDay();
            if (_lastActiveDay == today)
            {
                return;
            }

            if (_lastActiveDay == UnknownDay)
            {
                // 首次活跃：活跃天数与连续天数都从 1 开始（不是 0：今天确实活跃了）。
                _dailyActiveDays = 1;
                _currentStreakDays = 1;
            }
            else if (today > _lastActiveDay)
            {
                _dailyActiveDays++;
                _currentStreakDays = today == _lastActiveDay + 1 ? _currentStreakDays + 1 : 1;
            }
            else
            {
                // 时钟回拨：宁可把连续天数打断，也不把"过去的那一天"再算一次 DAU。
                _currentStreakDays = 1;
            }

            _lastActiveDay = today;
        }

        /// <summary>
        /// 取当前累计读数的一份快照（值拷贝，之后继续 Track 不影响已取走的快照）。
        /// </summary>
        /// <returns>只读快照。</returns>
        public PlayerMetricsSnapshot Snapshot()
        {
            return new PlayerMetricsSnapshot(
                _sessions,
                RoundToLong(_playSeconds),
                _levelsStarted,
                _levelsWon,
                _purchases,
                _adsWatched,
                _softSpent,
                _softEarned,
                _retries,
                _dailyActiveDays,
                _currentStreakDays);
        }

        /// <summary>
        /// 导出 CSV（**表头 + 单行**，LF 换行，末尾带换行）。列序与 <see cref="PlayerMetricsSnapshot.CsvHeader"/> 一致。
        /// </summary>
        /// <returns>可直接落盘/追加的 CSV 文本。</returns>
        public string ToCsv()
        {
            var builder = new StringBuilder(PlayerMetricsSnapshot.CsvHeader.Length * 2 + 64);
            builder.Append(PlayerMetricsSnapshot.CsvHeader).Append('\n');
            builder.Append(Snapshot().ToCsvRow(CurrentDateUtc)).Append('\n');
            return builder.ToString();
        }

        /// <summary>
        /// 清空**当日窗口**（当日关卡/购买/观看/收支/重开），累计字段与 DAU/连续天数一律不动。
        /// <para>
        /// 适配层可以在 UTC 零点定时调用；即使不调，任何"当日计数"的写入都会先按日序号自检，
        /// 跨天时自动切窗口（见 <see cref="EnsureWindowDay"/>），所以本方法主要给"手动切日/补跑离线数据"用。
        /// </para>
        /// </summary>
        public void ResetDaily()
        {
            ClearDailyWindow();
            _windowDay = SafeDayIndex();
        }

        /// <summary>
        /// 购买完成（软通货口径）：次数 +1，支出 += value1（花费）。
        /// <para>value1 为负/NaN 时按 0 处理（看板只增不减，不做退款冲抵）。</para>
        /// </summary>
        private void EarnOrSpendPurchase(in AnalyticsEvent evt)
        {
            EnsureWindowDay();
            _purchases++;
            _dailyPurchases++;

            long spent = ToUnits(evt.Value1);
            if (spent <= 0L)
            {
                return;
            }

            _softSpent += spent;
            _dailySoftSpent += spent;
        }

        /// <summary>道具授予：只有 value0 等于软通货 id 时才计入软通货收入。</summary>
        private void TrackItemGranted(in AnalyticsEvent evt)
        {
            if (_softCurrencyItemId <= 0)
            {
                return;
            }

            if (ToIntId(evt.Value0) != _softCurrencyItemId)
            {
                // 皮肤、材料等其它道具：不是货币，不折算（否则"发一件皮肤"会被算成通胀）。
                return;
            }

            long earned = ToUnits(evt.Value1);
            if (earned <= 0L)
            {
                return;
            }

            EnsureWindowDay();
            _softEarned += earned;
            _dailySoftEarned += earned;
        }

        /// <summary>关卡开始：同一关卡 id 的第二次及以后计为重开。</summary>
        private void TrackLevelStart(float levelId)
        {
            int id = ToIntId(levelId);
            if (id <= 0)
            {
                // 关卡 id 未知（<= 0 或脏数据）：无法判定"是不是重开"，只能记一次开始。
                return;
            }

            if (!_startedLevelIds.Add(id))
            {
                _retries++;
                _dailyRetries++;
            }
        }

        /// <summary>
        /// 保证当日窗口属于"今天"：日序号变了就清空窗口。时间源不可用时**不切窗口**
        /// （<see cref="UnknownDay"/>），计数继续写进当前窗口——少切一天也只是窗口偏大，
        /// 总比因为读不到时钟就把当地数据整段丢掉要好。
        /// </summary>
        private void EnsureWindowDay()
        {
            int today = SafeDayIndex();
            if (today == UnknownDay || today == _windowDay)
            {
                return;
            }

            _windowDay = today;
            ClearDailyWindow();
        }

        /// <summary>清空当日窗口计数（不动累计字段，也不动 DAU/连续天数）。</summary>
        private void ClearDailyWindow()
        {
            _dailyLevelsStarted = 0;
            _dailyLevelsWon = 0;
            _dailyPurchases = 0;
            _dailyAdsWatched = 0;
            _dailySoftSpent = 0L;
            _dailySoftEarned = 0L;
            _dailyRetries = 0;
        }

        /// <summary>
        /// 今天的 UTC 日序号；时间源违约或早于 1970 时返回 <see cref="UnknownDay"/>。
        /// <para>用 <c>(date - epoch).Days</c> 而不是 <c>Ticks / TicksPerDay</c>：后者对 1970 年前的负时刻会错一天。</para>
        /// </summary>
        private int SafeDayIndex()
        {
            try
            {
                DateTime now = _time.UtcNow;
                if (now.Kind == DateTimeKind.Local)
                {
                    now = now.ToUniversalTime();
                }

                if (now.Year < 1970)
                {
                    return UnknownDay;
                }

                return (int)(now.Date - DayEpoch).Days;
            }
            catch (Exception)
            {
                // 时间源违约：不抛给埋点调用点，按"今天未知"降级。
                return UnknownDay;
            }
        }

        /// <summary>UTC 日期文本；时间源违约/时间不可用时返回 <c>null</c>（由调用方决定退化写法）。</summary>
        private string? TryFormatUtcDate()
        {
            try
            {
                DateTime now = _time.UtcNow;
                if (now.Kind == DateTimeKind.Local)
                {
                    now = now.ToUniversalTime();
                }

                if (now.Year < 1970)
                {
                    return null;
                }

                return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>读单调秒数；时间源违约时返回 <c>false</c>（调用方据此跳过时长累加）。</summary>
        private bool TryReadElapsedSeconds(out double seconds)
        {
            try
            {
                double value = _time.ElapsedSeconds;
                if (double.IsNaN(value) || double.IsInfinity(value))
                {
                    seconds = 0d;
                    return false;
                }

                seconds = value;
                return true;
            }
            catch (Exception)
            {
                seconds = 0d;
                return false;
            }
        }

        /// <summary>把事件里的"数量"折算成整数：非正/NaN/Inf 记 0，其余四舍五入（远离零）。</summary>
        private static long ToUnits(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw) || raw <= 0f)
            {
                return 0L;
            }

            double rounded = Math.Round((double)raw, MidpointRounding.AwayFromZero);
            return rounded >= long.MaxValue ? long.MaxValue : (long)rounded;
        }

        /// <summary>把事件里的"id"折算成整数：非法/越界/小于 1 时返回 0（0 = 未知 id）。</summary>
        private static int ToIntId(float raw)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw) || raw < 1f || raw >= int.MaxValue)
            {
                return 0;
            }

            return (int)raw;
        }

        /// <summary>秒数/数量快照取整：四舍五入（远离零），负值夹到 0。</summary>
        private static long RoundToLong(double value)
        {
            if (double.IsNaN(value) || value <= 0d)
            {
                return 0L;
            }

            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            return rounded >= long.MaxValue ? long.MaxValue : (long)rounded;
        }
    }
}
