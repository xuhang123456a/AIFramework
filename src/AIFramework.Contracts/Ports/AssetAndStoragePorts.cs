using System;

namespace AIFramework.Contracts.Ports
{
    /// <summary>
    /// 资源加载端口。真机接 YooAsset / Addressables / Resources，命令行接本地目录。
    /// <para>
    /// 与 <see cref="IBinaryTableLoader"/> 的区别：配置表缺表属于"数据问题"，允许返回 <c>null</c> 由切片判定；
    /// 资源缺失属于"打包问题"，必须直接抛 <see cref="System.IO.FileNotFoundException"/>，在装配期暴露。
    /// </para>
    /// </summary>
    public interface IAssetLoader
    {
        /// <summary>按约定 key 读取资源原始字节；缺失即抛异常，禁止返回空数组兜底。</summary>
        /// <exception cref="System.IO.FileNotFoundException">资源不存在。</exception>
        byte[] LoadBytes(string key);

        /// <summary>判断资源是否存在（仅用于编辑器工具与诊断，不要在帧内调用）。</summary>
        bool Exists(string key);
    }

    /// <summary>
    /// 本地持久化端口。真机接 <c>Application.persistentDataPath</c>，命令行接任意目录。
    /// <para>写入必须是原子的（先写临时文件再替换），掉电/崩溃时不能留下半个存档。</para>
    /// <para>
    /// <b>key 是逻辑键，落盘命名由实现自行决定</b>（例如控制台实现落成 <c>key.sav</c>、Unity 实现直接用 <c>key</c>）。
    /// 因此存档**不支持跨适配器直接拷贝**：平台间迁移必须走导出/导入逻辑，而不是复制文件。
    /// </para>
    /// </summary>
    public interface IStoragePort
    {
        /// <summary>键是否存在。</summary>
        bool Exists(string key);

        /// <summary>读取原始字节；键不存在返回 <c>null</c>（由存档服务决定是"新档"还是"损坏"）。</summary>
        byte[]? Read(string key);

        /// <summary>原子写入（覆盖同名键）。</summary>
        void Write(string key, byte[] data);

        /// <summary>删除键；不存在时静默成功。</summary>
        void Delete(string key);

        /// <summary>枚举全部键，返回顺序必须稳定（升序），供存档列表/迁移使用。</summary>
        string[] ListKeys();
    }
}
