#nullable enable
using System;
using System.Collections.Generic;
using AIFramework.Contracts.Ports;
using AIFramework.Contracts.Services;
using AIFramework.Core.Services;
using UnityEngine;

// UnityEngine 自己有一个 ILogger（PlayerSettings 相关 API），同时 using 两个命名空间时裸写 ILogger 会撞成 CS0104。
using ILogger = AIFramework.Contracts.Ports.ILogger;

namespace AIFramework.Adapters.Unity
{
    /// <summary>
    /// Unity 音频服务：AudioSource 池 + <c>Resources</c> 剪辑加载，把 <see cref="IAudioService"/> 落到引擎侧。
    /// <para>
    /// <b>为什么是普通类而不是 MonoBehaviour</b>：本框架禁止依赖引擎生命周期回调（见 AGENTS.md 三·4），
    /// 装配层 <c>new</c> 出来即可用；它自己 <c>new GameObject</c> 挂音源并挂到传入的父节点下，
    /// 因此"谁创建、谁 Dispose"边界清晰，也不需要场景里预放 8 个空 AudioSource。
    /// </para>
    /// <para>
    /// <b>资源命名约定</b>：逻辑 ID → <c>"Audio/" + id</c>。剪辑必须位于某个 <c>Resources/Audio/</c> 下，
    /// 由 <see cref="Resources.Load{T}(string)"/> 按该 key 取；<see cref="IAssetLoader"/> 只在**加载失败**时
    /// 用来补充诊断（区分"字节根本不存在"与"存在但没进 Resources"），因此正常播放路径上零额外开销
    /// ——这也符合 <see cref="IAssetLoader.Exists"/> 文档里"仅用于诊断，不要在帧内调用"的约定。
    /// </para>
    /// <para>
    /// <b>失败语义：只 Warn，不抛</b>。音频缺失是表现问题，绝不允许让一个漏打包的音效打断战斗结算；
    /// 同一 ID 只警告一次（记进缺失集合），避免每帧触发时把 Console 刷爆。
    /// </para>
    /// <para>
    /// <b>并发策略</b>：8 条 SFX 音源池，全忙时抢占"轮转游标处的那一条"（等价于丢弃最早起播的音效）；
    /// BGM 与语音各占一条专用音源，与 SFX 池互不挤占。
    /// </para>
    /// </summary>
    public sealed class UnityAudioService : IAudioService, IDisposable
    {
        /// <summary>SFX 音源池大小。</summary>
        public const int SfxPoolSize = 8;

        /// <summary>剪辑逻辑键前缀，最终 key 形如 <c>Audio/1001</c>。</summary>
        public const string ClipKeyPrefix = "Audio/";

        /// <summary>
        /// 通道数量。<see cref="AudioChannel"/> 是 0..3 稠密枚举，实现用定长数组按下标存音量
        /// （不建字典：避免播放热路径装箱，也避免每帧查字典）。
        /// </summary>
        private const int ChannelCount = 4;

        /// <summary>"没有 BGM 在播"的哨兵值。</summary>
        private const int NoBgm = -1;

        /// <summary>资源端口：只在缺失诊断时使用，见类注释。</summary>
        private readonly IAssetLoader _assets;

        /// <summary>日志端口（已别名，避免与 <c>UnityEngine.ILogger</c> 撞名）。</summary>
        private readonly ILogger _logger;

        /// <summary>音源节点的父节点；<c>null</c> 表示放在场景根。</summary>
        private readonly Transform? _root;

        /// <summary>四通道音量（<c>[0,1]</c>），下标即 <see cref="AudioChannel"/> 数值。</summary>
        private readonly float[] _volumes;

        /// <summary>SFX 音源池。</summary>
        private readonly AudioSource[] _sfxPool;

        /// <summary>BGM 专用音源（循环）。</summary>
        private readonly AudioSource _bgmSource;

        /// <summary>语音专用音源（单条，新语音打断旧语音）。</summary>
        private readonly AudioSource _voiceSource;

        /// <summary>已成功解析的剪辑缓存（<c>Resources.Load</c> 自身有缓存，这里省掉每次的字符串拼接与查表）。</summary>
        private readonly Dictionary<int, AudioClip> _clipCache;

        /// <summary>已诊断过缺失的剪辑 ID，用于"同一 ID 只 Warn 一次"。</summary>
        private readonly HashSet<int> _missingClipIds;

