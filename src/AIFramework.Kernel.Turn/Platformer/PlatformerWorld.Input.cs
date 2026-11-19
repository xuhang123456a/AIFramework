#nullable enable
namespace AIFramework.Kernel.Turn.Platformer
{
    /// <summary>
    /// <see cref="PlatformerWorld"/> 的输入端口实现（<c>IPlatformerInput</c> 的"意图"部分）：左右移动与跳跃。
    /// <para>
    /// <b>输入语义</b>：对局结束后一律空操作；<see cref="SetMove(float)"/> 是"按住式"——
    /// 每次调用覆盖当前值（适配层每帧调一次，松手时传 0），<c>NaN</c> 当松手处理，超出 <c>-1..1</c> 的按满舵处理。
    /// </para>
    /// <para>
    /// <b>跳跃只在地面发起</b>：<see cref="IsOnGround"/> 为 false（含"已起跳但还没推进过"的那一瞬）一律返回 false
    /// 且不改任何状态，因此二段跳在这里就被拒掉了，不需要额外记录"已跳过几次"。
    /// </para>
    /// <para>
    /// <b>为什么单独拆文件</b>：本文件只有"意图 → 状态变化"；物理推进与终局判定在
    /// <c>PlatformerWorld.Physics.cs</c>，状态、视图与结算在 <c>PlatformerWorld.cs</c>。
    /// 三者都不超过 400 行，且仍是同一个 <c>partial</c> 类型，对外只有一个 <see cref="PlatformerWorld"/>。
    /// </para>
    /// <para>
    /// <b>零分配</b>：这里只有浮点赋值与比较，不申请任何托管堆内存。
    /// </para>
    /// </summary>
    public sealed partial class PlatformerWorld
    {
        /// <summary>
        /// 设置左右移动意图（按住式，每帧调用）：<c>-1</c> 全速向左、<c>+1</c> 全速向右、<c>0</c> 松手。
        /// <c>NaN</c> 当 <c>0</c>，超出量程的夹到 <c>±1</c>。对局结束后为空操作。
        /// </summary>
        public void SetMove(float direction)
        {
            if (IsFinished)
            {
                return;
            }

            _moveDirection = PlatformerRules.ClampMoveDirection(direction);
        }

        /// <summary>
        /// 起跳：置垂直速度 <c>8 m/s</c>，随后由 <see cref="Tick(double)"/> 积分成抛物线
        /// （解析顶点约 <c>1.4545</c> 米，滞空约 <c>0.727</c> 秒）。
        /// 只有站在平台上才能起跳，空中（含起跳后尚未推进的同一帧）返回 false 且不改任何状态。
        /// </summary>
        public bool TryJump()
        {
            if (IsFinished || !_isOnGround)
            {
                return false;
            }

            _playerVelocityY = PlatformerRules.JumpSpeed;
            _isOnGround = false;
            return true;
        }
    }
}
