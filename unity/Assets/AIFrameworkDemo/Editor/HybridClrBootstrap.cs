using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// HybridCLR 的一键安装/自检入口。
    /// <para>
    /// <b>为什么用反射而不是直接引用 HybridCLR.Editor</b>：本工程在"没装 HybridCLR"的状态下也必须能编译
    /// （纯逻辑闸门与形状校验都跑在这台机器上）。如果直接 <c>using HybridCLR.Installer;</c>，
    /// 一旦包没解析整个工程就编不过，等于用热更框架把主工程当人质。这里只按类型名字符串探测，
    /// 包不在时给出明确提示而不是编译错误。
    /// </para>
    /// <para>
    /// v9 起 HybridCLR 采用**工程内** il2cpp（<c>Library/HybridCLR/LocalIl2CppData-&lt;Platform&gt;</c>），
    /// 不再改写 Unity 安装目录，因此装/卸都是可逆的工程级操作。
    /// </para>
    /// </summary>
    public static class HybridClrBootstrap
    {
        private const string PackageName = "com.code-philosophy.hybridclr";
        private const string PackageVersion = "v9.1.0";
        private const string InstallerTypeName = "HybridCLR.Installer.InstallerController";

        [MenuItem("AIFramework/HybridCLR/安装并自检", priority = 50)]
        public static void InstallAndVerifyFromMenu()
        {
            int exitCode = InstallAndVerify();
            if (exitCode == 0)
            {
                EditorUtility.DisplayDialog("HybridCLR", "安装与自检通过，详见 Console。", "好");
            }
            else
            {
                EditorUtility.DisplayDialog("HybridCLR", "安装或自检失败（退出码 " + exitCode + "），详见 Console。", "好");
            }
        }

        [MenuItem("AIFramework/HybridCLR/只做自检（不安装）", priority = 51)]
        public static void VerifyOnlyFromMenu()
        {
            int exitCode = VerifyOnly();
            Debug.Log("[HybridCLR] 自检退出码=" + exitCode);
        }

        [MenuItem("AIFramework/HybridCLR/配置热更程序集并生成元数据", priority = 52)]
        public static void ConfigureHotUpdateFromMenu()
        {
            int exitCode = ConfigureHotUpdateAssembly();
            EditorUtility.DisplayDialog(
                "HybridCLR",
                exitCode == 0 ? "热更程序集配置与元数据生成完成，详见 Console。" : "配置失败（退出码 " + exitCode + "），详见 Console。",
                "好");
        }

        /// <summary>
        /// 把 <c>AIFramework.HotUpdate</c> 写进 HybridCLRSettings.hotUpdateAssemblies，并生成 AOT 元数据
        /// （link.xml / AOTGenericReferences / il2cpp def / 方法桥）。
        /// <para>
        /// 用 SerializedObject 而不是反射字段：HybridCLR 的 Settings 字段名稳定在序列化层，
        /// 但 C# 可见性会随版本变（v9 的公开属性只有 name/hideFlags），走序列化最不容易被版本升级打穿。
        /// </para>
        /// </summary>
        public static int ConfigureHotUpdateAssembly()
        {
            const string settingsDir = "Assets/HybridCLRGenerate";
            const string settingsPath = settingsDir + "/HybridCLRSettings.asset";
            const string hotUpdateAssembly = "AIFramework.HotUpdate";

            Type? settingsType = FindType("HybridCLR.Settings.HybridCLRSettings");
            if (settingsType == null)
            {
                Debug.LogError("[HybridCLR] 找不到 HybridCLR.Settings.HybridCLRSettings，包未解析？");
                return 2;
            }

            if (!AssetDatabase.IsValidFolder(settingsDir))
            {
                AssetDatabase.CreateFolder("Assets", "HybridCLRGenerate");
            }

            ScriptableObject? settings = AssetDatabase.LoadAssetAtPath<ScriptableObject>(settingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance(settingsType);
                AssetDatabase.CreateAsset(settings, settingsPath);
                Debug.Log("[HybridCLR] 已创建 " + settingsPath);
            }

            var serialized = new SerializedObject(settings);
            SerializedProperty? listProperty = serialized.FindProperty("hotUpdateAssemblies");
            if (listProperty == null)
            {
                Debug.LogError("[HybridCLR] 设置里没有 hotUpdateAssemblies 字段，版本不匹配（当前包可能不是 v9.x）。");
                return 3;
            }

            bool found = false;
            for (int i = 0; i < listProperty.arraySize; i++)
            {
                if (listProperty.GetArrayElementAtIndex(i).stringValue == hotUpdateAssembly)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                int index = listProperty.arraySize;
                listProperty.InsertArrayElementAtIndex(index);
                listProperty.GetArrayElementAtIndex(index).stringValue = hotUpdateAssembly;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                Debug.Log("[HybridCLR] hotUpdateAssemblies += " + hotUpdateAssembly);
            }
            else
            {
                Debug.Log("[HybridCLR] hotUpdateAssemblies 已包含 " + hotUpdateAssembly + "（幂等）");
            }

            Type? prebuild = FindType("HybridCLR.Commands.PrebuildCommand");
            MethodInfo? generateAll = prebuild?.GetMethod("GenerateAll", BindingFlags.Public | BindingFlags.Static);
            if (generateAll == null)
            {
                Debug.LogError("[HybridCLR] 找不到 PrebuildCommand.GenerateAll()，跳过元数据生成。");
                return 4;
            }

            try
            {
                generateAll.Invoke(null, null);
                Debug.Log("[HybridCLR] PrebuildCommand.GenerateAll() 完成");
            }
            catch (Exception ex)
            {
                Debug.LogError("[HybridCLR] GenerateAll 失败：" + (ex.InnerException ?? ex));
                return 5;
            }

            bool linkXml = File.Exists(Path.Combine(Application.dataPath, "link.xml"));
            bool aotRefs = File.Exists(Path.Combine(Application.dataPath, "AOTGenericReferences.cs"));
            string generated = Path.Combine(Application.dataPath, "HybridCLRGenerate");
            Debug.Log("[HybridCLR] link.xml=" + linkXml + " AOTGenericReferences.cs=" + aotRefs
                + " settings=OK generatedDir=" + Directory.Exists(generated));

            if (!linkXml || !aotRefs)
            {
                Debug.LogError("[HybridCLR] 元数据产物不完整（link.xml / AOTGenericReferences.cs 缺一），请查看 Unity 日志。");
                return 6;
            }

            return 0;
        }

        /// <summary>供 batchmode 调用：打印诊断 → 安装（幂等）→ 再自检。返回 0 表示通过。</summary>
        public static int InstallAndVerify()
        {
            Debug.Log("[HybridCLR] unity=" + Application.unityVersion + " package=" + PackageName + "@" + PackageVersion);

            Type? controllerType = FindType(InstallerTypeName);
            if (controllerType == null)
            {
                Debug.LogError("[HybridCLR] 找不到 " + InstallerTypeName
                    + "：包没解析成功。检查 unity/Packages/manifest.json 里的 " + PackageName
                    + " 是否指向 " + PackageVersion + "，以及 Package Manager 是否报错。");
                return 2;
            }

            object controller = Activator.CreateInstance(controllerType)!;
            Log(controllerType, controller, "MajorVersion");
            Log(controllerType, controller, "PackageVersion");
            Log(controllerType, controller, "HybridclrLocalVersion");
            Log(controllerType, controller, "InstalledLibil2cppVersion");

            try
            {
                controllerType.GetMethod("InstallDefaultHybridCLR")?.Invoke(controller, null);
                Debug.Log("[HybridCLR] InstallDefaultHybridCLR 调用完成（幂等，已安装时会跳过）");
            }
            catch (Exception ex)
            {
                Debug.LogError("[HybridCLR] 安装失败：" + (ex.InnerException ?? ex));
                return 3;
            }

            return VerifyOnly();
        }

        /// <summary>只自检：包是否解析、是否已安装、本地 il2cpp 目录是否就位。返回 0 表示通过。</summary>
        public static int VerifyOnly()
        {
            Type? controllerType = FindType(InstallerTypeName);
            if (controllerType == null)
            {
                Debug.LogError("[HybridCLR] 自检失败：包未解析（" + InstallerTypeName + " 不存在）。");
                return 2;
            }

            object controller = Activator.CreateInstance(controllerType)!;
            bool installed = (bool)(controllerType.GetMethod("HasInstalledHybridCLR")?.Invoke(controller, null) ?? false);
            string localIl2Cpp = (string)(controllerType.GetProperty("InstalledLibil2cppVersion")?.GetValue(controller) ?? string.Empty);

            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string localDir = Path.Combine(projectRoot, "Library/HybridCLR");
            bool localIl2CppExists = Directory.Exists(localDir);

            Debug.Log("[HybridCLR] installed=" + installed + " installedLibil2cpp=" + localIl2Cpp
                + " localCache=" + localIl2CppExists + " path=" + localDir);

            if (!installed || !localIl2CppExists)
            {
                Debug.LogError("[HybridCLR] 自检失败：installed=" + installed + " localCache=" + localIl2CppExists);
                return 4;
            }

            Debug.Log("[HybridCLR] 自检通过：安装态就绪（下一步是热更程序集配置 + IL2CPP 出包，见 Doc/07）。");
            return 0;
        }

        private static Type? FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type? type = assemblies[i].GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static void Log(Type controllerType, object controller, string propertyName)
        {
            PropertyInfo? property = controllerType.GetProperty(propertyName);
            if (property != null)
            {
                Debug.Log("[HybridCLR] " + propertyName + "=" + property.GetValue(controller));
            }
        }
    }
}
