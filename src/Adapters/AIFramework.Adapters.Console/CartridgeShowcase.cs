using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Numerics;
using AIFramework.Contracts.Ports;
using AIFramework.Kernel.Turn.AutoChess;
using AIFramework.Kernel.Turn.Puzzle;
using AIFramework.Kernel.Turn.Rhythm;
using AIFramework.Kernel.Turn.Tetris;

namespace AIFramework.Adapters.Console
{
    /// <summary>
    /// "卡带巡礼"：无头跑一遍 Kernel.Turn 的每一套内容，证明同一内核能承载完全不同的规则。
    /// <para>
    /// <b>为什么不走工厂</b>：这些卡带在场景里由适配层用 <c>GameplayKernelType</c> 派发；
    /// 命令行冒烟只关心"内容本身能跑通"，直接 new 出沙盒更短、也顺便证明内核内容不依赖装配层。
    /// </para>
    /// </summary>
    internal static class CartridgeShowcase
    {
        /// <summary>依次跑一遍所有回合制卡带并打印结论。</summary>
        /// <param name="writer">输出端口。</param>
        public static void Run(IIOWriter writer)
        {
            RunPuzzle(writer);
            RunTetris(writer);
            RunAutoChess(writer);
            RunRhythm(writer);
        }

        private static void RunPuzzle(IIOWriter writer)
        {
            var world = new PuzzleWorld(new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 1));

            // 第 1 关内置解法：右 右 右（3 步）。
            world.TryMove(1);
            world.TryMove(1);
            world.TryMove(1);
            world.Step(1f / 60f);

            GameplayResult result = world.CollectResult();
            writer.WriteLine("[Puzzle] 关卡=" + world.LevelId + " 步数=" + world.Moves
                + " 解出=" + world.IsSolved + " 归位=" + world.BoxesOnGoal + "/" + world.BoxCount
                + " 胜负=" + result.IsVictory + " 战利品=" + DescribeLoots(result));
        }

        private static void RunTetris(IIOWriter writer)
        {
            var world = new TetrisWorld(new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 0));

            // 硬降 3 块，再跑 2 秒重力；只要能消行/落地不异常就说明状态机稳定。
            for (int i = 0; i < 3; i++)
            {
                world.HardDrop();
                world.Step(0.5f);
            }

            writer.WriteLine("[Tetris] 方块数=" + world.PieceCount + " 分数=" + world.Score
                + " 消行=" + world.ClearedLines + " 等级=" + world.Level
                + " 下落间隔=" + world.DropIntervalMs + "ms 当前块=" + world.CurrentKind);
        }

        private static void RunAutoChess(IIOWriter writer)
        {
            var world = new AutoChessWorld(new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 3));

            world.TryPlace(1);
            world.TryPlace(2);
            world.TryPlace(3);
            world.TryMoveUnit(1, 0, 0);
            world.StartBattle();
            world.Step(1f / 60f);

            writer.WriteLine("[AutoChess] 回合=" + world.RoundIndex + " 玩家HP=" + world.PlayerHp
                + " 敌方HP=" + world.EnemyHp + " 战斗已结算=" + world.IsBattleResolved
                + " 玩家单位=" + world.PlayerUnitCount + " 敌方单位=" + world.EnemyUnitCount);
        }

        private static void RunRhythm(IIOWriter writer)
        {
            var world = new RhythmWorld(new GameplayInitData(levelId: 1, randomSeed: 20261120, unitCount: 0))
            {
                AutoPlay = true,
            };

            // 自动完美演奏：推进到曲尾。步长必须小于 Perfect 窗口（45ms），
            // 否则自动打点会跨过窗口，变成"漏打判 Miss"（这一点第一次跑就暴露了）。
            for (int i = 0; i < 2000 && !world.IsFinished; i++)
            {
                world.Tick(0.02d);
            }

            writer.WriteLine("[Rhythm] 曲长=" + world.SongLengthSeconds.ToString("F1") + "s 音符=" + world.NoteCount
                + " Perfect=" + world.PerfectCount + " Miss=" + world.MissCount
                + " 最大连击=" + world.MaxCombo + " 准确率=" + world.Accuracy.ToString("P1")
                + " 结束=" + world.IsFinished);
        }

        private static string DescribeLoots(GameplayResult result)
        {
            if (result.Loots == null || result.Loots.Count == 0)
            {
                return "无";
            }

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < result.Loots.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                LootItem loot = result.Loots[i];
                builder.Append(loot.ItemId);
                builder.Append('x');
                builder.Append(loot.Count);
            }

            return builder.ToString();
        }
    }
}