using System.Collections.Generic;
using AIFramework.Adapters.Unity;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIFramework.Tests.EditMode
{
    /// <summary>
    /// 场景装配的补充校验：某些组件必须真的挂在场景里（而不是只存在于 builder 代码里）。
    /// <para>
    /// <b>为什么不放在 PlayMode</b>：BootScene 一旦运行就会自动切到 LobbyScene，场景对象在
    /// PlayMode 断言前就可能已被卸载；EditMode 直接打开场景检查序列化内容，确定性更高。
    /// </para>
    /// </summary>
    public sealed class SceneWiringTests
    {
        [Test]
        public void BootScene_HasQualityTierAutoAdapter()
        {
            UnityEngine.SceneManagement.Scene scene = EditorSceneManager.OpenScene(
                "Assets/AIFrameworkDemo/Scenes/BootScene.unity",
                OpenSceneMode.Additive);

            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                bool found = false;
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i].GetComponentInChildren<QualityTierAutoAdapter>(true) != null)
                    {
                        found = true;
                        break;
                    }
                }

                Assert.IsTrue(found, "BootScene 里没有 QualityTierAutoAdapter（场景需要重新生成）。");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
