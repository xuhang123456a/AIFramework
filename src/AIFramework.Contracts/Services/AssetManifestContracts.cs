#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 资源清单里的一条：资源名 + 期望内容哈希 + 字节数。
    /// <para>
    /// <b>为什么带字节数</b>：哈希能判"内容变了"，但机器/传输截断时长度不对是更早、更便宜的信号；
    /// 两个都查可以在"下载不完整"和"内容被改"之间给出可区分的错误。
    /// </para>
    /// </summary>
    public readonly struct AssetManifestEntry
    {
        /// <summary>构造一条清单项。</summary>
        /// <param name="name">资源名（相对路径或逻辑名，由加载器解释）。</param>
        /// <param name="sha256Hex">内容 SHA-256（64 位十六进制，大小写不敏感）。</param>
        /// <param name="sizeBytes">期望字节数。</param>
        public AssetManifestEntry(string name, string sha256Hex, int sizeBytes)
        {
            Name = name;
            Sha256Hex = sha256Hex;
            SizeBytes = sizeBytes;
        }

        /// <summary>资源名。</summary>
        public string Name { get; }

        /// <summary>内容 SHA-256（64 位十六进制）。</summary>
        public string Sha256Hex { get; }

        /// <summary>期望字节数。</summary>
        public int SizeBytes { get; }
    }

    /// <summary>资源清单（一次热更/一个包的内容哈希表）。</summary>
    public readonly struct AssetManifest
    {
        /// <summary>构造清单。</summary>
        /// <param name="version">清单版本（内容哈希整体变化时递增，便于日志定位）。</param>
        /// <param name="entries">条目列表。</param>
        public AssetManifest(string version, AssetManifestEntry[] entries)
        {
            Version = version ?? string.Empty;
            Entries = entries ?? new AssetManifestEntry[0];
        }

        /// <summary>清单版本。</summary>
        public string Version { get; }

        /// <summary>条目列表。</summary>
        public AssetManifestEntry[] Entries { get; }
    }

    /// <summary>资源校验结论：哪一条坏、坏在哪，以及要不要回退内置资源。</summary>
    public enum AssetCheckResult : byte
    {
        /// <summary>内容与清单一致。</summary>
        Ok = 0,

        /// <summary>加载器读不到（文件缺失/网络未下发）。</summary>
        Missing = 1,

        /// <summary>读到了但字节数不对（下载截断）。</summary>
        SizeMismatch = 2,

        /// <summary>字节数对但内容哈希不符（被改/串包/清单过期）。</summary>
        HashMismatch = 3,
    }

    /// <summary>一条资源的校验结果。</summary>
    public readonly struct AssetCheckOutcome
    {
        /// <summary>构造结果。</summary>
        /// <param name="name">资源名。</param>
        /// <param name="result">校验结论。</param>
        /// <param name="actualSizeBytes">实际字节数（缺失为 -1）。</param>
        public AssetCheckOutcome(string name, AssetCheckResult result, int actualSizeBytes)
        {
            Name = name;
            Result = result;
            ActualSizeBytes = actualSizeBytes;
        }

        /// <summary>资源名。</summary>
        public string Name { get; }

        /// <summary>校验结论。</summary>
        public AssetCheckResult Result { get; }

        /// <summary>实际字节数（缺失为 -1）。</summary>
        public int ActualSizeBytes { get; }

        /// <summary>是否通过。</summary>
        public bool IsOk
        {
            get { return Result == AssetCheckResult.Ok; }
        }
    }
}
