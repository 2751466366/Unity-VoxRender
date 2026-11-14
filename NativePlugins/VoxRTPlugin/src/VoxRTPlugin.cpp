// ============================================================================
//  VoxRTPlugin.cpp — Unity 原生插件:DXR 体素路径追踪(D3D12 后端)
//
//  职责:
//    1. UnityPluginLoad 获取 IUnityGraphicsD3D12v8(ID3D12Device/队列/ExecuteCommandList)
//    2. Init: 探测 RaytracingTier、建根签名/描述符堆、加载 voxel_pathtracing.dxil、建 RTPSO + SBT
//    3. BuildScene: 把扁平 VoxelElement[] 砖化为 8³ 砖掩码 → 每 chunk 一个 BLAS + TLAS
//    4. UpdateVoxels: 只改占据掩码 / per-cell 材质，**不重建 BLAS**（编辑零重建）
//    5. Render: 写帧常量 → DispatchRays（raygen 驱动整个 bounce 循环）
//
//  执行模型:
//    所有 GPU 操作由 C# 通过 IssuePluginEventAndData 在 Unity 渲染线程调度，
//    插件自建 command list 录制，再交给 IUnityGraphicsD3D12v8::ExecuteCommandList
//    提交，保证与相邻 compute dispatch / DLSS evaluate 严格保序。
//    描述符堆容量固定 64，命中 slot 见 SrvSlot 枚举。
//
//  失败策略:
//    任何一步失败只置状态位 + SetMessage，绝不抛出/崩溃；C# 侧轮询 RT_GetState
//    自动回退 compute 后端（SVO_PathTracing.compute 保持原样，作为 A/B 参照）。
//
//  几何约定（与 shader 严格一致，改动必须三处同步）:
//    - AABB 在 **grid 空间** 构建：端点即整数，brick 原点 = (ox,oy,oz)，尺寸 8
//    - grid = (world - rootMin) / scale；rootMin 与 SVOBuilder 的 _SVORootMin 同源
//    - 射线也变换到 grid 空间，方向保持单位长度 → TraceRay 的 t = grid 空间距离
// ============================================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <d3dcommon.h>
#include <dxgi1_6.h>

#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <unordered_map>
#include <vector>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"

#include "VoxRTPluginAPI.h"

// ============================================================================
//  全局状态
// ============================================================================

static IUnityGraphics*        s_Graphics   = nullptr;
static IUnityGraphicsD3D12v8* s_D3D12      = nullptr;
static ID3D12Device*          s_Device     = nullptr;  // 非拥有
static ID3D12Device5*         s_Device5    = nullptr;  // 拥有（AddRef）
static ID3D12Fence*           s_FrameFence = nullptr;  // Unity 帧围栏（非拥有）

static std::atomic<int> s_State{ RT_STATE_NOT_INITIALIZED };

// ---- DXR 对象 ----
static ID3D12RootSignature* s_RootSig     = nullptr;
static ID3D12StateObject*   s_StateObject = nullptr;
static ID3D12Resource*      s_SbtBuffer   = nullptr;
static void*                s_SbtRayGen   = nullptr;
static void*                s_SbtMiss     = nullptr;
static void*                s_SbtHit      = nullptr;
static const UINT           kSbtRecordSize = 64;   // D3D12_RAYTRACING_SHADER_TABLE_BYTE_ALIGNMENT
static const UINT           kSbtMissCount  = 2;    // 0 = 主射线 miss，1 = 阴影射线 miss

// ---- 描述符堆（着色器可见，容量 64）----
static ID3D12DescriptorHeap* s_DescHeap = nullptr;
static UINT                  s_DescSize = 0;
static D3D12_CPU_DESCRIPTOR_HANDLE s_CpuHandleBase{};
static D3D12_GPU_DESCRIPTOR_HANDLE s_GpuHandleBase{};

// 槽位布局必须与根签名描述符表（t0..t7 连续、u0..u5 连续）一致
enum SrvSlot
{
    SRV_BRICKS = 0,
    SRV_VOXEL_CELLS,
    SRV_MATERIALS,
    SRV_EMISSIVE_LIGHTS,
    SRV_EMISSIVE_ALIAS,
    SRV_SKY,
    SRV_CHUNK_TABLE,
    SRV_TLAS,           // 维度 = RAYTRACING_ACCELERATION_STRUCTURE
    UAV_COLOR,          // 以下 6 个为 UAV
    UAV_MV,
    UAV_DEPTH,
    UAV_DIFFUSE_ALBEDO,
    UAV_SPECULAR_ALBEDO,
    UAV_NORMAL_ROUGHNESS,
    SRV_SLOT_COUNT
};

static const UINT kSrvTableCount = 8;  // t0..t7
static const UINT kUavTableCount = 6;  // u0..u5

// ---- 帧常量（4 槽环形上传缓冲，与 CmdSlot 一一对应）----
struct RTFrameConstants
{
    float invVP[16];
    float curVP[16];
    float prevVP[16];
    float cameraPos[4];
    float sunDir[4];      // w = hasSun
    float sunColor[4];
    float skyColor[4];    // w = hasSkyCube
    float toGrid[4];      // xyz = rootMin, w = 1/scale
    float params0[4];     // jitterX, jitterY, width, height
    float params1[4];     // maxDepth, frameIndex, reset, writeGuides
    float params2[4];     // voxelScale, chunkCount, emissiveCount, emissivePower
    float flags[4];       // x = useRR
};
static const UINT kCbBytesPerSlot = 512;   // > sizeof(RTFrameConstants) 且 256 对齐
static const UINT kCbSlotCount    = 4;
static ID3D12Resource* s_CbBuffer = nullptr;
static uint8_t*        s_CbMapped = nullptr;

// ---- 场景资源（全部由插件拥有）----
static ID3D12Resource* s_BrickBuffer   = nullptr;  // BrickRecord[96B]
static ID3D12Resource* s_CellBuffer    = nullptr;  // uint（每砖 512 项，仅非均匀砖占用）
static ID3D12Resource* s_MaterialBuf   = nullptr;  // float4（48B/材质）
static ID3D12Resource* s_EmissiveBuf   = nullptr;  // float4
static ID3D12Resource* s_AliasBuf      = nullptr;  // float2
static ID3D12Resource* s_ChunkTableBuf = nullptr;  // uint2
static ID3D12Resource* s_SkyFallback   = nullptr;  // 1×1×6 兜底 cubemap
static ID3D12Resource* s_Tlas           = nullptr;

// 每 chunk 的 BLAS
struct ChunkGpu
{
    ID3D12Resource* blas    = nullptr;
    ID3D12Resource* scratch = nullptr;
    UINT            brickBase = 0;
    UINT            brickCount = 0;
};
static std::vector<ChunkGpu> s_Chunks;

// 构建期临时缓冲（AABB 输入 / 实例描述），BUILD_SCENE 结束后释放
static ID3D12Resource* s_AabbInput    = nullptr;
static ID3D12Resource* s_InstanceBuf  = nullptr;
static ID3D12Resource* s_TlasScratch  = nullptr;

// ---- CPU 侧镜像（编辑通路与统计用）----
struct BrickCpu
{
    uint32_t occ[VOXRT_BRICK_OCC_U32];
    uint32_t cellBase;
    uint32_t meta;
    uint32_t flags;
    int32_t  ox, oy, oz;
};
static_assert(sizeof(BrickCpu) == 88, "BrickCpu 必须与 HLSL BrickRecord 前 88 字节一致");
static std::vector<BrickCpu>                 s_BrickCpu;
static std::unordered_map<uint64_t, uint32_t> s_BrickIndex;   // brickKey → 全局砖索引
static int32_t s_RootMin[3] = { 0, 0, 0 };
static float   s_VoxelScale = 1.0f;
static double  s_EmissivePower = 0.0;
static int     s_EmissiveCount = 0;
static uint32_t s_CellCapacity = 0;

// ---- 统计 ----
static RTStats s_Stats{};

// ---- 诊断信息（SRWLOCK 保护）----
static SRWLOCK s_MsgLock = SRWLOCK_INIT;
static char    s_Msg[1024] = {};

// ============================================================================
//  诊断配置：<DLL 目录>\voxrt_diag.cfg
//      level=<0..15>    0 = 正常渲染（写入 g_Flags.y 供 shader 读取）
//      dim=<整数>       0 = 不限制调度尺寸
//
//  level 档位（shader 侧实现见 voxel_pathtracing.hlsl 的 VoxelPathTrace 开头）：
//     1 = raygen 不发 TraceRay，只写恒定色 → 验证 DispatchRays 通路本身
//     2 = 每条像素只发 1 条主射线（跳过 NEE）→ 验证 BVH 遍历 + IS/CH
//     3 = 不发 DispatchRays（C++ 侧判定），只保留状态声明与 barrier
//     4 = raygen 读完 cbuffer 立即 return（不写任何 UAV）
//     5 = 只写颜色 UAV（u0）；6 = 只写引导面 u1/u2
//     10+n = 写颜色 + 前 n 张引导面（逐张定位挂死的 UAV）
//
//  ⚠ 故意不用环境变量：经 Start-Process/沙箱链路传递时曾静默丢失（设 13 实际跑成 0），
//    而“档位到底生效没有”是排查挂死时唯一可信的判据，不能有歧义。
//    每帧重读，代价只是一次小文件读，换来不重编、不重启就能换档位。
// ============================================================================
static bool DiagCfgPath(wchar_t* out, size_t cch)
{
    HMODULE hSelf = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&DiagCfgPath), &hSelf) || !hSelf)
        return false;

    wchar_t dllPath[MAX_PATH] = {};
    const DWORD n = GetModuleFileNameW(hSelf, dllPath, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return false;

    wchar_t* slash = wcsrchr(dllPath, L'\\');
    if (slash) *(slash + 1) = L'\0';
    return swprintf_s(out, cch, L"%svoxrt_diag.cfg", dllPath) > 0;
}

static int  s_CfgLevel  = 0;
static UINT s_CfgMaxDim = 0;

static void ReadDiagCfg()
{
    int  level  = 0;
    UINT maxDim = 0;

    wchar_t path[MAX_PATH] = {};
    FILE* f = nullptr;
    if (DiagCfgPath(path, MAX_PATH) && _wfopen_s(&f, path, L"r") == 0 && f)
    {
        char line[128];
        while (fgets(line, sizeof(line), f))
        {
            int v = 0;
            if (sscanf_s(line, "level=%d", &v) == 1)
                level = (v >= 0 && v <= 15) ? v : 0;
            else if (sscanf_s(line, "dim=%d", &v) == 1)
                maxDim = (v > 0) ? (UINT)v : 0u;
        }
        fclose(f);
    }

    s_CfgLevel  = level;
    s_CfgMaxDim = maxDim;
}

static int DiagLevel()
{
    ReadDiagCfg();
    return s_CfgLevel;
}

// ============================================================================
//  DispatchRays 尺寸上限（voxrt_diag.cfg 的 dim=）
//
//  与 level 正交的第二个旋钮：level 控制 shader 行为，这个控制**工作量**。
//  两者组合才能分离“写入内容非法”与“规模/超时”。
// ============================================================================
static UINT DiagMaxDim()
{
    ReadDiagCfg();
    return (s_CfgMaxDim > 0) ? s_CfgMaxDim : 0xFFFFFFFFu;
}

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
    OutputDebugStringA("[VoxRTPlugin] ");
    OutputDebugStringA(s_Msg);
    OutputDebugStringA("\n");
}

// ============================================================================
//  命令录制基础设施（与 DLSSPlugin 同构：4 槽环形 allocator/commandList）
// ============================================================================

struct CmdSlot
{
    ID3D12CommandAllocator*    allocator  = nullptr;
    ID3D12GraphicsCommandList* list       = nullptr;
    UINT64                     fenceValue = 0;
};

static CmdSlot s_Slots[4] = {};
static int     s_NextSlot = 0;
static bool    s_CmdInfraReady = false;

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
        s.list->Close();
    }
    s_CmdInfraReady = true;
    return true;
}

