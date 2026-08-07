# VoxRender 项目工作总结

## 日期
2026-08-05

## 项目概述
新建 Unity 工程，基于 VoxReader 插件解析 `.vox` 体素模型，并探索自定义体素渲染方案。

---

## 一、已完成工作

### 1. 项目结构搭建
- 新建 Unity 工程 `VoxRender`
- 引入 VoxReader 插件（包名：`com.sandrofigo.voxreader@5.0.6`）
- 准备测试模型：`Assets/Scenes/Vox/castle.vox`
- 新建脚本目录：`Assets/Scenes/Scripts/`

### 2. VoxReader 插件 API 调研
深入阅读了插件全部源码（约 30 个文件），掌握了以下核心 API：

| 类/接口 | 命名空间 | 作用 |
|---------|---------|------|
| `VoxReader.VoxReader` | VoxReader | 静态入口，`Read(string filePath)` 读取 .vox 文件 |
| `IVoxFile` | VoxReader.Interfaces | 顶层文件接口，包含 Models[] 和 Palette |
| `IModel` | VoxReader.Interfaces | 单个模型，包含 Voxels[]、LocalSize、GlobalPosition 等 |
| `Voxel` | VoxReader | 结构体，包含 LocalPosition、GlobalPosition、Color、ColorIndex |
| `VoxReader.Vector3` | VoxReader | 自定义 int 向量（X=right, Y=forward, Z=up） |
| `VoxReader.Color` | VoxReader | 自定义 byte 颜色（R,G,B,A） |
| `Palette` | VoxReader | 调色板，包含 RawColors、Colors、Notes |

**坐标系统说明**：
- VoxReader：X=right, Y=forward, Z=up
- Unity：X=right, Y=up, Z=forward
- 需要 Y↔Z 互换转换

### 3. VoxLoader 脚本开发
**文件**：`Assets/Scenes/Scripts/VoxLoader.cs`

#### 实现功能：
- ✅ 解析 `.vox` 文件并输出模型信息到 Console
- ✅ 两种渲染模式：独立 Cube（`useIndividualCubes=true`）和合并 Mesh（`useIndividualCubes=false`）
- ✅ 自动坐标转换（VoxReader 坐标系 → Unity 坐标系）
- ✅ 颜色空间转换（byte → float 归一化）
- ✅ 编辑器模式支持（`[ExecuteInEditMode]`）
- ✅ 右键 Inspector 菜单：`Load Vox File` / `Clear Voxels`
- ✅ Undo/Redo 支持（编辑器模式下）
- ✅ 自动查找可用 Shader（Standard → URP → HDRP → Diffuse）

#### 遇到并解决的编译错误：

| 错误 | 原因 | 解决方案 |
|------|------|----------|
| CS1001 `base` 关键字冲突 | `base` 是 C# 保留关键字 | 改为 `baseIndex` |
| CS0104 `Vector3` 歧义 | `VoxReader.Vector3` 与 `UnityEngine.Vector3` 同名 | 移除 `using VoxReader;`，使用完全限定名 |
| ArgumentNullException shader 为 null | 项目使用 Built-in RP，`Shader.Find("URP/Lit")` 返回 null | 实现 `CreateDefaultMaterial()` 多级 Shader 回退 |

### 4. 体素渲染技术路线调研
调研了三种体素渲染方案：

| 方案 | 原理 | 优点 | 缺点 |
|------|------|------|------|
| **转普通网格**（当前） | 每个体素 = 一个 Cube | 简单，利用现有管线 | 体素多时性能差（几千 Draw Call） |
| **3D 纹理 + 光线步进** | 体素写入 3D 纹理，Shader 中光线步进采样 | 性能好，支持动态修改 | 需要自定义 Shader 和渲染管线 |
| **GPU Instancing** | `DrawMeshInstanced` 一次性提交所有体素 | 仅 1 个 Draw Call | 无法体素级着色 |

---

## 二、光线步进渲染方案设计（未实现，流程已定）

### 整体流程
```
.vox 文件 → VoxReader 解析 → 写入 3D 纹理 → 自定义 Shader 光线步进 → OnRenderImage 接管渲染
```

### 需要创建的文件

| 文件 | 类型 | 职责 |
|------|------|------|
| `VoxLoader.cs` | 修改 | 增加 3D 纹理写入逻辑 |
| `VoxRayMarching.shader` | 新建 | 光线步进核心 Shader |
| `VoxRenderer.cs` | 新建 | 挂 Main Camera，通过 `OnRenderImage` 接管渲染 |

### 编辑器配置
```
Main Camera:
  - Culling Mask → Nothing
  - Clear Flags → Solid Color (Black)

VoxSystem GameObject:
  - 挂载 VoxLoader
  - 挂载 VoxRenderer
  - 指定 VoxRayMarching Shader
```

### 核心技术点
- **3D 纹理**：`RenderTexture` with `volumeDepth = modelSize.z`，RGBA32 格式
- **光线步进**：从摄像机发射射线 → 计算包围盒交点 → 逐步采样 3D 纹理 → 命中返回颜色
- **渲染接管**：`Graphics.Blit(source, dest, material)` 将光线步进结果输出到屏幕
- **Scene 视图**：不受影响，继续用默认管线渲染（便于调试）

---

## 三、关键知识点总结

### Unity 渲染管线
```
Mesh + Material + Shader → GPU Draw Call → 屏幕像素
```
- Unity 引擎本身不知道"体素"概念，只负责提交几何数据
- 是 Shader 代码决定了渲染方式（普通光栅化 vs 光线步进）

### 两种相机
| 相机 | 位置 | 可控性 |
|------|------|--------|
| Game 视图相机 | 场景中的 Main Camera | 完全可控 |
| Scene 视图相机 | Unity 内置 | 不可控 |

### CullingMask 的作用
- `Nothing` → 不渲染任何层，Game 窗口变空白画布
- 配合 `OnRenderImage` 可实现完全自定义渲染接管

---

## 四、下一步计划

1. **实现 3D 纹理写入**：在 VoxLoader 中增加 `WriteTo3DTexture()` 方法
2. **编写光线步进 Shader**：实现最简版光线步进（不含光照）
3. **编写 VoxRenderer**：通过 `OnRenderImage` 接管 Game 视图渲染
4. **增加光照**：漫反射 + 软阴影
5. **优化**：自适应步长、包围盒裁剪

---

## 五、项目文件清单

```
VoxRender/
├── Assets/
│   ├── Scenes/
│   │   ├── Vox/
│   │   │   └── castle.vox          # 测试体素模型
│   │   ├── Scripts/
│   │   │   └── VoxLoader.cs        # 已完成（解析 + 渲染接管待实现）
│   │   └── SampleScene.unity
│   └── Scenes.meta
├── Library/
│   └── PackageCache/
│       └── com.sandrofigo.voxreader@5.0.6/  # VoxReader 插件
└── Packages/
    ├── manifest.json
    └── packages-lock.json
```