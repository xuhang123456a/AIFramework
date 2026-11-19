#nullable enable
using AIFramework.Contracts.Ports;
using System;
using System.IO;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 本地目录资源加载端口：<c>key</c> 视为相对路径（如 <c>A/B/file.bin</c>），
    /// 规范化后必须仍位于根目录之内，越界一律按参数错误拒绝。
    /// <para>
    /// 与 <see cref="FileSystemStoragePort"/> 的失败语义刻意不同：资源缺失属于"打包问题"，
    /// 按 <see cref="IAssetLoader"/> 契约直接抛 <see cref="FileNotFoundException"/>，在启动期就炸出来，
    /// 绝不返回空数组兜底。
    /// </para>
    /// <para>
    /// 线程模型：<b>单线程使用，装配期初始化</b>；本类无状态、无缓存，不做并发控制。
    /// </para>
    /// </summary>
    public sealed class DirectoryAssetLoader : IAssetLoader
    {
        /// <summary>根目录绝对路径（带尾部分隔符，用于前缀判定）。</summary>
        private readonly string _rootDirectory;

        /// <summary>
        /// 以 <paramref name="rootDirectory"/> 为资源根目录。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> 为 <c>null</c>。</exception>
        /// <exception cref="DirectoryNotFoundException">根目录不存在（打包/部署遗漏，必须在启动期暴露）。</exception>
        public DirectoryAssetLoader(string rootDirectory)
        {
            if (rootDirectory == null)
            {
                throw new ArgumentNullException(nameof(rootDirectory));
            }

            if (!Directory.Exists(rootDirectory))
            {
                throw new DirectoryNotFoundException("资源根目录不存在：" + rootDirectory);
            }

            _rootDirectory = EnsureTrailingSeparator(Path.GetFullPath(rootDirectory));
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentException">键为空或规范化后越出根目录。</exception>
        /// <exception cref="FileNotFoundException">资源不存在。</exception>
        public byte[] LoadBytes(string key)
        {
            string path = ResolvePath(key);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("资源缺失：" + key, path);
            }

            return File.ReadAllBytes(path);
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentException">键为空或规范化后越出根目录。</exception>
        public bool Exists(string key)
        {
            return File.Exists(ResolvePath(key));
        }

        /// <summary>校验键并把相对 key 规范化为绝对路径，越界即抛。</summary>
        private string ResolvePath(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (key.Length == 0)
            {
                throw new ArgumentException("资源键不能为空。", nameof(key));
            }

            string fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, key));
            if (!fullPath.StartsWith(_rootDirectory, StringComparison.Ordinal))
            {
                throw new ArgumentException("资源键越出根目录：" + key, nameof(key));
            }

            return fullPath;
        }

        /// <summary>补尾部分隔符，避免 "root" 前缀误匹配 "root2"。</summary>
        private static string EnsureTrailingSeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