static ID3D12GraphicsCommandList* BeginCmdList(int& outSlotIndex)
{
    outSlotIndex = -1;
    if (!s_CmdInfraReady) { SetMessage("Cmd infra not ready"); return nullptr; }

    const int idx = s_NextSlot;
    s_NextSlot = (s_NextSlot + 1) % 4;
    CmdSlot& s = s_Slots[idx];

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
//  资源创建辅助
// ============================================================================

static ID3D12Resource* CreateBufferResource(UINT64 bytes, D3D12_HEAP_TYPE heap,
                                            D3D12_RESOURCE_STATES initialState,
                                            D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAG_NONE,
                                            const wchar_t* name = nullptr)
{
    bytes = (std::max)(bytes, (UINT64)256);
    // 缓冲尺寸必须 4 字节对齐即可，但 256 对齐可容纳 CBV/UAV 的常见约束
    bytes = (bytes + 255) & ~(UINT64)255;

    D3D12_RESOURCE_DESC desc = {};
    desc.Dimension        = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width            = bytes;
    desc.Height           = 1;
    desc.DepthOrArraySize = 1;
    desc.MipLevels        = 1;
    desc.Format           = DXGI_FORMAT_UNKNOWN;
    desc.SampleDesc       = { 1, 0 };
    desc.Layout           = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    desc.Flags            = flags;

    D3D12_HEAP_PROPERTIES props = {};
    props.Type = heap;

    ID3D12Resource* res = nullptr;
    HRESULT hr = s_Device->CreateCommittedResource(&props, D3D12_HEAP_FLAG_NONE, &desc,
                                                   initialState, nullptr, IID_PPV_ARGS(&res));
    if (FAILED(hr)) { SetMessage("CreateCommittedResource(%llu B) failed: 0x%08x", bytes, hr); return nullptr; }

    if (name) res->SetName(name);
    return res;
}

static D3D12_CPU_DESCRIPTOR_HANDLE CpuHandle(UINT slot)
{
    D3D12_CPU_DESCRIPTOR_HANDLE h = s_CpuHandleBase;
    h.ptr += (SIZE_T)slot * s_DescSize;
    return h;
}

static void CreateStructuredSrv(UINT slot, ID3D12Resource* res, UINT stride, UINT count)
{
    if (!res || count == 0) return;
    D3D12_SHADER_RESOURCE_VIEW_DESC desc = {};
    desc.Format = DXGI_FORMAT_UNKNOWN;
    desc.ViewDimension = D3D12_SRV_DIMENSION_BUFFER;
    desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
    desc.Buffer.FirstElement = 0;
    desc.Buffer.NumElements  = count;
    desc.Buffer.StructureByteStride = stride;
    s_Device->CreateShaderResourceView(res, &desc, CpuHandle(slot));
}

static void CreateSrvSlot(UINT slot, ID3D12Resource* res, const D3D12_SHADER_RESOURCE_VIEW_DESC& desc)
{
    if (!res) return;
    s_Device->CreateShaderResourceView(res, &desc, CpuHandle(slot));
}

static void CreateUavSlot(UINT slot, ID3D12Resource* res)
{
    if (!res) return;
    D3D12_RESOURCE_DESC rdesc = res->GetDesc();
    D3D12_UNORDERED_ACCESS_VIEW_DESC desc = {};
    desc.Format = rdesc.Format;
    if (rdesc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D)
    {
        desc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        desc.Texture2D.MipSlice = 0;
        desc.Texture2D.PlaneSlice = 0;
    }
    else
    {
        desc.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
        desc.Buffer.NumElements = (UINT)(rdesc.Width / 4);
        desc.Buffer.StructureByteStride = 0;
        desc.Format = DXGI_FORMAT_UNKNOWN;
    }
    s_Device->CreateUnorderedAccessView(res, nullptr, &desc, CpuHandle(slot));
}

// ============================================================================
//  根签名 / RTPSO / SBT
// ============================================================================

static bool CreateRootSignature()
{
    D3D12_ROOT_PARAMETER params[3] = {};

    // b0: 帧常量（root CBV，GPU VA 直接提交，无需 CBV 堆）
    params[0].ParameterType             = D3D12_ROOT_PARAMETER_TYPE_CBV;
    params[0].Descriptor.ShaderRegister = 0;
    params[0].Descriptor.RegisterSpace  = 0;
    params[0].ShaderVisibility          = D3D12_SHADER_VISIBILITY_ALL;

    // t0..t7: 结构化缓冲 + TLAS（同一描述符表，逐个 descriptor 自带维度）
    D3D12_DESCRIPTOR_RANGE srvRange = {};
    srvRange.RangeType                         = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
    srvRange.NumDescriptors                    = kSrvTableCount;
    srvRange.BaseShaderRegister                = 0;
    srvRange.RegisterSpace                     = 0;
    srvRange.OffsetInDescriptorsFromTableStart = 0;

    params[1].ParameterType                       = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[1].DescriptorTable.NumDescriptorRanges = 1;
    params[1].DescriptorTable.pDescriptorRanges   = &srvRange;
    params[1].ShaderVisibility                    = D3D12_SHADER_VISIBILITY_ALL;

    // u0..u5: 输出颜色 / MV / 深度 / RR 引导面
    D3D12_DESCRIPTOR_RANGE uavRange = {};
    uavRange.RangeType                         = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
    uavRange.NumDescriptors                    = kUavTableCount;
    uavRange.BaseShaderRegister                = 0;
    uavRange.RegisterSpace                     = 0;
    uavRange.OffsetInDescriptorsFromTableStart = 0;

    params[2].ParameterType                       = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
    params[2].DescriptorTable.NumDescriptorRanges = 1;
    params[2].DescriptorTable.pDescriptorRanges   = &uavRange;
    params[2].ShaderVisibility                    = D3D12_SHADER_VISIBILITY_ALL;

    // s0: 静态采样器（天空 cubemap 用，避免额外的采样器描述符）
    D3D12_STATIC_SAMPLER_DESC sampler = {};
    sampler.Filter           = D3D12_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.AddressU         = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    sampler.AddressV         = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    sampler.AddressW         = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
    sampler.MipLODBias       = 0.0f;
    sampler.MaxAnisotropy    = 1;
    sampler.ComparisonFunc   = D3D12_COMPARISON_FUNC_NEVER;
    sampler.BorderColor      = D3D12_STATIC_BORDER_COLOR_TRANSPARENT_BLACK;
    sampler.MinLOD           = 0.0f;
    sampler.MaxLOD           = D3D12_FLOAT32_MAX;
    sampler.ShaderRegister   = 0;
    sampler.RegisterSpace    = 0;
    sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

    D3D12_ROOT_SIGNATURE_DESC desc = {};
    desc.NumParameters     = 3;
    desc.pParameters       = params;
    desc.NumStaticSamplers = 1;
    desc.pStaticSamplers   = &sampler;
    desc.Flags             = D3D12_ROOT_SIGNATURE_FLAG_NONE;

    ID3DBlob* sigBlob = nullptr;
    ID3DBlob* errBlob = nullptr;
    HRESULT hr = D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1_0, &sigBlob, &errBlob);
    if (FAILED(hr))
    {
        SetMessage("SerializeRootSignature failed: 0x%08x %s", hr,
                   errBlob ? (const char*)errBlob->GetBufferPointer() : "");
        if (errBlob) errBlob->Release();
        return false;
    }

    hr = s_Device->CreateRootSignature(0, sigBlob->GetBufferPointer(), sigBlob->GetBufferSize(),
                                       IID_PPV_ARGS(&s_RootSig));
    sigBlob->Release();
    if (errBlob) errBlob->Release();
    if (FAILED(hr)) { SetMessage("CreateRootSignature failed: 0x%08x", hr); return false; }
    return true;
}

// 从文件加载 DXIL（返回拥有引用计数的 ID3DBlob 兼容对象）
class FileBlob final : public ID3DBlob
{
public:
    FileBlob(void* data, SIZE_T size) : m_Ref(1), m_Data(data), m_Size(size) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(ID3DBlob))
        {
            *ppv = this;
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)InterlockedIncrement(&m_Ref); }
    ULONG STDMETHODCALLTYPE Release() override
    {
        ULONG r = (ULONG)InterlockedDecrement(&m_Ref);
        if (r == 0) { free(m_Data); delete this; }
        return r;
    }
    LPVOID STDMETHODCALLTYPE GetBufferPointer() override { return m_Data; }
    SIZE_T STDMETHODCALLTYPE GetBufferSize() override { return m_Size; }

private:
    LONG  m_Ref;
    void* m_Data;
    SIZE_T m_Size;
};

static ID3DBlob* LoadDxilFile(const wchar_t* explicitPath)
{
    const DWORD kMaxPath = 1024;
    wchar_t path[kMaxPath] = {};
    if (explicitPath && explicitPath[0])
    {
        wcsncpy_s(path, explicitPath, _TRUNCATE);
    }
    else
    {
        // GetModuleFileNameW 的第一个参数必须是**模块基址**（GetModuleHandle* 的返回值）
        // 或 NULL（当前进程 exe）。传函数地址（即便是本 DLL 内的）会返回 ERROR_MOD_NOT_FOUND。
        // 因此先用 GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS 由函数地址反查本 DLL 的 HMODULE。
        HMODULE hSelf = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                reinterpret_cast<LPCWSTR>(&LoadDxilFile), &hSelf) || !hSelf)
        {
            SetMessage("GetModuleHandleEx 失败，无法定位插件自身模块 (err=%lu)", GetLastError());
            return nullptr;
        }

        wchar_t dllPath[kMaxPath] = {};
        DWORD n = GetModuleFileNameW(hSelf, dllPath, kMaxPath);
        if (n == 0 || n >= kMaxPath)
        {
            SetMessage("GetModuleFileName 失败 (err=%lu)", GetLastError());
            return nullptr;
        }

        wchar_t* slash = wcsrchr(dllPath, L'\\');
        if (slash) *(slash + 1) = L'\0';
        wcsncpy_s(path, dllPath, _TRUNCATE);
        wcsncat_s(path, L"voxel_pathtracing.dxil", _TRUNCATE);
        // 插件放在 Assets/Plugins/x86_64/，DXIL 与 DLL 同目录
    }

    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        SetMessage("找不到 DXIL: %ls（请先运行 build_shaders.ps1）", path);
        return nullptr;
    }

    LARGE_INTEGER size = {};
    GetFileSizeEx(file, &size);
    if (size.QuadPart <= 0 || size.QuadPart > (LONGLONG)(64 << 20))
    {
        CloseHandle(file);
        SetMessage("DXIL 尺寸非法: %lld (%ls)", size.QuadPart, path);
        return nullptr;
    }

    void* data = malloc((size_t)size.QuadPart);
    if (!data) { CloseHandle(file); SetMessage("DXIL 读入内存失败 (OOM)"); return nullptr; }

    DWORD read = 0;
    BOOL ok = ReadFile(file, data, (DWORD)size.QuadPart, &read, nullptr);
    CloseHandle(file);
    if (!ok || read != (DWORD)size.QuadPart)
    {
        free(data);
        SetMessage("DXIL 读取失败: %ls", path);
        return nullptr;
    }

    SetMessage("DXIL 已加载: %ls (%lld 字节)", path, size.QuadPart);
    return new FileBlob(data, (SIZE_T)size.QuadPart);
}

static bool CreateRaytracingPipeline(const wchar_t* dxilPath)
{
    ID3DBlob* dxil = LoadDxilFile(dxilPath);
    if (!dxil) { s_State = RT_STATE_INIT_FAILED; return false; }

    D3D12_DXIL_LIBRARY_DESC dxilLib = {};
    // 导出全部 shader（hit group 按名字引用）；blob 在本函数返回前一直有效
    dxilLib.DXILLibrary.pShaderBytecode = dxil->GetBufferPointer();
    dxilLib.DXILLibrary.BytecodeLength  = dxil->GetBufferSize();

    D3D12_HIT_GROUP_DESC hitGroup = {};
    hitGroup.HitGroupExport             = L"VoxelHitGroup";
    hitGroup.Type                       = D3D12_HIT_GROUP_TYPE_PROCEDURAL_PRIMITIVE;
    hitGroup.IntersectionShaderImport   = L"VoxelIntersection";
    hitGroup.ClosestHitShaderImport     = L"VoxelClosestHit";
    hitGroup.AnyHitShaderImport         = nullptr;

    D3D12_RAYTRACING_SHADER_CONFIG shaderConfig = {};
    shaderConfig.MaxPayloadSizeInBytes   = sizeof(float) * 4;  // VoxelPayload = 16B
    shaderConfig.MaxAttributeSizeInBytes = sizeof(uint32_t) * 2; // VoxelAttributes = 8B

    // raygen 驱动 bounce 循环，TraceRay 只从 raygen 发出 → 递归深度 1 足够
    D3D12_RAYTRACING_PIPELINE_CONFIG pipelineConfig = {};
    pipelineConfig.MaxTraceRecursionDepth = 1;

    D3D12_GLOBAL_ROOT_SIGNATURE globalRootSig = {};
    globalRootSig.pGlobalRootSignature = s_RootSig;

    D3D12_STATE_SUBOBJECT subobjects[5] = {};
    UINT n = 0;
    subobjects[n].Type = D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY;            subobjects[n++].pDesc = &dxilLib;
    subobjects[n].Type = D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP;               subobjects[n++].pDesc = &hitGroup;
    subobjects[n].Type = D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG; subobjects[n++].pDesc = &shaderConfig;
    subobjects[n].Type = D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG; subobjects[n++].pDesc = &pipelineConfig;
    subobjects[n].Type = D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE;   subobjects[n++].pDesc = &globalRootSig;

    D3D12_STATE_OBJECT_DESC soDesc = {};
    soDesc.Type          = D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE;
    soDesc.NumSubobjects = n;
    soDesc.pSubobjects   = subobjects;

    HRESULT hr = s_Device5->CreateStateObject(&soDesc, IID_PPV_ARGS(&s_StateObject));
    dxil->Release();
    if (FAILED(hr))
    {
        SetMessage("CreateStateObject 失败: 0x%08x（常见原因: DXIL 未签名 / dxc 版本过旧 / "
                   "MaxAttributeSizeInBytes 与 shader 不符）", hr);
        s_State = RT_STATE_INIT_FAILED;
        return false;
    }
    return true;
}

