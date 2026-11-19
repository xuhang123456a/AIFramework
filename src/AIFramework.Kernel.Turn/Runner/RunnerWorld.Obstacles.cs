#nullable enable
using System;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Runner
{
    /// <summary>
    /// <see cref="RunnerWorld"/> 的障碍管线（<c>partial</c>）：生成节奏、出生点、按速度后移、回收与碰撞结算。
    /// <para>
    /// <b>为什么单独拆文件</b>：<c>RunnerWorld.cs</c> 已经承载"状态 + 只读视图 + 推进 + 结算"，
    /// 再塞进障碍生命周期就会超过 400 行。拆的是方法不是真值：槽位数组 <c>_obstacles</c> 与计数
    /// <c>_obstacleCount</c> 仍声明在 <c>RunnerWorld.cs</c>，对外也仍然只有一个 <c>RunnerWorld</c>。
    /// </para>
    /// <para>
    /// <b>零分配</b>：本文件只对定长数组做就地改写（<c>_obstacles[i].X -= ...</c>、整体前移一格、置 <c>default</c>），
    /// 不申请任何托管堆内存，也不产生任何装箱。
    /// </para>
    /// <para>
    /// <b>回收口径</b>：越过玩家 <c>x &lt; -2</c> 才回收，而不是 <c>x &lt; 0</c> 就回收——
    /// 碰撞窗是 <c>|x| ≤ 0.6</c>，多留 1.4 米给渲染层补间与"刚撞上"的特写，避免障碍在判定完成的同一帧消失。
    /// </para>
    /// </summary>
    public sealed partial class RunnerWorld
    {
        /// <summary>把所有障碍按当前速度向后推 <paramref name="advance"/> 米，并回收越过 <c>x &lt; -2</c> 的槽位。</summary>
        private void AdvanceObstacles(float advance)
        {
            for (int i = 0; i < _obstacleCount; i++)
            {
                _obstacles[i].X -= advance;
            }

            // 倒序遍历：移除时后面的障碍整体前移一格，不会跳过还没检查的槽位。
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
        /// 生成节奏：累加器每攒满 <see cref="RunnerRules.SpawnIntervalSeconds"/> 就生成一个。
        /// <para>
        /// 一帧最多补齐 <see cref="RunnerRules.MaxObstacles"/> 个（槽位上限），避免超大 <c>deltaSeconds</c>
        /// 把循环撑成几百上千次；补齐后累加器若仍溢出，会在后续帧继续补，不会永久卡住。
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
    }
}
