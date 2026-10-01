// ============================================================================
//  DLSSPluginAPI.h — Unity C# 侧与本插件共享的接口契约
//
//  集成方式(C# 侧,步骤 4 时使用):
//    1. P/Invoke 查询接口(主线程安全):
//         DLSS_GetState / DLSS_GetOptimalSettings / DLSS_GetLastMessage
//    2. 渲染操作全部通过 GL/CommandBuffer.IssuePluginEventAndData 调度:
//         IntPtr func = DLSS_GetRenderEventFunc();
//         CommandBuffer.IssuePluginEventAndData(func, DLSS_EVENT_XXX, dataPtr);
//       dataPtr 指向下方对应结构体的封送内存(字段顺序/宽度必须一致)。
//
//  事件必须在 Unity 渲染线程上下文中执行(IssuePluginEvent 机制保证),
//  插件内部通过 IUnityGraphicsD3D12v8::ExecuteCommandList 把 NGX 命令
//  插入 Unity 命令流,顺序在 compute dispatch 之后。
// ============================================================================

#pragma once

// ---- 渲染事件 ID(与 C# 侧约定,4 字节) ----
static const int DLSS_EVENT_INIT            = 0x44530001; // data: DLSSInitData
static const int DLSS_EVENT_CREATE_FEATURE  = 0x44530002; // data: DLSSCreateData
static const int DLSS_EVENT_EVALUATE        = 0x44530003; // data: DLSSEvalData
static const int DLSS_EVENT_RELEASE_FEATURE = 0x44530004; // data: null
static const int DLSS_EVENT_SHUTDOWN        = 0x44530005; // data: null

// ---- 插件状态(DLSS_GetState 返回值) ----
enum DLSSPluginState
{
    DLSS_STATE_NOT_INITIALIZED =  0,  // 尚未 INIT 或已 SHUTDOWN
    DLSS_STATE_INIT_SUCCEEDED  =  1,  // NGX Init 成功,可 CreateFeature
    DLSS_STATE_FEATURE_READY   =  2,  // DLSS feature 可用,可 Evaluate
    DLSS_STATE_INIT_FAILED     = -1,  // 初始化失败(非 RTX/驱动过旧/缺 dll)
    DLSS_STATE_FEATURE_FAILED  = -2,  // feature 创建失败或运行中连续失败
};

// ---- 质量模式(直接对应 NVSDK_NGX_PerfQuality_Value) ----
enum DLSSPerfQuality
{
    DLSS_QUALITY_MAX_PERF       = 0,  // ~33%
    DLSS_QUALITY_BALANCED       = 1,  // ~58%
    DLSS_QUALITY_MAX_QUALITY    = 2,  // ~67%
    DLSS_QUALITY_ULTRA_PERF     = 3,  // ~33%(带动态分辨率范围)
    DLSS_QUALITY_ULTRA_QUALITY  = 4,  // ~80%
    DLSS_QUALITY_DLAA           = 5,  // 100%(仅降噪+AA,不上采样)
};

// ---- Feature 创建 flags(对应 NVSDK_NGX_DLSS_Feature_Flags) ----
static const int DLSS_FLAG_IS_HDR          = 1 << 0;
static const int DLSS_FLAG_MV_LOW_RES      = 1 << 1; // MV 为渲染分辨率(非输出分辨率)
static const int DLSS_FLAG_MV_JITTERED     = 1 << 2; // MV 采样时已含 jitter
static const int DLSS_FLAG_DEPTH_INVERTED  = 1 << 3; // hardware depth:近平面=0,远平面=1

// ============================================================================
//  事件数据结构(C# 侧 Marshal 结构体,字段顺序与宽度必须完全一致)
// ============================================================================

// DLSS_EVENT_INIT
struct DLSSInitData
{
    const wchar_t* dllSearchPath;   // nvngx_dlss.dll 所在目录(如 "<project>/Assets/Plugins/x86_64"),可为 null
    const wchar_t* appDataPath;     // NGX 日志/快照目录,可为 null(默认 ".")
    unsigned long long appId;       // 应用 ID,可为 0(插件用默认值)
};

// DLSS_EVENT_CREATE_FEATURE
struct DLSSCreateData
{
    unsigned int inputWidth;        // 渲染分辨率
    unsigned int inputHeight;
    unsigned int outputWidth;       // 输出分辨率
    unsigned int outputHeight;
    int perfQuality;                // DLSSPerfQuality
    int featureFlags;               // DLSS_FLAG_* 组合
};

// DLSS_EVENT_EVALUATE
struct DLSSEvalData
{
    void* color;                    // ID3D12Resource*:低分辨率 HDR 颜色(渲染输出)
    void* motionVectors;            // ID3D12Resource*:屏幕空间运动向量
    void* depth;                    // ID3D12Resource*:深度
    void* output;                   // ID3D12Resource*:高分辨率输出
    float jitterX;                  // 渲染像素空间的子像素抖动(-0.5~0.5)
    float jitterY;
    float mvScaleX;                 // MV → 像素空间缩放;0 = 使用默认 1.0
    float mvScaleY;
    unsigned int subrectWidth;      // 实际渲染尺寸(= inputWidth/Height,除非局部渲染)
    unsigned int subrectHeight;
    int reset;                      // 非 0 = 丢弃历史(分辨率变化、场景切换)
};

// ============================================================================
//  C 导出接口
// ============================================================================
#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define DLSS_API extern "C" __declspec(dllexport)
#else
#define DLSS_API extern "C"
#endif

// Unity 生命周期入口 UnityPluginLoad / UnityPluginUnload：
// 已由 IUnityInterface.h 声明（Unity 强制约定的导出名，通过 GetProcAddress 查找并
// 自动调用，勿改名、勿手动调用），实现位于 DLSSPlugin.cpp。
// 此处不再重复声明——参数签名若与 IUnityInterface.h 不一致会触发 extern "C" 重载冲突。

// 渲染事件回调指针:返回 UnityRenderingEventAndData 签名的函数指针
DLSS_API void* DLSS_GetRenderEventFunc(void);

// 当前状态(DLSSPluginState)
DLSS_API int DLSS_GetState(void);

// 查询 DLSS 推荐渲染分辨率(需 Init 成功;纯参数计算,不走 GPU)
DLSS_API int DLSS_GetOptimalSettings(int outputWidth, int outputHeight, int perfQuality,
                                     int* outOptimalWidth, int* outOptimalHeight);

// 最近一次诊断信息(线程安全读取)
DLSS_API const char* DLSS_GetLastMessage(void);

#ifdef __cplusplus
}
#endif
