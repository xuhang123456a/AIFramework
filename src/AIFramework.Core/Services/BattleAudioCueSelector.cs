#nullable enable
using System;

namespace AIFramework.Core.Services
{
    /// <summary>
    /// 一帧战斗状态快照（由适配层从只读视图填，纯数据）。
    /// <para>
    /// <b>为什么是"计数快照"而不是"事件列表"</b>：强实时内核<b>不发布事件</b>（帧内禁止碰 CQE 总线），
    /// 适配层能拿到的只有 <c>IUnitWorldReadView</c> / <c>IProjectileWorldReadView</c> / <c>IRealtimeSandbox</c>
    /// 的当前值。把"能读到的数"原样搬进这个结构体，帧差（发射/受击/死亡）由纯逻辑选择器去算，
    /// 适配层就只剩"填数字 + 把 cue 转成 <c>PlaySfx</c>"两件事，能脱离 Unity 单测。
    /// </para>
    /// <para>
    /// <b>字段口径由适配层定义</b>：内核没有"伤害/死亡"计数器（只暴露单位/飞行物只读视图与反射计数），
    /// 这些值只能由适配层按"上一帧 → 这一帧"的差推断，所以同一份选择器语义在不同适配层里
    /// 只要口径一致就能复现；本文档只声明"这几个数是什么形状"，不声明它们怎么来。
    /// </para>
    /// </summary>
    public readonly struct BattleAudioFrame
    {
        /// <summary>拍一帧战斗快照。</summary>
        /// <param name="aliveUnits">存活单位数（不参与本帧判定，见 <see cref="AliveUnits"/>）。</param>
        /// <param name="projectiles">场上飞行物数量（与上一帧比较即可推出"发射"）。</param>
        /// <param name="shieldActive">本帧护盾生效强度（&gt; 0 表示护盾这一帧真的吃到了攻击）。</param>
        /// <param name="damageTaken">本帧受到的伤害量（&gt; 0 表示这一帧有单位掉血/护盾吸收）。</param>
        /// <param name="deaths">本帧死亡单位数（&gt; 0 表示这一帧有阵亡）。</param>
        public BattleAudioFrame(int aliveUnits, int projectiles, int shieldActive, int damageTaken, int deaths)
        {
            AliveUnits = aliveUnits;
            Projectiles = projectiles;
            ShieldActive = shieldActive;
            DamageTaken = damageTaken;
            Deaths = deaths;
        }

        /// <summary>
        /// 存活单位数。<b>不参与选择器的判定</b>：它是快照的"现场温度"，
        /// 留给上层做诊断或将来按人数分级（例如人少时降低打击感音量），当前规则用不到它——
        /// 阵亡由 <see cref="Deaths"/> 直接给出，避免"选择器自己再算一次差值"两处口径打架。
        /// </summary>
        public int AliveUnits { get; }

        /// <summary>场上飞行物数量。</summary>
        public int Projectiles { get; }

        /// <summary>本帧护盾生效强度（&gt; 0 表示护盾这一帧吃到了攻击）。</summary>
        public int ShieldActive { get; }

        /// <summary>本帧受到的伤害量（&gt; 0 表示这一帧有单位掉血或护盾吸收）。</summary>
        public int DamageTaken { get; }

        /// <summary>本帧死亡单位数。</summary>
        public int Deaths { get; }
    }

    /// <summary>
    /// 音效 cue id 常量（适配层映射到实际资源；Core 只发 id）。
    /// <para>
    /// <b>为什么 id 写死在这里而不是配置表</b>：这四个是"战斗事实"级别的固定槽位，
    /// 换皮/换项目时改的是同一 ID 对应的 <c>Resources/Audio/&lt;id&gt;</c> 资源，而不是代码里的语义；
    /// 走查表反而会让"缺表 = 整局战斗静音"。
    /// </para>
    /// <para>
    /// <b>为什么从 1001 开始</b>：与演示工程的 BGM/UI 音效（1001 以下）分区，
    /// 资源命名冲突时一眼能看出是"战斗打击音"。
    /// </para>
    /// </summary>
    public static class BattleAudioCues
    {
        /// <summary>发射（飞行物出膛；同帧多发合并成一条）。</summary>
        public const int Shot = 1001;

