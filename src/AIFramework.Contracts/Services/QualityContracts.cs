#nullable enable

namespace AIFramework.Contracts.Services
{
    /// <summary>
    /// 画质档位：<b>Low &lt; Medium &lt; High</b>，数值越大画质越高、开销越大。
    /// <para>
    /// <b>为什么档位是"三档枚举"而不是连续质量系数</b>：连续值看起来精细，但落地时会立刻退化成
    /// "业务每次改一点、渲染管线各理解一点"的散装参数（纹理档、阴影档、后处理档互相打架）。
    /// 三档是"玩家能感知、QA 能穷举、适配层能一次写全"的最小集合；真的需要更细的档位时，
    /// 也是在档位与 <see cref="QualityProfile"/> 的映射表里加行，而不是把连续值塞进契约。
    /// </para>
    /// <para>
    /// <b>为什么底层类型固定 <c>byte</c></b>：档位会进日志、埋点与本地设置存档，数值顺序一旦变更会让
    /// 历史数据串档；因此只允许按 <c>Low = 0</c> 起追加，禁止调整既有值。
    /// </para>
    /// <para>
    /// <b>为什么档位由"实测帧耗时"决定，而不是按设备型号硬编</b>：见
    /// <c>AIFramework.Core.Services.QualityTierSelector</c> 的类注释（同一型号在不同画质选项、不同场景
    /// 复杂度、不同机身温度下的实际帧率可以差一倍以上；型号白名单还会随新机发布永久过期）。
    /// </para>
    /// </summary>
    public enum QualityTier : byte
    {
        /// <summary>低档：保底可玩，先保帧率（关阴影、粒子预算最小、限 30fps）。</summary>
        Low = 0,

        /// <summary>中档：主流移动端基线（阴影只留硬阴影、60fps 目标、中等粒子预算）。</summary>
        Medium = 1,

        /// <summary>高档：旗舰机/编辑器档（全阴影、粒子预算最大、60fps 目标）。</summary>
        High = 2
    }

    /// <summary>
    /// 一个画质档位对应的<b>可落地的配置快照</b>，只读、可自由复制。
    /// <para>
    /// <b>为什么是 <c>readonly struct</c> 而不是接口或类</b>：它每个场景只被构造几次、却要被
    /// <see cref="IQualitySettingsPort.Apply"/> 以 <c>in</c> 传递，值类型既没有堆分配也没有"谁来释放"的问题；
    /// 又因为它没有行为，做成类只会多一层间接与一次 GC 压力。
    /// </para>
    /// <para>
    /// <b>为什么只有这四个字段</b>：契约要给的是"能跨引擎落地的最小集合"——帧率上限、粒子预算、阴影等级。
    /// 纹理品质、后处理是否开启、抗锯齿 MSAA 倍数都属于<b>具体渲染管线</b>的知识，写进 Contracts 会让
    /// 纯逻辑程序集被迫理解 URP/HDRP 的差异；它们应当由适配层在"档位 → 引擎设置"的映射里补全。
    /// </para>
    /// <para>
    /// <b>构造期归一化</b>：负值一律夹到 0（0 在三个字段上的语义分别是"不限制/不生成/关阴影"）。
    /// 负值只可能来自配置错误或调用方笔误，让它进入引擎 API 只会制造平台相关的未定义行为；
    /// 上界<b>不在这里夹</b>——平台允许的最大值属于端口的知识（见 <see cref="IQualitySettingsPort"/>）。
    /// </para>
    /// </summary>
    public readonly struct QualityProfile
    {
        /// <summary>构造一份档位配置。</summary>
        /// <param name="tier">档位。</param>
        /// <param name="targetFrameRate">目标帧率（负值夹到 0；0 表示交给平台默认/不限制）。</param>
        /// <param name="particleBudget">粒子预算（负值夹到 0；0 表示不生成粒子）。</param>
        /// <param name="shadowQualityLevel">阴影等级（负值夹到 0；0 = 关，具体上界由端口按平台夹取）。</param>
        public QualityProfile(QualityTier tier, int targetFrameRate, int particleBudget, int shadowQualityLevel)
        {
            Tier = tier;
            TargetFrameRate = targetFrameRate < 0 ? 0 : targetFrameRate;
            ParticleBudget = particleBudget < 0 ? 0 : particleBudget;
            ShadowQualityLevel = shadowQualityLevel < 0 ? 0 : shadowQualityLevel;
        }

        /// <summary>档位（未知枚举值原样保留：端口按平台能力兜底，不在契约层猜语义）。</summary>
        public QualityTier Tier { get; }

        /// <summary>
        /// 目标帧率：<c>0</c> = 交给平台/垂直同步决定，<c>&gt; 0</c> = 明确要求的上限。
        /// <para>Unity 侧对应 <c>Application.targetFrameRate</c>；移动端该值只是"上限"，实际帧率仍由负载决定。</para>
        /// </summary>
        public int TargetFrameRate { get; }

        /// <summary>
        /// 粒子预算：同时存活的粒子上限（不是"每帧生成数"）。
        /// <para>
        /// <b>契约只给数、不管落地</b>：Unity 没有"全局粒子上限"这一个 API，具体怎么用（写
        /// <c>ParticleSystem.maxParticles</c>、限制特效池容量、还是按预算丢弃低优先级特效）由适配层决定。
        /// </para>
        /// </summary>
        public int ParticleBudget { get; }

        /// <summary>
        /// 阴影等级：<c>0</c> = 关闭，<c>1</c> = 仅硬阴影，<c>2</c> = 完整阴影。
        /// <para>
        /// <b>为什么不直接暴露 <c>UnityEngine.ShadowQuality</c></b>：那会把一个纯逻辑契约钉死在 Unity 的枚举上；
        /// 这里只约定"0/1/2 的语义顺序"，映射到 <c>ShadowQuality.Disable/HardOnly/All</c> 是适配层的事。
        /// 超过 2 的值由端口夹到平台支持的最大档，而不是让契约假定所有引擎都只有三档。
        /// </para>
        /// </summary>
        public int ShadowQualityLevel { get; }
    }

