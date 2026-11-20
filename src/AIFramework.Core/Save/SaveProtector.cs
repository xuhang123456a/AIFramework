#nullable enable
using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using AIFramework.Contracts.Save;

namespace AIFramework.Core.Save
{
    /// <summary>
    /// 存档保护实现：给"明文 AISV 包络字节"加一层 HMAC-SHA256 签名容器，读回时验签再交还原字节。
    /// <para>
    /// <b>为什么是"包裹"而不是改造 <see cref="SaveService"/></b>：AISV 包络已经是落盘格式的事实标准
    /// （Console 文件、Unity <c>persistentDataPath</c>、云端备份、客服手里的旧档全是它）。
    /// 把签名塞进包络里就得动 <see cref="SaveService"/> 的字段布局，等于让所有存量档案同时失效；
    /// 而"外挂一层容器"只需要在读写盘的前后各加一次调用，且<b>旧档天然就是"没有保护容器的 AISV"</b>，
    /// 由适配层按"先试保护容器、失败再按裸 AISV 读"实现平滑升级。
    /// </para>
    /// <para>
    /// <b>容器布局（小端，文件头固定 <see cref="HeaderSize"/> = 52 字节）</b>：
    /// <code>
    /// [0..3]    magic        "AIFS"（4 字节 ASCII）
    /// [4..7]    formatVersion int32   = <see cref="CurrentFormatVersion"/>
    /// [8..15]   deviceSeed    int64   写档时的设备/账号种子（明文存放，不保密）
    /// [16..19]  plainLength   int32   明文长度（>= 1）
    /// [20..35]  keyCheck      "密钥校验值"（见下）
    /// [36..51]  hmac          HMAC-SHA256 的<b>前 16 字节</b>
    /// [52..]    plain         明文负载（典型是一整包 AISV 字节）
    /// </code>
    /// 总长恒为 <c>52 + plainLength</c>。
    /// </para>
    /// <para>
    /// <b>密钥派生</b>：<c>key = SHA256(pepper || deviceSeed 小端 8 字节)</c>，<c>pepper</c> 为 <c>null</c> 或空数组时
    /// 按 <b>32 个 0x00</b> 参与运算，<c>KeySize</c> = 32 字节。之所以再套一层 SHA256 而不是直接用
    /// <c>pepper || seed</c>：种子是 <c>int64</c>（低熵、可枚举），HMAC 的密钥必须是定长且不可预测的；
    /// 先做一次压缩再进 HMAC 是标准做法，也顺手把"pepper 长度任意"这件事统一掉。
    /// </para>
    /// <para>
    /// <b>签名覆盖范围</b>：<c>formatVersion(4) + deviceSeed(8) + plainLength(4) + plain</c>，
    /// <b>不含</b> magic 与签名自身（否则签名要签自己）。三个头部字段都进签名，所以改版本号、改种子、
    /// 改长度字段都会让签名失效；magic 不进签名是有意的——它只做"这是不是本格式"的路由判定，
    /// 错了直接归 <see cref="SaveProtectionResult.Malformed"/>，不必浪费一次 HMAC。
    /// </para>
    /// <para>
    /// <b>32 字节签名域为什么要切成"校验值 16 + 签名 16"</b>（这是本实现唯一一处对"32 字节 HMAC"的细化，
    /// 容器布局与偏移完全不变）：
    /// <b>从密码学上，单靠一个 HMAC 无法区分"密钥不对"与"内容被改"</b>——两者都只是"算出来的值不匹配"。
    /// 而这两种失败的处置完全不同（换设备/换账号 vs 改档/坏档），所以必须额外存一个<b>只依赖密钥、不依赖内容</b>
    /// 的校验值：<c>keyCheck = HMAC(key, "AIFS-KCV" || formatVersion || deviceSeed)</c> 的前 16 字节。
    /// 验签失败时再比它——它不匹配说明是密钥错（<see cref="SaveProtectionResult.WrongKey"/>），
    /// 它匹配说明密钥对、内容被改（<see cref="SaveProtectionResult.Tampered"/>）。
    /// </para>
    /// <para>
    /// <b>这样切开的代价（明确的取舍，不是疏忽）</b>：
    /// </para>
    /// <list type="number">
    /// <item>签名从 256 位降到 128 位：伪造成功概率 2^-128，对抗"改档器"仍是天文数字级别，可接受。</item>
    /// <item>校验值相当于一个"这个密钥对不对"的离线判定器，会泄露<b>极少量</b>密钥信息。因为本项目的前提就是
    /// <b>客户端密钥可提取</b>（pepper 就在包里），攻击者拿到 pepper 本来就能直接算密钥，
    /// 所以它不降低实际安全边界。</item>
    /// <item>校验值本身<b>不在签名覆盖范围内</b>：内容完好时改这 16 字节不影响读档（<see cref="Protect"/> 的
    /// 成功路径根本不看它）。代价是"同时改内容 + 改校验值"的攻击会被判成 <see cref="SaveProtectionResult.WrongKey"/>
    /// 而不是 <see cref="SaveProtectionResult.Tampered"/>——只是文案/流程分岔不同，两种都<b>拒绝加载</b>，不放大风险。</item>
    /// </list>
    /// <para>
    /// <b>这是防篡改，不是防逆向</b>：客户端里必然存在能算出签名的材料（pepper 与 seed 都在安装包里/设备上），
    /// 有决心的攻击者一定能提取并伪造。本类的作用是<b>抬高离线改档的成本</b>，并把"改过"与"换了设备"分清楚；
    /// 任何真正值钱的东西（货币、掉落、排行榜）都必须由服务端做权威校验——服务端可以用同样的
    /// <see cref="DeriveKey"/> + HMAC 复现签名，客户端签名只当"第一道筛子"。
    /// </para>
    /// <para>
    /// <b>密钥不落盘</b>：容器里只有 <c>deviceSeed</c> 与两个 MAC 字段，pepper 与派生密钥一个字节都不写文件；
    /// pepper 由装配层从安全配置注入，构造时<b>拷贝</b>一份（调用方事后改自己的数组不影响本实例）。
    /// </para>
    /// <para>
    /// <b>线程模型：全同步、无状态</b>。构造后本实例只有只读的 pepper 副本，<see cref="Protect"/> /
    /// <see cref="TryUnprotect"/> 不写任何字段，可被多线程并发调用（每次调用各自 <c>using</c> 自己的
    /// <see cref="HMACSHA256"/> 实例，不共享加密对象）。
    /// </para>
    /// <para>
    /// <b>与 <see cref="SaveService"/> 的分工</b>：内层 <see cref="SaveService"/> 的 CRC32 负责"<b>随机损坏</b>"
    /// （断电、半截写、磁盘坏道——它是格式的一部分，且旧档必须有它才读得出来）；外层本类的 HMAC 负责
    /// "<b>有意篡改 + 设备绑定</b>"（CRC32 是公开算法，攻击者改完内容顺手重算 CRC 就绕过了，签名才拦得住）。
    /// 两层各司其职，不重复也不互相替代：签名层<b>不看也不重算</b>内层 CRC，内层也完全不知道外层的存在。
    /// </para>
    /// </summary>
    public sealed class SaveProtector : ISaveProtector
    {
        /// <summary>容器格式版本（写进容器头 <c>[4..7]</c>）。改动字段布局/签名范围必须同时 +1。</summary>
        public const int CurrentFormatVersion = 1;

