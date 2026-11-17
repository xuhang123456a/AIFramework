using AIFramework.Contracts.Cqe;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>
    /// 空实现命令处理器：真实 <c>CqeBus</c> 未注册处理器时会抛异常，
    /// 因此发奖/扣道具这类"由背包切片结算"的命令需要一个占位槽位；断言只看 <see cref="RecordingBus"/> 的记录。
    /// </summary>
    /// <typeparam name="TCommand">命令类型。</typeparam>
    public sealed class NoOpCommandHandler<TCommand> : ICommandHandler<TCommand>
        where TCommand : struct, ICommand
    {
        /// <inheritdoc />
        public void Handle(in TCommand command)
        {
            // 故意为空：这里不模拟背包结算，避免测试重复断言别人的职责。
        }
    }
}
