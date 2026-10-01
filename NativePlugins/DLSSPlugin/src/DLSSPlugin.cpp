// ============================================================================
//  DLSSPlugin.cpp — Unity 原生插件:NVIDIA NGX DLSS-SR / DLSS-RR(Ray Reconstruction)集成(D3D12 后端)
//
//  职责:
//    1. UnityPluginLoad 获取 IUnityGraphicsD3D12v8(ID3D12Device/队列/ExecuteCommandList)
//    2. NGX Init(静态链接 nvsdk_ngx_s.lib,运行时加载 nvngx_dlss.dll)
//    3. DLSS-SR feature 创建与每帧 evaluate(经 Unity 命令流,资源 UAV 状态契约)
//    4. 分层失败降级:任何一步失败只置状态位,绝不抛出/崩溃(C# 侧轮询状态)
//
//  执行模型:
//    所有 GPU 操作(Init/CreateFeature/Evaluate)由 C# 通过
//    IssuePluginEventAndData 在 Unity 渲染线程调度,插件自建 command list
//    录制 NGX 调用后,交给 IUnityGraphicsD3D12v8::ExecuteCommandList 执行,
//    states 参数声明 expected/current = UNORDERED_ACCESS,由 Unity 协调屏障,
//    保证 NGX 命令位于 compute dispatch 之后。
//
//  未来 Vulkan 后端:替换本文件 NGX_* 调用与命令基础设施,C 导出接口不变
//  (见方案文档「架构决策记录 → 决策 2」)。
// ============================================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>

#include "../UnityHeaders/IUnityInterface.h"
#include "../UnityHeaders/IUnityGraphics.h"
#include "../UnityHeaders/IUnityGraphicsD3D12.h"

#include "nvsdk_ngx.h"
#include "nvsdk_ngx_defs.h"
#include "nvsdk_ngx_params.h"
#include "nvsdk_ngx_helpers.h"
#include "nvsdk_ngx_defs_dlssd.h"    // RR(DLSS-D):参数名/枚举
#include "nvsdk_ngx_params_dlssd.h"  // RR:NVSDK_NGX_DLSSD_Create_Params
#include "nvsdk_ngx_helpers_dlssd.h" // RR:NGX_D3D12_CREATE/EVALUATE_DLSSD_EXT

#include "DLSSPluginAPI.h"

// ============================================================================
//  全局状态
// ============================================================================

static IUnityGraphics*          s_Graphics    = nullptr;
static IUnityGraphicsD3D12v8*   s_D3D12       = nullptr;
static ID3D12Device*            s_Device      = nullptr;
static ID3D12Fence*             s_FrameFence  = nullptr; // Unity 帧围栏(非拥有)

static std::atomic<int>         s_State { DLSS_STATE_NOT_INITIALIZED };

// NGX 对象
static NVSDK_NGX_Handle*        s_Feature          = nullptr;
static NVSDK_NGX_Parameter*     s_Params           = nullptr;  // 创建/评估共用(每次用前 Reset)
static NVSDK_NGX_Parameter*     s_CapabilityParams = nullptr;  // OptimalSettings 查询用

// Init 参数(静态保存:NGX 可能保留指针)
static wchar_t  s_DllSearchPath[MAX_PATH + 1] = {};
static wchar_t  s_AppDataPath[MAX_PATH + 1]   = {};
static unsigned long long s_AppId = 0;

// 活动模式(CreateFeature 时确定):0 = DLSS-SR, 1 = RR(DLSS-D)
static int s_ActiveMode = 0;

// NGX 日志回调:SDK fallback 模式下不挂回调则只走 OutputDebugString,不落盘。
// 调试期把 NGX 日志抓到 %TEMP%\NvNGX_VoxRender.log,便于排查 CreateFeature 错误码。
static std::mutex s_NgxLogMutex;
static FILE*      s_NgxLogFile = nullptr;
static void NgxLogCallback(const char* message, NVSDK_NGX_Logging_Level, NVSDK_NGX_Feature)
{
    if (!message) return;
    OutputDebugStringA("[NGX] ");
    OutputDebugStringA(message);
    std::lock_guard<std::mutex> lk(s_NgxLogMutex);
    if (!s_NgxLogFile)
    {
        char path[MAX_PATH];
        if (GetTempPathA(MAX_PATH, path) && strcat_s(path, "NvNGX_VoxRender.log") == 0)
            fopen_s(&s_NgxLogFile, path, "a");
    }
    if (s_NgxLogFile)
    {
        fputs(message, s_NgxLogFile);
        if (!strchr(message, '\n')) fputc('\n', s_NgxLogFile);
        fflush(s_NgxLogFile);
    }
}