        /// <summary>magic 长度（<c>"AIFS"</c>）。</summary>
        public const int MagicSize = 4;

        /// <summary>签名域长度：<c>keyCheck</c> + <c>hmac</c> 合计 32 字节（布局与设计文档一致）。</summary>
        public const int MacSize = 32;

        /// <summary>密钥校验值长度（签名域的前 16 字节）。</summary>
        public const int KcvSize = 16;

        /// <summary>HMAC 截断长度（签名域的后 16 字节）。</summary>
        public const int HmacSize = 16;

        /// <summary>文件头长度：magic(4) + 版本(4) + 种子(8) + 长度(4) + 签名域(32) = <b>52</b>。</summary>
        public const int HeaderSize = MacFieldOffset + MacSize;

        /// <summary>派生密钥长度（SHA-256 输出）。</summary>
        public const int KeySize = 32;

        /// <summary>
        /// 单份明文上限（16 MiB）。<b>超限直接抛</b>而不是截断：
        /// 存档是"几十个条目"量级（背包 200 条也就几 KB），上 MiB 基本意味着把资源包/日志/整个目录传了进来；
        /// 这种错误在写盘之前炸出来，比在真机上落成一份谁都解析不了的巨档便宜得多。
        /// </summary>
        public const int MaxPlainLength = 16 * 1024 * 1024;

