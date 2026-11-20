// 输入形状桩（T4-7）：只补 UnityEngine.Input / KeyCode / Touch / TouchPhase 的符号，与真实 API 同形。
// 不含任何设备轮询、触屏手势识别或按键映射逻辑——这些在真机上由引擎提供（Editor 里 touchCount 恒 0）。
using System;

namespace UnityEngine
{
    /// <summary>
    /// 按键枚举（形状桩）：<b>只列出适配层实际用到的键</b>，数值与真实 Unity 一致
    /// （Return=13、Escape=27、Space=32、A..Z=97..122、方向键 273..276）。
    /// 桩不参与任何键位判定，数值正确只为了"如果有人把 KeyCode 当整数用，行为不静默变化"。
    /// </summary>
    public enum KeyCode
    {
        /// <summary>手柄第 1 个键（多数手柄 A/✕，真实值 350）。</summary>
        JoystickButton0 = 350,

        /// <summary>手柄第 2 个键（B/○，真实值 351）。</summary>
        JoystickButton1 = 351,

        /// <summary>手柄第 8 个键（Start，真实值 357）。</summary>
        JoystickButton7 = 357,

        /// <summary>回车（主键盘 Enter）。</summary>
        Return = 13,

        /// <summary>Esc。</summary>
        Escape = 27,

        /// <summary>空格。</summary>
        Space = 32,

        /// <summary>字母 A。</summary>
        A = 97,

        /// <summary>字母 D。</summary>
        D = 100,

        /// <summary>字母 P。</summary>
        P = 112,

        /// <summary>字母 S。</summary>
        S = 115,

        /// <summary>字母 W。</summary>
        W = 119,

        /// <summary>小键盘 Enter（真实 Unity 与 Return 是两个键）。</summary>
        KeypadEnter = 271,

        /// <summary>方向键 ↑。</summary>
        UpArrow = 273,

        /// <summary>方向键 ↓。</summary>
        DownArrow = 274,

        /// <summary>方向键 →。</summary>
        RightArrow = 275,

        /// <summary>方向键 ←。</summary>
        LeftArrow = 276
    }

    /// <summary>
    /// 触摸阶段（形状桩）：数值与真实 Unity 一致，适配层按 <see cref="Began"/> / <see cref="Ended"/> 做手势判定。
    /// </summary>
    public enum TouchPhase
    {
        /// <summary>手指刚按下。</summary>
        Began = 0,

        /// <summary>手指移动中。</summary>
        Moved = 1,

        /// <summary>手指按住不动。</summary>
        Stationary = 2,

        /// <summary>手指抬起（一次触摸的最后一帧）。</summary>
        Ended = 3,

        /// <summary>触摸被系统取消（来电、手势冲突等），必须按"没发生过"处理。</summary>
        Canceled = 4
    }

    /// <summary>
    /// 单指触摸数据（形状桩）：真机里由引擎逐帧填充，桩只保证类型/成员与真实 API 同形。
    /// <para>
    /// <b>桩里所有成员恒为默认值</b>（<see cref="UnityEngine.Input.touchCount"/> 恒 0，因此没人会拿到实例）；
    /// 离线形状校验只能证明"代码调用的成员存在且类型正确"，<b>不能</b>证明滑动手势在真机上的判定正确——
    /// 那需要 PlayMode 真机（或 Unity Remote）验收。
    /// </para>
    /// </summary>
    public struct Touch
    {
        /// <summary>手指 id（同一根手指在 Began..Ended 期间保持不变；多指必须按 id 配对）。</summary>
        public int fingerId
        {
            get { return 0; }
        }

        /// <summary>当前屏幕位置（像素，左下原点）。</summary>
        public Vector2 position
        {
            get { return default; }
        }

        /// <summary>原始屏幕位置（像素，未经平台补偿；手势判定用 <see cref="position"/> 即可）。</summary>
        public Vector2 rawPosition
        {
            get { return default; }
        }

