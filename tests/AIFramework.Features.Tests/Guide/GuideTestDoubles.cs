using System;
using System.Collections.Generic;
using System.Text;
using AIFramework.Contracts.Cqe;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Cqe;
using AIFramework.Features.Guide;

namespace AIFramework.Features.Tests.Guide
{
    /// <summary>一条引导行的夹具定义（字段顺序与 <c>tables/TbGuideStep.json</c> 列顺序一致）。</summary>
    internal readonly struct GuideRowSpec
    {
        internal GuideRowSpec(int stepId, string title, string hint, int trigger, int targetId, int skippable)
        {
            StepId = stepId;
            Title = title;
            Hint = hint;
            Trigger = trigger;
            TargetId = targetId;
            Skippable = skippable;
        }

        internal int StepId { get; }

        internal string Title { get; }

        internal string Hint { get; }

        internal int Trigger { get; }

        internal int TargetId { get; }

        internal int Skippable { get; }
    }

    /// <summary>
    /// 引导子表加载桩：严格按 <c>tools/tablegen</c> 的布局造字节
    /// （小端 <c>[int32 rowCount]</c> + 每行 <c>stepId / [len]UTF-8 title / [len]UTF-8 hint / trigger / targetId / skippable</c>），
    /// 只在本测试目录内使用，不改共享 <c>Fakes/</c>。
    /// </summary>
    internal sealed class GuideTableStub : IBinaryTableLoader
    {
        internal const string TableName = "TbGuideStep";

        private readonly Dictionary<string, byte[]?> _tables;

        private GuideTableStub(Dictionary<string, byte[]?> tables)
        {
            _tables = tables;
        }

        /// <summary>一次都没成表的加载器：任何表都返回 <c>null</c>（表缺失场景）。</summary>
        internal static GuideTableStub WithoutTable()
        {
            return new GuideTableStub(new Dictionary<string, byte[]?>(StringComparer.Ordinal));
        }

        /// <summary>按给定行顺序造一张合法子表。</summary>
        internal static GuideTableStub WithRows(params GuideRowSpec[] rows)
        {
            return WithPayload(BuildPayload(rows));
        }

        /// <summary>直接挂一份字节（可用于空表 / 畸形字节 / 仓库真实 .bin）。</summary>
        internal static GuideTableStub WithPayload(byte[]? payload)
        {
            var tables = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            tables[TableName] = payload;
            return new GuideTableStub(tables);
        }

        /// <summary>
        /// 四步标准夹具：启动（不可跳）→ 进大厅（不可跳）→ 开商店（可跳）→ 打战斗 2001（可跳，targetId 限定）。
        /// 覆盖"targetId=0 不限定"与"targetId 限定"两种匹配语义。
        /// </summary>
        internal static GuideTableStub Standard()
        {
            return WithRows(
                new GuideRowSpec(1, "启动", "点击开始按钮", GuideTriggers.Launch, 0, 0),
                new GuideRowSpec(2, "进大厅", "点击大厅按钮", GuideTriggers.Lobby, 0, 0),
                new GuideRowSpec(3, "开商店", "点击商店入口", GuideTriggers.ShopOpen, 0, 1),
                new GuideRowSpec(4, "打战斗", "打完 2001 这一局", GuideTriggers.BattleFinished, 2001, 1));
        }

        internal static byte[] BuildPayload(GuideRowSpec[] rows)
        {
            return BuildPayload(rows, rows.Length);
        }

        /// <summary>按行内容造字节，但头部声明 <paramref name="declaredRowCount"/> 行（用于长度/截断反例）。</summary>
        internal static byte[] BuildPayload(GuideRowSpec[] rows, int declaredRowCount)
        {
            var bytes = new List<byte>(4 + (rows.Length * 40));
            AppendInt32(bytes, declaredRowCount);
            for (int i = 0; i < rows.Length; i++)
            {
                AppendInt32(bytes, rows[i].StepId);
                AppendUtf8(bytes, rows[i].Title);
                AppendUtf8(bytes, rows[i].Hint);
                AppendInt32(bytes, rows[i].Trigger);
                AppendInt32(bytes, rows[i].TargetId);
                AppendInt32(bytes, rows[i].Skippable);
            }

            return bytes.ToArray();
        }

