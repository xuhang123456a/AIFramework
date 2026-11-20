#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>一个资源分包（AssetBundle/分包）描述。</summary>
    public readonly struct AssetBundleSpec
    {
        /// <summary>构造分包描述。</summary>
        /// <param name="name">分包名（唯一）。</param>
        /// <param name="assets">包内资源路径列表。</param>
        /// <param name="dependencies">依赖的分包名。</param>
        /// <param name="sizeBytes">包体大小（字节，用于体积预算）。</param>
        public AssetBundleSpec(string name, string[] assets, string[] dependencies, long sizeBytes)
        {
            Name = name ?? string.Empty;
            Assets = assets ?? new string[0];
            Dependencies = dependencies ?? new string[0];
            SizeBytes = sizeBytes < 0 ? 0 : sizeBytes;
        }

        /// <summary>分包名。</summary>
        public string Name { get; }

        /// <summary>包内资源路径。</summary>
        public string[] Assets { get; }

        /// <summary>依赖的分包名。</summary>
        public string[] Dependencies { get; }

        /// <summary>包体大小（字节）。</summary>
        public long SizeBytes { get; }
    }

    /// <summary>分包清单校验问题。</summary>
    public readonly struct AssetBundleIssue
    {
        /// <summary>构造问题。</summary>
        /// <param name="scope">问题类别（bundle/asset/dependency/size）。</param>
        /// <param name="subject">涉及对象。</param>
        /// <param name="message">说明。</param>
        /// <param name="isError">true = 必须修。</param>
        public AssetBundleIssue(string scope, string subject, string message, bool isError)
        {
            Scope = scope;
            Subject = subject;
            Message = message;
            IsError = isError;
        }

        /// <summary>问题类别。</summary>
        public string Scope { get; }

        /// <summary>涉及对象。</summary>
        public string Subject { get; }

        /// <summary>说明。</summary>
        public string Message { get; }

        /// <summary>是否错误。</summary>
        public bool IsError { get; }

        public override string ToString()
        {
            return (IsError ? "✗ " : "⚠ ") + "[" + Scope + "] " + Subject + "：" + Message;
        }
    }
}
