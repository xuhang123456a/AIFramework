#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Rhythm
{
    /// <summary>
    /// 音游沙盒（T2-13，<see cref="GameplayKernelType.Turn"/>）：Kernel.Turn 的又一套卡带内容，
    /// 验证"时间轴 + 判定窗口 + 连击/准确率"这类<b>由真实时间驱动的节奏玩法</b>仍然能挂在同一套回合进出门上。
    /// <para>
    /// <b>为什么复用 <see cref="GameplayKernelType.Turn"/> 而不是新增枚举值</b>：卡带的进出门只有
    /// <see cref="IGameplaySession"/> 四个成员（推进、结束、结算），音游与俄罗斯方块/推箱子对主板而言完全同构；
    /// 本卡带的 <c>CollectResult</c> 固定报 <see cref="GameplayKernelType.Turn"/>，装配层不需要为它扩容枚举与存档协议。
    /// </para>
    /// <para>
    /// <b>规则（Phase 1，测试逐条验证）</b>：
    /// 4 条轨道、内置 2 个谱面（见 <see cref="RhythmChart"/>，<b>谱面后续由配置表 / Luban 提供</b>）——
    /// 谱面 1 是 30 个音符 / 15 秒，谱面 2 是 50 个音符 / 25 秒；<see cref="GameplayInitData.LevelId"/> 为 2 时选谱面 2，
    /// 其它值（含 0/9/负数）一律夹到谱面 1，并由 <see cref="LevelId"/> 回报夹取后的值。
    /// 判定窗口相对 <c>|SongTime - note.HitTimeSeconds|</c>：≤0.045s Perfect、≤0.09s Great、≤0.14s Good，
    /// 再远不算命中；<see cref="Tick(double)"/> 把越过 <c>HitTime + 0.14s</c> 仍未判定的音符自动判 Miss（连击归零），
    /// <see cref="Hit(int)"/> 只从<b>该轨道未判定</b>的音符里挑时间最近的那个。
    /// </para>
    /// <para>
    /// <b>空击与漏打是两件事（刻意的统计口径）</b>：判定窗外的 <see cref="Hit(int)"/> 返回
    /// <see cref="RhythmJudgement.Miss"/> 并<b>把连击归零</b>，但<b>不消耗音符、也不写任何计数</b>——
    /// 四个计数只统计"已结算的音符"，否则 <c>Perfect + Great + Good + Miss</c> 与
    /// <see cref="Accuracy"/> 的分母会对不上（空击没有对应音符可计入）。真正的漏打只由
    /// <see cref="Tick(double)"/> 在时间越过判定窗后自动写入 <see cref="MissCount"/>。
    /// </para>
    /// <para>
    /// <b>Accuracy 口径</b>：<c>(Perfect + Great*0.8 + Good*0.5) / 已结算音符数</c>。未判定音符不进分母
    /// （规则 5 的原文），因此局中准确率会随结算推进而波动；一局结束时所有音符都已结算，分母等于
    /// <see cref="NoteCount"/>。一个都还没结算时返回 <c>0</c>（"没有判定就没有准确率"），
    /// 避免出现"刚开局就 100%"这种会被 UI 误读的读数。
    /// </para>
    /// <para>
    /// <b>AutoPlay</b>（演示用，确定性）：为 <c>true</c> 时每次 <see cref="Tick(double)"/>
    /// 都对"已进入 ±0.045s 完美窗"的音符自动调一次 <see cref="Hit(int)"/>，等价于逐帧完美演奏。
    /// 适配层只要以 ≤0.09s 的步长驱动 <see cref="Tick(double)"/>（1/60s 远小于该上限），
    /// 每个音符都必然被至少一帧扫进完美窗，因此全曲结果恒为全 Perfect。
    /// </para>
    /// <para>
    /// <b>Step 语义</b>：<c>Step(dt) = Tick(dt) + ProcessedSteps++</c>；对局结束后为空操作、不再累加。
    /// </para>
    /// <para>
    /// <b>零分配</b>：音符定长数组在构造期按谱面音符数一次分配，<see cref="Tick(double)"/> /
    /// <see cref="Hit(int)"/> / <see cref="Step(float)"/> 全程只读数组、只做 double/int 运算，
    /// 不申请任何托管堆内存；只有 <see cref="CollectResult"/> 按胜负分配一次战利品数组
    /// （<see cref="GameplayResult"/> 契约要求一次构造）。
    /// </para>
    /// <para>
    /// <b>文件拆分</b>：时间推进、判定、AutoPlay 与漏打扫描在 <c>RhythmWorld.Input.cs</c>；
    /// 本文件只保留状态、只读视图、推进一步与结算。两者都不超过 400 行，评审可以一次读完。
    /// </para>
    /// </summary>
    public sealed partial class RhythmWorld : IGameplaySession, IRhythmReadView, IRhythmInput
    {
        /// <summary>Perfect 判定窗（秒）：<c>|delta| ≤ 0.045</c>。</summary>
        internal const double PerfectWindowSeconds = 0.045;

        /// <summary>Great 判定窗（秒）：<c>|delta| ≤ 0.09</c>。</summary>
        internal const double GreatWindowSeconds = 0.09;

        /// <summary>Good 判定窗（秒），也是"还算命中"的最大偏差：<c>|delta| ≤ 0.14</c>。</summary>
        internal const double GoodWindowSeconds = 0.14;

        /// <summary>Perfect 的准确率权重。</summary>
        private const double PerfectWeight = 1.0;

        /// <summary>Great 的准确率权重。</summary>
        private const double GreatWeight = 0.8;

        /// <summary>Good 的准确率权重（Miss 权重恒为 0，不参与求和）。</summary>
        private const double GoodWeight = 0.5;

        /// <summary>胜利门槛：准确率 ≥ 0.6。</summary>
        private const double VictoryAccuracy = 0.6;

        /// <summary>胜利战利品模板 ID（与其它回合卡带一致）。</summary>
        private const int VictoryLootItemId = 1003;

        /// <summary>胜利战利品数量。</summary>
        private const int VictoryLootCount = 1;

        /// <summary>音符定长数组（构造期一次分配，运行期只改 <see cref="RhythmNote"/> 的两个字段）。</summary>
        private readonly RhythmNote[] _notes;

        /// <summary>本谱面实际音符数（≤ <see cref="_notes"/> 长度）。</summary>
        private readonly int _noteCount;

        /// <summary>曲长（秒），末音符之后还留有 &gt; 0.14s 的尾巴（见 <see cref="RhythmChart"/>）。</summary>
        private readonly double _songLengthSeconds;

        /// <summary>夹取后的谱面 ID（1 或 2）。</summary>
        private readonly int _levelId;

        /// <summary>歌曲时钟（秒），只增不减。</summary>
        private double _songTime;

        /// <summary>已结算音符数（命中 + 漏打），同时是 <see cref="Accuracy"/> 的分母。</summary>
        private int _resolvedCount;

        private int _combo;

        private int _maxCombo;

        private int _perfectCount;

        private int _greatCount;

        private int _goodCount;

        private int _missCount;

        private int _processedSteps;

        private bool _resultCollected;

        public RhythmWorld(in GameplayInitData init)
        {
            _levelId = RhythmChart.NormalizeChartId(init.LevelId);
            _noteCount = RhythmChart.NoteCount(_levelId);
            _notes = new RhythmNote[_noteCount];
            _songLengthSeconds = RhythmChart.SongLengthSeconds(_levelId);

            RhythmChart.Build(_levelId, init.RandomSeed, _notes);

            _songTime = 0.0;
            _resolvedCount = 0;
            _combo = 0;
            _maxCombo = 0;
            _perfectCount = 0;
            _greatCount = 0;
            _goodCount = 0;
            _missCount = 0;
            _processedSteps = 0;
            _resultCollected = false;
            AutoPlay = false;
        }

        /// <summary>
        /// 演示用自动播放：为 <c>true</c> 时每次 <see cref="Tick(double)"/> 都会把已进入完美窗的音符
        /// 自动打掉（确定性，全曲 Perfect）。默认 <c>false</c>。
        /// </summary>
        public bool AutoPlay { get; set; }

        /// <summary>夹取后的谱面 ID（<c>1</c> 或 <c>2</c>）：<c>LevelId == 2</c> 选谱面 2，其余一律 1。</summary>
        public int LevelId
        {
            get { return _levelId; }
        }

        /// <summary>曲长（秒）：谱面 1 = 15，谱面 2 = 25。</summary>
        public double SongLengthSeconds
        {
            get { return _songLengthSeconds; }
        }

        /// <summary>当前歌曲时间（秒）：由 <see cref="Tick(double)"/> 累加，从 0 开始。</summary>
        public double SongTime
        {
            get { return _songTime; }
        }

        /// <summary>轨道数（固定 4）。</summary>
        public int LaneCount
        {
            get { return RhythmChart.LaneCount; }
        }

        /// <summary>本谱面音符数（谱面 1 = 30，谱面 2 = 50）。</summary>
        public int NoteCount
        {
            get { return _noteCount; }
        }

        /// <summary>当前连击：命中 +1，漏打或判定窗外的空击归零。</summary>
        public int Combo
        {
            get { return _combo; }
        }

        /// <summary>本局最高连击（只增不减，结算时写入 <see cref="GameplayResult.RemainingUnits"/>）。</summary>
        public int MaxCombo
        {
            get { return _maxCombo; }
        }

        /// <summary>Perfect 命中数。</summary>
        public int PerfectCount
        {
            get { return _perfectCount; }
        }

        /// <summary>Great 命中数。</summary>
        public int GreatCount
        {
            get { return _greatCount; }
        }

        /// <summary>Good 命中数。</summary>
        public int GoodCount
        {
            get { return _goodCount; }
        }

        /// <summary>漏打数（只由时间越过判定窗时自动结算，不含判定窗外的空击）。</summary>
        public int MissCount
        {
            get { return _missCount; }
        }

        /// <summary>
        /// 准确率 <c>0..1</c>：<c>(Perfect + Great*0.8 + Good*0.5) / 已结算音符数</c>；
        /// 尚未结算任何音符时返回 <c>0</c>。未判定音符不进分母（见类型注释）。
        /// </summary>
        public double Accuracy
        {
            get
            {
                if (_resolvedCount == 0)
                {
                    return 0.0;
                }

                double weighted = (_perfectCount * PerfectWeight)
                    + (_greatCount * GreatWeight)
                    + (_goodCount * GoodWeight);

                return weighted / _resolvedCount;
            }
        }

        /// <summary>累计推演步数，与结算报告里的 <see cref="GameplayResult.StepCount"/> 同源同值。</summary>
        public int ProcessedSteps
        {
            get { return _processedSteps; }
        }

        /// <summary>
        /// 对局是否结束：<b>歌曲时间越过曲长</b>且<b>所有音符都已结算</b>，或结算报告已被取回。
        /// 两个条件缺一不可（规则 3）：先把音符打完但没到曲尾不算结束，反过来时间到了但还有音符没结算也不算。
        /// </summary>
        public bool IsFinished
        {
            get
            {
                return _resultCollected
                    || (_songTime > _songLengthSeconds && _resolvedCount == _noteCount);
            }
        }

        /// <summary>
        /// 按时间升序取第 <paramref name="index"/> 个音符的只读快照（0 起）。
        /// <para>未判定音符的 <see cref="RhythmNoteView.Judgement"/> 是哨兵值 <c>(RhythmJudgement)byte.MaxValue</c>。</para>
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">下标不在 <c>0..NoteCount-1</c> 内。</exception>
        public RhythmNoteView GetNote(int index)
        {
            if (index < 0 || index >= _noteCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    "音符下标越界：合法区间是 0..NoteCount-1。");
            }

            RhythmNote note = _notes[index];
            RhythmJudgement judgement = note.Resolved ? note.Judgement : RhythmNote.UnresolvedJudgement;
            return new RhythmNoteView(note.NoteId, note.Lane, note.HitTimeSeconds, judgement);
        }

        /// <summary>
        /// 推演一步 = <see cref="Tick(double)"/> + <see cref="ProcessedSteps"/> +1。
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
        /// 取回结算报告：未结束或第二次调用一律抛 <see cref="InvalidOperationException"/>。
        /// <para>
        /// 胜负口径：<c>Accuracy &gt;= 0.6</c> 判胜，胜利战利品为 <c>new LootItem(1003, 1)</c>，
        /// 失败为空战利品；<see cref="GameplayResult.KernelType"/> 固定
        /// <see cref="GameplayKernelType.Turn"/>（本卡带不新增枚举值），
        /// <see cref="GameplayResult.RemainingUnits"/> 取 <see cref="MaxCombo"/>（音游没有兵力/资源，
        /// 这里复用该字段承载"本局最高连击"这种同样单调的局末读数）。
        /// </para>
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

            bool isVictory = Accuracy >= VictoryAccuracy;
            IReadOnlyList<LootItem> loots = isVictory
                ? new LootItem[] { new LootItem(VictoryLootItemId, VictoryLootCount) }
                : Array.Empty<LootItem>();

            return new GameplayResult(
                GameplayKernelType.Turn,
                isVictory,
                _processedSteps,
                _maxCombo,
                Array.Empty<GameplayTag>(),
                loots);
        }
    }
}