// RR 矩阵静态存储:NGX 在渲染线程异步消费 float*,不能指向调用方短命内存
// (C# 源块只需活到本事件返回,插件拷贝后自行持有生命周期)
static float s_WorldToView[16] = {};
static float s_ViewToClip[16]  = {};

// 诊断信息(SRWLOCK 保护)
static SRWLOCK      s_MsgLock   = SRWLOCK_INIT;
static char         s_Msg[512]  = {};

// 连续 evaluate 失败计数(超过阈值自动降级)
static std::atomic<int> s_ConsecutiveFails { 0 };
static const int        kMaxConsecutiveFails = 5;

// ============================================================================
//  诊断信息
// ============================================================================

static void SetMessage(const char* fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    AcquireSRWLockExclusive(&s_MsgLock);
    vsnprintf(s_Msg, sizeof(s_Msg), fmt, args);
    s_Msg[sizeof(s_Msg) - 1] = '\0';
    ReleaseSRWLockExclusive(&s_MsgLock);
    va_end(args);
    OutputDebugStringA("[DLSSPlugin] ");
    OutputDebugStringA(s_Msg);
    OutputDebugStringA("\n");
}

// ============================================================================
//  命令录制基础设施
//  4 槽环形 allocator/commandList,复用前等待 Unity 帧围栏,
//  避免 reset 正在被 GPU 执行的 allocator。
// ============================================================================

struct CmdSlot
{
    ID3D12CommandAllocator*    allocator  = nullptr;
    ID3D12GraphicsCommandList* list       = nullptr;
    UINT64                     fenceValue = 0;
};

static CmdSlot   s_Slots[4]       = {};
static int       s_NextSlot       = 0;
static bool      s_CmdInfraReady  = false;

static void DestroyCmdInfra()
{
    for (CmdSlot& s : s_Slots)
    {
        if (s.list)      { s.list->Release();      s.list      = nullptr; }
        if (s.allocator) { s.allocator->Release(); s.allocator = nullptr; }
        s.fenceValue = 0;
    }
    s_CmdInfraReady = false;
}

static bool CreateCmdInfra()
{
    if (!s_Device) return false;
    for (CmdSlot& s : s_Slots)
    {
        HRESULT hr = s_Device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,
                                                      IID_PPV_ARGS(&s.allocator));
        if (FAILED(hr)) { SetMessage("CreateCommandAllocator failed: 0x%08x", hr); DestroyCmdInfra(); return false; }

        hr = s_Device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT,
                                         s.allocator, nullptr, IID_PPV_ARGS(&s.list));
        if (FAILED(hr)) { SetMessage("CreateCommandList failed: 0x%08x", hr); DestroyCmdInfra(); return false; }
        // 创建出来即 recording 状态,先 Close,后续按需 Reset
        s.list->Close();
    }
    s_CmdInfraReady = true;
    return true;
}

// 取一个槽并开始录制;失败返回 nullptr
static ID3D12GraphicsCommandList* BeginCmdList(int& outSlotIndex)
{
    outSlotIndex = -1;
    if (!s_CmdInfraReady) { SetMessage("Cmd infra not ready"); return nullptr; }

    const int idx = s_NextSlot;
    s_NextSlot = (s_NextSlot + 1) % 4;
    CmdSlot& s = s_Slots[idx];

    // 该槽上一次执行的 fence 未完成则等待(正常情况下 4 槽轮转不会等待)
    if (s.fenceValue != 0 && s_FrameFence)
    {
        unsigned int spins = 0;
        while (s_FrameFence->GetCompletedValue() < s.fenceValue)
        {
            if (++spins > (1u << 28)) { SetMessage("Fence wait timeout (slot %d)", idx); return nullptr; }
            YieldProcessor();
        }
    }

    HRESULT hr = s.allocator->Reset();
    if (FAILED(hr)) { SetMessage("Allocator Reset failed: 0x%08x", hr); return nullptr; }
    hr = s.list->Reset(s.allocator, nullptr);
    if (FAILED(hr)) { SetMessage("CmdList Reset failed: 0x%08x", hr); return nullptr; }

    outSlotIndex = idx;
    return s.list;
}

