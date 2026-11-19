#nullable enable
using AIFramework.Contracts.Ports;
using System;
using System.IO;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// 目录版配置表加载器：按 <c>&lt;目录&gt;/&lt;表名&gt;.bin</c> 读字节（与 <c>Doc/03</c> §5.4 的路径约定一致，
    /// 也与 Unity 侧 <c>StreamingAssets/AIFrameworkDemo/</c> 完全同构）。
    /// <para>
    /// <b>为什么不再用内存表</b>：Console 与 Unity 必须读同一份字节，否则会出现"命令行冒烟通过、
    /// 真机数值是另一套"的假阳性。夹具由 <c>tools/fixtures</c> 生成，两边共用。
    /// </para>
    /// </summary>
    public sealed class DirectoryBinaryTableLoader : IBinaryTableLoader
    {
        private readonly string _directory;

        public DirectoryBinaryTableLoader(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("配置表目录不能为空。", nameof(directory));
            }

            _directory = directory;
        }

        /// <summary>表文件所在目录。</summary>
        public string Directory
        {
            get { return _directory; }
        }

        /// <summary>缺表返回 <c>null</c>（由切片显式失败，遵循 <see cref="IBinaryTableLoader"/> 契约）。</summary>
        public byte[]? Load(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return null;
            }

            string path = Path.Combine(_directory, tableName + ".bin");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
    }
}
