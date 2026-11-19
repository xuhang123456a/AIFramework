using System;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.HotUpdate
{
    /// <summary>
    /// 热更程序集的最小可验证载体：**只做"我能被换掉"这件事**，不掺业务。
    /// <para>
    /// 存在的意义是把 HybridCLR 链路打通到"能编译出一个独立 dll、能被主工程按名字加载"：
    /// 真正的业务热更（活动/玩法配置/UI 文案）后续下沉到这个程序集，主工程只保留稳定壳。
    /// 这里刻意不引用任何 UnityEngine，保持 <c>noEngineReferences: true</c>——
    /// 热更程序集一旦拖引擎引用，IL2CPP 侧符号裁剪与泛型 AOT 都会变复杂。
    /// </para>
    /// </summary>
    public static class HotUpdateInfo
    {
        /// <summary>热更程序集版本：改这个字符串 + 重新编译 dll 就是一次"热更内容"。</summary>
        public const string Version = "0.1.0";

        /// <summary>热更程序集的构建标记（由构建脚本或 CI 覆盖写入）。</summary>
        public const string BuildStamp = "local";

        /// <summary>
        /// 返回一段可被主工程/日志消费的版本描述。
        /// 主工程不直接引用本程序集（否则会打进 AOT 包，失去热更意义），
        /// 而是按程序集名反射调用——这正是 HybridCLR 推荐的热更边界。
        /// </summary>
        public static string Describe()
        {
            return "hotupdate v" + Version + " (" + BuildStamp + ")";
        }

        /// <summary>
        /// 用框架契约做一个**无副作用**的自检：能跑通说明热更 dll 里的类型系统与 Contracts 一致，
        /// 也说明它确实被编进了独立程序集（AOT 包里没有这份实现）。
        /// </summary>
        public static bool SelfCheck()
        {
            GameplayTagMask tags = GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive);
            return tags.Has(GameplayTags.UnitShieldActive)
                && tags.Remove(GameplayTags.UnitShieldActive).IsEmpty;
        }
    }
}
