#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;
using AIFramework.Features.Merge;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Lobby
{
    /// <summary>
    /// 合成场景控制器：建宿主 → 注册 <see cref="MergeHandler"/> → 查询棋盘快照推给 <see cref="MergeHud"/>，
    /// 并把 HUD 的三路点击翻译成 CQE 命令后立刻刷新。
    /// <para>
    /// <b>为什么这里自己注册切片</b>：<see cref="AIFrameworkUnityHost"/> 只装配大厅的商店 / 任务 / 邮件 / 签到 / 本地化，
    /// 合成由本场景按需装配——一个场景只付它真正用到的切片的代价。
    /// 背包处理器（金币账本）已由宿主注册，这里<b>绝不再注册</b>（重复注册会直接抛异常）。
    /// </para>
    /// <para>
    /// <b>为什么用同一程序集里的 <c>UnityStreamingAssetsTableLoader</c></b>：宿主没暴露它内部的表加载端口，
    /// 而本类与宿主同属一个适配层程序集，直接复用同一实现，保证整场景只有一套
    /// "表名 → StreamingAssets/AIFrameworkDemo/*.bin" 约定。
    /// </para>
    /// <para>
    /// <b>为什么"重新开始"要重建宿主</b>：<c>CqeBus</c> 没有反注册接口（同一命令类型注册第二次直接抛异常），
    /// 要回到"空棋盘 + 初始金币"只能换一个全新的宿主与全新的处理器实例。
    /// </para>
    /// <para>
    /// <b>为什么"生成 / 合成"先查快照再发命令</b>：切片对非法请求一律静默拒绝（不抛异常、不发事件），
    /// 装配层必须自己先算出"第一个空格""第一对相邻同等级格"，否则玩家只会看到一颗点了没反应的按钮。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MergePanelController : MonoBehaviour
    {
        /// <summary>演示子表目录（与 <c>AIFrameworkUnityHost</c> 的约定一致，相对 StreamingAssets）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>生成器每次产出的物品等级（演示只用最基础的一级物品堆合成链）。</summary>
        private const int SpawnLevel = 1;

        /// <summary>合成面板皮肤（本控制器的视图）。</summary>
        [SerializeField] private MergeHud hud = null!;

        /// <summary>子表加载端口；重开时复用同一实例（本身无状态）。</summary>
        private IBinaryTableLoader? _tables;

        /// <summary>宿主（总线 / 时间源 / 日志 / 背包的唯一入口）；销毁与重开时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>宿主的消息总线；重开时随宿主一起换。</summary>
        private ICqeBus? _bus;

        /// <summary>合成切片处理器；子表缺失时为 <c>null</c>（面板保持占位文案，不崩）。</summary>
        private MergeHandler? _handler;

        private void Awake()
        {
            _tables = new UnityStreamingAssetsTableLoader(new UnityAssetLoader(), TableRelativeDirectory);

            // 先订阅再建面板：HUD 的按钮监听在它自己的 Start 里挂，但事件订阅越早越不会漏掉首帧点击。
            SubscribeHud();
            CreatePanel();
        }

        private void Start()
        {
            Refresh();
        }

        private void OnDestroy()
        {
            UnsubscribeHud();

            _handler = null;
            _bus = null;
            _host?.Dispose();
            _host = null;
        }

        /// <summary>
        /// 建宿主 + 处理器并注册；缺表 / 坏表只关掉本面板，错误必须能在 Console 里看到。
        /// 顺序是"先建处理器，全成功后才注册"——绝不把半装配的处理器挂上总线。
        /// </summary>
        private void CreatePanel()
        {
            IBinaryTableLoader? tables = _tables;
            if (tables == null)
            {
                return;
            }

            var host = new AIFrameworkUnityHost(tables);
            host.Initialize();
            _host = host;
            _bus = host.Bus;

            if (hud == null)
            {
                host.Log.Error("[Merge] 控制器未挂 MergeHud，合成面板不装配。");
                return;
            }

            try
            {
                var handler = new MergeHandler(host.Bus, tables);
                handler.Register(host.Bus);
                _handler = handler;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is InvalidDataException
                || exception is IOException
                || exception is ArgumentException)
            {
                // 缺表/坏表是部署问题，不该让整个场景停摆：只关掉本面板。
                _handler = null;
                host.Log.Error("[Merge] 合成切片装配失败（子表 TbMergeItem 缺失或损坏），面板已禁用：" + exception.Message);
            }
        }

        /// <summary>
        /// 按当前快照刷新 HUD：棋盘来自切片查询，金币来自背包账本（合成奖励由切片发 <c>GrantItemCommand</c> 落账）。
        /// 面板未装配成功（缺表）时什么都不做，保持占位文案。
        /// </summary>
        private void Refresh()
        {
            ICqeBus? bus = _bus;
            MergeHandler? handler = _handler;
            MergeHud? view = hud;
            if (bus == null || handler == null || view == null)
            {
                return;
            }

            view.Render(QueryGrid(bus), handler.MergeCount, QueryGold(bus));
        }

        /// <summary>查一次棋盘快照（处理器每次返回防御性副本，本地读的快照不会被后续命令改写）。</summary>
        private static MergeGridSnapshot QueryGrid(ICqeBus bus)
        {
            return bus.Query<MergeGridQueryRequest, MergeGridSnapshot>(new MergeGridQueryRequest());
        }

        /// <summary>查一次金币数量；背包处理器由宿主注册，缺注册会在装配期就抛异常。</summary>
        private static int QueryGold(ICqeBus bus)
        {
            InventorySnapshot gold = bus.Query<InventoryQueryRequest, InventorySnapshot>(
                new InventoryQueryRequest(MetaItemIds.Gold));
            return gold.Count;
        }

        /// <summary>把 HUD 的三路点击接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            MergeHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnSpawnClicked += HandleSpawnClicked;
            view.OnMergeClicked += HandleMergeClicked;
            view.OnRestartClicked += HandleRestartClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击发两条命令）。</summary>
        private void UnsubscribeHud()
        {
            MergeHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnSpawnClicked -= HandleSpawnClicked;
            view.OnMergeClicked -= HandleMergeClicked;
            view.OnRestartClicked -= HandleRestartClicked;
        }

        /// <summary>点"生成"：在第一个空格落一件 1 级物品；棋盘已满就什么都不做（切片同样会静默拒绝）。</summary>
        private void HandleSpawnClicked()
        {
            ICqeBus? bus = _bus;
            MergeHandler? handler = _handler;
            if (bus == null || handler == null)
            {
                return;
            }

            MergeGridSnapshot snapshot = QueryGrid(bus);
            for (int y = 0; y < snapshot.Height; y++)
            {
                for (int x = 0; x < snapshot.Width; x++)
                {
                    if (snapshot.LevelAt(x, y) != 0)
                    {
                        continue;
                    }

                    bus.Send(new SpawnItemCommand(x, y, SpawnLevel));
                    Refresh();
                    return;
                }
            }

            // 满盘：没有空格可落，按切片口径静默，不弹提示、不打日志噪声。
            Refresh();
        }

        /// <summary>
        /// 点"合成"：按行优先扫棋盘，把第一对"正交相邻且同等级"的格子合成掉。
        /// 一对都找不到就静默（不发命令、不报错），与切片对非法请求的口径保持一致。
        /// </summary>
        private void HandleMergeClicked()
        {
            ICqeBus? bus = _bus;
            MergeHandler? handler = _handler;
            if (bus == null || handler == null)
            {
                return;
            }

            MergeGridSnapshot snapshot = QueryGrid(bus);
            bool merged = false;
            for (int y = 0; y < snapshot.Height && !merged; y++)
            {
                for (int x = 0; x < snapshot.Width && !merged; x++)
                {
                    merged = TryMergeAt(bus, handler, snapshot, x, y);
                }
            }

            // 无论合成成功还是全部被拒绝，都以真实快照重画——玩家看到的就是账本与棋盘的实际状态。
            Refresh();
        }

        /// <summary>
        /// 尝试用 <c>(x, y)</c> 与它的右邻 / 下邻合成，成功（处理器计数增长）返回 <c>true</c>。
        /// <para>
        /// <b>为什么要比对计数而不是直接返回"发过命令"</b>：相邻同等级也可能因为"已达满级"被静默拒绝，
        /// 只有计数真的增长了才算合成成功，扫描才会停；否则继续找下一对，避免满级物品把按钮点成假的。
        /// </para>
        /// <para>
        /// 越界不需要单独判定：<see cref="MergeGridSnapshot.LevelAt"/> 对越界返回 -1，
        /// 与任何正等级都不相等，右边界 / 下边界自然被拒绝。
        /// </para>
        /// </summary>
        /// <param name="bus">总线（发合成命令）。</param>
        /// <param name="handler">合成处理器（读成功计数）。</param>
        /// <param name="snapshot">本次扫描用的棋盘快照；被拒绝的命令不改棋盘，所以同一份快照可以继续用。</param>
        /// <param name="x">源格列下标。</param>
        /// <param name="y">源格行下标。</param>
        /// <returns>本次调用是否真的完成了一次合成。</returns>
        private static bool TryMergeAt(ICqeBus bus, MergeHandler handler, MergeGridSnapshot snapshot, int x, int y)
        {
            int level = snapshot.LevelAt(x, y);
            if (level <= 0)
            {
                return false;
            }

            // 只试右邻与下邻：行优先扫描下，每一对相邻格都会被"左/上"那一侧扫到且只扫一次。
            if (snapshot.LevelAt(x + 1, y) == level)
            {
                int before = handler.MergeCount;
                bus.Send(new MergeCellsCommand(x, y, x + 1, y));
                if (handler.MergeCount != before)
                {
                    return true;
                }
            }

            if (snapshot.LevelAt(x, y + 1) == level)
            {
                int before = handler.MergeCount;
                bus.Send(new MergeCellsCommand(x, y, x, y + 1));
                if (handler.MergeCount != before)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>点"重新开始"。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>
        /// 重开：丢弃旧宿主（总线随宿主一起废弃）→ 建新宿主 → 重新注册切片 → 刷新。
        /// <para>
        /// <b>为什么先置空再 Dispose</b>：<c>Dispose</c> 之后再有人调到 <see cref="Refresh"/> 会拿到已释放的宿主，
        /// 先断引用就能让中途的任何刷新变成空操作。
        /// </para>
        /// </summary>
        private void Restart()
        {
            _handler = null;
            _bus = null;
            _host?.Dispose();
            _host = null;

            CreatePanel();
            Refresh();
        }
    }
}