static bool CreateShaderBindingTable()
{
    ID3D12StateObjectProperties* props = nullptr;
    HRESULT hr = s_StateObject->QueryInterface(IID_PPV_ARGS(&props));
    if (FAILED(hr)) { SetMessage("StateObjectProperties 获取失败: 0x%08x", hr); return false; }

    s_SbtRayGen = props->GetShaderIdentifier(L"VoxelPathTrace");
    s_SbtMiss   = props->GetShaderIdentifier(L"VoxelMiss");
    void* shadowMiss = props->GetShaderIdentifier(L"VoxelMissShadow");
    s_SbtHit    = props->GetShaderIdentifier(L"VoxelHitGroup");
    props->Release();

    if (!s_SbtRayGen || !s_SbtMiss || !shadowMiss || !s_SbtHit)
    {
        SetMessage("GetShaderIdentifier 失败（导出名不匹配？raygen=%p miss=%p shadow=%p hit=%p）",
                   s_SbtRayGen, s_SbtMiss, shadowMiss, s_SbtHit);
        return false;
    }

    // 布局: [raygen][miss0][miss1][hit]，每条 64B
    const UINT total = (UINT)(1 + kSbtMissCount + 1) * kSbtRecordSize;
    s_SbtBuffer = CreateBufferResource(total, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_STATE_GENERIC_READ,
                                       D3D12_RESOURCE_FLAG_NONE, L"VoxRT_SBT");
    if (!s_SbtBuffer) return false;

    uint8_t* mapped = nullptr;
    if (FAILED(s_SbtBuffer->Map(0, nullptr, (void**)&mapped)) || !mapped)
    {
        SetMessage("SBT Map 失败");
        return false;
    }
    memset(mapped, 0, total);
    memcpy(mapped + 0 * kSbtRecordSize, s_SbtRayGen,   D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
    memcpy(mapped + 1 * kSbtRecordSize, s_SbtMiss,     D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
    memcpy(mapped + 2 * kSbtRecordSize, shadowMiss,    D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
    memcpy(mapped + 3 * kSbtRecordSize, s_SbtHit,      D3D12_SHADER_IDENTIFIER_SIZE_IN_BYTES);
    s_SbtBuffer->Unmap(0, nullptr);
    return true;
}

// ============================================================================
//  第二部分：砖化 / 加速结构构建 / 编辑通路 / 渲染
// ============================================================================

// 每个命令槽位的待释放 staging（槽位复用时释放，此时其围栏已到达）
static std::vector<ID3D12Resource*> s_SlotStaging[4];

// ID3D12GraphicsCommandList4 缓存（DispatchRays / BuildRaytracingAccelerationStructure）
static ID3D12GraphicsCommandList4* s_List4[4] = {};

static ID3D12GraphicsCommandList4* GetList4(int slot, ID3D12GraphicsCommandList* cmd)
{
    if (slot < 0 || slot >= 4) return nullptr;
    if (!s_List4[slot])
    {
        ID3D12GraphicsCommandList4* l4 = nullptr;
        if (FAILED(cmd->QueryInterface(IID_PPV_ARGS(&l4))) || !l4)
        {
            SetMessage("QueryInterface(ID3D12GraphicsCommandList4) 失败（需 Windows 10 1809+）");
            return nullptr;
        }
        s_List4[slot] = l4;
    }
    return s_List4[slot];
}

static void ReleaseList4Cache()
{
    for (ID3D12GraphicsCommandList4*& l : s_List4)
    {
        if (l) { l->Release(); l = nullptr; }
    }
}

// ---- 坐标哈希（每轴 21 位，与 SVOMortonBuilder 的 21 位坐标上限一致）----
static const uint64_t kAxisMask = 0x1FFFFFull;

static inline uint64_t PackKey3(int32_t x, int32_t y, int32_t z)
{
    return ((uint64_t)(uint32_t)x & kAxisMask)
         | (((uint64_t)(uint32_t)y & kAxisMask) << 21)
         | (((uint64_t)(uint32_t)z & kAxisMask) << 42);
}

static inline int32_t KeyAxisX(uint64_t k) { return (int32_t)(k & kAxisMask); }
static inline int32_t KeyAxisY(uint64_t k) { return (int32_t)((k >> 21) & kAxisMask); }
static inline int32_t KeyAxisZ(uint64_t k) { return (int32_t)((k >> 42) & kAxisMask); }

// brick key → chunk key（砖坐标 >> 3）。逐轴解包再打包，避免移位串位
static inline uint64_t BrickKeyToChunkKey(uint64_t k)
{
    return PackKey3(KeyAxisX(k) >> 3, KeyAxisY(k) >> 3, KeyAxisZ(k) >> 3);
}

static inline uint32_t PopCount32(uint32_t v)
{
    v = v - ((v >> 1) & 0x55555555u);
    v = (v & 0x33333333u) + ((v >> 2) & 0x33333333u);
    return (((v + (v >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24;
}

// 与 SVOTypes.cs 的 VoxelElement 逐字段镜像（16B）
struct VoxelElementCpu
{
    int32_t x, y, z;
    int32_t matIndex;   // < 0 = 墓碑
};
static_assert(sizeof(VoxelElementCpu) == 16, "VoxelElementCpu 必须是 16 字节");

// 与 RTVoxelDelta.edits 的 int4 镜像（16B）：场景空间整数体素坐标 + 材质
struct VoxelEditCpu
{
    int32_t x, y, z;
    int32_t matIndex;   // < 0 = 删除
};
static_assert(sizeof(VoxelEditCpu) == 16, "VoxelEditCpu 必须是 16 字节");

// ---- 砖化中间态（BUILD_SCENE 期间有效，结束后释放）----
struct BrickBuild
{
    uint32_t occ[VOXRT_BRICK_OCC_U32];
    uint32_t firstMat = 0;   // 首个写入的材质
    uint32_t mixed    = 0;   // 1 = 材质不唯一，cells 有效
    std::vector<uint32_t> cells;   // 仅 mixed 时分配（512 项）
};
static std::unordered_map<uint64_t, BrickBuild> s_BuildMap;

// per-cell 材质表的编辑余量（块数）：uniform 砖在运行期被刷成多材质时惰性分配
static const uint32_t kHeadroomBricks = 2048;
static uint32_t s_HeadroomCellBase = 0;
static uint32_t s_HeadroomUsed     = 0;

// ---- 每槽描述符区（4 槽 × 14 个，避免跨帧覆写竞争）----
static const UINT kDescHeapSlots = 64;
static inline UINT SlotRel(int slot, SrvSlot rel)
{
    return (UINT)slot * (UINT)SRV_SLOT_COUNT + (UINT)rel;
}

static D3D12_GPU_DESCRIPTOR_HANDLE GpuHandle(UINT absSlot)
{
    D3D12_GPU_DESCRIPTOR_HANDLE h = s_GpuHandleBase;
    h.ptr += (UINT64)absSlot * s_DescSize;
    return h;
}

// typeless → 合法 SRV/UAV 格式（Unity 创建的 RenderTexture 常为 typeless）
static DXGI_FORMAT ViewFormatOf(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS:  return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R8G8B8A8_TYPELESS:     return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS:     return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_R32G32_TYPELESS:       return DXGI_FORMAT_R32G32_FLOAT;
    case DXGI_FORMAT_R32_TYPELESS:          return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R16_TYPELESS:          return DXGI_FORMAT_R16_FLOAT;
    case DXGI_FORMAT_R8_TYPELESS:           return DXGI_FORMAT_R8_UNORM;
    case DXGI_FORMAT_BC7_TYPELESS:          return DXGI_FORMAT_BC7_UNORM;
    default:                                return f;
    }
}

// ---- 描述符堆分区（brick/cell/mat/emissive/alias/chunk/TLAS 全槽共享）----
static void WriteSceneDescriptors(UINT brickCount, UINT cellCapacity, UINT matCount,
                                  UINT emissiveCount, UINT chunkCount)
{
    for (int s = 0; s < (int)kCbSlotCount; ++s)
    {
        CreateStructuredSrv(SlotRel(s, SRV_BRICKS),          s_BrickBuffer,   VOXRT_BRICK_RECORD_BYTES, brickCount);
        CreateStructuredSrv(SlotRel(s, SRV_VOXEL_CELLS),     s_CellBuffer,    4u, cellCapacity);
        CreateStructuredSrv(SlotRel(s, SRV_MATERIALS),       s_MaterialBuf,   48u, matCount);
        CreateStructuredSrv(SlotRel(s, SRV_EMISSIVE_LIGHTS), s_EmissiveBuf,   16u, emissiveCount);
        CreateStructuredSrv(SlotRel(s, SRV_EMISSIVE_ALIAS),  s_AliasBuf,      8u,  emissiveCount);
        CreateStructuredSrv(SlotRel(s, SRV_CHUNK_TABLE),     s_ChunkTableBuf, 8u,  chunkCount);

        // TLAS 描述符：pResource 必须为 null，地址写在 Location 中
        if (s_Tlas)
        {
            D3D12_SHADER_RESOURCE_VIEW_DESC tv = {};
            tv.Format                  = DXGI_FORMAT_UNKNOWN;
            tv.ViewDimension           = D3D12_SRV_DIMENSION_RAYTRACING_ACCELERATION_STRUCTURE;
            tv.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            tv.RaytracingAccelerationStructure.Location = s_Tlas->GetGPUVirtualAddress();
            s_Device->CreateShaderResourceView(nullptr, &tv, CpuHandle(SlotRel(s, SRV_TLAS)));
        }
    }
}

// ============================================================================
//  资源释放
// ============================================================================
static void ReleaseSceneResources()
{
    for (ChunkGpu& c : s_Chunks)
    {
        if (c.blas)    { c.blas->Release();    c.blas    = nullptr; }
        if (c.scratch) { c.scratch->Release(); c.scratch = nullptr; }
    }
    s_Chunks.clear();

    ID3D12Resource** all[] = {
        &s_BrickBuffer, &s_CellBuffer, &s_MaterialBuf, &s_EmissiveBuf, &s_AliasBuf,
        &s_ChunkTableBuf, &s_Tlas, &s_AabbInput, &s_InstanceBuf, &s_TlasScratch
    };
    for (ID3D12Resource** p : all)
    {
        if (*p) { (*p)->Release(); *p = nullptr; }
    }

    s_BrickCpu.clear();
    s_BrickCpu.shrink_to_fit();
    s_BrickIndex.clear();
    s_BuildMap.clear();

    s_CellCapacity     = 0;
    s_HeadroomCellBase = 0;
    s_HeadroomUsed     = 0;
    s_EmissiveCount    = 0;
    s_EmissivePower    = 0.0;
    s_Stats            = RTStats{};
}

// ============================================================================
//  上传（单 staging + 逐段 CopyBufferRegion + 状态转换）
//  目标资源创建时必须处于 D3D12_RESOURCE_STATE_COPY_DEST
// ============================================================================
struct UploadItem
{
    ID3D12Resource*       dst;
    const void*           src;
    UINT64                bytes;
    D3D12_RESOURCE_STATES finalState;
};

static ID3D12Resource* RecordUploads(ID3D12GraphicsCommandList* cmd,
                                     const UploadItem* items, UINT count)
{
    UINT64 total = 0;
    std::vector<UINT64> offsets(count, 0);
    for (UINT i = 0; i < count; ++i)
    {
        offsets[i] = total;
        total += (items[i].bytes + 511) & ~(UINT64)511;
    }
    if (total == 0) return nullptr;

    ID3D12Resource* staging = CreateBufferResource(total, D3D12_HEAP_TYPE_UPLOAD,
                                                   D3D12_RESOURCE_STATE_GENERIC_READ,
                                                   D3D12_RESOURCE_FLAG_NONE, L"VoxRT_Upload");
    if (!staging) return nullptr;

    uint8_t* mapped = nullptr;
    if (FAILED(staging->Map(0, nullptr, (void**)&mapped)) || !mapped)
    {
        SetMessage("上传缓冲 Map 失败");
        staging->Release();
        return nullptr;
    }

    for (UINT i = 0; i < count; ++i)
    {
        if (items[i].src && items[i].bytes)
            memcpy(mapped + offsets[i], items[i].src, (size_t)items[i].bytes);
    }
    staging->Unmap(0, nullptr);

    for (UINT i = 0; i < count; ++i)
    {
        if (!items[i].dst || items[i].bytes == 0) continue;
        cmd->CopyBufferRegion(items[i].dst, 0, staging, offsets[i], items[i].bytes);

        if (items[i].finalState == D3D12_RESOURCE_STATE_COPY_DEST) continue;
        D3D12_RESOURCE_BARRIER b = {};
        b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource   = items[i].dst;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        b.Transition.StateAfter  = items[i].finalState;
        cmd->ResourceBarrier(1, &b);
    }
    return staging;
}

static void ReleaseSlotStaging(int slot)
{
    if (slot < 0 || slot >= (int)kCbSlotCount) return;
    for (ID3D12Resource* r : s_SlotStaging[slot]) if (r) r->Release();
    s_SlotStaging[slot].clear();
}

// 等待所有槽位的围栏：释放/重建 GPU 资源前必须调用，避免释放仍在飞行中的资源
static void WaitAllSlots()
{
    if (!s_FrameFence) return;
    for (CmdSlot& s : s_Slots)
    {
        if (s.fenceValue == 0) continue;
        unsigned int spins = 0;
        while (s_FrameFence->GetCompletedValue() < s.fenceValue)
        {
            if (++spins > (1u << 28)) return;
            YieldProcessor();
        }
    }
}

// ============================================================================
//  RT_EVENT_BUILD_SCENE — 砖化 + BLAS/TLAS 构建（纯 CPU 准备 + 一次提交）
// ============================================================================
static bool HandleBuildScene(const RTSceneDesc* desc)
{
    if (!desc || !desc->voxels || desc->voxelCount <= 0)
    {
        SetMessage("BUILD_SCENE 参数非法");
        s_State = RT_STATE_SCENE_FAILED;
        return false;
    }
    if (s_State != RT_STATE_INIT_SUCCEEDED && s_State != RT_STATE_READY)
    {
        SetMessage("BUILD_SCENE 时插件未初始化 (state=%d)", (int)s_State.load());
        return false;
    }

    WaitAllSlots();
    ReleaseSceneResources();
    s_State       = RT_STATE_INIT_SUCCEEDED;
    s_VoxelScale  = (desc->voxelScale > 0.0f) ? desc->voxelScale : 1.0f;

    const VoxelElementCpu* voxels = (const VoxelElementCpu*)desc->voxels;
    const int              voxelCount = desc->voxelCount;

    // ---------- 阶段 1：求 rootMin（与 SVOBuilder.Build 的活跃体素最小角同源）----------
    int32_t mnx = 0, mny = 0, mnz = 0;
    int32_t mxx = 0, mxy = 0, mxz = 0;
    int     activeCount = 0;
    for (int i = 0; i < voxelCount; ++i)
    {
        const VoxelElementCpu& v = voxels[i];
        if (v.matIndex < 0) continue;
        if (activeCount == 0)
        {
            mnx = mxx = v.x; mny = mxy = v.y; mnz = mxz = v.z;
        }
        else
        {
            mnx = (std::min)(mnx, v.x); mxx = (std::max)(mxx, v.x);
            mny = (std::min)(mny, v.y); mxy = (std::max)(mxy, v.y);
            mnz = (std::min)(mnz, v.z); mxz = (std::max)(mxz, v.z);
        }
        ++activeCount;
    }
    if (activeCount == 0)
    {
        SetMessage("BUILD_SCENE 失败：无活跃体素");
        s_State = RT_STATE_SCENE_FAILED;
        return false;
    }
    s_RootMin[0] = mnx; s_RootMin[1] = mny; s_RootMin[2] = mnz;

    // ---------- 阶段 2：按砖聚合占据掩码与材质 ----------
    s_BuildMap.clear();
    s_BuildMap.reserve((size_t)(activeCount / 96) + 16);

    for (int i = 0; i < voxelCount; ++i)
    {
        const VoxelElementCpu& v = voxels[i];
        if (v.matIndex < 0) continue;

        // 相对 rootMin 后必为非负 → 砖坐标与格内偏移都可用移位/掩码
        const int32_t gx = v.x - mnx;
        const int32_t gy = v.y - mny;
        const int32_t gz = v.z - mnz;

        const uint64_t key = PackKey3(gx >> 3, gy >> 3, gz >> 3);
        auto it = s_BuildMap.find(key);
        if (it == s_BuildMap.end())
        {
            BrickBuild bb;
            memset(bb.occ, 0, sizeof(bb.occ));
            bb.firstMat = (uint32_t)v.matIndex;
            bb.mixed    = 0;
            it = s_BuildMap.emplace(key, std::move(bb)).first;
        }

        BrickBuild& bb = it->second;
        const uint32_t bit = (uint32_t)(((gz & 7) << 6) | ((gy & 7) << 3) | (gx & 7));
        bb.occ[bit >> 5] |= (1u << (bit & 31u));

        const uint32_t mat = (uint32_t)v.matIndex;
        if (!bb.mixed)
        {
            if (bb.firstMat != mat)
            {
                // 材质不唯一 → 转 per-cell（先用首材质铺满，再逐格覆盖）
                bb.mixed = 1;
                bb.cells.assign((size_t)VOXRT_BRICK_CELLS, bb.firstMat);
                bb.cells[bit] = mat;
            }
        }
        else
        {
            bb.cells[bit] = mat;
        }
    }

    const size_t brickCount = s_BuildMap.size();
    if (brickCount == 0 || brickCount > (size_t)VOXRT_MAX_BRICKS_FOR_DXR)
    {
        SetMessage("BUILD_SCENE 失败：砖数 %zu 超出上限 %d", brickCount, VOXRT_MAX_BRICKS_FOR_DXR);
        s_State = RT_STATE_SCENE_FAILED;
        return false;
    }

    // ---------- 阶段 3：按 (chunkKey, brickKey) 排序 → 每 chunk 砖连续 ----------
    std::vector<std::pair<uint64_t, uint64_t>> order;   // (chunkKey, brickKey)
    order.reserve(brickCount);
    for (const auto& kv : s_BuildMap)
        order.emplace_back(BrickKeyToChunkKey(kv.first), kv.first);
    std::sort(order.begin(), order.end());

    struct ChunkBuild { uint32_t brickBase; uint32_t brickCount; };
    std::vector<ChunkBuild> chunkBuild;
    chunkBuild.reserve(brickCount / 64 + 1);

    s_BrickCpu.resize(brickCount);
    s_BrickIndex.clear();
    s_BrickIndex.reserve(brickCount);

    std::vector<uint32_t> cellData;
    cellData.reserve(brickCount * 8);   // 非均匀砖通常占比很低

    uint32_t solidCount   = 0;
    uint32_t perCellCount = 0;
    uint64_t curChunkKey  = ~(uint64_t)0;

    for (size_t i = 0; i < order.size(); ++i)
    {
        const uint64_t brickKey = order[i].second;
        const uint64_t chunkKey = order[i].first;
        BrickBuild& bb = s_BuildMap[brickKey];

        if (chunkKey != curChunkKey)
        {
            curChunkKey = chunkKey;
            chunkBuild.push_back({ (uint32_t)i, 0u });
        }
        chunkBuild.back().brickCount += 1u;

        BrickCpu& rec = s_BrickCpu[i];
        memcpy(rec.occ, bb.occ, sizeof(rec.occ));
        rec.ox      = KeyAxisX(brickKey) * VOXRT_BRICK_SIZE;
        rec.oy      = KeyAxisY(brickKey) * VOXRT_BRICK_SIZE;
        rec.oz      = KeyAxisZ(brickKey) * VOXRT_BRICK_SIZE;
        rec.flags   = 0u;
        rec.meta    = bb.mixed ? VOXRT_META_PER_CELL : bb.firstMat;
        rec.cellBase = 0xFFFFFFFFu;

        uint32_t occBits = 0;
        for (int w = 0; w < VOXRT_BRICK_OCC_U32; ++w) occBits += PopCount32(bb.occ[w]);
        if (occBits == (uint32_t)VOXRT_BRICK_CELLS)
        {
            rec.flags |= VOXRT_BRICK_FLAG_SOLID;
            ++solidCount;
        }

        if (bb.mixed)
        {
            rec.cellBase = (uint32_t)cellData.size();
            cellData.insert(cellData.end(), bb.cells.begin(), bb.cells.end());
            ++perCellCount;
        }
        else
        {
            rec.flags |= VOXRT_BRICK_FLAG_UNIFORM;
        }

        s_BrickIndex.emplace(brickKey, (uint32_t)i);
    }

    // 编辑余量：末尾预留 kHeadroomBricks 块 per-cell 空间
    s_HeadroomCellBase = (uint32_t)cellData.size();
    s_HeadroomUsed     = 0u;
    cellData.resize(cellData.size() + (size_t)kHeadroomBricks * VOXRT_BRICK_CELLS, 0u);
    s_CellCapacity = (uint32_t)cellData.size();

    // 砖化中间态到此为止（不再需要 cells 向量）
    s_BuildMap.clear();
    s_BuildMap.rehash(0);

    // ---------- 阶段 4：准备 GPU 侧数据 ----------
    std::vector<uint8_t> brickGpu(brickCount * (size_t)VOXRT_BRICK_RECORD_BYTES, 0);
    for (size_t i = 0; i < brickCount; ++i)
        memcpy(&brickGpu[i * (size_t)VOXRT_BRICK_RECORD_BYTES], &s_BrickCpu[i], sizeof(BrickCpu));

    std::vector<float> aabb((size_t)brickCount * 6u);
    // 上传前自检：AABB 必须满足 Min<=Max（砖坐标溢出/错位会让方向反转，
    // 硬件 BVH 遇到退化 AABB 会进入未定义状态，表现为首帧 DISPATCHRAYS 就 DEVICE_HUNG）。
    // 同时统计砖坐标的包围盒，用来校对它是否落在 raygen 使用的 grid 坐标空间（以 0 为原点）
    // 而不是世界绝对坐标（以 rootMin 为原点）——两者错开会造成全 miss，而非挂设备。
    size_t aabbBad = 0;
    int    bxMin = 0x7fffffff, bxMax = -0x7fffffff;
    int    byMin = 0x7fffffff, byMax = -0x7fffffff;
    int    bzMin = 0x7fffffff, bzMax = -0x7fffffff;
    for (size_t i = 0; i < brickCount; ++i)
    {
        const BrickCpu& r = s_BrickCpu[i];
        aabb[i * 6 + 0] = (float)r.ox;
        aabb[i * 6 + 1] = (float)r.oy;
        aabb[i * 6 + 2] = (float)r.oz;
        aabb[i * 6 + 3] = (float)(r.ox + VOXRT_BRICK_SIZE);
        aabb[i * 6 + 4] = (float)(r.oy + VOXRT_BRICK_SIZE);
        aabb[i * 6 + 5] = (float)(r.oz + VOXRT_BRICK_SIZE);

        if (r.ox < bxMin) bxMin = r.ox;
        if (r.ox > bxMax) bxMax = r.ox;
        if (r.oy < byMin) byMin = r.oy;
        if (r.oy > byMax) byMax = r.oy;
        if (r.oz < bzMin) bzMin = r.oz;
        if (r.oz > bzMax) bzMax = r.oz;

        if (aabb[i * 6 + 0] > aabb[i * 6 + 3] ||
            aabb[i * 6 + 1] > aabb[i * 6 + 4] ||
            aabb[i * 6 + 2] > aabb[i * 6 + 5])
            ++aabbBad;
    }
    if (aabbBad > 0)
    {
        SetMessage("BLAS AABB 自检失败：%zu / %u 个砖的 Min>Max（坐标溢出或砖坐标错乱）",
                   aabbBad, brickCount);
        s_State = RT_STATE_SCENE_FAILED;
        return false;
    }

    std::vector<uint32_t> chunkTable((size_t)chunkBuild.size() * 2u);
    for (size_t i = 0; i < chunkBuild.size(); ++i)
    {
        chunkTable[i * 2 + 0] = chunkBuild[i].brickBase;
        chunkTable[i * 2 + 1] = chunkBuild[i].brickCount;
    }

    const UINT matCount = (desc->materialCount > 0 && desc->materials)
        ? (UINT)desc->materialCount : 1u;
    // 自发光：GPU 缓冲至少 1 项（描述符必须合法），逻辑计数可为 0
    const bool hasEmissive = (desc->emissiveCount > 0 && desc->emissiveLights && desc->emissiveAlias);
    const UINT emCount = hasEmissive ? (UINT)desc->emissiveCount : 1u;
    s_EmissiveCount = hasEmissive ? desc->emissiveCount : 0;
    s_EmissivePower = hasEmissive ? (double)desc->emissiveTotalPower : 0.0;

    static const float  kZero4[4]  = { 0.0f, 0.0f, 0.0f, 0.0f };
    static const float  kZero2[2]  = { 0.0f, 0.0f };
    static const float  kZero48[12] = {};

    // ---------- 阶段 5：创建 GPU 资源（初始 COPY_DEST）----------
    s_BrickBuffer   = CreateBufferResource(brickGpu.size(),    D3D12_HEAP_TYPE_DEFAULT,
                                           D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"VoxRT_Bricks");
    s_CellBuffer    = CreateBufferResource((UINT64)cellData.size() * 4u, D3D12_HEAP_TYPE_DEFAULT,
                                           D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"VoxRT_Cells");
    s_MaterialBuf   = CreateBufferResource((UINT64)matCount * 48u, D3D12_HEAP_TYPE_DEFAULT,
                                           D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"VoxRT_Materials");
    s_EmissiveBuf   = CreateBufferResource((UINT64)emCount * 16u, D3D12_HEAP_TYPE_DEFAULT,
                                           D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"VoxRT_Emissive");
    s_AliasBuf      = CreateBufferResource((UINT64)emCount * 8u, D3D12_HEAP_TYPE_DEFAULT,
                                           D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"VoxRT_Alias");
    s_ChunkTableBuf = CreateBufferResource((UINT64)chunkBuild.size() * 8u, D3D12_HEAP_TYPE_DEFAULT,
                                           D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"VoxRT_ChunkTable");
    s_AabbInput     = CreateBufferResource((UINT64)aabb.size() * 4u, D3D12_HEAP_TYPE_DEFAULT,
                                           D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_FLAG_NONE, L"VoxRT_AABB");

    if (!s_BrickBuffer || !s_CellBuffer || !s_MaterialBuf || !s_EmissiveBuf ||
        !s_AliasBuf || !s_ChunkTableBuf || !s_AabbInput)
    {
        SetMessage("BUILD_SCENE 失败：场景 GPU 缓冲创建失败");
        s_State = RT_STATE_SCENE_FAILED;
        return false;
    }

    int slot = -1;
    ID3D12GraphicsCommandList* cmd = BeginCmdList(slot);
    if (!cmd) { s_State = RT_STATE_DEVICE_LOST; return false; }
    ReleaseSlotStaging(slot);

    ID3D12GraphicsCommandList4* cmd4 = GetList4(slot, cmd);
    if (!cmd4)
    {
        SetMessage("BUILD_SCENE 失败：CommandList 不支持 DXR（需 ID3D12GraphicsCommandList4）");
        s_State = RT_STATE_DEVICE_LOST;
        return false;
    }

    UploadItem items[7] = {};
    items[0] = { s_BrickBuffer,   brickGpu.data(),   (UINT64)brickGpu.size() * 1u,
                 D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
    items[1] = { s_CellBuffer,    cellData.data(),   (UINT64)cellData.size() * 4u,
                 D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
    items[2] = { s_MaterialBuf,   desc->materials ? desc->materials : kZero48,
                 (UINT64)matCount * 48u, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
    items[3] = { s_EmissiveBuf,   desc->emissiveLights ? desc->emissiveLights : kZero4,
                 (UINT64)emCount * 16u, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
    items[4] = { s_AliasBuf,      desc->emissiveAlias ? desc->emissiveAlias : kZero2,
                 (UINT64)emCount * 8u,  D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
    items[5] = { s_ChunkTableBuf, chunkTable.data(), (UINT64)chunkTable.size() * 4u,
                 D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
    items[6] = { s_AabbInput,     aabb.data(),       (UINT64)aabb.size() * 4u,
                 D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };

    ID3D12Resource* staging1 = RecordUploads(cmd, items, 7);
    if (!staging1) { s_State = RT_STATE_SCENE_FAILED; return false; }
    s_SlotStaging[slot].push_back(staging1);

    // ---------- 阶段 6：每 chunk 一个 BLAS ----------
    s_Chunks.resize(chunkBuild.size());
    UINT64 blasBytes = 0, blasScratch = 0;

    for (size_t c = 0; c < chunkBuild.size(); ++c)
    {
        ChunkGpu& cg = s_Chunks[c];
        cg.brickBase  = chunkBuild[c].brickBase;
        cg.brickCount = chunkBuild[c].brickCount;

        D3D12_RAYTRACING_GEOMETRY_DESC geom = {};
        geom.Type  = D3D12_RAYTRACING_GEOMETRY_TYPE_PROCEDURAL_PRIMITIVE_AABBS;
        geom.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
        geom.AABBs.AABBCount = cg.brickCount;
        geom.AABBs.AABBs.StartAddress = s_AabbInput->GetGPUVirtualAddress()
                                      + (UINT64)cg.brickBase * sizeof(D3D12_RAYTRACING_AABB);
        geom.AABBs.AABBs.StrideInBytes = sizeof(D3D12_RAYTRACING_AABB);

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS inputs = {};
        inputs.Type          = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.Flags         = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        // ⚠ NumDescs 是**几何体个数**（pGeometryDescs 数组长度），不是 AABB 个数！
        //   砖数写在 geom.AABBs.AABBCount。写成 brickCount 会让 D3D12 越界遍历栈上只声明
        //   的单个 D3D12_RAYTRACING_GEOMETRY_DESC，在 PrebuildInfo 内部直接崩在 D3D12Core。
        inputs.NumDescs      = 1;
        inputs.DescsLayout   = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.pGeometryDescs = &geom;

        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
        s_Device5->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
        if (info.ResultDataMaxSizeInBytes == 0 || info.ScratchDataSizeInBytes == 0)
        {
            SetMessage("BLAS PrebuildInfo 无效（chunk %zu, %u 砖）：result=%llu scratch=%llu",
                       c, cg.brickCount,
                       (unsigned long long)info.ResultDataMaxSizeInBytes,
                       (unsigned long long)info.ScratchDataSizeInBytes);
            s_State = RT_STATE_SCENE_FAILED;
            return false;
        }

        cg.scratch = CreateBufferResource(info.ScratchDataSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                          D3D12_RESOURCE_STATE_COMMON,
                                          D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                          L"VoxRT_BLAS_Scratch");
        cg.blas    = CreateBufferResource(info.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                          D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                                          D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                          L"VoxRT_BLAS");
        if (!cg.scratch || !cg.blas)
        {
            SetMessage("BLAS 资源创建失败（chunk %zu）", c);
            s_State = RT_STATE_SCENE_FAILED;
            return false;
        }

        blasBytes   += info.ResultDataMaxSizeInBytes;
        blasScratch += info.ScratchDataSizeInBytes;

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
        build.Inputs                            = inputs;
        build.DestAccelerationStructureData     = cg.blas->GetGPUVirtualAddress();
        build.ScratchAccelerationStructureData  = cg.scratch->GetGPUVirtualAddress();
        cmd4->BuildRaytracingAccelerationStructure(&build, 0, nullptr);

        D3D12_RESOURCE_BARRIER uav = {};
        uav.Type         = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uav.UAV.pResource = cg.blas;
        cmd->ResourceBarrier(1, &uav);
    }

    // ---------- 阶段 7：TLAS（每 chunk 一个 identity 实例）----------
    {
        const UINT instanceCount = (UINT)s_Chunks.size();
        std::vector<D3D12_RAYTRACING_INSTANCE_DESC> inst(instanceCount);
        memset(inst.data(), 0, sizeof(D3D12_RAYTRACING_INSTANCE_DESC) * instanceCount);

        for (UINT i = 0; i < instanceCount; ++i)
        {
            // identity 3x4（行主序）：几何已在 grid 空间 → 实例不需要额外变换
            inst[i].Transform[0][0] = 1.0f;
            inst[i].Transform[1][1] = 1.0f;
            inst[i].Transform[2][2] = 1.0f;
            inst[i].InstanceID                            = i;
            inst[i].InstanceMask                          = 0xFF;
            inst[i].InstanceContributionToHitGroupIndex   = 0;
            inst[i].Flags                                 = D3D12_RAYTRACING_INSTANCE_FLAG_NONE;
            inst[i].AccelerationStructure                 = s_Chunks[i].blas->GetGPUVirtualAddress();
        }

        s_InstanceBuf = CreateBufferResource(sizeof(D3D12_RAYTRACING_INSTANCE_DESC) * (UINT64)instanceCount,
                                             D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_STATE_COPY_DEST,
                                             D3D12_RESOURCE_FLAG_NONE, L"VoxRT_Instances");
        if (!s_InstanceBuf)
        {
            SetMessage("TLAS 实例缓冲创建失败");
            s_State = RT_STATE_SCENE_FAILED;
            return false;
        }

        UploadItem instItem = { s_InstanceBuf, inst.data(),
                                sizeof(D3D12_RAYTRACING_INSTANCE_DESC) * (UINT64)instanceCount,
                                D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE };
        ID3D12Resource* staging2 = RecordUploads(cmd, &instItem, 1);
        if (!staging2) { s_State = RT_STATE_SCENE_FAILED; return false; }
        s_SlotStaging[slot].push_back(staging2);

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS tinputs = {};
        tinputs.Type         = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
        tinputs.Flags        = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
        tinputs.NumDescs     = instanceCount;
        tinputs.DescsLayout  = D3D12_ELEMENTS_LAYOUT_ARRAY;
        tinputs.InstanceDescs = s_InstanceBuf->GetGPUVirtualAddress();

        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO tinfo = {};
        s_Device5->GetRaytracingAccelerationStructurePrebuildInfo(&tinputs, &tinfo);
        // 同理：零值必须在这里拦下，否则后面 CreateBufferResource(0) 失败只会报“资源创建失败”，
        // 看不出是实例数/标志的问题。
        if (tinfo.ResultDataMaxSizeInBytes == 0 || tinfo.ScratchDataSizeInBytes == 0)
        {
            SetMessage("TLAS PrebuildInfo 无效（%u 实例）：result=%llu scratch=%llu",
                       instanceCount,
                       (unsigned long long)tinfo.ResultDataMaxSizeInBytes,
                       (unsigned long long)tinfo.ScratchDataSizeInBytes);
            s_State = RT_STATE_SCENE_FAILED;
            return false;
        }

        s_TlasScratch = CreateBufferResource(tinfo.ScratchDataSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                             D3D12_RESOURCE_STATE_COMMON,
                                             D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                             L"VoxRT_TLAS_Scratch");
        s_Tlas        = CreateBufferResource(tinfo.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT,
                                             D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
                                             D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
                                             L"VoxRT_TLAS");
        if (!s_TlasScratch || !s_Tlas)
        {
            SetMessage("TLAS 资源创建失败");
            s_State = RT_STATE_SCENE_FAILED;
            return false;
        }

        D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC tbuild = {};
        tbuild.Inputs                           = tinputs;
        tbuild.DestAccelerationStructureData    = s_Tlas->GetGPUVirtualAddress();
        tbuild.ScratchAccelerationStructureData = s_TlasScratch->GetGPUVirtualAddress();
        cmd4->BuildRaytracingAccelerationStructure(&tbuild, 0, nullptr);

        D3D12_RESOURCE_BARRIER uav = {};
        uav.Type          = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        uav.UAV.pResource = s_Tlas;
        cmd->ResourceBarrier(1, &uav);

        s_Stats.tlasBytes = (long long)tinfo.ResultDataMaxSizeInBytes;
    }

    if (!EndAndExecute(slot, cmd, nullptr, 0))
    {
        s_State = RT_STATE_DEVICE_LOST;
        return false;
    }

    // ---------- 阶段 8：写描述符 + 统计 ----------
    WriteSceneDescriptors((UINT)brickCount, s_CellCapacity, matCount,
                          emCount, (UINT)chunkBuild.size());

    s_Stats.brickCount        = (int)brickCount;
    s_Stats.solidBrickCount   = (int)solidCount;
    s_Stats.chunkCount        = (int)chunkBuild.size();
    s_Stats.perCellBrickCount = (int)perCellCount;
    s_Stats.blasBytes         = (long long)blasBytes;

    s_State = RT_STATE_READY;

    SetMessage("BUILD_SCENE 完成：%zu 砖（实心 %u / 非均匀 %u），%zu chunk，"
               "体素 %d(活跃 %d)，包围盒 (%d,%d,%d)~(%d,%d,%d)，AABB 输入 %.2f MB，"
               "BLAS %.2f MB(scratch %.2f MB)，TLAS %.2f MB，"
               "砖坐标范围 (%d,%d,%d)~(%d,%d,%d)",
               brickCount, solidCount, perCellCount, chunkBuild.size(),
               voxelCount, activeCount,
               mnx, mny, mnz, mxx, mxy, mxz,
               (double)aabb.size() * 4.0 / (1024.0 * 1024.0),
               (double)blasBytes / (1024.0 * 1024.0),
               (double)blasScratch / (1024.0 * 1024.0),
               (double)s_Stats.tlasBytes / (1024.0 * 1024.0),
               bxMin, byMin, bzMin, bxMax, byMax, bzMax);
    return true;
}

// ============================================================================
//  RT_EVENT_UPDATE_VOXELS — 只改占据掩码 / per-cell 材质，**不重建 BLAS**
// ============================================================================
static void HandleUpdateVoxels(const RTVoxelDelta* delta)
{
    if (s_State != RT_STATE_READY || !delta || !delta->edits || delta->editCount <= 0) return;

    const VoxelEditCpu* edits = (const VoxelEditCpu*)delta->edits;

    // 变更集：整条 96B 砖记录 + 被触碰的 per-cell dword
    std::vector<uint32_t> changedBricks;
    struct CellChange { uint32_t cellIndex; uint32_t value; };
    std::vector<CellChange> cellChanges;
    changedBricks.reserve((size_t)delta->editCount);
    cellChanges.reserve((size_t)delta->editCount);

    int skippedMissing = 0;
    int skippedHeadroom = 0;

    for (int i = 0; i < delta->editCount; ++i)
    {
        const VoxelEditCpu& e = edits[i];
        const int32_t gx = e.x - s_RootMin[0];
        const int32_t gy = e.y - s_RootMin[1];
        const int32_t gz = e.z - s_RootMin[2];
        if (gx < 0 || gy < 0 || gz < 0) { ++skippedMissing; continue; }

        const uint64_t key = PackKey3(gx >> 3, gy >> 3, gz >> 3);
        auto it = s_BrickIndex.find(key);
        if (it == s_BrickIndex.end())
        {
            // 砖表里没有的砖：只能在下次 BUILD_SCENE 生效（编辑零重建的取舍）
            if (e.matIndex >= 0) ++skippedMissing;
            continue;
        }

        const uint32_t brickIdx = it->second;
        BrickCpu& rec = s_BrickCpu[brickIdx];
        const uint32_t bit = (uint32_t)(((gz & 7) << 6) | ((gy & 7) << 3) | (gx & 7));
        const uint32_t word = bit >> 5;
        const uint32_t mask = 1u << (bit & 31u);

        if (e.matIndex < 0)
        {
            rec.occ[word] &= ~mask;
        }
        else
        {
            const uint32_t mat = (uint32_t)e.matIndex;
            bool canStore = true;

            if ((rec.flags & VOXRT_BRICK_FLAG_UNIFORM) != 0u)
            {
                if (rec.meta != mat)
                {
                    // uniform → per-cell：从编辑余量池惰性分配一块 512 项表
                    if (s_HeadroomUsed >= kHeadroomBricks)
                    {
                        canStore = false;   // 余量耗尽：本格不写入，保持 CPU/GPU 镜像一致
                    }
                    else
                    {
                        const uint32_t cellBase = s_HeadroomCellBase + s_HeadroomUsed * VOXRT_BRICK_CELLS;
                        ++s_HeadroomUsed;

                        for (uint32_t c = 0; c < (uint32_t)VOXRT_BRICK_CELLS; ++c)
                            cellChanges.push_back({ cellBase + c, rec.meta });
                        cellChanges.push_back({ cellBase + bit, mat });

                        rec.cellBase = cellBase;
                        rec.meta     = VOXRT_META_PER_CELL;
                        rec.flags   &= ~VOXRT_BRICK_FLAG_UNIFORM;
                    }
                }
            }
            else
            {
                cellChanges.push_back({ rec.cellBase + bit, mat });
            }

            if (!canStore)
            {
                ++skippedHeadroom;
                continue;
            }
            rec.occ[word] |= mask;
        }

        // SOLID 标志重算（512 格全满才保留快速路径）
        uint32_t occBits = 0;
        for (int w = 0; w < VOXRT_BRICK_OCC_U32; ++w) occBits += PopCount32(rec.occ[w]);
        if (occBits == (uint32_t)VOXRT_BRICK_CELLS) rec.flags |= VOXRT_BRICK_FLAG_SOLID;
        else                                        rec.flags &= ~VOXRT_BRICK_FLAG_SOLID;

        if (changedBricks.empty() || changedBricks.back() != brickIdx)
            changedBricks.push_back(brickIdx);
    }

    if (changedBricks.empty() && cellChanges.empty())
    {
        if (skippedMissing || skippedHeadroom)
            SetMessage("UPDATE_VOXELS：全部跳过（砖不存在 %d / 余量耗尽 %d）",
                       skippedMissing, skippedHeadroom);
        return;
    }

    int slot = -1;
    ID3D12GraphicsCommandList* cmd = BeginCmdList(slot);
    if (!cmd) { s_State = RT_STATE_DEVICE_LOST; return; }
    ReleaseSlotStaging(slot);

    // 资源当前处于 NON_PIXEL_SHADER_RESOURCE（SRV 读取），拷贝前转到 COPY_DEST
    ID3D12Resource* touched[2] = { s_BrickBuffer, s_CellBuffer };
    for (ID3D12Resource* r : touched)
    {
        if (!r) continue;
        D3D12_RESOURCE_BARRIER b = {};
        b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource   = r;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        b.Transition.StateAfter  = D3D12_RESOURCE_STATE_COPY_DEST;
        cmd->ResourceBarrier(1, &b);
    }

    const UINT64 stride      = (UINT64)VOXRT_BRICK_RECORD_BYTES;
    const UINT64 brickBytes  = (UINT64)changedBricks.size() * stride;
    const UINT64 cellBytes   = (UINT64)cellChanges.size() * 4u;
    const UINT64 totalBytes  = ((brickBytes + 255) & ~(UINT64)255)
                             + ((cellBytes + 255) & ~(UINT64)255);

    ID3D12Resource* staging = CreateBufferResource(totalBytes, D3D12_HEAP_TYPE_UPLOAD,
                                                   D3D12_RESOURCE_STATE_GENERIC_READ,
                                                   D3D12_RESOURCE_FLAG_NONE, L"VoxRT_EditUpload");
    if (!staging) { s_State = RT_STATE_DEVICE_LOST; return; }
    s_SlotStaging[slot].push_back(staging);

    uint8_t* mapped = nullptr;
    if (FAILED(staging->Map(0, nullptr, (void**)&mapped)) || !mapped)
    {
        SetMessage("编辑上传缓冲 Map 失败");
        s_State = RT_STATE_DEVICE_LOST;
        return;
    }

    const UINT64 cellOffset = (brickBytes + 255) & ~(UINT64)255;
    for (size_t i = 0; i < changedBricks.size(); ++i)
    {
        // 只写前 88B，尾部 8B 保留 0（HLSL 结构里的 _pad0/_pad1）
        memset(mapped + i * stride, 0, (size_t)stride);
        memcpy(mapped + i * stride, &s_BrickCpu[changedBricks[i]], sizeof(BrickCpu));
    }
    for (size_t i = 0; i < cellChanges.size(); ++i)
    {
        memcpy(mapped + cellOffset + i * 4u, &cellChanges[i].value, 4u);
    }
    staging->Unmap(0, nullptr);

    for (size_t i = 0; i < changedBricks.size(); ++i)
    {
        cmd->CopyBufferRegion(s_BrickBuffer,
                              (UINT64)changedBricks[i] * stride,
                              staging, (UINT64)i * stride, stride);
    }
    for (size_t i = 0; i < cellChanges.size(); ++i)
    {
        cmd->CopyBufferRegion(s_CellBuffer,
                              (UINT64)cellChanges[i].cellIndex * 4u,
                              staging, cellOffset + (UINT64)i * 4u, 4u);
    }

    for (ID3D12Resource* r : touched)
    {
        if (!r) continue;
        D3D12_RESOURCE_BARRIER b = {};
        b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource   = r;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
        b.Transition.StateAfter  = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        cmd->ResourceBarrier(1, &b);
    }

    if (!EndAndExecute(slot, cmd, nullptr, 0))
    {
        s_State = RT_STATE_DEVICE_LOST;
        return;
    }

    if (skippedMissing || skippedHeadroom)
        SetMessage("UPDATE_VOXELS：应用 %zu 砖 / %zu 格（跳过：砖不存在 %d, 余量耗尽 %d）",
                   changedBricks.size(), cellChanges.size(), skippedMissing, skippedHeadroom);
}

// ============================================================================
//  兜底天空 cubemap（1×1×6 黑）—— 用于 skyCubemap 缺失时保持描述符表合法
// ============================================================================
static bool CreateSkyFallback()
{
    D3D12_RESOURCE_DESC tdesc = {};
    tdesc.Dimension        = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    tdesc.Width            = 1;
    tdesc.Height           = 1;
    tdesc.DepthOrArraySize = 6;
    tdesc.MipLevels        = 1;
    tdesc.Format           = DXGI_FORMAT_R8G8B8A8_UNORM;
    tdesc.SampleDesc       = { 1, 0 };
    tdesc.Layout           = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    tdesc.Flags            = D3D12_RESOURCE_FLAG_NONE;

    D3D12_HEAP_PROPERTIES props = {};
    props.Type = D3D12_HEAP_TYPE_DEFAULT;

    HRESULT hr = s_Device->CreateCommittedResource(&props, D3D12_HEAP_FLAG_NONE, &tdesc,
                                                   D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                   IID_PPV_ARGS(&s_SkyFallback));
    if (FAILED(hr))
    {
        SetMessage("兜底天空 cubemap 创建失败: 0x%08x", hr);
        s_SkyFallback = nullptr;
        return false;
    }

    // R8G8B8A8 1×1 的 RowPitch 必须对齐到 D3D12_TEXTURE_DATA_PITCH_ALIGNMENT(256)
    const UINT64 kSliceBytes = 256;
    ID3D12Resource* upload = CreateBufferResource(kSliceBytes * 6, D3D12_HEAP_TYPE_UPLOAD,
                                                  D3D12_RESOURCE_STATE_GENERIC_READ,
                                                  D3D12_RESOURCE_FLAG_NONE, L"VoxRT_SkyUpload");
    if (!upload) return false;

    uint8_t* mapped = nullptr;
    if (FAILED(upload->Map(0, nullptr, (void**)&mapped)) || !mapped)
    {
        SetMessage("兜底天空上传缓冲 Map 失败");
        upload->Release();
        return false;
    }
    memset(mapped, 0, (size_t)(kSliceBytes * 6));
    upload->Unmap(0, nullptr);

    int slot = -1;
    ID3D12GraphicsCommandList* cmd = BeginCmdList(slot);
    if (!cmd)
    {
        upload->Release();
        return false;
    }
    ReleaseSlotStaging(slot);

    D3D12_TEXTURE_COPY_LOCATION dstLoc = {};
    dstLoc.pResource = s_SkyFallback;
    dstLoc.Type      = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;

    D3D12_TEXTURE_COPY_LOCATION srcLoc = {};
    srcLoc.pResource = upload;
    srcLoc.Type      = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    srcLoc.PlacedFootprint.Footprint.Format   = DXGI_FORMAT_R8G8B8A8_UNORM;
    srcLoc.PlacedFootprint.Footprint.Width    = 1;
    srcLoc.PlacedFootprint.Footprint.Height   = 1;
    srcLoc.PlacedFootprint.Footprint.Depth    = 1;
    srcLoc.PlacedFootprint.Footprint.RowPitch = (UINT)kSliceBytes;

    for (UINT i = 0; i < 6; ++i)
    {
        dstLoc.SubresourceIndex        = i;
        srcLoc.PlacedFootprint.Offset  = (UINT64)i * kSliceBytes;
        cmd->CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, nullptr);
    }

    D3D12_RESOURCE_BARRIER b = {};
    b.Type                   = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    b.Transition.pResource   = s_SkyFallback;
    b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    b.Transition.StateAfter  = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
    cmd->ResourceBarrier(1, &b);

    if (!EndAndExecute(slot, cmd, nullptr, 0))
    {
        upload->Release();
        return false;
    }
    s_SlotStaging[slot].push_back(upload);
    return true;
}

// ============================================================================
//  RT_EVENT_RENDER — 写帧常量 → 刷描述符 → DispatchRays
// ============================================================================
static bool WriteUavDescriptor(UINT absSlot, ID3D12Resource* res)
{
    if (!res) return false;
    D3D12_RESOURCE_DESC rd = res->GetDesc();

    D3D12_UNORDERED_ACCESS_VIEW_DESC ud = {};
    if (rd.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D)
    {
        ud.Format                  = ViewFormatOf(rd.Format);
        ud.ViewDimension           = D3D12_UAV_DIMENSION_TEXTURE2D;
        ud.Texture2D.MipSlice      = 0;
        ud.Texture2D.PlaneSlice    = 0;
    }
    else
    {
        ud.Format                      = DXGI_FORMAT_UNKNOWN;
        ud.ViewDimension               = D3D12_UAV_DIMENSION_BUFFER;
        ud.Buffer.NumElements           = (UINT)(rd.Width / 4);
        ud.Buffer.StructureByteStride   = 0;
    }
    // 注：Create*View 系列均返回 void，无法从 HRESULT 查错；描述符创建是否合法
    //   只能靠调试层。真正需要取证的是资源是否带 ALLOW_UNORDERED_ACCESS——
    //   缺这个标志时建 UAV / 作为 UAV 访问都是未定义行为（调试层不一定报错）。
    s_Device->CreateUnorderedAccessView(res, nullptr, &ud, CpuHandle(absSlot));
    return true;
}

// ⚠ UAV 的 DXGI 格式必须与 shader 里的 RWTexture2D<T> 严格匹配，而普通 D3D12 调试层
//   不校验这种 shader 级匹配（那是 GPU-Based Validation 的职责），一旦不匹配就是
//   DEVICE_HUNG 而不是 ERROR。典型成因：MotionVector RT 的原生指针为 0 时，
//   旧实现会 fallback 到 RGBA32F 的颜色 RT，把 RWTexture2D<float2> 绑到 4 通道资源上。
static bool UavFormatOk(ID3D12Resource* res, int idx)
{
    if (!res) return false;
    const D3D12_RESOURCE_DESC rd = res->GetDesc();
    if (rd.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D) return false;
    const DXGI_FORMAT f = ViewFormatOf(rd.Format);
    switch (idx)
    {
    case 0:  return f == DXGI_FORMAT_R32G32B32A32_FLOAT || f == DXGI_FORMAT_R16G16B16A16_FLOAT;
    case 1:  return f == DXGI_FORMAT_R32G32_FLOAT;   // g_MotionVector: RWTexture2D<float2>
    case 2:  return f == DXGI_FORMAT_R32_FLOAT;      // g_Depth:        RWTexture2D<float>
    default: return f == DXGI_FORMAT_R32G32B32A32_FLOAT || f == DXGI_FORMAT_R16G16B16A16_FLOAT;
    }
}

// NULL UAV：写入被丢弃、读取返回 0，不产生任何内存访问，因此绝不会 fault。
// 缺资源时必须用这个，而不是借用另一张格式不同的 RT。
static bool WriteNullUav(UINT absSlot)
{
    s_Device->CreateUnorderedAccessView(nullptr, nullptr, nullptr, CpuHandle(absSlot));
    return true;
}

static void HandleRender(const RTRenderDesc* desc)
{
    if (!desc) return;
    if (s_State != RT_STATE_READY || !s_Tlas || !s_StateObject) return;
    if (desc->width <= 0 || desc->height <= 0) return;
    if (!s_CbMapped || !s_DescHeap || !s_SbtBuffer || !s_RootSig) return;

    int slot = -1;
    ID3D12GraphicsCommandList* cmd = BeginCmdList(slot);
    if (!cmd) { s_State = RT_STATE_DEVICE_LOST; return; }
    ReleaseSlotStaging(slot);

    ID3D12GraphicsCommandList4* cmd4 = GetList4(slot, cmd);
    if (!cmd4) { s_State = RT_STATE_DEVICE_LOST; return; }

    // ---- 1. 帧常量（4 槽环形，与本槽的围栏已同步）----
    RTFrameConstants* cb = (RTFrameConstants*)(s_CbMapped + (size_t)slot * kCbBytesPerSlot);
    memset(cb, 0, sizeof(RTFrameConstants));

    if (desc->invVP)  memcpy(cb->invVP,  desc->invVP,  16 * sizeof(float));
    if (desc->curVP)  memcpy(cb->curVP,  desc->curVP,  16 * sizeof(float));
    if (desc->prevVP) memcpy(cb->prevVP, desc->prevVP, 16 * sizeof(float));

    if (desc->cameraPos)
    {
        cb->cameraPos[0] = desc->cameraPos[0];
        cb->cameraPos[1] = desc->cameraPos[1];
        cb->cameraPos[2] = desc->cameraPos[2];
    }
    if (desc->sunDir)
    {
        cb->sunDir[0] = desc->sunDir[0];
        cb->sunDir[1] = desc->sunDir[1];
        cb->sunDir[2] = desc->sunDir[2];
        cb->sunDir[3] = desc->hasSun ? 1.0f : 0.0f;
    }
    if (desc->sunColor)
    {
        cb->sunColor[0] = desc->sunColor[0];
        cb->sunColor[1] = desc->sunColor[1];
        cb->sunColor[2] = desc->sunColor[2];
    }
    if (desc->skyColor)
    {
        cb->skyColor[0] = desc->skyColor[0];
        cb->skyColor[1] = desc->skyColor[1];
        cb->skyColor[2] = desc->skyColor[2];
    }
    cb->skyColor[3] = (desc->hasSkyCube && desc->skyCubemap) ? 1.0f : 0.0f;

    // grid = (world - rootMin*scale) / scale
    cb->toGrid[0] = s_RootMin[0] * s_VoxelScale;
    cb->toGrid[1] = s_RootMin[1] * s_VoxelScale;
    cb->toGrid[2] = s_RootMin[2] * s_VoxelScale;
    cb->toGrid[3] = 1.0f / s_VoxelScale;

    cb->params0[0] = desc->jitterX;
    cb->params0[1] = desc->jitterY;
    cb->params0[2] = (float)desc->width;
    cb->params0[3] = (float)desc->height;

    cb->params1[0] = (float)desc->maxDepth;
    cb->params1[1] = (float)desc->frameIndex;
    cb->params1[2] = desc->reset ? 1.0f : 0.0f;
    // 引导面位掩码：bit0 = 写 MV/深度(DLSS SR 也需要)，bit1 = 写 RR 三张引导面
    cb->params1[3] = (desc->writeGuides & VOXRT_GUIDE_MOTION_DEPTH) ? 1.0f : 0.0f;
    cb->params2[0] = s_VoxelScale;
    cb->params2[1] = (float)s_Chunks.size();
    cb->params2[2] = (float)s_EmissiveCount;
    cb->params2[3] = (float)s_EmissivePower;

    cb->flags[0] = (desc->writeGuides & VOXRT_GUIDE_RR) ? 1.0f : 0.0f;   // useRR

    // 诊断档位：由 voxrt_diag.cfg 的 level= 控制，写入 g_Flags.y 供 shader 读取
    int diag = DiagLevel();
    cb->flags[1] = (float)diag;
    // ---- 2. 每帧重建描述符（本槽专属，不会与飞行中帧竞争）----
    ID3D12Resource* sky = (ID3D12Resource*)desc->skyCubemap;
    if (!sky) sky = s_SkyFallback;
    if (sky)
    {
        D3D12_RESOURCE_DESC srd = sky->GetDesc();
        D3D12_SHADER_RESOURCE_VIEW_DESC sd = {};
        sd.Format                  = ViewFormatOf(srd.Format);
        sd.ViewDimension           = D3D12_SRV_DIMENSION_TEXTURECUBE;
        sd.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        sd.TextureCube.MostDetailedMip     = 0;
        sd.TextureCube.MipLevels           = (UINT)-1;
        sd.TextureCube.ResourceMinLODClamp = 0.0f;
        s_Device->CreateShaderResourceView(sky, &sd, CpuHandle(SlotRel(slot, SRV_SKY)));
    }

    ID3D12Resource* uavRes[6] = {
        (ID3D12Resource*)desc->colorRT,
        (ID3D12Resource*)desc->mvRT,
        (ID3D12Resource*)desc->depthRT,
        (ID3D12Resource*)desc->diffuseAlbedoRT,
        (ID3D12Resource*)desc->specularAlbedoRT,
        (ID3D12Resource*)desc->normalRoughnessRT,
    };
    // ⚠ 逐槽位校验格式：不合格的槽位写 NULL UAV（写入被丢弃），绝不能 fallback 到颜色 RT。
    //   同时把 shader 侧对应的写入开关关掉，双保险——描述符层面已经不匹配了，
    //   shader 再不改行为的话，部分驱动仍会在写入时 fault。
    bool mvOk = false, dpOk = false, rrOk = true;
    for (int i = 0; i < 6; ++i)
    {
        const bool ok = UavFormatOk(uavRes[i], i);
        if (ok) WriteUavDescriptor(SlotRel(slot, (SrvSlot)(UAV_COLOR + i)), uavRes[i]);
        else    WriteNullUav(SlotRel(slot, (SrvSlot)(UAV_COLOR + i)));

        if (i == 1)      mvOk = ok;
        else if (i == 2) dpOk = ok;
        else if (i >= 3) rrOk = rrOk && ok;
    }
    if (!mvOk || !dpOk) cb->params1[3] = 0.0f;   // 引导面不可用 → 不写 MV/深度
    if (!rrOk)          cb->flags[0]   = 0.0f;   // RR 三张不可用 → 不写 RR 引导面

    const bool colorOk = UavFormatOk(uavRes[0], 0);
    if (!colorOk)
    {
        // 不能直接 return：那样本槽位的围栏不会推进，下一帧 BeginCmdList 会等超时。
        // 退化为“不发 DispatchRays”，命令录制与状态转换照常完成。
        SetMessage("颜色 RT 不可用作 UAV（格式不符或原生指针为空），本帧跳过 DispatchRays");
        diag = 3;
        cb->flags[1] = 3.0f;
    }

    // 调度尺寸：dim= 可整体压低工作量，用于区分“规模超时”与“数据非法”
    const UINT dispatchW = (std::min)((UINT)desc->width,  DiagMaxDim());
    const UINT dispatchH = (std::min)((UINT)desc->height, DiagMaxDim());

    // ---- 3. 外部资源状态声明（Unity 侧创建的 RT / cubemap）----
    UnityGraphicsD3D12ResourceState states[7] = {};
    int stateCount = 0;
    for (int i = 0; i < 6; ++i)
    {
        ID3D12Resource* res = (ID3D12Resource*)uavRes[i];
        if (!res) continue;
        bool dup = false;
        for (int k = 0; k < stateCount; ++k)
            if (states[k].resource == res) { dup = true; break; }
        if (dup) continue;

        states[stateCount].resource = res;
        // ⚠ expected 必须与资源的**真实**状态一致，否则 Unity 不会插入 barrier。
        //   DXR 后端 bypass 了 Unity 的 CommandBuffer：这些 RT 在首帧从未被 Unity 的
        //   compute dispatch 碰过，真实状态是创建时的 COMMON，而不是 UAV。
        //   若这里写 UAV，Unity 会认为“已经是 UAV，无需转换”，GPU 就在非 UAV 状态下
        //   做 UAV 写入 → 未定义行为，症状是第一帧 DXGI_ERROR_DEVICE_HUNG(887a0006)。
        //   COMMON 作为 StateBefore 在 D3D12 中恒合法，因此对首帧与后续帧都安全
        //   （Unity 会每次插入 COMMON→UAV，代价可忽略）。
        states[stateCount].expected = D3D12_RESOURCE_STATE_COMMON;
        states[stateCount].current  = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        ++stateCount;
    }
    if (desc->hasSkyCube && desc->skyCubemap)
    {
        states[stateCount].resource = (ID3D12Resource*)desc->skyCubemap;
        states[stateCount].expected = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        states[stateCount].current  = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        ++stateCount;
    }

    // ---- 4. 录制 DispatchRays ----
    ID3D12DescriptorHeap* heaps[1] = { s_DescHeap };
    cmd->SetDescriptorHeaps(1, heaps);

    D3D12_RESOURCE_BARRIER uavB = {};
    uavB.Type          = D3D12_RESOURCE_BARRIER_TYPE_UAV;
    uavB.UAV.pResource = nullptr;          // 全局 UAV 屏障（上一帧写入 → 本帧读写）
    cmd->ResourceBarrier(1, &uavB);

    cmd4->SetPipelineState1(s_StateObject);
    // ⚠ DispatchRays 属于 compute 管线类别：它使用的是 **compute 根签名 + compute 根参数**。
    //   用 SetGraphicsRoot* 绑定对 DispatchRays 完全没有作用，而且不会报错——shader 侧
    //   读到的 CBV / 描述符表全部处于未绑定状态，写入被丢弃、读取是未定义行为。
    //   这与“不崩溃但整屏黑、且偶发 DEVICE_HUNG”完全一致：同一段 UB 两种表现。
    //   （DXR 官方示例一律是 SetComputeRootSignature + SetComputeRootDescriptorTable）
    cmd->SetComputeRootSignature(s_RootSig);
    cmd->SetComputeRootConstantBufferView(0, s_CbBuffer->GetGPUVirtualAddress()
                                              + (UINT64)slot * kCbBytesPerSlot);
    cmd->SetComputeRootDescriptorTable(1, GpuHandle(SlotRel(slot, SRV_BRICKS)));
    cmd->SetComputeRootDescriptorTable(2, GpuHandle(SlotRel(slot, UAV_COLOR)));

    const D3D12_GPU_VIRTUAL_ADDRESS sbtBase = s_SbtBuffer->GetGPUVirtualAddress();

    D3D12_DISPATCH_RAYS_DESC dr = {};
    dr.RayGenerationShaderRecord.StartAddress = sbtBase + 0ull * kSbtRecordSize;
    dr.RayGenerationShaderRecord.SizeInBytes  = kSbtRecordSize;
    dr.MissShaderTable.StartAddress           = sbtBase + 1ull * kSbtRecordSize;
    dr.MissShaderTable.SizeInBytes            = kSbtMissCount * kSbtRecordSize;
    dr.MissShaderTable.StrideInBytes          = kSbtRecordSize;
    dr.HitGroupTable.StartAddress             = sbtBase + (1ull + kSbtMissCount) * kSbtRecordSize;
    dr.HitGroupTable.SizeInBytes              = kSbtRecordSize;
    dr.HitGroupTable.StrideInBytes            = kSbtRecordSize;
    dr.Width  = dispatchW;
    dr.Height = dispatchH;
    dr.Depth  = 1;

    // 档位 3：不发 DispatchRays，只保留上面的状态声明 + barrier
    // 用于把“命令录制 + Unity 状态集成”与“DispatchRays 内容”彻底解耦
    if (diag < 3)
        cmd4->DispatchRays(&dr);

    // UAV 写入 → 后续读取（DLSS 插件在它自己的命令列表里读颜色，Unity 再做 Blit）
    // 之间必须有 UAV 屏障：DispatchRays 的写是否可见在无屏障时是未定义的，
    // 典型症状不是崩溃而是“整屏黑”，因为读到的是未被写入的旧内容/零。
    {
        D3D12_RESOURCE_BARRIER post[6] = {};
        UINT nPost = 0;
        for (int i = 0; i < 6; ++i)
        {
            if (!uavRes[i]) continue;
            post[nPost].Type          = D3D12_RESOURCE_BARRIER_TYPE_UAV;
            post[nPost].UAV.pResource = uavRes[i];
            ++nPost;
        }
        if (nPost > 0) cmd->ResourceBarrier(nPost, post);
    }

    if (!EndAndExecute(slot, cmd, states, stateCount))
        s_State = RT_STATE_DEVICE_LOST;
}

// ============================================================================
//  RT_EVENT_INIT / RT_EVENT_SHUTDOWN
// ============================================================================
static int s_Tier        = 0;
static int s_HasRayQuery = 0;

static void HandleShutdown()
{
    WaitAllSlots();
    ReleaseSceneResources();

    for (int i = 0; i < 4; ++i) ReleaseSlotStaging(i);

    ReleaseList4Cache();
    DestroyCmdInfra();

    if (s_CbMapped && s_CbBuffer) { s_CbBuffer->Unmap(0, nullptr); s_CbMapped = nullptr; }
    if (s_CbBuffer)   { s_CbBuffer->Release();   s_CbBuffer   = nullptr; }
    if (s_SbtBuffer)  { s_SbtBuffer->Release();  s_SbtBuffer  = nullptr; }
    if (s_StateObject){ s_StateObject->Release(); s_StateObject = nullptr; }
    if (s_RootSig)    { s_RootSig->Release();    s_RootSig    = nullptr; }
    if (s_DescHeap)   { s_DescHeap->Release();   s_DescHeap   = nullptr; }
    if (s_SkyFallback){ s_SkyFallback->Release(); s_SkyFallback = nullptr; }
    if (s_Device5)    { s_Device5->Release();    s_Device5    = nullptr; }

    s_SbtRayGen = nullptr;
    s_SbtMiss   = nullptr;
    s_SbtHit    = nullptr;
    s_Tier      = 0;
    s_HasRayQuery = 0;
    s_State     = RT_STATE_NOT_INITIALIZED;
}

static void HandleInit(const RTInitData* data)
{
    if (s_State == RT_STATE_INIT_SUCCEEDED || s_State == RT_STATE_READY)
    {
        SetMessage("INIT：已处于就绪状态，忽略重复请求");
        return;
    }

    s_State = RT_STATE_INIT_FAILED;   // 乐观置失败，全部成功后覆写

    if (!s_D3D12 || !s_Device)
    {
        SetMessage("INIT 失败：未获得 IUnityGraphicsD3D12v8 / ID3D12Device"
                   "（Player Settings 的图形 API 必须包含 D3D12）");
        return;
    }

    if (!s_Device5)
    {
        HRESULT hr = s_Device->QueryInterface(IID_PPV_ARGS(&s_Device5));
        if (FAILED(hr) || !s_Device5)
        {
            SetMessage("INIT 失败：ID3D12Device5 不可用（需 Win10 1809+ 与支持 DXR 的驱动）0x%08x", hr);
            s_Device5 = nullptr;
            return;
        }
    }

    D3D12_FEATURE_DATA_D3D12_OPTIONS5 opts5 = {};
    HRESULT hr = s_Device->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &opts5, sizeof(opts5));
    if (FAILED(hr))
    {
        SetMessage("INIT 失败：CheckFeatureSupport(D3D12_OPTIONS5) 0x%08x", hr);
        return;
    }

    s_Tier = (int)opts5.RaytracingTier;
    // RayQuery 需 SM6.5；此处仅作为保守的能力上报（Tier >= 1.1 时视为可用）
    s_HasRayQuery = (opts5.RaytracingTier >= D3D12_RAYTRACING_TIER_1_1) ? 1 : 0;

    if (opts5.RaytracingTier < D3D12_RAYTRACING_TIER_1_0)
    {
        SetMessage("INIT 失败：RaytracingTier=%d < 1.0（该 GPU/驱动不支持 DXR）", (int)opts5.RaytracingTier);
        return;
    }

    // ---- 描述符堆（4 槽 × 14 个 + 余量）----
    if (!s_DescHeap)
    {
        D3D12_DESCRIPTOR_HEAP_DESC hd = {};
        hd.Type           = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        hd.NumDescriptors = kDescHeapSlots;
        hd.Flags          = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        hr = s_Device->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&s_DescHeap));
        if (FAILED(hr))
        {
            SetMessage("CreateDescriptorHeap 失败: 0x%08x", hr);
            s_DescHeap = nullptr;
            return;
        }
        s_DescSize      = s_Device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        s_CpuHandleBase = s_DescHeap->GetCPUDescriptorHandleForHeapStart();
        s_GpuHandleBase = s_DescHeap->GetGPUDescriptorHandleForHeapStart();
    }

    if (!s_RootSig && !CreateRootSignature()) return;

    // ---- 命令基础设施 ----
    if (!s_CmdInfraReady)
    {
        ReleaseList4Cache();   // 旧 list 指针随之失效
        if (!CreateCmdInfra()) return;
    }

    // ---- 帧常量上传缓冲（4 槽 × 512B）----
    if (!s_CbBuffer)
    {
        s_CbBuffer = CreateBufferResource((UINT64)kCbBytesPerSlot * kCbSlotCount,
                                          D3D12_HEAP_TYPE_UPLOAD,
                                          D3D12_RESOURCE_STATE_GENERIC_READ,
                                          D3D12_RESOURCE_FLAG_NONE, L"VoxRT_FrameCB");
        if (!s_CbBuffer) return;

        if (FAILED(s_CbBuffer->Map(0, nullptr, (void**)&s_CbMapped)) || !s_CbMapped)
        {
            SetMessage("帧常量缓冲 Map 失败");
            s_CbBuffer->Release();
            s_CbBuffer = nullptr;
            return;
        }
        memset(s_CbMapped, 0, (size_t)kCbBytesPerSlot * kCbSlotCount);
    }

    // ---- RTPSO + SBT ----
    if (!s_StateObject && !CreateRaytracingPipeline(data ? data->dxilPath : nullptr)) return;
    if (!s_SbtBuffer && !CreateShaderBindingTable())
    {
        s_State = RT_STATE_INIT_FAILED;
        return;
    }

    // ---- 兜底天空 cubemap ----
    if (!s_SkyFallback && !CreateSkyFallback()) return;

    s_State = RT_STATE_INIT_SUCCEEDED;
    SetMessage("INIT 成功：RaytracingTier=%d，描述符槽=%d，DXIL=%s",
               s_Tier, (int)kDescHeapSlots, (data && data->dxilPath) ? "指定路径" : "DLL 同目录");
}

// ============================================================================
//  RT_EVENT_SECTION / Unity 生命周期 + 导出接口
// ============================================================================
static void UNITY_INTERFACE_API RT_RenderEvent(int eventId, void* data)
{
    switch (eventId)
    {
    case RT_EVENT_INIT:          HandleInit(reinterpret_cast<const RTInitData*>(data));       break;
    case RT_EVENT_BUILD_SCENE:   HandleBuildScene(reinterpret_cast<const RTSceneDesc*>(data)); break;
    case RT_EVENT_UPDATE_VOXELS: HandleUpdateVoxels(reinterpret_cast<const RTVoxelDelta*>(data)); break;
    case RT_EVENT_RENDER:        HandleRender(reinterpret_cast<const RTRenderDesc*>(data));    break;
    case RT_EVENT_RELEASE_SCENE:
        WaitAllSlots();
        ReleaseSceneResources();
        if (s_State == RT_STATE_READY) s_State = RT_STATE_INIT_SUCCEEDED;
        break;
    case RT_EVENT_SHUTDOWN:      HandleShutdown();                                             break;
    default:                     SetMessage("未知事件 ID: 0x%08x", eventId);                     break;
    }
}

static void UNITY_INTERFACE_API OnDeviceEvent(UnityGfxDeviceEventType type)
{
    switch (type)
    {
    case kUnityGfxDeviceEventInitialize:
        if (s_D3D12)
        {
            s_Device     = s_D3D12->GetDevice();
            s_FrameFence = s_D3D12->GetFrameFence();
        }
        break;
    case kUnityGfxDeviceEventBeforeReset:
    case kUnityGfxDeviceEventShutdown:
        // 设备即将失效：只销毁持有设备对象的命令基础设施，
        // DXR 资源的完整释放在 C# 显式调度的 SHUTDOWN 事件里完成。
        ReleaseList4Cache();
        DestroyCmdInfra();
        break;
    }
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    s_Graphics = unityInterfaces->Get<IUnityGraphics>();
    s_D3D12    = unityInterfaces->Get<IUnityGraphicsD3D12v8>();

    if (s_Graphics)
    {
        s_Graphics->RegisterDeviceEventCallback(OnDeviceEvent);
        // 插件加载可能晚于设备创建，手动触发一次以捕获当前设备
        OnDeviceEvent(kUnityGfxDeviceEventInitialize);
    }
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    HandleShutdown();
    if (s_Graphics) s_Graphics->UnregisterDeviceEventCallback(OnDeviceEvent);
    s_Graphics   = nullptr;
    s_D3D12      = nullptr;
    s_Device     = nullptr;
    s_FrameFence = nullptr;
}

RT_API void* RT_GetRenderEventFunc(void)
{
    return reinterpret_cast<void*>(&RT_RenderEvent);
}

RT_API int RT_GetState(void)
{
    return s_State.load(std::memory_order_acquire);
}

RT_API int RT_GetCapability(RTCapability* outCapability)
{
    if (!outCapability) return -1;
    outCapability->raytracingTier     = s_Tier;
    outCapability->hasRayQuery        = s_HasRayQuery;
    outCapability->maxBrickPrimitives = VOXRT_MAX_BRICKS_FOR_DXR;
    outCapability->reserved           = 0;
    return (s_Tier >= (int)D3D12_RAYTRACING_TIER_1_0) ? 0 : -1;
}

RT_API const char* RT_GetLastMessage(void)
{
    AcquireSRWLockShared(&s_MsgLock);
    const char* msg = s_Msg;
    ReleaseSRWLockShared(&s_MsgLock);
    return msg;
}

RT_API void RT_GetStats(RTStats* outStats)
{
    if (!outStats) return;
    *outStats = s_Stats;
}



