// ============================================================================
//  VoxRTPluginAPI.h — Unity C# 侧与 VoxRTPlugin（DXR 体素路径追踪）共享的接口契约
//
//  集成方式(C# 侧, DXRInterop.cs):
//    1. P/Invoke 查询接口(主线程安全):
//         RT_GetState / RT_GetCapability / RT_GetLastMessage / RT_GetStats
//    2. 场景构建与渲染全部通过 CommandBuffer.IssuePluginEventAndData 调度:
//         IntPtr func = RT_GetRenderEventFunc();
//         CommandBuffer.IssuePluginEventAndData(func, RT_EVENT_XXX, dataPtr);
//       dataPtr 指向下方对应结构体的封送内存(字段顺序/宽度必须一致)。
//
//  事件必须在 Unity 渲染线程上下文中执行(IssuePluginEvent 机制保证),
//  插件内部通过 IUnityGraphicsD3D12v8::ExecuteCommandList 把 DXR 命令
//  插入 Unity 命令流,顺序与相邻 dispatch / DLSS evaluate 严格保序。
//
//  ⚠ 结构体字段顺序与宽度一经发布不可改动 —— C# 侧 Marshal 布局依赖它。
//     追加字段只能放在结构体尾部。
// ============================================================================

#pragma once

// ============================================================================
//  编译期常量(必须与 C# DXRInterop / HLSL voxel_common.hlsli 三处一致)
// ============================================================================

// 砖边长(体素)。8³ = 512 格；改成 16 需同步改 IS 的 DDA 步数上限(24 → 48)
static const int VOXRT_BRICK_SIZE = 8;
// 每砖体素数 = BRICK_SIZE³
static const int VOXRT_BRICK_CELLS = VOXRT_BRICK_SIZE * VOXRT_BRICK_SIZE * VOXRT_BRICK_SIZE;
// 每砖占据掩码的 uint32 数量(512 bit)
static const int VOXRT_BRICK_OCC_U32 = VOXRT_BRICK_CELLS / 32;
// chunk 边长(体素)。64³ / 8³ = 512 砖；一个 chunk 对应一个 BLAS + 一个 TLAS instance
static const int VOXRT_CHUNK_SIZE = 64;
// 每个 chunk 沿单轴的砖数
static const int VOXRT_BRICKS_PER_CHUNK_AXIS = VOXRT_CHUNK_SIZE / VOXRT_BRICK_SIZE;
// 单 chunk 的砖格总数(仅用于本地索引编码，非分配量)
static const int VOXRT_BRICKS_PER_CHUNK = VOXRT_BRICKS_PER_CHUNK_AXIS *
                                          VOXRT_BRICKS_PER_CHUNK_AXIS *
                                          VOXRT_BRICKS_PER_CHUNK_AXIS;
// 砖数软上限：超过则 RT_BUILD_SCENE 失败，C# 侧回退 compute 后端
static const int VOXRT_MAX_BRICKS_FOR_DXR = 4000000;

// BrickRecord 字节步长(C++ 打包 / HLSL StructuredBuffer stride 必须一致)
//   occ[16]   64B
//   cellBase   4B
//   meta       4B
//   flags      4B
//   ox/oy/oz  12B  砖在 grid 空间的整数原点(IS 内 DDA 必需)
//   _pad       8B  补齐 16B 对齐
// 合计 96B。计划文档写的是 80B，实测 IS 必须知道砖原点才能做 DDA，
// 且 96B 保证 structured buffer 每条记录 16B 对齐(occ[] 可用 uint4 聚合读取)。
static const int VOXRT_BRICK_RECORD_BYTES = 96;

// 砖 meta 特殊值：表示该砖内材质不唯一，需查 per-cell 表
static const unsigned int VOXRT_META_PER_CELL = 0xFFFFFFFFu;

// 砖 flags 位定义
static const unsigned int VOXRT_BRICK_FLAG_SOLID = 1u << 0; // 512 格全满(IS 直接 ReportHit)
static const unsigned int VOXRT_BRICK_FLAG_UNIFORM = 1u << 1; // 单材质(meta 有效)

