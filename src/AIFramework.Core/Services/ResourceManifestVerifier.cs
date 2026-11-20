using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AIFramework.Contracts.Services;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 热更资源清单校验：逐条核对"读得到、字节数对、SHA-256 对"，并给出是否需要回退内置资源的结论。
    /// <para>
    /// <b>为什么必须逐条校验而不是只看总大小</b>：热更最常见的事故是"下载到一半网络断了"——
    /// 文件存在、大小可能接近、内容却是错的。只验总大小会把这类问题放到运行期才炸（资源加载出空贴图/错模型）。
    /// </para>
    /// <para>
    /// <b>为什么回退策略只有"全部通过才用热更"</b>：部分资源坏、部分用新版本会让美术/配置出现"半新半旧"的
    /// 组合态，问题极难复现。宁可整包回退到内置资源并给出明确告警，也不做局部替换。
    /// </para>
    /// <para>
    /// <b>为什么加载器用委托</b>：Core 不能知道资源来自磁盘、StreamingAssets 还是 AssetBundle；
    /// 调用方传 <c>Func&lt;string, byte[]?&gt;</c> 即可（返回 null 表示读不到）。
    /// </para>
    /// </summary>
    public sealed class ResourceManifestVerifier
    {
        /// <summary>SHA-256 十六进制长度。</summary>
        public const int Sha256HexLength = 64;

        /// <summary>校验一个清单：逐条产出结论；<paramref name="loader"/> 可多次调用（每个名字一次）。</summary>
        /// <param name="manifest">资源清单。</param>
        /// <param name="loader">加载器：返回 null 视为缺失。</param>
        /// <returns>逐条结论（顺序与清单一致）。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 null。</exception>
        public AssetCheckOutcome[] Verify(in AssetManifest manifest, Func<string, byte[]?> loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            AssetManifestEntry[] entries = manifest.Entries;
            var outcomes = new AssetCheckOutcome[entries.Length];

            for (int i = 0; i < entries.Length; i++)
            {
                AssetManifestEntry entry = entries[i];
                byte[]? data;
                try
                {
                    data = loader(entry.Name);
                }
                catch (Exception)
                {
                    // 加载器自己抛异常也按"缺失"处理：校验器不能成为新的崩溃点。
                    data = null;
                }

                if (data == null)
                {
                    outcomes[i] = new AssetCheckOutcome(entry.Name, AssetCheckResult.Missing, -1);
                    continue;
                }

                if (data.Length != entry.SizeBytes)
                {
                    outcomes[i] = new AssetCheckOutcome(entry.Name, AssetCheckResult.SizeMismatch, data.Length);
                    continue;
                }

                string actualHex = ToHex(Sha256(data));
                bool hashOk = string.Equals(actualHex, entry.Sha256Hex, StringComparison.OrdinalIgnoreCase);
                outcomes[i] = new AssetCheckOutcome(
                    entry.Name,
                    hashOk ? AssetCheckResult.Ok : AssetCheckResult.HashMismatch,
                    data.Length);
            }

            return outcomes;
        }

        /// <summary>是否所有条目都通过（false = 应回退内置资源，见类注释的"全有或全无"策略）。</summary>
        /// <param name="outcomes">校验结论。</param>
        /// <returns>全部通过返回 true。</returns>
        public static bool AllOk(AssetCheckOutcome[] outcomes)
        {
            if (outcomes == null)
            {
                throw new ArgumentNullException(nameof(outcomes));
            }

            for (int i = 0; i < outcomes.Length; i++)
            {
                if (!outcomes[i].IsOk)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>统计各类失败条数（日志/看板用）。</summary>
        /// <param name="outcomes">校验结论。</param>
        /// <returns>失败条数（Ok 不计）。</returns>
        public static int CountFailures(AssetCheckOutcome[] outcomes)
        {
            if (outcomes == null)
            {
                throw new ArgumentNullException(nameof(outcomes));
            }

            int failures = 0;
            for (int i = 0; i < outcomes.Length; i++)
            {
                if (!outcomes[i].IsOk)
                {
                    failures++;
                }
            }

            return failures;
        }

        /// <summary>生成十六进制摘要（小写，便于与清单比对）。</summary>
        /// <param name="data">内容。</param>
        /// <returns>64 位小写十六进制。</returns>
        public static string Sha256Hex(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            return ToHex(Sha256(data));
        }

        private static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
