using System;
using System.Collections.Generic;
using System.Reflection;
using AIFramework.Contracts.Cqe;
using AIFramework.Core.Cqe;
using AIFramework.Features.DailySignIn;
using AIFramework.Kernel.Realtime;
using AIFramework.Kernel.Turn;
using Xunit;

namespace AIFramework.ArchTests
{
    /// <summary>
    /// 程序集依赖拓扑守卫（纯反射，不引入 NetArchTest）：读编译出的程序集元数据里的引用表，
    /// 过滤 <c>AIFramework.</c> 前缀后逐个断言，验证 AGENTS.md §二 的"平行单向依赖 Contracts"。
    /// </summary>
    public sealed class TopologyTests
    {
        private const string ContractsAssemblyName = "AIFramework.Contracts";
        private const string CoreAssemblyName = "AIFramework.Core";
        private const string KernelTurnAssemblyName = "AIFramework.Kernel.Turn";
        private const string KernelRealtimeAssemblyName = "AIFramework.Kernel.Realtime";
        private const string FeaturesAssemblyName = "AIFramework.Features";

        [Fact]
        public void Contracts_MustNotReferenceAnyAIFrameworkAssembly()
        {
            string[] references = GetAIFrameworkReferences(typeof(ICqeBus).Assembly);

            Assert.Empty(references);
        }

        [Fact]
        public void Core_MustReferenceContractsOnly()
        {
            AssertReferencesAreExactly(typeof(CqeBus).Assembly, CoreAssemblyName, ContractsAssemblyName);
        }

        [Fact]
        public void TurnKernel_MustReferenceContractsOnly()
        {
            AssertReferencesAreExactly(typeof(TurnWorld).Assembly, KernelTurnAssemblyName, ContractsAssemblyName);
        }

        [Fact]
        public void RealtimeKernel_MustReferenceContractsOnly()
        {
            AssertReferencesAreExactly(typeof(BattleWorld).Assembly, KernelRealtimeAssemblyName, ContractsAssemblyName);
        }

        [Fact]
        public void Features_MustReferenceContractsOnly()
        {
            AssertReferencesAreExactly(typeof(SignInHandler).Assembly, FeaturesAssemblyName, ContractsAssemblyName);
        }

        [Fact]
        public void Kernels_MustNotReferenceEachOther()
        {
            string[] turnReferences = GetAIFrameworkReferences(typeof(TurnWorld).Assembly);
            string[] realtimeReferences = GetAIFrameworkReferences(typeof(BattleWorld).Assembly);

            Assert.DoesNotContain(KernelRealtimeAssemblyName, turnReferences);
            Assert.DoesNotContain(KernelTurnAssemblyName, realtimeReferences);
        }

        /// <summary>
        /// 取程序集元数据里的引用表，只保留 <c>AIFramework.</c> 前缀的条目（排序保证断言稳定）。
        /// 要求同时校验目标程序集名，避免"拿错程序集"导致断言永远成立。
        /// </summary>
        private static string[] GetAIFrameworkReferences(Assembly assembly)
        {
            var names = new List<string>();
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string? name = reference.Name;
                if (name != null && name.StartsWith("AIFramework.", StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names.ToArray();
        }

        private static void AssertReferencesAreExactly(Assembly assembly, string expectedAssemblyName, params string[] expected)
        {
            Assert.Equal(expectedAssemblyName, assembly.GetName().Name);

            string[] actual = GetAIFrameworkReferences(assembly);
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }
}
