#nullable enable
using AIFramework.Contracts.Ports;
using System;
using System.Collections.Generic;
using System.IO;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 本地目录存档端口：键 <c>key</c> 落盘为 <c>&lt;key&gt;.sav</c>。
    /// <para>
    /// 写入流程固定为 "先写 <c>&lt;key&gt;.sav.tmp</c>（<see cref="FileStream"/> + <see cref="FileStream.Flush(bool)"/> 刷盘）
    /// → <see cref="File.Move(string, string, bool)"/> 原子替换"，因此掉电或崩溃都不会留下半个存档文件；
    /// 临时文件一律由 <c>finally</c> 收尾，失败路径也不留残骸。
    /// </para>
    /// <para>
    /// 键名白名单为 <c>[A-Za-z0-9._-]</c>，另额外拒绝含 <c>..</c> 的键（白名单本身拦不住它，会构成目录穿越），
    /// 因此键永远落在根目录一级，不会生成子目录。
    /// </para>
    /// <para>
    /// 线程模型：<b>单线程使用，装配期初始化</b>。本类不加锁，也不支持多进程并发写同一目录；
    /// 真机由单一存档服务串行调用，帧内不需要也没必要并发保护。
    /// </para>
    /// </summary>
    public sealed class FileSystemStoragePort : IStoragePort
    {
        /// <summary>存档文件扩展名（落盘文件名 = key + 该后缀）。</summary>
        private const string Extension = ".sav";

        /// <summary>临时文件扩展名（先写它，再原子替换成正式文件）。</summary>
        private const string TempExtension = ".tmp";

        /// <summary>根目录绝对路径，构造后不再改变。</summary>
        private readonly string _rootDirectory;

        /// <summary>
        /// 以 <paramref name="rootDirectory"/> 为存档根目录；目录不存在则创建，
        /// 创建失败（无权限 / 路径非法）时异常直接冒泡，由装配期暴露。
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="rootDirectory"/> 为 <c>null</c>。</exception>
        public FileSystemStoragePort(string rootDirectory)
        {
            if (rootDirectory == null)
            {
                throw new ArgumentNullException(nameof(rootDirectory));
            }

            Directory.CreateDirectory(rootDirectory);
            _rootDirectory = Path.GetFullPath(rootDirectory);
        }

        /// <inheritdoc />
        public bool Exists(string key)
        {
            return File.Exists(ResolvePath(key));
        }

        /// <inheritdoc />
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
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }

                File.Move(tempPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        /// <inheritdoc />
        public void Delete(string key)
        {
            string path = ResolvePath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        /// <inheritdoc />
        public string[] ListKeys()
        {
            string[] files = Directory.GetFiles(_rootDirectory);
            var keys = new List<string>(files.Length);
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileName(files[i]);

                // 只认精确的 ".sav" 后缀：".sav.tmp" 自然被排除，
                // 同时绕开 Win32 通配符把 "*.sav" 也匹配到更长扩展名的历史行为。
                if (name.Length <= Extension.Length
                    || !name.EndsWith(Extension, StringComparison.Ordinal))
                {
                    continue;
                }

                keys.Add(name.Substring(0, name.Length - Extension.Length));
            }

            keys.Sort(StringComparer.Ordinal);
            return keys.ToArray();
        }

        /// <summary>校验键名并映射到绝对文件路径。</summary>
        private string ResolvePath(string key)
        {
            ValidateKey(key);
            return Path.Combine(_rootDirectory, key + Extension);
        }

        /// <summary>键名白名单校验：防目录穿越的唯一入口。</summary>
        private static void ValidateKey(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (key.Length == 0)
            {
                throw new ArgumentException("存档键不能为空。", nameof(key));
            }

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool allowed = (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '.'
                    || c == '_'
                    || c == '-';
                if (!allowed)
                {
                    throw new ArgumentException(
                        "存档键只允许 [A-Za-z0-9._-]，非法字符 '" + c + "'。", nameof(key));
                }
            }

            if (key.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException("存档键不允许包含 \"..\"（目录穿越防护）。", nameof(key));
            }
        }
    }
}
