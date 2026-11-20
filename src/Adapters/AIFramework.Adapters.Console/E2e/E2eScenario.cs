#nullable enable
using System;
using System.Collections.Generic;

namespace AIFramework.Adapters.Console.E2e
{
    /// <summary>
    /// E2E 场景：名字 + 步骤 + 是否遇到失败即停，<b>构造后不可变</b>。
    /// <para>
    /// <b>为什么要"遇到失败即停"这个开关</b>：两种冒烟目的对失败的处理方式相反。
    /// 主流程冒烟要的是"红灯定位"——第一步购买就断了，后面每一步都会报出无关的连锁失败，
    /// 报告反而看不出根因，所以默认停；而健康巡检/全量探针要的是"一次看清坏了几处"，
    /// 那就得继续跑完，把每个独立断点都摊开。用一个布尔值显式表达，而不是让调用方自己拼两套场景。
    /// </para>
    /// <para>
    /// <b>步骤数组在构造期复制并校验</b>：
    /// <list type="bullet">
    /// <item>复制：调用方之后改写传入数组不得改变已构造的场景（冒烟脚本常把步骤数组复用/拼接）。</item>
    /// <item>校验元素非 <c>null</c>：<c>null</c> 步骤没有名字也没有执行体，跑到一半才 NRE 会污染整份报告，
    /// 必须在构造期炸掉（装配期编程错误）。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>线程模型</b>：只读数据，构造完成后可被任意线程读取；但 <see cref="E2eRunner.Run"/>
    /// 对同一条场景的并发执行不在支持范围内（步骤闭包通常持有共享的场景状态）。
    /// </para>
    /// </summary>
    public sealed class E2eScenario
    {
        /// <summary>步骤副本（构造期一次分配，之后只读）。</summary>
        private readonly E2eStep[] _steps;

        /// <summary>创建一个 E2E 场景。</summary>
        /// <param name="name">场景名（<c>null</c> 归一化为空串；只用于报告）。</param>
        /// <param name="steps">步骤列表（<c>null</c> 归一化为空数组；数组内不允许 <c>null</c> 元素）。</param>
        /// <param name="stopOnFailure">
        /// 某步失败后是否中止后续步骤（默认 <c>true</c>）。
        /// <para>
        /// <c>true</c>：后续步骤标记为"未执行"并计入失败数，不再调用其执行/断言委托；
        /// <c>false</c>：继续执行后续步骤（每步独立判定）。
        /// </para>
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="steps"/> 数组内含 <c>null</c> 元素（装配期编程错误）。</exception>
        public E2eScenario(string name, E2eStep[] steps, bool stopOnFailure = true)
        {
            Name = name ?? string.Empty;
            _steps = CopyAndValidate(steps);
            StopOnFailure = stopOnFailure;
        }

        /// <summary>场景名（永远不为 <c>null</c>，可能为空串）。</summary>
        public string Name { get; }

        /// <summary>步骤只读列表（按执行顺序；空场景为零长度列表，绝不为 <c>null</c>）。</summary>
        public IReadOnlyList<E2eStep> Steps
        {
            get { return _steps; }
        }

        /// <summary>某步失败后是否中止后续步骤。</summary>
        public bool StopOnFailure { get; }

        /// <summary>复制步骤数组并拒绝 <c>null</c> 元素（见类型注释）。</summary>
        /// <param name="steps">调用方传入的数组（可为 <c>null</c>）。</param>
        /// <returns>独立的步骤副本。</returns>
        /// <exception cref="ArgumentNullException">数组内含 <c>null</c> 元素。</exception>
        private static E2eStep[] CopyAndValidate(E2eStep[]? steps)
        {
            if (steps == null)
            {
                return Array.Empty<E2eStep>();
            }

            var copy = new E2eStep[steps.Length];
            for (int i = 0; i < steps.Length; i++)
            {
                // 用 ?? throw 而不是先判空再抛：一行内同时完成"取值 + 校验 + 报出下标"。
                copy[i] = steps[i]
                    ?? throw new ArgumentNullException(nameof(steps), "步骤数组第 " + i + " 项为 null。");
            }

            return copy;
        }
    }
}
