using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SVO（Sparse Voxel Octree）构建器。
/// 从 VoxDataProvider 已加载的 voxelElements + materialElements 构建扁平化 GPU-ready 八叉树，
/// 并上传为 ComputeBuffer 全局着色器属性。
///
/// 构建流程:
///   Phase 1: Scene Bounds → gridOrigin / rootSide / depth + 合并全局 materialElements
///   Phase 2: Morton 排序 + 自底向上归并 → 紧凑子槽节点池（P0-4 方案 A）
///   Phase 3: GPU Upload → ComputeBuffer.SetData + Shader.SetGlobal*
///   Phase 4: 自发光体素 NEE 光源表
/// </summary>
public static class SVOBuilder
{
    // ---- GPU buffer 引用，供外部释放 ----
    private static ComputeBuffer sNodeBuf;
    private static ComputeBuffer sMatBuf;
    private static ComputeBuffer sEmissiveBuf;
    private static ComputeBuffer sAliasBuf;

    // ---- CPU 侧镜像（拾取/删除用，与 GPU buffer 同步维护）----
    private static List<SVONode>     sPool;         // 节点池副本（紧凑子槽布局）
    private static Vector3Int        sRootMinInt;   // 根最小角（scene-space 整数）
    private static Vector3           sRootMinWorld; // 根最小角（世界空间 = rootMin * scale）
    private static int               sDepth;        // 八叉树深度
    private static float             sScale;        // 体素缩放
    // gridCoord → voxelElements 索引：改为“按 Morton 排序的活跃索引数组 + 二分查找”，
    // 替代原先 2201 万项的 Dictionary<Vector3Int,int>（约 528~620MB、构建 2~5s）
    private static int[]             sSortedVoxelIdx;
    private static int               sActiveVoxelCount;
    private static HashSet<int> sEmissiveSet;              // 发光体素的 voxelElements 索引（重建光源表 O(E) 用）

    // ---- 运行时复用缓冲（拖刷高频调用，消除临时分配/GC）----
    private static readonly SVONode[]  sSingleNode = new SVONode[1];     // UploadNode 单节点上传

    /// <summary>SVO 树遍历栈项（CPU 拾取用，与 GPU TraverseSVO_Stack 同构）。</summary>
    private struct TravItem
    {
        public int        nodeIdx;
        public int        level;
        public Vector3Int nodeMin;
        public float      tMin;
        public float      tMax;
    }

    private static readonly List<TravItem> sTravStack = new List<TravItem>(64); // 树遍历栈复用
    private static readonly TravItem[]     sChildCands = new TravItem[8];       // 子节点收集复用
    private const int TravStackCap = 256; // 栈容量上限（每层最多4子×深度≤16，远够）

    /// <summary>SVO 已构建且 CPU 镜像可用（拾取/删除的前置条件）。</summary>
    public static bool IsReady => sPool != null && sPool.Count > 0 && sSortedVoxelIdx != null && sNodeBuf != null;

    /// <summary>grid 坐标 → 世界坐标（与 shader 的 grid*_SVOScale+_SVORootMin 一致）。</summary>
    public static Vector3 GridToWorld(Vector3Int gridCoord) => (Vector3)(gridCoord + sRootMinInt) * sScale;

    /// <summary>单个体素的世界空间尺寸（= _SVOScale）。</summary>
    public static float VoxelWorldSize => sScale;

    /// <summary>
    /// 体素删除成功通知（DXR 后端增量更新砖掩码用，避免一次编辑触发整场 BLAS 重建）。
    /// 参数：(场景空间绝对整数体素坐标 = gridCoord + sRootMinInt，删除后该格材质索引 -1)。
    /// 纯追加：不改变 RemoveVoxelAt 的任何既有语义；无订阅者时零开销。
    /// </summary>
    public static event System.Action<Vector3Int, int> VoxelRemoved;

