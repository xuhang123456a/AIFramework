using System;
using System.IO;

namespace AIFramework.Adapters.Tests
{
    /// <summary>
    /// 文件系统类适配器的临时目录夹具：每个测试实例独占
    /// <c>%TEMP%/aiframework-adapter-tests/&lt;guid&gt;</c>，<see cref="Dispose"/> 时递归删除。
    /// <para>
    /// 临时文件一律不落在仓库里；唯一 Guid 子目录保证并行执行的不同用例互不踩踏
    /// （两个测试类另外还加了同名 <c>[Collection]</c>，双保险）。
    /// </para>
    /// </summary>
    public abstract class TempDirectoryTest : IDisposable
    {
        protected TempDirectoryTest()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(), "aiframework-adapter-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        /// <summary>本用例独占的临时根目录（构造时已创建）。</summary>
        protected string RootPath { get; }

        /// <summary>递归删除临时目录；清理失败直接抛，不允许"留一半"悄悄过去。</summary>
        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            GC.SuppressFinalize(this);
        }
    }
}
