using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Numerics;
using AIFramework.Contracts.Replay;
using AIFramework.Core.Replay;
using AIFramework.Kernel.Realtime;

namespace AIFramework.Core.Tests.Replay
{
    /// <summary>
    /// 一局强实时战斗的确定性布场：同一种子 → 完全相同的初始单位/飞行物。
    /// <para>
    /// <b>为什么种子要参与布场</b>：<see cref="BattleWorld"/> 当前不消费 <see cref="GameplayInitData.RandomSeed"/>，
    /// 而"不同种子 → 指纹不同"必须由可观测状态体现。因此这里把种子映射成布场偏移，
    /// 让种子成为真实状态的一部分；回放时用同一种子重建，初始状态才能逐位一致。
    /// </para>
    /// <para>
    /// 布场脚本（单位位置、半径、血量、飞行物初速）属于**关卡数据**，不进入录像体：
    /// <see cref="ReplayHeader"/> 只存种子/关卡/布场数量，回放方用同一种子跑同一份布场脚本重建。
    /// 这是本轮"内存内录制/重放"的明确边界，录像落盘序列化留待后续。
    /// </para>
    /// </summary>
    internal sealed class BattleRig
    {
        private BattleRig(int seed)
        {
            Spatial = new MockSpatialQuery();

            GameplayInitData init = new GameplayInitData(ReplayHarness.LevelId, seed, 2);
            World = new BattleWorld(in init, Spatial, Array.Empty<ISkillInteractionPhase>());
            Spatial.Bind(World);

            // 种子 → 布场偏移（0.05 的整数倍，101/202 这类相邻种子即可产生可观测差异）。
            float seedOffset = (seed & 0xFF) * 0.05f;

            Hero = Spatial.SpawnUnit(
                new Vec2(seedOffset, 0f),
                0.5f,
                3,
                100,
                GameplayTagMask.Empty.Add(GameplayTags.UnitShieldActive));
            Enemy = Spatial.SpawnUnit(new Vec2(seedOffset + 2f, 0f), 0.5f, 1, 40, GameplayTagMask.Empty);

            // 朝主角飞的飞行物：第 9 帧被反射后反向飞离，全程不会命中。
            Incoming = World.SpawnProjectile(
                Enemy,
                new Vec2(seedOffset, 3f),
                new Vec2(0f, -1f),
                5,
                GameplayTagMask.Empty);

            // 朝敌人飞的飞行物：约第 30 帧命中，驱动 MockSpatialQuery + 命中伤害链路。
            Impact = World.SpawnProjectile(
                Hero,
                new Vec2(seedOffset + 2f, -2f),
                new Vec2(0f, 2.5f),
                6,
                GameplayTagMask.Empty);

            // 远离战场的匀速飞行物：用来验证"飞行物位置/速度"也进入每帧指纹。
            Escaping = World.SpawnProjectile(
                Enemy,
                new Vec2(seedOffset + 10f, 10f),
                new Vec2(1f, 0f),
                1,
                GameplayTagMask.Empty);
        }

        internal BattleWorld World { get; }

        internal MockSpatialQuery Spatial { get; }

        internal UnitHandle Hero { get; }

        internal UnitHandle Enemy { get; }

        internal ProjectileHandle Incoming { get; }

        internal ProjectileHandle Impact { get; }

        internal ProjectileHandle Escaping { get; }

        internal static BattleRig Create(int seed)
        {
            return new BattleRig(seed);
        }
    }

