#nullable enable
namespace AIFramework.Contracts.Save
{
    /// <summary>
    /// 存档保护（签名 / 完整性）结果。
    /// <para>
    /// <b>为什么是一个枚举而不是 <c>bool</c> 或异常</b>：读档失败必须先把"没有存档 / 内容被改 / 密钥不对 /
    /// 存档是未来版本 / 外层结构坏"分开，才能决定下一步是"新档开局"、"提示云端覆盖"、"引导换回原账号/原设备"
    /// 还是"上报排查"。压成 bool 的后果是线上只剩一句"存档读不出来"，玩家投诉时无从定位。
    /// </para>
    /// <para>
    /// <b>哪些值算"存档坏了"</b>：只有 <see cref="Tampered"/> 与 <see cref="Malformed"/> 意味着字节不可信。
    /// <see cref="Missing"/> 是正常的"还没有存档"，<see cref="WrongKey"/> 是密钥参数不匹配（内容可能完好），
    /// <see cref="UnsupportedVersion"/> 只是版本不匹配——后三者<b>都不应该触发"删档重来"</b>。
    /// </para>
    /// <para>数值固定（会进日志 / 埋点 / 申诉工单），只允许追加，不允许调整既有值。</para>
    /// </summary>
    public enum SaveProtectionResult : byte
    {
        /// <summary>签名校验通过：解包出的明文可以直接交给存档层。</summary>
        Ok = 0,

        /// <summary>
        /// 没有容器（入参为 <c>null</c>）。按"新玩家 / 新档"处理，<b>不要</b>当错误上报。
        /// </summary>
        Missing = 1,

        /// <summary>
        /// 签名不符，且密钥是<b>对的</b>（容器自带 <c>deviceSeed</c> 与调用方传入的一致）：
        /// 内容确实被改过（改档器 / 传输损坏 / 手工编辑）。<b>不要加载</b>，按坏档处理并上报。
        /// </summary>
        Tampered = 2,

        /// <summary>
        /// 密钥不对：容器自带 <c>deviceSeed</c> 与调用方传入的不同（换了设备 / 换了账号 / 存档文件被搬到另一台机器），
        /// 或写档时用的 <c>pepper</c> 与本实例不同（换了发行渠道、密钥轮换后没做旧档兼容）。
        /// <para>
        /// <b>它不等于"存档坏了"</b>：内容很可能完好，只是本机解不开。别急着删档——引导玩家换回原设备/账号，
        /// 或走云端存档覆盖，是比"清档重来"正确得多的处置。
        /// </para>
        /// </summary>
        WrongKey = 3,

        /// <summary>
        /// 容器格式版本不受支持（与本实现的 <see cref="ISaveProtector.FormatVersion"/> 不一致）。
        /// 拒绝半解析：旧客户端读新格式必须先升级，新客户端读旧格式必须走自己的兼容分支。
        /// </summary>
        UnsupportedVersion = 4,

        /// <summary>
        /// 外层结构损坏：长度不足文件头、magic 不符、长度字段与实际字节数不符、明文被截断、长度字段越界。
        /// 按坏档处理（这类失败通常来自断电半截写、磁盘满、云端半截响应）。
        /// </summary>
        Malformed = 5
    }