        /// <summary>SFX 池轮转游标：全忙时从它开始抢占。</summary>
        private int _poolCursor;

        /// <summary>当前 BGM ID；<see cref="NoBgm"/> 表示没在播。</summary>
        private int _currentBgmId = NoBgm;

        /// <summary>是否已释放。</summary>
        private bool _disposed;

        /// <summary>构造音频服务并创建音源节点。</summary>
        /// <param name="assets">资源端口（仅缺失诊断用）。</param>
        /// <param name="logger">日志端口。</param>
        /// <param name="transform">音源节点的父节点；<c>null</c> 时放场景根。</param>
        /// <exception cref="ArgumentNullException"><paramref name="assets"/> 或 <paramref name="logger"/> 为 <c>null</c>。</exception>
        public UnityAudioService(IAssetLoader assets, ILogger logger, Transform? transform = null)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _root = transform;

            _volumes = new float[ChannelCount];
            for (int i = 0; i < ChannelCount; i++)
            {
                _volumes[i] = 1f;
            }

            _clipCache = new Dictionary<int, AudioClip>();
            _missingClipIds = new HashSet<int>();

            _sfxPool = new AudioSource[SfxPoolSize];
            for (int i = 0; i < SfxPoolSize; i++)
            {
                _sfxPool[i] = CreateSource("Sfx" + i, loop: false);
            }

            _bgmSource = CreateSource("Bgm", loop: true);
            _voiceSource = CreateSource("Voice", loop: false);

            ApplyMasterVolume();
        }

        /// <summary>当前 BGM ID；没有播放时返回 -1。</summary>
        public int CurrentBgmId
        {
            get { return _currentBgmId; }
        }

        /// <summary>当前是否有 BGM 在播。</summary>
        public bool IsBgmPlaying
        {
            get { return _currentBgmId != NoBgm; }
        }

        /// <inheritdoc />
        /// <remarks>
        /// 真机上取各音源 <c>isPlaying</c> 的实时结果；形状桩里 <c>isPlaying</c> 恒为 false，
        /// 因此离线环境下本属性恒为 0（详见 <c>unity/tools/shims/UnityEngineShim.cs</c> 的说明）。
        /// </remarks>
        public int ActiveVoiceCount
        {
            get
            {
                if (_disposed)
                {
                    return 0;
                }

                int active = 0;
                for (int i = 0; i < _sfxPool.Length; i++)
                {
                    if (_sfxPool[i].isPlaying)
                    {
                        active++;
                    }
                }

                return active;
            }
        }

        /// <inheritdoc />
        public void PlaySfx(int sfxId)
        {
            if (_disposed)
            {
                return;
            }

            AudioClip? clip = ResolveClip(sfxId);
            if (clip == null)
            {
                return;
            }

            AudioSource source = RentSfxSource();
            source.clip = clip;
            source.volume = _volumes[(int)AudioChannel.Sfx];
            source.Play();
        }

        /// <inheritdoc />
        /// <remarks>同一 ID 重复调用为空操作：BGM 是单例，重播只会把当前曲目从头再切一遍。</remarks>
        public void PlayBgm(int bgmId)
        {
            if (_disposed)
            {
                return;
            }

            int requested = bgmId < 0 ? NoBgm : bgmId;
            if (requested == _currentBgmId)
            {
                return;
            }

            if (requested == NoBgm)
            {
                StopBgm();
                return;
            }

            AudioClip? clip = ResolveClip(requested);
            if (clip == null)
            {
                // 加载失败时**不改** _currentBgmId：这样下一次同 ID 请求仍会重试（资源可能后补加载）。
                return;
            }

            _bgmSource.clip = clip;
            _bgmSource.volume = _volumes[(int)AudioChannel.Bgm];
            _bgmSource.Play();
            _currentBgmId = requested;
        }

        /// <inheritdoc />
        public void StopBgm()
        {
            if (_disposed)
            {
                return;
            }

            if (_bgmSource.isPlaying)
            {
                _bgmSource.Stop();
            }

            _currentBgmId = NoBgm;
        }

        /// <inheritdoc />
        public void SetVolume(AudioChannel channel, float volume01)
        {
            if (_disposed)
            {
                return;
            }

            int index = (int)channel;
            if (index < 0 || index >= ChannelCount)
            {
                // 越界枚举值不抛：音频是表现层，绝不允许把业务流程炸掉。
                return;
            }

            _volumes[index] = Clamp01(volume01);

            if (channel == AudioChannel.Master)
            {
                ApplyMasterVolume();
                return;
            }

            if (channel == AudioChannel.Bgm)
            {
                _bgmSource.volume = _volumes[index];
            }

            // Sfx / Voice 在每次播放时读取，已在播的那一条不会被追溯改音量（Unity 语义如此）。
        }