    /// <summary>
    /// 固定输入脚本：按帧号产出一局"有意义"的战斗输入（四类意图全覆盖）。
    /// 录制期由本脚本驱动；回放期完全不再执行本脚本，改为消费录像。
    /// </summary>
    internal static class BattleScript
    {
        internal static void Apply(BattleRig rig, int frame, IIntentWriter writer)
        {
            switch (frame)
            {
                case 0:
                    writer.Submit(new MoveIntent(rig.Hero, new Vec2(0.25f, 0f)));
                    writer.Submit(new ControlIntent(rig.Enemy, 1, 6));
                    break;
                case 6:
                    writer.Submit(new DamageIntent(rig.Hero, rig.Enemy, 5, 2));
                    break;
                case 9:
                    writer.Submit(new ReflectProjectileIntent(rig.Hero, rig.Incoming));
                    break;
                case 12:
                    writer.Submit(new DamageIntent(rig.Enemy, rig.Hero, 2, 1));
                    break;
                case 18:
                    writer.Submit(new MoveIntent(rig.Enemy, new Vec2(0f, -0.5f)));
                    break;
                case 20:
                    writer.Submit(new DamageIntent(rig.Hero, rig.Enemy, 3, 0));
                    break;
                case 25:
                    writer.Submit(new ReflectProjectileIntent(rig.Enemy, rig.Escaping));
                    break;
                case 30:
                    writer.Submit(new MoveIntent(rig.Hero, new Vec2(0f, 0.25f)));
                    writer.Submit(new ControlIntent(rig.Hero, 2, 4));
                    break;
                case 40:
                    writer.Submit(new DamageIntent(rig.Enemy, rig.Hero, 4, 0));
                    break;
                case 55:
                    writer.Submit(new MoveIntent(rig.Hero, new Vec2(-0.5f, 0f)));
                    break;
                case 70:
                    writer.Submit(new DamageIntent(rig.Hero, rig.Enemy, 5, 2));
                    break;
                case 90:
                    writer.Submit(new MoveIntent(rig.Enemy, new Vec2(0.25f, 0.25f)));
                    break;
                case 105:
                    writer.Submit(new DamageIntent(rig.Enemy, rig.Hero, 1, 1));
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// 录像协议的数值打包：<see cref="IReplaySink"/> / <see cref="IReplaySource"/> 的 a..d 只有 4 个 int。
    /// <list type="bullet">
    /// <item>句柄打包成 <c>(generation &amp; 0x7FFF) &lt;&lt; 16 | (index &amp; 0xFFFF)</c>，0 表示无效句柄；
    /// 保留世代号，回放才能识别"槽位复用后的旧句柄"。</item>
    /// <item>位移用 IEEE-754 位模式（<see cref="BitConverter.SingleToInt32Bits"/>）而非定点量化，
    /// 保证录制期提交的 float 与回放期提交的 float 完全同一位，录制对内核零侵入。</item>
    /// <item>kind 固定为 1=Control 2=Move 3=Damage 4=Reflect，与 Contracts 注释一致。</item>
    /// </list>
    /// 已知上限：单位下标 &lt; 65536、世代号 &lt; 32768（BattleWorld 槽位上限 1024，实战不会触达）。
    /// </summary>
    internal static class BattleReplayCodec
    {
        internal const int KindControl = 1;

        internal const int KindMove = 2;

        internal const int KindDamage = 3;

        internal const int KindReflect = 4;

        private const int IndexMask = 0xFFFF;

        private const int GenerationMask = 0x7FFF;

        internal static int PackUnit(UnitHandle handle)
        {
            if (!handle.IsValid)
            {
                return 0;
            }

            return ((handle.Generation & GenerationMask) << 16) | (handle.Index & IndexMask);
        }

        internal static int PackProjectile(ProjectileHandle handle)
        {
            if (!handle.IsValid)
            {
                return 0;
            }

            return ((handle.Generation & GenerationMask) << 16) | (handle.Index & IndexMask);
        }

        internal static UnitHandle UnpackUnit(int packed)
        {
            if (packed <= 0)
            {
                return default;
            }

            return new UnitHandle(packed & IndexMask, (packed >> 16) & GenerationMask);
        }

        internal static ProjectileHandle UnpackProjectile(int packed)
        {
            if (packed <= 0)
            {
                return default;
            }

            return new ProjectileHandle(packed & IndexMask, (packed >> 16) & GenerationMask);
        }

        internal static int FloatToBits(float value)
        {
            return BitConverter.SingleToInt32Bits(value);
        }

        internal static float BitsToFloat(int bits)
        {
            return BitConverter.Int32BitsToSingle(bits);
        }

        /// <summary>把一条录像解码回内核意图并按原帧重新提交（未知 kind 直接失败，禁止静默跳过）。</summary>
        internal static void Submit(BattleWorld world, in RecordedIntent record)
        {
            switch (record.Kind)
            {
                case KindControl:
                    world.Submit(new ControlIntent(UnpackUnit(record.A), record.B, record.C));
                    break;
                case KindMove:
                    world.Submit(new MoveIntent(
                        UnpackUnit(record.A),
                        new Vec2(BitsToFloat(record.B), BitsToFloat(record.C))));
                    break;
                case KindDamage:
                    world.Submit(new DamageIntent(
                        UnpackUnit(record.A),
                        UnpackUnit(record.B),
                        record.C,
                        record.D));
                    break;
                case KindReflect:
                    world.Submit(new ReflectProjectileIntent(
                        UnpackUnit(record.A),
                        UnpackProjectile(record.B)));
                    break;
                default:
                    throw new InvalidOperationException("未知的录像意图类型码：" + record.Kind);
            }
        }
    }

    /// <summary>
    /// 录制通道包装：先按真实路径提交给内核，再记录到 <see cref="IReplaySink"/>。
    /// 内核拿到的仍然是原生意图，完全不知道回放在发生。
    /// </summary>
    internal sealed class RecordingIntentWriter : IIntentWriter
    {
        private readonly BattleWorld _world;

        private readonly IReplaySink _sink;

        private int _frame;

        internal RecordingIntentWriter(BattleWorld world, IReplaySink sink)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            _world = world;
            _sink = sink;
            _frame = 0;
        }

        internal void BeginFrame(int frame)
        {
            _frame = frame;
        }

        public void Submit(in ControlIntent intent)
        {
            _world.Submit(in intent);
            _sink.Record(
                _frame,
                BattleReplayCodec.KindControl,
                BattleReplayCodec.PackUnit(intent.Unit),
                intent.ControlFlags,
                intent.DurationFrames,
                0);
        }

        public void Submit(in MoveIntent intent)
        {
            _world.Submit(in intent);
            _sink.Record(
                _frame,
                BattleReplayCodec.KindMove,
                BattleReplayCodec.PackUnit(intent.Unit),
                BattleReplayCodec.FloatToBits(intent.DeltaPosition.X),
                BattleReplayCodec.FloatToBits(intent.DeltaPosition.Y),
                0);
        }

        public void Submit(in DamageIntent intent)
        {
            _world.Submit(in intent);
            _sink.Record(
                _frame,
                BattleReplayCodec.KindDamage,
                BattleReplayCodec.PackUnit(intent.Source),
                BattleReplayCodec.PackUnit(intent.Target),
                intent.Amount,
                intent.BreakPower);
        }

        public void Submit(in ReflectProjectileIntent intent)
        {
            _world.Submit(in intent);
            _sink.Record(
                _frame,
                BattleReplayCodec.KindReflect,
                BattleReplayCodec.PackUnit(intent.Reflector),
                BattleReplayCodec.PackProjectile(intent.Projectile),
                0,
                0);
        }
    }

    /// <summary>录制 / 回放两条驱动链：共用同一帧步长与同一份指纹口径。</summary>
    internal static class ReplayHarness
    {
        internal const float StepSeconds = 1f / 60f;

        internal const int LevelId = 7;

        /// <summary>跑录制局：每帧先执行输入脚本，再推进一帧，落一帧指纹。</summary>
        internal static long[] Record(BattleRig rig, ReplayRecorder recorder, int frames)
        {
            RecordingIntentWriter writer = new RecordingIntentWriter(rig.World, recorder);
            long[] fingerprints = new long[frames];

            for (int frame = 0; frame < frames; frame++)
            {
                writer.BeginFrame(frame);
                BattleScript.Apply(rig, frame, writer);
                rig.World.Step(StepSeconds);
                fingerprints[frame] = StateFingerprint.Of(rig.World, rig.World);
            }

            return fingerprints;
        }

        /// <summary>只跑脚本不录制（用于对照实验）。</summary>
        internal static long[] StepScripted(BattleRig rig, int frames)
        {
            long[] fingerprints = new long[frames];

            for (int frame = 0; frame < frames; frame++)
            {
                BattleScript.Apply(rig, frame, rig.World);
                rig.World.Step(StepSeconds);
                fingerprints[frame] = StateFingerprint.Of(rig.World, rig.World);
            }

            return fingerprints;
        }

        /// <summary>跑回放局：每帧先消费当前帧录像，再推进一帧，落一帧指纹。</summary>
        internal static long[] Replay(BattleRig rig, ReplayPlayer player, int frames)
        {
            long[] fingerprints = new long[frames];

            for (int frame = 0; frame < frames; frame++)
            {
                while (player.TryNext(rig.World.FrameId, out RecordedIntent record))
                {
                    BattleReplayCodec.Submit(rig.World, in record);
                }

                rig.World.Step(StepSeconds);
                player.AdvanceFrame();
                fingerprints[frame] = StateFingerprint.Of(rig.World, rig.World);
            }

            return fingerprints;
        }
    }
}
