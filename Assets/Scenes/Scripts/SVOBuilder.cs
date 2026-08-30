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

    /// <summary>释放上一次 Build 创建的 ComputeBuffer。</summary>
    public static void Release()
    {
        if (sNodeBuf != null) { sNodeBuf.Release(); sNodeBuf = null; }
        if (sMatBuf  != null) { sMatBuf.Release();  sMatBuf = null; }
        if (sEmissiveBuf != null) { sEmissiveBuf.Release(); sEmissiveBuf = null; }
        if (sAliasBuf    != null) { sAliasBuf.Release();    sAliasBuf = null; }
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

        foreach (var v in provider.voxelElements)
        {
            gridOrigin = Vector3Int.Min(gridOrigin, v.worldPos);
            gridMax    = Vector3Int.Max(gridMax, v.worldPos);
        }

        // 计算 octree depth / rootSide
        Vector3Int extent = gridMax - gridOrigin + Vector3Int.one;
        int maxExtent = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
        int depth = 1;
        while ((1 << depth) < maxExtent) depth++;
        int rootSide = 1 << depth;
        Vector3Int rootMin = gridOrigin;

        Debug.Log($"[SVOBuilder] Phase 1: gridOrigin={gridOrigin}, gridMax={gridMax}, " +
                  $"extent={extent}, depth={depth}, rootSide={rootSide}, " +
                  $"voxelElements={provider.voxelElements.Count}, materialElements={provider.materialElements.Count}");

        // ========================
        // Phase 2: Top-down Insert
        // ========================
        var pool = new List<SVONode>();
        pool.Add(new SVONode()); // pool[0] = 根节点

        int leafCount = 0;
        int duplicateCount = 0;
        
        for (int i = 0; i < provider.voxelElements.Count; i++)
        {
            var v = provider.voxelElements[i];
            Vector3Int coord = v.worldPos - rootMin; // VoxelToGrid: 一次整数减法
            
            // 检查坐标是否在有效范围内
            if (coord.x < 0 || coord.x >= rootSide ||
                coord.y < 0 || coord.y >= rootSide ||
                coord.z < 0 || coord.z >= rootSide)
            {
                duplicateCount++;
                continue;
            }
            
            Insert(pool, 0, coord, v.materialIndex, depth, Vector3Int.zero);  // 根节点在 grid 空间从 (0,0,0) 开始
            leafCount++;
        }

        Debug.Log($"[SVOBuilder] Phase 2: 节点池大小={pool.Count}, 叶子节点={leafCount}, 越界/重复={duplicateCount}");

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

        Debug.Log($"[SVOBuilder] Phase 3: GPU 上传完成 — nodes={pool.Count}, materials={provider.materialElements.Count}");

        // ========================
        // Phase 4: 自发光体素 NEE 光源数据
        // ========================
        var emissiveVoxels = new List<VoxelElement>();
        double totalPower = 0.0;
        foreach (var v in provider.voxelElements)
        {
            if (v.materialIndex >= 0 && v.materialIndex < provider.materialElements.Count)
            {
                var mat = provider.materialElements[v.materialIndex];
                if (Mathf.Abs(mat.type - 3f) < 0.5f) // Emissive
                {
                    emissiveVoxels.Add(v);
                    float lum = 0.2126f * mat.color.r + 0.7152f * mat.color.g + 0.0722f * mat.color.b;
                    totalPower += System.Math.Pow(2.0, mat.flux) * lum;
                }
            }
        }

        int emCount = emissiveVoxels.Count;

        if (emCount > 0 && totalPower > 0.0)
        {
            // 构建 alias table（按功率加权）
            var probs   = new float[emCount];
            var aliases = new int[emCount];
            for (int i = 0; i < emCount; i++)
            {
                var mat = provider.materialElements[emissiveVoxels[i].materialIndex];
                float lum = 0.2126f * mat.color.r + 0.7152f * mat.color.g + 0.0722f * mat.color.b;
                probs[i] = (float)(System.Math.Pow(2.0, mat.flux) * lum / totalPower);
            }
            BuildAliasTable(probs, aliases);

            // 上传发光体素数据: float4(x=gridX, y=gridY, z=gridZ, w=matIndex)
            var lightData = new Vector4[emCount];
            for (int i = 0; i < emCount; i++)
            {
                var v = emissiveVoxels[i];
                Vector3Int coord = v.worldPos - rootMin;
                lightData[i] = new Vector4(coord.x, coord.y, coord.z, v.materialIndex);
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

            Debug.Log($"[SVOBuilder] Phase 4: {emCount} 自发光体素, totalPower={(float)totalPower:F2}");
        }
        else
        {
            Shader.SetGlobalInt  ("_EmissiveLightCount", 0);
            Shader.SetGlobalFloat("_EmissiveTotalPower", 0f);
        }
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
