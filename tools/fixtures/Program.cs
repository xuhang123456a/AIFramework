using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AIFramework.Fixtures
{
    /// <summary>
    /// Phase 1 配置表夹具生成器：把四张表的二进制写进 Console 与 Unity 两份资源目录。
    /// <para>
    /// **这是过渡工具**：Luban 接入后删除，改用 `dotnet run --project tools/luban-gen` 之类的正式管线。
    /// 之所以现在要有：手搓 12/16 字节二进制太容易出错，且 Console 与 Unity 必须读同一份数据，
    /// 否则"冒烟通过但 Unity 里是另一套数值"。
    /// </para>
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var targets = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length)
                {
                    targets.Add(args[++i]);
                }
            }

            if (targets.Count == 0)
            {
                targets.Add(Path.Combine("unity", "Assets", "StreamingAssets", "AIFrameworkDemo"));
                targets.Add(Path.Combine("src", "Adapters", "AIFramework.Adapters.Console", "DemoTables"));
            }

            foreach (string target in targets)
            {
                Directory.CreateDirectory(target);
                WriteSignInReward(target);
                WriteShop(target);
                WriteQuest(target);
                WriteMail(target);
                WriteCard(target);
                WriteBuilding(target);
                WriteGachaPool(target);
                WriteMergeItem(target);
                WriteLocalization(target);
                WriteTower(target);
                WriteTycoonBuilding(target);
                WriteIdleStage(target);
                Console.WriteLine("已生成夹具：" + Path.GetFullPath(target));
            }

            return 0;
        }

        private static void WriteSignInReward(string directory)
        {
            // TbSignInReward: [rowCount] + row{days, itemId, itemCount}
            var rows = new List<(int Day, int ItemId, int ItemCount)>();
            for (int day = 1; day <= 7; day++)
            {
                rows.Add((day, 1000 + day, 1));
            }

            WriteTable(Path.Combine(directory, "TbSignInReward.bin"), rows.Count, (writer, index) =>
            {
                (int day, int itemId, int itemCount) = rows[index];
                writer.Write(day);
                writer.Write(itemId);
                writer.Write(itemCount);
            });
        }

        private static void WriteShop(string directory)
        {
            // TbShopItem: row{itemId, priceItemId, priceCount}
            var rows = new List<(int ItemId, int PriceItemId, int PriceCount)>
            {
                (2001, 1, 100),
                (2002, 1, 250),
                (2003, 1, 500)
            };

            WriteTable(Path.Combine(directory, "TbShopItem.bin"), rows.Count, (writer, index) =>
            {
                (int itemId, int priceItemId, int priceCount) = rows[index];
                writer.Write(itemId);
                writer.Write(priceItemId);
                writer.Write(priceCount);
            });
        }

        private static void WriteQuest(string directory)
        {
            // TbQuest: row{questId, targetCount, rewardItemId, rewardCount}
            var rows = new List<(int QuestId, int TargetCount, int RewardItemId, int RewardCount)>
            {
                (1, 3, 2001, 1),
                (2, 5, 1, 500)
            };

            WriteTable(Path.Combine(directory, "TbQuest.bin"), rows.Count, (writer, index) =>
            {
                (int questId, int targetCount, int rewardItemId, int rewardCount) = rows[index];
                writer.Write(questId);
                writer.Write(targetCount);
                writer.Write(rewardItemId);
                writer.Write(rewardCount);
            });
        }

        private static void WriteMail(string directory)
        {
            // TbMail: row{mailId, rewardItemId, rewardCount, sendDayOffset}
            var rows = new List<(int MailId, int RewardItemId, int RewardCount, int SendDayOffset)>
            {
                (1, 1, 100, 0),
                (2, 2003, 1, 1)
            };

            WriteTable(Path.Combine(directory, "TbMail.bin"), rows.Count, (writer, index) =>
            {
                (int mailId, int rewardItemId, int rewardCount, int sendDayOffset) = rows[index];
                writer.Write(mailId);
                writer.Write(rewardItemId);
                writer.Write(rewardCount);
                writer.Write(sendDayOffset);
            });
        }

        private static void WriteCard(string directory)
        {
            // TbCard: row{cardId, cost, damage, block, copies} —— 20 字节/行
            var rows = new List<(int CardId, int Cost, int Damage, int Block, int Copies)>
            {
                (1, 1, 6, 0, 3),
                (2, 1, 0, 5, 3),
                (3, 2, 12, 0, 2),
                (4, 0, 3, 3, 2)
            };

            WriteTable(Path.Combine(directory, "TbCard.bin"), rows.Count, (writer, index) =>
            {
                (int cardId, int cost, int damage, int block, int copies) = rows[index];
                writer.Write(cardId);
                writer.Write(cost);
                writer.Write(damage);
                writer.Write(block);
                writer.Write(copies);
            });
        }

        private static void WriteTower(string directory)
        {
            // TbTower: row{towerId, cost, damage, range} —— 16 字节/行（塔防切片的 Feature.TowerDefense 子表）
            var rows = new List<(int TowerId, int Cost, int Damage, int Range)>
            {
                (1, 100, 10, 2),
                (2, 250, 24, 3)
            };

            WriteTable(Path.Combine(directory, "TbTower.bin"), rows.Count, (writer, index) =>
            {
                (int towerId, int cost, int damage, int range) = rows[index];
                writer.Write(towerId);
                writer.Write(cost);
                writer.Write(damage);
                writer.Write(range);
            });
        }

        private static void WriteTycoonBuilding(string directory)
        {
            // TbTycoonBuilding: row{buildingId, kind, buildCost, baseIncome, maxLevel} —— 20 字节/行
            // （模拟经营切片 Feature.Tycoon 的子表；kind: 0 摊位 / 1 商店 / 2 餐厅）
            var rows = new List<(int BuildingId, int Kind, int BuildCost, int BaseIncome, int MaxLevel)>
            {
                (1, 0, 100, 5, 5),
                (2, 1, 300, 12, 5),
                (3, 2, 800, 30, 3)
            };

            WriteTable(Path.Combine(directory, "TbTycoonBuilding.bin"), rows.Count, (writer, index) =>
            {
                (int buildingId, int kind, int buildCost, int baseIncome, int maxLevel) = rows[index];
                writer.Write(buildingId);
                writer.Write(kind);
                writer.Write(buildCost);
                writer.Write(baseIncome);
                writer.Write(maxLevel);
            });
        }

        private static void WriteIdleStage(string directory)
        {
            // TbIdleStage: row{stageId, enemyHp, enemyAttack, goldReward, requiredPower} —— 20 字节/行
            // （挂机 RPG 切片 Feature.IdleRpg 的子表；stageId 必须从 1 起逐行连续）
            var rows = new List<(int StageId, int EnemyHp, int EnemyAttack, int GoldReward, int RequiredPower)>
            {
                (1, 30, 2, 20, 10),
                (2, 60, 4, 40, 16),
                (3, 120, 6, 80, 28),
                (4, 240, 9, 160, 46),
                (5, 480, 12, 320, 70)
            };

            WriteTable(Path.Combine(directory, "TbIdleStage.bin"), rows.Count, (writer, index) =>
            {
                (int stageId, int enemyHp, int enemyAttack, int goldReward, int requiredPower) = rows[index];
                writer.Write(stageId);
                writer.Write(enemyHp);
                writer.Write(enemyAttack);
                writer.Write(goldReward);
                writer.Write(requiredPower);
            });
        }

        private static void WriteBuilding(string directory)
        {
            // TbBuilding: row{buildingId, baseIncome, upgradeCost, maxLevel} —— 16 字节/行
            var rows = new List<(int BuildingId, int BaseIncome, int UpgradeCost, int MaxLevel)>
            {
                (1, 5, 100, 5),
                (2, 12, 250, 5),
                (3, 30, 600, 3)
            };

            WriteTable(Path.Combine(directory, "TbBuilding.bin"), rows.Count, (writer, index) =>
            {
                (int buildingId, int baseIncome, int upgradeCost, int maxLevel) = rows[index];
                writer.Write(buildingId);
                writer.Write(baseIncome);
                writer.Write(upgradeCost);
                writer.Write(maxLevel);
            });
        }

        private static void WriteGachaPool(string directory)
        {
            // TbGachaPool: row{itemId, weight, isRare} —— 12 字节/行（稀有 9001 / 普通 2001,2002）
            var rows = new List<(int ItemId, int Weight, int IsRare)>
            {
                (2001, 70, 0),
                (2002, 29, 0),
                (9001, 1, 1)
            };

            WriteTable(Path.Combine(directory, "TbGachaPool.bin"), rows.Count, (writer, index) =>
            {
                (int itemId, int weight, int isRare) = rows[index];
                writer.Write(itemId);
                writer.Write(weight);
                writer.Write(isRare);
            });
        }

        private static void WriteMergeItem(string directory)
        {
            // TbMergeItem: row{level, itemId, sellGold} —— 12 字节/行，level 必须从 1 连续
            var rows = new List<(int Level, int ItemId, int SellGold)>
            {
                (1, 3001, 10),
                (2, 3002, 30),
                (3, 3003, 70),
                (4, 3004, 150),
                (5, 3005, 300)
            };

            WriteTable(Path.Combine(directory, "TbMergeItem.bin"), rows.Count, (writer, index) =>
            {
                (int level, int itemId, int sellGold) = rows[index];
                writer.Write(level);
                writer.Write(itemId);
                writer.Write(sellGold);
            });
        }

        private static void WriteLocalization(string directory)
        {
            // TbLocalization（变长表）：[int32 keyCount] + keyCount × {[int32 keyLen]key UTF8
            //   + [int32 langCount] + langCount × {[int32 lang][int32 textLen]text UTF8}}
            var entries = new List<(string Key, int[] Languages, string[] Texts)>
            {
                ("ui.play", new[] { 0, 1, 2 }, new[] { "开始游戏", "Play", "ゲーム開始" }),
                ("ui.signin", new[] { 0, 1, 2 }, new[] { "签到", "Sign In", "ログイン" }),
                ("ui.shop", new[] { 0, 1 }, new[] { "商店", "Shop" })
            };

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                (string key, int[] languages, string[] texts) = entries[i];
                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                writer.Write(keyBytes.Length);
                writer.Write(keyBytes);

                writer.Write(languages.Length);
                for (int j = 0; j < languages.Length; j++)
                {
                    byte[] textBytes = Encoding.UTF8.GetBytes(texts[j]);
                    writer.Write(languages[j]);
                    writer.Write(textBytes.Length);
                    writer.Write(textBytes);
                }
            }

            writer.Flush();
            File.WriteAllBytes(Path.Combine(directory, "TbLocalization.bin"), stream.ToArray());
            Console.WriteLine("  TbLocalization.bin  " + stream.Length + " 字节, " + entries.Count + " 行");
        }

        private static void WriteTable(string path, int rowCount, Action<BinaryWriter, int> writeRow)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(rowCount);
            for (int i = 0; i < rowCount; i++)
            {
                writeRow(writer, i);
            }

            writer.Flush();
            File.WriteAllBytes(path, stream.ToArray());
            Console.WriteLine("  " + Path.GetFileName(path) + "  " + stream.Length + " 字节, " + rowCount + " 行");
        }
    }
}
