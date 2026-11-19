#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using AIFramework.Adapters.Unity;
using AIFramework.Adapters.Unity.Battle;
using AIFramework.Adapters.Unity.Lobby;
using AIFramework.Adapters.Unity.Scene;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 场景装配校验：确认每个生成的场景里**该挂的组件都在、该注入的引用都非空**。
    /// <para>
    /// <b>为什么需要它</b>：场景由编辑器脚本生成，字段通过 <c>SerializedObject</c> 注入；
    /// 只要字段名打错一个字母，Unity 不会报错，只会在运行时静默拿到 <c>null</c>，
    /// 表现为"按钮点了没反应"。形状级编译校验也查不出这种问题（它只编译代码）。
    /// 这里用反射读 <c>[SerializeField]</c> 私有字段，能在离屏 batchmode 里就把这类错误打出来。
    /// </para>
    /// <para>
    /// 用法：<c>Unity.exe -batchmode -quit -projectPath &lt;unity&gt; -executeMethod AIFramework.Demo.Editor.SceneVerifier.VerifyAll</c>；
    /// 校验失败会抛异常，使 Unity 以非 0 退出码结束（可用于 CI 与生成脚本的守门）。
    /// </para>
    /// </summary>
    public static class SceneVerifier
    {
        private sealed class SceneExpectation
        {
            public SceneExpectation(string path, Type componentType, bool requiresCanvas = false)
            {
                Path = path;
                ComponentType = componentType;
                RequiresCanvas = requiresCanvas;
            }

            public string Path { get; }

            public Type ComponentType { get; }

            public bool RequiresCanvas { get; }
        }

        private static readonly SceneExpectation[] Expectations =
        {
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/BootScene.unity", typeof(BootController)),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/LobbyScene.unity", typeof(LobbyController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/CardBattleScene.unity", typeof(CardBattleController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/Match3Scene.unity", typeof(Match3Controller), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/TowerDefenseScene.unity", typeof(TowerDefenseController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/ArenaScene.unity", typeof(ArenaController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/IdleScene.unity", typeof(IdlePanelController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/GachaScene.unity", typeof(GachaPanelController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/PuzzleScene.unity", typeof(PuzzleController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/TetrisScene.unity", typeof(TetrisController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/AutoChessScene.unity", typeof(AutoChessController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/RhythmScene.unity", typeof(RhythmController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/BulletHellScene.unity", typeof(BulletHellController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/RunnerScene.unity", typeof(RunnerController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/PlatformerScene.unity", typeof(PlatformerController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/RoguelikeScene.unity", typeof(RoguelikeController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/BoardGameScene.unity", typeof(BoardGameController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/TacticsScene.unity", typeof(TacticsController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/MergeScene.unity", typeof(MergePanelController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/AsyncPvpScene.unity", typeof(AsyncPvpPanelController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/TycoonScene.unity", typeof(TycoonPanelController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/IdleRpgScene.unity", typeof(IdleRpgPanelController), requiresCanvas: true),
            new SceneExpectation("Assets/AIFrameworkDemo/Scenes/DemoScene.unity", typeof(AIFrameworkBehaviour))
        };

        /// <summary>校验全部场景；有任何问题都抛 <see cref="InvalidOperationException"/>。</summary>
        [MenuItem("AIFramework/校验场景装配", priority = 11)]
        public static void VerifyAll()
        {
            var failures = new List<string>();
            int sceneCount = 0;

            foreach (SceneExpectation expectation in Expectations)
            {
                sceneCount++;

                if (!System.IO.File.Exists(expectation.Path))
                {
                    failures.Add(expectation.Path + "：场景文件不存在（先生成场景）");
                    continue;
                }

                Scene scene = EditorSceneManager.OpenScene(expectation.Path);
                VerifyScene(expectation, scene, failures);
            }

            if (failures.Count > 0)
            {
                var message = new StringBuilder();
                message.Append("场景装配校验失败（").Append(failures.Count).Append(" 项）：");
                for (int i = 0; i < failures.Count; i++)
                {
                    message.Append('\n').Append("  - ").Append(failures[i]);
                }

                throw new InvalidOperationException(message.ToString());
            }

            Debug.Log("[AIFramework] 场景装配校验通过：" + sceneCount + " 个场景，组件与注入引用全部就位。");
        }

        private static void VerifyScene(SceneExpectation expectation, Scene scene, List<string> failures)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            if (roots.Length == 0)
            {
                failures.Add(expectation.Path + "：场景为空");
                return;
            }

            var components = new List<MonoBehaviour>();
            for (int i = 0; i < roots.Length; i++)
            {
                components.AddRange(roots[i].GetComponentsInChildren<MonoBehaviour>(true));
            }

            bool found = false;
            for (int i = 0; i < components.Count; i++)
            {
                MonoBehaviour component = components[i];
                if (component == null)
                {
                    continue;
                }

                if (expectation.ComponentType.IsInstanceOfType(component))
                {
                    found = true;
                }

                VerifySerializedReferences(expectation.Path, component, failures);
            }

            if (!found)
            {
                failures.Add(expectation.Path + "：缺少组件 " + expectation.ComponentType.Name);
            }

            if (expectation.RequiresCanvas)
            {
                // Canvas 继承 Component 而不是 MonoBehaviour，必须单独扫。
                var canvases = new List<Canvas>();
                for (int i = 0; i < roots.Length; i++)
                {
                    canvases.AddRange(roots[i].GetComponentsInChildren<Canvas>(true));
                }

                if (canvases.Count == 0)
                {
                    failures.Add(expectation.Path + "：UI 场景缺少 Canvas");
                }
            }
        }

        /// <summary>
        /// 用反射检查 <c>[SerializeField]</c> 私有字段：引用类型不得为空，字符串不得为空。
        /// 只检查对象引用与字符串——值类型（int/float/enum）有默认值，不算"漏注入"。
        /// </summary>
        private static void VerifySerializedReferences(string scenePath, MonoBehaviour component, List<string> failures)
        {
            Type type = component.GetType();
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                bool serialized = field.IsPublic || field.GetCustomAttribute<SerializeField>() != null;
                if (!serialized)
                {
                    continue;
                }

                // 只看我们自己声明的字段：Unity 内置组件（Text/Image/Button/EventSystem…）的
                // m_FontData / m_Material / m_Sprite / m_Padding 等在正常场景里就允许为空，
                // 把它们算成"漏注入"只会淹没真正的错误。
                if (IsUnityDeclared(field))
                {
                    continue;
                }

                if (field.FieldType.IsValueType)
                {
                    continue;
                }

                object? value = field.GetValue(component);
                string where = scenePath + " → " + GameObjectName(component) + "." + type.Name + "." + field.Name;

                if (field.FieldType == typeof(string))
                {
                    if (string.IsNullOrEmpty((string?)value))
                    {
                        failures.Add(where + "：字符串为空");
                    }

                    continue;
                }

                // 数组/集合要按元素判空：Collider[] 本身不是 UnityEngine.Object，
                // 直接 as Object 会永远得到 null（这里踩过一次）。
                if (field.FieldType.IsArray)
                {
                    var array = value as Array;
                    if (array == null || array.Length == 0)
                    {
                        failures.Add(where + "：数组为空（未注入）");
                        continue;
                    }

                    bool hasNullElement = false;
                    for (int e = 0; e < array.Length; e++)
                    {
                        if (array.GetValue(e) as UnityEngine.Object == null)
                        {
                            hasNullElement = true;
                            break;
                        }
                    }

                    if (hasNullElement)
                    {
                        failures.Add(where + "：数组含空元素");
                    }

                    continue;
                }

                var unityObject = value as UnityEngine.Object;
                if (unityObject == null)
                {
                    failures.Add(where + "：引用未注入（null）");
                }
            }
        }

        /// <summary>字段是否由 Unity 自己的程序集声明（UnityEngine / UnityEngine.UI 等）。</summary>
        private static bool IsUnityDeclared(FieldInfo field)
        {
            Type? declaring = field.DeclaringType;
            if (declaring == null)
            {
                return true;
            }

            if (declaring.Assembly == typeof(MonoBehaviour).Assembly)
            {
                return true;
            }

            string? ns = declaring.Namespace;
            return ns != null && (ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal));
        }

        private static string GameObjectName(MonoBehaviour component)
        {
            GameObject? go = component.gameObject;
            return go != null ? go.name : "(null)";
        }
    }
}
