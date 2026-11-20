#nullable enable
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using UnityEngine;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// 画质档位自动适配器：每帧把 <c>Time.unscaledDeltaTime</c> 换算成毫秒喂给
    /// <see cref="QualityTierSelector"/>，档位变化时由选择器回写到 <see cref="IQualitySettingsPort"/>
    /// （本适配器的实现是 <see cref="UnityQualitySettingsPort"/>）。
    /// <para>
    /// <b>本类只做接线，不含任何决策</b>：阈值、窗口、冷却全在纯逻辑的选择器里（可无头单测）；
    /// 这里只负责"从引擎取一帧耗时"和"把变化落到引擎"。任何新增策略都必须加在选择器里，
    /// 加在这里就又变成了只能在真机上验证的代码。
    /// </para>
    /// <para>
    /// <b><c>unscaledDeltaTime</c> 而不是 <c>deltaTime</c></b>：<c>Time.timeScale = 0</c>（暂停/慢放）时
    /// <c>deltaTime</c> 归零，会把"暂停"喂成一串"0ms 完美帧"——选择器虽然会忽略 0，
    /// 但慢放（timeScale = 0.2）时就变成"每帧 5ms，真快"，直接误升档。帧耗时必须与游戏时间缩放无关。
    /// </para>
    /// <para>
    /// <b>采样顺序</b>：<c>Awake</c> 构造端口与选择器，并<b>立即接管一次</b>当前档位。接管是刻意的：
    /// 切场景不还原画质（见 <c>OnDestroy</c>），上一场景可能把引擎留在 Low，而新选择器的账本从 Medium 起算；
    /// 不接管就会出现"端口说 Medium、引擎实际是 Low"的错位，档位展示与升档判断都会少一级。
    /// 接管之后，只有选择器判定变化时才会再写引擎。
    /// </para>
    /// <para>
    /// <b>每帧成本</b>：一次乘法、一次环形缓冲写入、一次均值比较，不分配、不装箱、不拼字符串；
    /// 只有真正发生档位变化的那一帧才会调用引擎设置（一个场景里通常屈指可数）。
    /// </para>
    /// <para>
    /// <b>接线建议</b>：挂在每个<b>会渲染战斗</b>的场景里一个常驻对象上（例如 Boot/战斗场景的框架根节点），
    /// 不需要 <c>DontDestroyOnLoad</c>——本组件只做"采样 + 落地"，跨场景由下一场景的实例接力接管。
    /// 大厅/纯 UI 场景不要挂：那里帧率主要由 UI 决定，测出来的档位不能代表战斗负载。
    /// 调参建议：动作类窗口 90~120 帧、休闲类 180 帧；<c>cooldownFrames</c> 至少取窗口的 3 倍，
    /// 否则一次"升档后帧率回落"会在半分钟内来回切好几次。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class QualityTierAutoAdapter : MonoBehaviour
    {
        /// <summary>默认窗口帧数：120 帧 ≈ 2 秒 @60fps，与 <see cref="QualityTierSelector"/> 的构造默认值保持一致。</summary>
        private const int DefaultSampleWindowFrames = 120;

        /// <summary>默认冷却帧数：300 帧 ≈ 5 秒 @60fps，与 <see cref="QualityTierSelector"/> 的构造默认值保持一致。</summary>
        private const int DefaultCooldownFrames = 300;

        /// <summary>
        /// 滑动窗口帧数（喂给选择器）。<b>为什么在 Inspector 里开放</b>：不同品类对"多久算持续掉帧"的容忍度不同
        /// （消除类 vs 动作类差一个数量级），策划调参不该改代码。非法值由选择器夹取，这里不做二次校验。
        /// </summary>
        [SerializeField]
        private int _sampleWindowFrames = DefaultSampleWindowFrames;

        /// <summary>
        /// 变化后的冷却帧数（喂给选择器）。调大 = 更稳、更迟；调小 = 更跟手、更容易抖。
        /// 注意它和窗口帧数共同决定"最坏情况下多久才能降一档"：窗口 + 冷却。
        /// </summary>
        [SerializeField]
        private int _cooldownFrames = DefaultCooldownFrames;

        /// <summary>档位选择器（<c>Awake</c> 里构造；构造失败时为 <c>null</c>）。</summary>
        private QualityTierSelector? _selector;

        /// <summary>当前档位；<c>Awake</c> 之前返回 Medium（与选择器初始档一致）。</summary>
        public QualityTier CurrentTier
        {
            get
            {
                QualityTierSelector? selector = _selector;
                return selector != null ? selector.CurrentTier : QualityTier.Medium;
            }
        }

        /// <summary>窗口内平均帧耗时（毫秒），供 HUD 显示"当前为什么在这个档位"；<c>Awake</c> 之前为 0。</summary>
        public double AverageFrameMs
        {
            get
            {
                QualityTierSelector? selector = _selector;
                return selector != null ? selector.AverageFrameMs : 0d;
            }
        }

        /// <summary>累计档位变化次数（诊断用：0 表示整个场景都没变过档）。</summary>
        public int ChangeCount
        {
            get
            {
                QualityTierSelector? selector = _selector;
                return selector != null ? selector.ChangeCount : 0;
            }
        }

        /// <summary>
        /// 当前档位配置（含粒子预算），供特效层消费。
        /// <para><c>Awake</c> 之前返回 <c>Medium/0/0/0</c> 的占位值（帧率与预算为 0 表示"尚未配置"），
        /// 不要在 <c>Awake</c> 之前拿它做业务判断。</para>
        /// </summary>
        public QualityProfile CurrentProfile
        {
            get
            {
                QualityTierSelector? selector = _selector;
                return selector != null ? selector.CurrentProfile : new QualityProfile(QualityTier.Medium, 0, 0, 0);
            }
        }

        /// <summary>
        /// 构造端口与选择器，并接管一次当前档位。只在这里做装配，之后每帧只采样。
        /// </summary>
        private void Awake()
        {
            var port = new UnityQualitySettingsPort();
            var selector = new QualityTierSelector(port, _sampleWindowFrames, cooldownFrames: _cooldownFrames);

            // 端口由选择器持有（Apply 的调用方是选择器），本组件不再单独保存它：
            // 少一个可写字段，就少一处"端口被换掉但选择器还指着旧实例"的可能。
            _selector = selector;

            // 接管：把端口记账与引擎真实设置对齐（端口首次 Apply 必定落地，同档位不会被幂等短路）。
            QualityProfile baseline = selector.CurrentProfile;
            port.Apply(in baseline);
        }

        /// <summary>
        /// 每帧采样一次。<c>Awake</c> 抛异常（例如极端配置导致分配失败）时 Unity 仍会继续调 <c>Update</c>，
        /// 这里直接返回，宁可不采样也不要每帧刷一条空引用异常。
        /// </summary>
        private void Update()
        {
            QualityTierSelector? selector = _selector;
            if (selector == null)
            {
                return;
            }

            selector.Sample(Time.unscaledDeltaTime * 1000d);
        }

        /// <summary>
        /// <b>销毁时不还原画质</b>（刻意留空）：切场景不降档，由下一场景的适配器在 <c>Awake</c> 里再次接管。
        /// <para>
        /// 理由：还原会让"进战斗降档 → 回大厅画质突然变好 → 再进战斗又降档"的闪烁被放大两次，
        /// 而引擎的 <c>QualitySettings</c> 本来就是跨场景的全局状态。接管成本比"先还原再重新探测"低得多，
        /// 也不会出现"新场景探测期间画质比上一场景还高"的假象。
        /// </para>
        /// <para>
        /// 副作用：若下一个场景<b>没有</b>挂这个组件，画质会停在上一场景的档位（这是有意保留的行为，
        /// 不是漏还原）。需要显式恢复时，应由场景装配方自己调 <c>QualitySettings</c> 或端口。
        /// </para>
        /// </summary>
        private void OnDestroy()
        {
            // 刻意留空：见上方 XML 注释（不还原画质）。
        }
    }
}
