using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SVO（Sparse Voxel Octree）构建器。
/// 从 VoxDataProvider 已加载的 voxelElements + materialElements 构建扁平化 GPU-ready 八叉树，
/// 并上传为 ComputeBuffer 全局着色器属性。
///
/// 构建流程:
///   Phase 1: Scene Bounds → gridOrigin / rootSide / depth + 合并全局 materialElements
///   Phase 2: Top-down Insert → 逐体素递归插入节点池
///   Phase 3: GPU Upload → ComputeBuffer.SetData + Shader.SetGlobal*
/// </summary>
public static class SVOBuilder
{
    // ---- GPU buffer 引用，供外部释放 ----
    private static ComputeBuffer sNodeBuf;
    private static ComputeBuffer sMatBuf;
    private static ComputeBuffer sEmissiveBuf;
    private static ComputeBuffer sAliasBuf;

    // ---- CPU 侧镜像（拾取/删除用，与 GPU buffer 同步维护）----
    private static List<SVONode>     sPool;         // 节点池副本
    private static Vector3Int        sRootMinInt;   // 根最小角（scene-space 整数）
    private static Vector3           sRootMinWorld; // 根最小角（世界空间 = rootMin * scale）
    private static int               sDepth;        // 八叉树深度
    private static float             sScale;        // 体素缩放
    private static Dictionary<Vector3Int, int> sVoxelMap; // gridCoord → voxelElements 索引
    private static HashSet<int> sEmissiveSet;              // 发光体素的 voxelElements 索引（重建光源表 O(E) 用）

    // ---- 运行时复用缓冲（拖刷高频调用，消除临时分配/GC）----
    private static readonly List<int>  sParentIdxs = new List<int>(16);  // RemoveVoxelAt 父链
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
    public static bool IsReady => sPool != null && sVoxelMap != null && sNodeBuf != null;

    /// <summary>grid 坐标 → 世界坐标（与 shader 的 grid*_SVOScale+_SVORootMin 一致）。</summary>
    public static Vector3 GridToWorld(Vector3Int gridCoord) => (Vector3)(gridCoord + sRootMinInt) * sScale;

    /// <summary>单个体素的世界空间尺寸（= _SVOScale）。</summary>
    public static float VoxelWorldSize => sScale;

    /// <summary>释放上一次 Build 创建的 ComputeBuffer 与 CPU 镜像。</summary>
    public static void Release()
    {
        if (sNodeBuf != null) { sNodeBuf.Release(); sNodeBuf = null; }
        if (sMatBuf  != null) { sMatBuf.Release();  sMatBuf = null; }
        if (sEmissiveBuf != null) { sEmissiveBuf.Release(); sEmissiveBuf = null; }
        if (sAliasBuf    != null) { sAliasBuf.Release();    sAliasBuf = null; }

        sPool = null;
        sVoxelMap = null;
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
            sVoxelMap = null;
            return;
        }

        // 计算 octree depth / rootSide
        Vector3Int extent = gridMax - gridOrigin + Vector3Int.one;
        int maxExtent = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
        int depth = 1;
        while ((1 << depth) < maxExtent) depth++;
        int rootSide = 1 << depth;
        Vector3Int rootMin = gridOrigin;

        // ========================
        // Phase 2: Top-down Insert
        // ========================
        var pool = new List<SVONode>();
        pool.Add(new SVONode()); // pool[0] = 根节点

        // gridCoord → voxelElements 索引（CPU DDA 拾取查询用；
        // 重复坐标后写覆盖，与 Insert 叶子覆盖语义一致）
        var voxelMap = new Dictionary<Vector3Int, int>(provider.voxelElements.Count);

        int leafCount = 0;
        int duplicateCount = 0;
        
