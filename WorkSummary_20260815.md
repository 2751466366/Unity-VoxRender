# VoxRender 项目工作总结

## 日期
2026-08-15

## 项目概述
实现 SVO（Sparse Voxel Octree）体素可视化系统，包括 Build、Visualize、Debug 功能，并解决 SVO 与 DDA 渲染的坐标对齐问题。

---

## 一、已完成工作

### 1. SVO 核心架构设计
**参考文档**：`SVO_Build_Design.md`

实现了完整的 SVO 扁平节点池架构：
- **节点结构**：每个节点包含 `childrenMask`（8位掩码）+ 8个 childIndex
- **递归插入**：从 root 开始，根据体素位置逐层向下插入到 level 0 叶子节点
- **内存优化**：使用扁平数组而非树形指针，支持动态扩容

### 2. SVO Build 阶段（步骤 1-3）
**文件**：`Assets/Scenes/Scripts/SVO/VoxToSVOBuilder.cs`

#### 实现功能：
- ✅ **Vox→Unity 轴映射**：Vox Y → Unity Z，Vox Z → Unity Y
- ✅ **计算包围盒**：遍历所有体素，确定 `gridOrigin` 和 `gridSize`
- ✅ **构建 SVO 树**：递归插入体素到八叉树
- ✅ **生成节点池**：BFS 遍历生成扁平数组，包含根节点和内部节点
- ✅ **性能统计**：输出总节点数、最大深度、内存占用等信息

#### 关键算法：
```csharp
// 递归插入逻辑
void Insert(int nodeIdx, int depth, Vector3Int pos) {
    if (depth == 0) {
        nodes[nodeIdx].isLeaf = true;
        return;
    }
    
    // 计算子节点索引
    int bit = GetChildBit(pos, depth);
    if ((nodes[nodeIdx].childrenMask & (1 << bit)) == 0) {
        // 创建新子节点
        int childIdx = AllocateNode();
        nodes[nodeIdx].childrenMask |= (1 << bit);
        nodes[nodeIdx].childIndices[bit] = childIdx;
    }
    
    // 递归插入子节点
    Insert(nodes[nodeIdx].childIndices[bit], depth - 1, pos);
}
```

### 3. SVO Visualize 阶段（步骤 4-5）
**文件**：
- `Assets/Scenes/Scripts/SVO/SVOVisualizationData.cs` - 数据结构定义
- `Assets/Scenes/Scripts/SVO/Shaders/SVO_Visualize.shader` - 可视化 Shader
- `Assets/Scenes\Scripts\SVO\SVOVisualizer.cs` - Gizmos 绘制脚本

#### 实现功能：
- ✅ **Gizmos 包围盒**：显示 SVO 根节点的 AABB
- ✅ **网格线绘制**：在每个非空节点处绘制彩色网格
- ✅ **颜色编码**：不同深度使用不同颜色（红→橙→黄→绿→青→蓝→紫）
- ✅ **Inspector 控制**：可开关可视化、调整网格大小、显示统计信息

#### 可视化效果：
```
Depth 0: 红色大立方体（根节点）
Depth 1: 橙色中等立方体
Depth 2: 黄色小立方体
...
Depth 7: 紫色最小立方体（叶子节点）
```

### 4. SVO Ray Casting 阶段（步骤 6）
**文件**：
- `Assets/Scenes/Scripts/SVO/Shaders/SVO_RayCast.compute` - Compute Shader
- `Assets/Scenes/Scripts/SVO/SVORenderer.cs` - C# 调度脚本

#### 实现功能：
- ✅ **GPU 光线投射**：每个像素发射一条射线
- ✅ **DDA 遍历算法**：在 SVO 节点池中高效遍历
- ✅ **层级跳过**：遇到空节点时自动跳过整个子树
- ✅ **混合模式**：支持与 DDA 渲染按权重混合（0.0~1.0）
- ✅ **Fallback 机制**：Compute Shader 不支持时自动回退到 CPU 版本

#### 核心算法：
```hlsl
// DDA 遍历 SVO 节点池
while (t < tMax) {
    // 计算当前体素位置
    float3 currentVoxel = floor(roGrid + rdGrid * t);
    
    // 查找对应节点
    int nodeIdx = FindNode(currentVoxel);
    if (nodeIdx >= 0 && IsLeaf(nodeIdx)) {
        // 命中叶子节点，返回颜色
        return FetchColor(nodeIdx);
    }
    
    // 步进到下一个体素
    t += stepSize;
}
```

### 5. Inspector 编辑器集成
**文件**：`Assets/Scenes/Scripts/SVO/Editor/SVOSystemEditor.cs`

#### 实现功能：
- ✅ **Build SVO 按钮**：一键触发 SVO 构建流程
- ✅ **Toggle 可视化**：实时开关 Gizmos 显示
- ✅ **Ray Cast 启用**：控制 GPU 光线投射开关
- ✅ **混合滑块**：调节 SVO/DDA 混合比例（0.0~1.0）
- ✅ **统计信息显示**：展示节点数、深度、内存等数据

