using System;
using System.Collections.Generic;
using System.IO;

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
