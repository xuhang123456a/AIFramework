// UnityEditor 形状桩：只为"无 Unity 机器上编译校验编辑器脚本"存在。
// 它不做任何真实操作（不会建场景、不会写资产），因此**不能替代真机验证**。
using System;
using UnityEngine;

namespace UnityEditor
{
    /// <summary>菜单项特性。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        /// <summary>菜单排序权重（Unity 的 MenuItem 也支持 `priority = N` 命名参数写法）。</summary>
        public int priority;

        public MenuItem(string itemName)
        {
        }

        public MenuItem(string itemName, bool isValidateFunction)
        {
        }

        public MenuItem(string itemName, bool isValidateFunction, int priority)
        {
        }
    }

    /// <summary>初始化回调特性（点位：编辑器加载后执行一次）。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class InitializeOnLoadMethod : Attribute
    {
    }

    /// <summary>资产导入回调特性。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class DidReloadScripts : Attribute
    {
    }

    /// <summary>编辑器操作入口的精简桩。</summary>
    public static class EditorUtility
    {
        /// <summary>标记资产已修改。</summary>
        public static void SetDirty(UnityEngine.Object target)
        {
        }

        /// <summary>弹消息框（桩：直接返回 true）。</summary>
        public static bool DisplayDialog(string title, string message, string ok)
        {
            return true;
        }
    }

    /// <summary>资产数据库桩。</summary>
    /// <summary>脚本后端（形状桩）。</summary>
    public enum ScriptingImplementation
    {
        Mono2x = 0,
        IL2CPP = 1,
    }

    /// <summary>构建目标分组（形状桩）。</summary>
    public enum BuildTargetGroup
    {
        Unknown = 0,
        Standalone = 1,
    }

    /// <summary>项目设置（形状桩）。</summary>
    public static class PlayerSettings
    {
        /// <summary>设置脚本后端（桩）。</summary>
        public static void SetScriptingBackend(BuildTargetGroup targetGroup, ScriptingImplementation implementation)
        {
        }
    }

    public static class AssetDatabase
    {
        /// <summary>目录是否存在（桩：永远 true，避免把真实路径判断带进编译校验）。</summary>
        public static bool IsValidFolder(string path)
        {
            return true;
        }

        /// <summary>刷新（桩）。</summary>
        public static void Refresh()
        {
        }

        /// <summary>创建资源（桩）。</summary>
        public static void CreateAsset(UnityEngine.Object asset, string path)
        {
        }

        /// <summary>删除资源（桩）。</summary>
        public static void DeleteAsset(string path)
        {
        }

        /// <summary>保存资产（桩）。</summary>
        public static void SaveAssets()
        {
        }

        /// <summary>创建目录（桩）。</summary>
        public static string CreateFolder(string parentFolder, string newFolderName)
        {
            return parentFolder + "/" + newFolderName;
        }

        /// <summary>装载资产（桩：永远 null）。</summary>
        public static T? LoadAssetAtPath<T>(string assetPath) where T : UnityEngine.Object
        {
            return null;
        }

        /// <summary>路径 → GUID（桩）。</summary>
        public static string AssetPathToGUID(string path)
        {
            return string.Empty;
        }

        /// <summary>GUID → 路径（桩）。</summary>
        public static string GUIDToAssetPath(string guid)
        {
            return string.Empty;
        }

        /// <summary>按标签查找（桩：空数组）。</summary>
        public static string[] FindAssets(string filter)
        {
            return Array.Empty<string>();
        }

        /// <summary>按标签在指定目录下查找（桩：空数组）。</summary>
        public static string[] FindAssets(string filter, string[] searchInFolders)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>序列化对象桩：FindProperty 永远返回一个可写的假属性。</summary>
    public sealed class SerializedObject
    {
        public SerializedObject(UnityEngine.Object target)
        {
        }

        /// <summary>查找字段。</summary>
        public SerializedProperty FindProperty(string propertyPath)
        {
            return new SerializedProperty();
        }

        /// <summary>应用修改。</summary>
        public bool ApplyModifiedProperties()
        {
            return true;
        }

        /// <summary>应用修改（不进撤销栈）。</summary>
        public bool ApplyModifiedPropertiesWithoutUndo()
        {
            return true;
        }
    }

    /// <summary>序列化属性桩。</summary>
    public sealed class SerializedProperty
    {
        /// <summary>对象引用值。</summary>
        public UnityEngine.Object? objectReferenceValue { get; set; }

        /// <summary>整数值。</summary>
        public int intValue { get; set; }

        /// <summary>布尔值。</summary>
        public bool boolValue { get; set; }

        /// <summary>字符串值。</summary>
        public string stringValue { get; set; } = string.Empty;

        /// <summary>浮点值（建场景脚本给 <c>fixedStepSeconds</c> 这类 float 字段赋值时用）。</summary>
        public float floatValue { get; set; }

        /// <summary>数组长度（桩：可读写）。</summary>
        public int arraySize { get; set; }

        /// <summary>取数组元素（桩：返回新属性）。</summary>
        public SerializedProperty GetArrayElementAtIndex(int index)
        {
            return new SerializedProperty();
        }

        /// <summary>插入数组元素（桩）。</summary>
        public void InsertArrayElementAtIndex(int index)
        {
        }

        /// <summary>删除数组元素（桩）。</summary>
        public void DeleteArrayElementAtIndex(int index)
        {
        }
    }

    /// <summary>选中项桩。</summary>
    public static class Selection
    {
        /// <summary>当前选中的对象。</summary>
        public static UnityEngine.Object? activeObject { get; set; }

        /// <summary>当前选中的 GameObject（导出器用它取面板根；桩里可读写，不连编辑器）。</summary>
        public static GameObject? activeGameObject { get; set; }

        /// <summary>当前选中的对象集合。</summary>
        public static UnityEngine.Object[] objects { get; set; } = Array.Empty<UnityEngine.Object>();
    }

    /// <summary>
    /// Build Settings 里的一条场景记录（桩：只是一个可写容器，不校验场景是否存在、不写回磁盘）。
    /// </summary>
    public sealed class EditorBuildSettingsScene
    {
        /// <summary>场景资产路径，例如 <c>Assets/AIFrameworkDemo/Scenes/BootScene.unity</c>。</summary>
        public string path { get; set; } = string.Empty;

        /// <summary>是否勾选进构建。</summary>
        public bool enabled { get; set; }

        /// <summary>空条目（真实 Unity 也允许无参构造后再赋 <see cref="path"/>）。</summary>
        public EditorBuildSettingsScene()
        {
        }

        /// <summary>以路径与勾选状态构造。</summary>
        /// <param name="path">场景资产路径。</param>
        /// <param name="enabled">是否勾选进构建。</param>
        public EditorBuildSettingsScene(string path, bool enabled)
        {
            this.path = path;
            this.enabled = enabled;
        }
    }

    /// <summary>
    /// 构建设置（桩：读写只在内存里，<b>不会</b>真正落盘到 <c>ProjectSettings/EditorBuildSettings.asset</c>，
    /// 因此"Build Settings 是否被正确写入"必须真机验证）。
    /// </summary>
    public static class EditorBuildSettings
    {
        /// <summary>构建场景列表；真实 Unity 里赋值即原子替换整个列表并使资产变脏。</summary>
        public static EditorBuildSettingsScene[] scenes { get; set; } = Array.Empty<EditorBuildSettingsScene>();
    }
}

