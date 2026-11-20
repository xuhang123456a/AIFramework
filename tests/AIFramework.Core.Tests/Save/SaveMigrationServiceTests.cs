using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Save;
using Xunit;

namespace AIFramework.Core.Tests.Save
{
    /// <summary>内存存储 + 写失败注入：只测迁移逻辑，不碰文件系统。</summary>
    internal sealed class MigrationStorage : IStoragePort
    {
        private readonly Dictionary<string, byte[]> _items = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>迁移期间发生的写次数（夹具预置走 <see cref="Put"/>，不计入）。</summary>
        public int WriteCount { get; private set; }

        /// <summary>迁移期间发生的删次数。</summary>
        public int DeleteCount { get; private set; }

        /// <summary>被写过的 key（按发生顺序），用于验证"备份先于目标写"。</summary>
        public List<string> WrittenKeys { get; } = new List<string>();

        /// <summary>写这个 key 时抛异常（模拟磁盘满/权限/云端半截响应）；<c>null</c> = 不注入。</summary>
        public string? ThrowOnWriteKey { get; set; }

        /// <summary>读这个 key 时抛异常（模拟存储端口违约：契约要求用 <c>null</c> 表达"没有"）；<c>null</c> = 不注入。</summary>
        public string? ThrowOnReadKey { get; set; }

        public bool Exists(string key)
        {
            return _items.ContainsKey(key);
        }

        public byte[]? Read(string key)
        {
            if (ThrowOnReadKey != null && string.Equals(key, ThrowOnReadKey, StringComparison.Ordinal))
            {
                throw new IOException("simulated read failure: " + key);
            }

            return _items.TryGetValue(key, out byte[]? value) ? (byte[])value.Clone() : null;
        }

        public void Write(string key, byte[] data)
        {
            WriteCount++;
            WrittenKeys.Add(key);

            if (ThrowOnWriteKey != null && string.Equals(key, ThrowOnWriteKey, StringComparison.Ordinal))
            {
                throw new IOException("simulated write failure: " + key);
            }

            _items[key] = (byte[])data.Clone();
        }

        public void Delete(string key)
        {
            DeleteCount++;
            _items.Remove(key);
        }

        public string[] ListKeys()
        {
            var keys = new List<string>(_items.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys.ToArray();
        }

        /// <summary>夹具预置：直接落字节，不动计数器。</summary>
        internal void Put(string key, byte[] data)
        {
            _items[key] = (byte[])data.Clone();
        }

        /// <summary>取原始字节（不存在会抛 KeyNotFoundException，用于"必须先存在"的断言）。</summary>
        internal byte[] Raw(string key)
        {
            return _items[key];
        }

        internal bool Has(string key)
        {
            return _items.ContainsKey(key);
        }

        /// <summary>测试钩子：直接篡改磁盘内容，模拟坏档。</summary>
        internal void Tamper(string key, Action<byte[]> mutate)
        {
            mutate(_items[key]);
        }
    }

    /// <summary>
    /// 可注入的假时钟：迁移只把时间用于诊断盖章（<see cref="SaveMigrationService.LastMigrationAtUnixSeconds"/>），
    /// 因此这里只需能设定绝对时刻、并能模拟"时钟违约"。
    /// </summary>
    internal sealed class MigrationTime : ITimeProvider
    {
        private DateTime _utcNow = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>为 <c>true</c> 时读 <see cref="UtcNow"/> 抛异常（模拟平台时钟不可用）。</summary>
        public bool ThrowOnUtcNow { get; set; }

        public double ElapsedSeconds { get; set; }

        public DateTime UtcNow
        {
            get
            {
                if (ThrowOnUtcNow)
                {
                    throw new InvalidOperationException("clock unavailable");
                }

                return _utcNow;
            }
        }

        /// <summary>设定当前 UTC 时刻。</summary>
        public void SetUtcNow(DateTime utcNow)
        {
            _utcNow = utcNow;
        }
    }

