// ============================================================================
//  voxel_common.hlsli — DXR 体素路径追踪公共声明与工具函数
//
//  职责:
//    1. 与 VoxRTPluginAPI.h 严格同步的常量（砖尺寸 / chunk 尺寸 / 标志位）
//    2. 常量缓冲 b0 与全部 SRV/UAV 声明（寄存器号必须与 VoxRTPlugin.cpp 根签名一致）
//    3. 从 SVO_PathTracing.compute **逐函数等价搬迁**的数学与采样工具
//       （保持同名同签名，便于与 compute 后端逐行对拍）
//
//  与 compute 路径的唯一差别:
//    - _SVOMaterials → g_Materials，_SVORootMin/_SVOScale 等全局量 → g_ToGrid / g_Params2
//    - 遍历不再是 TraverseSVO_Stack，而是硬件 TraceRay（见 voxel_pathtracing.hlsl）
//    - 世界/网格空间统一改为 **grid 空间求交**：AABB 在 grid 空间构建（整数端点），
//      避免 float 大坐标精度损失；raygen 里一次性把射线变换到 grid 空间
// ============================================================================

#ifndef VOXEL_COMMON_HLSLI
#define VOXEL_COMMON_HLSLI

// ============================================================================
//  编译期常量（与 VoxRTPluginAPI.h / DXRInterop.cs 三处一致）
// ============================================================================
#define VOXRT_BRICK_SIZE      8
#define VOXRT_BRICK_CELLS     512
#define VOXRT_BRICK_OCC_U32   16
#define VOXRT_META_PER_CELL   0xFFFFFFFFu
#define VOXRT_BRICK_FLAG_SOLID   1u   // 512 格全满 → IS 直接 ReportHit
#define VOXRT_BRICK_FLAG_UNIFORM 2u   // 单材质 → meta 即材质索引

// 非实心砖内 DDA 的最大步数：8³ 砖沿任意方向最多穿过 3*8-2 = 22 格，
// 取 24 留余量；若把砖尺寸改成 16，此处必须同步改成 48
#define VOXRT_DDA_MAX_STEPS   24

static const float EPSILON = 1e-6f;
static const float PI = 3.14159265359f;
static const float INV_PI = 0.31830988618f;
static const float FIREFLY_THRESHOLD = 5.0f;
static const float INFINITE = 1e30f;

// ============================================================================
//  brick 记录（96B，与 VoxRTPluginAPI.h 的 VOXRT_BRICK_RECORD_BYTES 一致）
// ============================================================================
struct BrickRecord
{
    uint  occ[VOXRT_BRICK_OCC_U32]; // +0   64B  占据掩码: bit = (z<<6)|(y<<3)|x
    uint  cellBase;                 // +64   4B   g_VoxelCells 基址（每砖 512 项）
    uint  meta;                     // +68   4B   单材质索引；0xFFFFFFFF = 逐格查表
    uint  flags;                    // +72   4B   SOLID / UNIFORM
    int   ox;                       // +76   4B   砖在 grid 空间的整数原点
    int   oy;                       // +80   4B
    int   oz;                       // +84   4B
    uint  _pad0;                    // +88   4B
    uint  _pad1;                    // +92   4B  → 96B（16B 对齐，occ 可聚合读）
};