        /// <summary>成功时的 <c>reason</c>（空串）。</summary>
        public const string ReasonOk = "";

        /// <summary>失败原因：容器为 <c>null</c>（= 没有存档）。</summary>
        public const string ReasonMissing = "存档容器为 null：没有存档。";

        /// <summary>失败原因：容器为 0 字节。</summary>
        public const string ReasonEmpty = "存档容器为 0 字节：外层结构损坏。";

        /// <summary>失败原因：容器短于文件头。</summary>
        public const string ReasonTooShort = "容器长度不足文件头：外层结构损坏。";

        /// <summary>失败原因：magic 不是 <c>"AIFS"</c>。</summary>
        public const string ReasonBadMagic = "magic 不匹配：不是 AIFS 存档保护容器。";

        /// <summary>失败原因前缀：容器格式版本不受支持（其后拼接实际/期望版本号）。</summary>
        public const string ReasonVersionMismatchPrefix = "容器格式版本不受支持：";

        /// <summary>失败原因前缀：长度字段非法（&lt; 1 或 &lt; 0）。</summary>
        public const string ReasonDeclaredLengthInvalidPrefix = "明文长度字段非法：";

        /// <summary>失败原因前缀：长度字段与容器实际长度不符（含明文被截断/尾部多出字节）。</summary>
        public const string ReasonLengthMismatchPrefix = "明文长度字段与容器实际长度不符：";

        /// <summary>失败原因前缀：调用方种子与容器内种子不一致（换设备/换账号/种子字段被改）。</summary>
        public const string ReasonWrongKeySeedPrefix = "密钥错误：调用方 deviceSeed 与容器内不一致（调用方 ";

        /// <summary>失败原因：密钥错误，且种子一致——只剩"pepper 不同"这一种可能。</summary>
        public const string ReasonWrongKeyPepper = "密钥错误：容器由另一套 pepper（密钥派生参数）写出。";

        /// <summary>失败原因：种子一致但签名不符——内容被改。</summary>
        public const string ReasonTampered = "签名不符：内容已被篡改。";

        /// <summary>失败原因：解析过程中出现未预期异常（契约要求绝不外抛，统一降级成这种）。</summary>
        public const string ReasonUnexpected = "解析容器时发生未预期异常：外层结构损坏。";

        /// <summary>容器 magic（ASCII <c>"AIFS"</c>）。</summary>
        private static readonly byte[] Magic = { (byte)'A', (byte)'I', (byte)'F', (byte)'S' };

        /// <summary>密钥校验值的域分隔标签（与签名消息显然是不同的字节串，双重保证不撞）。</summary>
        private static readonly byte[] KcvLabel = { (byte)'A', (byte)'I', (byte)'F', (byte)'S', (byte)'-', (byte)'K', (byte)'C', (byte)'V' };

        private const int KcvLabelSize = 8;

        /// <summary>种子字段宽度（<c>int64</c>）。</summary>
        private const int SeedSize = 8;

        /// <summary>格式版本字段偏移（4）。</summary>
        private const int VersionOffset = MagicSize;                 // 4

        /// <summary>种子字段偏移（8）。</summary>
        private const int SeedOffset = VersionOffset + 4;            // 8