    /// <summary>
    /// 游客档 → 正式账号槽位的迁移（T7-6 存档迁移）。
    /// <para>
    /// <b>这些用例锁的是什么</b>：不是"能写进去"，而是三条数据安全不变量——
    /// (1) 目标坏档绝不被覆盖；(2) 写失败绝不丢游客档；(3) 重复请求幂等且不产生第二份备份。
    /// 另外四个合并策略的裁决口径、条目上限、备份槽位格式也逐条钉死。
    /// </para>
    /// <para>
    /// <b>夹具为什么经真实 <see cref="SaveService"/> 造字节</b>：包络（magic / 长度 / CRC32 / schema 版本）只有一处定义，
    /// 测试若手搓字节就等于把格式复制进测试，格式一改测试照样绿。这里只有"坏档"才手工篡改字节。
    /// </para>
    /// </summary>
    public sealed class SaveMigrationServiceTests
    {
        private const string GuestSlot = "guest-1";
        private const string TargetSlot = "acc-42";
        private const string GuestKey = "save.guest-1";
        private const string TargetKey = "save.acc-42";
        private const string GuestBackupKey = "save.guest-1.guest";

        /// <summary>2026-11-20T12:00:00Z 的 Unix 秒（与既有云同步用例同一基准）。</summary>
        private const long FixedUnixSeconds = 1795176000L;

        // ============================================================== 复制分支（目标不存在）