    /// <summary>释放上一次 Build 创建的 ComputeBuffer 与 CPU 镜像。</summary>
    public static void Release()
    {
        if (sNodeBuf != null) { sNodeBuf.Release(); sNodeBuf = null; }
        if (sMatBuf  != null) { sMatBuf.Release();  sMatBuf = null; }
        if (sEmissiveBuf != null) { sEmissiveBuf.Release(); sEmissiveBuf = null; }
        if (sAliasBuf    != null) { sAliasBuf.Release();    sAliasBuf = null; }

        sPool = null;
        sSortedVoxelIdx = null;
        sActiveVoxelCount = 0;
        if (sEmissiveSet != null) sEmissiveSet.Clear();
        sRootMinInt = Vector3Int.zero;
        sRootMinWorld = Vector3.zero;
        sDepth = 0;
        sScale = 1f;
    }

    /// <summary>
    /// 从所有活跃资产构建 SVO 并上传 GPU。
    /// </summary>
    public static void Build(IReadOnlyList<VoxAsset> assets, float scale)
    {
        var provider = VoxDataProvider.Instance;
        if (provider == null)
        {
            Debug.LogWarning("[SVOBuilder] VoxDataProvider 未初始化");
            return;
        }

        // 收集所有活跃资产
        var activeAssets = new List<VoxAsset>();
        for (int i = 0; i < assets.Count; i++)
        {
            if (assets[i] != null && assets[i].isActive)
                activeAssets.Add(assets[i]);
        }
        if (activeAssets.Count == 0 || provider.voxelElements.Count == 0)
        {
            Debug.LogWarning("[SVOBuilder] 无活跃体素数据，跳过构建");
            return;
        }

        // ========================
        // Phase 1: Scene Bounds（直接从体素坐标计算 tight bounds）
        // ========================
        Vector3Int gridOrigin = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
        Vector3Int gridMax    = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        int activeCount = 0;

        foreach (var v in provider.voxelElements)
        {
            if (v.materialIndex < 0) continue; // 墓碑：运行时已删除的体素，重建时剔除
            gridOrigin = Vector3Int.Min(gridOrigin, v.worldPos);
            gridMax    = Vector3Int.Max(gridMax, v.worldPos);
            activeCount++;
        }

        if (activeCount == 0)
        {
            Debug.LogWarning("[SVOBuilder] 所有体素均已被删除（墓碑），跳过构建");
            sPool = null;      // 清 CPU 镜像，Raycast/RemoveVoxelAt 走“未就绪”分支
            sSortedVoxelIdx = null;
            sActiveVoxelCount = 0;
            return;
        }

        // 计算 octree depth / rootSide
        Vector3Int extent = gridMax - gridOrigin + Vector3Int.one;
        int maxExtent = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
        int depth = 1;
        while ((1 << depth) < maxExtent) depth++;
        if (depth > 21)
        {
            // 64 位 Morton 每轴仅 21 位：超过则强制截断（同时告知 SVOMortonBuilder，保持 CPU/GPU 一致）
            Debug.LogWarning($"[SVOBuilder] 场景范围过大（maxExtent={maxExtent}），depth 由 {depth} 截断为 21，超范围体素将被丢弃");
            depth = 21;
        }
        Vector3Int rootMin = gridOrigin;

        // ========================
        // Phase 2: Morton 排序 + 自底向上归并（P0-4 方案 A）
        // ========================
        // 先释放旧 buffer / CPU 镜像，避免新旧节点池同时驻留导致峰值翻倍
        Release();

        var built = SVOMortonBuilder.Build(provider.voxelElements, rootMin, depth);
        if (built.activeCount == 0 || built.nodes.Count == 0)
        {
            Debug.LogWarning("[SVOBuilder] Morton 构建得到空树（全部体素被过滤/删除）");
            return;
        }

        // ========================
        // Phase 3: GPU Upload
        // ========================
        sNodeBuf = new ComputeBuffer(built.nodes.Count, 16);
        sNodeBuf.SetData(built.nodes);

        sMatBuf = new ComputeBuffer(provider.materialElements.Count, 48);
        sMatBuf.SetData(provider.materialElements);

        Shader.SetGlobalBuffer("_SVONodes",     sNodeBuf);
        Shader.SetGlobalBuffer("_SVOMaterials", sMatBuf);
        Shader.SetGlobalVector("_SVORootMin",   (Vector3)rootMin * scale);
        Shader.SetGlobalFloat ("_SVOScale",     scale);
        Shader.SetGlobalInt   ("_SVODepth",     depth);

        // 保存 CPU 侧镜像（Raycast / RemoveVoxelAt 用）
        sPool             = built.nodes;
        sSortedVoxelIdx   = built.sortedVoxelIdx;
        sActiveVoxelCount = built.activeCount;
        sRootMinInt       = rootMin;
        sRootMinWorld     = (Vector3)rootMin * scale;
        sDepth            = depth;
        sScale            = scale;

        Debug.Log($"[SVOBuilder] 节点池={built.nodes.Count}（紧凑子槽；旧实现稠密 8 槽约 " +
                  $"{8 * (built.nodes.Count - built.activeCount)}），活跃体素={built.activeCount}，" +
                  $"同坐标覆盖={built.duplicateCount}，越界={built.skippedCount}，depth={depth}");

        // ========================
        // Phase 4: 自发光体素 NEE 光源数据
        // ========================
        // 只遍历去重后的活跃体素（O(活跃数)），不再扫全列表
        if (sEmissiveSet == null) sEmissiveSet = new HashSet<int>();
        sEmissiveSet.Clear();
        var activeIdxArr = built.sortedVoxelIdx;
        for (int i = 0; i < built.activeCount; i++)
        {
            int vi = activeIdxArr[i];
            if (IsValidEmissive(provider, vi)) sEmissiveSet.Add(vi);
        }
        RebuildEmissiveLights(rootMin);
    }