// ============================================================================
//  帧常量（336B；CBV 缓冲按 256B 对齐分配 512B）
//
//  矩阵布局约定：
//    C# 侧 DXRPathTracer.WriteRenderDesc 用 FlattenMatrixColumnMajor 把 Unity
//    Matrix4x4 的**列主序**内存原样拷进本 cbuffer（插件侧不做任何转置）。
//    HLSL/DXC 的矩阵 packing 默认值就是 column_major，与此天然一致，
//    因此 mul(M, v) 即标准矩阵乘向量。修饰符显式写出仅为表达意图，删掉不改行为。
//    （不要拿 Unity 的 Shader.SetMatrix 类比：那条路径的上传格式由 Unity 决定，
//      与本插件裸 memcpy 的约定无关。）
// ============================================================================
cbuffer FrameCB : register(b0)
{
    column_major float4x4 g_InvVP;   // 裁剪空间 → 世界空间
    column_major float4x4 g_CurVP;   // 当前帧 ViewProjection（含 jitter）
    column_major float4x4 g_PrevVP;  // 上一帧 ViewProjection（含 jitter）
    float4   g_CameraPos;  // xyz = 相机世界坐标
    float4   g_SunDir;     // xyz = 指向光源方向, w = hasSun
    float4   g_SunColor;   // rgb = 颜色 × 强度
    float4   g_SkyColor;   // rgb = 回退天空色, w = hasSkyCube
    float4   g_ToGrid;     // xyz = rootMin(世界空间), w = 1/scale  → grid = (world-xyz)*w
    float4   g_Params0;    // x = jitterX, y = jitterY, z = width, w = height
    float4   g_Params1;    // x = maxDepth, y = frameIndex, z = reset, w = writeGuides(仅 bit0: 写 MV/深度)
    float4   g_Params2;    // x = voxelScale, y = chunkCount, z = emissiveCount, w = emissivePower
    float4   g_Flags;      // x = useRR(1 = 写 RR 引导面且放宽萤火虫阀值), y = 诊断档位(0 正常), zw 保留
};

// ============================================================================
//  资源绑定（寄存器号与 VoxRTPlugin.cpp 根签名一一对应）
// ============================================================================
StructuredBuffer<BrickRecord> g_Bricks        : register(t0);
StructuredBuffer<uint>        g_VoxelCells    : register(t1);
StructuredBuffer<float4>      g_Materials     : register(t2);
StructuredBuffer<float4>      g_EmissiveLights: register(t3);
StructuredBuffer<float2>      g_EmissiveAlias : register(t4);
TextureCube                   g_Sky           : register(t5);
StructuredBuffer<uint2>       g_ChunkTable    : register(t6); // x = brickBase, y = brickCount
RaytracingAccelerationStructure g_TLAS        : register(t7); // 描述符表内的 SRV(维度 RAYTRACING_ACCELERATION_STRUCTURE)

SamplerState g_SkySampler : register(s0); // 根签名静态采样器（LINEAR / CLAMP / MIP）

RWTexture2D<float4> g_Color          : register(u0);
RWTexture2D<float2> g_MotionVector   : register(u1);
RWTexture2D<float>  g_Depth          : register(u2);
RWTexture2D<float4> g_DiffuseAlbedo  : register(u3);
RWTexture2D<float4> g_SpecularAlbedo : register(u4);
RWTexture2D<float4> g_NormalRoughness: register(u5);

// ============================================================================
//  射线载荷 / 交点属性（payload 16B、attribute 8B，与 RTPSO 配置一致）
// ============================================================================
struct VoxelPayload
{
    float t;        // 命中距离（世界参数）；miss = -1；阴影射线 = 0 遮挡 / 1 可见
    uint  matIndex; // 命中体素材质
    uint  face;     // 进入面: 0=+X 1=-X 2=+Y 3=-Y 4=+Z 5=-Z
    uint  _pad;
};

struct VoxelAttributes
{
    uint2 data;     // x = face, y = 保留（逐格线性索引，调试用）
};