        /// <inheritdoc />
        public float GetVolume(AudioChannel channel)
        {
            int index = (int)channel;
            if (index < 0 || index >= ChannelCount)
            {
                return 0f;
            }

            return _volumes[index];
        }

        /// <summary>
        /// 播放一条语音/对白（<see cref="IAudioService"/> 之外的扩展口，供剧情/教程使用）。
        /// 语义：独占语音音源，新语音直接打断上一条。
        /// </summary>
        /// <param name="voiceId">语音逻辑 ID。</param>
        /// <returns>是否真的播出去了（资源缺失返回 <c>false</c>）。</returns>
        public bool PlayVoice(int voiceId)
        {
            if (_disposed)
            {
                return false;
            }

            AudioClip? clip = ResolveClip(voiceId);
            if (clip == null)
            {
                return false;
            }

            _voiceSource.clip = clip;
            _voiceSource.volume = _volumes[(int)AudioChannel.Voice];
            _voiceSource.Play();
            return true;
        }

        /// <summary>销毁本服务创建的音源节点；重复调用安全。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            for (int i = 0; i < _sfxPool.Length; i++)
            {
                DestroySource(_sfxPool[i]);
            }

            DestroySource(_bgmSource);
            DestroySource(_voiceSource);

            _clipCache.Clear();
            _missingClipIds.Clear();
            _currentBgmId = NoBgm;
        }

        /// <summary>
        /// 解析剪辑：先查缓存 → 再查 <c>Resources</c> → 都没有则诊断并返回 <c>null</c>（不抛）。
        /// </summary>
        private AudioClip? ResolveClip(int clipId)
        {
            AudioClip cached;
            if (_clipCache.TryGetValue(clipId, out cached))
            {
                return cached;
            }

            if (_missingClipIds.Contains(clipId))
            {
                // 已诊断过：静默跳过，避免高频道具每帧触发时把日志刷爆。
                return null;
            }

            string key = ClipKeyPrefix + clipId;
            AudioClip? clip = Resources.Load<AudioClip>(key);
            if (clip != null)
            {
                _clipCache[clipId] = clip;
                return clip;
            }

            _missingClipIds.Add(clipId);
            _logger.Warn(BuildMissingClipMessage(key));
            return null;
        }

        /// <summary>拼一条"为什么没加载到"的诊断文本；资源端口异常同样降级成文本，绝不上抛。</summary>
        private string BuildMissingClipMessage(string key)
        {
            string detail;
            try
            {
                detail = _assets.Exists(key)
                    ? "StreamingAssets 里有同名字节，但未能作为 AudioClip 加载（多半没放在 Resources 目录下，或没打进本次构建）"
                    : "资源字节不存在";
            }
            catch (Exception exception)
            {
                // Android/iOS 上 StreamingAssets 位于 APK/包内，UnityAssetLoader 会抛 DirectoryNotFoundException。
                detail = "资源存在性探测失败：" + exception.Message;
            }

            return "音频资源缺失：" + key + "；" + detail + "（该 ID 后续播放将静默跳过）";
        }

        /// <summary>取一条可用音源；池全忙时抢占轮转游标处的那条（丢弃最早起播的音效）。</summary>
        private AudioSource RentSfxSource()
        {
            for (int i = 0; i < _sfxPool.Length; i++)
            {
                int index = _poolCursor + i;
                if (index >= _sfxPool.Length)
                {
                    index -= _sfxPool.Length;
                }

                if (!_sfxPool[index].isPlaying)
                {
                    _poolCursor = AdvanceCursor(index);
                    return _sfxPool[index];
                }
            }

            AudioSource victim = _sfxPool[_poolCursor];
            _poolCursor = AdvanceCursor(_poolCursor);
            return victim;
        }

        /// <summary>游标前进一步（环形）。</summary>
        private int AdvanceCursor(int index)
        {
            int next = index + 1;
            return next == _sfxPool.Length ? 0 : next;
        }

        /// <summary>创建一个音源节点并挂到父节点下。</summary>
        private AudioSource CreateSource(string name, bool loop)
        {
            var gameObject = new GameObject("AIFrameworkAudio_" + name);

            Transform? root = _root;
            if (root != null)
            {
                gameObject.transform.SetParent(root, false);
            }

            AudioSource source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            return source;
        }

        /// <summary>销毁一条音源的节点；<c>null</c> 安全。</summary>
        private static void DestroySource(AudioSource? source)
        {
            if (source == null)
            {
                return;
            }

            AudioSource target = source;
            UnityEngine.Object.Destroy(target.gameObject);
        }

        /// <summary>Master 通道走全局监听器音量，避免每条音源各乘一次造成"改总音量不生效"。</summary>
        private void ApplyMasterVolume()
        {
            AudioListener.volume = _volumes[(int)AudioChannel.Master];
        }

        /// <summary>夹到 <c>[0,1]</c>；NaN 归 0（NaN 会污染混音并让音量彻底失控）。</summary>
        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || value < 0f)
            {
                return 0f;
            }

            if (value > 1f)
            {
                return 1f;
            }

            return value;
        }
    }

    /// <summary>
    /// Unity 侧的服务端校验适配器。<b>当前是占位实现</b>，存在的意义是把"远端校验还没接线"这件事
    /// 变成一个**显式、可观测、不会伪装成功**的状态：
    /// <list type="bullet">
    /// <item><see cref="Offline"/>：完全离线，用 <see cref="MatchValidationLedger"/> 做本地重复/分数/长度粗筛。</item>
    /// <item><see cref="RemotePending"/>：按"真人服务端"部署但 HTTP 通道尚未接线，
    /// 一律返回 <see cref="NetworkValidationResult.Unavailable"/>（上层应重试），**绝不返回 Accepted**。</item>
    /// </list>
    /// <para>
    /// <b>为什么不在这里实现 UnityWebRequest</b>：契约 <see cref="IMatchValidator.Validate"/> 是同步的，
    /// 在主线程上阻塞等 HTTP 会直接卡死帧；正确做法是装配层用"请求队列 + 后台线程/协程"包一层异步传输，
    /// 再把结果喂回本端口。帧内同步语义与异步传输的这一层粘合属于具体游戏的网络层，不属于框架。
    /// </para>
    /// </summary>
    public sealed class UnityMatchValidator : IMatchValidator
    {
        /// <summary>远端未接线时的原因串（常量，避免每次分配）。</summary>
        public const string RemoteNotWiredReason = "remote validator not wired";

        /// <summary>离线台账：重复指纹 / 分数合理性 / 对局长度三条硬规则。</summary>
        private readonly MatchValidationLedger _ledger;

        /// <summary>日志端口。</summary>
        private readonly ILogger _logger;

        /// <summary>是否处于"远端待接线"模式。</summary>
        private readonly bool _remotePending;

        /// <summary>私有构造：模式只能通过两个静态工厂选择，避免装配层随意切换语义。</summary>
        private UnityMatchValidator(ILogger logger, bool remotePending)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ledger = new MatchValidationLedger();
            _remotePending = remotePending;
        }

        /// <summary>创建离线校验器（本地台账，可用于单机/断网/开发期）。</summary>
        /// <param name="logger">日志端口。</param>
        public static UnityMatchValidator Offline(ILogger logger)
        {
            return new UnityMatchValidator(logger, remotePending: false);
        }

        /// <summary>创建"远端待接线"校验器（一律返回 Unavailable，提醒上层重试）。</summary>
        /// <param name="logger">日志端口。</param>
        public static UnityMatchValidator RemotePending(ILogger logger)
        {
            return new UnityMatchValidator(logger, remotePending: true);
        }

        /// <summary>是否处于"远端待接线"模式。</summary>
        public bool IsRemotePending
        {
            get { return _remotePending; }
        }

        /// <inheritdoc />
        public ValidationResponse Validate(in MatchSummary summary)
        {
            if (_remotePending)
            {
                _logger.Warn("远端校验尚未接线，本局返回 Unavailable（上层应重试，不得视为通过）。");
                return new ValidationResponse(NetworkValidationResult.Unavailable, 0, RemoteNotWiredReason);
            }

            ValidationResponse response = _ledger.Validate(in summary);
            if (!response.IsAccepted)
            {
                _logger.Warn("本局校验被拒绝：" + response.Reason + "（指纹=" + summary.StateFingerprint + "）");
            }

            return response;
        }
    }
}