// RTRenderDesc.writeGuides 的位掩码。
//   SR / RR 都需要 MV + 深度；RR 额外需要 albedo/normal-roughness 三张引导面。
//   拆成两个 bit 是因为 SR 模式下写 RR 引导面纯属浪费，且会误触发 RR 的萤火虫阀值放宽。
static const int VOXRT_GUIDE_MOTION_DEPTH = 1 << 0; // 写 MV(u1) + 深度(u2)
static const int VOXRT_GUIDE_RR           = 1 << 1; // 写 RR 引导面(u3/u4/u5) + 大幅放宽萤火虫钳制

// ============================================================================
//  渲染事件 ID(与 C# 侧约定, 4 字节)
//  前缀 0x5654xxxx("VT")，避开 DLSSPlugin 的 0x4453xxxx
// ============================================================================
static const int RT_EVENT_INIT          = 0x56540001; // data: RTInitData
static const int RT_EVENT_BUILD_SCENE   = 0x56540002; // data: RTSceneDesc
static const int RT_EVENT_UPDATE_VOXELS = 0x56540003; // data: RTVoxelDelta
static const int RT_EVENT_RENDER        = 0x56540004; // data: RTRenderDesc
static const int RT_EVENT_RELEASE_SCENE = 0x56540005; // data: null
static const int RT_EVENT_SHUTDOWN      = 0x56540006; // data: null

// ============================================================================
//  插件状态(RT_GetState 返回值)
// ============================================================================
enum RTPluginState
{
    RT_STATE_NOT_INITIALIZED =  0,  // 尚未 INIT 或已 SHUTDOWN
    RT_STATE_INIT_SUCCEEDED  =  1,  // 设备/Pipeline 就绪，可 BUILD_SCENE
    RT_STATE_READY           =  2,  // 场景加速结构就绪，可 RENDER
    RT_STATE_INIT_FAILED     = -1,  // 非 D3D12 / Tier<1.0 / 缺 DXIL
    RT_STATE_SCENE_FAILED    = -2,  // 砖化失败(超上限/OOM)
    RT_STATE_DEVICE_LOST     = -3,  // 设备移除或命令提交失败
};

// ============================================================================
//  事件数据结构(C# 侧 Marshal 结构体, 字段顺序与宽度必须完全一致)
// ============================================================================

// RT_EVENT_INIT
struct RTInitData
{
    const wchar_t* dxilPath;   // voxel_pathtracing.dxil 的完整路径；null = DLL 同目录
    int            reserved;   // 对齐占位
};

// RT_EVENT_BUILD_SCENE
//   voxels / materials 直接指向 Unity 侧托管数组的 pin 内存，插件只读不持有。
//   worldPos 已是 scene-space 整数；materialIndex < 0 为墓碑(跳过)。
//   插件自行推导 rootMin(= 活跃体素最小角) 与 SVOBuilder 完全一致。
struct RTSceneDesc
{
    const void*  voxels;              // VoxelElement[16B]: int3 worldPos + int materialIndex
    int          voxelCount;          // 元素个数(含墓碑)
    const void*  materials;           // MaterialElement[48B]: 3 × float4
    int          materialCount;
    const void*  emissiveLights;      // float4[]: xyz=grid coord, w=matIndex
    const void*  emissiveAlias;       // float2[]: x=prob, y=alias
    int          emissiveCount;
    float        emissiveTotalPower;  // sum(pow(2,flux) * lum(color))
    int          rootMinX;            // 提示值；插件仍自算以保持独立正确性
    int          rootMinY;
    int          rootMinZ;
    float        voxelScale;          // 单个体素的世界尺寸(= SVOBuilder.VoxelWorldSize)
    int          svoDepth;            // 日志用，DXR 路径不依赖
};

// RT_EVENT_UPDATE_VOXELS
//   edits: int4 { x, y, z, matIndex }，matIndex < 0 = 删除。
//   ⚠ (x,y,z) 是**场景空间绝对整数体素坐标**，与 VoxelElement.worldPos 同源
//     （不是相对 rootMin 的 grid 坐标）；插件内部自行减 s_RootMin 后再定位砖。
//   插件只改 brick 占据掩码与 per-cell 材质，**不重建 BLAS**。
struct RTVoxelDelta
{
    const void* edits;      // int4[]
    int         editCount;
};

