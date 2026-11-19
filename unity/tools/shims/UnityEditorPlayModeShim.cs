// UnityEditor 形状桩（Play 模式开关）：只为"无 Unity 机器上编译校验编辑器脚本"存在。
// 与 UnityEditorShim.cs 分开成文件，是为了让"本次为 OneClickTools 新增的形状"可一眼 diff 出来。
//
// 刻意**不**提供 EditorApplication.OpenScene：它不是真实 API（真实入口是
// UnityEditor.SceneManagement.EditorSceneManager.OpenScene）。桩一个真实 Unity 里不存在的方法，
// 会让离线形状校验放过"真机必炸"的调用，等于把这道闸门自己拆了。

namespace UnityEditor
{
    /// <summary>
    /// <see cref="EditorApplication"/> 的形状桩：真实 Unity 里它由编辑器驱动（帧回调、运行状态、退出），
    /// 这里只保留"能读写、不抛异常"的静态开关。桩不做任何真实操作，<b>不能替代真机验证</b>。
    /// </summary>
    public static class EditorApplication
    {
        /// <summary>
        /// 是否处于 Play 模式。
        /// <para>
        /// 真实 Unity 里对它赋 <c>false</c> 是"请求退出 Play"，退出是异步的（下一帧才真正切回编辑态）；
        /// 桩只保存这个值，既不切模式也不触发回调。
        /// </para>
        /// </summary>
        public static bool isPlaying { get; set; }
    }
}
