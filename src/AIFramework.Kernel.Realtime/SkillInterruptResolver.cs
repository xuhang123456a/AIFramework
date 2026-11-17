namespace AIFramework.Kernel.Realtime
{
    /// <summary>
    /// 打断仲裁器：唯一的"霸体 vs 打断力"判定入口，禁止各切片自行比较。
    /// 语义写死为 <c>breakPower &gt; armorLevel</c> 才打断；<b>相等不打断</b>。
    /// </summary>
    public static class SkillInterruptResolver
    {
        /// <summary>判定一次伤害是否打断目标。</summary>
        /// <param name="armorLevel">目标霸体等级。</param>
        /// <param name="breakPower">本次伤害的打断力。</param>
        /// <returns>打断力严格大于霸体等级时返回 <c>true</c>；相等或更低返回 <c>false</c>。</returns>
        public static bool ShouldInterrupt(int armorLevel, int breakPower)
        {
            return breakPower > armorLevel;
        }
    }
}
