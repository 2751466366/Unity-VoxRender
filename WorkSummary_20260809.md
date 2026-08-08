# VoxRender 项目工作总结

## 日期
2026-08-09

---

## 一、已完成工作

### 1. 架构重构：VoxLoader 拆分为 VoxDataProvider + VoxModel
**文件**：`Assets/Scenes/Scripts/VoxDataProvider.cs`、`Assets/Scenes/Scripts/VoxModel.cs`

#### 改动背景：
原 VoxLoader 将解析、渲染、资源管理耦合在一个脚本中。为支持多模型管理、后续 SVO 体素路径追踪等需求，拆分为：

| 脚本 | 职责 |
|------|------|
| VoxDataProvider | 单例管理器，负责 VOX 解析、3D 纹理创建、chunk 立方体创建、按句柄存取、生命周期管理 |
| VoxModel | 场景组件（门面），指定路径调用 provider 加载，chunk 作为自身子物体 |

#### 核心 API：
```csharp
// VoxDataProvider
int Load(string voxFilePath, Transform parent, float scale, Material material)
VoxAsset GetAsset(int handle)
IVoxFile GetVoxFile(int handle)
IList<Texture3D> GetTextures(int handle)
IList<GameObject> GetChunkCubes(int handle)
void Unload(int handle)
void UnloadAll()

// VoxModel
[ContextMenu("Load Vox File")] void Load()
[ContextMenu("Clear Voxels")] void Unload()
```

#### 句柄管理：
- `List<VoxAsset> assets` 按索引存取，句柄即索引
- 加载时优先复用 `isActive=false` 的空槽，无空槽才追加
- 卸载时不置 null（Unity 序列化不支持 List null 元素），仅标记 `isActive=false`

### 2. VoxAsset 资源生命周期管理
**文件**：`Assets/Scenes/Scripts/VoxDataProvider.cs`

- `[System.Serializable] class VoxAsset`：包含 filePath、voxFile、textures、chunkCubes、materials、chunkDims
- `isActive` 字段：标记资产是否有效（Dispose 后置 false），参与序列化跨重编译保持
- `Dispose()` 统一销毁立方体（SetActive(false) + DestroyImmediate）、材质、纹理
- `voxFile` 标记 `[System.NonSerialized]`（IVoxFile 是接口无法序列化，运行时按需重新读取）

### 3. Editor 只读显示
**文件**：`Assets/Scenes/Scripts/Editor/VoxRenderEditor.cs`

- VoxDataProvider/VoxModel 的运行时字段（assets/handle）加 `[HideInInspector]`
- 自定义 Editor 在 Inspector 底部用 `EditorGUI.BeginDisabledGroup(true)` 绘制只读运行时信息
- 可通过注释 `[CustomEditor]` 行快速启用/禁用

### 4. 世界空间 Shader（VoxRender_DDA_W）
**文件**：`Assets/Scenes/Scripts/VoxRender_DDA_W.shader`

#### 与本地空间版本的核心区别：
| 方面 | VoxRender_DDA（本地空间） | VoxRender_DDA_W（世界空间） |
|------|---|---|
| 顶点输出 | localPos | worldPos |
| 相机位置 | mul(unity_WorldToObject, _WorldSpaceCameraPos) | 直接用 _WorldSpaceCameraPos |
| AABB | 固定 [-0.5, 0.5] | CPU 传入 _WorldBoxMin/_WorldBoxMax |
| Cull | Cull Front | Cull Off |
| 深度输出 | 无 | SV_Depth（自定义命中点深度） |

#### 世界 AABB 更新：
- `VoxDataProvider.UpdateWorldAABB()`：变换本地 8 角点到世界空间取 min/max
- 创建时调用一次，`Update()` 每帧更新所有活跃 chunk

### 5. 自定义深度写入（SV_Depth）
**文件**：`Assets/Scenes/Scripts/VoxRender_DDA_W.shader`

#### 问题：
多 chunk 场景中，相机进入立方体内部时会显示应被遮挡的 chunk 体素。根因是 GPU 写入的是三角形深度（立方体表面），而非 DDA 命中的体素深度。

#### 修复：
- `#pragma target 4.0` + `struct fragOutput { fixed4 color : SV_Target; float depth : SV_Depth; }`
- 追踪 `entryT`（光线进入当前体素的时间），用 `gridOrigin + gridDir * entryT` 计算精确命中点
- `computeHitDepth()` 将命中点网格坐标 → 本地 → 世界 → 裁剪空间 → NDC 深度

### 6. DDA 体素扭曲修复（关键 Bug）
**文件**：`VoxRender_DDA_W.shader`、`VoxRender_DDA.shader`

#### 问题：
非立方体 chunk 从侧面观察时体素整体错位/拉伸，正面正常。

#### 根因：
`gridDir` 计算时归一化，丢失了 chunk 各轴的体素数量比例。

```hlsl
// ❌ 错误：归一化导致各轴步进频率比例错误
float3 gridDir = normalize(float3(localDir.x, localDir.z, localDir.y));

// ✅ 正确：乘以 chunkDim，让各分量比例反映体素密度
float3 gridDir = float3(localDir.x, localDir.z, localDir.y) * _ChunkSize.xyz;
```

#### 修复范围：
- 两个 shader 的主 DDA 方向
- 两个 shader 的阴影 DDA 方向
- 详见 `DebugNote_DDA体素扭曲问题.md`

### 7. 起点在体素内部的处理
**文件**：`Assets/Scenes/Scripts/VoxRender_DDA_W.shader`