### 6. SVO 与 DDA 坐标对齐修复
**问题**：SVO 渲染比 DDA 渲染整体偏移 0.5 单位

**根因分析**：
- DDA shader 的 `toGridSpace` 函数有 +0.5 偏移（将本地空间 `[-0.5, 0.5]` 映射到网格空间 `[0, chunkSize]`）
- SVO 的坐标系统没有这个偏移，导致两者不一致

**修复方案**：
在 SVO shader 的射线起点转换中减去 0.5 偏移：
```hlsl
// SVO_RayCast.compute
float3 roGrid = (ro - _SVORootMin) / _SVOScale - float3(0.5, 0.5, 0.5);
```

**验证结果**：
- ✅ SVO 和 DDA 渲染完全对齐
- ✅ 体素边界仍然是整数坐标
- ✅ 没有间隙或重叠

---

## 二、关键技术决策

### 1. 为什么选择扁平节点池？
| 方案 | 优点 | 缺点 |
|------|------|------|
| **扁平数组** | 缓存友好，易于序列化，GPU 友好 | 需要手动管理索引 |
| **指针树** | 直观，易于调试 | 缓存不友好，GPU 难以访问 |
| **哈希表** | 稀疏存储节省内存 | 查找开销大，GPU 不支持 |

**结论**：扁平数组最适合 GPU 并行处理，且便于后续导出为二进制格式。

### 2. 为什么需要 -0.5 偏移？
- DDA 的历史设计：`toGridSpace(localPos) = localPos + 0.5`
- 这个偏移使体素中心对齐到网格点，但破坏了"体素占据整数单元格"的直观理解
- SVO 采用更纯粹的坐标系统：体素最小角在整数坐标 `(gx, gy, gz)`，占据 `[gx, gx+1]`
- 为了兼容 DDA，只能在射线起点处补偿这个偏移

### 3. 为什么 Gizmos 能看到体素但 Ray Cast 看不到？
**已解决的问题**：
- 最初 Ray Cast 完全不渲染任何体素
- 通过添加诊断日志发现 `_SVONodes` 数组未正确传递到 Shader
- 修复了 `Material.SetBuffer()` 调用时机问题

**当前状态**：
- ✅ Gizmos 正常显示所有深度的体素
- ✅ Ray Cast 能正确渲染并与 DDA 对齐

---

## 三、遇到的问题和解决方案

### 问题 1：CS0136 变量名冲突
**错误**：`A local or parameter named 'pos' cannot be declared in this scope`

**原因**：在 `FindNode` 函数中重复声明了 `pos` 变量

**解决**：重命名为 `currentVoxel`

### 问题 2：SVO 渲染偏移 0.5
**现象**：SVO 渲染的体素比 DDA 小半个单位

**尝试的错误方案**：
1. 修改 `VoxDataProvider.cs` 的 `FloorToInt` 为 `RoundToInt` → 反而变大 0.5
2. 在 SVO shader 中交换 Y/Z 轴 → 偏移更大
3. 修改 GBuffer shader 去掉 +0.5 → 破坏 DDA 渲染逻辑

**最终方案**：在 SVO shader 射线起点减 0.5

### 问题 3：多模型坐标系混淆
**现象**：日志显示两个城堡的体素在 SVO 网格位置不同

**原因**：两个 chunk 的世界位置不同（`(0, 10, 0)` vs `(-40, 10, 0)`），但 SVO 是统一坐标系

**结论**：这不是 bug，而是预期行为；真正的偏移问题是单个模型内部的 0.5 偏差

---

## 四、项目文件清单

```
VoxRender/
├── Assets/
│   ├── Scenes/
│   │   ├── Scripts/
│   │   │   ├── SVO/
│   │   │   │   ├── VoxToSVOBuilder.cs          # SVO 构建器
│   │   │   │   ├── SVOVisualizationData.cs     # 可视化数据结构
│   │   │   │   ├── SVOVisualizer.cs            # Gizmos 绘制
│   │   │   │   ├── SVORenderer.cs              # GPU Ray Cast 调度
│   │   │   │   ├── Editor/
│   │   │   │   │   └── SVOSystemEditor.cs      # Inspector 编辑器
│   │   │   │   └── Shaders/
│   │   │   │       ├── SVO_Visualize.shader    # 可视化 Shader
│   │   │   │       └── SVO_RayCast.compute     # Ray Cast Compute Shader
│   │   │   ├── VoxRender_GBuffer.shader        # DDA G-Buffer（含 +0.5 偏移）
│   │   │   ├── VoxRender_DDA.shader            # DDA 主 Shader
│   │   │   ├── VoxRender_Lighting.shader       # 光照合成 Shader
│   │   │   ├── VoxDataProvider.cs              # 体素数据提供者
│   │   │   └── VoxLoader.cs                    # Vox 文件加载器
│   │   └── Vox/
│   │       ├── castle.vox                      # 测试模型 1
│   │       └── castle-all.vox                  # 测试模型 2
│   └── Materials/
│       └── Sky.mat                             # 天空盒材质
├── SVO_Build_Design.md                         # SVO 设计文档
├── WorkSummary_20260805.md                     # 前期工作总结
├── WorkSummary_20260808.md                     # 中期工作总结
├── WorkSummary_20260809.md                     # 后期工作总结
└── WorkSummary_20260815.md                     # 本文档
```