        /// <summary>相对上一帧的位移（像素）。</summary>
        public Vector2 deltaPosition
        {
            get { return default; }
        }

        /// <summary>本帧触摸阶段的时长（秒）。</summary>
        public float deltaTime
        {
            get { return 0f; }
        }

        /// <summary>当前触摸阶段。</summary>
        public TouchPhase phase
        {
            get { return TouchPhase.Canceled; }
        }

        /// <summary>连击次数（引擎维护；适配层不用它判点击，点击用"总位移小于阈值"判）。</summary>
        public int tapCount
        {
            get { return 0; }
        }

        /// <summary>压力（0~1）；无压感设备恒为 1 或 0，适配层不读。</summary>
        public float pressure
        {
            get { return 0f; }
        }
    }

    /// <summary>
    /// 旧版输入 API（形状桩）：真机里是引擎对键盘/鼠标/触摸/摇杆的统一轮询入口。
    /// <para>
    /// <b>为什么适配层用旧版 <c>UnityEngine.Input</c> 而不是新 Input System</b>：本工程的 Unity asmdef
    /// 没有引用 <c>Unity.InputSystem</c> 包，且演示场景的按钮走的是 <c>StandaloneInputModule</c>（同样是旧版输入）；
    /// 混用两套输入会把"键鼠不响应"变成包依赖问题。将来要换新输入只需新增一个 <c>IInputSource</c> 实现。
    /// </para>
    /// <para>
    /// <b>桩里的返回值</b>：一律"没有输入"（<c>false</c> / <c>0</c> / <c>touchCount = 0</c>）。
    /// 因此离线校验能证明适配层编译得过、接口实现完整，但<b>不能</b>证明按键真的驱动了控制器——那需要 PlayMode。
    /// </para>
    /// </summary>
    public static class Input
    {
        /// <summary>本帧是否刚按下某个键（边沿）。</summary>
        public static bool GetKeyDown(KeyCode key)
        {
            return false;
        }

        /// <summary>某个键当前是否按住（持续态）。</summary>
        public static bool GetKey(KeyCode key)
        {
            return false;
        }

        /// <summary>本帧是否刚松开某个键（边沿）。</summary>
        public static bool GetKeyUp(KeyCode key)
        {
            return false;
        }

        /// <summary>读取轴（未做死区/平滑的原始值，-1..1）。</summary>
        public static float GetAxisRaw(string axisName)
        {
            return 0f;
        }

        /// <summary>读取轴（引擎已按 Input Manager 的死区/灵敏度处理，-1..1）。</summary>
        public static float GetAxis(string axisName)
        {
            return 0f;
        }

        /// <summary>本帧鼠标某键是否刚按下（0 左 / 1 右 / 2 中）。</summary>
        public static bool GetMouseButtonDown(int button)
        {
            return false;
        }

        /// <summary>鼠标某键当前是否按住。</summary>
        public static bool GetMouseButton(int button)
        {
            return false;
        }

        /// <summary>鼠标是否被按住（本帧松开）。</summary>
        public static bool GetMouseButtonUp(int button)
        {
            return false;
        }

        /// <summary>鼠标屏幕位置（像素，左下原点）。</summary>
        public static Vector3 mousePosition
        {
            get { return default; }
        }

        /// <summary>当前活跃触摸数（真机才非 0；Editor 里恒 0）。</summary>
        public static int touchCount
        {
            get { return 0; }
        }

        /// <summary>取第 index 个触摸（0 ≤ index &lt; <see cref="touchCount"/>）。</summary>
        public static Touch GetTouch(int index)
        {
            return default;
        }

        /// <summary>当前平台是否支持触摸。</summary>
        public static bool touchSupported
        {
            get { return false; }
        }

        /// <summary>当前平台是否有鼠标。</summary>
        public static bool mousePresent
        {
            get { return true; }
        }
    }
}