    /// <summary>
    /// 画质设置端口：把一份 <see cref="QualityProfile"/> 落到具体平台的画质设置上。
    /// <para>
    /// <b>为什么需要端口而不是让逻辑层直接写 <c>QualitySettings</c></b>：档位决策必须是纯逻辑
    /// （能在 <c>dotnet test</c> 里毫秒级验收），而"写引擎全局设置"必然依赖引擎；端口就是这条边界的唯一接缝。
    /// 控制台/无头宿主可以用"记录最后一次 Apply"的假实现，Unity 用 <c>UnityQualitySettingsPort</c>。
    /// </para>
    /// <para>
    /// <b>幂等是硬要求</b>：与 <see cref="CurrentTier"/> 相同的档位必须是空操作。理由是<b>调用方可能高估变化频率</b>
    /// ——冷启动、场景重载、UI 里手动选档都会把同一份配置再喂一次；每次都重写
    /// <c>QualitySettings.shadows</c> / <c>targetFrameRate</c> 在 Unity 里会触发不必要的渲染路径重建，
    /// 表现为"切场景卡一下"。
    /// </para>
    /// <para>
    /// <b>禁止抛异常</b>：画质是外围体验层，任何平台不支持某项设置都必须静默降级（能写多少写多少），
    /// 不允许因为一次 <c>Apply</c> 失败把正在跑的游戏打断。实现方必须自己吃掉平台异常。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：全同步，只在主线程调用（Unity 的 <c>QualitySettings</c> 本身也只能主线程访问）；
    /// 实现方不需要加锁，选择器也不会从别的线程喂它。
    /// </para>
    /// </summary>
    public interface IQualitySettingsPort
    {
        /// <summary>
        /// 当前<b>已应用</b>的档位（不是"引擎真实设置读回来的值"——很多平台读不回来，端口只对自己的写得负责）。
        /// <para>
        /// 供选择器判断"这次变化是否真的需要落地"，也供 HUD/设置面板展示；实现方必须在
        /// <see cref="Apply"/> 成功后同步更新它。
        /// </para>
        /// </summary>
        QualityTier CurrentTier { get; }

        /// <summary>应用档位（幂等：与当前相同则空操作）。</summary>
        /// <param name="profile">档位配置（<c>in</c> 传参，避免每个场景重载时复制结构体）。</param>
        void Apply(in QualityProfile profile);
    }
}
