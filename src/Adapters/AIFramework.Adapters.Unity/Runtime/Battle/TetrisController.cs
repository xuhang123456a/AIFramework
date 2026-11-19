#nullable enable
using System;
using AIFramework.Contracts.Gameplay;
using AIFramework.Contracts.Gameplay.Turn;
using AIFramework.Contracts.Meta;
using UnityEngine;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 俄罗斯方块场景控制器：建宿主 → <c>Begin(Tetris)</c> → 每帧把"重力"喂给内核 → 只读视图推给 HUD →
    /// 判负后结算一次。
    /// <para>
    /// <b>为什么这里没有任何方块规则</b>：能不能左移、旋转能不能踢墙、触底由谁锁定、消行给多少分，
    /// 全部由内核判定；适配层一旦自己算一遍，两侧就会各自演化，改内核而不改 UI 时行为静默分叉。
    /// 因此本类只做五件事：装配宿主、把"操作"翻译成 <see cref="ITetrisInput"/> 意图、
    /// 每帧驱动一次自动下落、判负时结算并广播事实、把只读视图推给 HUD。
    /// </para>
    /// <para>
    /// <b>为什么只认 Contracts 接口</b>：本类<b>不</b>引用 <c>AIFramework.Kernel.Turn</c> 的
    /// <c>TetrisWorld</c>，只用 <see cref="ITetrisReadView"/> / <see cref="ITetrisInput"/> 与
    /// <see cref="IGameplaySession"/>；内核实现与宿主工厂分支由装配层接，本类无需改动。
    /// </para>
    /// <para>
    /// <b>为什么用 <see cref="Update"/> 计时而不是协程</b>：宪法禁止把业务节奏挂到 <c>IEnumerator</c> /
    /// <c>StartCoroutine</c> 上——协程在场景卸载、组件禁用、<c>Time.timeScale = 0</c> 时的行为各不相同，
    /// 且无法在无头测试里驱动。每帧一次 <see cref="ITetrisInput.Tick"/> 只有一条可读的时间线。
    /// </para>
    /// <para>
    /// <b>为什么用 <c>Tick</c> 而不是 <c>IGameplaySession.Step</c></b>：方块游戏的时间语义是"真实时间重力"，
    /// <c>Tick</c> 正是按毫秒累加的下落时钟；代价是结算报告里的 <c>StepCount</c> 会停在 0
    /// （<c>Step</c> 才会给它 +1）。这个字段只进日志与对拍，不影响胜负/战利品，
    /// 若将来需要它有意义，把 <see cref="Update"/> 里那一行换成 <c>session.Step</c> 即可（行为等价）。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TetrisController : MonoBehaviour
    {
        /// <summary>
        /// 进门关卡 ID。<b>为什么是常量而不是 <c>[SerializeField]</c></b>：方块游戏没有关卡内容，
        /// 该值只用于 <see cref="GameplayInitData"/> 与 <see cref="GameFinishedEvent"/> 的场次标识；
        /// 写成字段会多一个"场景里没注入"的空洞，公开面也不再是锁死的那一组。
        /// </summary>
        private const int DefaultLevelId = 1;

        /// <summary>
        /// 建会话时交给内核的单位数。<b>为什么是 1</b>：方块是单人玩法，该字段对内核算术无影响，
        /// 但保持与"单人心智模型"一致，便于日志与快照比对。
        /// </summary>
        private const int SinglePlayerUnitCount = 1;

        /// <summary>
        /// 下落倍率的兜底值：字段被 Inspector 改成 0 或负数时用它。
        /// <b>为什么必须兜底</b>：内核的 <c>Tick</c> 会忽略非正数增量，倍率为 0 时方块永远不下落，
        /// 看起来像"游戏卡住"而不是"参数被改坏"。
        /// </summary>
        private const float FallbackFallSpeedMultiplier = 1f;

        /// <summary>棋盘皮肤（渲染状态行与字符网格，本类不碰任何 UI 组件）。</summary>
        [SerializeField] private TetrisHud hud = null!;

        /// <summary>随机种子：固定值让同一局的 7-bag 序列可复现。</summary>
        [SerializeField] private int randomSeed = 20261120;

        /// <summary>下落速度倍率：1 = 内核原速，2 = 两倍速（只影响送进 <c>Tick</c> 的时间，不改内核规则）。</summary>
        [SerializeField] private float fallSpeedMultiplier = 1f;

        /// <summary>宿主（总线/会话/日志/背包的唯一入口）；销毁时置空。</summary>
        private AIFrameworkUnityHost? _host;

        /// <summary>当前对局会话；结算或开局失败后置空。<b>只有它还非空时才允许 <c>EndAndCollect</c></b>。</summary>
        private IGameplaySession? _session;

        /// <summary>会话强转出来的只读视图；结算后仍保留（HUD 要继续显示最终局面），重开时替换。</summary>
        private ITetrisReadView? _readView;

        /// <summary>会话强转出来的输入端口；开局失败时为空（此时只能看不能动）。</summary>
        private ITetrisInput? _input;

        /// <summary>本局是否已结算（<c>EndAndCollect</c> 只允许发生一次，用它兜住重入）。</summary>
        private bool _collected;

        /// <summary>当前分数（结算后仍可读；无会话时为 0）。</summary>
        public int Score
        {
            get
            {
                ITetrisReadView? view = _readView;
                return view != null ? view.Score : 0;
            }
        }

        /// <summary>本局累计消除的行数（结算后仍可读；无会话时为 0）。</summary>
        public int ClearedLines
        {
            get
            {
                ITetrisReadView? view = _readView;
                return view != null ? view.ClearedLines : 0;
            }
        }

        /// <summary>
        /// 内核是否已判负（新方块出生即碰撞）。
        /// <b>注意</b>：这里只反映内核的判定，不反映"是否已结算"——结算只发生一次，由内部标记兜住。
        /// </summary>
        public bool IsGameOver
        {
            get
            {
                ITetrisReadView? view = _readView;
                return view != null && view.IsGameOver;
            }
        }

        /// <summary>
        /// 重开一局：交还上一局 → 用同一份种子参数重新 <c>Begin</c>。
        /// <para>
        /// <b>为什么必须重建会话而不是"清盘"</b>：内核在 <c>CollectResult</c> 之后把输入全部冻结
        /// （会话所有权已交还宿主），此时任何 <c>TryMove*</c> 都是空操作；只有重新 <c>Begin</c>
        /// 才能真正回到初始局面。恰好结算完成后也是走这条路，所以"重开"与"再来一局"是同一段代码。
        /// </para>
        /// <para>
        /// <b>局中调用同样安全</b>：旧会话还没结算就直接 <c>Release</c> 丢弃（纯内存对象，无原生资源），
        /// 不会出现"两个会话同时活着"从而让 <c>Begin</c> 抛"已有活跃会话"。
        /// </para>
        /// <para>
        /// <b>为什么先补一次结算</b>：按钮回调来自 <c>EventSystem</c>（也是一个 MonoBehaviour），
        /// 它的 <c>Update</c> 可能排在本类的 <c>Update</c> 之前。若刚好落在"已判负但还没结算"的那一帧，
        /// 直接重建会把这一局的 <c>GameFinishedEvent</c> 与战利品静默吞掉。判定已结束就先走
        /// <see cref="Finish"/>（它有 <c>_collected</c> 兜底，绝不会结算两次），再重建。
        /// </para>
        /// </summary>
        public void Restart()
        {
            if (!_collected && _session != null && IsGameOver)
            {
                Finish();
            }

            BeginSession();
        }

        /// <summary>左移一格；被内核拒绝（出界/碰撞/已结束）时什么都不会发生。</summary>
        public void MoveLeft()
        {
            ITetrisInput? input = _input;
            if (input != null)
            {
                input.TryMoveLeft();
            }
        }

        /// <summary>右移一格；被内核拒绝（出界/碰撞/已结束）时什么都不会发生。</summary>
        public void MoveRight()
        {
            ITetrisInput? input = _input;
            if (input != null)
            {
                input.TryMoveRight();
            }
        }

        /// <summary>顺时针旋转 90°；踢墙由内核尝试，两处都不行则朝向与位置都不变。</summary>
        public void Rotate()
        {
            ITetrisInput? input = _input;
            if (input != null)
            {
                input.TryRotate();
            }
        }

        /// <summary>软降一格；触底那一次由内核顺手锁定方块（返回值在接口里，公开面不需要它）。</summary>
        public void SoftDrop()
        {
            ITetrisInput? input = _input;
            if (input != null)
            {
                input.TrySoftDrop();
            }
        }

        /// <summary>硬降到底并锁定；加分与消行全部由内核结算。</summary>
        public void HardDrop()
        {
            ITetrisInput? input = _input;
            if (input != null)
            {
                input.HardDrop();
            }
        }

        /// <summary>
        /// 建宿主并开局。<b>为什么在 <c>Awake</c> 而不是 <c>Start</c></b>：HUD 的按钮订阅与首帧渲染要在
        /// 玩家可能点下去之前就绪；挂到 <c>Start</c> 会留一个"第一次点击没反应"的空窗。
        /// </summary>
        private void Awake()
        {
            var host = new AIFrameworkUnityHost();
            host.Initialize();
            _host = host;

            SubscribeHud();
            BeginSession();
        }

        /// <summary>
        /// 每帧：把"真实时间 × 倍率"喂给内核的重力时钟 → 刷新 HUD → 判负则结算一次。
        /// <b>顺序不可换</b>：先 Tick 再渲染，玩家看到的就是这一步之后的真实局面；
        /// 先渲染再 Tick 会让画面永远慢一格（触底/消行都要多等一帧才上屏）。
        /// </summary>
        private void Update()
        {
            IGameplaySession? session = _session;
            if (session == null)
            {
                return;
            }

            ITetrisInput? input = _input;
            if (input != null)
            {
                double deltaSeconds = (double)Time.deltaTime * (double)EffectiveFallSpeedMultiplier();

                // 单帧最多下落一格（内核保证），因此掉帧/长帧不会让方块瞬移，只会让它慢半拍。
                input.Tick(deltaSeconds);
            }

            RenderHud();

            if (!_collected && IsGameOver)
            {
                Finish();
            }
        }

        /// <summary>退订 HUD 并释放宿主（<c>Dispose</c> 会丢弃未结算的对局引用）。</summary>
        private void OnDestroy()
        {
            UnsubscribeHud();

            _session = null;
            _readView = null;
            _input = null;
            _collected = true;

            AIFrameworkUnityHost? host = _host;
            _host = null;
            if (host != null)
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// 建（或重建）一局的会话：交还旧会话 → <c>Begin(Tetris)</c> → 强转契约 → 首帧渲染。
        /// <para>
        /// <b>为什么每次都先 <c>Release</c></b>：宿主只允许一个活跃会话，上一局（重开、场景重进）
        /// 若还挂着，<c>Begin</c> 会直接抛"已有活跃会话"。
        /// </para>
        /// </summary>
        private void BeginSession()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null)
            {
                return;
            }

            _session = null;
            _readView = null;
            _input = null;
            _collected = false;

            if (host.Sessions.HasActiveSession)
            {
                host.Sessions.Release();
            }

            IGameplaySession session;
            var init = new GameplayInitData(DefaultLevelId, randomSeed, SinglePlayerUnitCount);
            try
            {
                session = host.Sessions.Begin(GameplayKernelType.Tetris, in init);
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Tetris] 开局失败（宿主已有未结算会话？）：" + exception.Message);

                // 没有会话可推：把 _collected 立起来，让 Update 停在渲染之外，避免每帧重复开局。
                _collected = true;
                return;
            }

            _session = session;
            _readView = session as ITetrisReadView;
            _input = session as ITetrisInput;

            if (_readView == null || _input == null)
            {
                // 会话没暴露方块契约：说明工厂还没接 Tetris 分支。
                // 此时绝不能用错的卡带继续推演，也不能让 HUD 显示一份假棋盘——立刻交还会话并关掉 HUD。
                host.Log.Error("[Tetris] 会话未实现 ITetrisReadView/ITetrisInput（UnityKernelFactory 还没接 Tetris 分支？），"
                    + "本局已放弃，HUD 已禁用。");

                _session = null;
                _readView = null;
                _input = null;
                _collected = true;
                host.Sessions.Release();

                if (hud != null)
                {
                    hud.enabled = false;
                }

                return;
            }

            // 上一次开局失败可能把 HUD 关掉了；重开成功后必须重新打开，否则玩家能操作但看不见棋盘。
            if (hud != null && !hud.enabled)
            {
                hud.enabled = true;
            }

            RenderHud();

            host.Log.Info("[Tetris] 开局：场次=" + DefaultLevelId + " 种子=" + randomSeed
                + " 尺寸=" + _readView.Width + "×" + _readView.Height
                + " 下落倍率=" + EffectiveFallSpeedMultiplier());
        }

        /// <summary>
        /// 结算：<c>EndAndCollect</c> → 广播 <c>GameFinishedEvent</c> → 战利品入账 → HUD 显示终局文案。
        /// <para>
        /// <b>顺序不可换</b>：先广播"这局已结束"这个事实，再发奖，保证任务/成就类切片看到的结果
        /// 与账本状态一致（反之会读到"已结束但物品还没到账"的中间态）。
        /// </para>
        /// </summary>
        private void Finish()
        {
            AIFrameworkUnityHost? host = _host;
            if (host == null || _collected)
            {
                return;
            }

            // 先置标记再结算：CollectResult 失败（未结束/二次结算）时不允许再重试，否则每帧都会抛一次。
            _collected = true;
            _session = null;

            int score = Score;

            GameplayResult result;
            try
            {
                result = host.Sessions.EndAndCollect();
            }
            catch (InvalidOperationException exception)
            {
                host.Log.Error("[Tetris] 结算失败：" + exception.Message);
                return;
            }

            host.PublishGameFinished(GameplayKernelType.Tetris, DefaultLevelId, result.IsVictory);

            var loots = result.Loots;
            for (int i = 0; i < loots.Count; i++)
            {
                LootItem loot = loots[i];
                host.Bus.Send(new GrantItemCommand(loot.ItemId, loot.Count, MetaReasonTags.GameLoot));
            }

            if (hud != null)
            {
                hud.ShowGameOver(score);
            }

            host.Log.Info("[Tetris] 结算：场次=" + DefaultLevelId
                + " 胜负=" + result.IsVictory
                + " 分数=" + score
                + " 消行=" + ClearedLines
                + " 剩余空间=" + result.RemainingUnits
                + " 战利品条目=" + loots.Count);
        }

        /// <summary>把 HUD 的"重新开始"接到本类（<c>Awake</c> 调用，与 <see cref="OnDestroy"/> 严格配对）。</summary>
        private void SubscribeHud()
        {
            TetrisHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked += HandleRestartClicked;
        }

        /// <summary>退订 HUD（避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void UnsubscribeHud()
        {
            TetrisHud? view = hud;
            if (view == null)
            {
                return;
            }

            view.OnRestartClicked -= HandleRestartClicked;
        }

        /// <summary>点"重新开始"：无论局中还是终局，都走同一条重建路径。</summary>
        private void HandleRestartClicked()
        {
            Restart();
        }

        /// <summary>渲染一次（会话可能已不在，故先取本地副本；视图为空时 HUD 自己会跳过）。</summary>
        private void RenderHud()
        {
            TetrisHud? view = hud;
            ITetrisReadView? read = _readView;
            if (view != null && read != null)
            {
                view.Render(read);
            }
        }

        /// <summary>下落倍率：字段被改成非法值时退回 <see cref="FallbackFallSpeedMultiplier"/>。</summary>
        /// <returns>大于 0 的倍率。</returns>
        private float EffectiveFallSpeedMultiplier()
        {
            float multiplier = fallSpeedMultiplier;
            return multiplier > 0f ? multiplier : FallbackFallSpeedMultiplier;
        }
    }
}
