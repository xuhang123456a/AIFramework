#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Ports;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 资源加载端口：把 key 当成 <c>Application.streamingAssetsPath</c> 下的相对路径直读字节。
    /// <para>
    /// <b>为什么只用它做打包期/开发期通道</b>：<c>StreamingAssets</c> 在编辑器与 PC/主机上是普通目录，
    /// 但在 <b>Android 上它位于 APK 内部的 jar 里，不是文件系统路径</b>，必须经 UnityWebRequest 或
    /// AssetBundle 才能读；iOS 同样不保证是普通文件。所以本类在真机上迟早要整体替换成
    /// YooAsset / Addressables 实现——接口 <see cref="IAssetLoader"/> 不变，调用方一行都不用改。
    /// </para>
    /// <para>
    /// 失败语义与 <see cref="UnityStoragePort"/> 刻意不同：资源缺失属于"打包/部署问题"，
    /// 按契约直接抛 <see cref="FileNotFoundException"/> 在装配期炸出来，<b>绝不返回空数组兜底</b>。
    /// </para>
    /// <para>
    /// 线程模型：单线程、无状态、无缓存，不做并发控制（真机替换实现后另说）。
    /// </para>
    /// </summary>
    public sealed class UnityAssetLoader : IAssetLoader
    {
        /// <summary>
        /// 根目录绝对路径，<b>带尾部分隔符</b>。为什么要带：越界判定用的是字符串前缀比较，
        /// 少了分隔符会让 <c>/assets-other</c> 被误判成位于 <c>/assets</c> 之内。
        /// </summary>
        private readonly string _rootDirectory;

        /// <summary>根目录取 <see cref="Application.streamingAssetsPath"/> 本身。</summary>
        public UnityAssetLoader()
            : this(null)
        {
        }

        /// <summary>
        /// 根目录取 <c>Application.streamingAssetsPath/&lt;subDirectory&gt;</c>。
        /// <remarks>
        /// <b>构造期不校验根目录是否存在</b>：构造只记路径，真正访问资源时才判定。
        /// 这样"工程里没有 StreamingAssets"只会打断用到资源的切片（如签到子表），
        /// 不会顺手把总线、会话宿主与战斗沙盒一起拖死；而错误照样在首次访问时以
        /// <see cref="DirectoryNotFoundException"/> 明确抛出，不会静默降级。
        /// </remarks>
        /// </summary>
        /// <param name="subDirectory">StreamingAssets 下的子目录名；传 <c>null</c> 或空串表示直接用 StreamingAssets 根。</param>
        public UnityAssetLoader(string? subDirectory)
        {
            string root = Application.streamingAssetsPath;

            string? requestedSubDirectory = subDirectory;
            if (requestedSubDirectory != null && requestedSubDirectory.Length > 0)
            {
                root = Path.Combine(root, requestedSubDirectory);
            }

            _rootDirectory = EnsureTrailingSeparator(Path.GetFullPath(root));
        }

        /// <summary>根目录绝对路径（诊断用）。</summary>
        public string RootDirectory
        {
            get { return _rootDirectory; }
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentException">键为空，或规范化后越出根目录。</exception>
        /// <exception cref="DirectoryNotFoundException">根目录不存在（打包/部署遗漏，或该平台不是普通目录）。</exception>
        /// <exception cref="FileNotFoundException">资源不存在，或该键指向一个目录。</exception>
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
        /// <exception cref="ArgumentException">键为空，或规范化后越出根目录。</exception>
        /// <exception cref="DirectoryNotFoundException">根目录不存在。</exception>
        public bool Exists(string key)
        {
            return File.Exists(ResolvePath(key));
        }

        /// <summary>校验键并把相对 key 规范化成绝对路径，越界即抛。</summary>
        /// <exception cref="DirectoryNotFoundException">
        /// 根目录不存在（工程里没有 StreamingAssets，或该平台不把它当普通目录，典型：Android）。
        /// </exception>
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

            // 每次访问都判一次根目录：StreamingAssets 可能被工程配置/打包脚本在运行前才创建，
            // 构造期缓存一个布尔值反而会让"先 Create 目录再读"的流程失效。
            if (!Directory.Exists(_rootDirectory))
            {
                throw new DirectoryNotFoundException(
                    "资源根目录不存在或不是普通目录：" + _rootDirectory
                    + "。Android 上 StreamingAssets 位于 APK 内，本实现不适用，需替换为 AssetBundle/YooAsset 实现。");
            }

            // GetFullPath 会把 "A/../../x" 归一化掉，因此"归一化之后再判前缀"是唯一可靠的越界检查；
            // 绝对路径（如 "/etc/passwd"）在 Path.Combine 里会顶掉根目录，同样被前缀判定拦下。
            string fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, key));
            if (!fullPath.StartsWith(_rootDirectory, StringComparison.Ordinal))
            {
                throw new ArgumentException("资源键越出根目录：" + key, nameof(key));
            }

            return fullPath;
        }

        /// <summary>补尾部分隔符，避免前缀判定把同级目录误认成子目录。</summary>
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