namespace UnityEditor.SceneManagement
{
    using UnityEngine.SceneManagement;

    /// <summary>新建场景时的初始化类型。</summary>
    public enum NewSceneSetup
    {
        EmptyScene = 0,
        DefaultGameObjects = 1
    }

    /// <summary>新建场景模式。</summary>
    public enum NewSceneMode
    {
        Single = 0,
        Additive = 1
    }

    /// <summary>场景管理桩。</summary>
    public static class EditorSceneManager
    {
        /// <summary>新建场景（桩：返回空场景）。</summary>
        public static Scene NewScene(NewSceneSetup setup, NewSceneMode mode)
        {
            return default;
        }

        /// <summary>保存场景（桩：返回 true）。</summary>
        public static bool SaveScene(Scene scene, string scenePath)
        {
            return true;
        }

        /// <summary>保存场景（桩：返回 true）。</summary>
        public static bool SaveScene(Scene scene)
        {
            return true;
        }

        /// <summary>当前打开的场景（桩：空场景）。</summary>
        public static Scene GetActiveScene()
        {
            return default;
        }

        /// <summary>打开场景（桩：返回空场景）。</summary>
        public static Scene OpenScene(string scenePath)
        {
            return default;
        }
    }
}

namespace UnityEngine.SceneManagement
{
    /// <summary>场景句柄桩。</summary>
    public struct Scene
    {
        /// <summary>场景名。</summary>
        public string name { get; set; }

        /// <summary>场景路径。</summary>
        public string path { get; set; }

        /// <summary>是否有效。</summary>
        public bool IsValid()
        {
            return true;
        }

        /// <summary>取根物体（桩：空数组）。</summary>
        public GameObject[] GetRootGameObjects()
        {
            return Array.Empty<GameObject>();
        }
    }

    /// <summary>场景加载桩。</summary>
    public static class SceneManager
    {
        /// <summary>按名加载。</summary>
        public static void LoadScene(string sceneName)
        {
        }

        /// <summary>按索引加载。</summary>
        public static void LoadScene(int sceneBuildIndex)
        {
        }

        /// <summary>当前激活场景（桩：空场景，名字为空串）。</summary>
        public static Scene GetActiveScene()
        {
            return default;
        }
    }
}





