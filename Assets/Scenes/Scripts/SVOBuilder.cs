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

    /// <summary>释放上一次 Build 创建的 ComputeBuffer。</summary>
    public static void Release()
    {
        if (sNodeBuf != null) { sNodeBuf.Release(); sNodeBuf = null; }
        if (sMatBuf  != null) { sMatBuf.Release();  sMatBuf = null; }
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
}
