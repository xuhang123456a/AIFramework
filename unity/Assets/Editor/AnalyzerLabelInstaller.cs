using UnityEditor;
using UnityEngine;

namespace AIFramework.Editor
{
    /// <summary>
    /// 自动给 <c>Assets/Plugins/Analyzers/</c> 下的 DLL 打 <c>RoslynAnalyzer</c> 标签。
    /// 手动打标签是 Unity 里最容易漏的一步，漏了守卫就静默失效——所以这里做一次自动补齐。
    /// </summary>
    public static class AnalyzerLabelInstaller
    {
        private const string AnalyzerFolder = "Assets/Plugins/Analyzers";
        private const string RoslynLabel = "RoslynAnalyzer";

        [InitializeOnLoadMethod]
        private static void EnsureLabels()
        {
            if (!AssetDatabase.IsValidFolder(AnalyzerFolder))
            {
                return;
            }

            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { AnalyzerFolder });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!path.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Object asset = AssetDatabase.LoadAssetAtPath<Object>(path);
                if (asset == null)
                {
                    continue;
                }

                string[] labels = AssetDatabase.GetLabels(asset);
                if (System.Array.IndexOf(labels, RoslynLabel) >= 0)
                {
                    continue;
                }

                var next = new string[labels.Length + 1];
                System.Array.Copy(labels, next, labels.Length);
                next[labels.Length] = RoslynLabel;
                AssetDatabase.SetLabels(asset, next);
                Debug.Log("[AIFramework] 已为 " + path + " 打上 RoslynAnalyzer 标签。");
            }
        }
    }
}
