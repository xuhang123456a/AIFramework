using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Kernel.Turn
{
    /// <summary>
    /// 回合 / 状态流玩法沙盒（<see cref="GameplayKernelType.Turn"/>）。
    /// <para>
    /// 推演模型：外部把 <see cref="TurnAction"/> 投入预分配环形队列；每次 <see cref="Step(float)"/>
    /// 出队一个动作，对目标敌人造成 1 点伤害；队列空则打首个存活敌人。
    /// </para>
    /// <para>
    /// 零分配约束：敌人血量数组与动作环形队列都在构造期一次分配，<see cref="Step(float)"/> 内
    /// 不申请任何托管堆内存；堆数组只在 <see cref="CollectResult"/> 里按结算结果分配。
    /// </para>
    /// </summary>
    public sealed class TurnWorld : IGameplaySession
    {
        /// <summary>回合动作环形队列的定长容量：满则丢弃新动作并计一次溢出。</summary>
        private const int ActionQueueCapacity = 64;

        /// <summary>每个敌人的初始血量。</summary>
        private const int EnemyInitialHp = 2;

        /// <summary>敌人血量，长度等于 <see cref="GameplayInitData.UnitCount"/>，构造期一次分配。</summary>
        private readonly int[] _enemyHp;

        /// <summary>动作环形队列本体，构造期一次分配。</summary>
        private readonly TurnAction[] _actions;

        private readonly int _maxSteps;

        /// <summary>环形队列头下标。</summary>
        private int _queueHead;

        /// <summary>环形队列当前元素数。</summary>
        private int _queueCount;

        private int _droppedActionCount;

        private int _processedSteps;

        private bool _resultCollected;

        public TurnWorld(in GameplayInitData init)
        {
            int enemyCount = init.UnitCount > 0 ? init.UnitCount : 0;

            _enemyHp = new int[enemyCount];
            for (int i = 0; i < _enemyHp.Length; i++)
            {
                _enemyHp[i] = EnemyInitialHp;
            }

            // MaxSteps = UnitCount * 3，至少 1（UnitCount <= 0 时同样兜底为 1，保证会话必然可结束）。
            long requestedSteps = (long)enemyCount * 3L;
            _maxSteps = requestedSteps > 1L ? (int)requestedSteps : 1;

            _actions = new TurnAction[ActionQueueCapacity];
            _queueHead = 0;
            _queueCount = 0;
            _droppedActionCount = 0;
            _processedSteps = 0;
            _resultCollected = false;
        }

        /// <summary>步数上限：<c>init.UnitCount * 3</c>，至少 1。</summary>
        public int MaxSteps
        {
            get { return _maxSteps; }
        }

        /// <summary>当前存活敌人数（HP &gt; 0）。</summary>
        public int AliveCount
        {
            get
            {
                int alive = 0;
                for (int i = 0; i < _enemyHp.Length; i++)
                {
                    if (_enemyHp[i] > 0)
                    {
                        alive++;
                    }
                }

                return alive;
            }
        }

        /// <summary>已推演步数。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>因环形队列已满而被丢弃的动作总数。</summary>
        public int DroppedActionCount
        {
            get { return _droppedActionCount; }
        }

        /// <summary>
        /// 投入一个回合动作。队列满时丢弃该动作并累加 <see cref="DroppedActionCount"/>，
        /// 不扩容、不抛异常（环形容器大小固定，帧内零分配）。
        /// </summary>
        public void Enqueue(in TurnAction action)
        {
            if (_queueCount >= ActionQueueCapacity)
            {
                _droppedActionCount++;
                return;
            }

            int tail = _queueHead + _queueCount;
            if (tail >= ActionQueueCapacity)
            {
                tail -= ActionQueueCapacity;
            }

            _actions[tail] = action;
            _queueCount++;
        }

        /// <summary>
        /// 推演一个回合：出队一个动作结算 1 点伤害；队列空则打首个存活敌人。
        /// 已结束的会话再调用本方法为空操作（不推进 <see cref="ProcessedSteps"/>）。
        /// </summary>
        public void Step(float deltaSeconds)
        {
            if (IsFinished)
            {
                return;
            }

            int target = -1;
            if (_queueCount > 0)
            {
                TurnAction action = _actions[_queueHead];
                _queueHead++;
                if (_queueHead >= ActionQueueCapacity)
                {
                    _queueHead = 0;
                }

                _queueCount--;

                int requested = action.TargetIndex;
                if (requested >= 0 && requested < _enemyHp.Length && _enemyHp[requested] > 0)
                {
                    target = requested;
                }
            }

            if (target < 0)
            {
                target = FindFirstAliveIndex();
            }

            if (target >= 0)
            {
                _enemyHp[target] = _enemyHp[target] - 1;
            }

            _processedSteps++;
        }

        /// <summary>全部敌人 HP &lt;= 0，或已推演步数触达上限 <see cref="MaxSteps"/>。</summary>
        public bool IsFinished
        {
            get { return AliveCount == 0 || _processedSteps >= _maxSteps; }
        }

        /// <summary>
        /// 取回结算报告。未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// 数组只在这里按结果分配一次。
        /// </summary>
        public GameplayResult CollectResult()
        {
            if (_resultCollected)
            {
                throw new InvalidOperationException("GameplayResult 已取回，会话所有权已交还，禁止二次结算。");
            }

            if (!IsFinished)
            {
                throw new InvalidOperationException("对局尚未结束，禁止取回 GameplayResult。");
            }

            _resultCollected = true;

            int alive = AliveCount;
            bool isVictory = alive == 0;

            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(1001, 1) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _processedSteps,
                alive,
                Array.Empty<GameplayTag>(),
                loots);
        }

        /// <summary>取首个 HP &gt; 0 的敌人下标；无存活敌人返回 -1。</summary>
        private int FindFirstAliveIndex()
        {
            for (int i = 0; i < _enemyHp.Length; i++)
            {
                if (_enemyHp[i] > 0)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