        for (int i = 0; i < provider.voxelElements.Count; i++)
        {
            var v = provider.voxelElements[i];
            if (v.materialIndex < 0) continue; // 墓碑：已删除体素不参与重建（自然压缩）
            Vector3Int coord = v.worldPos - rootMin; // VoxelToGrid: 一次整数减法
            
            // 检查坐标是否在有效范围内
            if (coord.x < 0 || coord.x >= rootSide ||
                coord.y < 0 || coord.y >= rootSide ||
                coord.z < 0 || coord.z >= rootSide)
            {
                duplicateCount++;
                continue;
            }

            // 重复坐标：旧条目打墓碑，保证一格只有一条活跃条目
            // （O(1) 删除快路径依赖此不变量：哈希索引即该格唯一活跃条目）
            if (voxelMap.TryGetValue(coord, out int prevIdx))
            {
                var prev = provider.voxelElements[prevIdx];
                if (prev.materialIndex >= 0)
                {
                    prev.materialIndex = -1;
                    provider.voxelElements[prevIdx] = prev;
                }
                duplicateCount++;
            }
            
            Insert(pool, 0, coord, v.materialIndex, depth, Vector3Int.zero);  // 根节点在 grid 空间从 (0,0,0) 开始
            voxelMap[coord] = i;
            leafCount++;
        }

        // ========================
        // Phase 3: GPU Upload
        // ========================
        Release(); // 释放旧 buffer

        sNodeBuf = new ComputeBuffer(pool.Count, 16);
        sNodeBuf.SetData(pool);

        sMatBuf = new ComputeBuffer(provider.materialElements.Count, 48);
        sMatBuf.SetData(provider.materialElements);

        Shader.SetGlobalBuffer("_SVONodes",     sNodeBuf);
        Shader.SetGlobalBuffer("_SVOMaterials", sMatBuf);
        Shader.SetGlobalVector("_SVORootMin",   (Vector3)rootMin * scale);
        Shader.SetGlobalFloat ("_SVOScale",     scale);
        Shader.SetGlobalInt   ("_SVODepth",     depth);


        // 保存 CPU 侧镜像（Raycast / RemoveVoxelAt 用）
        sPool         = pool;
        sRootMinInt   = rootMin;
        sRootMinWorld = (Vector3)rootMin * scale;
        sDepth        = depth;
        sScale        = scale;
        sVoxelMap     = voxelMap;

        // ========================
        // Phase 4: 自发光体素 NEE 光源数据
        // ========================
        // 扫描全列表填充发光索引集合（Build 本就 O(N)，顺带完成；
        // 运行时删除只增删集合项，重建光源表降为 O(E)）
        if (sEmissiveSet == null) sEmissiveSet = new HashSet<int>();
        sEmissiveSet.Clear();
        for (int i = 0; i < provider.voxelElements.Count; i++)
        {
            var v = provider.voxelElements[i];
            if (v.materialIndex >= 0 && v.materialIndex < provider.materialElements.Count &&
                Mathf.Abs(provider.materialElements[v.materialIndex].type - 3f) < 0.5f)
                sEmissiveSet.Add(i);
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

    /// <summary>
    /// 递归插入体素到八叉树节点池。
    /// level == 0 → 叶子 (childMask=0, data=materialIndex, flags=LEAF_FLAG)
    /// level >  0 → 内部节点 (childMask 标记子存在，data=childrenOffset)
    /// </summary>
    static void Insert(List<SVONode> pool, int nodeIdx, Vector3Int coord, int mat, int level, Vector3Int nodeMin)
    {
        if (level == 0)
        {
            pool[nodeIdx] = SVONode.MakeLeaf(mat);  // 设置 LEAF_FLAG
            return;
        }
    
        var n = pool[nodeIdx];
    
        // 如果此内部节点尚未分配子槽（非叶子且 childMask=0），分配 8 个连续空槽
        if (!n.IsLeaf && n.childMask == 0)
        {
            n.data = (uint)pool.Count; // 8 槽起点
            for (int k = 0; k < 8; k++)
                pool.Add(SVONode.MakeEmptyInternal());  // 占位空槽
        }
    
        // 根据 coord 与 nodeMin+half 比较选子象限
        int half = 1 << (level - 1);
        int cx = (coord.x >= nodeMin.x + half) ? 1 : 0;
        int cy = (coord.y >= nodeMin.y + half) ? 1 : 0;
        int cz = (coord.z >= nodeMin.z + half) ? 1 : 0;
        int c  = cx | (cy << 1) | (cz << 2);

        n.childMask |= (uint)(1 << c);
        pool[nodeIdx] = n;

        var childMin = nodeMin + new Vector3Int(cx * half, cy * half, cz * half);
        Insert(pool, (int)n.data + c, coord, mat, level - 1, childMin);
    }

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
                if (sVoxelMap.TryGetValue(hitGrid, out int vi)) voxelIdx = vi;
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
                    nodeIdx = (int)node.data + i,
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
    ///   1. 走树路径定位叶子节点，覆写为空内部节点（仅 16B GPU 上传）；
    ///   2. 向上清理子树全空的父链 childMask，避免空壳路径残留；
    ///   3. 数据源同步：该格子所有条目（含重复坐标）打墓碑（materialIndex=-1）并移除哈希表项；
    ///   4. 若是自发光体素，重建 NEE 光源表。
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

        // ---- 1. 走树路径定位叶子（记录父链供向上清理，栈复用零分配）----
        int nodeIdx = 0;
        int level = sDepth;
        Vector3Int nodeMin = Vector3Int.zero;
        sParentIdxs.Clear();

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
                Debug.LogWarning($"[SVOBuilder.RemoveVoxelAt] grid={gridCoord} 在树中无子节点（childMask 缺 bit {c}）— 树与哈希表不同步？");
                return false;
            }