// RT_EVENT_RENDER
//   矩阵一律用**持久 16-float 块**(列主序，与 Unity Matrix4x4 内存序一致)：
//   渲染线程异步消费，调用方不得传入短命栈内存。
struct RTRenderDesc
{
    const float* invVP;               // 16 float: 逆 ViewProjection(裁剪 → 世界)
    const float* curVP;               // 16 float: 当前 ViewProjection(含 jitter)
    const float* prevVP;              // 16 float: 上一帧 ViewProjection(含 jitter)
    const float* cameraPos;           // 3 float
    const float* sunDir;              // 3 float: 指向光源
    const float* sunColor;            // 3 float: 颜色 × 强度
    const float* skyColor;            // 3 float: 无 Cubemap 时的回退天空色
    int          hasSun;              // 非 0 = 方向光启用
    int          hasSkyCube;          // 非 0 = skyCubemap 有效
    void*        skyCubemap;          // ID3D12Resource*(TextureCube)
    void*        colorRT;             // ID3D12Resource*: HDR 颜色输出(UAV)
    void*        mvRT;                // ID3D12Resource*: 像素空间 MV(RG32F)
    void*        depthRT;             // ID3D12Resource*: hardware depth(R32F)
    void*        diffuseAlbedoRT;     // ID3D12Resource*: RR 引导面(RGBA16F)
    void*        specularAlbedoRT;    // ID3D12Resource*: RR 引导面
    void*        normalRoughnessRT;   // ID3D12Resource*: xyz=法线, w=roughness
    float        jitterX;             // 渲染像素空间抖动(-0.5~0.5)
    float        jitterY;
    int          width;               // 内部渲染分辨率
    int          height;
    int          maxDepth;            // 最大弹射次数
    int          frameIndex;          // 时域累积/随机种子
    int          reset;               // 非 0 = 丢弃累积历史
    int          writeGuides;         // 位掩码: bit0=写 MV/深度, bit1=写 RR 引导面(VOXRT_GUIDE_*)
};

// ============================================================================
//  能力信息(RT_GetCapability 输出，仅查询用，不参与渲染)
// ============================================================================
struct RTCapability
{
    int raytracingTier;      // D3D12_RAYTRACING_TIER 枚举值(10 = 1_0, 11 = 1_1)
    int hasRayQuery;         // 1 = 支持 inline RayQuery(SM 6.5+)
    int maxBrickPrimitives;  // 本插件接受的砖数上限
    int reserved;
};

// ============================================================================
//  统计信息(RT_GetStats 输出)
// ============================================================================
struct RTStats
{
    int           brickCount;        // 非空砖数(= primitive 数)
    int           solidBrickCount;   // 实心砖数(IS 快速路径)
    int           chunkCount;        // chunk 数(= BLAS / instance 数)
    int           perCellBrickCount; // 非单材质砖数
    long long     blasBytes;         // BLAS 结果总字节(实测 PrebuildInfo)
    long long     tlasBytes;         // TLAS 结果字节(实测 PrebuildInfo)
};

// ============================================================================
//  C 导出接口
// ============================================================================
#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define RT_API extern "C" __declspec(dllexport)
#else
#define RT_API extern "C"
#endif

// Unity 生命周期入口 UnityPluginLoad / UnityPluginUnload：
// 已由 IUnityInterface.h 声明（Unity 强制约定的导出名，勿改名），实现位于 VoxRTPlugin.cpp。

// 渲染事件回调指针:返回 UnityRenderingEventAndData 签名的函数指针
RT_API void* RT_GetRenderEventFunc(void);

// 当前状态(RTPluginState)
RT_API int RT_GetState(void);

// 能力探测(需 INIT 成功后有效)；返回 0 = 成功, 非 0 = 未初始化
RT_API int RT_GetCapability(RTCapability* outCapability);

// 最近一次诊断信息(线程安全读取)
RT_API const char* RT_GetLastMessage(void);

// 场景统计(需 BUILD_SCENE 成功后有效)
RT_API void RT_GetStats(RTStats* outStats);

#ifdef __cplusplus
}
#endif
