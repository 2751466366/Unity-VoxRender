# VoxRender 项目工作总结

## 日期
2026-08-08

---

## 一、已完成工作

### 1. VoxLoader 代码重构与清理
**文件**：`Assets/Scenes/Scripts/VoxLoader.cs`

#### 改动内容：
- ✅ 移除多余枚举（渲染模式枚举等），**仅保留 3D 纹理渲染方式**
- ✅ 移除 `useIndividualCubes` 独立 Cube 渲染路径及相关无用函数
- ✅ 移除 `VoxChunks_3DTexture` 中间父物体，chunk 直接作为 VoxLoader 所在 GameObject 的子物体
- ✅ 模型创建位置改为跟随脚本所在 GameObject（`SetParent(transform, false)` + `localPosition`）
- ✅ 在 `LoadVoxFile()` 和 `ClearExistingVoxels()` 结束后添加本体位置 Debug 日志，确认创建/清除时本体位置未变化
- ✅ VoxLoader 自动创建材质逻辑：若 `chunk3DMaterial` 未赋值，自动实例化使用 `Unlit/VoxRender_DDA` shader 的 Material

### 2. .gitignore 配置
**文件**：`.gitignore`

基于 Unity 官方推荐配置，忽略以下目录/文件：
- **强制忽略**：`Library/`、`Temp/`、`Obj/`、`UserSettings/`、`Logs/`、`MemoryCaptures/`
- **可选忽略**：`*.csproj`、`*.sln`、`Build/`、`.vs/`、`.idea/`、`Thumbs.db`、`.DS_Store`
- `Packages/` 目录保留（`manifest.json` 跨机器追踪依赖）

### 3. DDA Shader 面法线 + 漫反射光照
**文件**：`Assets/Scenes/Scripts/VoxRender_DDA.shader`

