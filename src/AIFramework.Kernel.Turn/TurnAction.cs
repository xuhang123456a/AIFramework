namespace AIFramework.Kernel.Turn
{
    /// <summary>
    /// 回合动作：纯值类型，只描述"这一回合打谁"，不含任何执行结果。
    /// 真正结算由 <see cref="TurnWorld.Step(float)"/> 统一完成，动作本身不可变。
    /// </summary>
    public readonly struct TurnAction
    {
        public TurnAction(int targetIndex)
        {
            TargetIndex = targetIndex;
        }

        /// <summary>目标敌人在内部预分配数组中的下标；越界或目标已阵亡时由内核回退到首个存活敌人。</summary>
        public int TargetIndex { get; }
    }
}
