using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Morton 排序 + 自底向上的紧凑 SVO 构建器（性能优化 P0-4 方案 A）。
///
/// 旧实现（SVOBuilder.Insert）的问题：
///   - 每个内部节点无条件分配 8 个子槽，而实际平均只有 2~3 个子非空
///     → 2201 万体素时代码里要分配 4000 万~8000 万槽（0.6~1.3GB），且 CPU 镜像 + GPU buffer 各一份；
///   - 每个体素从根递归下降 depth 层（22M × 11 ≈ 2.4 亿次指针追逐）。
///
/// 本实现的步骤：
///   1) 为每个活跃体素计算 Morton 码（与子槽编码一致：bit0=X, bit1=Y, bit2=Z）；
///   2) LSD 基数排序（只排有效位）→ 同坐标体素必然相邻，顺带完成"后写覆盖"去重与墓碑标记；
///   3) 叶子节点按 Morton 顺序连续写入节点池；
///   4) 逐层把 ≤8 个同前缀子节点归并成一个父节点，**只分配实际存在的子槽**（紧凑存储）；
///   5) 根节点固定放在 pool[0]，与 shader 的 nodeIdx=0 约定一致。
///
/// 节点语义（与 shader 约定）：
///   - 内部节点：childMask 的 bit c 表示第 c 象限存在；data = 子槽起始索引，
///     第 c 个子节点索引 = data + popcount(childMask & ((1&lt;&lt;c)-1))；
///   - 叶子节点：flags bit8 = 1，data = materialIndex；
///   - childMask == 0 且非叶子 = 空节点（运行时删除的体素覆写为此形态，遍历时跳过）。
/// </summary>
public static class SVOMortonBuilder
{
    public sealed class Result
    {
        public List<SVONode> nodes;       // nodes[0] = 根节点
        public int[]  sortedVoxelIdx;     // 去重后的活跃体素索引（按 Morton 升序，二分拾取用）
        public int    activeCount;
        public int    duplicateCount;     // 被同坐标后写覆盖的条目数
        public int    skippedCount;       // 越界/被过滤的条目数
        public int    depth;
        public Vector3Int rootMin;
    }