#### 核心改动：
- ✅ **AABB Slab 相交测试**（[VoxRender_DDA.shader#L57-L78](file:///d:/UnityHub/Project/VoxRender/Assets/Scenes/Scripts/VoxRender_DDA.shader#L57-L78)）：提取光线进入立方体的面法线（`enterNormal`），避免首次命中体素时法线为零向量
- ✅ **初始法线判断**：相机在 AABB 外部时使用 Slab 进入面法线；相机在内部时按 `gridDir` 最大分量方向取入口面
- ✅ **坐标空间转换**：`hitNormal`（网格空间，Vox: x右/y前/z上）→ Unity 本地空间（x右/y上/z前，Y/Z 交换）→ 世界空间（`mul unity_ObjectToWorld`）
- ✅ **Lambert 漫反射**：`ndotl = max(0, dot(worldNormal, lightDir))`，配合 `_LightColor0`
- ✅ **环境光**：`_Ambient` 参数（0~1），确保背光面可见
- ✅ 修复光照方向错误：`_WorldSpaceLightPos0.xyz`（从表面指向光源）直接使用，去掉多余的负号

### 4. 自阴影 DDA 实现
**文件**：`Assets/Scenes/Scripts/VoxRender_DDA.shader`

#### 独立函数 `computeShadow`（[L82-L160](file:///d:/UnityHub/Project/VoxRender/Assets/Scenes/Scripts/VoxRender_DDA.shader#L82-L160)）：
```hlsl
float computeShadow(float3 hitVoxel, float3 gridLightDir, float3 chunkDim)
```

#### 实现逻辑：
1. **方向变换**：世界空间 `lightDir` → 本地空间（`unity_WorldToObject`）→ 网格空间（Y/Z 交换）
2. **初始化**：`shadowVoxel = hitVoxel`，`shadowNextBoundary` 基于 `hitVoxel` 计算，保证首次跨越的 t 值正确
3. **先步进后采样**（[L105-L149](file:///d:/UnityHub/Project/VoxRender/Assets/Scenes/Scripts/VoxRender_DDA.shader#L105-L149)）：
   - 每次循环先 DDA 步进跨越一个体素边界，`shadowVoxel` 变成相邻体素
   - 再做边界检查 + 采样 3D 纹理
   - **避免自遮挡**：第一次采样的是相邻体素而不是命中体素本身
4. **命中距离修正压暗幅度**（[L154-L159](file:///d:/UnityHub/Project/VoxRender/Assets/Scenes/Scripts/VoxRender_DDA.shader#L154-L159)）：
   - 用 `chunkDim.x + y + z` 作为归一化分母（对角线方向最大步进数）
   - 近处遮挡（`hitStep` 小）→ 返回 `_ShadowFactor`（最暗）
   - 远处遮挡 → 衰减到 `_ShadowFactor * 0.3`（较淡）
   - 无遮挡 → 返回 1.0
5. **颜色合成**：阴影只作用于 `diffuse`，`ambient` 不受影响

#### 新增材质参数：
| 参数 | 类型 | 默认值 | 作用 |
|------|------|--------|------|
| `_ShadowFactor` | Range(0,1) | 0.7 | 阴影压暗系数（材质面板可调节） |

---

## 二、Shader 关键 Bug 修复记录

| Bug | 根因 | 修复方式 |
|-----|------|----------|
| 相机贴立方体边缘时另一侧错误渲染其他模型 | `Cull Front` + 未做正确的 AABB 相交测试，光线起点错误 | 改用 AABB Slab 计算 `tEnter`，`entryPoint = localCameraPos + rayDir * max(tEnter, 0)`，配合 `Cull Front`（立方体背面进入） |
| 循环无法展开编译报错 | DDA 439 次迭代超过默认展开上限 | 循环添加 `[loop]` 属性使用动态流控 |
| 模型透明 | MagicaVoxel PAL chunk alpha 通常为 0 | 非空体素强制 `alpha = 1`（保留用户自行改回 `voxel.Color.A/255f` 的选项） |
| Queue 未定义报错 | `"Queue"="2000"` 非预定义字符串 | 改为 `"Queue"="Geometry"`（对应数值 2000，语义一致） |
| 首次命中体素法线错误 | `hitNormal` 初值 (0,0,0)，首次采样前未被步进更新 | 从 Slab 测试提取光线进入 AABB 的面法线作为初始值 |
| 光照方向相反 | Lambert 公式多写了负号 `dot(worldNormal, -lightDir)` | 去掉负号，直接用 `dot(worldNormal, lightDir)` |
| 紧贴体素阴影漏检（相邻向光面未变暗） | 初始 `shadowVoxel = hitVoxel + shadowStepDir` 导致 `shadowNextBoundary` 计算错误，首次 t 跳过了相邻体素 | `shadowVoxel` 从 `hitVoxel` 开始，`shadowNextBoundary` 用 `hitVoxel` 计算，先步进后采样 |
| 阴影距离归一化恒为 1 | 命中时 `stepCnt = hitStep+1`，比值恒 1 | 改用 `chunkDim.x+y+z` 做分母 |
| 自遮挡（命中体素把自己当成遮挡） | 先采样后步进，第一次采样的是 `hitVoxel` 自己 | 改为先步进后采样，第一次步进后已是相邻体素 |

---

## 三、当前渲染流程总览

```
.vox 文件
   ↓ VoxReader 解析
VoxData (Models[], Voxels[], Palette)
   ↓ VoxLoader.CreateChunksWith3DTexture()
按 size 创建 chunk → 遍历所属体素写入 3D Texture (RGBA32)
   ↓
创建 Cube Mesh (1x1x1) + 挂载 MeshFilter/MeshRenderer + 设置 Material (Unlit/VoxRender_DDA)
   ↓
CPU 提交 Draw Call（每个 chunk 1 个 Draw Call）
   ↓ VoxRender_DDA.shader
Vert: 顶点 localPos 传递给片元
Frag:
  1. localCameraPos + rayDir (本地空间)
  2. AABB Slab 求 entryPoint + hitNormal 初始法线
  3. gridOrigin / gridDir / currentVoxel / stepDir / tDelta / tMax 初始化
  4. [loop] DDA 主循环：tex3D 采样 → alpha>0.01?
       ↓ 命中：
          a. hitNormal 转换到 worldNormal
          b. Lambert 漫反射 + 环境光
          c. computeShadow() 二次 DDA 自阴影
          d. diffuse *= shadow → return fixed4(diffuse + ambient, a)
       ↓ 未命中：按 tMax 最小轴步进，更新 hitNormal
  5. 超出 chunkDim：discard
```

---

## 四、项目文件清单（当前状态）

```
VoxRender/
├── .gitignore                              # Unity 官方推荐配置
├── WorkSummary_20260805.md                 # 8/5 工作总结（初期调研+方案设计）
├── WorkSummary_20260808.md                 # 8/8 工作总结（本日）
└── Assets/
    └── Scenes/
        ├── Vox/
        │   └── castle.vox                  # 测试体素模型
        ├── Scripts/
        │   ├── VoxLoader.cs                # .vox 解析 + chunk 划分 + 3D Texture 创建
        │   └── VoxRender_DDA.shader        # AABB Slab + DDA 体素渲染 + 法线光照 + 自阴影
        └── SampleScene.unity
```

---

## 五、下一步计划（可选方向）

1. **性能优化**：`_MaxSteps` 按 chunk 体积自适应；阴影 DDA 提前终止优化
2. **Mipmap / 滤波**：3D 纹理 mipmap 缓解远距离锯齿
3. **多 chunk 接缝**：光线穿出当前 chunk 后是否需要进下一个 chunk 继续步进（当前单 chunk 无此需求）
4. **透明体素**：alpha 混合穿透（当前按 alpha>0.01 判定为实心）
5. **材质属性**：VOX 文件中的 MATT/Matl chunk 是否需要读取（粗糙度、金属度等）
