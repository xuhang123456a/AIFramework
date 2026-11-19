#nullable enable
namespace AIFramework.Analyzer
{
    /// <summary>
    /// 分析器规则 ID 的唯一来源。测试工程直接引用这里的常量做断言，禁止在别处硬编码字符串。
    /// </summary>
    public static class DiagnosticIds
    {
        /// <summary>Features 切片之间禁止互相引用。</summary>
        public const string CrossSlice = "AIF001";

        /// <summary>纯逻辑程序集禁止引用引擎命名空间。</summary>
        public const string EngineReference = "AIF002";

        /// <summary>逻辑层禁止直接调用非确定性系统 API（时间 / 随机）。</summary>
        public const string NonDeterministicApi = "AIF003";

        /// <summary>Features 切片禁止越层引用 Core / Kernel / Adapters。</summary>
        public const string ForbiddenLayerReference = "AIF004";
    }
}