        [Fact]
        public void TargetMissing_CopiesGuestPayloadAndConsumesGuestSlot()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100), Row(2001, 2, 200)));

            // 目标不存在时策略不参与裁决：没有可冲突的对象，照搬游客档即可。
            SaveMigrationResult result = service.MigrateGuestToAccount(Request(policy: SaveMergePolicy.PreferRemote));

            Assert.True(result.Success);
            Assert.Equal(TargetSlot, result.TargetSlotKey);
            Assert.Empty(result.BackupSlotKey);
            Assert.Equal(2, result.MergedEntries);
            Assert.Empty(result.Reason);

            Assert.False(storage.Has(GuestKey));

            InventorySaveEntry[] target = ReadTargetEntries(storage, TargetKey);
            Assert.Equal(2, target.Length);
            Assert.Equal(1001, target[0].ItemId);
            Assert.Equal(5, target[0].Count);
            Assert.Equal(100, target[0].UpdatedTicks);
            Assert.Equal(2001, target[1].ItemId);
            Assert.Equal(2, target[1].Count);
            Assert.Equal(200, target[1].UpdatedTicks);
        }

        [Fact]
        public void TargetMissing_EmptyGuestPayload_IsCopiedAsEmptyInventory()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);

            // SaveService 允许写空负载（全新档还没有任何条目）：迁移不能把"空背包"当坏档拒绝。
            Seed(storage, GuestKey, SaveService.CurrentVersion, Array.Empty<byte>());

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            Assert.Equal(0, result.MergedEntries);
            Assert.Empty(ReadTargetPayload(storage, TargetKey));
            Assert.Empty(ReadTargetEntries(storage, TargetKey));
            Assert.False(storage.Has(GuestKey));
        }

        [Fact]
        public void GuestMissing_FailsAndTouchesNothing()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 1, 10)));
            byte[] targetBefore = storage.Raw(TargetKey);

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonGuestSaveMissing, result.Reason);
            Assert.Equal(0, result.MergedEntries);
            Assert.Empty(result.BackupSlotKey);

            Assert.Equal(0, storage.WriteCount);
            Assert.Equal(0, storage.DeleteCount);
            Assert.Equal(targetBefore, storage.Raw(TargetKey));
        }

        // ============================================================== 坏档

        [Fact]
        public void TargetCorruptCrc_IsNeverOverwritten()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 50)));

            // 篡改目标负载最后一个字节：长度仍合法，CRC 不再匹配 → 坏档。
            storage.Tamper(TargetKey, bytes => bytes[bytes.Length - 1] ^= 0xFF);
            byte[] corruptBytes = storage.Raw(TargetKey);

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(policy: SaveMergePolicy.PreferLocal));

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonTargetSaveCorrupt, result.Reason);

            // 坏档一个字节都不许动（人工恢复的唯一线索），游客档也必须原地保留。
            Assert.Equal(corruptBytes, storage.Raw(TargetKey));
            Assert.True(storage.Has(GuestKey));
            Assert.Equal(0, storage.WriteCount);
            Assert.Equal(0, storage.DeleteCount);
        }

        [Fact]
        public void TargetFutureSchema_IsTreatedAsCorrupt()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));

            // 更高 schema = 未来版本档：旧客户端不许半解析，更不许覆盖。
            Seed(storage, TargetKey, SaveService.CurrentVersion + 1, PayloadOf(Row(2001, 2, 50)));
            byte[] targetBefore = storage.Raw(TargetKey);

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonTargetSaveCorrupt, result.Reason);
            Assert.Equal(targetBefore, storage.Raw(TargetKey));
            Assert.Equal(0, storage.WriteCount);
        }

        [Fact]
        public void GuestCorruptCrc_FailsAndLeavesTargetAlone()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 50)));

            storage.Tamper(GuestKey, bytes => bytes[bytes.Length - 1] ^= 0xFF);
            byte[] guestBytes = storage.Raw(GuestKey);
            byte[] targetBytes = storage.Raw(TargetKey);

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonGuestSaveCorrupt, result.Reason);

            // 坏掉的游客档也原样留着（可能还能人工修），目标一个字节不动。
            Assert.Equal(guestBytes, storage.Raw(GuestKey));
            Assert.Equal(targetBytes, storage.Raw(TargetKey));
            Assert.Equal(0, storage.WriteCount);
            Assert.Equal(0, storage.DeleteCount);
        }

        [Fact]
        public void GuestFutureSchema_IsRejected()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion + 1, PayloadOf(Row(1001, 5, 100)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonGuestSaveCorrupt, result.Reason);
            Assert.False(storage.Has(TargetKey));
            Assert.Equal(0, storage.WriteCount);
        }

        // ============================================================== 四个策略

        [Fact]
        public void PreferLocal_OverwritesTargetWithGuestContent()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 1, 900), Row(9001, 3, 800)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(SaveMergePolicy.PreferLocal));

            Assert.True(result.Success);
            Assert.Equal(1, result.MergedEntries);

            // 整档覆盖（不逐项）：目标原有但游客没有的 9001 一并消失——这就是 PreferLocal 的语义。
            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(1001, entry.ItemId);
            Assert.Equal(5, entry.Count);
            Assert.Equal(100, entry.UpdatedTicks);
            Assert.False(storage.Has(GuestKey));
        }

        [Fact]
        public void PreferRemote_KeepsTargetAndDropsGuestContent()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 900)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 100)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(SaveMergePolicy.PreferRemote));

            Assert.True(result.Success);
            Assert.Equal(1, result.MergedEntries);

            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(2001, entry.ItemId);
            Assert.Equal(2, entry.Count);

            // 游客内容被策略丢弃，但槽位仍然失效（否则同一请求会反复重放）。
            Assert.False(storage.Has(GuestKey));
        }

        [Fact]
        public void NewerWins_GuestIsNewer_WritesGuestContent()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 500)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 400)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(SaveMergePolicy.NewerWins));

            Assert.True(result.Success);
            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(1001, entry.ItemId);
            Assert.Equal(500, entry.UpdatedTicks);
        }

        [Fact]
        public void NewerWins_TargetIsNewer_KeepsTargetContent()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 900)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(SaveMergePolicy.NewerWins));

            Assert.True(result.Success);
            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(2001, entry.ItemId);
            Assert.Equal(900, entry.UpdatedTicks);
        }

        [Fact]
        public void NewerWins_SameTimestamp_TakesLocal()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 700)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 700)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(SaveMergePolicy.NewerWins));

            Assert.True(result.Success);

            // 同刻取本地：否则两台设备在同一秒互相覆盖会来回横跳。
            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(1001, entry.ItemId);
        }

        [Fact]
        public void NewerWins_BothTimestampsUnknown_TakesLocal()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 0)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 0)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(SaveMergePolicy.NewerWins));

            Assert.True(result.Success);
            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(1001, entry.ItemId);
        }

        [Fact]
        public void UnknownPolicyValue_FallsBackToPreferLocal()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 900)));

            var request = new SaveMigrationRequest(GuestSlot, TargetSlot, (SaveMergePolicy)200, keepGuestBackup: false);
            SaveMigrationResult result = service.MigrateGuestToAccount(request);

            Assert.True(result.Success);
            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(1001, entry.ItemId);
        }

        [Fact]
        public void PolicyDefaultValue_IsPreferLocal()
        {
            // "空策略"= default(SaveMergePolicy)：必须是 0 = 本地优先（宁可多覆盖一次目标，也不静默丢游客进度）。
            Assert.Equal(SaveMergePolicy.PreferLocal, default(SaveMergePolicy));
            Assert.Equal(SaveMergePolicy.PreferLocal, new SaveMigrationRequest(GuestSlot, TargetSlot, default, false).Policy);
        }

        // ============================================================== UnionBest

        [Fact]
        public void UnionBest_TakesMaxCountAndMaxTicksPerItem()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100), Row(2001, 1, 10)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 3, 900), Row(3001, 4, 20)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            Assert.Equal(3, result.MergedEntries);

            // 同 itemId：count 取大（5 > 3）、updatedTicks 取大（900 > 100）；两侧独有的 itemId 取并集。
            InventorySaveEntry[] target = ReadTargetEntries(storage, TargetKey);
            Assert.Equal(3, target.Length);
            Assert.Equal(1001, target[0].ItemId);
            Assert.Equal(5, target[0].Count);
            Assert.Equal(900, target[0].UpdatedTicks);
            Assert.Equal(2001, target[1].ItemId);
            Assert.Equal(1, target[1].Count);
            Assert.Equal(10, target[1].UpdatedTicks);
            Assert.Equal(3001, target[2].ItemId);
            Assert.Equal(4, target[2].Count);
            Assert.Equal(20, target[2].UpdatedTicks);
        }

        [Fact]
        public void UnionBest_TakesMaxUpdatedTicksEvenWhenCountsAreEqual()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 900)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            InventorySaveEntry entry = Assert.Single(ReadTargetEntries(storage, TargetKey));
            Assert.Equal(5, entry.Count);
            Assert.Equal(900, entry.UpdatedTicks);
        }

        [Fact]
        public void UnionBest_OutputIsSortedByItemId()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);

            // 输入故意乱序：游客档降序、目标档插入中间，输出必须升序（可复现的字节）。
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(9001, 1, 10), Row(1001, 1, 10)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(5001, 1, 10)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            InventorySaveEntry[] target = ReadTargetEntries(storage, TargetKey);
            Assert.Equal(3, target.Length);
            Assert.Equal(1001, target[0].ItemId);
            Assert.Equal(5001, target[1].ItemId);
            Assert.Equal(9001, target[2].ItemId);
        }

        [Fact]
        public void UnionBest_DuplicateItemIdInOneSave_CollapsesToMax()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);

            // 编码器允许把同一 itemId 写成两行（历史数据/坏商家都可能有），合并后必须收敛成一行。
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100), Row(1001, 2, 300)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 1, 10)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            Assert.Equal(2, result.MergedEntries);

            InventorySaveEntry[] target = ReadTargetEntries(storage, TargetKey);
            Assert.Equal(2, target.Length);
            Assert.Equal(1001, target[0].ItemId);
            Assert.Equal(5, target[0].Count);
            Assert.Equal(300, target[0].UpdatedTicks);
        }

        [Fact]
        public void UnionBest_UndecodableTargetPayload_FailsWithoutTouchingSlots()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));

            // 包络合法但负载不是背包编码：能整档搬运，却无法做并集。
            Seed(storage, TargetKey, SaveService.CurrentVersion, Encoding.UTF8.GetBytes("not-an-inventory"));
            byte[] targetBefore = storage.Raw(TargetKey);

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonMergeFailed, result.Reason);
            Assert.Equal(targetBefore, storage.Raw(TargetKey));
            Assert.True(storage.Has(GuestKey));
            Assert.Equal(0, storage.WriteCount);
            Assert.Equal(0, storage.DeleteCount);
        }

        [Fact]
        public void UnionBest_ExceedingEntryLimit_FailsWithoutWriting()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(DistinctRows(1, 150)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(DistinctRows(1001, 150)));

            // 并集 300 条 > 200：拒绝迁移，两侧都保持原样（游客档还在，可以人工决定怎么处理）。
            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonTooManyEntries, result.Reason);
            Assert.Equal(0, result.MergedEntries);
            Assert.True(storage.Has(GuestKey));
            Assert.Equal(0, storage.WriteCount);
            Assert.Equal(0, storage.DeleteCount);
        }

        [Fact]
        public void Copy_ExceedingEntryLimit_Fails()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(DistinctRows(1, SaveMigrationService.MaxMergedEntries + 1)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonTooManyEntries, result.Reason);
            Assert.False(storage.Has(TargetKey));
            Assert.True(storage.Has(GuestKey));
            Assert.Equal(0, storage.WriteCount);
        }

        [Fact]
        public void UnionBest_EmptyTargetPayload_IsTreatedAsEmptyInventory()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100), Row(2001, 2, 200)));

            // 目标存在但负载 0 字节（全新账号档）：等价于空背包，应照常并集，而不是"合并不了"。
            Seed(storage, TargetKey, SaveService.CurrentVersion, Array.Empty<byte>());

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            Assert.Equal(2, result.MergedEntries);
            Assert.Equal(2, ReadTargetEntries(storage, TargetKey).Length);
        }

        // ============================================================== 备份与幂等

        [Fact]
        public void KeepGuestBackup_CopiesOriginalBytesBeforeGuestIsRemoved()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            byte[] guestBytes = storage.Raw(GuestKey);

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(keepGuestBackup: true));

            Assert.True(result.Success);
            Assert.Equal(GuestBackupKey, result.BackupSlotKey);

            // 备份是原始包络字节（连 schema 版本一起留），不是迁移后的重编码结果。
            Assert.Equal(guestBytes, storage.Raw(GuestBackupKey));
            Assert.False(storage.Has(GuestKey));

            // 备份先于目标写：出现问题的顺序在日志里必须能还原。
            Assert.Equal(2, storage.WrittenKeys.Count);
            Assert.Equal(GuestBackupKey, storage.WrittenKeys[0]);
            Assert.Equal(TargetKey, storage.WrittenKeys[1]);
        }

        [Fact]
        public void KeepGuestBackupFalse_LeavesNoBackupSlot()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            Assert.Empty(result.BackupSlotKey);
            Assert.False(storage.Has(GuestBackupKey));
            Assert.False(storage.Has(GuestKey));
            Assert.Equal(1, storage.WriteCount);
        }

        [Fact]
        public void SlotKeys_FollowSaveServicePrefixFormat()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage, out _, "profile");
            Seed(storage, "profile.g1", SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));

            // 逻辑槽位 → 物理 key 的拼法与 SaveService.Key 完全一致（<prefix>.<slot>）。
            Assert.Equal("profile.g1", service.Key("g1"));
            Assert.Equal("profile.g1.guest", service.BackupKey("g1"));

            SaveMigrationResult result = service.MigrateGuestToAccount(
                new SaveMigrationRequest("g1", "a1", SaveMergePolicy.UnionBest, keepGuestBackup: true));

            Assert.True(result.Success);
            Assert.Equal("a1", result.TargetSlotKey);              // 目标是逻辑槽位名
            Assert.Equal("profile.g1.guest", result.BackupSlotKey); // 备份已是物理存储 key
            Assert.True(storage.Has("profile.a1"));
            Assert.True(storage.Has("profile.g1.guest"));
            Assert.False(storage.Has("profile.g1"));
        }

        [Fact]
        public void RepeatedMigration_IsIdempotent_NoSecondWriteNoSecondBackup()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));

            SaveMigrationResult first = service.MigrateGuestToAccount(Request(keepGuestBackup: true));
            Assert.True(first.Success);

            byte[] targetAfterFirst = storage.Raw(TargetKey);
            byte[] backupAfterFirst = storage.Raw(GuestBackupKey);
            int writesAfterFirst = storage.WriteCount;
            int deletesAfterFirst = storage.DeleteCount;

            SaveMigrationResult second = service.MigrateGuestToAccount(Request(keepGuestBackup: true));

            Assert.False(second.Success);
            Assert.Equal(SaveMigrationService.ReasonGuestSaveMissing, second.Reason);
            Assert.Equal(0, second.MergedEntries);
            Assert.Empty(second.BackupSlotKey);

            Assert.Equal(writesAfterFirst, storage.WriteCount);
            Assert.Equal(deletesAfterFirst, storage.DeleteCount);
            Assert.Equal(targetAfterFirst, storage.Raw(TargetKey));
            Assert.Equal(backupAfterFirst, storage.Raw(GuestBackupKey));
        }

        // ============================================================== 写失败

        [Fact]
        public void TargetWriteFailure_KeepsGuestSave()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 50)));
            byte[] targetBefore = storage.Raw(TargetKey);

            storage.ThrowOnWriteKey = TargetKey;

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonWriteFailed, result.Reason);
            Assert.Equal(0, result.MergedEntries);
            Assert.Empty(result.BackupSlotKey);

            // 写失败绝不能被翻译成"游客进度作废"。
            Assert.True(storage.Has(GuestKey));
            Assert.Equal(targetBefore, storage.Raw(TargetKey));
            Assert.Equal(0, storage.DeleteCount);
        }

        [Fact]
        public void BackupWriteFailure_AbortsBeforeTargetIsWritten()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));

            storage.ThrowOnWriteKey = GuestBackupKey;

            SaveMigrationResult result = service.MigrateGuestToAccount(Request(keepGuestBackup: true));

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonWriteFailed, result.Reason);

            // 备份写失败发生在目标写之前：目标槽位连"尝试写过"都不该出现。
            Assert.False(storage.Has(TargetKey));
            Assert.False(storage.Has(GuestBackupKey));
            Assert.True(storage.Has(GuestKey));
            Assert.Equal(0, storage.DeleteCount);
        }

        [Fact]
        public void StorageReadViolation_PropagatesInsteadOfMasqueradingAsCorrupt()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));

            // IStoragePort 的契约是"键不存在返回 null"：实现自己抛异常属于端口违约，
            // 这里刻意不吞（把 IO 错误伪装成"坏档"会让客服按错误路径排查），异常边界由用例钉住。
            storage.ThrowOnReadKey = GuestKey;

            Assert.Throws<IOException>(() => service.MigrateGuestToAccount(Request()));

            Assert.True(storage.Has(GuestKey));
            Assert.Equal(0, storage.WriteCount);
        }

        // ============================================================== 请求校验（null / 同槽位）

        [Fact]
        public void NullSlotKeys_AreRejectedWithoutTouchingStorage()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 1, 10)));

            SaveMigrationResult nullGuest = service.MigrateGuestToAccount(
                new SaveMigrationRequest(null!, TargetSlot, SaveMergePolicy.UnionBest, keepGuestBackup: false));
            SaveMigrationResult nullTarget = service.MigrateGuestToAccount(
                new SaveMigrationRequest(GuestSlot, null!, SaveMergePolicy.UnionBest, keepGuestBackup: false));
            SaveMigrationResult emptyBoth = service.MigrateGuestToAccount(
                new SaveMigrationRequest(string.Empty, string.Empty, SaveMergePolicy.UnionBest, keepGuestBackup: false));

            Assert.False(nullGuest.Success);
            Assert.Equal(SaveMigrationService.ReasonInvalidSlotKey, nullGuest.Reason);
            Assert.False(nullTarget.Success);
            Assert.Equal(SaveMigrationService.ReasonInvalidSlotKey, nullTarget.Reason);
            Assert.False(emptyBoth.Success);
            Assert.Equal(SaveMigrationService.ReasonInvalidSlotKey, emptyBoth.Reason);

            Assert.Equal(0, storage.WriteCount);
            Assert.Equal(0, storage.DeleteCount);
        }

        [Fact]
        public void Request_NormalizesNullSlotKeys()
        {
            var request = new SaveMigrationRequest(null!, null!, SaveMergePolicy.UnionBest, keepGuestBackup: true);

            Assert.Equal(string.Empty, request.GuestSlotKey);
            Assert.Equal(string.Empty, request.TargetSlotKey);
            Assert.Equal(SaveMergePolicy.UnionBest, request.Policy);
            Assert.True(request.KeepGuestBackup);
        }

        [Fact]
        public void SameSlotForGuestAndTarget_IsRejectedWithoutDeletingTheResult()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);

            // 游客槽位与目标槽位相同：若不挡掉，合并结果会写回该 key，紧接着"清理游客档"就把它删了。
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            byte[] before = storage.Raw(TargetKey);

            SaveMigrationResult result = service.MigrateGuestToAccount(
                new SaveMigrationRequest(TargetSlot, TargetSlot, SaveMergePolicy.UnionBest, keepGuestBackup: false));

            Assert.False(result.Success);
            Assert.Equal(SaveMigrationService.ReasonSameSlot, result.Reason);
            Assert.Equal(before, storage.Raw(TargetKey));
            Assert.Equal(0, storage.WriteCount);
            Assert.Equal(0, storage.DeleteCount);
        }

        // ============================================================== 结果结构体的归一化

        [Fact]
        public void Result_OnFailure_NormalizesEntriesAndNulls()
        {
            var result = new SaveMigrationResult(false, null!, GuestBackupKey, 12, null!);

            // "失败却报告合并了 12 条 / 备份在某处"会让调用方在日志里追一个不存在的成功；
            // 归一化由构造器强制（不是"实现方记得传对"的君子协定）。
            Assert.False(result.Success);
            Assert.Equal(0, result.MergedEntries);
            Assert.Equal(string.Empty, result.TargetSlotKey);
            Assert.Equal(string.Empty, result.BackupSlotKey);
            Assert.Equal(string.Empty, result.Reason);
        }

        [Fact]
        public void Result_OnSuccess_ClampsNegativeEntries()
        {
            var result = new SaveMigrationResult(true, TargetSlot, GuestBackupKey, -3, "diagnostic");

            Assert.True(result.Success);
            Assert.Equal(0, result.MergedEntries);
            Assert.Equal(TargetSlot, result.TargetSlotKey);
            Assert.Equal(GuestBackupKey, result.BackupSlotKey);
            Assert.Equal("diagnostic", result.Reason);
        }

        // ============================================================== 旧档 / 时间来源

        [Fact]
        public void LegacyV1GuestSave_IsMigratedThenMerged()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage);

            // v1 负载没有 updatedTicks：迁移链补 0（未知），合并时不能压掉目标档已知的更大时间戳。
            var v1Entries = new[]
            {
                new InventoryEntrySnapshot(1001, 5),
                new InventoryEntrySnapshot(9001, 1)
            };
            Seed(storage, GuestKey, 1, InventorySaveCodec.Encode(v1Entries));
            Seed(storage, TargetKey, SaveService.CurrentVersion, PayloadOf(Row(2001, 2, 700)));

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            Assert.Equal(3, result.MergedEntries);

            InventorySaveEntry[] target = ReadTargetEntries(storage, TargetKey);
            Assert.Equal(3, target.Length);
            Assert.Equal(1001, target[0].ItemId);
            Assert.Equal(0, target[0].UpdatedTicks);
            Assert.Equal(2001, target[1].ItemId);
            Assert.Equal(700, target[1].UpdatedTicks);
            Assert.Equal(9001, target[2].ItemId);
        }

        [Fact]
        public void Migration_StampsDiagnosticTimeFromTimeProvider()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage, out MigrationTime time);
            time.SetUtcNow(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));

            // 即使这次迁移失败，也要留下"这个请求是几点来的"（只用于诊断，不参与裁决）。
            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.False(result.Success);
            Assert.Equal(FixedUnixSeconds, service.LastMigrationAtUnixSeconds);
        }

        [Fact]
        public void Migration_ClockFailure_DoesNotFailTheMigration()
        {
            SaveMigrationService service = CreateService(out MigrationStorage storage, out MigrationTime time);
            Seed(storage, GuestKey, SaveService.CurrentVersion, PayloadOf(Row(1001, 5, 100)));
            time.ThrowOnUtcNow = true;

            SaveMigrationResult result = service.MigrateGuestToAccount(Request());

            Assert.True(result.Success);
            Assert.Equal(-1L, service.LastMigrationAtUnixSeconds);
        }

        [Fact]
        public void NewService_HasNoMigrationStamp()
        {
            var service = new SaveMigrationService(new MigrationStorage(), new MigrationTime());

            Assert.Equal(-1L, service.LastMigrationAtUnixSeconds);
        }

        // ============================================================== 夹具

        private static SaveMigrationService CreateService(out MigrationStorage storage)
        {
            return CreateService(out storage, out _);
        }

        private static SaveMigrationService CreateService(out MigrationStorage storage, out MigrationTime time)
        {
            return CreateService(out storage, out time, "save");
        }

        private static SaveMigrationService CreateService(out MigrationStorage storage, out MigrationTime time, string prefix)
        {
            storage = new MigrationStorage();
            time = new MigrationTime();
            return new SaveMigrationService(storage, time, prefix);
        }

        private static SaveMigrationRequest Request(
            SaveMergePolicy policy = SaveMergePolicy.UnionBest,
            bool keepGuestBackup = false,
            string guestSlot = GuestSlot,
            string targetSlot = TargetSlot)
        {
            return new SaveMigrationRequest(guestSlot, targetSlot, policy, keepGuestBackup);
        }

        /// <summary>用<b>真实</b> <see cref="SaveService"/> 造包络字节（格式只有一处定义），再直接落进存储夹具。</summary>
        private static void Seed(MigrationStorage storage, string key, int schemaVersion, byte[] payload)
        {
            var scratch = new MigrationStorage();
            var writer = new SaveService(scratch, "scratch");
            writer.Save(0, new SaveEnvelope(schemaVersion, payload));
            storage.Put(key, scratch.Raw(writer.Key(0)));
        }

        /// <summary>用<b>真实</b> <see cref="SaveService"/> 读回目标槽位（含 CRC 与迁移链校验）。</summary>
        private static byte[] ReadTargetPayload(MigrationStorage storage, string key)
        {
            var scratch = new MigrationStorage();
            var reader = new SaveService(scratch, "scratch");
            scratch.Put(reader.Key(0), storage.Raw(key));

            Assert.True(reader.TryLoad(0, out SaveEnvelope envelope), "目标槽位必须能读回合法存档");
            return envelope.Payload;
        }

        private static InventorySaveEntry[] ReadTargetEntries(MigrationStorage storage, string key)
        {
            byte[] payload = ReadTargetPayload(storage, key);
            if (payload.Length == 0)
            {
                return Array.Empty<InventorySaveEntry>();
            }

            Assert.True(
                InventorySaveCodec.TryDecodeAuto(payload, out InventorySaveEntry[] entries),
                "目标负载必须是合法背包编码");
            return entries;
        }

        private static InventorySaveEntry Row(int itemId, int count, long updatedTicks)
        {
            return new InventorySaveEntry(itemId, count, updatedTicks);
        }

        private static byte[] PayloadOf(params InventorySaveEntry[] entries)
        {
            return InventorySaveCodec.EncodeV2(entries);
        }

        /// <summary>从 <paramref name="firstItemId"/> 起生成 <paramref name="count"/> 条互不相同的条目（条目上限用例）。</summary>
        private static InventorySaveEntry[] DistinctRows(int firstItemId, int count)
        {
            var rows = new InventorySaveEntry[count];
            for (int i = 0; i < count; i++)
            {
                rows[i] = new InventorySaveEntry(firstItemId + i, 1, 1);
            }

            return rows;
        }
    }
}
