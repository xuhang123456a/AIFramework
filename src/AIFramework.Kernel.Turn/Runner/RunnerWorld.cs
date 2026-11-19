#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Runner
{
    /// <summary>
    /// 跑酷沙盒：Kernel.Turn 的又一套卡带内容，验证"<b>自动前进 + 跳跃/下蹲 + 障碍生成 + 距离计分</b>"
    /// 这类<b>连续推进状态机</b>能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么不新增 <c>GameplayKernelType</c> 枚举值</b>：本卡带对主板而言只是"又一个回合制会话"，
    /// 结算仍按 <see cref="GameplayKernelType.Turn"/>；新增枚举值会强逼装配层与存档协议同步扩容
    /// （与战棋、三消、俄罗斯方块、自走棋同一条理由）。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证，数值口径全部集中在 <see cref="RunnerRules"/>）</b>：
    /// 玩家固定在 <c>x = 0</c>，只跑不掉头；障碍从 <c>Distance + 20</c> 出生、按当前速度向后相对移动；
    /// 速度 <c>8 → 12 m/s</c> 随距离线性提升（100 米封顶）；分数 <c>floor(Distance) * 10</c>；
    /// 每 1.4 秒生成一个障碍（同屏最多 6 个，越过 <c>x &lt; -2</c> 回收）；
    /// 碰撞窗 <c>|x| ≤ 0.6</c>：Low 需要 <c>PlayerY ≥ 1.0</c>，High 需要 <c>IsSliding</c>，否则判负。
    /// </para>
    /// <para>
    /// <b>推进顺序</b>（<see cref="Tick"/>，与规则 8 的枚举一致）：前进距离 → 障碍后移与回收 →
    /// 跳跃物理 → 下蹲计时 → 生成 → 碰撞判定。速度取<b>本帧移动前</b>的距离计算，一帧内距离与障碍
    /// 共用同一个位移量，因此两者不会出现"障碍比地面跑得快"的错位。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：<c>Step(dt) = Tick(dt) + ProcessedSteps++</c>（规则 6 的原文口径）；
    /// 对局结束后两者都为空操作，<c>Restart</c> 也不清零 <see cref="ProcessedSteps"/>（属于会话统计）。
    /// </para>
    /// <para>
    /// <b>零分配</b>：障碍槽位数组在构造期一次分配，<see cref="Tick"/> / <see cref="TryJump"/> /
    /// <see cref="TrySlide"/> / <see cref="Restart"/> / <see cref="Step"/> 全程只做浮点运算与定长数组就地改写；
    /// 只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组。
    /// </para>
    /// <para>
    /// <b>文件拆分</b>：输入端口（跳跃/下蹲）与它们的"空中拒绝"判定放在 <c>RunnerWorld.Input.cs</c>，
    /// 本文件只保留状态、只读视图、推进、重开与结算。
    /// </para>
    /// </summary>
    public sealed partial class RunnerWorld : IGameplaySession, IRunnerReadView, IRunnerInput
    {
        /// <summary>障碍槽位（定长，构造期分配；有效区间是 <c>[0, _obstacleCount)</c>，按生成顺序排列）。</summary>
        private readonly RunnerObstacle[] _obstacles;

        /// <summary>障碍种类序列的确定性随机源（xorshift32，由 <c>RandomSeed</c> 决定）。</summary>
        private RunnerRandom _random;

        private float _distance;

        private float _playerY;

        private float _playerVerticalVelocity;

        /// <summary>下蹲剩余秒数；<c>IsSliding</c> 为 false 时无意义。</summary>
        private double _slideRemainingSeconds;

        /// <summary>距离下一次生成还差多少秒（累加器，重开归零）。</summary>
        private double _spawnAccumulatorSeconds;

        private int _obstacleCount;

        /// <summary>下一个障碍的编号；从 1 开始自增，重开不回卷（0 永远表示"没有障碍"）。</summary>
        private int _nextObstacleId;

        private int _processedSteps;

        private int _bestScore;

        private bool _isSliding;

        private bool _gameOver;

        private bool _resultCollected;

        public RunnerWorld(in GameplayInitData init)
        {
            _obstacles = new RunnerObstacle[RunnerRules.MaxObstacles];
            _random = new RunnerRandom(init.RandomSeed);
            _nextObstacleId = 1;
            _processedSteps = 0;
            _bestScore = 0;
            _resultCollected = false;

            ResetRunState();
        }

        /// <summary>已跑距离（米，单调递增；重开归零）。</summary>
        public float Distance
        {
            get { return _distance; }
        }

        /// <summary>当前分数：<c>floor(Distance) * 10</c>（每次由距离现算，不单独累加）。</summary>
        public int Score
        {
            get { return RunnerRules.ScoreAt(_distance); }
        }

        /// <summary>玩家离地高度（米）：0 = 地面，&gt; 0 = 跳跃中。</summary>
        public float PlayerY
        {
            get { return _playerY; }
        }

        /// <summary>是否正在下蹲（到时自动站起）。</summary>
        public bool IsSliding
        {
            get { return _isSliding; }
        }

        /// <summary>是否已撞上障碍。</summary>
        public bool IsGameOver
        {
            get { return _gameOver; }
        }

        /// <summary>当前存活的障碍数量（0..6）。</summary>
        public int ObstacleCount
        {
            get { return _obstacleCount; }
        }

        /// <summary>玩家横坐标，恒为 0。</summary>
        public float PlayerX
        {
            get { return 0f; }
        }

        /// <summary>当前前进速度（米/秒）：<c>8 + min(Distance / 100, 1) * 4</c>。</summary>
        public float Speed
        {
            get { return RunnerRules.SpeedAt(_distance); }
        }

        /// <summary>
        /// 本会话内历史最好分数。<b>重开不清零</b>：对局结束（判负）时写入一次，
        /// <see cref="Restart"/> 再取一次 max（幂等），因此"最好成绩"永远不会因重开而回退。
        /// </summary>
        public int BestScore
        {
            get { return _bestScore; }
        }

        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>对局是否结束：已判负，或结算报告已被取回。</summary>
        public bool IsFinished
        {
            get { return _gameOver || _resultCollected; }
        }

        /// <summary>
        /// 取第 <paramref name="index"/> 个障碍的快照（按生成顺序，0 = 最早生成、最靠近玩家）。
        /// 越界返回 <c>default</c>（<c>ObstacleId == 0</c>），不抛异常。
        /// </summary>
        public RunnerObstacleView GetObstacle(int index)
        {
            if (index < 0 || index >= _obstacleCount)
            {
                return default;
            }

            return _obstacles[index].ToView();
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
        /// 推进一帧（规则 8）：前进距离 → 障碍后移与回收 → 跳跃物理 → 下蹲计时 → 生成 → 碰撞判定。
        /// <para>
        /// <b>非法帧长</b>：<c>deltaSeconds &lt;= 0</c>、NaN 或大到转成 <c>float</c> 溢出的一律忽略（整帧空操作），
        /// 避免"负时间倒着跑"或"无穷距离"污染存档口径。
        /// </para>
        /// <para>
        /// <b>碰撞是逐帧点采样</b>：障碍位置只在本帧末尾被采样一次，因此极长的 <c>deltaSeconds</c>
        /// （位移超过 1.2 米的碰撞窗）可能穿过障碍。这是"<c>|x| ≤ 0.6</c> 时判定"的原文口径，
        /// 正常帧率（≤ 1/30 秒）不会有问题。
        /// </para>
        /// </summary>
        public void Tick(double deltaSeconds)
        {
            if (IsFinished || !(deltaSeconds > 0.0))
            {
                return;
            }

            float dt = (float)deltaSeconds;
            if (!(dt > 0f) || float.IsInfinity(dt))
            {
                return;
            }

            float speed = RunnerRules.SpeedAt(_distance);
            float advance = speed * dt;

            _distance += advance;
            AdvanceObstacles(advance);
            AdvanceJumpPhysics(dt);
            AdvanceSlideTimer(deltaSeconds);
            AdvanceSpawner(deltaSeconds);
            ResolveCollisions();
        }

        /// <summary>
        /// 重开一局：Distance / Score / 障碍 / 玩家姿态全部回到初始，<b>保留</b> <see cref="BestScore"/>、
        /// <see cref="ProcessedSteps"/> 与随机源进度（重开不会重播同一串障碍）。
        /// <para>结算报告已取回后会话所有权已经交还，此时 <see cref="Restart"/> 为空操作。</para>
        /// </summary>
        public void Restart()
        {
            if (_resultCollected)
            {
                return;
            }

            int score = Score;
            if (_bestScore < score)
            {
                _bestScore = score;
            }

            ResetRunState();
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// <para>
        /// 胜利口径：<c>Score ≥ 500</c>（跑满 50 米），胜利战利品为 <c>new LootItem(1003, 1)</c>；
        /// <see cref="GameplayResult.KernelType"/> 固定 <see cref="GameplayKernelType.Turn"/>；
        /// <see cref="GameplayResult.RemainingUnits"/> = <c>(int)Distance</c>（跑过的整米数）；
        /// <see cref="GameplayResult.TriggeredTags"/> 为空（跑酷没有事件标签）。
        /// </para>
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!_gameOver)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;
            bool isVictory = Score >= RunnerRules.VictoryScore;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(RunnerRules.VictoryLootItemId, RunnerRules.VictoryLootCount) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _processedSteps,
                (int)Distance,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>玩家是否离地（已起跳但尚未在 Tick 里移动过也算离地，因此"起跳当帧的二段跳"同样被拒）。</summary>
        private bool IsPlayerAirborne
        {
            get { return _playerY > 0f || _playerVerticalVelocity > 0f; }
        }

        /// <summary>把所有障碍按当前速度向后推 <paramref name="advance"/> 米，并回收越过 <c>x &lt; -2</c> 的槽位。</summary>
        private void AdvanceObstacles(float advance)
        {
            for (int i = 0; i < _obstacleCount; i++)
            {
                _obstacles[i].X -= advance;
            }

            for (int i = _obstacleCount - 1; i >= 0; i--)
            {
                if (_obstacles[i].X < -RunnerRules.RecycleBehindDistance)
                {
                    RemoveObstacleAt(i);
                }
            }
        }

        /// <summary>移除槽位并把它后面的障碍整体前移一格，保持"索引 = 生成顺序"。</summary>
        private void RemoveObstacleAt(int index)
        {
            for (int i = index + 1; i < _obstacleCount; i++)
            {
                _obstacles[i - 1] = _obstacles[i];
            }

            _obstacleCount--;
            _obstacles[_obstacleCount] = default;
        }

        /// <summary>
        /// 跳跃物理：精确匀加速积分 <c>y += v·dt - ½g·dt²</c>、<c>v -= g·dt</c>，
        /// 落地（<c>y ≤ 0</c>）立即归零并清零垂直速度。
        /// <para>
        /// <b>为什么用解析积分而不是 <c>v += a·dt; y += v·dt</c></b>：半隐式欧拉会系统性压低顶点
        /// （1/60 帧下顶点只有 1.003 米，而规则给的 6.5 m/s / 20 m/s² 解析顶点是 1.056 米），
        /// 会把"跳过 1.0 米"这条判定卡在误差带上；解析式让曲线与规则数值严格一致。
        /// </para>
        /// </summary>
        private void AdvanceJumpPhysics(float deltaSeconds)
        {
            if (_playerY <= 0f && _playerVerticalVelocity <= 0f)
            {
                return;
            }

            float halfGravityDt = 0.5f * RunnerRules.Gravity * deltaSeconds * deltaSeconds;
            _playerY += (_playerVerticalVelocity * deltaSeconds) - halfGravityDt;
            _playerVerticalVelocity -= RunnerRules.Gravity * deltaSeconds;

            if (_playerY <= 0f)
            {
                _playerY = 0f;
                _playerVerticalVelocity = 0f;
            }
        }

        /// <summary>下蹲计时：剩余秒数归零即自动站起（计时用 <c>double</c>，与帧长同精度）。</summary>
        private void AdvanceSlideTimer(double deltaSeconds)
        {
            if (!_isSliding)
            {
                return;
            }

            _slideRemainingSeconds -= deltaSeconds;
            if (_slideRemainingSeconds <= 0.0)
            {
                _isSliding = false;
                _slideRemainingSeconds = 0.0;
            }
        }

        /// <summary>
        /// 生成节奏：累加器每攒满 <see cref="RunnerRules.SpawnIntervalSeconds"/> 就生成一个。
        /// <para>
        /// 一帧最多补齐 <see cref="RunnerRules.MaxObstacles"/> 个（槽位上限），避免超大 <c>deltaSeconds</c>
        /// 把 while 循环撑成几百上千次；补齐后累加器若仍溢出，会在后续帧继续补，不会永久卡住。
        /// </para>
        /// </summary>
        private void AdvanceSpawner(double deltaSeconds)
        {
            _spawnAccumulatorSeconds += deltaSeconds;

            double dueRatio = _spawnAccumulatorSeconds / RunnerRules.SpawnIntervalSeconds;
            int due = dueRatio >= RunnerRules.MaxObstacles ? RunnerRules.MaxObstacles : (int)dueRatio;
            _spawnAccumulatorSeconds -= due * RunnerRules.SpawnIntervalSeconds;

            for (int i = 0; i < due; i++)
            {
                SpawnOneObstacle();
            }
        }

        /// <summary>在当前距离前方 20 米处放一个障碍；种类由随机源决定（Low 70% / High 30%）；槽位满则放弃。</summary>
        private void SpawnOneObstacle()
        {
            if (_obstacleCount >= RunnerRules.MaxObstacles)
            {
                return;
            }

            RunnerObstacleKind kind = _random.NextPercent() < RunnerRules.LowKindPercent
                ? RunnerObstacleKind.Low
                : RunnerObstacleKind.High;

            _obstacles[_obstacleCount] = new RunnerObstacle(
                _nextObstacleId,
                _distance + RunnerRules.SpawnAheadDistance,
                kind);

            _nextObstacleId++;
            _obstacleCount++;
        }

        /// <summary>
        /// 碰撞判定：<c>|x| ≤ 0.6</c> 的障碍逐个检查——Low 要 <c>PlayerY ≥ 1.0</c>、
        /// High 要正在下蹲，否则判负并给该障碍打上 <see cref="RunnerObstacleView.Hit"/>。
        /// 同一帧只结算第一个撞上的障碍（其余保持未命中，对局已结束）。
        /// </summary>
        private void ResolveCollisions()
        {
            for (int i = 0; i < _obstacleCount; i++)
            {
                if (Math.Abs(_obstacles[i].X) > RunnerRules.CollisionHalfWidth)
                {
                    continue;
                }

                bool blocked = _obstacles[i].Kind == RunnerObstacleKind.Low
                    ? _playerY < RunnerRules.LowClearHeight
                    : !_isSliding;

                if (!blocked)
                {
                    continue;
                }

                _obstacles[i].Hit = true;
                _gameOver = true;

                if (_bestScore < Score)
                {
                    _bestScore = Score;
                }

                return;
            }
        }

        /// <summary>
        /// 把"一局"的状态清空（构造期与 <see cref="Restart"/> 共用）：
        /// 不动 <see cref="BestScore"/>、<see cref="ProcessedSteps"/>、随机源与障碍编号计数器。
        /// </summary>
        private void ResetRunState()
        {
            _distance = 0f;
            _playerY = 0f;
            _playerVerticalVelocity = 0f;
            _isSliding = false;
            _slideRemainingSeconds = 0.0;
            _spawnAccumulatorSeconds = 0.0;
            _obstacleCount = 0;
            _gameOver = false;

            for (int i = 0; i < _obstacles.Length; i++)
            {
                _obstacles[i] = default;
            }
        }
    }
}
