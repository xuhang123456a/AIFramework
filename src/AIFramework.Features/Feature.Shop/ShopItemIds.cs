#nullable enable
namespace AIFramework.Features.Shop
{
    /// <summary>
    /// 本切片商品道具 ID 常量。<b>Phase 1 手写夹具</b>：与 <c>tools/fixtures</c> 生成的
    /// <c>TbShopItem</c> 行一一对应；Luban 接入后由 <c>TbItem</c> 提供，届时删除本类。
    /// <para>
    /// 货币侧一律复用 <c>MetaItemIds</c>（如 <c>MetaItemIds.Gold</c>），禁止在这里另写一份货币 ID。
    /// </para>
    /// </summary>
    public static class ShopItemIds
    {
        /// <summary>商品：生命药水（夹具价 100 金币）。</summary>
        public const int HealthPotion = 2001;

        /// <summary>商品：魔力药水（夹具价 250 金币）。</summary>
        public const int ManaPotion = 2002;

        /// <summary>商品：复活币（夹具价 500 金币）。</summary>
        public const int ReviveToken = 2003;
    }
}
