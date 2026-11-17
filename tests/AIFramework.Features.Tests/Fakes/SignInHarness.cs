using System;
using AIFramework.Contracts.Meta;
using AIFramework.Features.DailySignIn;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>
    /// 签到切片的一次性装配：真实 <see cref="SignInHandler"/> / <see cref="SignInPresenter"/>
    /// + 真实 <c>CqeBus</c>（经 <see cref="RecordingBus"/> 记录）+ 全部桩端口。
    /// </summary>
    public sealed class SignInHarness
    {
        /// <summary>补签卡道具 ID（与切片内常量一致，写在这里只是为了让用例读得懂）。</summary>
        public const int RetroactiveCardItemId = 9001;

        private SignInHarness(
            FakeTimeProvider time,
            RecordingBus bus,
            FakeInventoryQueryHandler inventory,
            FakeSignInView view,
            SignInHandler handler,
            SignInPresenter presenter)
        {
            Time = time;
            Bus = bus;
            Inventory = inventory;
            View = view;
            Handler = handler;
            Presenter = presenter;
        }

        /// <summary>假时间源。</summary>
        public FakeTimeProvider Time { get; }

        /// <summary>记录型总线。</summary>
        public RecordingBus Bus { get; }

        /// <summary>背包查询桩。</summary>
        public FakeInventoryQueryHandler Inventory { get; }

        /// <summary>视图假实现。</summary>
        public FakeSignInView View { get; }

        /// <summary>被测处理器。</summary>
        public SignInHandler Handler { get; }

        /// <summary>被测 Presenter。</summary>
        public SignInPresenter Presenter { get; }

        /// <summary>当前周期第 1 天的绝对天序号。</summary>
        public int CycleStartDayIndex
        {
            get { return Handler.Snapshot.CycleStartDayIndex; }
        }

        /// <summary>按给定 UTC 起点装配一套夹具（子表为标准 7 行夹具，背包初始为空）。</summary>
        public static SignInHarness Create(DateTime startUtc)
        {
            FakeTimeProvider time = new FakeTimeProvider(startUtc);
            FakeTableLoader loader = FakeTableLoader.WithDefaultSignInReward();
            RecordingBus bus = new RecordingBus();
            FakeInventoryQueryHandler inventory = new FakeInventoryQueryHandler();
            FakeSignInView view = new FakeSignInView();

            // 发奖/扣道具由背包切片结算，这里只提供槽位，避免真实总线因未注册抛异常。
            bus.RegisterCommandHandler<GrantItemCommand>(new NoOpCommandHandler<GrantItemCommand>());
            bus.RegisterCommandHandler<ConsumeItemCommand>(new NoOpCommandHandler<ConsumeItemCommand>());
            bus.RegisterQueryHandler<InventoryQueryRequest, InventorySnapshot>(inventory);

            SignInHandler handler = new SignInHandler(bus, time, loader);
            handler.Register(bus);
            SignInPresenter presenter = new SignInPresenter(handler, view);

            return new SignInHarness(time, bus, inventory, view, handler, presenter);
        }

        /// <summary>发一条领取当日奖励命令。</summary>
        public void SendClaim()
        {
            Bus.Send(new ClaimDailySignInCommand());
        }

        /// <summary>发一条补签命令（参数是绝对天序号）。</summary>
        public void SendRetroactive(int dayIndex)
        {
            Bus.Send(new RetroactiveSignInCommand(dayIndex));
        }
    }
}
