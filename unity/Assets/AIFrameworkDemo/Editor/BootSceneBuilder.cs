using System.Collections.Generic;
using AIFramework.Adapters.Unity.Scene;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键生成启动场景（<c>BootScene.unity</c>），并把 Boot / Lobby 两个场景按启动顺序写进 Build Settings。
    /// <para>
    /// <b>为什么用脚本建场景而不是手写 <c>.unity</c> YAML</b>：手写 YAML 要拼 fileID/GUID，错一个就整场景打不开，
    /// 且 AI 无法可靠修改；脚本建场景可重复执行、结果确定，AI 只碰 C#。
    /// </para>
    /// <para>
    /// <b>启动场景为什么只有一个相机加一个空物体</b>：它是"外壳"，不装配任何宿主
    /// （宿主由大厅场景自己 <c>new</c> 并在 <c>OnDestroy</c> 里 <c>Dispose</c>，见 <see cref="BootController"/> 注释）。
    /// 起挂就是为了让 <c>Start</c> 里那一跳可被 Inspector 改、可被关掉。
    /// </para>
    /// <para>
    /// <b>为什么要单独一个"加入 Build Settings"的菜单</b>：<c>SceneManager.LoadScene(名字)</c> 只认 Build Settings
    /// 里的场景；这一项属于工程配置而非代码，只能由编辑器写 <c>ProjectSettings/EditorBuildSettings.asset</c>。
    /// </para>
    /// </summary>
    public static class BootSceneBuilder
    {
        /// <summary>场景目录（与大厅场景构建脚本保持一致）。</summary>
        private const string SceneFolder = "Assets/AIFrameworkDemo/Scenes";

        /// <summary>启动场景路径。</summary>
        private const string BootScenePath = SceneFolder + "/BootScene.unity";

        /// <summary>大厅场景路径（由 <c>LobbySceneBuilder</c> 生成；本脚本只负责把它写进 Build Settings）。</summary>
        private const string LobbyScenePath = SceneFolder + "/LobbyScene.unity";

        /// <summary>启动后要进入的场景名：与 Build Settings 列表中的文件名一致，不带后缀。</summary>
        private const string NextSceneName = "LobbyScene";

        /// <summary>
        /// <see cref="BootController"/> 上被序列化的私有字段名。<b>为什么用字符串而不是直接赋值</b>：
        /// 该字段是私有的 <c>[SerializeField]</c>，只能通过 <see cref="SerializedObject"/> 写。
        /// </summary>
        private const string NextSceneFieldName = "nextScene";

        /// <summary>启动场景里相机物体的名字。</summary>
        private const string CameraName = "Main Camera";

        /// <summary>启动场景里挂控制器的物体名字。</summary>
        private const string BootObjectName = "Boot";

        /// <summary>
        /// 生成 <c>Assets/AIFrameworkDemo/Scenes/BootScene.unity</c>：空场景 + Main Camera + 挂了
        /// <see cref="BootController"/> 的 <c>Boot</c> 物体（<c>nextScene</c> 预设为 <c>LobbyScene</c>）。
        /// </summary>
        [MenuItem("AIFramework/创建启动场景", priority = 2)]
        public static void CreateBootScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder(SceneFolder);

            CreateCamera();

            var bootGo = new GameObject(BootObjectName);
            var controller = bootGo.AddComponent<BootController>();
            SetStringField(controller, NextSceneFieldName, NextSceneName);

            EditorSceneManager.SaveScene(scene, BootScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 启动场景已生成：" + BootScenePath
                + "（BootController.nextScene=" + NextSceneName + "，autoLoad=true）。"
                + "下一步执行菜单 AIFramework/把场景加入 Build Settings，然后按 Play。");
        }

        /// <summary>
        /// 把 <c>BootScene</c> 与 <c>LobbyScene</c> 按这个顺序<b>整表覆盖</b>写入 Build Settings
        /// （索引 0 即发包后的启动场景）。<b>只写磁盘上真实存在的场景</b>：缺失的跳过并给出提示，
        /// 免得往列表里塞一条指向空文件、打包时才炸的条目。
        /// </summary>
        [MenuItem("AIFramework/把场景加入 Build Settings", priority = 3)]
        public static void AddScenesToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(2);
            AddIfSceneExists(scenes, BootScenePath);
            AddIfSceneExists(scenes, LobbyScenePath);
            AddIfSceneExists(scenes, CardBattleScenePath);
            AddIfSceneExists(scenes, Match3ScenePath);

            if (scenes.Count == 0)
            {
                Debug.LogWarning("[AIFramework] 没有找到任何可加入的场景，Build Settings 未改动。"
                    + "请先执行 AIFramework/创建启动场景 与 AIFramework/创建大厅场景。期望路径："
                    + BootScenePath + "、" + LobbyScenePath);
                return;
            }

            EditorBuildSettings.scenes = scenes.ToArray();

            for (int i = 0; i < scenes.Count; i++)
            {
                Debug.Log("[AIFramework] Build Settings[" + i + "] = " + scenes[i].path);
            }

            Debug.Log("[AIFramework] 已写入 Build Settings，共 " + scenes.Count
                + " 个场景（顺序即索引，索引 0 为启动场景）。缺失的场景没有被写入，请先创建它再执行一次本菜单。");
        }

        /// <summary>创建启动场景用的相机（位置 <c>(0, 0, -10)</c>，与大厅场景保持一致）。</summary>
        private static void CreateCamera()
        {
            var cameraGo = new GameObject(CameraName);
            cameraGo.transform.position = new Vector3(0f, 0f, -10f);
            cameraGo.AddComponent<Camera>();
        }

        /// <summary>用 <see cref="SerializedObject"/> 给私有 <c>[SerializeField]</c> 字符串字段赋值（不依赖字段可见性）。</summary>
        /// <param name="target">目标对象。</param>
        /// <param name="fieldName">字段名。</param>
        /// <param name="value">字符串值。</param>
        private static void SetStringField(Object target, string fieldName, string value)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            property.stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>把存在的场景追加进待写入列表；不存在的只提示、不写入。</summary>
        /// <param name="scenes">待写入列表。</param>
        /// <param name="assetPath">场景资产路径。</param>
        /// <summary>卡牌战斗场景路径（与 <c>CardBattleSceneBuilder</c> 保持一致）。</summary>
        private const string CardBattleScenePath = "Assets/AIFrameworkDemo/Scenes/CardBattleScene.unity";

        /// <summary>三消场景路径（与 <c>Match3SceneBuilder</c> 保持一致）。</summary>
        private const string Match3ScenePath = "Assets/AIFrameworkDemo/Scenes/Match3Scene.unity";

        private static void AddIfSceneExists(List<EditorBuildSettingsScene> scenes, string assetPath)
        {
            if (!SceneFileExists(assetPath))
            {
                Debug.LogWarning("[AIFramework] 场景文件不存在，已跳过 Build Settings：" + assetPath);
                return;
            }

            scenes.Add(new EditorBuildSettingsScene(assetPath, true));
        }

        /// <summary>
        /// 场景资产是否真实存在于磁盘上。
        /// <para>
        /// <b>为什么不用 <c>AssetDatabase.LoadAssetAtPath</c></b>：那个只认"已导入"的资产，
        /// 刚生成/刚拷进来还没导入的场景会被误判为不存在。
        /// <b>为什么可以直接用相对路径</b>：编辑器进程的当前目录就是工程根（<c>Assets</c> 与
        /// <c>ProjectSettings</c> 的父目录），资产相对路径可直接当物理路径判断。
        /// </para>
        /// </summary>
        /// <param name="assetPath">资产相对路径。</param>
        /// <returns>磁盘上是否存在该文件。</returns>
        private static bool SceneFileExists(string assetPath)
        {
            return System.IO.File.Exists(assetPath);
        }

        /// <summary>确保目录存在（父目录不存在时递归创建）。</summary>
        /// <param name="path">资产相对路径，例如 <c>Assets/AIFrameworkDemo/Scenes</c>。</param>
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = System.IO.Path.GetDirectoryName(path) ?? "Assets";
            parent = parent.Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