- 条件：`s == 0 && tEnter <= 0.0`（相机在立方体内且首个体素即命中）
- 输出：`color = (0,0,0,0)`，`depth = 1.0`（Reversed-Z 最近）
- 用 `UNITY_REVERSED_Z` 宏兼容两种深度缓冲方向

### 8. VOX 旋转支持
**文件**：`Assets/Scenes/Scripts/VoxDataProvider.cs`

- 新增 `Matrix3ToQuaternion()` 函数：VoxReader Matrix3（Vox 坐标系）→ Unity Quaternion
- 自动处理 Vox→Unity 的 Y/Z 轴交换（`R_unity = SwapYZ * R_vox * SwapYZ`）
- chunk 立方体创建时设置 `localRotation`

---

## 二、Bug 修复记录

| Bug | 根因 | 修复方式 |
|-----|------|----------|
| VoxModel Start 时找不到 VoxDataProvider | `[ExecuteInEditMode]` 下 Awake/Start 时序不保证 | GetProvider() 兜底用 FindObjectOfType |
| Unload 不生效 | Handle 不被序列化，重编译后丢失 | 改为 `[SerializeField] private int handle` |
| Unload 仍不生效 | `??` 运算符不兼容 Unity fake-null | 改用显式 `== null` 逐级检查 |
| Unload 销毁无效 | VoxAsset.Dispose 与 VoxModel 双重 Destroy 同一对象 | Dispose 统一销毁，VoxModel 只转发调用 |
| assets 列表空槽"复活" | Unity 序列化 List 不支持 null 元素 | 永不置 null，仅用 isActive 标记 |
| 侧面体素扭曲 | gridDir 归一化丢失 chunkDim 比例 | gridDir 乘以 _ChunkSize.xyz，不归一化 |
| 深度=0 仍能看到其他体素 | Unity Reversed-Z 下 0=最远 | 用 UNITY_REVERSED_Z 宏，Reversed-Z 下 depth=1 |
| DestroyImmediate 编译错误 | VoxAsset 非 MonoBehaviour | 用 UnityEngine.Object.DestroyImmediate 完整限定 |

---

## 三、当前文件清单

```
VoxRender/
├── .gitignore
├── DebugNote_DDA体素扭曲问题.md           # DDA 扭曲问题排查记录
├── WorkSummary_20260805.md
├── WorkSummary_20260808.md
├── WorkSummary_20260809.md                 # 本日
└── Assets/
    └── Scenes/
        ├── Vox/
        │   └── *.vox
        ├── Scripts/
        │   ├── VoxDataProvider.cs          # 单例管理器（解析/3D纹理/chunk/句柄）
        │   ├── VoxModel.cs                 # 场景门面组件
        │   ├── VoxRender_DDA.shader        # 本地空间 DDA shader
        │   ├── VoxRender_DDA_W.shader      # 世界空间 DDA shader（含 SV_Depth）
        │   └── Editor/
        │       └── VoxRenderEditor.cs      # Inspector 只读显示
        └── SampleScene.unity
```

---

## 四、体素碰撞方案（待实现）

当前场景规模：**1130 个 chunk**，需要支持玩家进入建筑内部但被墙体阻挡。

### 方案 1：DDA 自定义碰撞（推荐）

复用 VoxDataProvider 的 3D 纹理数据，CPU 端跑 DDA 做碰撞检测。

```
玩家位置 → 找到所在 chunk → CPU DDA 检测前方体素 → 阻止/允许移动
```

| 指标 | 表现 |
|------|------|
| 额外内存 | 零（直接用 3D 纹理 GetPixel） |
| 1130 chunk 影响 | 无（只检测玩家附近 1-8 个 chunk） |
| 精度 | 体素级精确 |
| 兼容性 | 不支持 NavMesh、Physics.Raycast（需自己处理） |

**核心接口**：
```csharp
// VoxDataProvider 新增
bool IsVoxelSolid(Vector3 worldPos)
VoxAsset GetAssetAtWorldPos(Vector3 worldPos)  // 找到世界坐标所在的 chunk
```

**碰撞检测流程**：
1. 玩家移动前，沿移动方向取 N 个采样点（如玩家胶囊体的顶点/底点）
2. 每个采样点调 `IsVoxelSolid` 检测
3. 任一采样点在实心体素内 → 阻止该方向移动

**优点**：
- 1130 个 chunk 零额外开销
- 体素级精度
- 修改体素后碰撞即时更新（3D 纹理同步）

### 方案 3：Compute Shader DDA 碰撞（高性能版）

把 DDA 碰撞检测放到 Compute Shader 上，GPU 并行处理多个采样点。

```
玩家胶囊体采样点 → ComputeBuffer → Compute Shader DDA → 回读结果 → 阻止/允许
```

| 指标 | 表现 |
|------|------|
| 性能 | 极高（GPU 并行，一次 Dispatch 检测所有采样点） |
| 内存 | ComputeBuffer（采样点 + 结果） |
| 复杂度 | 高（GPU→CPU 回读，需处理异步等待） |

**适用场景**：
- 玩家胶囊体需要高密度采样（如 50+ 采样点）
- 多个 NPC 同时碰撞检测
- 方案 1 的 CPU 检测成为性能瓶颈时升级

**实现要点**：
- Compute Shader 接收 3D 纹理 + 采样点 buffer
- 每个 thread 处理一个采样点，跑 DDA 检测
- 结果写入 buffer，CPU 端 `GetData` 回读
- 用 `AsyncGPUReadback` 避免管线 stalls