    /// <summary>
    /// 存档保护端口：把<b>明文 AISV 字节</b>包成"带签名的容器"，读回时验签再交还明文。
    /// <para>
    /// <b>它在框架里的位置</b>：只吃 <c>byte[]</c>、只吐 <c>byte[]</c>，不认识 <c>SaveService</c>、不认识存储端口，
    /// 也不知道负载里装的是背包还是剧情进度。因此它既能包在 <c>SaveService</c> 外面（最常见：
    /// <c>SaveService.Save</c> 写出的 AISV 字节再保护一层），也能包在存储端口里（写盘前保护、读盘后验签），
    /// 两种装配方式对调用方是同一套语义。
    /// </para>
    /// <para>
    /// <b>它到底防什么</b>：防"改了本地存档文件让自己多几个道具"这类<b>离线小改</b>，并且能区分"内容被改"与
    /// "换了设备/账号"两种完全不同的处置路径。它<b>不</b>防逆向：客户端里必然存在能算出签名的密钥材料，
    /// 有决心的攻击者一定能提取并伪造。真正的防作弊必须由服务端做权威校验（见
    /// <c>ISaveProtector</c> 实现的文档注释）。
    /// </para>
    /// <para>
    /// <b>实现必须遵守的硬约束</b>（这几条是数据安全，不是风格）：
    /// </para>
    /// <list type="number">
    /// <item><see cref="TryUnprotect"/> <b>绝不抛异常</b>：缺档、坏档、版本不符、密钥不对一律落成返回值 +
    /// <c>reason</c>。读档发生在启动路径上，不允许因为一份坏档把进程打断。</item>
    /// <item><b>不修改入参</b>：容器与明文数组的内容与长度调用后保持原样（调用方可能要拿原始字节做备份/上报）。</item>
    /// <item><b>密钥不落盘</b>：容器里只放 <c>deviceSeed</c> 与签名，pepper / 派生密钥一个字节都不进文件。</item>
    /// <item><b>全同步</b>：不含 IO、不含随机、不读时钟，因此同一输入永远同一输出（可被无头单测逐字节钉死）。</item>
    /// <item><see cref="Protect"/> 的<b>入参非法要抛</b>（<c>null</c> / 空明文 / 超上限）：那是装配期编程错误，
    /// 写坏档比抛异常危险得多。</item>
    /// </list>
    /// </summary>
    public interface ISaveProtector
    {
        /// <summary>
        /// 容器格式版本（写进容器头，也用它判定 <see cref="SaveProtectionResult.UnsupportedVersion"/>）。
        /// <b>它只管"容器布局"</b>，与存档 schema 版本（<c>SaveService.CurrentVersion</c>）是两回事：
        /// 前者变了意味着"这段字节读不出来了"，后者变了意味着"读出来了但要迁移"。
        /// </summary>
        int FormatVersion { get; }

        /// <summary>
        /// 签名并包装：返回容器字节（<b>新数组</b>，不与入参共享缓冲区）。
        /// </summary>
        /// <param name="plain">明文负载（典型是 <c>SaveService</c> 写出的 AISV 整包字节）。</param>
        /// <param name="deviceSeed">
        /// 设备/账号粒度的随机种子（由适配层从设备指纹、安装 id 或账号 id 派生）。
        /// <b>它进容器头</b>，所以换了它等于换了密钥：同一个 <paramref name="plain"/> 换 seed 就会得到完全不同的容器。
        /// </param>
        /// <returns>容器字节。</returns>
        /// <exception cref="System.ArgumentException">
        /// <paramref name="plain"/> 为 <c>null</c>（<see cref="System.ArgumentNullException"/> 子类）、为空数组、或超过实现上限。
        /// <b>空存档无意义</b>：写一份"什么都没有"的保护容器，读回来只能得到一个必须被拒绝的坏档。
        /// </exception>
        byte[] Protect(byte[] plain, long deviceSeed);

        /// <summary>
        /// 校验并解包。失败返回原因，<b>绝不抛给调用方</b>。
        /// </summary>
        /// <param name="container">
        /// 容器字节。<b>允许为 <c>null</c></b>（= 这个槽位没有存档）→ <see cref="SaveProtectionResult.Missing"/>。
        /// </param>
        /// <param name="deviceSeed">
        /// 调用方（本机/本账号）的种子。与容器内记录的不一致时判 <see cref="SaveProtectionResult.WrongKey"/>
        /// ——注意此时<b>不会</b>用调用方的 seed 去重算签名然后报"篡改"，那会把"换设备"误报成"改档"。
        /// </param>
        /// <param name="plain">成功时是明文的新数组（调用方拥有）；<b>失败时恒为 <c>null</c></b>。</param>
        /// <param name="reason">失败原因（人类可读；成功时为空串）。<b>禁止按文案分支</b>，只按返回值分支。</param>
        /// <returns>保护结果。</returns>
        SaveProtectionResult TryUnprotect(byte[]? container, long deviceSeed, out byte[]? plain, out string reason);
    }
}
