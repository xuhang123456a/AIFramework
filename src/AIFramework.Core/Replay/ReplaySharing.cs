using System;
using System.Text;
using AIFramework.Core.Replay;

namespace AIFramework.Core.Replay
{
    /// <summary>
    /// 回放分享：把一局录像导出成可粘贴的文本（Base64 + 校验头），以及从文本导入。
    /// <para>
    /// <b>为什么用文本而不是文件</b>：分享场景（聊天/论坛/客服截图）里最可靠的载体是字符串；
    /// 文件需要存储权限与 MIME 处理，且跨平台路径问题多。Base64 文本可放进剪贴板、二维码或日志。
    /// </para>
    /// <para>
    /// <b>为什么外面再包一层信封</b>：直接给 Base64 的话，接收方无法区分"这不是录像"和"录像损坏"；
    /// 加一行可读头（<c>AIFR-SHARE:1:</c> + 长度 + 摘要）能让导入端给出明确错误，而不是把任意
    /// 文本当录像解出一堆乱码。
    /// </para>
    /// </summary>
    public static class ReplaySharing
    {
        /// <summary>信封前缀（含格式版本）。</summary>
        public const string Prefix = "AIFR-SHARE:1:";

        /// <summary>单条分享文本的最大长度（约 64KB 录像 × 4/3 + 头；超出拒绝，避免误把超大档塞进聊天）。</summary>
        public const int MaxShareLength = 256 * 1024;

        /// <summary>把录像字节导出成分享文本。</summary>
        /// <param name="archive">录像字节（<see cref="ReplayArchiveCodec.Encode"/> 的产物）。</param>
        /// <returns>可粘贴的分享文本。</returns>
        /// <exception cref="ArgumentNullException"><paramref name="archive"/> 为 null。</exception>
        /// <exception cref="ArgumentException">录像为空或过大。</exception>
        public static string Export(byte[] archive)
        {
            if (archive == null)
            {
                throw new ArgumentNullException(nameof(archive));
            }

            if (archive.Length == 0)
            {
                throw new ArgumentException("录像为空，无法导出。", nameof(archive));
            }

            string base64 = Convert.ToBase64String(archive);
            // 摘要用长度 + 简单校验和：目的不是防篡改（真要校验录像内部已有 CRC），
            // 而是让"粘贴时被聊天工具截断"这类问题在导入时立刻暴露。
            return Prefix + archive.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":" + Checksum(archive).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                + ":" + base64;
        }

        /// <summary>从分享文本导入录像字节。</summary>
        /// <param name="text">分享文本。</param>
        /// <param name="archive">导入的录像字节（失败时为 null）。</param>
        /// <param name="reason">失败原因（成功时为空串）。</param>
        /// <returns>导入成功返回 <c>true</c>。</returns>
        public static bool TryImport(string? text, out byte[]? archive, out string reason)
        {
            archive = null;

            if (string.IsNullOrEmpty(text))
            {
                reason = "分享文本为空。";
                return false;
            }

            if (text!.Length > MaxShareLength)
            {
                reason = "分享文本过长（" + text.Length + " > " + MaxShareLength + "），可能不是录像或已被截断。";
                return false;
            }

            if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                reason = "缺少录像分享头（应以 " + Prefix + " 开头）。";
                return false;
            }

            int lengthStart = Prefix.Length;
            int lengthEnd = text.IndexOf(':', lengthStart);
            if (lengthEnd < 0)
            {
                reason = "分享头缺少长度字段。";
                return false;
            }

            if (!int.TryParse(
                text.Substring(lengthStart, lengthEnd - lengthStart),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int declaredLength)
                || declaredLength <= 0
                || declaredLength > MaxShareLength)
            {
                reason = "分享头长度字段非法。";
                return false;
            }

            int checksumEnd = text.IndexOf(':', lengthEnd + 1);
            if (checksumEnd < 0)
            {
                reason = "分享头缺少校验字段。";
                return false;
            }

            string checksumText = text.Substring(lengthEnd + 1, checksumEnd - lengthEnd - 1);
            if (!uint.TryParse(checksumText, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint declaredChecksum))
            {
                reason = "分享头校验字段非法。";
                return false;
            }

            string base64 = text.Substring(checksumEnd + 1);
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                reason = "录像内容不是合法 Base64（可能被截断或混入其它字符）。";
                return false;
            }

            if (decoded.Length != declaredLength)
            {
                reason = "录像长度不符（头声明 " + declaredLength + "，实际 " + decoded.Length + "）。";
                return false;
            }

            if (Checksum(decoded) != declaredChecksum)
            {
                reason = "录像校验和不符（内容已损坏或被改写）。";
                return false;
            }

            archive = decoded;
            reason = string.Empty;
            return true;
        }

        /// <summary>FNV-1a32 校验和（跨进程稳定；不用 <see cref="string.GetHashCode()"/> 那类随机化哈希）。</summary>
        private static uint Checksum(byte[] data)
        {
            const uint OffsetBasis = 2166136261u;
            const uint Prime = 16777619u;

            uint hash = OffsetBasis;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= Prime;
            }

            return hash;
        }
    }
}
