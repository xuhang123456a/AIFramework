#nullable enable
namespace AIFramework.Contracts.Input
{
    /// <summary>
    /// 跨端统一动作：手柄按键 / 触屏手势 / 键鼠都映射到这几个，业务只认动作。
    /// <para>
    /// <b>为什么先有动作再有设备</b>：跑酷的"跳"和平台跳跃的"跳"在语义上是同一件事，但手柄的 A、
    /// 键盘的空格、触屏的上滑是三套完全不同的 API（<c>KeyCode</c> / <c>Touch</c> / 摇杆轴）。
    /// 业务层一旦直接读设备，每接一种设备就要在玩法代码里加一个 <c>if</c>；把设备差异收口成
    /// 5 个动作后，内核与 Presenter 只消费 <see cref="InputFrame"/>，换设备不改玩法。
    /// </para>
    /// <para>
    /// <b>为什么底层类型固定 <c>byte</c></b>：动作号会进日志、回放与（将来的）输入录制文件，
    /// 数值顺序一旦变更会让历史数据串档，只允许追加、不允许调整或删除既有值。
    /// </para>
    /// </summary>
    public enum InputAction : byte
    {
        /// <summary>跳跃（空格 / 手柄 A / 触屏上滑）。</summary>
        Jump = 0,

        /// <summary>下蹲 / 下滑（S、↓ / 手柄 B / 触屏下滑）。</summary>
        Slide = 1,

        /// <summary>主要交互：确认 / 出牌 / 攻击（鼠标左键 / 手柄 A / 点击右半屏）。</summary>
        Primary = 2,

        /// <summary>次要交互：取消 / 返回（鼠标右键 / 手柄 B / 点击左半屏）。</summary>
        Secondary = 3,

        /// <summary>暂停 / 菜单（Esc / P / 手柄 Start）。</summary>
        Pause = 4
    }