    /// <summary>
    /// 构建紧凑 SVO。
    /// </summary>
    /// <param name="voxels">全局体素表（去重时会对被覆盖条目写入墓碑 materialIndex = -1）</param>
    /// <param name="rootMin">根节点最小角（scene-space 整数）</param>
    /// <param name="depth">八叉树深度（rootSide = 1 &lt;&lt; depth）</param>
    public static Result Build(List<VoxelElement> voxels, Vector3Int rootMin, int depth)
    {
        if (depth < 1) depth = 1;
        if (depth > 21) depth = 21; // 64 位 Morton 每轴最多 21 位

        int rootSide = 1 << depth;
        int total = voxels.Count;

        // ---- 1. 统计活跃条目数（越界过滤）----
        int active = 0;
        for (int i = 0; i < total; i++)
        {
            var v = voxels[i];
            if (v.materialIndex < 0) continue;
            int x = v.worldPos.x - rootMin.x;
            int y = v.worldPos.y - rootMin.y;
            int z = v.worldPos.z - rootMin.z;
            if (x < 0 || x >= rootSide || y < 0 || y >= rootSide || z < 0 || z >= rootSide) continue;
            active++;
        }

        if (active == 0)
        {
            return new Result
            {
                nodes = new List<SVONode>(0),
                sortedVoxelIdx = Array.Empty<int>(),
                activeCount = 0,
                depth = depth,
                rootMin = rootMin,
            };
        }

        // ---- 2. 填 Morton 码 ----
        var keys = new ulong[active];
        var idxs = new int[active];
        int k = 0;
        int skipped = 0;
        for (int i = 0; i < total; i++)
        {
            var v = voxels[i];
            if (v.materialIndex < 0) continue;
            int x = v.worldPos.x - rootMin.x;
            int y = v.worldPos.y - rootMin.y;
            int z = v.worldPos.z - rootMin.z;
            if (x < 0 || x >= rootSide || y < 0 || y >= rootSide || z < 0 || z >= rootSide) { skipped++; continue; }
            keys[k] = Morton(x, y, z);
            idxs[k] = i;
            k++;
        }

        // ---- 3. 基数排序（稳定：同 Morton 保持原始索引升序 → 后写覆盖语义）----
        int rounds = Mathf.Max(1, (3 * depth + 7) / 8);
        RadixSort(keys, idxs, active, rounds);

        // ---- 4. 去重：同坐标只保留最后一个（就地压缩 keys/idxs，省一份 176MB 临时数组）----
        int a = 0;
        int dup = 0;
        for (int i = 0; i < active; i++)
        {
            if (i + 1 < active && keys[i] == keys[i + 1])
            {
                int dead = idxs[i];
                var dv = voxels[dead];
                if (dv.materialIndex >= 0)
                {
                    dv.materialIndex = -1; // 墓碑：保证"一格只有一条活跃条目"
                    voxels[dead] = dv;
                }
                dup++;
                continue;
            }
            keys[a] = keys[i];
            idxs[a] = idxs[i];
            a++;
        }

        // ---- 5. 节点池：pool[0] 预留给根节点，叶子按 Morton 顺序连续追加 ----
        //  内部节点数可以精确算出来（无需猜一个 a/2 之类的经验值，避免 List 扩容把峰值翻倍）：
        //  相邻两条 entry 的 keys 异或后的最高位 b → 只在 L ≤ ⌊b/3⌋ 的层上新开分组。
        long internalCount = depth; // 第 0 条 entry 在 1..depth 每一层都开启一个新分组
        for (int i = 1; i < a; i++)
        {
            ulong x = keys[i] ^ keys[i - 1];
            if (x != 0) internalCount += HighestBit(x) / 3;
        }

        var pool = new List<SVONode>(1 + a + (int)internalCount);
        pool.Add(default(SVONode)); // [0] = 根（构建完成后回填）

        for (int i = 0; i < a; i++)
        {
            var v = voxels[idxs[i]];
            pool.Add(SVONode.MakeLeaf(v.materialIndex));
        }

        // ---- 6. 逐层归并（自底向上）----
        //  第 L 层的分组前缀 = keys >> (3L)，子象限 = (keys >> (3L-3)) & 7。
        //  每层都直接从**条目数组**扫描分组（keys[i] 的 i 是体素条目下标，不能拿分组序号去索引 keys）。
        //  上一层节点按条目顺序连续写入 pool，所以「组 g 的子节点起始槽 = prevBase + 该组之前已结束的兄弟组数」，
        //  用一个单调递增的 childOffset 即可推算，无需额外的分组索引数组（省 2×4×N 字节）。
        int prevBase = 1;   // 上一层节点在 pool 中的起始槽（第 0 层 = 叶子，从 1 开始连续）
        int level = 0;
        int rootPoolIdx = -1;
        while (true)
        {
            level++;
            int shift = 3 * level;
            int childShift = shift - 3;
            int levelBase = pool.Count; // 本层第一个节点的槽位
            int nc = 0;
            int childOffset = 0;        // 上一层已结束的分组数 = 当前组的首个子槽偏移

            int i = 0;
            while (i < a)
            {
                ulong prefix = keys[i] >> shift;
                int firstChild = prevBase + childOffset;
                uint mask = 0;

                do
                {
                    mask |= 1u << (int)((keys[i] >> childShift) & 7UL);
                    i++;
                    // 上一层分组边界（子象限变化处）→ 下一个父节点的子槽起点后移
                    if (i < a && (keys[i] >> childShift) != (keys[i - 1] >> childShift)) childOffset++;
                }
                while (i < a && (keys[i] >> shift) == prefix);

                // 子节点在 pool 中连续且按 c 升序 → data 指向首个子节点即可
                pool.Add(new SVONode
                {
                    childMask = mask,
                    data = (uint)firstChild,
                    flags = 0,
                });
                nc++;
            }

            prevBase = levelBase;

            if (nc == 1 && level >= depth)
            {
                rootPoolIdx = levelBase;
                break;
            }
            if (level > depth + 4)
            {
                // 安全阀：理论上不可达（depth 层后必然只剩一组）
                rootPoolIdx = levelBase;
                break;
            }
        }

        // ---- 7. 根节点回填到 pool[0]（shader 约定根索引 = 0）----
        if (rootPoolIdx > 0) pool[0] = pool[rootPoolIdx];
        // 根节点副本落在池尾时直接移除，避免留一个无引用槽
        if (pool.Count > 1 && rootPoolIdx == pool.Count - 1) pool.RemoveAt(pool.Count - 1);

        return new Result
        {
            nodes = pool,
            sortedVoxelIdx = a == active ? idxs : TrimArray(idxs, a),
            activeCount = a,
            duplicateCount = dup,
            skippedCount = skipped,
            depth = depth,
            rootMin = rootMin,
        };
    }

