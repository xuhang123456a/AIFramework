#nullable enable
namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 一次特效播放请求：业务只发"在哪个位置播哪个特效"，播放池/合批/回收由适配层决定。
    /// <para>
    /// <b>为什么是只读 struct + 逻辑 ID</b>：特效与音频同属"表现层"，业务切片不应该持有
    /// <c>ParticleSystem</c>、材质、预制体这类引擎类型（AIF002）。<see cref="EffectId"/> 由配置表或
    /// 命名约定映射到具体资源（Unity 侧约定为 <c>Resources/Vfx/&lt;id&gt;</c>），因此切片里既没有
    /// 引擎引用，也不依赖"资源已打进包"这一事实。
    /// </para>
    /// <para>
    /// <b>为什么坐标用裸 <c>float</c> 而不是 <c>Vec3</c></b>：本契约只服务 2D UI / 2D 战场表现
    /// （签到、开箱、领取特效），z 轴恒为 0；引入三维成熟值类型会让"UI 上撒个特效"这类调用
    /// 多出一次 Z 值决策。真要做 3D 世界特效时应新增独立契约，而不是把这个结构体改成万能箱。
    /// </para>
    /// <para>
    /// <b>字段语义</b>
    /// <list type="bullet">
    /// <item><see cref="DurationSeconds"/> 由<u>调用方</u>决定而不是读资源时长：技能/UI 演出需要
    /// "提前 0.2 秒收掉"这种裁剪，读资源时长反而做不到；0 或负值属于非法请求，实现必须静默丢弃。</item>
    /// <item><see cref="Scale"/> 是统一缩放（相对资源原始尺寸），不是像素尺寸——分辨率/DPI 由 Canvas 决定。</item>
    /// </list>
    /// </para>
    /// </summary>
    public readonly struct VfxRequest
    {
        /// <summary>构造一条特效请求。</summary>
        /// <param name="effectId">特效逻辑 ID（必须 &gt; 0，实现方按非法参数静默拒绝）。</param>
        /// <param name="x">播放位置的 X（UI/战场坐标，单位由调用方约定）。</param>
        /// <param name="y">播放位置的 Y。</param>
        /// <param name="scale">统一缩放倍率（必须 &gt; 0）。</param>
        /// <param name="durationSeconds">期望播放时长（秒，必须 &gt; 0）；到期后由适配层回收。</param>
        public VfxRequest(int effectId, float x, float y, float scale, float durationSeconds)
        {
            EffectId = effectId;
            X = x;
            Y = y;
            Scale = scale;
            DurationSeconds = durationSeconds;
        }

        /// <summary>特效逻辑 ID。</summary>
        public int EffectId { get; }

        /// <summary>播放位置 X。</summary>
        public float X { get; }

        /// <summary>播放位置 Y。</summary>
        public float Y { get; }

        /// <summary>统一缩放倍率。</summary>
        public float Scale { get; }

        /// <summary>期望播放时长（秒）。</summary>
        public float DurationSeconds { get; }
    }

    /// <summary>
    /// 特效服务端口：业务只发"播什么"，不关心播放池大小、合批、资源加载与回收策略。
    /// <para>
    /// <b>为什么不抛异常</b>：与 <see cref="IAudioService"/> 同一套纪律——特效缺失/参数非法属于
    /// 表现问题，实现方只记录日志并降级。一个漏打包的特效绝不允许打断结算、领奖或场景切换。
    /// </para>
    /// <para>
    /// <b><see cref="ActiveCount"/> 的口径</b>：当前"正在播"的特效数量（不是历史累计播放次数），
    /// 供诊断与并发上限验收；实现可以是精确值，也可以是文档里写明的近似值
    /// （无头实现没有资源也没有时钟，只能估算）。
    /// </para>
    /// </summary>
    public interface IVfxService
    {
        /// <summary>播放一次特效（不阻塞调用方）。</summary>
        /// <param name="request">
        /// 只读请求（<c>in</c> 传参，不复制结构体）。坐标/缩放/时长非法或资源缺失时实现必须静默丢弃，
        /// 不得抛异常，也不得把该次请求计入 <see cref="ActiveCount"/>。
        /// </param>
        void Play(in VfxRequest request);

        /// <summary>当前正在播放的特效数量（诊断用；不包含因资源缺失而未生成的请求）。</summary>
        int ActiveCount { get; }

        /// <summary>立刻回收全部正在播放的特效（切场景/断线回大厅时清场，可重复调用）。</summary>
        void Clear();
    }
}
