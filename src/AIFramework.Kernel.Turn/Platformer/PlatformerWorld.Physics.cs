#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Platformer
{
    /// <summary>
    /// <see cref="PlatformerWorld"/> 的物理推进（<c>IPlatformerInput.Tick</c>）：子步切分、水平移动、
    /// 站地维持、重力积分、单向平台落地、收集品拾取与终局判定。
    /// <para>
    /// <b>为什么按 0.05 秒切子步</b>：下落封顶 18 m/s 时一个 0.05 秒子步最多走 0.9 米，小于任何两层平台的高度差，
    /// 因此"从平台上方落到顶面"不会因为一帧太大而直接穿过去。切分只发生在 <see cref="Tick(double)"/> 内部，
    /// 每个子步都用<b>同一个</b> <c>delta</c> 口径推进，不改变调用方传入的帧长。
    /// </para>
    /// <para>
    /// <b>单个子步的顺序</b>：水平移动 → 站地维持（走出边缘即失去支撑）→ 重力与落地 → 收集品拾取 → 终局判定。
    /// 这个顺序保证"本帧刚走到的位置"就能被收集与终点采样，不会出现"前一帧的坐标算了分"这种滞后。
    /// </para>
    /// <para>
    /// <b>零分配</b>：本文件只有浮点运算、布尔标记与对定长数组按值读写，不申请任何托管堆内存。
    /// </para>
    /// </summary>
    public sealed partial class PlatformerWorld
    {
        /// <summary>
        /// 推进物理：按 <see cref="PlatformerRules.MaxSubstepSeconds"/> 切子步，逐子步做
        /// 水平移动、站地维持、重力积分、单向平台落地、收集品拾取与终局判定。
        /// <para>
        /// <b>非法帧长</b>：<c>deltaSeconds &lt;= 0</c>、<c>NaN</c>、<c>±∞</c> 一律忽略（整帧空操作），
        /// 避免"负时间倒着走"或"无穷位移"污染状态；单帧超过 2 秒的按 2 秒推（宿主断点保护）。
        /// </para>
        /// </summary>
        public void Tick(double deltaSeconds)
        {
            if (IsFinished || !(deltaSeconds > 0.0) || double.IsInfinity(deltaSeconds))
            {
                return;
            }

            double remaining = deltaSeconds;
            if (remaining > PlatformerRules.MaxTickSeconds)
            {
                remaining = PlatformerRules.MaxTickSeconds;
            }

            while (remaining > 0.0)
            {
                double step = remaining > PlatformerRules.MaxSubstepSeconds
                    ? PlatformerRules.MaxSubstepSeconds
                    : remaining;

                remaining -= step;
                AdvanceSubstep((float)step);

                if (IsFinished)
                {
                    return;
                }
            }
        }

        /// <summary>一个物理子步：水平移动 → 站地维持 → 重力与落地 → 收集品 → 终局判定。</summary>
        private void AdvanceSubstep(float deltaSeconds)
        {
            AdvanceHorizontal(deltaSeconds);
            RefreshGroundSupport();
            AdvanceVertical(deltaSeconds);
            CollectOverlappedCollectibles();
            ResolveTerminalState();
        }

        /// <summary>
        /// 水平移动：速度 <c>move · 6 m/s</c>，结果夹进 <c>[0, WorldWidth - PlayerWidth]</c>。
        /// 平台不阻挡左右移动（单向平台口径），因此这里只受世界边界约束。
        /// </summary>
        private void AdvanceHorizontal(float deltaSeconds)
        {
            if (_moveDirection == 0f)
            {
                return;
            }

            float nextX = _playerX + (_moveDirection * PlatformerRules.MoveSpeed * deltaSeconds);
            _playerX = PlatformerRules.ClampPlayerX(nextX, _worldWidth);
        }

        /// <summary>
        /// 站地维持：还站在某块平台顶面就保持 <see cref="IsOnGround"/>；走出边缘（水平不再相交）
        /// 当帧即转为自由下落，垂直速度归零（从静止开始掉，而不是继承上一段跳跃的残余速度）。
        /// </summary>
        private void RefreshGroundSupport()
        {
            if (!_isOnGround)
            {
                return;
            }

            if (IsStandingOnPlatform())
            {
                return;
            }

            _isOnGround = false;
            _playerVelocityY = 0f;
        }

        /// <summary>脚底是否正贴着某块平台的顶面（高度差在容差内且水平相交）。</summary>
        private bool IsStandingOnPlatform()
        {
            for (int i = 0; i < _platformCount; i++)
            {
                PlatformerView platform = _platforms[i];
                float top = platform.Y + platform.Height;

                float difference = _playerY - top;
                if (difference < 0f)
                {
                    difference = -difference;
                }

                if (difference > PlatformerRules.GroundSnapTolerance)
                {
                    continue;
                }

                if (!PlatformerRules.HorizontalOverlap(
                    _playerX,
                    PlatformerRules.PlayerWidth,
                    platform.X,
                    platform.Width))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// 重力与垂直积分：<c>v -= g·dt</c>（下落封顶 18 m/s），位移取 <b>新旧速度的梯形平均 × dt</b>，
        /// 然后尝试落到平台顶面。
        /// <para>
        /// <b>为什么用梯形平均而不是半隐式欧拉 <c>y += v_new·dt</c></b>：恒定加速度下梯形积分是<b>精确</b>的
        /// （位移 <c>= v_old·dt - ½g·dt²</c>），采样点全部落在连续抛物线上，起跳顶点 1.4545 米 / 滞空 0.727 秒
        /// 与解析值只差一个采样粒度；半隐式欧拉会系统性压低顶点，把"跳上 2.2 米平台"这类判定卡在误差带上。
        /// </para>
        /// </summary>
        private void AdvanceVertical(float deltaSeconds)
        {
            if (_isOnGround)
            {
                return;
            }

            float previousY = _playerY;

            float nextVelocity = _playerVelocityY - (PlatformerRules.Gravity * deltaSeconds);
            if (nextVelocity < -PlatformerRules.MaxFallSpeed)
            {
                nextVelocity = -PlatformerRules.MaxFallSpeed;
            }

            float halfDeltaSeconds = 0.5f * deltaSeconds;
            _playerY += (_playerVelocityY + nextVelocity) * halfDeltaSeconds;
            _playerVelocityY = nextVelocity;

            ResolveLanding(previousY);
        }

        /// <summary>
        /// 单向落地：只认"上一子步脚底不低于平台顶面（<c>previousY &gt;= top - ε</c>）且本子步穿到顶面之下"
        /// 的平台，因此从下方顶上去、贴边横穿都不会被挡住。
        /// <para>
        /// 一帧内同时穿过多块平台时取<b>最高的顶面</b>（先撞上的那块），避免高速下落时落到更低的平台上。
        /// </para>
        /// </summary>
        private void ResolveLanding(float previousY)
        {
            float landingTop = 0f;
            bool landed = false;

            for (int i = 0; i < _platformCount; i++)
            {
                PlatformerView platform = _platforms[i];
                float top = platform.Y + platform.Height;

                if (_playerY > top)
                {
                    continue;
                }

                if (previousY < top - PlatformerRules.LandingEpsilon)
                {
                    continue;
                }

                if (!PlatformerRules.HorizontalOverlap(
                    _playerX,
                    PlatformerRules.PlayerWidth,
                    platform.X,
                    platform.Width))
                {
                    continue;
                }

                if (landed && top <= landingTop)
                {
                    continue;
                }

                landingTop = top;
                landed = true;
            }

            if (!landed)
            {
                return;
            }

            _playerY = landingTop;
            _playerVelocityY = 0f;
            _isOnGround = true;
        }

        /// <summary>
        /// 收集品拾取：玩家 AABB（0.8 × 1.6）与收集品判定盒（0.5 × 0.5，中心为收集品坐标）相交即收集，
        /// 每颗 <c>+10</c> 分。已收集的槽位直接跳过，因此来回走动不会重复计分。
        /// </summary>
        private void CollectOverlappedCollectibles()
        {
            for (int i = 0; i < _collectibleCount; i++)
            {
                CollectibleView collectible = _collectibles[i];
                if (collectible.Collected)
                {
                    continue;
                }

                bool overlaps = PlatformerRules.BoxesOverlap(
                    _playerX,
                    _playerY,
                    PlatformerRules.PlayerWidth,
                    PlatformerRules.PlayerHeight,
                    collectible.X - PlatformerRules.CollectibleHalfExtent,
                    collectible.Y - PlatformerRules.CollectibleHalfExtent,
                    PlatformerRules.CollectibleHalfExtent * 2f,
                    PlatformerRules.CollectibleHalfExtent * 2f);

                if (!overlaps)
                {
                    continue;
                }

                _collectibles[i] = new CollectibleView(
                    collectible.CollectibleId,
                    collectible.X,
                    collectible.Y,
                    true);

                _collectedCount++;
                _score += PlatformerRules.ScorePerCollectible;
            }
        }

        /// <summary>
        /// 终局判定：脚底低于 <c>-10</c> 米判负（掉出世界）；站在地面上且左沿越过终点线判胜。
        /// 两条互斥，先判负——否则"掉出世界时恰好还在终点线上"会被误判成胜利。
        /// </summary>
        private void ResolveTerminalState()
        {
            if (_playerY < PlatformerRules.FallOutY)
            {
                _isVictory = false;
                _isFinished = true;
                return;
            }

            if (_isOnGround && _playerX >= _goalX)
            {
                _isVictory = true;
                _isFinished = true;
            }
        }
    }
}