        /// <summary>明文长度字段偏移（16）。</summary>
        private const int LengthOffset = SeedOffset + SeedSize;      // 16

        /// <summary>签名域起始偏移（20，= 密钥校验值偏移）。对外公开：外部工具/服务端复现签名需要它。</summary>
        public const int MacFieldOffset = LengthOffset + 4;          // 20

        /// <summary>密钥校验值偏移（20，签名域前 16 字节）。</summary>
        public const int KeyCheckOffset = MacFieldOffset;            // 20

        /// <summary>HMAC 偏移（36，签名域后 16 字节）。</summary>
        public const int SignatureOffset = KeyCheckOffset + KcvSize; // 36

        /// <summary>签名消息里"头部三字段"的宽度：版本(4) + 种子(8) + 长度(4)。</summary>
        private const int MacMessageHeaderSize = 4 + SeedSize + 4;   // 16

        /// <summary>归一化后的 pepper（<c>null</c>/空 → 32 个 0x00）；构造后只读。</summary>
        private readonly byte[] _pepper;

        /// <summary>创建保护器。</summary>
        /// <param name="pepper">
        /// 密钥派生参数（<b>保密</b>，来自安全配置，不要写进存档、不要进日志）。
        /// <b>长度任意</b>（建议 16–64 字节的随机串）；<c>null</c> 或空数组按"32 个 0x00"处理——
        /// 语义上等于"只用 deviceSeed 派生"，适合内部工具/单机原型，正式发行不要这么用。
        /// <para>
        /// 本构造器会<b>拷贝</b>一份：调用方事后改自己的数组不会影响已构造的实例。
        /// </para>
        /// </param>
        public SaveProtector(byte[]? pepper = null)
        {
            _pepper = NormalizePepper(pepper);
            HasPepper = _pepper.Length != KeySize || !IsAllZero(_pepper);
        }

        /// <summary>
        /// 是否配置了"非空且非全零"的 pepper（<b>只为装配期自检/诊断</b>，不暴露内容）。
        /// <para>为 <c>false</c> 时密钥只由 <c>deviceSeed</c> 决定，任何拿到存档的人都能重构密钥——正式发行应为 <c>true</c>。</para>
        /// </summary>
        public bool HasPepper { get; }

        /// <inheritdoc />
        public int FormatVersion
        {
            get { return CurrentFormatVersion; }
        }

        /// <summary>
        /// 密钥派生：<c>key = SHA256(pepper || deviceSeed 小端 8 字节)</c>。
        /// <para>
        /// <b>为什么公开</b>：服务端/离线工具要<b>复现</b>同一份签名做权威校验，必须能独立算出同一个密钥；
        /// 客户端业务代码不需要它（用 <see cref="Protect"/> / <see cref="TryUnprotect"/> 就够了）。
        /// 公开它不降低安全性——知道方法的攻击者照样需要 pepper。
        /// </para>
        /// </summary>
        /// <param name="pepper">pepper（<c>null</c>/空 → 32 个 0x00）。</param>
        /// <param name="deviceSeed">设备/账号种子。</param>
        /// <returns>32 字节密钥（<b>新数组</b>；调用方负责不要把它写进日志/存档）。</returns>
        public static byte[] DeriveKey(byte[]? pepper, long deviceSeed)
        {
            byte[] normalized = NormalizePepper(pepper);
            var material = new byte[normalized.Length + SeedSize];
            Buffer.BlockCopy(normalized, 0, material, 0, normalized.Length);
            BinaryPrimitives.WriteInt64LittleEndian(material.AsSpan(normalized.Length), deviceSeed);

            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(material);
            }
        }

