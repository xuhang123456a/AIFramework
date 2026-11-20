#nullable enable
using System;

namespace AIFramework.Adapters.Console.E2e
{
    /// <summary>
    /// 一个 E2E 步骤：名字 + 执行委托 + 断言委托（可选），<b>只读</b>。
    /// <para>
    /// <b>为什么"执行"与"断言"分成两个委托而不是合成一个 <c>Func&lt;bool&gt;</c></b>：
    /// 真实链路里"动作发生了"和"结果是对的"是两件事——购买动作可能成功返回
    /// <c>AlreadyOwned</c>（这是正确行为），也可能抛异常（这是接线错误）。合成一个委托后，
    /// 调用方只能把断言写进执行体里，异常与断言失败就再也分不开：报告里都只有一句"失败"，
    /// 排障时看不出是<b>链路断了</b>还是<b>规则变了</b>。分开之后，
    /// <see cref="E2eRunner"/> 能给异常带上类型名、给断言失败带上固定文案。
    /// </para>
    /// <para>
    /// <b>构造期就拒绝 null 执行委托</b>：没有执行体的步骤永远"通过"（或永远抛 <c>NullReferenceException</c>），
    /// 两种结果都会让整条冒烟绿灯失去意义。这是装配期编程错误，必须当场炸掉。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：委托由 <see cref="E2eRunner"/> 在调用线程上<b>同步</b>执行（本类不启动任何任务、
    /// 不加锁、不捕获同步上下文）；冒烟脚本要求单线程串行，步骤之间共享的场景状态才不会被并发写坏。
    /// </para>
    /// </summary>
    public sealed class E2eStep
    {
        /// <summary>执行体（非 <c>null</c>）。</summary>
        private readonly Action _execute;

        /// <summary>断言体（<c>null</c> = 只看"执行没抛异常"）。</summary>
        private readonly Func<bool>? _verify;

        /// <summary>创建一个 E2E 步骤。</summary>
        /// <param name="name">步骤名（<c>null</c> 归一化为空串；用于报告定位，不参与判定）。</param>
        /// <param name="execute">执行委托（非 <c>null</c>）。</param>
        /// <param name="verify">
        /// 断言委托（<c>null</c> = 本步没有断言，执行不抛异常即算通过）。
        /// 返回 <c>false</c> 表示断言未通过；抛异常同样按失败处理（异常类型名会进报告）。
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="execute"/> 为 <c>null</c>（装配期编程错误）。</exception>
        public E2eStep(string name, Action execute, Func<bool>? verify = null)
        {
            Name = name ?? string.Empty;
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _verify = verify;
        }

        /// <summary>步骤名（永远不为 <c>null</c>，可能为空串）。</summary>
        public string Name { get; }

        /// <summary>执行委托（供同程序集的 <see cref="E2eRunner"/> 调用；不对外暴露，避免被绕开断言直接执行）。</summary>
        internal Action Execute
        {
            get { return _execute; }
        }

        /// <summary>断言委托（<c>null</c> = 无断言）。</summary>
        internal Func<bool>? Verify
        {
            get { return _verify; }
        }
    }
}
