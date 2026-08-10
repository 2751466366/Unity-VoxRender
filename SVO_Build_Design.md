# SVO（Sparse Voxel Octree）创建方案设计

> 基于 [VoxDataProvider.cs](file:///d:/UnityHub/Project/VoxRender/Assets/Scenes/Scripts/VoxDataProvider.cs) 已加载的体素数据，构建**扁平化 GPU-ready** 的稀疏八叉树，供射线步进/路径追踪消费。

---

## 1. 目标与总览

### 1.1 目标
- `VoxDataProvider` 多 chunk/VoxAsset → **单一世界空间整数体素网格**
- 解析 VOX 时落成**两个扁平数组**，方便上传 GPU：
  - `VoxelElement[]`：体素**最小角**（scene-space 整数 `Vector3Int`，scale 已剥离）+ `materialIndex`；最大角 = 最小角 + 1，全程整数
  - `MaterialElement[]`：≤256 项，颜色 + PBR 参数（diffuse/metal/glass/emissive/...），GPU 查表用
- 构建 **Sparse Voxel Octree**，**叶子存 `materialIndex`**（不再内联颜色）；**节点池也扁平**（`List<SVONode>`，用 `childrenOffset` 整数偏移寻址子节点，无指针/引用）
- 上传两个 ComputeBuffer：`_SVONodes`（节点池）+ `_SVOMaterials`（全局材质表）

### 1.2 数据流

```
VoxModel.Load() → VoxDataProvider.Load → CreateChunks
                                                ├─ Texture3D + Material + ChunkInfo (DDA pass 保留)
                                                ├─ VoxelElement[]     (新增: 扁平, 16B/项)
                                                └─ MaterialElement[]  (新增: ≤256 项, 48B/项)
DeferredPipeline.Start → 等 Load 完成 → SVOBuilder.Build
                                              Phase 1: Scene Bounds + 合并全局材质表
                                              Phase 2: 逐体素 top-down 插入节点池
                                              Phase 3: SetData 上传 _SVONodes + _SVOMaterials
```

SVO 构建只读 `voxelElements + materialElements`，不再碰 chunk / Texture3D / transform。

---

## 2. 数据结构重构（VoxAsset）

### 2.1 新增两个扁平 struct

```csharp
[System.Serializable]
public struct VoxelElement                    // 16B，扁平数组下标即 id
{
    public Vector3Int worldPos;               // 体素最小角, scene-space 整数, scale 已剥离
    public int        materialIndex;          // 指向 VoxAsset.materialElements (0-based)
}
// 体素在网格中占据 [worldPos, worldPos + (1,1,1)]; 最大角=最小角+1, 全程整数, 无 .5 偏移, 无小数运算

public enum MaterialType { Diffuse, Metal, Glass, Emissive, Cloud, Blend }

[System.Serializable]
[StructLayout(LayoutKind.Explicit, Size = 48)] // 3×float4, GPU 对齐
public struct MaterialElement                 // 48B，≤256 项/资产
{
    [FieldOffset(0)]  public MaterialType type;
    [FieldOffset(4)]  public float weight, rough, spec;
    [FieldOffset(16)] public float ior, att, flux, _pad0;
    [FieldOffset(32)] public Color color;     // palette RGBA (A=透明度)
}
```

### 2.2 简化 ChunkInfo + VoxAsset

```csharp
[System.Serializable]
public class ChunkInfo                        // 仅 DDA pass + 算 bounds 用, SVO 不读
{
    public GameObject cube;
    public Vector3Int dims;                   // Vox 空间 (sx, sy, sz)
    public Vector3 worldMin, worldMax;        // 旋转后的世界 AABB (§3.1 一次性算好)
}

public class VoxAsset
{
    public string filePath;
    [System.NonSerialized] public IVoxFile voxFile;
    // SVO 输入: 扁平, GPU-ready
    public List<VoxelElement>    voxelElements    = new();
    public List<MaterialElement> materialElements = new();
    // DDA pass 用: 按 chunk 索引平行存储
    public List<ChunkInfo> chunks; public List<Texture3D> textures; public List<Material> materials;
    public bool isActive = true;
}
```

### 2.3 CreateChunks 改造（伪代码）

**A. 建 materialElements（chunk 循环前，一次）**：voxreader 5.0.6 只把 MATL 暴露为 `IChunk.Content` 原始字节，需自解析（规范：`int32 id + DICT{STRING key, STRING value}`）。
- 先用 palette 为 256 槽填默认 diffuse（颜色来自 `voxFile.Palette.Colors`）
- 遍历 `voxFile.Chunks` 中 `Type==MaterialNew`，按 `_type/_weight/_rough/_spec/_ior/_att/_flux` 覆盖对应槽
- palette 0-based / MATL id 1-based：转换处统一 `±1`；未知 key 静默忽略（前向兼容）；缺 MATL 退化为 diffuse

**B. chunk 循环里同时写 Texture3D + voxelElements + ChunkInfo**：
```csharp
// 最小角本地映射 (不带 +0.5, 去掉中心偏移)
Vector3 LocalPosMinToCubeLocal(Vector3Int lp, Vector3Int d) => new(
    lp.x/(float)d.x - .5f, lp.z/(float)d.z - .5f, lp.y/(float)d.y - .5f); // Vox Y↔Z

foreach (var voxel in model.Voxels)
{
    int ix=... iy=... iz=...; // 边界略
    // 1) 原有: 写 Texture3D pixels[...]
    // 2) 新增: 写扁平 voxelElements (scene-space 整数最小角)
    Vector3 cubeLocal        = LocalPosMinToCubeLocal(new(ix,iy,iz), new(sx,sy,sz));
    Vector3 worldMinCorner   = cube.transform.TransformPoint(cubeLocal);       // 含 scale
    Vector3Int sceneMinCorner= Vector3Int.RoundToInt(worldMinCorner / scale); // 剥离 scale, 落整数
    asset.voxelElements.Add(new() { worldPos = sceneMinCorner, materialIndex = voxel.ColorIndex - 1 });
}
// chunk 末尾: 旋转半边长法算 worldMin/worldMax 落 ChunkInfo (§3.1)
```

> 90° 旋转 + 整数 chunk 位置 + 整数 scale 下，`worldMinCorner/scale` 严格为整数，`RoundToInt` 仅消除浮点表示误差（如 `0.9999999 → 1`）。
> `voxelElements.worldPos` 是解析期快照，chunk 移动/旋转后需重建 SVO。

---

## 3. 坐标系统一

### 3.1 ChunkInfo.worldMin/worldMax：算场景 bounds（唯一用途）

**禁止 `position ± chunkSize*0.5f`**（旋转后半边长需置换），二选一：
- **旋转半边长法（推荐）**：`h = rot * (chunkSize*.5f); worldHalf = Abs(h); min/max = pos ± worldHalf`（一次四元数乘）
- **8 角点变换法**：`TransformPoint` 变 `(±0.5,±0.5,±0.5)` 取 min/max（任意角度都对）

### 3.2 VoxelToGrid：一次整数减法（构建期零浮点）

```csharp
Vector3Int VoxelToGrid(Vector3Int worldPosMin, Vector3Int gridOrigin)
    => worldPosMin - gridOrigin;
```

构建后对比全体素 `worldPos` min/max 与 `gridOrigin/gridMax`（由 chunk AABB 量化），差应 ≤ 1。

### 3.3 根立方体构造 + 跨象限原理

```
gridOrigin = min( RoundToInt(worldMin/scale) )
gridMax    = max( CeilToInt(worldMax/scale) - 1 )
octreeDepth = ceil(log2( max(gridMax-gridOrigin+1) ));  rootSide = 1<<depth;  rootMin = gridOrigin
```

**跨象限不成立**：我们不按 chunk 放置；逐体素 `Insert`，每个体素从根递归选唯一子（一次 `VoxelToGrid` → 原子单位），chunk AABB 只算 bounds。**体素原子性**：`side=1 ≤ 2^(L-1)`（L≥1），不可能横跨 power-of-2 切分面，每层唯一子象限。

---

## 4. 扁平化 SVO 数据结构（GPU 友好）

三层扁平数组，**全固定步长、无引用/指针、整数偏移寻址**，`ComputeBuffer.SetData` 直接传：

| 层级 | C# 容器 | 单元大小 | GPU 变量 | 寻址方式 |
|---|---|---|---|---|
| L1 材质表 | `List<MaterialElement>` （多资产 Phase 1 拼接为全局单表） | 48B | `_SVOMaterials` (StructuredBuffer) | SVO 叶子 `data` 作下标查表 |
| L2 节点池 | `List<SVONode>`（整棵八叉树就这一个数组） | 16B | `_SVONodes` (StructuredBuffer) | `childrenOffset` + childIdx |
| L3 体素数组 | `List<VoxelElement>`（仅 CPU 构建用，上传后可释放） | 16B | - | 遍历 id 递增，构建完丢弃 |

### 4.1 SVONode（16B/项，叶子=materialIndex / 内部=childrenOffset）

```hlsl
struct SVONode {
    uint childMask;        // 8-bit 子掩码 (bit i=子 i 存在); 叶子=0
    uint data;             // 内部: childrenOffset (子节点在 pool 的起始索引);  叶子: materialIndex
    uint flags; uint _pad;
};
// 判别: childMask != 0 → 内部 (读 data 作 childrenOffset);  ==0 → 叶子 (读 data 作 materialIndex)
```

### 4.2 叶子定义：side == 1

```
side == 1           → 叶子, data=materialIndex（查 MaterialElement[] 取颜色+PBR）
side>1 且有非空子   → 内部节点, childMask 标记子存在, data=childrenOffset 指向 8 连续槽
side>1 且无非空子   → 不分配（稀疏剪枝）
```

`rootSide = 2^octreeDepth` → 根在 L=octreeDepth，叶子在 L=0。后续 brick 优化（8³ 叶子聚 atlas）可把节点数降到 ~1/512（非本期范围）。

---

## 5. 构建流程

### 5.1 总体

```
Phase 0 Parse&Flatten  (CreateChunks 内同步完成)
Phase 1 Scene Bounds   → gridOrigin/rootSide/depth + 合并全局 materialElements
Phase 2 Top-down Insert → for voxel in voxelElements: VoxelToGrid → Insert 递归
Phase 3 GPU Upload     → ComputeBuffer.SetData(_SVONodes + _SVOMaterials + RootInfo)
```

无中间 `rootSide³` 稠密数组；`globalMaterials = Concat(asset.materialElements)`，资产多可按 `(type,color,rough,ior,...)` hash 去重。

### 5.2 Phase 2 Top-down Insert（核心，伪代码）

```csharp
List<SVONode> pool = new() { new() };  // pool[0] = 根
for (int a = 0; a < assets.Count; a++) {
    var voxels = assets[a].voxelElements; int matBase = assetMatOffset[a];
    for (int i = 0; i < voxels.Count; i++) {
        var v = voxels[i];
        Insert(0, VoxelToGrid(v.worldPos, gridOrigin), matBase + v.materialIndex, depth, rootMin);
    }
}
void Insert(int nodeIdx, Vector3Int coord, int mat, int level, Vector3Int nodeMin)
{
    if (level == 0) { pool[nodeIdx] = new() { childMask = 0, data = (uint)mat }; return; }
    var n = pool[nodeIdx];
    if (n.data == 0 && n.childMask == 0) {
        n.data = (uint)pool.Count;                    // 8 槽起点
        for (int k=0; k<8; k++) pool.Add(default);    // 占位空槽（GPU 按 childMask 跳过，不读）
    }
    int half = 1 << (level-1);
    int cx=(coord.x>=nodeMin.x+half?1:0), cy=(coord.y>=nodeMin.y+half?1:0), cz=(coord.z>=nodeMin.z+half?1:0);
    int c = cx | (cy<<1) | (cz<<2);
    n.childMask |= (uint)(1 << c); pool[nodeIdx] = n;
    var childMin = nodeMin + new Vector3Int(cx*half, cy*half, cz*half);
    Insert((int)n.data + c, coord, mat, level-1, childMin);
}
```

**要点**：扁平遍历 `voxelElements`（无 chunk 嵌套）；8 槽一次性分配；后续子直接 `offset+childIdx`；同格重叠体素后插入覆盖。复杂度 N × depth。缓存优化：按 Morton 码排序 `voxelElements` 后再插入。

### 5.3 Phase 3 GPU Upload

```csharp
var nodeBuf = new ComputeBuffer(pool.Count, 16);                nodeBuf.SetData(pool);
var matBuf  = new ComputeBuffer(globalMaterials.Count, 48);     matBuf.SetData(globalMaterials);
Shader.SetGlobalBuffer("_SVONodes",      nodeBuf);
Shader.SetGlobalBuffer("_SVOMaterials",  matBuf);
Shader.SetGlobalVector("_SVORootMin",   (Vector3)rootMin * scale);
Shader.SetGlobalFloat ("_SVOScale",     scale);
Shader.SetGlobalInt   ("_SVODepth",     depth);
```

---

## 6. GPU 遍历（伪步骤）

SVO 在 scene-space 整数网格中构建（体素 AABB = `[gridCoord, gridCoord+1]`）。

```
0. 世界→网格空间: ro_grid = (ro - _SVORootMin) / _SVOScale;  rd_grid = rd / _SVOScale;
1. 根 AABB = [0, rootSide] 相交 tEnter
2. DDA + 栈式遍历（Crassin 2011）：childMask 下行到叶子
3. 叶子: materialIndex = node.data → 查 _SVOMaterials[materialIndex] 取 color/type/PBR 参数
4. 按 type 分支着色：Diffuse=Lambertian; Metal=GGX+Schlick; Glass=薄 BSDF(ior/att); Emissive=color*flux; Cloud=体积散射; Blend=混合
5. Shadow Pass：沿 _LightDir 步进; 命中非发光叶子 → shadow mask = 1（发光不算遮挡）
```


### 为什么说「SVO 数据结构是扁平化的」（一句话总览）
三层扁平连续数组：**材质表 `MaterialElement[]`**（按 materialIndex 查表）+ **节点池 `SVONode[]`**（内部节点用 `childrenOffset` 整数偏移指向 8 连续子槽，无指针）+ **体素数组 `VoxelElement[]`**（构建期扁平遍历，构建完上传后可释放）。三者步长固定、无引用/递归结构，直接 `ComputeBuffer.SetData` 即可上传 GPU。