        /// <summary>受击（有单位掉血，且当帧护盾没吃到攻击）。</summary>
        public const int Hit = 1002;

        /// <summary>格挡（护盾当帧吃到了攻击；优先级高于 <see cref="Hit"/>）。</summary>
        public const int Shield = 1003;

        /// <summary>死亡（有单位阵亡；同帧多死合并成一条）。</summary>
        public const int Death = 1004;
    }

    /// <summary>
    /// 把"帧间状态变化"翻成音效 cue，带节流（同一 cue 每 N 秒最多一次）与去重。
    /// <para>
    /// <b>为什么要放在 Core 而不是适配层</b>：规则本身是纯算术（差值 + 计时 + 优先级上限），
    /// 与引擎无关；一旦让它长在 <c>MonoBehaviour</c> 里，"同帧三连击只响一声"这种语义就只能靠真机听感回归。
    /// 放在 Core 后，<c>BattleAudioCueSelectorTests</c> 的 28 条无头用例能把每一条规则钉死。
    /// </para>
    /// <para>
    /// <b>"去重"的三层含义</b>（都由本类负责，调用方不需要再做）：
    /// <list type="number">
    /// <item><b>同帧同类合并</b>：一帧发射 5 发飞行物只出 1 条 <see cref="BattleAudioCues.Shot"/>，
    /// 同帧死 3 个单位只出 1 条 <see cref="BattleAudioCues.Death"/>（世界状态只给了计数，本来也分不出先后）。</item>
    /// <item><b>同 cue 节流</b>：<c>minSecondsBetweenSameCue</c> 窗口内同类 cue 只发一次，
    /// 防"连射/连击"把 8 条音源池打爆（Unity 侧池满会抢占最早起播的那条，听感是声音被切断）。</item>
    /// <item><b>每帧上限</b>：最多 <c>maxCuesPerFrame</c> 条，按 Death &gt; Shield &gt; Hit &gt; Shot 取，
    /// 超出的丢弃并计入 <see cref="SuppressedCount"/>——"这一帧死了人"永远比"这一帧开了枪"更该被听见。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>零分配</b>：<see cref="Update"/> 只写调用方传入的 <see cref="Span{T}"/>，
    /// 内部是定长 <c>float[4]</c> 计时器 + 4 个 <c>int</c> 帧差，没有任何 <c>List</c>/闭包/装箱；
    /// 预热后逐帧调用的托管堆增量是 0 字节。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：非线程安全。只在主线程/逻辑帧线程上调用，与 <c>Step</c> 同线程。
    /// </para>
    /// </summary>
    public sealed class BattleAudioCueSelector
    {
        /// <summary>cue 种类数（Death / Shield / Hit / Shot）。计时器按下标存，不建字典。</summary>
        private const int CueKindCount = 4;

        /// <summary>死亡在计时器数组里的下标（也即优先级最高的一档）。</summary>
        private const int DeathKind = 0;

        /// <summary>格挡在计时器数组里的下标。</summary>
        private const int ShieldKind = 1;

        /// <summary>受击在计时器数组里的下标。</summary>
        private const int HitKind = 2;

        /// <summary>发射在计时器数组里的下标（优先级最低）。</summary>
        private const int ShotKind = 3;

        /// <summary>同一 cue 的最小间隔（秒）；&lt;= 0 表示不节流。</summary>
        private readonly float _minSecondsBetweenSameCue;

        /// <summary>每帧最多发几条；&lt;= 0 表示本帧一条都不发（全部计入抑制数）。</summary>
        private readonly int _maxCuesPerFrame;

        /// <summary>各类 cue 的剩余冷却秒数，下标见 <see cref="DeathKind"/> 等常量。</summary>
        private readonly float[] _cooldowns;

        /// <summary>上一帧的飞行物数量，用来算"这一帧是不是有新弹出膛"。</summary>
        private int _previousProjectiles;