    /// <summary>
    /// 从发光索引集合（sEmissiveSet）重建自发光 NEE 光源数据（_EmissiveLights + alias table）。
    /// Build 全量构建与运行时删除自发光体素后共用。
    /// 只遍历发光体素（O(E)），不扫全列表（O(N)）——拖刷高频删除的关键。
    /// </summary>
    static void RebuildEmissiveLights(Vector3Int rootMin)
    {
        var provider = VoxDataProvider.Instance;
        if (provider == null || sEmissiveSet == null) return;

        // 有效发光条目计数（跳过墓碑/失效索引防御）
        int emCount = 0;
        double totalPower = 0.0;
        foreach (int idx in sEmissiveSet)
        {
            if (!IsValidEmissive(provider, idx)) continue;
            var v = provider.voxelElements[idx];
            var mat = provider.materialElements[v.materialIndex];
            float lum = 0.2126f * mat.color.r + 0.7152f * mat.color.g + 0.0722f * mat.color.b;
            totalPower += System.Math.Pow(2.0, mat.flux) * lum;
            emCount++;
        }

        // 释放旧光源 buffer（Build 路径已由 Release 统一释放，运行时重建需自释放）
        if (sEmissiveBuf != null) { sEmissiveBuf.Release(); sEmissiveBuf = null; }
        if (sAliasBuf    != null) { sAliasBuf.Release();    sAliasBuf = null; }

        if (emCount > 0 && totalPower > 0.0)
        {
            // 构建 alias table（按功率加权）；两次遍历集合（未修改时顺序稳定）
            var probs   = new float[emCount];
            var aliases = new int[emCount];
            int k = 0;
            foreach (int idx in sEmissiveSet)
            {
                if (!IsValidEmissive(provider, idx)) continue;
                var v = provider.voxelElements[idx];
                var mat = provider.materialElements[v.materialIndex];
                float lum = 0.2126f * mat.color.r + 0.7152f * mat.color.g + 0.0722f * mat.color.b;
                probs[k] = (float)(System.Math.Pow(2.0, mat.flux) * lum / totalPower);
                k++;
            }
            BuildAliasTable(probs, aliases);

            // 上传发光体素数据: float4(x=gridX, y=gridY, z=gridZ, w=matIndex)
            var lightData = new Vector4[emCount];
            k = 0;
            foreach (int idx in sEmissiveSet)
            {
                if (!IsValidEmissive(provider, idx)) continue;
                var v = provider.voxelElements[idx];
                Vector3Int coord = v.worldPos - rootMin;
                lightData[k] = new Vector4(coord.x, coord.y, coord.z, v.materialIndex);
                k++;
            }

            var aliasData = new Vector2[emCount];
            for (int i = 0; i < emCount; i++)
                aliasData[i] = new Vector2(probs[i], aliases[i]);

            sEmissiveBuf = new ComputeBuffer(emCount, sizeof(float) * 4);
            sEmissiveBuf.SetData(lightData);
            sAliasBuf = new ComputeBuffer(emCount, sizeof(float) * 2);
            sAliasBuf.SetData(aliasData);

            Shader.SetGlobalBuffer("_EmissiveLights",     sEmissiveBuf);
            Shader.SetGlobalBuffer("_EmissiveAliasTable", sAliasBuf);
            Shader.SetGlobalInt  ("_EmissiveLightCount",  emCount);
            Shader.SetGlobalFloat("_EmissiveTotalPower",  (float)totalPower);
        }
        else
        {
            // 无自发光体素时仍需绑定占位 buffer，否则 D3D11 Dispatch 报错
            sEmissiveBuf = new ComputeBuffer(1, sizeof(float) * 4);
            sAliasBuf    = new ComputeBuffer(1, sizeof(float) * 2);
            Shader.SetGlobalBuffer("_EmissiveLights",     sEmissiveBuf);
            Shader.SetGlobalBuffer("_EmissiveAliasTable", sAliasBuf);
            Shader.SetGlobalInt  ("_EmissiveLightCount", 0);
            Shader.SetGlobalFloat("_EmissiveTotalPower", 0f);
        }
    }