    // ============================================================
    //  子槽寻址（与 shader 的 SVORank 对应）
    // ============================================================

    /// <summary>第 childIndex 个子节点在紧凑子槽数组中的偏移。</summary>
    public static int Rank(uint childMask, int childIndex)
    {
        if (childIndex <= 0) return 0;
        return PopCount(childMask & ((1u << childIndex) - 1u));
    }

    public static int PopCount(uint v)
    {
        v = v - ((v >> 1) & 0x55555555u);
        v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
        v = (v + (v >> 4)) & 0x0F0F0F0Fu;
        return (int)((v * 0x01010101u) >> 24);
    }

    // ============================================================
    //  Morton 编码（bit3i = X_i, bit3i+1 = Y_i, bit3i+2 = Z_i）
    //  与 childMask 的编码 c = cx | (cy &lt;&lt; 1) | (cz &lt;&lt; 2) 一致
    // ============================================================

    private static readonly ulong[] sSpreadX = BuildSpreadTable(0);
    private static readonly ulong[] sSpreadY = BuildSpreadTable(1);
    private static readonly ulong[] sSpreadZ = BuildSpreadTable(2);

    private static ulong[] BuildSpreadTable(int axis)
    {
        var table = new ulong[256];
        for (int v = 0; v < 256; v++)
        {
            ulong t = 0;
            for (int b = 0; b < 8; b++)
            {
                if (((v >> b) & 1) == 0) continue;
                t |= 1UL << (3 * b + axis);
            }
            table[v] = t;
        }
        return table;
    }

    /// <summary>3D Morton 码（每轴取低 21 位）。</summary>
    public static ulong Morton(int x, int y, int z)
    {
        ulong m = sSpreadX[x & 0xFF] | sSpreadY[y & 0xFF] | sSpreadZ[z & 0xFF];
        m |= (sSpreadX[(x >> 8) & 0xFF] | sSpreadY[(y >> 8) & 0xFF] | sSpreadZ[(z >> 8) & 0xFF]) << 24;
        m |= (sSpreadX[(x >> 16) & 0x1F] | sSpreadY[(y >> 16) & 0x1F] | sSpreadZ[(z >> 16) & 0x1F]) << 48;
        return m;
    }

    // ============================================================
    //  LSD 基数排序（8 bit × rounds，稳定）
    // ============================================================

    private static void RadixSort(ulong[] keys, int[] idx, int count, int rounds)
    {
        if (count <= 1) return;

        var bufKeys = new ulong[count];
        var bufIdx  = new int[count];
        var hist    = new int[256];

        ulong[] srcK = keys, dstK = bufKeys;
        int[]   srcI = idx,  dstI = bufIdx;

        for (int r = 0; r < rounds; r++)
        {
            int shift = r * 8;
            Array.Clear(hist, 0, 256);

            for (int i = 0; i < count; i++)
                hist[(int)((srcK[i] >> shift) & 0xFF)]++;

            int sum = 0;
            for (int b = 0; b < 256; b++)
            {
                int c = hist[b];
                hist[b] = sum;
                sum += c;
            }

            for (int i = 0; i < count; i++)
            {
                int b = (int)((srcK[i] >> shift) & 0xFF);
                int d = hist[b]++;
                dstK[d] = srcK[i];
                dstI[d] = srcI[i];
            }

            var tk = srcK; srcK = dstK; dstK = tk;
            var ti = srcI; srcI = dstI; dstI = ti;
        }

        // 结果不在原数组时拷回
        if (!ReferenceEquals(srcK, keys))
        {
            Array.Copy(srcK, keys, count);
            Array.Copy(srcI, idx, count);
        }
    }

    private static int HighestBit(ulong x)
    {
        int b = 0;
        if (x >> 32 != 0) { b += 32; x >>= 32; }
        if (x >> 16 != 0) { b += 16; x >>= 16; }
        if (x >> 8 != 0) { b += 8; x >>= 8; }
        if (x >> 4 != 0) { b += 4; x >>= 4; }
        if (x >> 2 != 0) { b += 2; x >>= 2; }
        if (x >> 1 != 0) { b += 1; }
        return b;
    }

    private static int[] TrimArray(int[] src, int count)
    {
        if (count == src.Length) return src;
        var dst = new int[count];
        Array.Copy(src, dst, count);
        return dst;
    }
}