            sParentIdxs.Add(nodeIdx);
            nodeIdx = (int)node.data + c;
            nodeMin += new Vector3Int(cx * half, cy * half, cz * half);
            level--;
        }

        var leaf = sPool[nodeIdx];
        if (!leaf.IsLeaf)
        {
            Debug.LogWarning($"[SVOBuilder.RemoveVoxelAt] nodeIdx={nodeIdx} 不是叶子 — 树与哈希表不同步？");
            return false;
        }
        int matIndex = (int)leaf.data;

        // ---- 2. 叶子 → 空内部节点（GPU 遍历自动跳过该体素）----
        sPool[nodeIdx] = SVONode.MakeEmptyInternal();
        UploadNode(nodeIdx);

        // ---- 3. 向上清理父链：子树全空的父节点清掉 childMask ----
        for (int i = sParentIdxs.Count - 1; i >= 0; i--)
        {
            int parentIdx = sParentIdxs[i];
            var parent = sPool[parentIdx];
            if (parent.IsLeaf || parent.childMask == 0u) break;

            bool allChildrenEmpty = true;
            for (int b = 0; b < 8; b++)
            {
                if ((parent.childMask & (1u << b)) == 0u) continue;
                var child = sPool[(int)parent.data + b];
                if (child.IsLeaf || child.childMask != 0u) { allChildrenEmpty = false; break; }
            }
            if (!allChildrenEmpty) break;

            parent.childMask = 0u;
            sPool[parentIdx] = parent;
            UploadNode(parentIdx);
        }

        // ---- 4. 数据源同步：墓碑标记，不移动列表元素 ----
        // 列表结构不变 → 哈希表中其余索引永远有效。
        // 快路径：Raycast 已返回 voxelIdx（哈希表维护的该格唯一活跃条目），O(1)；
        // 慢路径：全列表扫描（防御/外部调用无索引时），大场景下 O(N) 仅作兑底。
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
            for (int i = 0; i < provider.voxelElements.Count; i++)
            {
                var e = provider.voxelElements[i];
                if (e.materialIndex >= 0 && e.worldPos == worldPos)
                {
                    e.materialIndex = -1; // 墓碑
                    provider.voxelElements[i] = e;
                    tombstoned++;
                    if (wasEmissive) sEmissiveSet.Remove(i);
                }
            }
        }
        sVoxelMap.Remove(gridCoord);

        // ---- 5. 自发光体素被删 → 重建 NEE 光源表（O(E)，集合已同步移除）----
        if (wasEmissive)
            RebuildEmissiveLights(sRootMinInt);

        return true;
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
