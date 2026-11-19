#nullable enable
using System;
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Rhythm
{
    /// <summary>
    /// <see cref="RhythmWorld"/> 的时间推进与判定端口（<c>IRhythmInput</c> + 内核私有判定逻辑）。
    /// <para>
    /// <b>一次 <see cref="Tick(double)"/> 的顺序</b>：先把 <c>deltaSeconds</c> 累进歌曲时钟，
    /// 再（<see cref="RhythmWorld.AutoPlay"/> 开启时）把已进入 ±0.045s 完美窗的音符自动打掉，
    /// 最后扫描"已越过 <c>HitTime + 0.14s</c> 仍未判定"的音符并自动判 Miss。
    /// 顺序不影响正确性（完美窗 ⊂ 判定窗，两个扫描互不覆盖），但固定下来保证 AutoPlay 逐帧可复现。
    /// </para>
    /// <para>
    /// <b>为什么 <see cref="Tick(double)"/> 忽略 0/负数/NaN</b>：适配层在暂停、切后台或首帧可能送来
    /// 0 或异常的帧时间；回拨歌曲时钟会让已结算的音符"复活"（与单调的 <see cref="RhythmWorld.IsFinished"/>
    /// 冲突），因此非正数与 NaN 一律当作"本帧不推进"，而不是"倒带"。
    /// </para>
    /// <para>
    /// <b>零分配</b>：这里只有 double/int/枚举运算与对定长数组的读写，不申请任何托管堆内存。
    /// </para>
    /// </summary>
    public sealed partial class RhythmWorld
    {
        /// <summary>
        /// 推进一个时间片（秒）。对局结束后为空操作；<c>deltaSeconds</c> 为 0/负数/NaN 时同样为空操作。
        /// </summary>
        public void Tick(double deltaSeconds)
        {
            if (IsFinished || !(deltaSeconds > 0.0))
            {
                return;
            }

            _songTime += deltaSeconds;

            if (AutoPlay)
            {
                AutoPlayNotes();
            }

            ResolveMissedNotes();
        }

        /// <summary>
        /// 在 <paramref name="lane"/> 轨道上击打一次：从该轨道<b>尚未判定</b>的音符里挑
        /// <c>|SongTime - HitTimeSeconds|</c> 最小的一个结算。
        /// <para>
        /// <b>命中</b>（≤0.14s）：写入判定、累计各计数、连击 +1、刷新最高连击，返回该判定。<br/>
        /// <b>空击</b>（该轨道没有未判定音符，或最近的一个也超过 0.14s）：返回
        /// <see cref="RhythmJudgement.Miss"/>，<b>连击归零但不消耗音符、不改任何计数</b>（见类型注释的统计口径）。
        /// </para>
        /// <para>
        /// 对局结束后返回 <see cref="RhythmJudgement.Miss"/> 且完全不改状态（连击也冻结）；
        /// 越界轨道（不在 <c>0..3</c>）按"空轨道"处理，同样只断连击。
        /// 本方法在不确定"有没有音符"时也可以安全调用，适配层不需要先查询视图。
        /// </para>
        /// </summary>
        public RhythmJudgement Hit(int lane)
        {
            if (IsFinished)
            {
                return RhythmJudgement.Miss;
            }

            if (lane < 0 || lane >= RhythmChart.LaneCount)
            {
                _combo = 0;
                return RhythmJudgement.Miss;
            }

            int index = FindNearestUnresolved(lane);
            if (index < 0)
            {
                _combo = 0;
                return RhythmJudgement.Miss;
            }

            double delta = Math.Abs(_songTime - _notes[index].HitTimeSeconds);
            RhythmJudgement judgement;
            if (delta <= PerfectWindowSeconds)
            {
                judgement = RhythmJudgement.Perfect;
            }
            else if (delta <= GreatWindowSeconds)
            {
                judgement = RhythmJudgement.Great;
            }
            else if (delta <= GoodWindowSeconds)
            {
                judgement = RhythmJudgement.Good;
            }
            else
            {
                // 最近的一个都在判定窗外：不消耗它，只断连击。
                _combo = 0;
                return RhythmJudgement.Miss;
            }

            ResolveNote(index, judgement);
            return judgement;
        }

        /// <summary>
        /// 在 <paramref name="lane"/> 上找 <c>|SongTime - HitTimeSeconds|</c> 最小的未判定音符下标；
        /// 该轨道一个未判定音符都没有时返回 <c>-1</c>。
        /// <para>
        /// <b>为什么"最近"一定会被判定窗筛掉远音符</b>：谱面的相邻音符间距 ≥ 0.42s（见 <see cref="RhythmChart"/>），
        /// 同一轨道里任何"非最近"的音符都至少比最近的那个远 0.42s，因此只要最近的那个在窗外，
        /// 其余必然也在窗外，无需再逐个比较。
        /// </para>
        /// </summary>
        private int FindNearestUnresolved(int lane)
        {
            int bestIndex = -1;
            double bestDelta = double.MaxValue;

            for (int i = 0; i < _noteCount; i++)
            {
                ref RhythmNote note = ref _notes[i];
                if (note.Resolved || note.Lane != lane)
                {
                    continue;
                }

                double delta = Math.Abs(_songTime - note.HitTimeSeconds);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        /// <summary>
        /// 结算一个音符（命中与漏打共用）：写判定、累加 <see cref="_resolvedCount"/> 与对应计数，
        /// Miss 把连击归零，命中则连击 +1 并刷新 <see cref="_maxCombo"/>。
        /// <para>同一个音符只会被结算一次（调用方都先挡了 <see cref="RhythmNote.Resolved"/>），所以计数器不会重复累加。</para>
        /// </summary>
        private void ResolveNote(int index, RhythmJudgement judgement)
        {
            ref RhythmNote note = ref _notes[index];
            note.Resolved = true;
            note.Judgement = judgement;
            _resolvedCount++;

            switch (judgement)
            {
                case RhythmJudgement.Perfect:
                    _perfectCount++;
                    break;
                case RhythmJudgement.Great:
                    _greatCount++;
                    break;
                case RhythmJudgement.Good:
                    _goodCount++;
                    break;
                default:
                    _missCount++;
                    break;
            }

            if (judgement == RhythmJudgement.Miss)
            {
                _combo = 0;
                return;
            }

            _combo++;
            if (_combo > _maxCombo)
            {
                _maxCombo = _combo;
            }
        }

        /// <summary>
        /// 漏打扫描：把所有"歌曲时间已越过 <c>HitTime + 0.14s</c>（严格大于）且仍未判定"的音符判为 Miss。
        /// <para>
        /// <b>边界是严格大于</b>：<c>SongTime - HitTime == 0.14</c> 的那一刻音符仍然可打（Good），
        /// 这样"判定窗 ≤0.14s"与"自动漏打 &gt;0.14s"两处对同一个数取到了同一侧，不会出现
        /// "恰好卡在边界上既不能打也不判 Miss"的空洞。
        /// </para>
        /// </summary>
        private void ResolveMissedNotes()
        {
            for (int i = 0; i < _noteCount; i++)
            {
                ref RhythmNote note = ref _notes[i];
                if (note.Resolved)
                {
                    continue;
                }

                if (_songTime - note.HitTimeSeconds > GoodWindowSeconds)
                {
                    ResolveNote(i, RhythmJudgement.Miss);
                }
            }
        }

        /// <summary>
        /// AutoPlay 扫描：把已进入 ±0.045s 完美窗的未判定音符通过 <see cref="Hit(int)"/> 打掉
        /// （走同一条判定路径，因此连击/计数/准确率与手动完美演奏完全一致）。
        /// <para>
        /// <b>为什么是"逐音符调 Hit"而不是直接写 Perfect</b>：这样自动播放验证的是真实判定链路，
        /// 一旦判定窗或最近音符选择被改坏，AutoPlay 的全 Perfect 断言会立刻失败。
        /// </para>
        /// </summary>
        private void AutoPlayNotes()
        {
            for (int i = 0; i < _noteCount; i++)
            {
                ref RhythmNote note = ref _notes[i];
                if (note.Resolved)
                {
                    continue;
                }

                if (Math.Abs(_songTime - note.HitTimeSeconds) > PerfectWindowSeconds)
                {
                    continue;
                }

                int lane = note.Lane;
                Hit(lane);
            }
        }
    }
}
