#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 战斗音效桥：把"内核状态帧差"翻成 cue，再落到 <see cref="IAudioService.PlaySfx"/>。
    /// <para>
    /// <b>为什么是普通 C# 类而不是 MonoBehaviour</b>：本框架禁止业务逻辑依赖引擎生命周期回调
    /// （见 AGENTS.md 三·4）；桥只需要一个 <see cref="IAudioService"/> 和一个纯逻辑选择器，
    /// 由 <see cref="ArenaController"/> 持有并在 <c>Update</c> 里显式 <see cref="Tick"/>，
    /// 因此它不会在场景卸载/暂停的时序里偷偷继续跑，也能被无头测试直接构造。
    /// </para>
    /// <para>
    /// <b>它只管"翻译"，不管"怎么算"</b>：发射/受击/格挡/死亡四条规则、节流与每帧上限全在
    /// <see cref="BattleAudioCueSelector"/> 里（Core，可无头单测）。本类唯一的职责边界是
    /// "cue id → <see cref="IAudioService.PlaySfx"/>"，不允许在这里加第二条玩法规则。
    /// </para>
    /// <para>
    /// <b>零分配</b>：cue 缓冲是构造期一次性分配的 <c>int[4]</c> 成员，逐帧只是
    /// <c>selector.Update(..., _cueBuffer)</c> + 几次虚调用；预热后 <see cref="Tick"/> 的托管堆增量是 0 字节。
    /// </para>
    /// </summary>
    public sealed class BattleAudioBridge
    {
        /// <summary>
        /// cue 缓冲容量。<b>为什么是 4</b>：cue 种类只有四种，一帧最多四类各一条；
        /// 选择器的 <c>maxCuesPerFrame</c> 通常远小于它，缓冲只是"绝不给逐帧路径 new 数组"的兜底。
        /// </summary>
        private const int CueBufferCapacity = 4;

        /// <summary>构造期注入的音频端口（<see cref="Tick"/> 参数为 <c>null</c> 时回退到它）。</summary>
        private readonly IAudioService _audio;

        /// <summary>纯逻辑选择器：帧差 + 节流 + 优先级上限。</summary>
        private readonly BattleAudioCueSelector _selector;

        /// <summary>逐帧复用的 cue 缓冲（唯一的可变引用成员，构造后不再分配）。</summary>
        private readonly int[] _cueBuffer;

        /// <summary>
        /// 创建音效桥。
        /// </summary>
        /// <param name="audio">音频端口（通常是装配层的 <c>UnityAudioService</c>）。</param>
        /// <param name="selector">选择器；换局时由调用方 <c>Reset()</c>，不要在这里换实例。</param>
        /// <exception cref="ArgumentNullException"><paramref name="audio"/> 或 <paramref name="selector"/> 为 <c>null</c>。</exception>
        public BattleAudioBridge(IAudioService audio, BattleAudioCueSelector selector)
        {
            if (audio == null)
            {
                throw new ArgumentNullException(nameof(audio));
            }

            if (selector == null)
            {
                throw new ArgumentNullException(nameof(selector));
            }

            _audio = audio;
            _selector = selector;
            _cueBuffer = new int[CueBufferCapacity];
        }

        /// <summary>
        /// 喂一帧：选择器算出本帧该播的 cue，逐条转成 <see cref="IAudioService.PlaySfx"/>。
        /// <para>
        /// <b>为什么端口既在构造期注入、又在参数里收一次</b>：契约签名要求如此，且实际带来了两个好处——
        /// 热更/换设备后可以临时换一条服务重注入；测试里也能在同一桥实例上喂"会记录调用的假服务"。
        /// 参数为 <c>null</c> 时回退构造期那一条，因此正常路径永远不会空引用。
        /// </para>
        /// </summary>
        /// <param name="audio">本帧要用的音频端口；传 <c>null</c> 表示用构造期注入的那条。</param>
        /// <param name="frame">本帧状态快照（由 <see cref="BattleAudioFrameSampler"/> 从只读视图填）。</param>
        /// <param name="deltaSeconds">距上一帧的秒数（传逻辑帧长，不要传 <c>Time.deltaTime</c>：
        /// 内核按固定步长推进，节流窗口要对齐逻辑时间而不是渲染帧率）。</param>
        public void Tick(IAudioService audio, in BattleAudioFrame frame, float deltaSeconds)
        {
            IAudioService target = audio != null ? audio : _audio;

            // Span 只在这里临时包一层，不产生分配；selector 内部不会把它存起来。
            int count = _selector.Update(in frame, deltaSeconds, _cueBuffer);
            for (int i = 0; i < count; i++)
            {
                target.PlaySfx(_cueBuffer[i]);
            }
        }
    }

    /// <summary>
    /// 把只读视图拼成 <see cref="BattleAudioFrame"/> 的采样器（适配层内部使用）。
    /// <para>
    /// <b>为什么单独一个类、而且放在适配层</b>：核心难点不在"数数"，而在"内核没有的事件只能靠帧差推断"——
    /// 这份推断口径必须写在一处并被注释清楚，不能散在 <c>ArenaController.Update</c> 里。
    /// 选择器只接受现成的计数（可单测），采样器负责把视图翻译成计数（必须真机验证）。
    /// </para>
    /// <para>
    /// <b>本采样器定义的口径</b>（内核不发布事件，以下全部是推断值）：
    /// <list type="bullet">
    /// <item><c>AliveUnits</c>：<c>Hp &gt; 0</c> 的单位数（内核槽位不回收，死单位仍占 <c>Count</c>，不能直接用它）。</item>
    /// <item><c>Projectiles</c>：存活飞行物数（内核出界/命中即回收槽位，<c>Count</c> 就是活着的）。</item>
    /// <item><c>Deaths</c>：上一帧存活数 − 本帧存活数（正数才记）；同帧死多个也只会得到 1 条 Death cue（由选择器合并）。</item>
    /// <item><c>ShieldActive</c>：本帧 <c>IRealtimeSandbox.ReflectedCount</c> 的增量（护盾这一帧吃到了几次攻击）。</item>
    /// <item><c>DamageTaken</c>：本帧总血量损失 + 护盾吸收次数。
    /// <b>为什么护盾吸收要并进来</b>：被反射掉的那一发不会掉血，若只按血量差分，则
    /// "DamageTaken &gt; 0 且 ShieldActive &gt; 0 → Shield"这条规则永远不成立，格挡音效等于死代码；
    /// 而"被打中"与"被护盾吃掉"在听感上都属于"受到攻击"，合并计数才让 Hit/Shield 两条 cue 都能真正响。
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>换波/换局不需要 Reset</b>：所有差值都做了"负数按 0"的钳制——新会话的
    /// <c>ReflectedCount</c> 从 0 重新开始、总血量从满血重新开始、存活数从 0 变多，
    /// 三种跳变都会被钳成 0，不会凭空放出一条 Death。只有"想丢掉旧基线"时才需要显式 <see cref="Reset"/>。
    /// </para>
    /// </summary>
    internal sealed class BattleAudioFrameSampler
    {
        /// <summary>是否已经采过至少一帧；第一帧没有"上一帧"，只能填绝对量。</summary>
        private bool _hasPrevious;

        /// <summary>上一帧的存活单位数。</summary>
        private int _previousAliveUnits;

        /// <summary>上一帧的全场总血量（含主角与所有敌人）。</summary>
        private int _previousTotalHp;

        /// <summary>上一帧的累计反射次数（会话内单调递增，换会话会归零）。</summary>
        private int _previousReflected;

        /// <summary>
        /// 采一帧。
        /// </summary>
        /// <param name="units">单位只读视图；<c>null</c> 表示内核没暴露（此时存活数按 0 计）。</param>
        /// <param name="projectiles">飞行物只读视图；<c>null</c> 同上。</param>
        /// <param name="sandbox">布场端口（同时是 <c>ReflectedCount</c> 的读口）；<c>null</c> 表示读不到反射数。</param>
        /// <returns>本帧快照。</returns>
        public BattleAudioFrame Capture(
            IUnitWorldReadView? units,
            IProjectileWorldReadView? projectiles,
            IRealtimeSandbox? sandbox)
        {
            int aliveUnits = 0;
            int totalHp = 0;

            if (units != null)
            {
                int unitCount = units.Count;
                for (int i = 0; i < unitCount; i++)
                {
                    UnitReadView view = units[i];
                    if (view.Hp > 0)
                    {
                        aliveUnits++;
                        totalHp += view.Hp;
                    }
                }
            }

            int reflected = sandbox != null ? sandbox.ReflectedCount : 0;
            if (reflected < 0)
            {
                reflected = 0;
            }

            int shieldActive = 0;
            int damageTaken = 0;
            int deaths = 0;

            if (_hasPrevious)
            {
                // 掉血：总血量差分。任何一方掉血都算"受击"（反射弹打敌人同样该有打击感）。
                int hpLoss = _previousTotalHp - totalHp;
                if (hpLoss > 0)
                {
                    damageTaken += hpLoss;
                }

                // 格挡：反射增量。负增量（换会话）钳成 0，同时把吸收次数并进"受到攻击"总量。
                int reflectDelta = reflected - _previousReflected;
                if (reflectDelta > 0)
                {
                    shieldActive = reflectDelta;
                    damageTaken += reflectDelta;
                }

                // 阵亡：存活数差分。同帧死三个只会得到 3，选择器把它合并成一条 Death。
                int deathDelta = _previousAliveUnits - aliveUnits;
                if (deathDelta > 0)
                {
                    deaths = deathDelta;
                }
            }

            _previousAliveUnits = aliveUnits;
            _previousTotalHp = totalHp;
            _previousReflected = reflected;
            _hasPrevious = true;

            int liveProjectiles = projectiles != null ? projectiles.Count : 0;
            return new BattleAudioFrame(aliveUnits, liveProjectiles, shieldActive, damageTaken, deaths);
        }

        /// <summary>丢掉基线（下一次 <see cref="Capture"/> 当作第一帧，不产出任何差值）。</summary>
        public void Reset()
        {
            _hasPrevious = false;
            _previousAliveUnits = 0;
            _previousTotalHp = 0;
            _previousReflected = 0;
        }
    }
}
