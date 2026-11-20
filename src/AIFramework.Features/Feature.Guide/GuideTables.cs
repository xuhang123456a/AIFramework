#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.Guide
{
    /// <summary>
    /// 引导触发类别取值域：必须与 <c>tables/TbGuideStep.json</c> 的 <c>trigger</c> 列语义一致。
    /// <para>
    /// 类别是封闭集合（0..5）：不在集合内的数值一律按"未知触发"在加载期拒绝，
    /// 因为一条永远不会被命中的引导步等于玩家永远卡在这一步。
    /// </para>
    /// </summary>
    public static class GuideTriggers
    {
        /// <summary>0：启动即触发（装配层在开局上报一次）。</summary>
        public const int Launch = 0;

        /// <summary>1：进入大厅。</summary>
        public const int Lobby = 1;

        /// <summary>2：打开商店。</summary>
        public const int ShopOpen = 2;

        /// <summary>3：完成一场战斗。</summary>
        public const int BattleFinished = 3;

        /// <summary>4：领取签到奖励。</summary>
        public const int SignInClaimed = 4;

        /// <summary>5：达到等级。</summary>
        public const int LevelReached = 5;

        /// <summary>受支持的触发类别数量（取值域上界 +1）。</summary>
        public const int Count = 6;

        /// <summary>是否为已知触发类别；未知类别既不匹配也不报错（命令通道），但加载期会拒绝。</summary>
        public static bool IsSupported(int trigger)
        {
            return trigger >= 0 && trigger < Count;
        }
    }

    /// <summary>
    /// 引导子表行：纯数据，对应 <c>TbGuideStep</c> 的一行。
    /// <para>
    /// <see cref="Trigger"/> 是"什么事实能让这一步变成可操作"的类别，
    /// <see cref="TargetId"/> 是同一类别下的进一步限定（0 = 不限定目标）。
    /// </para>
    /// </summary>
    public readonly struct GuideDef
    {
        public GuideDef(int stepId, string title, string hint, int trigger, int targetId, bool skippable)
        {
            StepId = stepId;
            Title = title;
            Hint = hint;
            Trigger = trigger;
            TargetId = targetId;
            Skippable = skippable;
        }

        /// <summary>步号（配置主键，子表内从 1 起连续）。</summary>
        public int StepId { get; }

        /// <summary>步标题（非空）。</summary>
        public string Title { get; }

        /// <summary>提示文案（允许为空串，表示"只高亮不写字"）。</summary>
        public string Hint { get; }

        /// <summary>触发类别，取值见 <see cref="GuideTriggers"/>。</summary>
        public int Trigger { get; }

        /// <summary>触发目标 id（0 = 不限定目标）。</summary>
        public int TargetId { get; }

        /// <summary>是否允许玩家跳过本步。</summary>
        public bool Skippable { get; }
    }

    /// <summary>
    /// 本切片专属子表入口（<c>TbGuideStep</c>）。
    /// <para>
    /// 布局（小端，由 <c>tools/tablegen</c> 按 JSON 列顺序生成）：
    /// <c>[int32 rowCount]</c> + rowCount × { <c>int32 stepId</c>; <c>[int32 titleLen]</c> + UTF-8 title;
    /// <c>[int32 hintLen]</c> + UTF-8 hint; <c>int32 trigger</c>; <c>int32 targetId</c>; <c>int32 skippable</c> }。
    /// </para>
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续；而 0 行是合法配置（<see cref="Count"/> == 0），
    /// 切片此时把整段引导视为"已完成"，不抛也不卡 UI。
    /// </para>
    /// <para>
    /// 解析完成后行按 <see cref="GuideDef.StepId"/> 升序排列，并强制"从 1 连续"，
    /// 因此 <c>stepId = 下标 + 1</c> 恒成立——状态机的位图与 O(1) 定位都依赖这条不变量。
    /// 步数上限 <see cref="MaxSteps"/>（31）：存档位图是 <c>int</c>，最高位是符号位，超过就放不下。
    /// </para>
    /// </summary>
    public sealed class GuideTables
    {
        /// <summary>子表名（与 <c>tables/TbGuideStep.json</c> 的 <c>table</c> 字段一致）。</summary>
        private const string TableNameValue = "TbGuideStep";

        /// <summary>头部：<c>[int32 rowCount]</c>。</summary>
        private const int HeaderBytes = 4;

        /// <summary>单个 int32 字段宽度（小端）。</summary>
        private const int Int32Bytes = 4;

        /// <summary>一行的最小可读字节数：6 个 int32（title / hint 每个至少 4 字节长度前缀）。</summary>
        private const int MinRowBytes = Int32Bytes * 6;

        /// <summary>
        /// 最大步数：<see cref="GuideSaveState"/> 用 <c>int</c> 位图存进度，最高位是符号位，
        /// 所以可取 31 位；超过就必须改存档格式，因此在加载期直接拒绝。
        /// </summary>
        private const int MaxSteps = 31;

        private readonly GuideDef[] _rows;

        /// <summary>加载并解析 <c>TbGuideStep</c>；表缺失抛 <see cref="InvalidOperationException"/>，数据非法抛 <see cref="InvalidDataException"/>。</summary>
        /// <exception cref="ArgumentNullException"><paramref name="loader"/> 为 <c>null</c>。</exception>
        /// <exception cref="InvalidOperationException">加载器返回 <c>null</c>，即子表缺失。</exception>
        /// <exception cref="InvalidDataException">截断、尾部残留、步号不连续、触发/可跳过/目标非法、标题为空、步数超上限。</exception>
        public GuideTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("引导子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数（引导步数）；0 是合法配置。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按下标取行（下标顺序即 <see cref="GuideDef.StepId"/> 升序，且 StepId == index + 1）；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public GuideDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "引导子表下标越界，当前行数 " + _rows.Length + "。");
            }

            return _rows[index];
        }

        private static GuideDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("引导子表头部长度不足 " + HeaderBytes + " 字节。");
            }

            PayloadReader reader = new PayloadReader(payload);
            int rowCount = reader.ReadInt32("rowCount");
            if (rowCount < 0)
            {
                throw new InvalidDataException("引导子表 rowCount 为负：" + rowCount + "。");
            }

            if (rowCount > MaxSteps)
            {
                // 存档位图是 int（最高位为符号位，可用 31 位）：超过 31 步就没法无损落盘，
                // 必须在加载期挡住而不是运行时静默丢进度。
                throw new InvalidDataException(
                    "引导子表声明 " + rowCount + " 步，超过上限 " + MaxSteps + "（存档位图放不下）。");
            }

            // 先做"声明行数 vs 剩余字节"前置校验，避免畸形头部触发超大分配。
            if ((long)rowCount * MinRowBytes > reader.Remaining)
            {
                throw new InvalidDataException(
                    "引导子表声明 " + rowCount + " 行，但只剩 " + reader.Remaining + " 字节，字节流被截断。");
            }

            GuideDef[] rows = new GuideDef[rowCount];
            HashSet<int> seenStepIds = new HashSet<int>();
            for (int i = 0; i < rowCount; i++)
            {
                int stepId = reader.ReadInt32("stepId");
                string title = reader.ReadUtf8("title");
                string hint = reader.ReadUtf8("hint");
                int trigger = reader.ReadInt32("trigger");
                int targetId = reader.ReadInt32("targetId");
                int skippable = reader.ReadInt32("skippable");

                if (title.Length == 0)
                {
                    throw new InvalidDataException("引导子表 title 不能为空，stepId " + stepId + "。");
                }

                if (!GuideTriggers.IsSupported(trigger))
                {
                    throw new InvalidDataException(
                        "引导子表 trigger 未知，stepId " + stepId + " 实际为 " + trigger + "。");
                }

                if (targetId < 0)
                {
                    throw new InvalidDataException(
                        "引导子表 targetId 不能为负，stepId " + stepId + " 实际为 " + targetId + "。");
                }

                if (skippable != 0 && skippable != 1)
                {
                    throw new InvalidDataException(
                        "引导子表 skippable 只能是 0/1，stepId " + stepId + " 实际为 " + skippable + "。");
                }

                if (!seenStepIds.Add(stepId))
                {
                    throw new InvalidDataException("引导子表 stepId 重复：" + stepId + "。");
                }

                rows[i] = new GuideDef(stepId, title, hint, trigger, targetId, skippable == 1);
            }

            if (reader.Remaining != 0)
            {
                throw new InvalidDataException(
                    "引导子表尾部残留 " + reader.Remaining + " 字节，声明行数与字节流长度不符。");
            }

            // 行序统一按步号升序；随后强制"从 1 连续"，让 stepId ↔ 下标 成为恒等映射。
            Array.Sort(rows, CompareByStepId);
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i].StepId != i + 1)
                {
                    throw new InvalidDataException(
                        "引导子表 stepId 必须从 1 连续，第 " + i + " 行实际为 " + rows[i].StepId + "。");
                }
            }

            return rows;
        }

        private static int CompareByStepId(GuideDef left, GuideDef right)
        {
            return left.StepId.CompareTo(right.StepId);
        }

        /// <summary>
        /// 小端字节流游标：每次读取都做越界检查，越界统一转 <see cref="InvalidDataException"/>。
        /// 只在构造期使用（允许分配），因此不做零分配优化。
        /// </summary>
        private sealed class PayloadReader
        {
            private readonly byte[] _payload;
            private int _offset;

            internal PayloadReader(byte[] payload)
            {
                _payload = payload;
                _offset = 0;
            }

            /// <summary>尚未读取的字节数。</summary>
            internal int Remaining
            {
                get { return _payload.Length - _offset; }
            }

            /// <summary>读一个小端 int32；不足 4 字节即判定截断。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal int ReadInt32(string fieldName)
            {
                if (Remaining < Int32Bytes)
                {
                    throw new InvalidDataException(
                        "引导子表读取 " + fieldName + " 时字节流已截断：偏移 " + _offset + "，剩余 " + Remaining + " 字节。");
                }

                int value = _payload[_offset]
                    | (_payload[_offset + 1] << 8)
                    | (_payload[_offset + 2] << 16)
                    | (_payload[_offset + 3] << 24);
                _offset += Int32Bytes;
                return value;
            }

            /// <summary>读"长度前缀 + UTF-8 字节"的 string；负长度或超出剩余字节即判定非法。</summary>
            /// <param name="fieldName">字段名，仅用于异常消息。</param>
            internal string ReadUtf8(string fieldName)
            {
                int length = ReadInt32(fieldName + "Len");
                if (length < 0)
                {
                    throw new InvalidDataException("引导子表 " + fieldName + " 长度为负：" + length + "。");
                }

                if (length > Remaining)
                {
                    throw new InvalidDataException(
                        "引导子表读取 " + fieldName + " 时字节流已截断：声明 " + length + " 字节，剩余 " + Remaining + " 字节。");
                }

                string text = Encoding.UTF8.GetString(_payload, _offset, length);
                _offset += length;
                return text;
            }
        }
    }
}
