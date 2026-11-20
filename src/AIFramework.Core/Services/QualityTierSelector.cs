#nullable enable
using System;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 画质档位选择器：按<b>最近 N 帧的平均帧耗时</b>决定升档/降档，纯逻辑、零引擎依赖、零逐帧分配。
    /// <para>
    /// <b>为什么不按设备型号硬编档位</b>（本类存在的根本理由）：
    /// <list type="number">
    /// <item><b>型号不等于帧率</b>：同一颗 SoC，在 720p/1080p、URP/HDRP、开不开后处理、场景里 10 个还是 200 个
    /// 单位时，实测帧率能差一倍以上。型号表只能给出"这台机器大概多强"，给不出"这一帧到底够不够快"。</item>
    /// <item><b>型号表会永久过期</b>：新机发布、系统大版本升级、厂商换调度策略，白名单每次都要人工补；
    /// 而"帧太慢就降档"这条策略对任何新硬件自动成立。</item>
    /// <item><b>发烫降频是运行时现象</b>：开局 60fps 的机器，玩 10 分钟后可能只剩 42fps。型号表在启动时查一次，
    /// 之后再也不更新；滑动窗口每天都在重新测量。</item>
    /// <item><b>型号表在无头环境不可测</b>：本框架的核心验收标准是"逻辑能在 <c>dotnet test</c> 里跑"，
    /// 型号表需要真实设备矩阵，而"喂帧耗时 → 看档位"用假数据就能穷举边界。</item>
    /// </list>
    /// 型号表不是完全无用——它适合当<b>冷启动的第一帧猜测</b>；本任务先落地"实测驱动"这条主干，
    /// 冷启动基线固定为 <see cref="QualityTier.Medium"/>（见构造函数注释）。
    /// </para>
    /// <para>
    /// <b>决策规则</b>（阈值默认值：窗口 120 帧 / 好帧 55fps / 坏帧 40fps / 冷却 300 帧）：
    /// <list type="number">
    /// <item><b>窗口未满不决策</b>：少于 <c>windowFrames</c> 个有效样本时一律返回 <c>false</c>。
    /// 这条是防抖的第一道闸：开局前几帧必然包含加载/编译着色器的长帧，不能据此降档。</item>
    /// <item><b>降档</b>：窗口满 且 平均 fps &lt; <c>badFps</c> 且 窗口内<b>每一帧</b>都慢于 <c>badFps</c>
    /// → 降一档（<see cref="QualityTier.Low"/> 止步）。
    /// "每一帧都慢"比"平均慢"严格：一次 200ms 的卡顿就能把 120 帧的均值拖到 50fps 以下，但那不是设备不行，
    /// 是加载/GC；只有持续慢才值得牺牲画质。</item>
    /// <item><b>升档</b>：窗口满 且 平均 fps &gt; <c>goodEnoughFps</c> 且 上面还有档
    /// 且 距上次变化 ≥ <c>cooldownFrames</c> 帧 → 升一档（<see cref="QualityTier.High"/> 止步）。
    /// 升档阈值（55）比降档阈值（40）高出一截，就是所谓"迟滞带"：帧率在 40~55 之间时两边都不动，
    /// 避免在临界点来回切档导致画质闪烁。</item>
    /// <item><b>冷却</b>：任何一次变化之后 <c>cooldownFrames</c> 个有效帧内不再变化。
    /// 规格里降档条款没写冷却、只有升档条款写了，但总则"任何变化后 <c>cooldownFrames</c> 帧内不再变化"
    /// 是更硬的约束，本实现按总则执行（否则一个持续慢的窗口会在降档后的下一帧立刻再降一档，
    /// 两帧之内从 High 掉到 Low，玩家看到的是"突然糊成一片"）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>对照哪些输入算"一帧"</b>：<c>&lt;= 0</c>、<c>NaN</c>、<c>±Infinity</c> 一律忽略且<b>不计入</b>窗口。
    /// 它们不是"很快/很慢的帧"，而是"没有测到帧"：暂停（<c>deltaTime = 0</c>）、编辑器失焦、
    /// 测试桩都可能喂出 0；<c>+Infinity</c> 若被计入会把槽位永久钉成 0fps，之后每次决策都被它带偏。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：<b>全同步、非线程安全</b>。采样、决策与 <see cref="IQualitySettingsPort.Apply"/> 都在调用方
    /// 线程（Unity 主线程）串行完成；没有后台计时器，也不捕获任何上下文。多线程喂样本会让
    /// 滑动窗口与计数错乱，因此契约上就不支持。
    /// </para>
    /// <para>
    /// <b>分配</b>：窗口缓冲只在构造期分配一次（<c>double[]</c> + <c>bool[]</c>），<see cref="Sample"/> 路径不分配、
    /// 不装箱、不拼字符串（端口实现自己分配的账不算在本类头上）。
    /// </para>
    /// </summary>
    public sealed class QualityTierSelector
    {
        /// <summary>默认窗口帧数：120 帧 ≈ 2 秒 @60fps，足够滤掉单帧卡顿，又不至于反应太迟。</summary>
        private const int DefaultWindowFrames = 120;

        /// <summary>默认"够好"帧率：高于它才有资格升档（与 40fps 之间留出迟滞带）。</summary>
        private const int DefaultGoodEnoughFps = 55;

        /// <summary>默认"太差"帧率：低于它且整窗都慢才降档。</summary>
        private const int DefaultBadFps = 40;

        /// <summary>默认冷却帧数：300 帧 ≈ 5 秒 @60fps，防止在临界点来回抖。</summary>
        private const int DefaultCooldownFrames = 300;

        /// <summary>
        /// 窗口帧数上限（≈10 分钟 @60fps）：Inspector 里被误填成 <c>int.MaxValue</c> 时，
        /// 按原值分配两个数组会直接 OOM。这里夹到"实际上不可能填满"的长度，
        /// 行为上等价于"永不决策"，但至少不会让 <c>Awake</c> 崩掉。
        /// </summary>
        private const int MaxWindowFrames = 36000;

        /// <summary>
        /// 档位 → 配置的映射表（<b>演示级默认值，不是设备实测最优</b>）：
        /// <list type="bullet">
        /// <item><b>Low</b>：30fps / 200 粒子 / 阴影 0（关）——保底可玩，先把帧率拉回来。</item>
        /// <item><b>Medium</b>：60fps / 500 粒子 / 阴影 1（硬阴影）——主流中端机基线。</item>
        /// <item><b>High</b>：60fps / 1000 粒子 / 阴影 2（全阴影）——旗舰机/编辑器。</item>
        /// </list>
        /// 三档的帧率不是 30/60/90：移动端位移式渲染下 90/120fps 的收益远小于发热代价；
        /// 真正需要高刷的项目改这张表即可，选择器的决策逻辑不受影响。
        /// </summary>
        private static readonly QualityProfile LowProfile = new QualityProfile(QualityTier.Low, 30, 200, 0);

        private static readonly QualityProfile MediumProfile = new QualityProfile(QualityTier.Medium, 60, 500, 1);

        private static readonly QualityProfile HighProfile = new QualityProfile(QualityTier.High, 60, 1000, 2);

        /// <summary>画质落地端口（非空；同档位重复调用必须被它短路）。</summary>
        private readonly IQualitySettingsPort _port;

        /// <summary>窗口长度（已夹到 <c>[1, MaxWindowFrames]</c>）。</summary>
        private readonly int _windowFrames;

        /// <summary>"够好"帧率阈值（<c>&lt;= 0</c> 时任何可测帧率都算够好，等价于关掉迟滞带、窗口一满就升档）。</summary>
        private readonly int _goodEnoughFps;

        /// <summary>"太差"帧率阈值（<c>&lt;= 0</c> 时不判慢，等价于关闭降档）。</summary>
        private readonly int _badFps;

        /// <summary>变化后的冷却帧数（&lt; 0 已夹到 0，即不冷却）。</summary>
        private readonly int _cooldownFrames;

        /// <summary>帧耗时环形窗口（毫秒），长度 == <see cref="_windowFrames"/>；构造后再不扩容。</summary>
        private readonly double[] _samples;

        /// <summary>与 <see cref="_samples"/> 同下标的"该帧是否低于 <c>badFps</c>"标记，用于 O(1) 维护整窗是否全慢。</summary>
        private readonly bool[] _lowFlags;

        /// <summary>下一个写入槽位；窗口填满后它指向最旧的样本。</summary>
        private int _nextSlot;

        /// <summary>窗口内的有效样本数（上限 == 窗口长度）。</summary>
        private int _sampleCount;

        /// <summary>窗口内低于 <c>badFps</c> 的样本数（随淘汰增量维护，避免每帧遍历窗口）。</summary>
        private int _lowInWindow;

        /// <summary>窗口内样本的耗时和（毫秒）。增量维护，淘汰时减去被覆盖的旧值。</summary>
        private double _sumMilliseconds;

        /// <summary>距上次档位变化的有效帧数；从未变化时初始化为冷却时长，表示"冷却已满、可以变化"。</summary>
        private int _framesSinceChange;

        /// <summary>当前档位。</summary>
        private QualityTier _tier = QualityTier.Medium;

        /// <summary>累计变化次数（自构造或最近一次 <see cref="Reset"/> 起）。</summary>
        private int _changeCount;

        /// <summary>
        /// 创建选择器。
        /// <para>
        /// <b>初始档位固定 <see cref="QualityTier.Medium"/>，且构造期<b>不</b>调用 <see cref="IQualitySettingsPort.Apply"/></b>：
        /// 构造函数一旦产生"改画质"的副作用，装配顺序就变成了语义的一部分（谁先 new 谁赢），
        /// 而 <see cref="Sample"/> 的返回值也会失去"这次采样是否改变了档位"的含义。
        /// 初始档取中档是"未知设备"的保守猜测：既不一上来就按低端机砍画质，也不假设自己是旗舰机。
        /// </para>
        /// </summary>
        /// <param name="port">画质落地端口（非 <c>null</c>）。</param>
        /// <param name="windowFrames">滑动窗口帧数（<c>&lt; 1</c> 按 1 处理，过大按 <see cref="MaxWindowFrames"/> 夹取）。</param>
        /// <param name="goodEnoughFps">升档阈值（平均 fps 严格大于它才升）。</param>
        /// <param name="badFps">降档阈值（平均 fps 严格小于它、且整窗都慢才降）。</param>
        /// <param name="cooldownFrames">变化后的冷却帧数（<c>&lt; 0</c> 按 0 处理）。</param>
        /// <exception cref="ArgumentNullException"><paramref name="port"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public QualityTierSelector(
            IQualitySettingsPort port,
            int windowFrames = DefaultWindowFrames,
            int goodEnoughFps = DefaultGoodEnoughFps,
            int badFps = DefaultBadFps,
            int cooldownFrames = DefaultCooldownFrames)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));

            int frames = windowFrames;
            if (frames < 1)
            {
                // 0/负数来自 Inspector 误填或调用方笔误：退化为"每帧都能决策"，
                // 而不是抛异常或让窗口永远填不满（后者会表现为"画质永远不生效"）。
                frames = 1;
            }
            else if (frames > MaxWindowFrames)
            {
                frames = MaxWindowFrames;
            }

            _windowFrames = frames;
            _goodEnoughFps = goodEnoughFps;
            _badFps = badFps;
            _cooldownFrames = cooldownFrames < 0 ? 0 : cooldownFrames;

            _samples = new double[frames];
            _lowFlags = new bool[frames];

            // 没有"上次变化"，冷却视为已满：窗口一填满就可以立即升/降档，不必白白再等一个冷却周期。
            _framesSinceChange = _cooldownFrames;
        }

        /// <summary>当前档位（等于 <see cref="CurrentProfile"/>.<c>Tier</c>）。</summary>
        public QualityTier CurrentTier
        {
            get { return _tier; }
        }

        /// <summary>窗口内平均帧耗时（毫秒）；窗口为空时返回 0。</summary>
        public double AverageFrameMs
        {
            get
            {
                int count = _sampleCount;
                return count == 0 ? 0d : _sumMilliseconds / count;
            }
        }

        /// <summary>累计档位变化次数（自构造或最近一次 <see cref="Reset"/> 起）。</summary>
        public int ChangeCount
        {
            get { return _changeCount; }
        }

        /// <summary>当前档位对应的配置快照（未知档位值按 Medium 兜底，杜绝选择器自己产出脏档位）。</summary>
        public QualityProfile CurrentProfile
        {
            get { return ProfileFor(_tier); }
        }

        /// <summary>喂一帧耗时（毫秒）；返回是否发生了档位变化（变化时已调用 port.Apply）。</summary>
        /// <param name="frameMilliseconds">该帧的真实耗时（毫秒）。<c>&lt;= 0</c>/<c>NaN</c>/<c>±Infinity</c> 会被忽略且不计数。</param>
        /// <returns>本次采样是否改变了档位；<c>true</c> 时已经同步调用过 <see cref="IQualitySettingsPort.Apply"/>。</returns>
        public bool Sample(double frameMilliseconds)
        {
            if (!IsPlausibleFrame(frameMilliseconds))
            {
                // 非法样本连"计数"都不算：否则暂停/失焦会悄悄推进冷却，让本该发生的升降档被吞掉。
                return false;
            }

            Append(frameMilliseconds);
            _framesSinceChange++;

            if (_sampleCount < _windowFrames)
            {
                // 窗口未填满不决策：开局加载、着色器编译的长帧不能作为"这台机器不行"的证据。
                return false;
            }

            double averageMilliseconds = _sumMilliseconds / _sampleCount;
            double averageFps = averageMilliseconds > 0d ? 1000d / averageMilliseconds : double.PositiveInfinity;
            bool cooldownElapsed = _framesSinceChange >= _cooldownFrames;

            if (cooldownElapsed && _tier > QualityTier.Low && averageFps < _badFps && _lowInWindow == _sampleCount)
            {
                // 降档优先于升档：阈值被配反（goodEnoughFps < badFps）时，保帧率比保画质重要。
                return ChangeTo((QualityTier)((byte)_tier - 1));
            }

            if (cooldownElapsed && _tier < QualityTier.High && averageFps > _goodEnoughFps)
            {
                return ChangeTo((QualityTier)((byte)_tier + 1));
            }

            return false;
        }

        /// <summary>
        /// 清空测量状态（窗口、均值、整窗慢计数、冷却计数、变化次数），<b>档位与端口都不动</b>。
        /// <para>
        /// 语义是"换一段负载重新开始测量"（换关卡、换场景、手动重测），<b>不是</b>"恢复出厂画质"：
        /// 把已经生效的档位悄悄改回去，玩家看到的是"刚进新场景画质自己变好了/变差了"，
        /// 比不重置更糟。真的需要回滚档位，请显式调用 <see cref="IQualitySettingsPort.Apply"/>。
        /// </para>
        /// <para>
        /// 重置后冷却同样视为已满（下一次窗口填满即可决策），与构造后的初始状态一致。
        /// </para>
        /// </summary>
        public void Reset()
        {
            Array.Clear(_samples, 0, _samples.Length);
            Array.Clear(_lowFlags, 0, _lowFlags.Length);
            _nextSlot = 0;
            _sampleCount = 0;
            _lowInWindow = 0;
            _sumMilliseconds = 0d;
            _framesSinceChange = _cooldownFrames;
            _changeCount = 0;
        }

        /// <summary>
        /// 把一帧耗时写进环形窗口，并同步维护"耗时和"与"整窗慢计数"。
        /// <para>窗口已满时，写入槽位恰好是最旧样本，先把它从两个累计量里扣掉再覆盖——全程 O(1)、无分配。</para>
        /// </summary>
        private void Append(double milliseconds)
        {
            int slot = _nextSlot;

            if (_sampleCount == _windowFrames)
            {
                _sumMilliseconds -= _samples[slot];
                if (_lowFlags[slot])
                {
                    _lowInWindow--;
                }
            }
            else
            {
                _sampleCount++;
            }

            _samples[slot] = milliseconds;
            _sumMilliseconds += milliseconds;

            // 单帧"慢"的口径与均值判定一致：1000/ms < badFps ⇔ ms * badFps > 1000。
            // 用乘法而不是逐帧除法，既省一次除法，也避免 1000/badFps 的舍入影响边界判定。
            bool low = _badFps > 0 && milliseconds * _badFps > 1000d;
            _lowFlags[slot] = low;
            if (low)
            {
                _lowInWindow++;
            }

            slot++;
            _nextSlot = slot >= _windowFrames ? 0 : slot;
        }

        /// <summary>
        /// 切换档位：先记内部状态，再落地到端口。
        /// <para>
        /// <b>顺序是刻意的</b>：端口若违约抛异常，选择器的账已经推进，异常继续上抛——
        /// 宁可"记了账但引擎没落地"，也不要出现"引擎变了但选择器还以为没变"（后者会导致下一次同档位 Apply 被
        /// 幂等短路，画质永远卡在半路）。契约里要求端口不得抛异常，这里只是把违约后果收敛到一个方向。
        /// </para>
        /// </summary>
        private bool ChangeTo(QualityTier target)
        {
            _tier = target;
            _framesSinceChange = 0;
            _changeCount++;

            QualityProfile profile = ProfileFor(target);
            _port.Apply(in profile);
            return true;
        }

        /// <summary>档位 → 配置。未知枚举值按 Medium 兜底（脏数据不猜语义，也不让选择器产出越界配置）。</summary>
        private static QualityProfile ProfileFor(QualityTier tier)
        {
            switch (tier)
            {
                case QualityTier.Low:
                    return LowProfile;
                case QualityTier.High:
                    return HighProfile;
                default:
                    return MediumProfile;
            }
        }

        /// <summary>
        /// 该值是否是一帧真实耗时：有限、且严格大于 0。
        /// <para>
        /// 不用 <c>double.IsFinite</c>：它到 .NET 5 才有，而本程序集的目标框架是 <c>netstandard2.1</c>
        /// （源码要能原样进 Unity 与 HybridCLR）。
        /// </para>
        /// </summary>
        private static bool IsPlausibleFrame(double milliseconds)
        {
            return milliseconds > 0d && !double.IsNaN(milliseconds) && !double.IsInfinity(milliseconds);
        }
    }
}
