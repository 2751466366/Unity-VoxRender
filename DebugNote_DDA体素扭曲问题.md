# DDA 体素扭曲问题排查记录

## 日期
2026-08-08

## 问题描述

在体素渲染中，从立方体**正面**观察时体素显示正常，但从**侧面**（斜角）观察时，渲染出的体素出现**整体错位/拉伸**的扭曲效果。

## 排查过程

### 假设 1：透视校正插值问题（已排除）

最初怀疑是顶点属性 `worldPos` / `localPos` 在屏幕空间线性插值导致倾斜面精度丢失。

**结论**：Unity 的顶点属性插值**已经是透视校正的**（perspective-correct interpolation），GPU 硬件自动按 W 加权。这不是扭曲的原因。

### 假设 2：DDA 步进精度累积（已排除）

怀疑 `tMax` 在多次累加后浮点误差累积导致步进错位。

**结论**：浮点误差只会导致局部噪声，不会导致"整体错位"。

### 假设 3：MagicaVoxel 的做法（参考）

查阅后发现 MagicaVoxel **不使用光线步进**，而是为每个体素生成实际的立方体网格（mesh-based rendering），因此不存在 DDA 精度问题。无法直接借鉴。

### 根因：gridDir 缺少 chunkDim 缩放

最终定位到 `gridDir` 的计算方式有误。

## 根因分析

### 错误代码

```hlsl
// ❌ 错误：归一化 + 只做 Y/Z 交换，丢失了 chunk 各轴的体素数量比例
float3 localDir = normalize(mul((float3x3)unity_WorldToObject, rayDir));
float3 gridDir  = normalize(float3(localDir.x, localDir.z, localDir.y));
```

### 正确代码

```hlsl
// ✅ 正确：不归一化，Y/Z 交换后乘以 chunkDim
float3 localDir = mul((float3x3)unity_WorldToObject, rayDir);
float3 gridDir  = float3(localDir.x, localDir.z, localDir.y) * _ChunkSize.xyz;
```

### 为什么会扭曲

DDA 算法中 `tDelta = 1/abs(gridDir)` 表示穿越一个体素所需的时间。各轴的 `tDelta` 比例必须反映 chunk 各轴的体素数量。

**具体例子**：chunk 尺寸为 100×10×10，从侧面以 45° 角观察：

| | 错误（归一化） | 正确（乘 chunkDim） |
|---|---|---|
| `gridDir` | `(0.707, 0, 0.707)` | `(70.7, 0, 7.07)` |
| `tDelta` | `(1.41, ∞, 1.41)` → X:Z = 1:1 | `(0.014, ∞, 0.141)` → X:Z = 10:1 |
| 步进比例 | X 和 Z 步进频率相同 ❌ | X 步进 10 次才有 1 次 Z 步进 ✅ |

- **正确**：X 方向有 100 个体素，Z 方向只有 10 个，45° 光线应该在 X 方向步进更多次
- **错误**：归一化后各轴步进频率相同，导致 X 方向体素被拉伸、Z 方向被压缩

### 为什么正面没问题

正面（垂直于最长轴）观察时，光线几乎纯沿一个轴方向，其他分量极小，即使比例错误影响也很小。侧面观察时，多轴分量显著，缩放错误被放大。

## 修复范围

### VoxRender_DDA_W.shader（世界空间版本）

1. **主 DDA 方向**（L228-L231）：
   ```hlsl
   float3 localDir = mul((float3x3)unity_WorldToObject, rayDir);
   float3 gridDir  = float3(localDir.x, localDir.z, localDir.y) * _ChunkSize.xyz;
   ```

2. **阴影 DDA 方向**（L283-L285）：
   ```hlsl
   float3 shadowLocalDir = mul((float3x3)unity_WorldToObject, lightDir);
   float3 shadowGridDir  = float3(shadowLocalDir.x, shadowLocalDir.z, shadowLocalDir.y) * _ChunkSize.xyz;
   ```

### VoxRender_DDA.shader（本地空间版本）

1. **主 DDA 方向**（L182-L183）：
   ```hlsl
   float3 gridDir = float3(rayDir.x, rayDir.z, rayDir.y) * _ChunkSize.xyz;
   ```

2. **阴影 DDA 方向**（L242-L244）：
   ```hlsl
   float3 shadowLocalDir = mul((float3x3)unity_WorldToObject, lightDir);
   float3 shadowGridDir  = float3(shadowLocalDir.x, shadowLocalDir.z, shadowLocalDir.y) * _ChunkSize.xyz;
   ```

## 关键知识点

### DDA 方向向量不需要归一化

DDA 的 `tDelta` 和 `tMax` 只使用比值：
- `tDelta = 1/abs(gridDir)` — 穿越一个体素的时间
- `tMax = (nextBoundary - gridOrigin) / gridDir` — 到达下一个边界的时间

方向向量的整体长度会约掉，关键的是各分量的**比例**必须正确反映各轴的体素密度。

### 非均匀缩放空间的坐标变换

当立方体各轴尺寸不同（非立方体块）时，方向向量从本地空间转换到网格空间必须乘以 `chunkDim`，以补偿各轴的体素密度差异。归一化会抹掉这个差异信息。

## 涉及文件

- [VoxRender_DDA_W.shader](Assets/Scenes/Scripts/VoxRender_DDA_W.shader) — 世界空间版本
- [VoxRender_DDA.shader](Assets/Scenes/Scripts/VoxRender_DDA.shader) — 本地空间版本