        /// <summary>
        /// 签名并包装。返回<b>新数组</b>：容器与 <paramref name="plain"/> 不共享缓冲区，
        /// 调用方之后怎么改明文都不会污染已产出的容器。
        /// </summary>
        /// <param name="plain">明文（非 <c>null</c>、非空、&lt;= <see cref="MaxPlainLength"/>）。</param>
        /// <param name="deviceSeed">设备/账号种子（进容器头，可为零/负数：0 表示"不绑设备，只防篡改"）。</param>
        /// <returns>容器字节（长度 = <see cref="HeaderSize"/> + <paramref name="plain"/>.Length）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="plain"/> 为 <c>null</c>。</exception>
        /// <exception cref="ArgumentException"><paramref name="plain"/> 为空数组或超过 <see cref="MaxPlainLength"/>。</exception>
        public byte[] Protect(byte[] plain, long deviceSeed)
        {
            if (plain == null)
            {
                // 用子类而非 ArgumentException：null 与"空/超长"是两种不同的装配期错误，堆栈里能一眼分开。
                throw new ArgumentNullException(nameof(plain), "明文不能为 null：空存档无意义。");
            }

            if (plain.Length == 0)
            {
                throw new ArgumentException("明文不能为空：空存档无意义（写一份读回来必须被拒绝的容器没有价值）。", nameof(plain));
            }

            if (plain.Length > MaxPlainLength)
            {
                throw new ArgumentException(
                    "明文超过单档上限 " + MaxPlainLength + " 字节（实际 " + plain.Length + "）：这不像存档，拒绝写。",
                    nameof(plain));
            }

            byte[] key = DeriveKey(_pepper, deviceSeed);
            byte[] keyCheck = ComputeKeyCheck(key, CurrentFormatVersion, deviceSeed);
            byte[] hmac = ComputeHmac(key, CurrentFormatVersion, deviceSeed, plain.Length, plain, 0);

            var container = new byte[HeaderSize + plain.Length];
            Buffer.BlockCopy(Magic, 0, container, 0, MagicSize);
            BinaryPrimitives.WriteInt32LittleEndian(container.AsSpan(VersionOffset), CurrentFormatVersion);
            BinaryPrimitives.WriteInt64LittleEndian(container.AsSpan(SeedOffset), deviceSeed);
            BinaryPrimitives.WriteInt32LittleEndian(container.AsSpan(LengthOffset), plain.Length);
            Buffer.BlockCopy(keyCheck, 0, container, KeyCheckOffset, KcvSize);
            // 只取 HMAC-SHA256 的前 16 字节（见类注释"32 字节签名域为什么要切开"）。
            Buffer.BlockCopy(hmac, 0, container, SignatureOffset, HmacSize);
            Buffer.BlockCopy(plain, 0, container, HeaderSize, plain.Length);

            return container;
        }

        /// <summary>
        /// 校验并解包。<b>任何可预期的失败都落成返回值</b>，绝不抛给调用方（连未预期的异常也被兜成
        /// <see cref="SaveProtectionResult.Malformed"/>）。
        /// <para>
        /// <b>判定顺序（固定，不可换）</b>：长度 → magic → 版本 → 长度字段 → <b>种子</b> → 签名 → 校验值。
        /// 种子比较必须早于签名：签名是用<b>容器自己的种子</b>算的，若先算签名再比较种子，换设备场景会被
        /// 误判成"内容被改"（这正是要避免的线上误报）。
        /// </para>
        /// </summary>
        /// <param name="container">容器字节（可为 <c>null</c> → <see cref="SaveProtectionResult.Missing"/>）。</param>
        /// <param name="deviceSeed">本机/本账号种子。</param>
        /// <param name="plain">成功时是明文的新数组；失败时恒为 <c>null</c>。</param>
        /// <param name="reason">失败原因（成功时为空串）。</param>
        /// <returns>保护结果。</returns>
        public SaveProtectionResult TryUnprotect(byte[]? container, long deviceSeed, out byte[]? plain, out string reason)
        {
            plain = null;

            if (container == null)
            {
                reason = ReasonMissing;
                return SaveProtectionResult.Missing;
            }

            try
            {
                return UnprotectCore(container, deviceSeed, out plain, out reason);
            }
            catch (Exception)
            {
                // 契约要求"绝不抛给调用方"：平台裁剪、AOT 限制、意外的加密实现异常都统一降级成"结构损坏"。
                // 读档发生在启动路径上，一条坏档把进程打断比"这份档读不出来"严重得多。
                plain = null;
                reason = ReasonUnexpected;
                return SaveProtectionResult.Malformed;
            }
        }

