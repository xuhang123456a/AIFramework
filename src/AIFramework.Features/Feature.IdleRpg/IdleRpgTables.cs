#nullable enable
using System;
using System.IO;
using AIFramework.Contracts.Meta;
using AIFramework.Contracts.Ports;

namespace AIFramework.Features.IdleRpg
{
    /// <summary>
    /// 本切片专属子表入口（<c>TbIdleStage</c>）。
    /// <para>
    /// 表缺失或校验不过一律显式抛异常，禁止静默按空表继续——空表和"表没打进包"是两件事。
    /// 布局（Phase 1 夹具）：小端 <c>[int32 rowCount]</c> +
    /// <c>rowCount × { int32 stageId; int32 enemyHp; int32 enemyAttack; int32 goldReward; int32 requiredPower; }</c>，单行 20 字节。
    /// </para>
    /// <para>
    /// 关卡序号强制"从 1 起逐行连续"：<c>stageId</c> 既不能重复也不能跳号。
    /// 这样抓关只需 <c>stage + 1</c>，不会出现"表里有个孤岛关卡谁也到不了"的脏配置。
    /// </para>
    /// </summary>
    public sealed class IdleRpgTables
    {
        private const string TableNameValue = "TbIdleStage";
        private const int HeaderBytes = 4;
        private const int RowBytes = 20;

        private readonly IdleStageDef[] _rows;

        public IdleRpgTables(IBinaryTableLoader loader)
        {
            if (loader == null)
            {
                throw new ArgumentNullException(nameof(loader));
            }

            byte[]? payload = loader.Load(TableNameValue);
            if (payload == null)
            {
                throw new InvalidOperationException("挂机 RPG 子表 '" + TableNameValue + "' 不存在，切片拒绝启用。");
            }

            _rows = Parse(payload);
        }

        /// <summary>子表行数；0 行是合法配置（空关卡表），不是加载失败。</summary>
        public int Count
        {
            get { return _rows.Length; }
        }

        /// <summary>按行序取第 <paramref name="index"/> 行；越界抛 <see cref="ArgumentOutOfRangeException"/>。</summary>
        public IdleStageDef Get(int index)
        {
            if (index < 0 || index >= _rows.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index,
                    "关卡行下标必须落在 0.." + (_rows.Length - 1) + " 之间。");
            }

            return _rows[index];
        }

        /// <summary>按关卡序号查行；子表已保证序号连续，找不到返回 <c>false</c>（不抛异常）。</summary>
        public bool TryFind(int stageId, out IdleStageDef def)
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i].StageId == stageId)
                {
                    def = _rows[i];
                    return true;
                }
            }

            def = default;
            return false;
        }

        private static IdleStageDef[] Parse(byte[] payload)
        {
            if (payload.Length < HeaderBytes)
            {
                throw new InvalidDataException("挂机 RPG 子表头部长度不足 4 字节。");
            }

            int rowCount = ReadInt32LittleEndian(payload, 0);
            if (rowCount < 0)
            {
                throw new InvalidDataException("挂机 RPG 子表声明行数为负：" + rowCount + "。");
            }

            long expectedLength = HeaderBytes + ((long)rowCount * RowBytes);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "挂机 RPG 子表长度 " + payload.Length + " 与声明行数 " + rowCount + " 不符，期望 " + expectedLength + " 字节。");
            }

            IdleStageDef[] rows = new IdleStageDef[rowCount];
            for (int i = 0; i < rowCount; i++)
            {
                int offset = HeaderBytes + (i * RowBytes);
                int stageId = ReadInt32LittleEndian(payload, offset);
                int enemyHp = ReadInt32LittleEndian(payload, offset + 4);
                int enemyAttack = ReadInt32LittleEndian(payload, offset + 8);
                int goldReward = ReadInt32LittleEndian(payload, offset + 12);
                int requiredPower = ReadInt32LittleEndian(payload, offset + 16);

                // 一处判定同时覆盖"重复"与"跳号"：行 i 的序号必须恰好是 i + 1。
                if (stageId != i + 1)
                {
                    throw new InvalidDataException(
                        "挂机 RPG 子表 stageId 必须从 1 起逐行连续，第 " + (i + 1) + " 行实际为 " + stageId
                        + "（重复或跳号均为非法配置）。");
                }

                if (enemyHp <= 0)
                {
                    throw new InvalidDataException(
                        "挂机 RPG 子表 enemyHp 必须大于 0，stageId " + stageId + " 实际为 " + enemyHp + "。");
                }

                if (enemyAttack < 0)
                {
                    throw new InvalidDataException(
                        "挂机 RPG 子表 enemyAttack 不能为负，stageId " + stageId + " 实际为 " + enemyAttack + "。");
                }

                if (goldReward < 0)
                {
                    throw new InvalidDataException(
                        "挂机 RPG 子表 goldReward 不能为负，stageId " + stageId + " 实际为 " + goldReward + "。");
                }

                if (requiredPower < 0)
                {
                    throw new InvalidDataException(
                        "挂机 RPG 子表 requiredPower 不能为负，stageId " + stageId + " 实际为 " + requiredPower + "。");
                }

                rows[i] = new IdleStageDef(stageId, enemyHp, enemyAttack, goldReward, requiredPower);
            }

            return rows;
        }

        private static int ReadInt32LittleEndian(byte[] payload, int offset)
        {
            int value = payload[offset]
                | (payload[offset + 1] << 8)
                | (payload[offset + 2] << 16)
                | (payload[offset + 3] << 24);
            return value;
        }
    }
}