    /// <summary>索引 idx 是否为有效的活跃发光体素条目（非墓碑、材质为 Emissive）。</summary>
    static bool IsValidEmissive(VoxDataProvider provider, int idx)
    {
        if (idx < 0 || idx >= provider.voxelElements.Count) return false;
        var v = provider.voxelElements[idx];
        if (v.materialIndex < 0 || v.materialIndex >= provider.materialElements.Count) return false;
        return Mathf.Abs(provider.materialElements[v.materialIndex].type - 3f) < 0.5f;
    }

    // ============================================================
    //  说明：原 Top-down Insert 递归已由 SVOMortonBuilder 替代
    //  （Morton 排序 + 自底向上归并，只分配实际存在的子槽）。
    // ============================================================

    // ============================================================
    //  运行时体素拾取 / 删除（标记删除，不重建 SVO）
    //
    //  原理：GPU 遍历（TraverseSVO_Stack / SVOLookup / DDA）把
    //  “childMask=0 且非叶子”的节点当作空节点跳过，
    //  因此把叶子覆写为 MakeEmptyInternal() 即等效删除该体素，
    //  全部 shader 无需改动。
    // ============================================================

    /// <summary>
    /// CPU 射线拾取：世界空间射线 → grid 空间 → SVO 栈式遍历（CPU 镜像）。
    /// 与 GPU TraverseSVO_Stack 同构：空子树整块跳过，
    /// 大场景空白区域从“逐格步进 O(rootSide)”降为“按子树跳跃 O(访问节点数)”。
    /// 子节点按 tMin 降序压栈 → 第一个弹出的叶子即最近命中
    /// （区间嵌套保证后代 tMin ≥ 祖先 tMin，栈顶叶子必为全局最近）。
    /// 同时输出 voxelElements 索引（供 RemoveVoxelAt 的 O(1) 墓碑快路径）。
    /// verbose=false 时静默未命中日志（拖刷高频采样用，防 Console 刷屏）。
    /// </summary>
    public static bool Raycast(Vector3 roWorld, Vector3 rdWorld,
                               out Vector3Int hitGrid, out int matIndex,
                               out int voxelIdx, bool verbose = true)
    {
        hitGrid  = Vector3Int.zero;
        matIndex = -1;
        voxelIdx = -1;
        if (!IsReady) return false;

        int rootSide = 1 << sDepth;

        // 世界 → grid（根 AABB 在 grid 空间 = [0, rootSide]³，均匀缩放方向不变）
        Vector3 ro = (roWorld - sRootMinWorld) / sScale;
        Vector3 rd = rdWorld.normalized;

        // 与根 AABB 求交
        Vector3 rdInv = new Vector3(
            Mathf.Abs(rd.x) > 1e-6f ? 1f / rd.x : (rd.x >= 0f ? 1e30f : -1e30f),
            Mathf.Abs(rd.y) > 1e-6f ? 1f / rd.y : (rd.y >= 0f ? 1e30f : -1e30f),
            Mathf.Abs(rd.z) > 1e-6f ? 1f / rd.z : (rd.z >= 0f ? 1e30f : -1e30f));

        Vector3 t0 = Vector3.Scale(-ro, rdInv);
        Vector3 t1 = Vector3.Scale(new Vector3(rootSide, rootSide, rootSide) - ro, rdInv);
        float tEnter = Mathf.Max(Mathf.Max(Mathf.Min(t0.x, t1.x), Mathf.Min(t0.y, t1.y)), Mathf.Min(t0.z, t1.z));
        float tExit  = Mathf.Min(Mathf.Min(Mathf.Max(t0.x, t1.x), Mathf.Max(t0.y, t1.y)), Mathf.Max(t0.z, t1.z));

        if (tEnter > tExit || tExit < 0f)
        {
            return false;
        }
        tEnter = Mathf.Max(tEnter, 0f);

        sTravStack.Clear();
        sTravStack.Add(new TravItem { nodeIdx = 0, level = sDepth, nodeMin = Vector3Int.zero, tMin = tEnter, tMax = tExit });
        int visited = 0;

        while (sTravStack.Count > 0)
        {
            TravItem cur = sTravStack[sTravStack.Count - 1];
            sTravStack.RemoveAt(sTravStack.Count - 1); // 尾部移除 O(1)
            visited++;

            var node = sPool[cur.nodeIdx];

            // ----- 叶子：命中（栈序保证这是最近命中）-----
            if (node.IsLeaf)
            {
                hitGrid  = cur.nodeMin;
                matIndex = (int)node.data;
                voxelIdx = FindVoxelIndex(hitGrid);
                return true;
            }

            // ----- 内部节点：对存在的子节点求交（空子树自动跳过——SVO 加速核心）-----
            int half = 1 << (cur.level - 1);
            int childCount = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((node.childMask & (1u << i)) == 0u) continue;

                int cx = i & 1, cy = (i >> 1) & 1, cz = (i >> 2) & 1;
                Vector3 cMin = new Vector3(cur.nodeMin.x + cx * half, cur.nodeMin.y + cy * half, cur.nodeMin.z + cz * half);
                Vector3 cMax = new Vector3(cMin.x + half, cMin.y + half, cMin.z + half);

                Vector3 ct0 = Vector3.Scale(cMin - ro, rdInv);
                Vector3 ct1 = Vector3.Scale(cMax - ro, rdInv);
                Vector3 ctMin3 = Vector3.Min(ct0, ct1);
                Vector3 ctMax3 = Vector3.Max(ct0, ct1);
                float cTMin = Mathf.Max(Mathf.Max(ctMin3.x, ctMin3.y), ctMin3.z);
                float cTMax = Mathf.Min(Mathf.Min(ctMax3.x, ctMax3.y), ctMax3.z);

                cTMin = Mathf.Max(cTMin, cur.tMin); // 裁剪到父区间
                cTMax = Mathf.Min(cTMax, cur.tMax);
                if (cTMin > cTMax || cTMax < 0f) continue;

                sChildCands[childCount] = new TravItem
                {
                    // 紧凑子槽：第 i 个子节点 = data + popcount(childMask 的低 i 位)
                    nodeIdx = (int)node.data + SVOMortonBuilder.Rank(node.childMask, i),
                    level   = cur.level - 1,
                    nodeMin = cur.nodeMin + new Vector3Int(cx * half, cy * half, cz * half),
                    tMin    = cTMin,
                    tMax    = cTMax,
                };
                childCount++;
            }

            // 按 tMin 降序冒泡（≤8 元素）→ 最近的最后压栈、先弹出
            for (int a = 0; a < childCount - 1; a++)
            {
                for (int b = a + 1; b < childCount; b++)
                {
                    if (sChildCands[a].tMin < sChildCands[b].tMin)
                    {
                        var tmp = sChildCands[a]; sChildCands[a] = sChildCands[b]; sChildCands[b] = tmp;
                    }
                }
            }

            for (int k = 0; k < childCount; k++)
            {
                if (sTravStack.Count < TravStackCap)
                    sTravStack.Add(sChildCands[k]);
            }
        }