        public byte[]? Load(string tableName)
        {
            byte[]? payload;
            return _tables.TryGetValue(tableName, out payload) ? payload : null;
        }

        private static void AppendInt32(List<byte> target, int value)
        {
            target.Add((byte)(value & 0xFF));
            target.Add((byte)((value >> 8) & 0xFF));
            target.Add((byte)((value >> 16) & 0xFF));
            target.Add((byte)((value >> 24) & 0xFF));
        }

        private static void AppendUtf8(List<byte> target, string text)
        {
            byte[] raw = Encoding.UTF8.GetBytes(text);
            AppendInt32(target, raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                target.Add(raw[i]);
            }
        }
    }

    /// <summary>
    /// 装配：真实 <see cref="CqeBus"/> + 事件记录桩 + 切片 <see cref="GuideHandler"/>。
    /// 记录的是"切片真实广播了什么、顺序如何"，派发仍由真实总线承担。
    /// </summary>
    internal sealed class GuideHarness
    {
        private readonly List<object> _events;

        private GuideHarness(CqeBus bus, GuideHandler handler, List<object> events)
        {
            Bus = bus;
            Handler = handler;
            _events = events;
        }

        internal CqeBus Bus { get; }

        internal GuideHandler Handler { get; }

        internal IReadOnlyList<object> Events
        {
            get { return _events; }
        }

        internal static GuideHarness Create(GuideTableStub loader)
        {
            var bus = new CqeBus();
            var events = new List<object>();
            bus.Subscribe(new GuideEventSpy<GuideStepChangedEvent>(events));
            bus.Subscribe(new GuideEventSpy<GuideCompletedEvent>(events));

            var handler = new GuideHandler(bus, loader);
            handler.Register(bus);
            return new GuideHarness(bus, handler, events);
        }

        internal void Trigger(int trigger, int targetId)
        {
            Bus.Send(new GuideTriggerCommand(trigger, targetId));
        }

        internal void Advance(int stepId)
        {
            Bus.Send(new GuideAdvanceCommand(stepId));
        }

        internal void Skip(int stepId)
        {
            Bus.Send(new GuideSkipCommand(stepId));
        }

        internal void SkipAll()
        {
            Bus.Send(new GuideSkipAllCommand());
        }

        internal GuideSnapshot Query()
        {
            return Bus.Query<GuideQueryRequest, GuideSnapshot>(new GuideQueryRequest());
        }

        internal GuideSaveState Capture()
        {
            return Handler.CaptureState();
        }

        internal bool Restore(GuideSaveState state)
        {
            return Handler.TryRestoreState(in state);
        }

        /// <summary>当前步"触发命中 → 显式完成"两步走，用于把状态推到下一步。</summary>
        internal void CompleteStep(int stepId, int trigger, int targetId)
        {
            Trigger(trigger, targetId);
            Advance(stepId);
        }

        internal List<T> EventsOf<T>()
            where T : struct
        {
            var result = new List<T>();
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i] is T typed)
                {
                    result.Add(typed);
                }
            }

            return result;
        }

        internal int CountOf<T>()
            where T : struct
        {
            return EventsOf<T>().Count;
        }
    }

    /// <summary>事件桩：走真实 <see cref="CqeBus"/> 派发，按发生顺序记录。</summary>
    internal sealed class GuideEventSpy<TEvent> : IEventHandler<TEvent>
        where TEvent : struct, IEvent
    {
        private readonly List<object> _events;

        internal GuideEventSpy(List<object> events)
        {
            _events = events;
        }

        public void Handle(in TEvent evt)
        {
            _events.Add(evt);
        }
    }
}
