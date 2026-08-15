using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 体素元素：扁平数组下标即 id，16B。
/// worldPos 为体素最小角（scene-space 整数，scale 已剥离）；
/// 体素占据 [worldPos, worldPos+(1,1,1)]，最大角=最小角+1，全程整数。
/// </summary>
[System.Serializable]
public struct VoxelElement
{
    public Vector3Int worldPos;       // 体素最小角, scene-space 整数, scale 已剥离
    public int        materialIndex;  // 指向 VoxAsset.materialElements (0-based)
}

/// <summary>材质类型枚举，对应 GPU 着色器中的着色分支。</summary>
public enum MaterialType { Diffuse, Metal, Glass, Emissive, Cloud, Blend }

/// <summary>
/// 材质元素：48B，≤256 项/资产，GPU StructuredBuffer 直传。
/// 使用 Explicit Layout 保证与 HLSL 对齐一致。
/// </summary>
[System.Serializable]
[StructLayout(LayoutKind.Explicit, Size = 48)] // 3×float4, GPU 对齐
public struct MaterialElement
{
    [FieldOffset(0)]  public MaterialType type;
    [FieldOffset(4)]  public float weight, rough, spec;
    [FieldOffset(16)] public float ior, att, flux, _pad0;
    [FieldOffset(32)] public Color color;     // palette RGBA (A=透明度)
}

/// <summary>
/// SVO 节点：16B/项，扁平节点池元素。
/// 判别：flags bit8 = 叶子标记；
///        childMask != 0 → 内部节点 (data = childrenOffset);
///        childMask == 0 && flags bit8 设置 → 叶子 (data = materialIndex)。
/// </summary>
[System.Serializable]
public struct SVONode
{
    public uint childMask;   // 8-bit 子掩码 (bit i = 子 i 存在); 叶子 = 0
    public uint data;        // 内部：childrenOffset (子节点在 pool 的起始索引); 叶子：materialIndex
    public uint flags;       // bit 8: 叶子标记 (LEAF_FLAG)
    public uint _pad;

    /// <summary>flags 中用于标记叶子节点的位 (bit 8)。</summary>
    public const uint LEAF_FLAG = 1u << 8;

    /// <summary>判断是否为叶子节点。</summary>
    public bool IsLeaf => (flags & LEAF_FLAG) != 0;

    /// <summary>创建叶子节点。</summary>
    public static SVONode MakeLeaf(int materialIndex) =>
        new SVONode { childMask = 0, data = (uint)materialIndex, flags = LEAF_FLAG };

    /// <summary>创建空内部节点（未分配子槽）。</summary>
    public static SVONode MakeEmptyInternal() =>
        new SVONode { childMask = 0, data = 0, flags = 0 };
}

/// <summary>
/// Chunk 信息：仅 DDA pass + 算 bounds 用，SVO 不直接读取。
/// </summary>
[System.Serializable]
public class ChunkInfo
{
    public GameObject cube;
    public Vector3Int dims;                    // Vox 空间 (sx, sy, sz)
    public Vector3 worldMin, worldMax;         // 旋转后的世界 AABB (§3.1 一次性算好)
}