        return false;
    }

    /// <summary>
    /// 删除指定 grid 坐标的体素（标记删除）：
    ///   1. 走树路径定位叶子节点（子槽偏移 = childMask 的 rank），覆写为空内部节点（16B 上传）；
    ///   2. 数据源同步：该格子唯一活跃条目打墓碑（materialIndex=-1）；
    ///   3. 若是自发光体素，重建 NEE 光源表。
    /// 注意：紧凑子槽下**不能**清父节点的 childMask——rank 依赖它，清位会让后续寻址错乱；
    /// 残留的空壳路径仅多访几个空节点，不影响正确性。
    /// 返回是否删除成功。
    /// </summary>
    public static bool RemoveVoxelAt(Vector3Int gridCoord, int voxelIdx = -1)
    {
        if (!IsReady)
        {
            Debug.LogWarning("[SVOBuilder.RemoveVoxelAt] SVO 未就绪，无法删除");
            return false;
        }
        var provider = VoxDataProvider.Instance;
        if (provider == null)
        {
            Debug.LogWarning("[SVOBuilder.RemoveVoxelAt] VoxDataProvider 不可用");
            return false;
        }

        // ---- 1. 走树路径定位叶子（紧凑子槽：childMask 的 rank 即子槽偏移）----
        int nodeIdx = 0;
        int level = sDepth;
        Vector3Int nodeMin = Vector3Int.zero;

        while (level > 0)
        {
            var node = sPool[nodeIdx];
            if (node.IsLeaf)
            {
                Debug.LogWarning($"[SVOBuilder.RemoveVoxelAt] 树路径异常：level={level} 处遇非预期叶子 nodeIdx={nodeIdx}");
                return false;
            }

            int half = 1 << (level - 1);
            int cx = gridCoord.x >= nodeMin.x + half ? 1 : 0;
            int cy = gridCoord.y >= nodeMin.y + half ? 1 : 0;
            int cz = gridCoord.z >= nodeMin.z + half ? 1 : 0;
            int c = cx | (cy << 1) | (cz << 2);

            if ((node.childMask & (1u << c)) == 0u)
            {
                Debug.LogWarning($"[SVOBuilder.RemoveVoxelAt] grid={gridCoord} 在树中无子节点（childMask 缺 bit {c}）— 树与索引不同步？");
                return false;
            }

            nodeIdx = (int)node.data + SVOMortonBuilder.Rank(node.childMask, c);
            nodeMin += new Vector3Int(cx * half, cy * half, cz * half);
            level--;
        }

        var leaf = sPool[nodeIdx];
        if (!leaf.IsLeaf)
        {
            Debug.LogWarning($"[SVOBuilder.RemoveVoxelAt] nodeIdx={nodeIdx} 不是叶子 — 树与索引不同步？");
            return false;
        }
        int matIndex = (int)leaf.data;

        // ---- 2. 叶子 → 空内部节点（GPU 遍历自动跳过该体素）----
        sPool[nodeIdx] = SVONode.MakeEmptyInternal();
        UploadNode(nodeIdx);

        // ---- 3. 数据源同步：墓碑标记，不移动列表元素 ----
        // 列表结构不变 → 排序索引数组中其余条目永远有效。
        // 快路径：Raycast 已返回 voxelIdx，O(1)；
        // 慢路径：Morton 二分查找（外部调用无索引时），O(log N)。
        Vector3Int worldPos = gridCoord + sRootMinInt;
        int tombstoned = 0;
        bool wasEmissive = matIndex >= 0 && matIndex < provider.materialElements.Count &&
                           Mathf.Abs(provider.materialElements[matIndex].type - 3f) < 0.5f;

        bool fastPath = voxelIdx >= 0 && voxelIdx < provider.voxelElements.Count;
        if (fastPath)
        {
            var e = provider.voxelElements[voxelIdx];
            fastPath = e.materialIndex >= 0 && e.worldPos == worldPos;
        }

        if (fastPath)
        {
            var e = provider.voxelElements[voxelIdx];
            e.materialIndex = -1; // 墓碑
            provider.voxelElements[voxelIdx] = e;
            tombstoned = 1;
            if (wasEmissive) sEmissiveSet.Remove(voxelIdx);
        }
        else
        {
            int found = FindVoxelIndex(gridCoord);
            if (found >= 0)
            {
                var e = provider.voxelElements[found];
                if (e.materialIndex >= 0)
                {
                    e.materialIndex = -1; // 墓碑
                    provider.voxelElements[found] = e;
                    tombstoned++;
                    if (wasEmissive) sEmissiveSet.Remove(found);
                }
            }
        }

        // ---- 4. 自发光体素被删 → 重建 NEE 光源表（O(E)，集合已同步移除）----
        if (wasEmissive)
            RebuildEmissiveLights(sRootMinInt);

        // ---- 5. 通知外部（worldPos 为场景空间绝对坐标，与 VoxelElement.worldPos 同源）----
        // 订阅方（CameraRender → DXRPathTracer）只累积增量，下一帧随 UPDATE_VOXELS 一次性发出。
        var onRemoved = VoxelRemoved;
        if (onRemoved != null)
        {
            try { onRemoved(worldPos, -1); }
            catch (System.Exception ex) { Debug.LogWarning($"[SVOBuilder] VoxelRemoved 订阅方异常: {ex.Message}"); }
        }

        return true;
    }

    /// <summary>
    /// grid 坐标 → voxelElements 索引（Morton 有序数组上的二分查找，替代旧 Dictionary）。
    /// 未找到返回 -1。比较时现算 Morton（约 11 次位运算），不再额外缓存键数组。
    /// </summary>
    static int FindVoxelIndex(Vector3Int gridCoord)
    {
        if (sSortedVoxelIdx == null || sActiveVoxelCount <= 0) return -1;
        var provider = VoxDataProvider.Instance;
        if (provider == null) return -1;

        ulong target = SVOMortonBuilder.Morton(gridCoord.x, gridCoord.y, gridCoord.z);
        int lo = 0, hi = sActiveVoxelCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int vi = sSortedVoxelIdx[mid];
            if (vi < 0 || vi >= provider.voxelElements.Count) return -1;
            Vector3Int c = provider.voxelElements[vi].worldPos - sRootMinInt;
            ulong k = SVOMortonBuilder.Morton(c.x, c.y, c.z);
            if (k == target) return vi;
            if (k < target) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>单节点覆写上传（16B，点击级开销；复用缓冲零分配）。</summary>
    static void UploadNode(int nodeIdx)
    {
        sSingleNode[0] = sPool[nodeIdx];
        sNodeBuf.SetData(sSingleNode, 0, nodeIdx, 1);
    }

    /// <summary>
    /// 构建 alias method 采样表（Vose's alias method）。
    /// 输入: probs[] 为归一化概率（和为1），aliases[] 输出别名索引。
    /// </summary>
    static void BuildAliasTable(float[] probs, int[] aliases)
    {
        int n = probs.Length;
        var scaled = new float[n];
        for (int i = 0; i < n; i++)
            scaled[i] = probs[i] * n;

        var small = new Stack<int>();
        var large = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (scaled[i] < 1.0f) small.Push(i);
            else large.Push(i);
        }

        while (small.Count > 0 && large.Count > 0)
        {
            int s = small.Pop();
            int l = large.Pop();
            probs[s]   = scaled[s];
            aliases[s] = l;
            scaled[l]  = (scaled[l] + scaled[s]) - 1.0f;
            if (scaled[l] < 1.0f) small.Push(l);
            else large.Push(l);
        }

        // 处理浮点误差残余
        while (large.Count > 0) { int l = large.Pop(); probs[l] = 1.0f; }
        while (small.Count > 0) { int s = small.Pop(); probs[s] = 1.0f; }
    }
}
