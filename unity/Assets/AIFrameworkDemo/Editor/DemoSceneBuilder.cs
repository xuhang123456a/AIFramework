#nullable enable
using AIFramework.Adapters.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一键生成演示场景：不手写 .unity YAML，避免占位 GUID 拼错。
    /// 生成的场景只做两件事——挂一个 <see cref="AIFrameworkBehaviour"/>，摆两个 capsule 当单位。
    /// </summary>
    public static class DemoSceneBuilder
    {
        private const string ScenePath = "Assets/AIFrameworkDemo/Scenes/DemoScene.unity";

        [MenuItem("AIFramework/创建演示场景", priority = 0)]
        public static void CreateDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EnsureFolder("Assets/AIFrameworkDemo");
            EnsureFolder("Assets/AIFrameworkDemo/Scenes");

            var host = new GameObject("AIFrameworkBehaviour");
            var behaviour = host.AddComponent<AIFrameworkBehaviour>();

            GameObject shield = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            shield.name = "ShieldUnit";
            shield.transform.position = new Vector3(0f, 0f, 0f);

            GameObject attacker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            attacker.name = "AttackerUnit";
            attacker.transform.position = new Vector3(0f, 0f, 3f);

            GameObject projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            projectile.name = "IncomingProjectile";
            projectile.transform.position = new Vector3(0f, 0f, 1.4f);
            projectile.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);

            // 把两个胶囊的碰撞体喂给 Behaviour：没有这份清单，Play 时它会认为"没有可绑定单位"而空转。
            var units = new Collider[2];
            units[0] = shield.GetComponent<Collider>();
            units[1] = attacker.GetComponent<Collider>();
            var serialized = new SerializedObject(behaviour);
            SerializedProperty sceneUnits = serialized.FindProperty("_sceneUnits");
            sceneUnits.arraySize = units.Length;
            for (int i = 0; i < units.Length; i++)
            {
                sceneUnits.GetArrayElementAtIndex(i).objectReferenceValue = units[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            Debug.Log("[AIFramework] 演示场景已生成：" + ScenePath + "，按 Play 运行。");
        }

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
