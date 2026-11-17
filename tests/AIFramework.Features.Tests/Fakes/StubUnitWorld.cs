using System;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>单位世界只读视图桩：护盾反射阶段不读单位，因此只提供一个空实现占位。</summary>
    public sealed class StubUnitWorld : IUnitWorldReadView
    {
        /// <inheritdoc />
        public int Count
        {
            get { return 0; }
        }

        /// <inheritdoc />
        public UnitReadView this[int index]
        {
            get { throw new NotSupportedException("护盾反射阶段不读取单位视图。"); }
        }

        /// <inheritdoc />
        public bool TryGetUnit(UnitHandle handle, out UnitReadView view)
        {
            view = default(UnitReadView);
            return false;
        }
    }
}