        /// <summary>生命周期累计发出的 cue 条数（<see cref="Reset"/> 不清）。</summary>
        private int _emitted;

        /// <summary>生命周期累计被抑制的 cue 条数（<see cref="Reset"/> 不清）。</summary>
        private int _suppressed;

        /// <summary>
        /// 创建选择器。
        /// </summary>
        /// <param name="minSecondsBetweenSameCue">
        /// 同一 cue 的最小间隔（秒）。默认 0.05 秒 ≈ 3 帧（60 fps）：既能保住"连击有节奏感"，
        /// 又不会让同一次连击在 8 条音源池里互相抢占。传 0 或负数表示关闭节流。
        /// </param>
        /// <param name="maxCuesPerFrame">
        /// 每帧最多发几条。默认 2：一帧里同时出现"死人 + 格挡"时两条都该响，
        /// 再多就会糊成一片。传 0 或负数表示本帧一条都不发。
        /// </param>
        public BattleAudioCueSelector(float minSecondsBetweenSameCue = 0.05f, int maxCuesPerFrame = 2)
        {
            // NaN 通过 "> 0" 判定为 false，自动退化成"不节流"，不需要单独判 NaN。
            _minSecondsBetweenSameCue = minSecondsBetweenSameCue > 0f ? minSecondsBetweenSameCue : 0f;
            _maxCuesPerFrame = maxCuesPerFrame > 0 ? maxCuesPerFrame : 0;
            _cooldowns = new float[CueKindCount];
        }

        /// <summary>生命周期累计发出的 cue 条数（<see cref="Reset"/> 不回退，便于断言"这一局到底响了几声"）。</summary>
        public int EmittedCount
        {
            get { return _emitted; }
        }

        /// <summary>
        /// 生命周期累计被抑制的 cue 条数。抑制来源有三类，都记在这里：
        /// <list type="bullet">
        /// <item>同类 cue 还在节流窗口内；</item>
        /// <item>本帧已发满 <c>maxCuesPerFrame</c> 条（低优先级的被丢弃）；</item>
        /// <item>调用方给的 <see cref="Span{T}"/> 比本帧上限还小（写不进去的按丢弃处理，绝不越界写）。</item>
        /// </list>
        /// </summary>
        public int SuppressedCount
        {
            get { return _suppressed; }
        }

        /// <summary>
        /// 喂一帧；返回本帧应播的 cue（写入调用方缓冲，返回条数）。
        /// <para>
        /// <b>输出顺序 = 优先级顺序</b>（Death → Shield → Hit → Shot）：
        /// 适配层若只想播第一条（例如池只剩一条空闲音源），取 <c>cues[0]</c> 就是最该被听见的那条。
        /// </para>
        /// </summary>
        /// <param name="frame">本帧状态快照；负数计数按 0 处理（不抛，见类注释的"只降级不打断"纪律）。</param>
        /// <param name="deltaSeconds">距上一帧的秒数；<c>&lt;= 0</c>（含 <c>NaN</c>）按 0 处理，计时器不前进。</param>
        /// <param name="cues">输出缓冲；容量不足时按容量截断，不扩容、不越界写。</param>
        /// <returns>实际写入 <paramref name="cues"/> 的条数（0..min(maxCuesPerFrame, cues.Length)）。</returns>
        public int Update(in BattleAudioFrame frame, float deltaSeconds, Span<int> cues)
        {
            AdvanceCooldowns(deltaSeconds);

            // 帧差只认"这一帧的数"与"上一帧的数"：先把两个计数口径都归一化，规则本身就不必再判负数。
            int projectiles = Sanitize(frame.Projectiles);
            int damageTaken = Sanitize(frame.DamageTaken);
            int shieldActive = Sanitize(frame.ShieldActive);
            int deaths = Sanitize(frame.Deaths);

            // 发射：只看"变多了"。多出来的那几发是同一帧出膛的，合并不出第二条（见类注释"同帧发射合并"）。
            bool shot = projectiles > _previousProjectiles;
            _previousProjectiles = projectiles;

            // 格挡优先：护盾当帧确实吃到了攻击时只发 Shield，不再发 Hit——
            // 两声响叠在一起会听成"格挡没挡住"，反而让玩家误判。
            bool shield = shieldActive > 0 && damageTaken > 0;
            bool hit = damageTaken > 0 && shieldActive <= 0;
            bool death = deaths > 0;

            // 调用方缓冲比配置上限小时以缓冲为准：写不进就是丢弃，绝不越界写。
            int capacity = _maxCuesPerFrame < cues.Length ? _maxCuesPerFrame : cues.Length;

            int written = 0;
            TryEmit(DeathKind, BattleAudioCues.Death, death, cues, ref written, capacity);
            TryEmit(ShieldKind, BattleAudioCues.Shield, shield, cues, ref written, capacity);
            TryEmit(HitKind, BattleAudioCues.Hit, hit, cues, ref written, capacity);
            TryEmit(ShotKind, BattleAudioCues.Shot, shot, cues, ref written, capacity);

            return written;
        }