// 关闭并提交到 Unity 命令流;states 可为 null(无资源状态声明)
static bool EndAndExecute(int slotIndex, ID3D12GraphicsCommandList* cmd,
                          UnityGraphicsD3D12ResourceState* states, int stateCount)
{
    HRESULT hr = cmd->Close();
    if (FAILED(hr)) { SetMessage("CmdList Close failed: 0x%08x", hr); return false; }

    const UINT64 fenceValue = s_D3D12->ExecuteCommandList(cmd, stateCount, states);
    s_Slots[slotIndex].fenceValue = fenceValue;
    return true;
}

// ============================================================================
//  渲染事件处理
// ============================================================================

static void HandleShutdown();

static void HandleInit(DLSSInitData* data)
{
    // 渲染线程上下文:重新获取设备(设备可能在插件加载后才创建)
    if (!s_D3D12 || !s_Graphics || s_Graphics->GetRenderer() != kUnityGfxRendererD3D12)
    {
        SetMessage("Init: renderer is not D3D12");
        s_State = DLSS_STATE_INIT_FAILED;
        return;
    }
    s_Device     = s_D3D12->GetDevice();
    s_FrameFence = s_D3D12->GetFrameFence();
    if (!s_Device) { SetMessage("Init: ID3D12Device unavailable"); s_State = DLSS_STATE_INIT_FAILED; return; }

    if (s_State != DLSS_STATE_NOT_INITIALIZED)
        HandleShutdown(); // 幂等:重新初始化前先清干净

    // 保存 Init 参数(NGX 可能保留指针,用静态存储)
    if (data && data->dllSearchPath && data->dllSearchPath[0])
    {
        wcsncpy(s_DllSearchPath, data->dllSearchPath, MAX_PATH);
        s_DllSearchPath[MAX_PATH] = L'\0';
    }
    else s_DllSearchPath[0] = L'\0';

    if (data && data->appDataPath && data->appDataPath[0])
    {
        wcsncpy(s_AppDataPath, data->appDataPath, MAX_PATH);
        s_AppDataPath[MAX_PATH] = L'\0';
    }
    else s_AppDataPath[0] = L'\0';

    s_AppId = (data && data->appId) ? data->appId : 0x00564F58ULL; // 默认 "VOX"

    // ---- NGX Init ----
    NVSDK_NGX_FeatureCommonInfo info = {};
    const wchar_t* pathPtrs[1] = {};
    if (s_DllSearchPath[0] != L'\0')
    {
        pathPtrs[0]           = s_DllSearchPath;
        info.PathListInfo.Path   = pathPtrs;
        info.PathListInfo.Length = 1;
    }
    info.LoggingInfo.LoggingCallback    = NgxLogCallback;
    info.LoggingInfo.MinimumLoggingLevel = NVSDK_NGX_LOGGING_LEVEL_VERBOSE; // 调试期：经回调写入 %TEMP%\NvNGX_VoxRender.log；定稿改回 OFF

    const wchar_t* appDataPath = (s_AppDataPath[0] != L'\0') ? s_AppDataPath : L".";
    NVSDK_NGX_Result r = NVSDK_NGX_D3D12_Init(s_AppId, appDataPath, s_Device, &info, NVSDK_NGX_Version_API);
    if (NVSDK_NGX_FAILED(r))
    {
        // 常见原因:非 RTX 显卡、驱动过旧、nvngx_dlss.dll 缺失/签名校验失败
        SetMessage("NGX_D3D12_Init failed: 0x%08x", r);
        s_State = DLSS_STATE_INIT_FAILED;
        return;
    }

    // ---- 参数对象 ----
    r = NVSDK_NGX_D3D12_AllocateParameters(&s_Params);
    if (NVSDK_NGX_FAILED(r))
    {
        SetMessage("AllocateParameters failed: 0x%08x", r);
        NVSDK_NGX_D3D12_Shutdown1(s_Device);
        s_State = DLSS_STATE_INIT_FAILED;
        return;
    }
    // Capability parameters(含 DLSSOptimalSettingsCallback,查询推荐分辨率用)
    if (NVSDK_NGX_FAILED(NVSDK_NGX_D3D12_GetCapabilityParameters(&s_CapabilityParams)))
        s_CapabilityParams = nullptr; // 非致命:仅影响 DLSS_GetOptimalSettings

    // ---- 命令基础设施 ----
    if (!CreateCmdInfra())
    {
        NVSDK_NGX_D3D12_DestroyParameters(s_Params); s_Params = nullptr;
        NVSDK_NGX_D3D12_Shutdown1(s_Device);
        s_State = DLSS_STATE_INIT_FAILED;
        return;
    }

    s_State = DLSS_STATE_INIT_SUCCEEDED;
    SetMessage("NGX Init OK (dlss path: %ls)", s_DllSearchPath[0] ? s_DllSearchPath : L"<exe dir>");
}