// ============================================================================
//  伪随机数生成（与 SVO_PathTracing.compute L97-L108 完全一致）
// ============================================================================
uint pcg(inout uint seed)
{
    uint state = seed * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float rand(inout uint seed)
{
    seed = pcg(seed);
    return asfloat(0x3f800000u | (seed >> 9)) - 1.0f;
}

// ============================================================================
//  BRDF 采样函数（与 compute 版逐行一致）
// ============================================================================
float3 SampleDiffuse(float3 N, inout uint seed)
{
    float u1 = rand(seed);
    float u2 = rand(seed);
    float r = sqrt(u1);
    float theta = 2.0f * PI * u2;
    float x = r * cos(theta);
    float y = r * sin(theta);
    float z = sqrt(1.0f - u1);

    float3 up = abs(N.y) < 0.999f ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 T = normalize(cross(up, N));
    float3 B = cross(N, T);
    return normalize(T * x + B * y + N * z);
}

float3 SampleGGX(float3 N, float roughness, inout uint seed)
{
    float u1 = rand(seed);
    float u2 = rand(seed);
    float a = roughness * roughness;
    float a2 = a * a;

    float phi = 2.0f * PI * u1;
    float cosTheta = sqrt((1.0f - u2) / (1.0f + (a2 - 1.0f) * u2));
    float sinTheta = sqrt(1.0f - cosTheta * cosTheta);

    float3 up = abs(N.y) < 0.999f ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 T = normalize(cross(up, N));
    float3 B = cross(N, T);

    return normalize(T * (sinTheta * cos(phi)) + B * (sinTheta * sin(phi)) + N * cosTheta);
}

float SchlickFresnel(float F0, float cosTheta)
{
    return F0 + (1.0f - F0) * pow(1.0f - max(cosTheta, 0.0f), 5.0f);
}

float SmithG1_GGX(float3 w, float3 N, float roughness)
{
    float a2 = roughness * roughness * roughness * roughness;
    float cosTheta = max(0.0, dot(w, N));
    float sinTheta2 = 1.0f - cosTheta * cosTheta;
    return 2.0f / (1.0f + sqrt(1.0f + a2 * sinTheta2 / max(cosTheta * cosTheta, 1e-7f)));
}

// 网格空间与世界空间仅差均匀缩放 + 平移 → 方向向量不变
float3 GridDirToWorld(float3 gridD)
{
    return normalize(gridD);
}

// ============================================================================
//  材质读取（48B = 3×float4，与 SVO_PathTracing.compute L449-L469 一致）
// ============================================================================
struct VoxelMaterial
{
    float  type;
    float  weight;
    float  rough;
    float  spec;
    float  ior;
    float  att;
    float  flux;
    float3 color;
    float  alpha;
};

VoxelMaterial ReadMaterial(int matIndex)
{
    VoxelMaterial mat;
    uint base = (uint)matIndex * 3u;

    float4 slot0 = g_Materials[base];
    float4 slot1 = g_Materials[base + 1u];
    float4 slot2 = g_Materials[base + 2u];

    mat.type   = slot0.x;
    mat.weight = slot0.y;
    mat.rough  = max(slot0.z, 0.01f);
    mat.spec   = slot0.w;
    mat.ior    = slot1.x;
    mat.att    = slot1.y;
    mat.flux   = slot1.z;
    mat.color  = slot2.rgb;
    mat.alpha  = slot2.w;

    return mat;
}

// ============================================================================
//  天空采样
// ============================================================================
float3 SampleSky(float3 dir)
{
    return (g_SkyColor.w > 0.5f)
        ? g_Sky.SampleLevel(g_SkySampler, dir, 0).rgb
        : g_SkyColor.rgb;
}

// ============================================================================
//  NEE — 直接光结构（与 compute 版 L484-L490 一致）
// ============================================================================
struct DirectLight
{
    float3 direction;        // 指向光源的方向
    float3 radianceOverPdf;  // 辐射亮度 / PDF
    float  distance;         // 到光源距离（环境光 = INFINITE）
    float  pdf;              // 概率密度
};

float3 EvalBSDF(VoxelMaterial mat, float3 N, float3 wo, float3 wi, out float bsdfPDF)
{
    bsdfPDF = 0.0;
    float NoL = dot(N, wi);

    if (abs(mat.type - 0.0f) < 0.5f || abs(mat.type - 4.0f) < 0.5f || abs(mat.type - 5.0f) < 0.5f)
    {
        // Diffuse / Cloud / Blend
        if (NoL <= 0.0 || dot(N, wo) <= 0.0) return float3(0, 0, 0);
        bsdfPDF = NoL * INV_PI;
        return mat.color * INV_PI * NoL;
    }
    else if (abs(mat.type - 1.0f) < 0.5f)
    {
        // Metal (GGX)
        if (NoL <= 0.0 || dot(N, wo) <= 0.0) return float3(0, 0, 0);
        float3 H = normalize(wo + wi);
        float NoH = max(dot(N, H), 1e-7f);
        float NoW = max(dot(N, wo), 1e-7f);
        float a2 = pow(mat.rough, 4.0);
        float d = NoH * NoH * (a2 - 1.0) + 1.0;
        float D = a2 / (PI * d * d);
        float G1_wi = SmithG1_GGX(wi, N, mat.rough);
        float G1_wo = SmithG1_GGX(wo, N, mat.rough);
        float G2 = G1_wi * G1_wo;
        float F = SchlickFresnel(mat.spec, max(dot(H, wo), 0.0));
        float3 spec = mat.color * F;
        float3 bsdf = spec * D * G2 / (4.0 * NoW * NoL);
        bsdfPDF = D * NoH / (4.0 * max(dot(wo, H), 1e-7f));
        return bsdf * NoL;
    }
    else if (abs(mat.type - 2.0f) < 0.5f)
    {
        // Glass: delta 分布，非精确方向返回 0
        return float3(0, 0, 0);
    }
    else if (abs(mat.type - 3.0f) < 0.5f)
    {
        // Emissive: 不参与 NEE
        return float3(0, 0, 0);
    }

    // 默认按 Diffuse 处理
    if (NoL <= 0.0 || dot(N, wo) <= 0.0) return float3(0, 0, 0);
    bsdfPDF = NoL * INV_PI;
    return mat.color * INV_PI * NoL;
}

// 环境光 PDF（余弦加权半球）
float EnvironmentLightPDF(float3 N, float3 dir)
{
    return max(0.0, dot(N, dir)) * INV_PI;
}

// ---- 自发光体素 alias table 采样 ----
// compute 版用 _EmissiveLightCount / _EmissiveTotalPower 两个标量全局量；
// DXR 版没有全局标量通道，改为打包在 g_Params2.zw：
//   g_Params2 = (voxelScale, chunkCount, emissiveCount, emissiveTotalPower)
#define g_EmissiveCount   ((int)g_Params2.z)
#define g_EmissivePower   (g_Params2.w)

uint SampleEmissiveAliasIndex(inout uint seed)
{
    uint n = (uint)g_EmissiveCount;
    if (n <= 1u) return 0u;
    float r1 = rand(seed);
    float r2 = rand(seed);
    uint idx = min((uint)(r1 * n), n - 1u);
    float2 entry = g_EmissiveAlias[idx];
    return (r2 < entry.x) ? idx : (uint)entry.y;
}

bool SampleEmissiveLight(float3 hitPosGrid, float3 NGrid,
                         inout uint seed, out DirectLight directLight)
{
    directLight.pdf = 0.0;
    directLight.distance = 0.0;
    directLight.radianceOverPdf = float3(0, 0, 0);
    directLight.direction = float3(0, 1, 0);

    float totalPower = g_EmissivePower;
    if (g_EmissiveCount <= 0 || totalPower <= 0.0)
        return false;

    float  scale = g_Params2.x;
    float3 rootMin = g_ToGrid.xyz;

    // 1. alias table 采样一个发光体素
    uint idx = SampleEmissiveAliasIndex(seed);
    float4 lightData = g_EmissiveLights[idx];

    // 2. grid 坐标 → 世界坐标中心
    float3 lightCenterWorld = (lightData.xyz + 0.5) * scale + rootMin;
    float3 hitPosWorld = hitPosGrid * scale + rootMin;

    // 3. 方向和距离
    float3 toLight = lightCenterWorld - hitPosWorld;
    float distWorld = length(toLight);
    if (distWorld < 1e-4f) return false;

    // 距离筛选（世界空间 100 单位）
    if (distWorld > 100.0f) return false;

    float3 lightDir = toLight / distWorld;

    // 法线半球检查
    if (dot(NGrid, lightDir) <= 0.0f) return false;

    // 4. 读取发光材质
    // ⚠ 与 compute 版 L769 的差异：compute 用 asint(lightData.w) 取材质索引，
    //   但 C# 侧写入的 w 是 (float)materialIndex（SVOBuilder.RebuildEmissiveLights），
    //   按位重解释会得到脏索引 → 越界读 0 → 自发光 NEE 静默失效。
    //   DXR 版改为直接按 float 转 int，与数据格式一致。
    int matIdx = (int)(lightData.w + 0.5f);
    VoxelMaterial emMat = ReadMaterial(matIdx);
    float3 emColor = pow(2.0f, emMat.flux) * emMat.color;

    // 5. 无偏 NEE 估计：贡献 / 选中概率
    float distGrid2 = distWorld * distWorld / max(scale * scale, 1e-12f);
    float lightLum  = dot(emColor, float3(0.2126, 0.7152, 0.0722));
    float pSelect   = lightLum / totalPower;
    directLight.radianceOverPdf = emColor / max(pSelect * distGrid2, 1e-12f);
    directLight.direction = lightDir;
    directLight.distance = distWorld;
    directLight.pdf = 1.0f;  // delta 光源，MIS weight = 1.0

    return true;
}

// NEE — 环境光采样（余弦半球 + Cubemap）
float3 SampleEnvironmentLight(float3 NGrid, inout uint seed,
                              out float3 lightDirGrid, out float lightPDF)
{
    lightDirGrid = SampleDiffuse(NGrid, seed);
    lightPDF = max(0.0, dot(NGrid, lightDirGrid)) * INV_PI;

    float3 lightDirWorld = GridDirToWorld(lightDirGrid);
    float3 radiance = SampleSky(lightDirWorld);
    return radiance / max(lightPDF, 1e-10f);
}

// NEE — 方向光（太阳）采样
bool SampleSunLight(float3 NGrid, inout uint seed, out DirectLight directLight)
{
    directLight.pdf = 0.0;
    directLight.distance = INFINITE;
    directLight.radianceOverPdf = float3(0, 0, 0);
    directLight.direction = float3(0, 1, 0);

    if (g_SunDir.w < 0.5f) return false;

    float3 sunDirGrid = normalize(g_SunDir.xyz);
    float ndotl = dot(NGrid, sunDirGrid);
    if (ndotl <= 0.0f) return false;

    directLight.direction = sunDirGrid;
    directLight.radianceOverPdf = g_SunColor.rgb;
    directLight.pdf = 1.0f;  // delta 光源，MIS weight = 1.0

    return true;
}

// NEE — 统一光源采样入口（HDR 环境光 + 方向光 + 自发光体素，one-sample MIS）
void SampleLights(float3 hitPosGrid, float3 NGrid, float3 woGrid,
                  inout uint seed, out DirectLight directLight)
{
    directLight.pdf = 0.0;
    directLight.distance = INFINITE;
    directLight.radianceOverPdf = float3(0, 0, 0);
    directLight.direction = float3(0, 1, 0);

    bool hasSky = g_SkyColor.w > 0.5f;
    bool hasSun = g_SunDir.w > 0.5f;
    bool hasEm  = g_EmissiveCount > 0;

    float envWeight = hasSky ? 0.33f : 0.0f;
    float sunWeight = hasSun ? 0.34f : 0.0f;
    float emWeight  = hasEm  ? 0.33f : 0.0f;
    float totalW = envWeight + sunWeight + emWeight;
    if (totalW < 1e-6f) return;

    // 按权重随机选择一种光源采样（one-sample MIS）
    float r = rand(seed) * totalW;

    if (r < envWeight && hasSky)
    {
        // ---- HDR 环境光采样 ----
        float3 lightDirGrid;
        float  envPDF;
        float3 radianceOverPdf = SampleEnvironmentLight(NGrid, seed, lightDirGrid, envPDF);

        directLight.direction = lightDirGrid;
        directLight.pdf = envPDF;  // 原始 PDF，选择概率在期望中自然消掉
        directLight.radianceOverPdf = radianceOverPdf;
        // distance 保持 INFINITE
    }
    else if (r < envWeight + sunWeight && hasSun)
    {
        // ---- 方向光采样 ----
        DirectLight sunLight;
        if (SampleSunLight(NGrid, seed, sunLight))
        {
            directLight = sunLight;
            directLight.pdf = sunLight.pdf;
        }
    }
    else if (hasEm)
    {
        // ---- 自发光体素采样 ----
        DirectLight emLight;
        if (SampleEmissiveLight(hitPosGrid, NGrid, seed, emLight))
        {
            directLight = emLight;
            directLight.pdf = emLight.pdf;
        }
    }
}

// ============================================================================
//  BSDF 采样（与 compute 版 L885-L974 一致）
// ============================================================================
struct BsdfSampleData
{
    float3 newDir;        // 新弹射方向（grid 空间）
    float3 bsdf_over_pdf; // BSDF/PDF 比值
    float  pdf;
    int    eventType;     // 0=正常, 1=吸收
};

void SampleBSDF(VoxelMaterial mat, float3 hitNGrid, float3 rdGrid,
                inout bool isInside, inout uint seed,
                out BsdfSampleData data)
{
    float3 NGrid = normalize(hitNGrid);
    data.eventType = 0;

    if (abs(mat.type - 0.0f) < 0.5f) // Diffuse
    {
        data.newDir = SampleDiffuse(NGrid, seed);
        data.bsdf_over_pdf = mat.color;
        data.pdf = max(0.0, dot(NGrid, data.newDir)) * INV_PI;
    }
    else if (abs(mat.type - 1.0f) < 0.5f) // Metal (GGX)
    {
        float3 woGrid = normalize(-rdGrid);
        float3 H = SampleGGX(NGrid, mat.rough, seed);
        float3 wrGrid = reflect(-woGrid, H);
        float ndotwr = max(0.0, dot(NGrid, wrGrid));

        if (ndotwr > 0.0f)
        {
            data.newDir = normalize(wrGrid);
            float cosTheta = max(0.0, dot(H, woGrid));
            float F = SchlickFresnel(mat.spec, cosTheta);
            float G1_wi = SmithG1_GGX(woGrid, NGrid, mat.rough);
            float G1_wr = SmithG1_GGX(wrGrid, NGrid, mat.rough);
            data.bsdf_over_pdf = mat.color * F * (G1_wi / max(G1_wr, 1e-6f));

            float NoH = max(dot(NGrid, H), 1e-7f);
            float WoH = max(dot(woGrid, H), 1e-7f);
            float a2 = pow(mat.rough, 4.0);
            float d = NoH * NoH * (a2 - 1.0) + 1.0;
            float D = a2 / (PI * d * d);
            data.pdf = D * NoH / (4.0 * WoH);
        }
        else
        {
            data.newDir = SampleDiffuse(NGrid, seed);
            data.bsdf_over_pdf = mat.color;
            data.pdf = max(0.0, dot(NGrid, data.newDir)) * INV_PI;
        }
    }
    else if (abs(mat.type - 2.0f) < 0.5f) // Glass
    {
        float3 woGrid = normalize(-rdGrid);
        float cosTheta = dot(NGrid, woGrid);
        float etaI = isInside ? mat.ior : 1.0f;
        float etaT = isInside ? 1.0f : mat.ior;
        float eta = etaI / etaT;
        float F = SchlickFresnel(0.04f, abs(cosTheta));

        if (rand(seed) < F)
        {
            data.newDir = normalize(reflect(-woGrid, NGrid));
        }
        else
        {
            float3 wtGrid = refract(-woGrid, NGrid, eta);
            if (length(wtGrid) < 0.001f)
                data.newDir = normalize(reflect(-woGrid, NGrid));
            else
            {
                data.newDir = normalize(wtGrid);
                isInside = !isInside;
            }
        }
        data.bsdf_over_pdf = mat.color;
        data.pdf = 1.0f;  // delta 分布
    }
    else // Cloud / Blend 等，按 Diffuse 处理
    {
        data.newDir = SampleDiffuse(NGrid, seed);
        data.bsdf_over_pdf = mat.color;
        data.pdf = max(0.0, dot(NGrid, data.newDir)) * INV_PI;
    }
}

#endif // VOXEL_COMMON_HLSLI
