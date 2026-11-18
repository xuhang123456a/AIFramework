#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Ports;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 本地存档端口：每个键落盘为根目录下的同名文件（<c>&lt;root&gt;/&lt;key&gt;</c>）。
    /// <para>
    /// <b>为什么必须原子写</b>：手机在写档途中被系统杀掉、PC 在写档途中蓝屏都会留下半个文件；
    /// 半个存档比没有存档更糟——它会让"文件存在"被误判成"存档有效"。因此固定流程是
    /// "先写 <c>&lt;key&gt;.tmp</c> 并刷盘 → <see cref="File.Replace(string, string, string)"/> 原子替换"，
    /// 只有目标文件还不存在时才退成 <see cref="File.Move(string, string)"/>（没有旧文件可替换，
    /// 这一步本身就是原子的）。
    /// </para>
    /// <para>
    /// <b>为什么键名白名单是 <c>[A-Za-z0-9._-]</c></b>：白名单挡不住 <c>..</c>，所以额外显式拒绝含 <c>..</c>
    /// 或等于 <c>"."</c> 的键——这是唯一的目录穿越入口。同时拒绝以 <c>.tmp</c> 结尾的键：
    /// 它会和临时文件同名空间撞车，而 <see cref="ListKeys"/> 又按约定过滤 <c>.tmp</c>，
    /// 结果是"写进去了却永远列不出来"，属于必须提前拦掉的死路。
    /// </para>
    /// <para>
    /// 线程模型：单线程使用（Unity 主线程 / 单一存档服务串行调用），不加锁、不支持多进程并发写同一目录。
    /// </para>
    /// </summary>
    public sealed class UnityStoragePort : IStoragePort
    {
        /// <summary>临时文件后缀：先写它，再原子替换成正式文件。</summary>
        private const string TempExtension = ".tmp";

        /// <summary>存档根目录绝对路径，构造后不再变化。</summary>
        private readonly string _rootDirectory;

        /// <summary>根目录取 <see cref="Application.persistentDataPath"/> 本身。</summary>
        public UnityStoragePort()
            : this(null)
        {
        }

        /// <summary>
        /// 以 <c>Application.persistentDataPath/&lt;subDirectory&gt;</c> 为根；
        /// 目录不存在则创建，创建失败（无权限 / 路径非法）的异常直接冒泡，在装配期暴露。
        /// </summary>
        /// <param name="subDirectory">persistentDataPath 下的子目录名；传 <c>null</c> 或空串表示直接用 persistentDataPath。</param>
        /// <exception cref="ArgumentException"><paramref name="subDirectory"/> 含非法字符或试图越出 persistentDataPath。</exception>
        public UnityStoragePort(string? subDirectory)
        {
            string root = Application.persistentDataPath;

            string? requestedSubDirectory = subDirectory;
            if (requestedSubDirectory != null && requestedSubDirectory.Length > 0)
            {
                ValidateName(requestedSubDirectory, nameof(subDirectory), "子目录名");
                root = Path.Combine(root, requestedSubDirectory);
            }

            Directory.CreateDirectory(root);
            _rootDirectory = Path.GetFullPath(root);
        }

        /// <summary>存档根目录绝对路径（诊断用；正式代码不应绕过本类直接读写它）。</summary>
        public string RootDirectory
        {
            get { return _rootDirectory; }
        }

        /// <inheritdoc />
        public bool Exists(string key)
        {
            return File.Exists(ResolvePath(key));
        }

        /// <inheritdoc />
        /// <remarks>键存在但内容为 0 字节时返回空数组（不是 <c>null</c>），"空档"判定交给存档服务。</remarks>
        public byte[]? Read(string key)
        {
            string path = ResolvePath(key);
            if (!File.Exists(path))
            {
                return null;
            }

            return File.ReadAllBytes(path);
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="data"/> 为 <c>null</c>。</exception>
        public void Write(string key, byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            string targetPath = ResolvePath(key);
            string tempPath = targetPath + TempExtension;

            try
            {
                // FileShare.None：写临时文件期间不允许别人抢读半个档；Flush(true) 让字节真正落到介质，
                // 否则进程崩溃时操作系统缓存里的内容会一起丢，"原子"就只剩文件系统层面的假象。
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }

                Replace(tempPath, targetPath);
            }
            finally
            {
                // 失败路径也必须收尾：残留的 .tmp 会被下一次 ListKeys 过滤掉，但会长期占盘。
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>键不存在时静默成功（与 <see cref="IStoragePort.Delete"/> 契约一致）。</remarks>
        public void Delete(string key)
        {
            string path = ResolvePath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 序数升序（<c>Z</c> 排在 <c>a</c> 之前），不受运行机器区域设置影响；
        /// 以 <c>.tmp</c> 结尾的文件一律不返回。
        /// </remarks>
        public string[] ListKeys()
        {
            string[] files = Directory.GetFiles(_rootDirectory);
            var keys = new List<string>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);
                if (name.Length == 0 || name.EndsWith(TempExtension, StringComparison.Ordinal))
                {
                    continue;
                }

                keys.Add(name);
            }

            keys.Sort(StringComparer.Ordinal);
            return keys.ToArray();
        }

        /// <summary>
        /// 原子替换：目标存在走 <see cref="File.Replace(string, string, string)"/>（不产生中间态）；
        /// 目标不存在走 <see cref="File.Move(string, string)"/>。
        /// </summary>
        /// <remarks>
        /// 部分平台（历史上某些 Mono / 只读文件系统）没有实现 <c>File.Replace</c>，会抛
        /// <see cref="NotSupportedException"/>；此时退成"先删后移动"，虽然丢掉"原子"这一步，
        /// 但至少不会让写档整体失败——这条兜底路径必须在日志里能识别出来（异常本身即信号）。
        /// </remarks>
        private static void Replace(string tempPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            try
            {
                File.Replace(tempPath, targetPath, null);
            }
            catch (NotSupportedException)
            {
                File.Delete(targetPath);
                File.Move(tempPath, targetPath);
            }
        }

        /// <summary>校验键名并映射到绝对路径；这是防目录穿越的唯一入口。</summary>
        private string ResolvePath(string key)
        {
            ValidateName(key, nameof(key), "存档键");
            return Path.Combine(_rootDirectory, key);
        }

        /// <summary>键名/子目录名白名单校验，规则见类注释。</summary>
        private static void ValidateName(string name, string parameterName, string displayName)
        {
            if (name == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (name.Length == 0)
            {
                throw new ArgumentException(displayName + "不能为空。", parameterName);
            }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool allowed = (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '.'
                    || c == '_'
                    || c == '-';
                if (!allowed)
                {
                    throw new ArgumentException(
                        displayName + "只允许 [A-Za-z0-9._-]，非法字符 '" + c + "'。", parameterName);
                }
            }

            if (name == "." || name.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException(displayName + "不允许是 \".\" 或包含 \"..\"（目录穿越防护）。", parameterName);
            }

            if (name.EndsWith(TempExtension, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    displayName + "不允许以 \"" + TempExtension + "\" 结尾（与临时文件同名空间冲突，且永远不会出现在 ListKeys 里）。",
                    parameterName);
            }
        }
    }
}
