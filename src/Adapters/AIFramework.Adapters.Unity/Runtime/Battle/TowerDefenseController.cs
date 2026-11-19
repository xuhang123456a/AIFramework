#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Numerics;
using AIFramework.Contracts.Ports;
using AIFramework.Features.TowerDefense;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 塔防场景控制器：<b>Meta 建造 + Realtime 战斗双内核协作</b>的唯一装配点。
    /// <para>
    /// <b>双内核怎么协作</b>：棋盘状态归 <see cref="TowerDefenseHandler"/>（Meta 切片，走 CQE 总线），
    /// 战斗推演归 <c>Kernel.Realtime</c>（<see cref="IGameplaySession"/> + <see cref="IRealtimeSandbox"/>）。
    /// 两者互不引用，中间只有一条只读数据通道：本类查 <see cref="TowerLayoutQueryRequest"/> 拿到布局，
    /// 把格坐标翻译成世界坐标后 <c>SpawnUnit</c> 布场。换掉任何一侧都不需要改另一侧。
    /// </para>
    /// <para>
    /// <b>为什么建造后要重开一局</b>：Phase 1 的实时内核在布场阶段预分配槽位，帧内不允许新增单位
    /// （见 <see cref="IRealtimeSandbox"/> 契约）。"建完塔再塞进正在跑的战局"会破坏零分配假设，
    /// 因此建成后 <c>Release</c> 旧会话、用新布局重开一局——玩家看到的是"下一波坦克按新阵型开打"。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TowerDefenseController : MonoBehaviour
    {
        /// <summary>步进间隔兜底值：字段被 Inspector 改成 0 或负数时用它，避免除零/死循环。</summary>
        private const float FallbackStepSeconds = 1f / 60f;

        /// <summary>单帧最多结算的步数：掉帧后不允许在一帧里追赶出一大段战斗（防"追帧爆炸"）。</summary>
        private const int MaxStepsPerFrame = 8;

        /// <summary>HUD 刷新间隔（秒）：状态行每帧重写会持续触发 Text 重建。</summary>
        private const float HudRefreshSeconds = 0.2f;

        /// <summary>每局放进去的敌方单位数（同时决定 <c>GameplayInitData.UnitCount</c>）。</summary>
        private const int EnemyUnitCount = 3;

        /// <summary>每格对应的世界单位长度。</summary>
        private const float BoardCellWorldSize = 1f;

        /// <summary>塔的单位半径（世界单位）。</summary>
        private const float TowerRadius = 0.4f;

        /// <summary>塔的护甲等级。</summary>
        private const int TowerArmorLevel = 2;

        /// <summary>
        /// 塔的生命值。<b>刻意给足</b>：内核 Phase 1 的胜负判定是"首个布场的单位（主角）是否存活"，
        /// 塔必须撑到本局结束，否则玩家建得越多反而越容易输。
        /// </summary>
        private const int TowerHp = 100;

        /// <summary>敌方单位的半径。</summary>
        private const float EnemyRadius = 0.3f;

        /// <summary>敌方单位护甲等级。</summary>
        private const int EnemyArmorLevel = 0;

        /// <summary>敌方单位生命值。</summary>
        private const int EnemyHp = 10;

        /// <summary>敌方飞行物伤害。</summary>
        private const int EnemyProjectileDamage = 4;

        /// <summary>敌方飞行物速度（世界单位/秒）。</summary>
        private const float EnemyProjectileSpeed = 6f;

        /// <summary>飞行物出膛点相对敌方单位中心的偏移，避免出生当帧就与自己扫掠相交。</summary>
        private const float EnemyMuzzleOffset = 0.6f;

        /// <summary>敌方出生点相对棋盘上边缘的外扩距离（"从上方入场"）。</summary>
        private const float EnemySpawnOffset = 1f;

        /// <summary>演示子表所在目录（相对 StreamingAssets，与宿主保持一致）。</summary>
        private const string TableRelativeDirectory = "AIFrameworkDemo";

        /// <summary>未选中任何塔时的哨兵值。</summary>
        private const int NoTowerSelected = 0;

        /// <summary>棋盘皮肤（渲染与点击都经它，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private TowerDefenseHud hud = null!;

        /// <summary>关卡 ID，进门前快照的一部分。</summary>
        [SerializeField] private int levelId = 1;

        /// <summary>随机种子：固定值让同一波布局可复现。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>每次 <c>Step</c> 的逻辑步长（秒）；决定"实时"战斗的推进速度。</summary>
        [SerializeField] private float stepSeconds = FallbackStepSeconds;

        /// <summary>宿主（总线/会话/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>塔防 Meta 切片；子表缺失时为 <c>null</c>（此时只能看不能建）。</summary>
        private TowerDefenseHandler? _towers;

        /// <summary>当前实时战斗会话；未开战或已结算时为 <c>null</c>。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的布场端口；内核未实现该接口时为 <c>null</c>。</summary>
        private IRealtimeSandbox? _sandbox;

        /// <summary>本局塔的世界坐标（布场时算一次，飞行物瞄准用）。</summary>
        private readonly List<Vec2> _towerPositions = new List<Vec2>();

        /// <summary>步进累加器：攒够 <see cref="stepSeconds"/> 才走一步。</summary>
        private float _accumulator;

        /// <summary>HUD 刷新累加器。</summary>
        private float _hudAccumulator;

        /// <summary>当前波次（每次建造重开一局即 +1）。</summary>
        private int _wave = 1;

        /// <summary>本局已推进的逻辑帧数（只用于 HUD 展示）。</summary>
        private int _stepCount;

        /// <summary>本局是否已结算（结算只允许发生一次）。</summary>
        private bool _finished;

        /// <summary>当前实时战斗会话（供外部诊断/测试读取；未开战为 <c>null</c>）。</summary>
        public IGameplaySession? Session
        {
            get { return _session; }
        }

        /// <summary>塔防 Meta 切片（供外部诊断读取；子表缺失为 <c>null</c>）。</summary>
        public TowerDefenseHandler? Towers
        {
            get { return _towers; }
        }

        /// <summary>
        /// 建宿主 → 装塔防切片 → 绑 HUD → 开第一局。
        /// <b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的点击订阅要在玩家可能点下去之前挂好。
        /// </summary>
        private void Awake()
        {
            AIFrameworkUnityHost host;
            try
            {
                host = new AIFrameworkUnityHost();
                host.Initialize();
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is ArgumentException
                || exception is IOException)
            {
                // 宿主装配失败（存档目录不可写等）时不能静默：整局就没法玩了。
                Debug.LogError("[TowerDefense] 宿主装配失败：" + exception.Message);
                return;
            }

            _host = host;

            // 塔防切片自己装：宿主的 Initialize 不负责它（本切片晚于宿主引入），
            // 与商店/任务的"缺表只关掉自己"策略一致，失败时 HUD 会明确显示"塔表缺失"。
            IBinaryTableLoader tableLoader = new UnityStreamingAssetsTableLoader(host.Assets, TableRelativeDirectory);
            try
            {
                var towers = new TowerDefenseHandler(host.Bus, tableLoader);
                towers.Register(host.Bus);
                _towers = towers;
                host.Log.Info("[TowerDefense] 塔防切片已装配：塔种类=" + towers.TowerKindCount);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is InvalidDataException
                || exception is IOException
                || exception is ArgumentException)
            {
                _towers = null;
                host.Log.Error("塔防切片装配失败（子表缺失或损坏），已跳过建造：" + exception.Message);
            }

            TowerDefenseHud? view = hud;
            if (view != null)
            {
                view.Bind(host.Bus);
                view.OnCellClicked += HandleCellClicked;
            }
            else
            {
                host.Log.Error("[TowerDefense] hud 未注入，棋盘无法渲染与点击。");
            }

            StartBattle();

            host.Log.Info("[TowerDefense] 开局：关卡=" + levelId + " 种子=" + randomSeed
                + " 逻辑步长=" + EffectiveStepSeconds() + "s 敌人=" + EnemyUnitCount);
        }

        /// <summary>
        /// 按累加器推进逻辑步，每 <see cref="HudRefreshSeconds"/> 刷一次 HUD，结束即结算。
        /// <b>刻意不用协程</b>（宪法禁止业务逻辑挂协程）。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            if (session == null || _finished)
            {
                return;
            }

            if (session.IsFinished)
            {
                FinishBattle();
                return;
            }

            float interval = EffectiveStepSeconds();
            _accumulator += Time.deltaTime;

            int steps = 0;
            while (_accumulator >= interval && steps < MaxStepsPerFrame)
            {
                _accumulator -= interval;
                steps++;
                session.Step(interval);
                _stepCount++;

                if (session.IsFinished)
                {
                    break;
                }
            }

            // 严重掉帧后不要把积压的步全补上，只留一步的余量。
            if (_accumulator > interval)
            {
                _accumulator = interval;
            }

            _hudAccumulator += Time.deltaTime;
            if (_hudAccumulator >= HudRefreshSeconds)
            {
                _hudAccumulator = 0f;
                RenderHud();
            }

            if (session.IsFinished)
            {
                FinishBattle();
            }
        }

        /// <summary>退订 HUD 并释放宿主（宿主 Dispose 会丢弃未结算的对局引用）。</summary>
        private void OnDestroy()
        {
            TowerDefenseHud? view = hud;
            if (view != null)
            {
                view.OnCellClicked -= HandleCellClicked;
                view.Unbind();
            }

            _session = null;
            _sandbox = null;
            _finished = true;

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 点格子 → 发建造命令 → 成功后重开一局。
        /// <para>
        /// <b>为什么不在这里复刻"钱够不够/格子空不空"</b>：那些规则全在切片里，
        /// 适配层复制一份就会与切片各自演化；这里只比较 <c>BuildCount</c> 判断是否真的建成。
        /// </para>
        /// </summary>
        private void HandleCellClicked(int x, int y)
        {
            AIFrameworkUnityHost? host = _host;
            TowerDefenseHandler? towers = _towers;
            if (host == null || towers == null)
            {
                return;
            }

            TowerDefenseHud? view = hud;
            int towerId = view != null ? view.SelectedTowerId : NoTowerSelected;
            if (towerId == NoTowerSelected)
            {
                return;
            }

            int before = towers.BuildCount;
            host.Bus.Send(new BuildTowerCommand(x, y, towerId));
            if (towers.BuildCount == before)
            {
                // 静默拒绝（越界/占用/未知塔/钱不够）：只刷新一次 UI，不做提示。
                RenderHud();
                return;
            }

            host.Log.Info("[TowerDefense] 建造成功：格子=(" + x + "," + y + ") 塔=" + towerId
                + " 累计=" + towers.BuildCount + "，按新阵型重开一局。");

            _wave++;
            StartBattle();
        }

        /// <summary>
        /// 开一局实时战斗并布场。棋盘上没有任何塔时<b>不开战</b>：
        /// 内核 Phase 1 的"主角 = 首个布场单位"判定会让空战场莫名其妙地判胜，
        /// 与其展示一个假的胜利，不如让 HUD 提示"先建塔"。
        /// </summary>
        private void StartBattle()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            TowerLayoutSnapshot? layout = QueryLayout();
            if (layout == null)
            {
                RenderHud();
                return;
            }

            if (layout.TowerCount == 0)
            {
                host.Log.Info("[TowerDefense] 棋盘上还没有塔，暂不开战（先点格子建一座）。");
                RenderHud();
                return;
            }

            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            IGameplaySession session;
            try
            {
                var init = new GameplayInitData(levelId, randomSeed + _wave, EnemyUnitCount);
                session = host.Sessions.Begin(GameplayKernelType.Realtime, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[TowerDefense] 开局失败（宿主工厂未接 Realtime 分支？）：" + exception.Message);
                return;
            }

            _session = session;
            _sandbox = session as IRealtimeSandbox;
            _accumulator = 0f;
            _hudAccumulator = 0f;
            _stepCount = 0;
            _finished = false;

            TowerDefenseHud? view = hud;
            if (view != null)
            {
                view.ClearResult();
            }

            if (_sandbox == null)
            {
                host.Log.Error("[TowerDefense] 会话未实现 IRealtimeSandbox，无法布场（战斗不会发生）。");
                RenderHud();
                return;
            }

            int placed = DeployField(_sandbox, layout);
            host.Log.Info("[TowerDefense] 第 " + _wave + " 波开战：塔单位=" + placed
                + " 敌单位=" + EnemyUnitCount + " 已建塔格=" + layout.TowerCount);

            RenderHud();
        }

        /// <summary>
        /// 布场：塔按格坐标转世界坐标放入，3 个敌方单位从棋盘上方入场并各发射一枚飞行物。
        /// <b>布场顺序即语义</b>：第一个 <c>SpawnUnit</c> 的单位是内核认定的"主角"，
        /// 因此塔必须先于敌人放入，胜负才会被读成"塔阵守住了"。
        /// </summary>
        /// <returns>成功放入的塔单位数。</returns>
        private int DeployField(IRealtimeSandbox sandbox, TowerLayoutSnapshot layout)
        {
            _towerPositions.Clear();

            for (int y = 0; y < layout.Height; y++)
            {
                for (int x = 0; x < layout.Width; x++)
                {
                    if (layout.TowerIdAt(x, y) <= 0)
                    {
                        continue;
                    }

                    Vec2 position = CellToWorld(x, y);
                    UnitHandle handle = sandbox.SpawnUnit(position, TowerRadius, TowerArmorLevel, TowerHp, GameplayTagMask.Empty);
                    if (!handle.IsValid)
                    {
                        // 槽位耗尽：不再尝试，剩下的格子在报告里体现为"塔单位数 < 塔格数"。
                        return _towerPositions.Count;
                    }

                    _towerPositions.Add(position);
                }
            }

            for (int i = 0; i < EnemyUnitCount; i++)
            {
                Vec2 position = EnemySpawnPosition(i);
                UnitHandle enemy = sandbox.SpawnUnit(position, EnemyRadius, EnemyArmorLevel, EnemyHp, GameplayTagMask.Empty);
                if (!enemy.IsValid)
                {
                    continue;
                }

                Vec2 direction = AimDirection(position, i);
                sandbox.SpawnProjectile(
                    enemy,
                    position + (direction * EnemyMuzzleOffset),
                    direction * EnemyProjectileSpeed,
                    EnemyProjectileDamage,
                    GameplayTagMask.Empty);
            }

            return _towerPositions.Count;
        }

        /// <summary>
        /// 结算：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示胜负。
        /// <b>顺序不可换</b>：先广播事实再发奖，保证任务/成就类切片看到的是"这局已结束"时的账本状态。
        /// </summary>
        private void FinishBattle()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _finished)
            {
                return;
            }

            _finished = true;
            _session = null;
            _sandbox = null;

            GameplayResult result;
            try
            {
                result = host.Sessions.EndAndCollect();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[TowerDefense] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Realtime, levelId, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            TowerDefenseHud? view = hud;
            if (view != null)
            {
                view.ShowResult(result);
            }

            host.Log.Info("[TowerDefense] 第 " + _wave + " 波结算：胜负=" + result.IsVictory
                + "，推演帧=" + result.StepCount
                + "，存活单位=" + result.RemainingUnits
                + "，战利品条目=" + loots.Count);
        }

        /// <summary>渲染一次 HUD（会话可能已被销毁，故先取本地副本）。</summary>
        private void RenderHud()
        {
            TowerDefenseHud? view = hud;
            if (view != null)
            {
                view.Render(_wave, _stepCount);
            }
        }

        /// <summary>查布局快照；处理器未注册（塔表缺失）时返回 <c>null</c>。</summary>
        private TowerLayoutSnapshot? QueryLayout()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _towers == null)
            {
                return null;
            }

            try
            {
                ICqeBus bus = host.Bus;
                return bus.Query<TowerLayoutQueryRequest, TowerLayoutSnapshot>(new TowerLayoutQueryRequest());
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>格坐标 → 世界坐标：棋盘以世界原点为中心，一格一个世界单位。</summary>
        /// <param name="x">列坐标。</param>
        /// <param name="y">行坐标。</param>
        /// <returns>该格中心的世界坐标。</returns>
        private static Vec2 CellToWorld(int x, int y)
        {
            float halfWidth = (TowerDefenseHandler.Width - 1) * 0.5f;
            float halfHeight = (TowerDefenseHandler.Height - 1) * 0.5f;
            return new Vec2((x - halfWidth) * BoardCellWorldSize, (y - halfHeight) * BoardCellWorldSize);
        }

        /// <summary>第 <paramref name="index"/> 个敌人的入场点：棋盘上边缘外 <see cref="EnemySpawnOffset"/> 处横向排开。</summary>
        private static Vec2 EnemySpawnPosition(int index)
        {
            float halfHeight = (TowerDefenseHandler.Height - 1) * 0.5f;
            float y = halfHeight + EnemySpawnOffset;
            float startX = -2f;
            return new Vec2(startX + (index * 1.5f), y);
        }

        /// <summary>
        /// 敌人瞄准方向：按 <paramref name="enemyIndex"/> 轮转分配塔位（多个塔才会同时挨打），
        /// 场上没有塔时朝下飞（纯展示，不可能命中任何单位）。
        /// </summary>
        private Vec2 AimDirection(Vec2 from, int enemyIndex)
        {
            if (_towerPositions.Count == 0)
            {
                return new Vec2(0f, -1f);
            }

            Vec2 target = _towerPositions[enemyIndex % _towerPositions.Count];
            Vec2 direction = new Vec2(target.X - from.X, target.Y - from.Y).Normalized;
            return direction.SqrLength > 0f ? direction : new Vec2(0f, -1f);
        }

        /// <summary>出步间隔：字段被改成非法值时退回 1/60 秒。</summary>
        private float EffectiveStepSeconds()
        {
            float interval = stepSeconds;
            return interval > 0f ? interval : FallbackStepSeconds;
        }
    }
}
