#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Platformer
{
    /// <summary>
    /// 平台跳跃沙盒（T2-7）：Kernel.Turn 的又一套卡带内容，验证"<b>重力 / 跳跃 / 单向平台碰撞 / 收集品</b>"
    /// 这类<b>连续物理推进</b>同样能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么复用 <see cref="GameplayKernelType.Turn"/> 而不是新增枚举值</b>：对主板而言本卡带只是
    /// "又一个回合制会话"——<see cref="IGameplaySession"/> 的四个成员（推进、结束、结算）与战棋/音游完全同构；
    /// 新增枚举值会强逼装配层、存档协议与场景路由同步扩容（与跑酷、俄罗斯方块、自走棋同一条理由）。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证，数值口径全部集中在 <see cref="PlatformerRules"/>）</b>：
    /// 内置 3 关（<see cref="PlatformerLevel"/>，<b>关卡数据后续由配置表提供</b>），
    /// 宽 40/48/56 米、平台 6/7/8 块、收集品 6/7/8 个、终点在右墙；
    /// 重力 <c>22</c>、起跳初速 <c>8</c>、水平速度 <c>6</c>、下落封顶 <c>18</c>，
    /// 玩家 AABB <c>0.8 × 1.6</c>；<see cref="Tick(double)"/> 按 <c>0.05s</c> 上限切子步，
    /// 平台只做"从上方落到顶面"的单向碰撞，收集品 AABB 相交即得分（<c>+10</c>），
    /// 到达终点且在地面判胜，脚底低于 <c>-10</c> 判负。
    /// </para>
    /// <para>
    /// <b>单向平台的落地口径</b>：只有当<b>上一子步的脚底不低于平台顶面</b>、且<b>本子步穿到顶面之下</b>时才落地；
    /// 因此从下方顶上去、贴边横穿、站在平台下方都不会被挡住，而"从高处落到顶面"会精确贴到 <c>Y + Height</c>。
    /// 走出平台边缘（水平不再相交）当帧即失去支撑转为自由下落。
    /// </para>
    /// <para>
    /// <b>终点口径</b>：<c>PlayerX &gt;= GoalX</c> 且 <see cref="IsOnGround"/> 才判胜——空中越过终点线不算，
    /// 必须落地那一刻才结算。这条让"跳跃冲线"与"走线冲线"得到同一个结果，也避免空中擦线直接锁死操作。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：<c>Step(dt) = Tick(dt) + ProcessedSteps++</c>；对局结束后两者都是空操作，
    /// <see cref="Restart"/> 也不清零 <see cref="ProcessedSteps"/>（那是会话统计，不属于"一局"）。
    /// </para>
    /// <para>
    /// <b>零分配</b>：平台与收集品数组在构造期一次分配，<see cref="Tick(double)"/> / <see cref="TryJump"/> /
    /// <see cref="SetMove"/> / <see cref="Restart"/> / <see cref="Step"/> 全程只做浮点运算与定长数组就地改写；
    /// 只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组。
    /// </para>
    /// <para>
    /// <b>文件拆分</b>：状态、只读视图、推进步与结算法在本文件；输入端口（左右/跳跃）在
    /// <c>PlatformerWorld.Input.cs</c>；物理（子步切分、重力、落地、收集、终局判定）在
    /// <c>PlatformerWorld.Physics.cs</c>。三个文件都不超过 400 行，评审可以分别读完。
    /// </para>
    /// </summary>
    public sealed partial class PlatformerWorld : IGameplaySession, IPlatformerReadView, IPlatformerInput
    {
        /// <summary>平台定长数组（构造期一次分配；有效区间是 <c>[0, _platformCount)</c>）。</summary>
        private readonly PlatformerView[] _platforms;

        /// <summary>收集品定长数组（构造期一次分配；收集时就地改成 <c>Collected = true</c>）。</summary>
        private readonly CollectibleView[] _collectibles;

        /// <summary>本关平台数（≤ <see cref="PlatformerLevel.MaxPlatforms"/>）。</summary>
        private readonly int _platformCount;

        /// <summary>本关收集品数（≤ <see cref="PlatformerLevel.MaxCollectibles"/>）。</summary>
        private readonly int _collectibleCount;

        /// <summary>夹取后的关卡号（1..3）。</summary>
        private readonly int _levelId;

        /// <summary>世界宽度（米）：右墙位置。</summary>
        private readonly float _worldWidth;

        /// <summary>终点线横坐标（米）：等于右墙，玩家左沿贴到它即到达。</summary>
        private readonly float _goalX;

        /// <summary>出生平台顶面高度（米）：出生的 <c>PlayerY</c>，也是第 1 关的地面高度。</summary>
        private readonly float _groundTop;

        /// <summary>出生点横坐标（米）。</summary>
        private readonly float _spawnX;

        private float _playerX;

        private float _playerY;

        private float _playerVelocityY;

        /// <summary>当前左右移动意图（<c>-1..1</c>，按住式，由 <see cref="SetMove(float)"/> 每帧写入）。</summary>
        private float _moveDirection;

        private int _score;

        private int _collectedCount;

        private int _processedSteps;

        private bool _isOnGround;

        private bool _isVictory;

        private bool _isFinished;

        private bool _resultCollected;

        public PlatformerWorld(in GameplayInitData init)
        {
            _levelId = PlatformerLevel.NormalizeLevelId(init.LevelId);
            _worldWidth = PlatformerLevel.WorldWidth(_levelId);
            _goalX = _worldWidth - PlatformerRules.PlayerWidth;
            _spawnX = PlatformerLevel.SpawnXOf(_levelId);

            _platformCount = PlatformerLevel.PlatformCount(_levelId);
            _platforms = new PlatformerView[PlatformerLevel.MaxPlatforms];
            PlatformerLevel.BuildPlatforms(_levelId, _platforms);

            _collectibleCount = PlatformerLevel.CollectibleCount(_levelId);
            _collectibles = new CollectibleView[PlatformerLevel.MaxCollectibles];

            _groundTop = _platforms[0].Y + _platforms[0].Height;
            _processedSteps = 0;
            _resultCollected = false;

            ResetRunState();
        }

        /// <summary>夹取后的关卡号（<c>1..3</c>）：<c>LevelId</c> 只有 2/3 命中其它两关，其余一律第 1 关。</summary>
        public int LevelId
        {
            get { return _levelId; }
        }

        /// <summary>终点线横坐标（米）：等于右墙 <c>WorldWidth - 0.8</c>，到达即（落地时）判胜。</summary>
        public float GoalX
        {
            get { return _goalX; }
        }

        /// <summary>玩家 AABB 左下角的世界横坐标（米）。</summary>
        public float PlayerX
        {
            get { return _playerX; }
        }

        /// <summary>玩家 AABB 左下角的世界纵坐标（米）：站在平台上时等于该平台顶面高度。</summary>
        public float PlayerY
        {
            get { return _playerY; }
        }

        /// <summary>玩家垂直速度（米/秒，向上为正；在地面时恒为 0）。</summary>
        public float PlayerVelocityY
        {
            get { return _playerVelocityY; }
        }

        /// <summary>玩家是否站在平台上：起跳当帧即 false，落地当帧即 true。</summary>
        public bool IsOnGround
        {
            get { return _isOnGround; }
        }

        /// <summary>当前分数：每收集一个 <c>+10</c>（重开归零）。</summary>
        public int Score
        {
            get { return _score; }
        }

        /// <summary>已收集数量（<c>0..CollectibleCount</c>）。</summary>
        public int CollectedCount
        {
            get { return _collectedCount; }
        }

        /// <summary>本关收集品总数（6/7/8）。</summary>
        public int CollectibleCount
        {
            get { return _collectibleCount; }
        }

        /// <summary>本关平台总数（6/7/8；第 0 块是出生平台）。</summary>
        public int PlatformCount
        {
            get { return _platformCount; }
        }

        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>对局是否结束：已到达终点、已掉出世界，或结算报告已被取回。</summary>
        public bool IsFinished
        {
            get { return _isFinished || _resultCollected; }
        }

        /// <summary>结束方式是否为胜利；未结束或掉出世界时恒为 false。</summary>
        public bool IsVictory
        {
            get { return _isVictory; }
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 个收集品的只读快照（0 起，顺序即关卡数据顺序）。
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">下标不在 <c>0..CollectibleCount-1</c> 内。</exception>
        public CollectibleView GetCollectible(int index)
        {
            if (index < 0 || index >= _collectibleCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    "收集品下标越界：合法区间是 0..CollectibleCount-1。");
            }

            return _collectibles[index];
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 块平台的只读快照（0 起，第 0 块是出生平台）。
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">下标不在 <c>0..PlatformCount-1</c> 内。</exception>
        public PlatformerView GetPlatform(int index)
        {
            if (index < 0 || index >= _platformCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    "平台下标越界：合法区间是 0..PlatformCount-1。");
            }

            return _platforms[index];
        }

        /// <summary>
        /// 推进一步 = <see cref="Tick(double)"/> + <see cref="ProcessedSteps"/> +1。
        /// 已结束（含结算已取回）时为空操作，不累加。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            Tick(deltaSeconds);
            _processedSteps++;
        }

        /// <summary>
        /// 重开一局：玩家位置/速度、移动意图、分数、收集品状态与终局标记全部回到初始，
        /// <b>保留</b> <see cref="ProcessedSteps"/>（会话统计）与关卡数据（<see cref="LevelId"/> 不变）。
        /// <para>结算报告已取回后会话所有权已经交还，此时 <see cref="Restart"/> 为空操作。</para>
        /// </summary>
        public void Restart()
        {
            if (_resultCollected)
            {
                return;
            }

            ResetRunState();
        }

        /// <summary>
        /// 取回结算报告：未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// <para>
        /// 胜负口径：<see cref="IsVictory"/>（到达终点且落地）；胜利战利品为
        /// <c>new LootItem(1003, 1)</c>，失败为空战利品；
        /// <see cref="GameplayResult.KernelType"/> 固定 <see cref="GameplayKernelType.Turn"/>（本卡带不新增枚举值）；
        /// <see cref="GameplayResult.RemainingUnits"/> 取 <see cref="Score"/>（Platformer 没有兵力，
        /// 这里复用该字段承载"本局得分"这种同样单调的局末读数）。
        /// </para>
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_isFinished)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;

            IReadOnlyList<LootItem> loots = _isVictory
                ? new LootItem[] { new LootItem(PlatformerRules.VictoryLootItemId, PlatformerRules.VictoryLootCount) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                _isVictory,
                _processedSteps,
                _score,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>
        /// 把"一局"的状态清空（构造期与 <see cref="Restart"/> 共用）：
        /// 不动 <see cref="ProcessedSteps"/>、关卡数据与结算标记。
        /// </summary>
        private void ResetRunState()
        {
            PlatformerLevel.BuildCollectibles(_levelId, _collectibles);

            _playerX = _spawnX;
            _playerY = _groundTop;
            _playerVelocityY = 0f;
            _moveDirection = 0f;
            _score = 0;
            _collectedCount = 0;
            _isOnGround = true;
            _isVictory = false;
            _isFinished = false;
        }
    }
}
