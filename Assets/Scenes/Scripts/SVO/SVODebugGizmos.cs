using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SVO 调试可视化：在 Scene 视图中用 Gizmos 画出 SVO 叶子节点。
/// 挂在任意 GameObject 上，SVO 构建完成后自动显示。
/// </summary>
public class SVODebugGizmos : MonoBehaviour
{
    /// <summary>是否显示 Gizmos。</summary>
    public bool showGizmos = true;

    /// <summary>叶子节点绘制颜色。</summary>
    public Color leafColor = new Color(1f, 0.5f, 0f, 0.5f);

    /// <summary>是否绘制内部节点线框（非常耗性能，仅调试用）。</summary>
    public bool showInternalNodes = false;
    public Color internalColor = new Color(0f, 1f, 0f, 0.15f);

    // SVO 数据缓存
    private List<SVONode> cachedNodes;
    private Vector3 cachedRootMin;
    private float cachedScale;
    private int cachedDepth;
    private bool dataReady;

    // 收集到的叶子节点位置（世界空间）
    private List<Vector3> leafPositions;

    /// <summary>
    /// 由 SVOBuilder 调用，传入构建好的节点数据。
    /// </summary>
    public void SetSVOData(List<SVONode> nodes, Vector3 rootMin, float scale, int depth)
    {
        cachedNodes = nodes;
        cachedRootMin = rootMin;
        cachedScale = scale;
        cachedDepth = depth;
        dataReady = true;

        // CPU 遍历 SVO，收集所有叶子节点的世界位置
        CollectLeafPositions();

        Debug.Log($"[SVODebugGizmos] 收到 SVO 数据：{nodes.Count} 节点，depth={depth}，叶子={leafPositions?.Count ?? 0}");
    }

    void CollectLeafPositions()
    {
        if (cachedNodes == null || cachedNodes.Count == 0) return;

        leafPositions = new List<Vector3>();
        float rootSide = Mathf.Pow(2f, cachedDepth);

        // 诊断：直接遍历节点池，统计有多少节点有 LEAF_FLAG
        int totalLeavesInPool = 0;
        foreach (var node in cachedNodes)
        {
            if ((node.flags & SVONode.LEAF_FLAG) != 0)
                totalLeavesInPool++;
        }
        Debug.Log($"[SVODebugGizmos] 节点池总叶子数（直接遍历）={totalLeavesInPool}");

        // 从根节点开始递归遍历
        CollectLeavesRecursive(0, Vector3.zero, rootSide);
    }

    void CollectLeavesRecursive(int nodeIdx, Vector3 nodeMinGrid, float nodeSide)
    {
        if (nodeIdx < 0 || nodeIdx >= cachedNodes.Count) return;

        var node = cachedNodes[nodeIdx];

        // 叶子节点：记录位置
        if ((node.flags & SVONode.LEAF_FLAG) != 0)
        {
            // grid 空间 → 世界空间
            Vector3 worldPos = cachedRootMin + nodeMinGrid * cachedScale;
            leafPositions.Add(worldPos);
            return;
        }

        // 内部节点：递归 8 个子节点
        uint childMask = node.childMask;
        uint childrenOffset = node.data;
        float childSide = nodeSide * 0.5f;

        for (int c = 0; c < 8; c++)
        {
            if ((childMask & (1u << c)) == 0) continue;

            float cx = (c & 1) != 0 ? childSide : 0f;
            float cy = (c & 2) != 0 ? childSide : 0f;
            float cz = (c & 4) != 0 ? childSide : 0f;

            Vector3 childMin = nodeMinGrid + new Vector3(cx, cy, cz);
            int childIdx = (int)childrenOffset + c;

            CollectLeavesRecursive(childIdx, childMin, childSide);
        }
    }

    void OnDrawGizmos()
    {
        if (!showGizmos || !dataReady || leafPositions == null) return;

        float leafSize = cachedScale; // 1 个体素的世界大小

        // 绘制叶子节点
        Gizmos.color = leafColor;
        foreach (var pos in leafPositions)
        {
            Gizmos.DrawCube(pos + Vector3.one * leafSize * 0.5f, Vector3.one * leafSize);
        }

        // 可选：绘制内部节点线框（非常耗性能）
        if (showInternalNodes && cachedNodes != null)
        {
            Gizmos.color = internalColor;
            float rootSide = Mathf.Pow(2f, cachedDepth);
            DrawInternalNodesRecursive(0, Vector3.zero, rootSide);
        }
    }

    void DrawInternalNodesRecursive(int nodeIdx, Vector3 nodeMinGrid, float nodeSide)
    {
        if (nodeIdx < 0 || nodeIdx >= cachedNodes.Count) return;

        var node = cachedNodes[nodeIdx];
        if ((node.flags & SVONode.LEAF_FLAG) != 0) return; // 叶子不画线框

        // 画当前节点的线框
        Vector3 center = cachedRootMin + (nodeMinGrid + Vector3.one * nodeSide * 0.5f) * cachedScale;
        float worldSide = nodeSide * cachedScale;
        Gizmos.DrawWireCube(center, Vector3.one * worldSide);

        // 递归子节点
        uint childMask = node.childMask;
        uint childrenOffset = node.data;
        float childSide = nodeSide * 0.5f;

        for (int c = 0; c < 8; c++)
        {
            if ((childMask & (1u << c)) == 0) continue;

            float cx = (c & 1) != 0 ? childSide : 0f;
            float cy = (c & 2) != 0 ? childSide : 0f;
            float cz = (c & 4) != 0 ? childSide : 0f;

            Vector3 childMin = nodeMinGrid + new Vector3(cx, cy, cz);
            int childIdx = (int)childrenOffset + c;

            DrawInternalNodesRecursive(childIdx, childMin, childSide);
        }
    }
}
