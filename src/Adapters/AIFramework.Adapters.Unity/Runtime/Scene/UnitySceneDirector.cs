#nullable enable
using System;
using UnityEngine;
using UnityEngine.SceneManagement;
// 注意：Unity 自己有一个 UnityEngine.ILogger（形状桩里特意保留了同名接口），
// 一旦同时 using 两个命名空间，裸写 ILogger 就会撞成 CS0104；这里用别名把归属钉死。
using ILogger = AIFramework.Contracts.Ports.ILogger;

namespace AIFramework.Adapters.Unity.Scene
{
    /// <summary>
    /// 场景切换器（T1-1 Boot / T3-4 SceneDirector 第一版）：把 Unity 的同步场景加载收成一次
    /// "有返回值、不抛异常"的调用。
    /// <para>
    /// <b>职责边界（场景切换纪律，务必遵守）</b>：
    /// <list type="bullet">
    /// <item>本类<b>不持有宿主</b>（<c>AIFrameworkUnityHost</c>）、<b>不注册总线</b>、不引用任何切片；
    /// 它只做一件事——把当前场景换成另一个场景。</item>
    /// <item>宿主由<b>各场景自己</b>创建并销毁：大厅场景的 <c>LobbyController</c> 在 <c>Awake</c> 里
    /// <c>new AIFrameworkUnityHost()</c> + <c>Initialize()</c>，在 <c>OnDestroy</c> 里 <c>Dispose()</c>。</item>
    /// <item>因此"切场景前先卸载宿主"是<b>自然发生</b>的：<see cref="SceneManager.LoadScene(string)"/> 是同步的，
    /// 它会先销毁当前场景的全部对象（触发 <c>OnDestroy</c> → 宿主 <c>Dispose</c>），再装载目标场景。
    /// 启动场景（Boot）因此<b>不要</b>创建宿主，否则切场景后会留下抢总线/抢存档的幽灵装配。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为什么失败不抛异常</b>：场景没进 Build Settings 属于<b>部署/配置问题</b>，不是程序逻辑错误。
    /// 让异常从 <c>Start</c> 里冒出来会直接打断启动流程，且调用方无从补救；这里统一
    /// catch <see cref="UnityException"/> → 记日志 → 返回 <c>false</c>，把"要不要兜底"的决定权交回调用方。
    /// </para>
    /// <para>
    /// <b>只支持单场景切换</b>：不提供 Additive 加载，也不做加载进度/异步——Phase 1 的启动链路只有
    /// "Boot → Lobby" 一跳，异步与叠加加载等真实需求出现时再加，避免现在猜出错误抽象。
    /// </para>
    /// </summary>
    public sealed class UnitySceneDirector
    {
        /// <summary>日志端口；未注入时为 <c>null</c>，此时静默执行（单元测试与纯形状校验里不希望刷日志）。</summary>
        private readonly ILogger? _logger;

        /// <summary>
        /// 最近一次<b>成功</b>加载的场景名；从未加载过时为空串（不是 <c>null</c>，调用方无需判空）。
        /// <b>为什么不直接在 getter 里取活动场景名</b>：那样"本类是否加载过场景"这个状态就丢了，
        /// <c>Reload</c> 也无法区分"没加载过（忽略）"和"加载过（重载）"。
        /// </summary>
        private string _currentSceneName = string.Empty;

        /// <summary>创建场景切换器。</summary>
        /// <param name="logger">
        /// 日志端口，可省略。<b>为什么允许 <c>null</c></b>：本类要能在没有宿主的启动场景里被 new 出来，
        /// 而宿主是日志端口的装配者；此时"没有日志"是合法状态，不是错误。
        /// </param>
        public UnitySceneDirector(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>最近一次成功加载的场景名；从未加载过时为 <see cref="string.Empty"/>。</summary>
        public string CurrentSceneName
        {
            get { return _currentSceneName; }
        }

        /// <summary>
        /// 按场景名同步加载。<b>只有在 Build Settings 里存在的场景才能加载成功。</b>
        /// <para>
        /// 三种"返回 false 但不算错误"的情况：场景名为空、目标场景<b>已经是当前场景</b>（重复加载要跳过）、
        /// 场景不在 Build Settings（Unity 抛 <see cref="UnityException"/>，这里吃掉并记日志）。
        /// </para>
        /// </summary>
        /// <param name="sceneName">场景名，<b>不带</b> <c>.unity</c> 后缀，且必须与 Build Settings 列表中的文件名一致。</param>
        /// <returns>是否真的发生了切换。</returns>
        public bool Load(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                LogError("场景名为空，已跳过加载。");
                return false;
            }

            // 同场景重复 LoadScene 会触发一次完整的场景重建（对象全销毁再重建、宿主来回 Dispose/Initialize），
            // 属于纯粹的性能浪费与状态抖动，所以先比对活动场景名。
            string activeSceneName = SceneManager.GetActiveScene().name;
            if (string.Equals(activeSceneName, sceneName, StringComparison.Ordinal))
            {
                LogInfo("目标场景就是当前场景，跳过重复加载：" + sceneName);
                return false;
            }

            try
            {
                SceneManager.LoadScene(sceneName);
            }
            catch (UnityException exception)
            {
                // Unity 在"场景不在 Build Settings / AssetBundle 未加载"时抛的就是这个类型；
                // 这里必须吃掉：异常逃到 MonoBehaviour.Start 会打断整个启动流程。
                LogError("场景加载失败（" + sceneName + " 可能不在 Build Settings 里）：" + exception.Message);
                return false;
            }
            catch (ArgumentException exception)
            {
                // 兜底网：UnityException 与 ArgumentException 是兄弟类型（互不捕获），
                // 个别 Unity 版本会把"场景不在 Build Settings"报成 ArgumentException。
                // 契约的真正要求是"不要把异常抛给调用方"，所以这里一并吃掉。
                LogError("场景名不合法或未进 Build Settings（" + sceneName + "）：" + exception.Message);
                return false;
            }

            _currentSceneName = sceneName;
            LogInfo("已切换到场景：" + sceneName + "（原场景："
                + (activeSceneName.Length == 0 ? "<未命名>" : activeSceneName) + "）");
            return true;
        }

        /// <summary>
        /// 重载当前场景（把当前场景重新装载一遍，用于"回到本场景初始状态"）。
        /// <b>没有当前场景时什么也不做</b>——启动场景刚起来就调用它属于调用方的时序错误，
        /// 但没必要为此抛异常把调用方搞崩。
        /// </summary>
        public void Reload()
        {
            string sceneName = _currentSceneName;
            if (sceneName.Length == 0)
            {
                LogInfo("还没有成功加载过任何场景，忽略重载请求。");
                return;
            }

            try
            {
                SceneManager.LoadScene(sceneName);
            }
            catch (UnityException exception)
            {
                LogError("场景重载失败（" + sceneName + "）：" + exception.Message);
                return;
            }
            catch (ArgumentException exception)
            {
                // 与 Load 同样的兜底理由：重载失败也不允许把异常抛给调用方。
                LogError("场景重载失败，场景名不合法或已不在 Build Settings（" + sceneName + "）：" + exception.Message);
                return;
            }

            LogInfo("已重载场景：" + sceneName);
        }

        /// <summary>信息日志；端口未注入时静默丢弃。</summary>
        /// <param name="message">日志内容。</param>
        private void LogInfo(string message)
        {
            _logger?.Info(message);
        }

        /// <summary>错误日志；端口未注入时静默丢弃。</summary>
        /// <param name="message">日志内容。</param>
        private void LogError(string message)
        {
            _logger?.Error(message);
        }
    }
}