static void HandleCreateFeature(DLSSCreateData* data)
{
    if (s_State != DLSS_STATE_INIT_SUCCEEDED) { SetMessage("CreateFeature: NGX not initialized"); return; }
    if (!data || data->inputWidth == 0 || data->inputHeight == 0 ||
        data->outputWidth == 0 || data->outputHeight == 0)
    {
        SetMessage("CreateFeature: invalid data");
        s_State = DLSS_STATE_FEATURE_FAILED;
        return;
    }

    s_ActiveMode = (data->mode == 1) ? 1 : 0;
    s_Params->Reset(); // 清除上次 evaluate/create 残留的键

    // 创建命令经 Unity 命令流执行(与后续 evaluate 同队列,顺序保证)
    int slot = -1;
    ID3D12GraphicsCommandList* cmd = BeginCmdList(slot);
    if (!cmd) { s_State = DLSS_STATE_FEATURE_FAILED; return; }

    NVSDK_NGX_Result r;
    if (s_ActiveMode == 1)
    {
        // ---- RR (DLSS-D) ----
        NVSDK_NGX_DLSSD_Create_Params createParams = {};
        createParams.InWidth                = data->inputWidth;
        createParams.InHeight               = data->inputHeight;
        createParams.InTargetWidth          = data->outputWidth;
        createParams.InTargetHeight         = data->outputHeight;
        createParams.InPerfQualityValue     = static_cast<NVSDK_NGX_PerfQuality_Value>(data->perfQuality);
        // RR 强制要求声明 MV 为渲染分辨率（MVLowRes）；我们的 MV 本就在输入分辨率，仅需声明
        createParams.InFeatureCreateFlags   = data->featureFlags | NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
        createParams.InEnableOutputSubrects = false;
        createParams.InRoughnessMode        = data->roughnessMode
            ? NVSDK_NGX_DLSS_Roughness_Mode_Packed    // roughness 存 normals.w
            : NVSDK_NGX_DLSS_Roughness_Mode_Unpacked;
        createParams.InUseHWDepth           = NVSDK_NGX_DLSS_Depth_Type_HW; // 输出即 HW depth(非反转)
        // DLAA 档固定 preset D（transformer 模型）；注意 1/2/3 是已移除的 preset A/B/C，勿用。
        // 用 unsigned 重载（与 helper 内部 SetUI 一致，避免参数表类型不匹配）
        s_Params->Set(NVSDK_NGX_Parameter_RayReconstruction_Hint_Render_Preset_DLAA,
                      static_cast<unsigned int>(NVSDK_NGX_RayReconstruction_Hint_Render_Preset_D));

        r = NGX_D3D12_CREATE_DLSSD_EXT(cmd, 0, 0, &s_Feature, s_Params, &createParams);
    }
    else
    {
        // ---- DLSS-SR ----
        NVSDK_NGX_DLSS_Create_Params createParams = {};
        createParams.Feature.InWidth           = data->inputWidth;
        createParams.Feature.InHeight          = data->inputHeight;
        createParams.Feature.InTargetWidth     = data->outputWidth;
        createParams.Feature.InTargetHeight    = data->outputHeight;
        createParams.Feature.InPerfQualityValue = static_cast<NVSDK_NGX_PerfQuality_Value>(data->perfQuality);
        createParams.InFeatureCreateFlags      = data->featureFlags;
        createParams.InEnableOutputSubrects    = false;

        r = NGX_D3D12_CREATE_DLSS_EXT(cmd, 0, 0, &s_Feature, s_Params, &createParams);
    }

    if (NVSDK_NGX_FAILED(r))
    {
        SetMessage("CreateFeature(%s) failed: 0x%08x", s_ActiveMode == 1 ? "RR" : "DLSS-SR", r);
        EndAndExecute(slot, cmd, nullptr, 0); // 即使失败也把已录制命令收尾
        s_Feature = nullptr;
        s_State   = DLSS_STATE_FEATURE_FAILED;
        return;
    }
    if (!EndAndExecute(slot, cmd, nullptr, 0)) { s_State = DLSS_STATE_FEATURE_FAILED; return; }

    s_State = DLSS_STATE_FEATURE_READY;
    SetMessage("%s feature ready (in %ux%u -> out %ux%u)",
               s_ActiveMode == 1 ? "DLSS-RR" : "DLSS-SR",
               data->inputWidth, data->inputHeight, data->outputWidth, data->outputHeight);
}

