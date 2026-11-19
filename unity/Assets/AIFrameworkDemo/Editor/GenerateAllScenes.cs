#nullable enable
using UnityEditor;
using UnityEngine;

namespace AIFramework.Demo.Editor
{
    /// <summary>
    /// 一次性生成全部演示场景（供命令行 / CI 使用）。
    /// <para>
    /// 为什么要有入口：Unity 的 <c>-executeMethod</c> 只能调一个静态无参方法，
    /// 而我们需要按顺序生成 5 个场景（Boot → Lobby → CardBattle → Match3 → Demo）。
    /// 菜单项各自仍可单独调用（便于只重生成一个）。
    /// </para>
    /// <para>
    /// 用法（工程未被其它编辑器实例占用时）：
    /// <c>Unity.exe -batchmode -quit -projectPath &lt;unity 目录&gt; -executeMethod AIFramework.Demo.Editor.GenerateAllScenes.Generate -logFile gen.log</c>
    /// </para>
    /// </summary>
    public static class GenerateAllScenes
    {
        /// <summary>按依赖顺序生成全部场景。</summary>
        [MenuItem("AIFramework/生成全部场景", priority = 10)]
        public static void Generate()
        {
            Debug.Log("[AIFramework] 开始生成全部场景…");

            DemoSceneBuilder.CreateDemoScene();
            LobbySceneBuilder.CreateLobbyScene();
            CardBattleSceneBuilder.CreateCardBattleScene();
            Match3SceneBuilder.CreateMatch3Scene();
            TowerDefenseSceneBuilder.CreateTowerDefenseScene();
            ArenaSceneBuilder.CreateArenaScene();
            IdleSceneBuilder.CreateIdleScene();
            GachaSceneBuilder.CreateGachaScene();
            PuzzleSceneBuilder.CreatePuzzleScene();
            TetrisSceneBuilder.CreateTetrisScene();
            AutoChessSceneBuilder.CreateAutoChessScene();
            RhythmSceneBuilder.CreateRhythmScene();
            BulletHellSceneBuilder.CreateBulletHellScene();
            RunnerSceneBuilder.CreateRunnerScene();
            PlatformerSceneBuilder.CreatePlatformerScene();
            BootSceneBuilder.CreateBootScene();

            // Build Settings 属于工程配置（ProjectSettings/EditorBuildSettings.asset），
            // 不写进去的话 Boot 场景按 Play 会跳转失败；命令行生成时一并写入。
            BootSceneBuilder.AddScenesToBuildSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 生成完立刻校验：字段漏注入这类问题只有真 Unity 能发现（编译期与形状校验都看不见）。
            SceneVerifier.VerifyAll();

            Debug.Log("[AIFramework] 全部场景生成完成。");
        }
    }
}