---

## 五、下一步计划

### 短期目标（已完成）
- ✅ SVO Build 流程完整实现
- ✅ SVO Visualize Gizmos 正常工作
- ✅ SVO Ray Cast GPU 渲染正常工作
- ✅ SVO 与 DDA 坐标对齐

### 中期目标（待实现）
1. **性能优化**：
   - 实现自适应步长（adaptive stepping）
   - 添加包围盒裁剪（bounding box culling）
   - 优化节点池内存布局（SOA vs AOS）

2. **功能增强**：
   - 支持动态体素增删（增量更新 SVO）
   - 实现体素级光照（法线计算 + Phong 着色）
   - 添加软阴影（shadow mapping for voxels）

3. **工具链完善**：
   - SVO 导出为二进制格式（`.svo` 文件）
   - 支持从 `.svo` 文件快速加载
   - 实现 SVO 压缩算法（游程编码 RLE）

### 长期目标（探索方向）
1. **大规模场景**：
   - 支持多分辨率 SVO（LOD 系统）
   - 实现流式加载（streaming large worlds）
   - GPU-driven rendering pipeline

2. **高级渲染**：
   - 全局光照（voxel-based GI）
   - 反射和折射（ray tracing effects）
   - 体积雾和粒子（volumetric effects）

3. **编辑器工具**：
   - SVO 可视化编辑器（实时编辑体素）
   - 体素画笔工具（paint voxels）
   - SVO 分析和调试工具

---

## 六、关键知识点总结

### SVO vs DDA 对比
| 特性 | SVO | DDA |
|------|-----|-----|
| **数据结构** | 八叉树（稀疏） | 3D 纹理/数组（密集） |
| **内存占用** | O(N log N)，N=体素数 | O(X*Y*Z)，XYZ=包围盒尺寸 |
| **渲染算法** | 层级跳过（高效） | 逐体素步进（简单） |
| **适用场景** | 大规模稀疏场景 | 小规模密集场景 |
| **GPU 友好度** | 高（扁平数组） | 中（3D 纹理采样） |

### 坐标系统要点
1. **VoxReader 坐标系**：X=right, Y=forward, Z=up
2. **Unity 坐标系**：X=right, Y=up, Z=forward
3. **转换规则**：Vox Y → Unity Z，Vox Z → Unity Y
4. **网格空间**：体素最小角在整数坐标 `(gx, gy, gz)`
5. **偏移补偿**：DDA 有 +0.5 历史遗留，SVO 需 -0.5 补偿

### GPU Compute Shader 要点
1. **线程组配置**：`[numthreads(8, 8, 1)]` 适合屏幕空间处理
2. **RWTexture2D**：用于输出渲染结果
3. **StructuredBuffer**：传递 SVO 节点池数据
4. **Dispatch**：按屏幕分辨率分派线程组

---

## 七、技术亮点

1. **完整的 SVO 流水线**：从 `.vox` 文件解析到 GPU 渲染，全流程打通
2. **灵活的混合模式**：支持 SVO/DDA 任意比例混合，便于对比验证
3. **强大的可视化工具**：Gizmos 彩色编码深度，直观展示八叉树结构
4. **健壮的 Fallback 机制**：Compute Shader 不支持时自动回退到 CPU
5. **精确的坐标对齐**：通过深入分析找到并修复 0.5 偏移问题

---

## 八、经验教训

### 成功经验
1. **先可视化再渲染**：先用 Gizmos 验证 SVO 结构正确，再实现 GPU 渲染
2. **渐进式开发**：Build → Visualize → Ray Cast，每步都验证通过再继续
3. **详细日志诊断**：遇到偏移问题时，通过打印中间变量快速定位根因

### 踩坑记录
1. **不要盲目修改数据层**：最初试图修改 `VoxDataProvider.cs`，结果越改越乱
2. **理解历史债务**：DDA 的 +0.5 偏移是历史设计，强行"修正"会破坏现有逻辑
3. **坐标系转换要谨慎**：Y/Z 轴交换只在特定环节需要，不能全局应用

---

**总结**：今天完成了 SVO 可视化系统的核心功能实现，包括 Build、Visualize、Ray Cast 三大模块，并成功解决了坐标对齐问题。现在 SVO 渲染已经可以正常工作，并与 DDA 渲染完美对齐。