static void HandleEvaluate(DLSSEvalData* data)
{
    if (s_State != DLSS_STATE_FEATURE_READY) return; // 降级态静默跳过
    if (!data || !data->color || !data->output || !data->motionVectors || !data->depth)
    {
        SetMessage("Evaluate: null input/output");
        return;
    }
    if (s_ActiveMode == 1 &&
        (!data->diffuseAlbedo || !data->specularAlbedo || !data->normals ||
         !data->worldToViewMatrix || !data->viewToClipMatrix))
    {
        SetMessage("Evaluate(RR): missing GBuffer inputs");
        return;
    }

    // 重置参数表(清除上次 evaluate/CreateFeature 残留的键)
    s_Params->Reset();

    int slot = -1;
    ID3D12GraphicsCommandList* cmd = BeginCmdList(slot);
    if (!cmd) return; // 本帧跳过,不影响状态

    NVSDK_NGX_Result r;
    UnityGraphicsD3D12ResourceState states[7];
    int stateCount = 4;

    if (s_ActiveMode == 1)
    {
        // ---- RR:矩阵拷贝到插件持久内存(C# 源块只需活到本事件返回)----
        memcpy(s_WorldToView, data->worldToViewMatrix, sizeof(float) * 16);
        memcpy(s_ViewToClip,  data->viewToClipMatrix,  sizeof(float) * 16);

        NVSDK_NGX_D3D12_DLSSD_Eval_Params eval = {};
        eval.pInColor           = static_cast<ID3D12Resource*>(data->color);
        eval.pInOutput          = static_cast<ID3D12Resource*>(data->output);
        eval.pInDepth           = static_cast<ID3D12Resource*>(data->depth);
        eval.pInMotionVectors   = static_cast<ID3D12Resource*>(data->motionVectors);
        eval.InJitterOffsetX    = data->jitterX;
        eval.InJitterOffsetY    = data->jitterY;
        eval.InRenderSubrectDimensions.Width  = data->subrectWidth;
        eval.InRenderSubrectDimensions.Height = data->subrectHeight;
        eval.InReset             = data->reset ? 1 : 0;
        eval.InMVScaleX          = data->mvScaleX; // helper 内 0 → 默认 1.0
        eval.InMVScaleY          = data->mvScaleY;
        eval.pInDiffuseAlbedo    = static_cast<ID3D12Resource*>(data->diffuseAlbedo);
        eval.pInSpecularAlbedo   = static_cast<ID3D12Resource*>(data->specularAlbedo);
        eval.pInNormals          = static_cast<ID3D12Resource*>(data->normals);
        eval.pInWorldToViewMatrix = s_WorldToView;
        eval.pInViewToClipMatrix  = s_ViewToClip;
        eval.InPreExposure          = data->preExposure;          // 0 → helper 默认 1.0
        eval.InFrameTimeDeltaInMsec = data->frameTimeDeltaInMsec;

        r = NGX_D3D12_EVALUATE_DLSSD_EXT(cmd, s_Feature, s_Params, &eval);

        // NGX DX12 资源状态契约:输入/输出资源以 UAV 状态进出
        states[0] = { static_cast<ID3D12Resource*>(data->color),          D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[1] = { static_cast<ID3D12Resource*>(data->depth),          D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[2] = { static_cast<ID3D12Resource*>(data->motionVectors),  D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[3] = { static_cast<ID3D12Resource*>(data->output),         D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[4] = { static_cast<ID3D12Resource*>(data->diffuseAlbedo),  D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[5] = { static_cast<ID3D12Resource*>(data->specularAlbedo), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[6] = { static_cast<ID3D12Resource*>(data->normals),        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        stateCount = 7;
    }
    else
    {
        // ---- DLSS-SR ----
        NVSDK_NGX_D3D12_DLSS_Eval_Params eval = {};
        eval.Feature.pInColor   = static_cast<ID3D12Resource*>(data->color);
        eval.Feature.pInOutput  = static_cast<ID3D12Resource*>(data->output);
        eval.Feature.InSharpness = 0.0f; // DLSS 3.7+ 已弃用锐化,保持 0
        eval.pInDepth           = static_cast<ID3D12Resource*>(data->depth);
        eval.pInMotionVectors   = static_cast<ID3D12Resource*>(data->motionVectors);
        eval.InJitterOffsetX    = data->jitterX;
        eval.InJitterOffsetY    = data->jitterY;
        eval.InRenderSubrectDimensions.Width  = data->subrectWidth;
        eval.InRenderSubrectDimensions.Height = data->subrectHeight;
        eval.InReset             = data->reset ? 1 : 0;
        eval.InMVScaleX          = data->mvScaleX; // helper 内 0 → 默认 1.0
        eval.InMVScaleY          = data->mvScaleY;

        r = NGX_D3D12_EVALUATE_DLSS_EXT(cmd, s_Feature, s_Params, &eval);

        states[0] = { static_cast<ID3D12Resource*>(data->color),         D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[1] = { static_cast<ID3D12Resource*>(data->depth),         D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[2] = { static_cast<ID3D12Resource*>(data->motionVectors), D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
        states[3] = { static_cast<ID3D12Resource*>(data->output),        D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS };
    }

    const bool submitted = EndAndExecute(slot, cmd, states, stateCount);

    // 连续失败降级:C# 侧 DLSS_GetState 会看到 FEATURE_FAILED 并回退时域累积
    if (NVSDK_NGX_FAILED(r) || !submitted)
    {
        const int fails = s_ConsecutiveFails.fetch_add(1) + 1;
        if (fails >= kMaxConsecutiveFails)
        {
            SetMessage("Evaluate failed %d times consecutively (last NGX result: 0x%08x), degrading",
                       fails, r);
            s_State = DLSS_STATE_FEATURE_FAILED;
        }
    }
    else
    {
        s_ConsecutiveFails = 0;
    }
}

static void HandleReleaseFeature()
{
    if (s_Feature)
    {
        NVSDK_NGX_D3D12_ReleaseFeature(s_Feature);
        s_Feature = nullptr;
        SetMessage("DLSS feature released");
    }
    s_ConsecutiveFails = 0;
    // FEATURE_FAILED 也回退到 INIT_SUCCEEDED:C# 侧 RR→SR 降级重试依赖此语义
    if (s_State == DLSS_STATE_FEATURE_READY || s_State == DLSS_STATE_FEATURE_FAILED)
        s_State = DLSS_STATE_INIT_SUCCEEDED;
}

static void HandleShutdown()
{
    const int prev = s_State.exchange(DLSS_STATE_NOT_INITIALIZED);
    if (prev == DLSS_STATE_NOT_INITIALIZED) return;

    if (s_Feature)
    {
        NVSDK_NGX_D3D12_ReleaseFeature(s_Feature);
        s_Feature = nullptr;
    }
    if (s_CapabilityParams)
    {
        NVSDK_NGX_D3D12_DestroyParameters(s_CapabilityParams);
        s_CapabilityParams = nullptr;
    }
    if (s_Params)
    {
        NVSDK_NGX_D3D12_DestroyParameters(s_Params);
        s_Params = nullptr;
    }
    if (s_Device)
    {
        NVSDK_NGX_D3D12_Shutdown1(s_Device);
    }
    DestroyCmdInfra();
    SetMessage("NGX shutdown");
}

// ============================================================================
//  渲染事件分发
// ============================================================================

static void UNITY_INTERFACE_API DLSSRenderEvent(int eventId, void* data)
{
    switch (eventId)
    {
    case DLSS_EVENT_INIT:            HandleInit(reinterpret_cast<DLSSInitData*>(data));       break;
    case DLSS_EVENT_CREATE_FEATURE:  HandleCreateFeature(reinterpret_cast<DLSSCreateData*>(data)); break;
    case DLSS_EVENT_EVALUATE:        HandleEvaluate(reinterpret_cast<DLSSEvalData*>(data));   break;
    case DLSS_EVENT_RELEASE_FEATURE: HandleReleaseFeature();                                   break;
    case DLSS_EVENT_SHUTDOWN:        HandleShutdown();                                         break;
    default: break;
    }
}

// ============================================================================
//  设备事件
// ============================================================================

static void UNITY_INTERFACE_API OnDeviceEvent(UnityGfxDeviceEventType type)
{
    switch (type)
    {
    case kUnityGfxDeviceEventInitialize:
    case kUnityGfxDeviceEventAfterReset:
        if (s_Graphics && s_Graphics->GetRenderer() == kUnityGfxRendererD3D12 && s_D3D12)
        {
            s_Device     = s_D3D12->GetDevice();
            s_FrameFence = s_D3D12->GetFrameFence();
        }
        break;
    case kUnityGfxDeviceEventBeforeReset:
    case kUnityGfxDeviceEventShutdown:
        // 设备即将失效;NGX 清理由 C# 显式调度的 SHUTDOWN 事件负责,
        // 这里只销毁插件自有命令基础设施(持有设备资源的对象)。
        DestroyCmdInfra();
        break;
    }
}

// ============================================================================
//  Unity 插件入口与导出接口
// ============================================================================

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    s_Graphics = unityInterfaces->Get<IUnityGraphics>();
    s_D3D12    = unityInterfaces->Get<IUnityGraphicsD3D12v8>();

    if (s_Graphics)
    {
        s_Graphics->RegisterDeviceEventCallback(OnDeviceEvent);
        // 插件加载晚于设备创建时,手动触发一次以捕获当前设备
        OnDeviceEvent(kUnityGfxDeviceEventInitialize);
    }
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    HandleShutdown();
    if (s_Graphics) s_Graphics->UnregisterDeviceEventCallback(OnDeviceEvent);
    s_Graphics = nullptr;
    s_D3D12    = nullptr;
    s_Device   = nullptr;
    s_FrameFence = nullptr;
}

DLSS_API void* DLSS_GetRenderEventFunc(void)
{
    return reinterpret_cast<void*>(&DLSSRenderEvent);
}

DLSS_API int DLSS_GetState(void)
{
    return s_State.load(std::memory_order_acquire);
}

DLSS_API int DLSS_GetOptimalSettings(int outputWidth, int outputHeight, int perfQuality,
                                     int* outOptimalWidth, int* outOptimalHeight)
{
    if (s_State != DLSS_STATE_INIT_SUCCEEDED && s_State != DLSS_STATE_FEATURE_READY)
        return -1;
    if (!s_CapabilityParams || !outOptimalWidth || !outOptimalHeight)
        return -2;

    unsigned int optW = 0, optH = 0, maxW = 0, maxH = 0, minW = 0, minH = 0;
    float sharpness = 0.0f;
    NVSDK_NGX_Result r = NGX_DLSS_GET_OPTIMAL_SETTINGS(
        s_CapabilityParams,
        static_cast<unsigned int>(outputWidth), static_cast<unsigned int>(outputHeight),
        static_cast<NVSDK_NGX_PerfQuality_Value>(perfQuality),
        &optW, &optH, &maxW, &maxH, &minW, &minH, &sharpness);
    if (NVSDK_NGX_FAILED(r))
    {
        SetMessage("GetOptimalSettings failed: 0x%08x", r);
        return -3;
    }

    *outOptimalWidth  = static_cast<int>(optW);
    *outOptimalHeight = static_cast<int>(optH);
    return 0;
}

DLSS_API const char* DLSS_GetLastMessage(void)
{
    AcquireSRWLockShared(&s_MsgLock);
    // 单线程编辑器场景下直接返回;严格并发时可复制到调用方缓冲
    const char* msg = s_Msg;
    ReleaseSRWLockShared(&s_MsgLock);
    return msg;
}

DLSS_API int DLSS_RRAvailable(void)
{
    if (s_State != DLSS_STATE_INIT_SUCCEEDED && s_State != DLSS_STATE_FEATURE_READY)
        return -1;
    if (!s_CapabilityParams) { SetMessage("RRAvailable: no capability params"); return 0; }
    int v = 0;
    NVSDK_NGX_Result r = NVSDK_NGX_Parameter_GetI(s_CapabilityParams,
            NVSDK_NGX_Parameter_SuperSamplingDenoising_Available, &v);
    if (NVSDK_NGX_FAILED(r)) { SetMessage("RRAvailable: GetI failed 0x%08x", r); return 0; }
    SetMessage("RRAvailable: %d (requires nvngx_dlssd.dll in search path)", v);
    return v ? 1 : 0;
}