    /// <summary>
    /// 一帧输入快照：按下事件 + 轴，<b>只读</b>。
    /// <para>
    /// <b>为什么"按下"必须是位掩码而不是 <c>bool</c> 数组</b>：一帧最多 5 个动作，掩码是单个 <c>int</c>，
    /// 无分配、可按值拷贝、可一次比较；<see cref="IsPressed"/> 里再按动作取出。数组版本每帧都要
    /// 新建或清零，热路径上不可接受。
    /// </para>
    /// <para>
    /// <b>边沿语义</b>：<see cref="PressedMask"/> 只表示"本帧刚按下"，<b>不</b>表示"按住"。
    /// 按住状态走 <see cref="IInputSource.IsHeld"/>——两者混用会让"长按跳跃"变成"每帧都跳"，
    /// 内核侧表现为空中反复提交意图，很难从现象反推。
    /// </para>
    /// <para>
    /// <b>构造期归一化</b>：两个轴一律夹到 <c>-1..1</c>，<c>NaN</c>/<c>±Infinity</c> 归一化为 <c>0</c>。
    /// 归一化放在契约构造点而不是各输入源里，是为了让<b>任何</b>新增的 <see cref="IInputSource"/>
    /// 实现（手柄、录屏回放、AI 脚本）天然无法把污染值送进业务的移动逻辑——与
    /// <c>AdShowRequest</c> 在构造期把负 id 归一化为 0 是同一取舍。
    /// </para>
    /// </summary>
    public readonly struct InputFrame
    {
        /// <summary>
        /// 动作数量（= <see cref="InputAction"/> 的枚举项数）。
        /// <para>
        /// <b>为什么要这个常量</b>：C# 的 <c>int</c> 移位只取低 5 位，<c>1 &lt;&lt; 99</c> 会静默变成
        /// <c>1 &lt;&lt; 3</c>（<see cref="InputAction.Slide"/>）。不做越界判定的话，一个非法枚举值会
        /// <b>冒充成另一个合法动作</b>——这是最难从现象反推的一类 bug。新增动作时必须同步改这里。
        /// </para>
        /// </summary>
        public const int ActionCount = 5;

        /// <summary>构造一帧输入快照（轴会被夹到 <c>-1..1</c>，<c>NaN</c> 归零）。</summary>
        /// <param name="pressedMask">本帧刚按下的动作位掩码：<c>1 &lt;&lt; (int)InputAction</c>。</param>
        /// <param name="horizontal">横向轴（-1 左 .. +1 右）。</param>
        /// <param name="vertical">纵向轴（-1 下 .. +1 上）。</param>
        public InputFrame(int pressedMask, float horizontal, float vertical)
        {
            PressedMask = pressedMask;
            Horizontal = NormalizeAxis(horizontal);
            Vertical = NormalizeAxis(vertical);
        }

        /// <summary>本帧刚按下的动作位掩码（<c>1 &lt;&lt; (int)InputAction</c>；0 = 无）。</summary>
        public int PressedMask { get; }

        /// <summary>横向轴，已归一到 <c>-1..1</c>（0 = 无输入）。</summary>
        public float Horizontal { get; }

        /// <summary>纵向轴，已归一到 <c>-1..1</c>（0 = 无输入）。</summary>
        public float Vertical { get; }

        /// <summary>
        /// 本帧是否刚按下某个动作。
        /// <para>非法枚举值（负数 / 超过 <see cref="InputAction.Pause"/>）一律返回 <c>false</c>，
        /// 绝不因移位截断而误判成别的动作。</para>
        /// </summary>
        /// <param name="action">待查询的动作。</param>
        /// <returns>刚按下返回 <c>true</c>。</returns>
        public bool IsPressed(InputAction action)
        {
            int index = (int)action;
            if (index < 0 || index >= ActionCount)
            {
                return false;
            }

            return (PressedMask & (1 << index)) != 0;
        }

        /// <summary>
        /// 轴归一化：<c>NaN</c>/无穷 → 0，其余夹到 <c>-1..1</c>。
        /// <para><b>为什么负数也要显式判范围</b>：<c>Math.Clamp</c> 在 .NET Standard 2.1 有，
        /// 但 <c>NaN</c> 与任何值比较都为 <c>false</c>，会被原样漏过去；这里先判有限性再夹。</para>
        /// </summary>
        /// <param name="value">原始轴值。</param>
        /// <returns>-1..1 之间的有限值。</returns>
        private static float NormalizeAxis(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 0f;
            }

            if (value > 1f)
            {
                return 1f;
            }

            if (value < -1f)
            {
                return -1f;
            }

            return value;
        }
    }

    /// <summary>
    /// 输入源端口：把手柄 / 触屏 / 键鼠 / 回放脚本统一成"每帧拉一次"的拉取式接口。
    /// <para>
    /// <b>为什么是拉取（Poll）而不是事件回调</b>：玩法在固定帧管线（或 Unity 的 <c>Update</c>）里推进，
    /// 拉取式天然把"这一帧的输入"和"这一帧的推进"对齐；事件回调会在 <c>Update</c> 之间插入，
    /// 导致同一帧里既有输入事件又有物理步进，时序无法复现。
    /// </para>
    /// <para>
    /// <b>实现约束</b>：
    /// 1) <see cref="Poll"/> 必须把"本帧按下"清空——连续调用第二次返回空帧（边沿只出一次）；
    /// 2) <see cref="IsHeld"/> 是持续态，<b>不</b>受 <see cref="Poll"/> 清空影响；
    /// 3) 两个方法都不得抛异常，无输入时返回空帧 / <c>false</c>（设备断连、无触摸、焦点丢失都算正常状态）。
    /// </para>
    /// </summary>
    public interface IInputSource
    {
        /// <summary>
        /// 拉取并清空"本帧按下"集合（边沿触发，重复调用第二次返回空）。
        /// <para>轴随本帧快照一并返回；返回后源内的轴归零，下一帧重新累积。</para>
        /// </summary>
        /// <returns>本帧输入快照（无输入时 <see cref="InputFrame.PressedMask"/> 为 0、轴为 0）。</returns>
        InputFrame Poll();

        /// <summary>某动作当前是否按住（持续态，与 <see cref="Poll"/> 的边沿语义分开）。</summary>
        /// <param name="action">待查询的动作。</param>
        /// <returns>按住返回 <c>true</c>；非法动作与无该设备时返回 <c>false</c>。</returns>
        bool IsHeld(InputAction action);
    }
}
