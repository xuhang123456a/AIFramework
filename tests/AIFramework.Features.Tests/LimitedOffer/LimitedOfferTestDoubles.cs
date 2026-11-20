using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.LimitedOffer;
using AIFramework.Features.Tests.Fakes;

namespace AIFramework.Features.Tests.LimitedOffer
{
    /// <summary>一条礼包行的夹具定义（字段顺序与 <c>tables/TbLimitedOffer.json</c> 列顺序一致）。</summary>
    internal sealed class LimitedOfferRowSpec
    {
        internal LimitedOfferRowSpec(
            int offerId,
            string name,
            int itemId,
            int grantCount,
            int priceItemId,
            int originalPrice,
            int discountPercent,
            int startUnixSeconds,
            int endUnixSeconds,
            int dailyLimit,
            int totalLimit)
        {
            OfferId = offerId;
            Name = name;
            ItemId = itemId;
            GrantCount = grantCount;
            PriceItemId = priceItemId;
            OriginalPrice = originalPrice;
            DiscountPercent = discountPercent;
            StartUnixSeconds = startUnixSeconds;
            EndUnixSeconds = endUnixSeconds;
            DailyLimit = dailyLimit;
            TotalLimit = totalLimit;
        }

        internal int OfferId { get; }

        internal string Name { get; }

        internal int ItemId { get; }

        internal int GrantCount { get; }

        internal int PriceItemId { get; }

        internal int OriginalPrice { get; }

        internal int DiscountPercent { get; }

        internal int StartUnixSeconds { get; }

        internal int EndUnixSeconds { get; }

        internal int DailyLimit { get; }

        internal int TotalLimit { get; }
    }

    /// <summary>
    /// 限时礼包子表加载桩：严格按 <c>tools/tablegen</c> 的布局造字节
    /// （小端 <c>[int32 rowCount]</c> + 每行 <c>{ int32 offerId; [int32 len]+UTF-8 name; int32 × 9 }</c>），
    /// 只在本测试目录内使用，不改共享 <c>Fakes/</c>。
    /// </summary>
    internal sealed class LimitedOfferTableStub : IBinaryTableLoader
    {
        internal const string TableName = "TbLimitedOffer";

        private const int HeaderBytes = 4;

        /// <summary>单行定长部分：10 个 int32 字段 + name 的 4 字节长度前缀。</summary>
        private const int RowFixedBytes = 44;

        private readonly Dictionary<string, byte[]?> _tables;

        private LimitedOfferTableStub(Dictionary<string, byte[]?> tables)
        {
            _tables = tables;
        }

        /// <summary>一次都没成表的加载器：任何表都返回 <c>null</c>（表缺失场景）。</summary>
        internal static LimitedOfferTableStub WithoutTable()
        {
            return new LimitedOfferTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        }

        /// <summary>按给定行顺序造一张合法子表。</summary>
        internal static LimitedOfferTableStub WithRows(params LimitedOfferRowSpec[] rows)
        {
            return WithPayload(BuildPayload(rows));
        }

        /// <summary>直接挂一份字节（可用于空表 / 畸形字节 / 仓库真实 <c>.bin</c>）。</summary>
        internal static LimitedOfferTableStub WithPayload(byte[]? payload)
        {
            var tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            tables[TableName] = payload;
            return new LimitedOfferTableStub(tables);
        }

        internal static byte[] BuildPayload(LimitedOfferRowSpec[] rows)
        {
            return BuildPayload(rows, rows.Length);
        }

        /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于长度/截断反例）。</summary>
        internal static byte[] BuildPayload(LimitedOfferRowSpec[] rows, int declaredRowCount)
        {
            var bytes = new List<byte>(HeaderBytes + (rows.Length * RowFixedBytes));
            AppendInt32(bytes, declaredRowCount);
            for (int i = 0; i < rows.Length; i++)
            {
                LimitedOfferRowSpec row = rows[i];
                AppendInt32(bytes, row.OfferId);
                byte[] name = Encoding.UTF8.GetBytes(row.Name);
                AppendInt32(bytes, name.Length);
                bytes.AddRange(name);
                AppendInt32(bytes, row.ItemId);
                AppendInt32(bytes, row.GrantCount);
                AppendInt32(bytes, row.PriceItemId);
                AppendInt32(bytes, row.OriginalPrice);
                AppendInt32(bytes, row.DiscountPercent);
                AppendInt32(bytes, row.StartUnixSeconds);
                AppendInt32(bytes, row.EndUnixSeconds);
                AppendInt32(bytes, row.DailyLimit);
                AppendInt32(bytes, row.TotalLimit);
            }

            return bytes.ToArray();
        }

        /// <summary>只写一串小端 int32（不写字符串体），用于手工构造"长度前缀本身畸形"的字节流。</summary>
        internal static byte[] BuildRaw(params int[] values)
        {
            var bytes = new List<byte>(values.Length * 4);
            for (int i = 0; i < values.Length; i++)
            {
                AppendInt32(bytes, values[i]);
            }

            return bytes.ToArray();
        }

