// 仅由 Directory.Build.targets 链接进 netstandard 工程，供 C# 9 的 record / init 使用。
// net10.0 已内置该类型，禁止链接，否则重复定义。
namespace System.Runtime.CompilerServices
{
    internal sealed class IsExternalInit
    {
    }
}
