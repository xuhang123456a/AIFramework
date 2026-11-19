#nullable enable
using System;
using System.Text;
using AIFramework.Contracts.Gameplay.Turn;
using UnityEngine;
using UnityEngine.UI;

namespace AIFramework.Adapters.Unity.Battle
{
    /// <summary>
    /// 音游的 uGUI 皮肤：状态行（歌曲时间/连击/准确率/四个判定计数）+ 4 行轨道字符视图 +
    /// 4 个轨道按钮 + 一个"重新开始"按钮。
    /// <para>
    /// <b>为什么轨道用字符而不是一列下落方块</b>：一个 <see cref="Text"/> 就能表达"每条轨道未来 2 秒
    /// 有哪些音符"，零池化、零 <c>Instantiate</c>；而"每帧几十个音符在下落"用 <see cref="Image"/>
    /// 去做，等于每帧几十次脏标记，反而更贵。字符视图也正好让无头环境可以断言（同一份视图能打日志）。
    /// </para>
    /// <para>
    /// <b>本类不含任何规则</b>：判定窗、漏打、连击、准确率、胜负全部由内核判定；HUD 只负责
    /// "把只读数据翻译成字符/文本"和"把手指点位翻译成事件"。这条边界一旦破了，UI 就会变成第二套规则实现。
    /// </para>
    /// <para>
    /// <b>量化口径</b>：每行固定 20 列，每列 0.1 秒，覆盖从当前 <see cref="IRhythmReadView.SongTime"/>
    /// 起 2 秒。列号 = <c>floor((HitTimeSeconds - SongTime) / 0.1)</c>；未来 2 秒内<b>还没判定</b>的音符
    /// 画 <c>●</c>，其余位置（含已判定音符）保持轨道基线 <c>·</c>——判定过的音符回到基线，
    /// 一眼就能看出"接下来要打哪里"。
    /// </para>
    /// <para>
    /// <b>脏检查</b>：状态行与轨道文本都先比后写。轨道只在音符跨过 0.1 秒列边界时才真正变，稳态下
    /// 不会每帧触发 <see cref="Text"/> 的字形重建。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RhythmHud : MonoBehaviour
    {
        /// <summary>轨道行数（固定 4，与 <c>IRhythmReadView.LaneCount</c> 的约定一致）。</summary>
        private const int TrackRowCount = 4;

        /// <summary>轨道视图覆盖的时间窗（秒）：从当前歌曲时间起 2 秒。</summary>
        private const double TrackWindowSeconds = 2.0;

        /// <summary>每一列代表的时间（秒）。</summary>
        private const double TrackColumnSeconds = 0.1;

        /// <summary>每行的列数（<see cref="TrackWindowSeconds"/> / <see cref="TrackColumnSeconds"/>）。</summary>
        private const int TrackColumns = 20;

        /// <summary>一行字符在缓冲里的步长（20 列 + 1 个换行）。</summary>
        private const int RowStride = TrackColumns + 1;

        /// <summary>整个轨道缓冲的长度：4 行 × 21，去掉最后一个换行。</summary>
        private const int TrackLength = (TrackRowCount * RowStride) - 1;

        /// <summary>未来 2 秒内尚未判定的音符。</summary>
        private const char NoteChar = '●';

        /// <summary>轨道基线：空位与已判定的音符都用它。</summary>
        private const char TrackChar = '·';

        /// <summary>行分隔符。</summary>
        private const char RowSeparator = '\n';

        /// <summary>点"重新开始"。</summary>
        public event Action? OnRestartClicked;

        /// <summary>点了某条轨道按钮；参数是轨道下标 0..3。</summary>
        public event Action<int>? OnLaneClicked;

        /// <summary>状态行：歌曲时间、连击/最高连击、准确率，第二行是四个判定计数（结算后改写为结果文案）。</summary>
        [SerializeField] private Text statusText = null!;

        /// <summary>轨道字符视图：4 行 × 20 列（字号 16，等宽感靠"一行一轨"保证）。</summary>
        [SerializeField] private Text notesText = null!;

        /// <summary>0 号轨道按钮。</summary>
        [SerializeField] private Button lane0Button = null!;

        /// <summary>1 号轨道按钮。</summary>
        [SerializeField] private Button lane1Button = null!;

        /// <summary>2 号轨道按钮。</summary>
        [SerializeField] private Button lane2Button = null!;

        /// <summary>3 号轨道按钮。</summary>
        [SerializeField] private Button lane3Button = null!;

        /// <summary>"重新开始"按钮。</summary>
        [SerializeField] private Button restartButton = null!;

        /// <summary>复用的轨道缓冲：每次 <see cref="Render"/> 清空重填，不逐帧申请数组。</summary>
        private readonly char[] _track = new char[TrackLength];

        /// <summary>复用的状态行缓冲：两行文本 + 数字，容量够用到底。</summary>
        private readonly StringBuilder _status = new StringBuilder(256);

        /// <summary>已写到控件上的轨道文本（脏检查基准）。</summary>
        private string _lastTrack = string.Empty;

        /// <summary>已写到控件上的状态行（脏检查基准）。</summary>
        private string _lastStatus = string.Empty;

        /// <summary>
        /// 是否已显示结果文案。<b>为什么需要它</b>：结算后控制器仍可能继续推视图，
        /// 若每次都重写状态行，"演奏结束 · …"会立刻被下一帧的实时计数盖掉。
        /// </summary>
        private bool _resultShown;

        /// <summary>
        /// 把按钮点击接到转发方法上。<b>为什么在 <c>Start</c></b>：Unity 保证 <c>Start</c> 在本帧任何
        /// <c>Update</c> 之前跑完一次，此时订阅不会与控制器首帧渲染抢顺序。
        /// </summary>
        private void Start()
        {
            if (lane0Button != null)
            {
                lane0Button.onClick.AddListener(RaiseLane0Clicked);
            }

            if (lane1Button != null)
            {
                lane1Button.onClick.AddListener(RaiseLane1Clicked);
            }

            if (lane2Button != null)
            {
                lane2Button.onClick.AddListener(RaiseLane2Clicked);
            }

            if (lane3Button != null)
            {
                lane3Button.onClick.AddListener(RaiseLane3Clicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.AddListener(RaiseRestartClicked);
            }
        }

        /// <summary>退订按钮（与 <see cref="Start"/> 严格配对，避免场景重载后残留监听导致一次点击触发两次）。</summary>
        private void OnDestroy()
        {
            if (lane0Button != null)
            {
                lane0Button.onClick.RemoveListener(RaiseLane0Clicked);
            }

            if (lane1Button != null)
            {
                lane1Button.onClick.RemoveListener(RaiseLane1Clicked);
            }

            if (lane2Button != null)
            {
                lane2Button.onClick.RemoveListener(RaiseLane2Clicked);
            }

            if (lane3Button != null)
            {
                lane3Button.onClick.RemoveListener(RaiseLane3Clicked);
            }

            if (restartButton != null)
            {
                restartButton.onClick.RemoveListener(RaiseRestartClicked);
            }
        }

        /// <summary>
        /// 按只读视图刷新状态行与轨道视图。<paramref name="view"/> 为 <c>null</c> 时原样返回
        /// （内核没就绪/本局已放弃），不抛异常也不清屏，避免"装配没到位"看起来像"界面炸了"。
        /// </summary>
        /// <param name="view">内核提供的只读视图。</param>
        public void Render(IRhythmReadView view)
        {
            if (view == null)
            {
                return;
            }

            // 重开之后视图回到"未结束"：内部状态跟着复位，结果文案不再霸占状态行。
            if (_resultShown && !view.IsFinished)
            {
                _resultShown = false;
                _lastStatus = string.Empty;
            }

            UpdateTrack(view);

            if (!_resultShown)
            {
                SetStatus(BuildStatus(view));
            }
        }

        /// <summary>
        /// 显示结果文案（最佳判定 + 准确率）。<b>只改状态行、不动轨道视图</b>：
        /// 终局画面仍要看到最后一段轨道，否则玩家分不清"打完了"与"界面停了"。
        /// </summary>
        /// <param name="best">本局最佳判定（Perfect &lt; Great &lt; Good &lt; Miss）。</param>
        /// <param name="accuracy">本局最终准确率（0..1）。</param>
        public void ShowResult(RhythmJudgement best, double accuracy)
        {
            _resultShown = true;

            _status.Clear();
            _status.Append("演奏结束 · 最佳判定 ").Append(JudgementLabel(best))
                .Append(" · 准确率 ").Append(PercentText(accuracy))
                .Append(RowSeparator)
                .Append("点『重新开始』再来一局");

            SetStatus(_status.ToString());
        }

        /// <summary>
        /// 重画 4 行轨道：先铺满基线，再把"未来 2 秒内未判定"的音符按 0.1 秒量化成列画上 <c>●</c>。
        /// <para>
        /// <b>为什么可以先 <c>break</c></b>：<see cref="IRhythmReadView.GetNote"/> 按
        /// <c>HitTimeSeconds</c> 升序返回，一旦偏移超过 2 秒，后面的音符只会更远。
        /// </para>
        /// </summary>
        /// <param name="view">只读视图。</param>
        private void UpdateTrack(IRhythmReadView view)
        {
            for (int i = 0; i < _track.Length; i++)
            {
                _track[i] = TrackChar;
            }

            for (int lane = 1; lane < TrackRowCount; lane++)
            {
                _track[(lane * RowStride) - 1] = RowSeparator;
            }

            int noteCount = view.NoteCount;
            double songTime = view.SongTime;

            for (int i = 0; i < noteCount; i++)
            {
                RhythmNoteView note = view.GetNote(i);
                double offset = note.HitTimeSeconds - songTime;

                if (offset >= TrackWindowSeconds)
                {
                    break;
                }

                // 已经越过播放线的音符不画（窗口只覆盖"从现在起 2 秒"）；NaN 也会落进这里被跳过。
                if (!(offset >= 0.0))
                {
                    continue;
                }

                int lane = note.Lane;
                if (lane < 0 || lane >= TrackRowCount)
                {
                    continue;
                }

                // 已判定：留在基线 '·' 上——只有"还没打"的音符才需要被玩家看见。
                if (note.IsResolved)
                {
                    continue;
                }

                int column = (int)(offset / TrackColumnSeconds);
                if (column < 0)
                {
                    column = 0;
                }
                else if (column >= TrackColumns)
                {
                    // 0.1 的浮点除法可能把"恰好 2.0 秒"算成 20；窗口本来就不含 2.0，夹到最后一列是安全兜底。
                    column = TrackColumns - 1;
                }

                _track[(lane * RowStride) + column] = NoteChar;
            }

            string track = new string(_track);
            if (track != _lastTrack)
            {
                _lastTrack = track;
                if (notesText != null)
                {
                    notesText.text = track;
                }
            }
        }

        /// <summary>
        /// 拼状态行：第一行时间/连击/最高连击/准确率，第二行四个判定计数。
        /// <b>为什么分两行</b>：一行塞下 7 个数字会在 1280 宽的 Game 视图里被 <see cref="Text"/> 截断，
        /// 而截断的正是"漏打"这种最需要看见的读数。
        /// </summary>
        /// <param name="view">只读视图。</param>
        /// <returns>两行状态文本。</returns>
        private string BuildStatus(IRhythmReadView view)
        {
            _status.Clear();
            _status.Append("时间 ").Append(view.SongTime.ToString("F1")).Append("s")
                .Append(" · 连击 ").Append(view.Combo)
                .Append("（最高 ").Append(view.MaxCombo).Append("）")
                .Append(" · 准确率 ").Append(PercentText(view.Accuracy))
                .Append(RowSeparator)
                .Append("完美 ").Append(view.PerfectCount)
                .Append(" · 优秀 ").Append(view.GreatCount)
                .Append(" · 良好 ").Append(view.GoodCount)
                .Append(" · 漏打 ").Append(view.MissCount);

            return _status.ToString();
        }

        /// <summary>写状态行（文本未变就不碰控件，避免每帧重建字形）。</summary>
        /// <param name="line">要显示的两行文本。</param>
        private void SetStatus(string line)
        {
            if (line == _lastStatus)
            {
                return;
            }

            _lastStatus = line;
            if (statusText != null)
            {
                statusText.text = line;
            }
        }

        /// <summary>把 0..1 的准确率翻成一位小数的百分比（如 <c>96.3%</c>）。</summary>
        /// <param name="ratio">0..1 的比例。</param>
        /// <returns>形如 <c>96.3%</c> 的文本。</returns>
        private static string PercentText(double ratio)
        {
            return (ratio * 100.0).ToString("F1") + "%";
        }

        /// <summary>判定等级的中文名（只用于展示，不参与任何运算）。</summary>
        /// <param name="judgement">判定等级。</param>
        /// <returns>中文标签。</returns>
        private static string JudgementLabel(RhythmJudgement judgement)
        {
            switch (judgement)
            {
                case RhythmJudgement.Perfect:
                    return "完美";
                case RhythmJudgement.Great:
                    return "优秀";
                case RhythmJudgement.Good:
                    return "良好";
                case RhythmJudgement.Miss:
                    return "漏打";
                default:
                    return "未知";
            }
        }

        /// <summary>转发"重新开始"。</summary>
        private void RaiseRestartClicked()
        {
            Action? handler = OnRestartClicked;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>转发 0 号轨道点击。</summary>
        private void RaiseLane0Clicked()
        {
            RaiseLaneClicked(0);
        }

        /// <summary>转发 1 号轨道点击。</summary>
        private void RaiseLane1Clicked()
        {
            RaiseLaneClicked(1);
        }

        /// <summary>转发 2 号轨道点击。</summary>
        private void RaiseLane2Clicked()
        {
            RaiseLaneClicked(2);
        }

        /// <summary>转发 3 号轨道点击。</summary>
        private void RaiseLane3Clicked()
        {
            RaiseLaneClicked(3);
        }

        /// <summary>把轨道下标抬成 <see cref="OnLaneClicked"/> 事件。</summary>
        /// <param name="lane">轨道下标 0..3。</param>
        private void RaiseLaneClicked(int lane)
        {
            Action<int>? handler = OnLaneClicked;
            if (handler != null)
            {
                handler(lane);
            }
        }
    }
}