        /// <summary>真正的解包逻辑（已被 <see cref="TryUnprotect"/> 包在异常兜底里）。</summary>
        private SaveProtectionResult UnprotectCore(byte[] container, long deviceSeed, out byte[]? plain, out string reason)
        {
            plain = null;

            if (container.Length == 0)
            {
                reason = ReasonEmpty;
                return SaveProtectionResult.Malformed;
            }

            if (container.Length < HeaderSize)
            {
                reason = ReasonTooShort;
                return SaveProtectionResult.Malformed;
            }

            if (container[0] != Magic[0] || container[1] != Magic[1] || container[2] != Magic[2] || container[3] != Magic[3])
            {
                reason = ReasonBadMagic;
                return SaveProtectionResult.Malformed;
            }

            int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(VersionOffset));
            if (formatVersion != CurrentFormatVersion)
            {
                reason = ReasonVersionMismatchPrefix + "v" + formatVersion + "（当前 v" + CurrentFormatVersion + "）。";
                return SaveProtectionResult.UnsupportedVersion;
            }

            long containerSeed = BinaryPrimitives.ReadInt64LittleEndian(container.AsSpan(SeedOffset));
            int declaredLength = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(LengthOffset));

            // 长度字段必须先做"非负且非零"检查：否则 52 字节的容器 + 声明 0 字节能通过下面的等式，
            // 得到一个"零字节明文"的合法外观（空存档无意义，见 Protect 的入参约束）。
            if (declaredLength < 1)
            {
                reason = ReasonDeclaredLengthInvalidPrefix + "声明 " + declaredLength + " 字节（必须 >= 1）。";
                return SaveProtectionResult.Malformed;
            }

            // 上限闸门必须放在"验签"之前：验签本身要按 declaredLength 拼一份签名消息，
            // 一个声明 2 GiB 的恶意文件会在被拒绝之前先把内存打爆（本地存档也怕"垃圾文件"）。
            if (declaredLength > MaxPlainLength)
            {
                reason = ReasonDeclaredLengthInvalidPrefix + "声明 " + declaredLength + " 字节，超过单档上限 " + MaxPlainLength + " 字节。";
                return SaveProtectionResult.Malformed;
            }

            // 用 long 求和：declaredLength 是攻击者可写的 int，52 + int.MaxValue 在 int 里会溢出成负数，
            // 一旦溢出成"刚好等于容器长度"就会把截断的档放过去。
            if ((long)HeaderSize + declaredLength != container.Length)
            {
                reason = ReasonLengthMismatchPrefix + "声明 " + declaredLength + " 字节，容器实际 "
                    + (container.Length - HeaderSize) + " 字节。";
                return SaveProtectionResult.Malformed;
            }

            // 种子判定在签名之前：换设备/换账号是"内容完好但本机解不开"，绝不能误报成"内容被改"。
            if (containerSeed != deviceSeed)
            {
                reason = ReasonWrongKeySeedPrefix + deviceSeed + "，容器内 " + containerSeed + "）。";
                return SaveProtectionResult.WrongKey;
            }

            byte[] key = DeriveKey(_pepper, deviceSeed);
            byte[] expectedHmac = ComputeHmac(key, formatVersion, containerSeed, declaredLength, container, HeaderSize);

            if (!FixedTimeEquals(expectedHmac, 0, container, SignatureOffset, HmacSize))
            {
                // 签名不符时再分一次"密钥错"还是"内容被改"：
                // 校验值只依赖密钥与头部（不依赖内容），它也不符 → 密钥不对（pepper 不同）。
                byte[] expectedKeyCheck = ComputeKeyCheck(key, formatVersion, containerSeed);
                if (!FixedTimeEquals(expectedKeyCheck, 0, container, KeyCheckOffset, KcvSize))
                {
                    reason = ReasonWrongKeyPepper;
                    return SaveProtectionResult.WrongKey;
                }

                reason = ReasonTampered;
                return SaveProtectionResult.Tampered;
            }

            // 验签通过之后才分配/复制明文：坏档不产生一份"看起来能用"的负载。
            var payload = new byte[declaredLength];
            Buffer.BlockCopy(container, HeaderSize, payload, 0, declaredLength);
            plain = payload;
            reason = ReasonOk;
            return SaveProtectionResult.Ok;
        }

        /// <summary>
        /// 计算签名消息的 HMAC-SHA256：消息 = <c>版本(4) + 种子(8) + 长度(4) + 明文</c>。
        /// <para>
        /// <paramref name="plainOffset"/> 让验签路径能直接拿容器里的明文片段，不必先复制一份
        /// （<see cref="HMACSHA256.ComputeHash(byte[])"/> 只吃整数组，所以消息仍要拼一次；
        /// 但省掉"先复制明文再拼消息"的第二次整段复制）。
        /// </para>
        /// </summary>
        private static byte[] ComputeHmac(byte[] key, int formatVersion, long deviceSeed, int declaredLength, byte[] plain, int plainOffset)
        {
            var message = new byte[MacMessageHeaderSize + declaredLength];
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(0), formatVersion);
            BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(4), deviceSeed);
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(12), declaredLength);
            Buffer.BlockCopy(plain, plainOffset, message, MacMessageHeaderSize, declaredLength);

            return HmacSha256(key, message);
        }

        /// <summary>
        /// 计算"密钥校验值"：<c>HMAC(key, "AIFS-KCV" || 版本(4) || 种子(8))</c> 的前 <see cref="KcvSize"/> 字节。
        /// <para>
        /// <b>它刻意不含明文长度与明文内容</b>：只有"不含内容"才能回答"这个密钥对不对"，
        /// 含了内容就会把"内容被改"也变成校验值不符，白分一场。
        /// </para>
        /// </summary>
        private static byte[] ComputeKeyCheck(byte[] key, int formatVersion, long deviceSeed)
        {
            var message = new byte[KcvLabelSize + 4 + SeedSize];
            Buffer.BlockCopy(KcvLabel, 0, message, 0, KcvLabelSize);
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(KcvLabelSize), formatVersion);
            BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(KcvLabelSize + 4), deviceSeed);

            byte[] full = HmacSha256(key, message);
            var keyCheck = new byte[KcvSize];
            Buffer.BlockCopy(full, 0, keyCheck, 0, KcvSize);
            return keyCheck;
        }

        private static byte[] HmacSha256(byte[] key, byte[] message)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(message);
            }
        }

        /// <summary>
        /// 常量时间比较（<b>手写</b>：<c>netstandard2.1</c> 里没有 <c>CryptographicOperations.FixedTimeEquals</c>）。
        /// <para>
        /// 本地存档被计时攻击的风险很低，但签名比较用"提前 return"的写法是加密代码里的经典坏习惯，
        /// 这里不给自己留这个口子——逐字节异或累加，耗时与"第几个字节不同"无关。
        /// </para>
        /// </summary>
        private static bool FixedTimeEquals(byte[] left, int leftOffset, byte[] right, int rightOffset, int length)
        {
            int diff = 0;
            for (int i = 0; i < length; i++)
            {
                diff |= left[leftOffset + i] ^ right[rightOffset + i];
            }

            return diff == 0;
        }

        /// <summary>
        /// pepper 归一化：<c>null</c> / 空数组 → 32 个 0x00；否则<b>拷贝</b>一份（防调用方事后改数组）。
        /// </summary>
        private static byte[] NormalizePepper(byte[]? pepper)
        {
            if (pepper == null || pepper.Length == 0)
            {
                return new byte[KeySize];
            }

            var copy = new byte[pepper.Length];
            Buffer.BlockCopy(pepper, 0, copy, 0, pepper.Length);
            return copy;
        }

        private static bool IsAllZero(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
