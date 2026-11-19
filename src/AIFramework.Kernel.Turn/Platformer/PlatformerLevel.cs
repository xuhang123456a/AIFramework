#nullable enable
using AIFramework.Contracts.Gameplay.Turn;

namespace AIFramework.Kernel.Turn.Platformer
{
    /// <summary>
    /// 内置关卡表：把"世界多宽、平台在哪、收集品在哪、出生点在哪"收在一处。
    /// <para>
    /// <b>关卡数据后续由配置表提供</b>：这里的三关是 Phase 1 的硬编码样例，目的是让内核在没有资源管线、
    /// 没有物理引擎、没有引擎时钟的情况下也能跑通"重力 / 跳跃 / 单向平台 / 收集品 / 终点"。
    /// 接配置表（Luban 导出的只读关卡行）时只需要把 <see cref="BuildPlatforms"/> /
    /// <see cref="BuildCollectibles"/> 的取数源换掉，<see cref="PlatformerWorld"/> 与物理分支一行都不用改。
    /// </para>
    /// <para>
    /// <b>数据格式</b>：平台用扁平 <c>float[]</c>，每 4 个数是一块平台（<c>x, y, width, height</c>，
    /// 左下角口径）；收集品每 2 个数一个（<c>x, y</c>，中心点口径）。用扁平数组而不是自定义结构体数组，
    /// 是为了让"一关就是一张表"能在一屏内读完，也避免为纯数据再造一个类型。
    /// </para>
    /// <para>
    /// <b>关卡 1（宽 40）</b>：地面横贯全图，没有坑，用来验证"走到底即胜利"与跳跃曲线。
    /// <b>关卡 2（宽 48）</b>：四段地面 + 三个 4 米宽的坑，用来验证"走出平台边缘 → 掉出世界判负"。
    /// <b>关卡 3（宽 56）</b>：四段地面 + 四块浮台，平台与收集品数量都取到上限 8。
    /// </para>
    /// <para>
    /// <b>出生点</b>：三关都固定在 <c>x = 1</c>，<c>y</c> 取第 0 块平台（出生平台）的顶面，因此
    /// 玩家一进场就是"站在地上"的状态，不会先掉一帧。
    /// </para>
    /// <para>
    /// <b>终点</b>：就是右墙（<c>WorldWidth - PlayerWidth</c>）——玩家左沿贴到右墙即算到达终点线，
    /// 因此"能否走到右端"与"能否通关"是同一件事，不存在右墙之前还有一段走不到的装饰地形。
    /// </para>
    /// <para>
    /// <b>零分配</b>：本类只有静态只读表与静态方法；<see cref="BuildPlatforms"/> / <see cref="BuildCollectibles"/>
    /// 只写调用方预先分配的定长数组，自身不申请托管堆内存。
    /// </para>
    /// </summary>
    internal static class PlatformerLevel
    {
        /// <summary>内置关卡数量（<c>LevelId</c> 只有 1..3 有效，其它夹到 1）。</summary>
        internal const int LevelCount = 3;

        /// <summary>单关平台数量上限（内核据此分配定长数组）。</summary>
        internal const int MaxPlatforms = 8;

        /// <summary>单关收集品数量上限（内核据此分配定长数组）。</summary>
        internal const int MaxCollectibles = 8;

        /// <summary>平台数据步长：每 4 个 <c>float</c> 描述一块平台。</summary>
        private const int PlatformStride = 4;

        /// <summary>收集品数据步长：每 2 个 <c>float</c> 描述一个收集品。</summary>
        private const int CollectibleStride = 2;

        /// <summary>三关共用的出生点横坐标（米）。</summary>
        private const float SpawnX = 1f;

        /// <summary>第 1 关平台：地面横贯全图（0..40），上面 5 块单向浮台。</summary>
        private static readonly float[] LevelOnePlatforms =
        {
            0f, 0f, 40f, 1f,
            6f, 1.2f, 4f, 1f,
            13f, 2.4f, 3f, 1f,
            20f, 1.2f, 5f, 1f,
            27f, 3f, 4f, 1f,
            33f, 1.2f, 3f, 1f
        };

        /// <summary>第 1 关收集品：地面 1 个 + 每块浮台上方各 1 个。</summary>
        private static readonly float[] LevelOneCollectibles =
        {
            3f, 1.6f,
            8f, 3.1f,
            14.5f, 4.3f,
            22.5f, 3.1f,
            29f, 4.9f,
            34.5f, 3.1f
        };

        /// <summary>第 2 关平台：四段地面（12 / 8 / 8 / 8 米）与三块浮台，地面之间是三个 4 米宽的坑。</summary>
        private static readonly float[] LevelTwoPlatforms =
        {
            0f, 0f, 12f, 1f,
            8f, 1.2f, 8f, 1f,
            16f, 0f, 8f, 1f,
            18f, 1.2f, 4f, 1f,
            28f, 0f, 8f, 1f,
            31f, 1.2f, 4f, 1f,
            40f, 0f, 8f, 1f
        };