        /// <summary>
        /// 清帧差与节流计时，<b>不清</b> <see cref="EmittedCount"/> / <see cref="SuppressedCount"/>。
        /// <para>
        /// <b>为什么这样切</b>：换波/换局要的是"不要拿上一局的飞行物数量当基线"，
        /// 而"这一局一共响了几声"是生命周期诊断值，清掉就再也查不出"为什么这么安静"。
        /// </para>
        /// </summary>
        public void Reset()
        {
            _previousProjectiles = 0;
            Array.Clear(_cooldowns, 0, _cooldowns.Length);
        }

        /// <summary>
        /// 推进节流计时器。<b>负数/<c>NaN</c> 的 delta 一律按 0</b>：
        /// 时间倒流只会让计时器越减越多，把"刚响过"的 cue 永久卡死。
        /// </summary>
        /// <param name="deltaSeconds">距上一帧的秒数。</param>
        private void AdvanceCooldowns(float deltaSeconds)
        {
            if (float.IsNaN(deltaSeconds) || deltaSeconds < 0f)
            {
                deltaSeconds = 0f;
            }

            for (int i = 0; i < _cooldowns.Length; i++)
            {
                float remaining = _cooldowns[i] - deltaSeconds;

                // +Infinity 的 delta 会把剩余量打成 -Infinity，这里统一夹回 0，避免下一次比较拿到怪值。
                _cooldowns[i] = remaining > 0f ? remaining : 0f;
            }
        }

        /// <summary>
        /// 尝试写入一条 cue：候选 → 冷却 → 容量三道闸，任一不过就只记抑制数。
        /// <para>
        /// <b>为什么冷却检查在容量检查之前</b>：被节流的那条不占用本帧名额。
        /// 否则"Death 刚响过还在冷却"会把 Hit 的名额也吃掉，实际听感是"该响的没响"。
        /// </para>
        /// </summary>
        /// <param name="kind">cue 种类下标（同时是冷却计时器的下标）。</param>
        /// <param name="cue">要写入的 cue id。</param>
        /// <param name="candidate">本帧是否触发了这一类事实。</param>
        /// <param name="cues">输出缓冲。</param>
        /// <param name="written">已写入条数（按引用累加）。</param>
        /// <param name="capacity">本帧允许写入的上限。</param>
        private void TryEmit(int kind, int cue, bool candidate, Span<int> cues, ref int written, int capacity)
        {
            if (!candidate)
            {
                return;
            }

            if (_cooldowns[kind] > 0f)
            {
                _suppressed++;
                return;
            }

            if (written >= capacity)
            {
                _suppressed++;
                return;
            }

            cues[written] = cue;
            written++;

            // 只有真的写出去才起冷却：被丢弃的那条下一次仍然有机会响。
            _cooldowns[kind] = _minSecondsBetweenSameCue;
            _emitted++;
        }

        /// <summary>非法计数按 0 处理（音频是表现层，绝不允许一个负数把战斗流程炸掉）。</summary>
        /// <param name="value">视图里读到的原始计数。</param>
        /// <returns>非负计数。</returns>
        private static int Sanitize(int value)
        {
            return value > 0 ? value : 0;
        }
    }
}
