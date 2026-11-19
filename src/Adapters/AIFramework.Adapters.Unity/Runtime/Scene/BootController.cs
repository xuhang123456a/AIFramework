#nullable enable
using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIFramework.Adapters.Unity.Scene
{
    /// <summary>
    /// 启动场景控制器（T1-1 Boot）：整条启动链路里<b>唯一</b>的职责，就是在 <c>Start</c> 里把控制权交给下一个场景。
    /// <para>
    /// <b>场景切换纪律（必读）</b>：
    /// <list type="bullet">
    /// <item>启动场景<b>不创建宿主</b>。<c>AIFrameworkUnityHost</c> 的装配与释放由<b>业务场景自己</b>负责：
    /// 大厅场景的 <c>LobbyController</c> 在 <c>Awake</c> 里 <c>new AIFrameworkUnityHost()</c> + <c>Initialize()</c>，
    /// 在 <c>OnDestroy</c> 里 <c>Dispose()</c>（已有实现）。</item>
    /// <item>所以本类在切场景<b>之前既不需要、也不允许</b>去卸载宿主：同步的
    /// <see cref="SceneManager.LoadScene(string)"/> 会先销毁当前场景的全部对象（触发 <c>OnDestroy</c> →
    /// 宿主 <c>Dispose</c>），再装载目标场景。宿主生命周期天然收口在各自场景内。</item>
    /// <item>反过来，如果启动场景也建了宿主，切场景后它会继续存活，与大厅里的宿主抢同一份存档与总线——
    /// 这正是 Phase 1 最容易踩的坑，所以这里显式不装配。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>调试</b>：只想单独看启动场景时，把 <c>autoLoad</c> 取消勾选即可留在本场景，不需要改代码。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BootController : MonoBehaviour
    {
        /// <summary>
        /// 默认跳转目标的场景名。<b>为什么是写死的字符串常量</b>：场景名在 Unity 侧本来就只是字符串，
        /// 而宪法不允许为"启动跳哪个场景"新增 Contracts 类型。
        /// </summary>
        public const string DefaultNextScene = "LobbyScene";

        /// <summary>本类日志前缀，便于在 Unity Console 里只筛启动链路。</summary>
        private const string LogPrefix = "[AIFramework/Boot] ";

        /// <summary>跳转失败时的补救提示：明确列出必须进 Build Settings 的两个场景。</summary>
        private const string RequiredSceneHint =
            "请确认以下场景已加入 Build Settings（菜单 AIFramework/把场景加入 Build Settings，"
            + "或 Edit > Project Settings > Editor > Build Settings），且已勾选：\n"
            + "  1) Assets/AIFrameworkDemo/Scenes/BootScene.unity\n"
            + "  2) Assets/AIFrameworkDemo/Scenes/LobbyScene.unity\n"
            + "场景名必须与文件名一致（不带 .unity 后缀）；只想留在启动场景调试时，把 BootController 的 autoLoad 关掉。";

        /// <summary>启动后要加载的场景名（可在 Inspector 里改，也可由编辑器脚本用 SerializedObject 写入）。</summary>
        [SerializeField] private string nextScene = DefaultNextScene;

        /// <summary>Play 时是否自动跳转；关掉便于单独调试启动场景。</summary>
        [SerializeField] private bool autoLoad = true;

        /// <summary>场景切换器，惰性创建（见 <see cref="Director"/>）。</summary>
        private UnitySceneDirector? _director;

        /// <summary>
        /// 启动后加载的场景名。赋 <c>null</c> 或空串等同于"不跳转"（不会变成非法状态）。
        /// </summary>
        public string NextScene
        {
            get { return nextScene; }
            set { nextScene = string.IsNullOrEmpty(value) ? string.Empty : value; }
        }

        /// <summary>
        /// 场景切换器，只读。<b>为什么惰性创建</b>：避免依赖 <c>Awake</c> 与其他脚本的执行顺序，
        /// 编辑器工具与测试也能在 Play 之前直接取到可用实例。
        /// <b>为什么不提供 setter</b>：换实现（换日志端口/换加载策略）应当在自己代码里
        /// <c>new UnitySceneDirector(...)</c>，而不是替换场景里这个组件的内部依赖。
        /// </summary>
        public UnitySceneDirector Director
        {
            get
            {
                UnitySceneDirector? current = _director;
                if (current == null)
                {
                    // 启动场景没有宿主，拿不到宿主的日志端口，所以自建一个 UnityLogger；
                    // 前缀与本类保持一致，Console 里一眼可辨。
                    current = new UnitySceneDirector(new UnityLogger(LogPrefix));
                    _director = current;
                }

                return current;
            }
        }

        /// <summary>
        /// 启动：按需把控制权交给 <see cref="NextScene"/> 指定的场景。
        /// <para>
        /// <b>失败一律用 <c>Debug.LogError</c> 明说原因</b>：跳转失败通常不是代码坏了，而是场景没进
        /// Build Settings；这条错误是给"刚把工程 clone 下来按 Play"的人看的，必须自带补救步骤。
        /// </para>
        /// </summary>
        private void Start()
        {
            string target = nextScene;

            if (!autoLoad)
            {
                Debug.Log(LogPrefix + "autoLoad 已关闭，停留在启动场景（调试模式）。目标场景："
                    + Describe(target));
                return;
            }

            if (string.IsNullOrEmpty(target))
            {
                Debug.LogWarning(LogPrefix + "nextScene 为空，不会跳转到任何场景。");
                return;
            }

            // 已经站在目标场景上（例如把 BootController 临时挂进了大厅场景做联调）：
            // 这不是错误，先拦住，避免 UnitySceneDirector.Load 返回 false 后被误报成加载失败。
            if (string.Equals(SceneManager.GetActiveScene().name, target, StringComparison.Ordinal))
            {
                Debug.Log(LogPrefix + "当前已经是目标场景，不重复跳转：" + target);
                return;
            }

            if (Director.Load(target))
            {
                return;
            }

            Debug.LogError(LogPrefix + "启动跳转失败：无法加载场景 " + Describe(target) + "。\n" + RequiredSceneHint);
        }

        /// <summary>把场景名包成日志友好的形态（空值显示为 <c>&lt;未设置&gt;</c>）。</summary>
        /// <param name="sceneName">场景名。</param>
        /// <returns>带引号的场景名。</returns>
        private static string Describe(string sceneName)
        {
            return string.IsNullOrEmpty(sceneName) ? "<未设置>" : "\"" + sceneName + "\"";
        }
    }
}