        /// <summary>第 2 关收集品：地面与浮台各若干，第一个浮台正上方那颗需要跳起来才拿得到。</summary>
        private static readonly float[] LevelTwoCollectibles =
        {
            3f, 1.6f,
            6f, 1.6f,
            10f, 3.1f,
            20f, 1.6f,
            20f, 3.1f,
            33f, 3.1f,
            44f, 1.6f
        };

        /// <summary>第 3 关平台：四段地面（18 / 10 / 8 / 8 米）+ 四块高低不一的浮台，数量取到 8 块上限。</summary>
        private static readonly float[] LevelThreePlatforms =
        {
            0f, 0f, 18f, 1f,
            6f, 1.2f, 4f, 1f,
            13f, 2.4f, 4f, 1f,
            22f, 0f, 10f, 1f,
            25f, 1.2f, 4f, 1f,
            36f, 0f, 8f, 1f,
            39f, 2.4f, 4f, 1f,
            48f, 0f, 8f, 1f
        };

        /// <summary>第 3 关收集品：8 个，数量取到上限。</summary>
        private static readonly float[] LevelThreeCollectibles =
        {
            3f, 1.6f,
            8f, 3.1f,
            15f, 4.3f,
            25f, 1.6f,
            27f, 3.1f,
            40f, 1.6f,
            41f, 4.3f,
            52f, 1.6f
        };

        /// <summary>
        /// 把任意 <c>LevelId</c> 夹到内置关卡：只有 <c>2</c> / <c>3</c> 命中其它两关，
        /// 其余（含 0、负数、4 以上）一律第 1 关，并由 <see cref="PlatformerWorld.LevelId"/> 回报夹取后的值。
        /// </summary>
        internal static int NormalizeLevelId(int levelId)
        {
            if (levelId == 2 || levelId == 3)
            {
                return levelId;
            }

            return 1;
        }

        /// <summary>本关出生点横坐标（米）。</summary>
        internal static float SpawnXOf(int levelId)
        {
            return SpawnX;
        }

        /// <summary>本关平台数量（5..8）。</summary>
        internal static int PlatformCount(int levelId)
        {
            return PlatformsOf(levelId).Length / PlatformStride;
        }

        /// <summary>本关收集品数量（5..8）。</summary>
        internal static int CollectibleCount(int levelId)
        {
            return CollectiblesOf(levelId).Length / CollectibleStride;
        }

        /// <summary>世界宽度（米）：所有平台右沿的最大值（地面平台横贯全图，因此它就是世界宽度）。</summary>
        internal static float WorldWidth(int levelId)
        {
            float[] data = PlatformsOf(levelId);
            float maxRight = 0f;

            for (int i = 0; i + 3 < data.Length; i += PlatformStride)
            {
                float right = data[i] + data[i + 2];
                if (right > maxRight)
                {
                    maxRight = right;
                }
            }

            return maxRight;
        }

        /// <summary>
        /// 把本关平台写进调用方预先分配的定长数组（只写前 <see cref="PlatformCount"/> 个槽位）。
        /// 平台编号从 1 开始，顺序即数据顺序，第 0 个是出生平台。
        /// </summary>
        internal static void BuildPlatforms(int levelId, PlatformerView[] buffer)
        {
            float[] data = PlatformsOf(levelId);
            int count = data.Length / PlatformStride;

            for (int i = 0; i < count; i++)
            {
                int offset = i * PlatformStride;
                buffer[i] = new PlatformerView(
                    i + 1,
                    data[offset],
                    data[offset + 1],
                    data[offset + 2],
                    data[offset + 3]);
            }
        }

        /// <summary>
        /// 把本关收集品写进调用方预先分配的定长数组（只写前 <see cref="CollectibleCount"/> 个槽位），
        /// 全部初始化为未收集。编号从 1 开始，顺序即数据顺序。
        /// </summary>
        internal static void BuildCollectibles(int levelId, CollectibleView[] buffer)
        {
            float[] data = CollectiblesOf(levelId);
            int count = data.Length / CollectibleStride;

            for (int i = 0; i < count; i++)
            {
                int offset = i * CollectibleStride;
                buffer[i] = new CollectibleView(
                    i + 1,
                    data[offset],
                    data[offset + 1],
                    false);
            }
        }

        /// <summary>按关卡号取平台表；调用前必须已经过 <see cref="NormalizeLevelId"/> 夹取。</summary>
        private static float[] PlatformsOf(int levelId)
        {
            if (levelId == 2)
            {
                return LevelTwoPlatforms;
            }

            if (levelId == 3)
            {
                return LevelThreePlatforms;
            }

            return LevelOnePlatforms;
        }

        /// <summary>按关卡号取收集品表；调用前必须已经过 <see cref="NormalizeLevelId"/> 夹取。</summary>
        private static float[] CollectiblesOf(int levelId)
        {
            if (levelId == 2)
            {
                return LevelTwoCollectibles;
            }

            if (levelId == 3)
            {
                return LevelThreeCollectibles;
            }

            return LevelOneCollectibles;
        }
    }
}