        /// <summary>在合法字节尾部追加垃圾字节（模拟"声明行数与字节流不符"的尾部残留）。</summary>
        internal static byte[] AppendTrailingBytes(byte[] payload, int count)
        {
            var bytes = new List<byte>(payload.Length + count);
            bytes.AddRange(payload);
            for (int i = 0; i < count; i++)
            {
                bytes.Add(0x7F);
            }

            return bytes.ToArray();
        }

        public byte[]? Load(string tableName)
        {
            byte[]? payload;
            return _tables.TryGetValue(tableName, out payload) ? payload : null;
        }

        private static void AppendInt32(List<byte> target, int value)
        {
            target.Add((byte)(value & 0xFF));
            target.Add((byte)((value >> 8) & 0xFF));
            target.Add((byte)((value >> 16) & 0xFF));
            target.Add((byte)((value >> 24) & 0xFF));
        }
    }

    /// <summary>
    /// 装配：记录型总线（<see cref="RecordingBus"/>）+ 可快进时间源 + 切片
    /// <see cref="LimitedOfferHandler"/>。默认时间锚点固定，保证所有窗口/跨日断言可复现。
    /// <para>
    /// 总线里预注册了 <see cref="GrantItemCommand"/> 的空实现：真实 <c>CqeBus</c> 未注册处理器会抛异常，
    /// 而"发货由背包切片结算"不属于本切片的测试职责（断言只看 <see cref="RecordingBus"/> 的记录）。
    /// </para>
    /// </summary>
    internal sealed class LimitedOfferHarness
    {
        /// <summary>默认时间锚点：2026-11-20T08:00:00Z（UTC 日序号 20777 的 08:00，离跨日还有 16 小时）。</summary>
        internal static readonly DateTime DefaultNow = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private LimitedOfferHarness(RecordingBus bus, LimitedOfferHandler handler, FakeTimeProvider time)
        {
            Bus = bus;
            Handler = handler;
            Time = time;
        }

        internal RecordingBus Bus { get; }

        internal LimitedOfferHandler Handler { get; }

        internal FakeTimeProvider Time { get; }

        internal static LimitedOfferHarness Create(LimitedOfferTableStub loader)
        {
            return Create(loader, DefaultNow);
        }

        internal static LimitedOfferHarness Create(LimitedOfferTableStub loader, DateTime now)
        {
            var bus = new RecordingBus();
            bus.RegisterCommandHandler(new NoOpCommandHandler<GrantItemCommand>());
            var time = new FakeTimeProvider(now);
            var handler = new LimitedOfferHandler(bus, time, loader);
            handler.Register(bus);
            return new LimitedOfferHarness(bus, handler, time);
        }

        /// <summary>UTC 时刻 → Unix 秒（测试侧独立实现，避免"用被测代码算期望值"）。</summary>
        internal static long ToUnixSeconds(DateTime utc)
        {
            DateTime normalized = utc.Kind == DateTimeKind.Utc
                ? utc
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return (long)(normalized - UnixEpoch).TotalSeconds;
        }

        /// <summary>UTC 时刻 → int32 Unix 秒（表内时间列是 int32，2038 年前够用）。</summary>
        internal static int ToUnixSecondsInt(DateTime utc)
        {
            return (int)ToUnixSeconds(utc);
        }

        /// <summary>锚点偏移 <paramref name="seconds"/> 秒后的 int32 Unix 秒。</summary>
        internal static int OffsetSeconds(int seconds)
        {
            return ToUnixSecondsInt(DefaultNow.AddSeconds(seconds));
        }

        internal void AdvanceSeconds(double seconds)
        {
            Time.AdvanceSeconds(seconds);
        }

        internal void AdvanceDays(int days)
        {
            Time.AdvanceDays(days);
        }

        /// <summary>同步购买入口（能拿到逐次结果）。</summary>
        internal LimitedOfferPurchaseOutcome Purchase(int offerId)
        {
            return Handler.Purchase(offerId);
        }

        /// <summary>走真实总线发一条购买命令（验证命令注册链路）。</summary>
        internal void SendPurchase(int offerId)
        {
            Bus.Send(new LimitedOfferPurchaseCommand(offerId));
        }

        internal LimitedOfferSnapshot Query()
        {
            return Bus.Query<LimitedOfferQueryRequest, LimitedOfferSnapshot>(new LimitedOfferQueryRequest());
        }

        internal List<LimitedOfferPurchasedEvent> PurchasedEvents()
        {
            return Bus.PublishedOf<LimitedOfferPurchasedEvent>();
        }

        internal List<GrantItemCommand> GrantCommands()
        {
            return Bus.SentOf<GrantItemCommand>();
        }

        /// <summary>按 id 取快照里的礼包状态；找不到直接抛（说明快照漏行）。</summary>
        internal static LimitedOfferState StateOf(LimitedOfferSnapshot snapshot, int offerId)
        {
            for (int i = 0; i < snapshot.Offers.Length; i++)
            {
                if (snapshot.Offers[i].OfferId == offerId)
                {
                    return snapshot.Offers[i];
                }
            }

            throw new InvalidOperationException("快照里没有 offerId " + offerId + "。");
        }
    }
}
