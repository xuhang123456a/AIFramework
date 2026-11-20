// UnityEngine.TextAsset 形状桩（T5-9）：只为让适配层的 Resources.Load<TextAsset> 能在无 Unity 机器上编译。
// 它不读任何真实文件，text/bytes 恒为空——**不能代替真机验证**。
//
// 与真实 API 的唯一形状差异：真实 Unity 的 TextAsset 没有公开无参构造（资源只能由导入器产出），
// 而仓库既有桩 Resources.Load<T> 带 `where T : Object, new()` 约束（UnityEngineShim.cs:252），
// 因此这里必须显式提供公开无参构造才能被该约束接受；这是桩的既有约束，不是本任务引入的。
using System;

namespace UnityEngine
{
    /// <summary>
    /// 文本资源（形状桩）：真机上 <see cref="text"/> 是导入后的文本内容。
    /// </summary>
    public class TextAsset : Object
    {
        /// <summary>资源文本内容；桩恒为空串。</summary>
        public string text
        {
            get { return string.Empty; }
        }

        /// <summary>资源原始字节；桩恒为空数组。</summary>
        public byte[] bytes
        {
            get { return Array.Empty<byte>(); }
        }
    }
}
