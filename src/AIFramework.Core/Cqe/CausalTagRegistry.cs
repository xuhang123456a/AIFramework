using System;
using System.Collections.Generic;
using AIFramework.Contracts.Cqe;

namespace AIFramework.Core.Cqe
{
    /// <summary>
    /// 数值 Tag 与可读名字的双向映射表。
    /// <para>
    /// 内部用 <see cref="Dictionary{TKey,TValue}"/> 查重名 + <see cref="List{T}"/> 按 id 反查名字，
    /// 只在装配期与日志导出 / 断言阶段使用，绝不进入热路径。
    /// </para>
    /// </summary>
    public sealed class CausalTagRegistry : ICausalTagRegistry
    {
        private const int InitialTagCapacity = 32;

        private readonly Dictionary<string, int> _idsByName;
        private readonly List<string> _namesById;

        /// <summary>创建一个空的 Tag 注册表；id 从 1 开始分配，0 永远表示"未注册"。</summary>
        public CausalTagRegistry()
        {
            _idsByName = new Dictionary<string, int>(InitialTagCapacity, StringComparer.Ordinal);
            _namesById = new List<string>(InitialTagCapacity);
        }

        /// <inheritdoc />
        /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 <c>null</c>。</exception>
        public int Register(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            int existingId;
            if (_idsByName.TryGetValue(name, out existingId))
            {
                return existingId;
            }

            int tagId = _namesById.Count + 1;
            _namesById.Add(name);
            _idsByName.Add(name, tagId);
            return tagId;
        }

        /// <inheritdoc />
        /// <remarks>id 小于等于 0 或超出已注册范围时返回 <c>null</c>。</remarks>
        public string? Resolve(int tagId)
        {
            if (tagId <= 0 || tagId > _namesById.Count)
            {
                return null;
            }

            return _namesById[tagId - 1];
        }
    }
}
