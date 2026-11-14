// ============================================================================
//  voxel_pathtracing.hlsl — DXR 体素路径追踪着色器库（lib_6_3）
//
//  入口:
//    [raygeneration] VoxelPathTrace   — 等价于 SVO_PathTracing.compute 的 CSMain + PathTrace
//    [miss]          VoxelMiss        — 主射线离开场景（missShaderIndex = 0）
//    [miss]          VoxelMissShadow  — 阴影射线未被遮挡 → 可见（missShaderIndex = 1）
//    [intersection]  VoxelIntersection— 8³ 砖 AABB 内 slab 求交 + 无栈 DDA
//    [closesthit]    VoxelClosestHit  — 写 16B payload（t / 材质 / 入射面）
//
//  设计要点（与 compute 路径的差异，均在代码中标注）:
//    1. 加速结构改用硬件 BVH：primitive = 非空砖 AABB，遍历由 RT core 完成，
//       彻底消除 SVOStackItem stack[64] 的 local memory 溢出（问题 I2）
//    2. 弹射循环放在 raygen（不递归），PSO 只需 MaxTraceRecursionDepth = 1
//    3. 全部几何量在 **grid 空间** 表达（AABB 端点即整数），方向向量为单位长度，
//       故 TraceRay 的 t 参数 = grid 空间距离；scale=1 时与 compute 路径完全一致
//    4. 自相交用 RayDesc.TMin = 1e-3（grid 单位）+ 出射点 0.01 偏移，
//       替代 compute 版的 skipVoxel 参数（问题：两者不同源，需逐帧对拍验证）
// ============================================================================

#include "voxel_common.hlsli"

// ============================================================================
//  首命中信息（与 SVO_PathTracing.compute L59-L66 一致；DLSS-RR 引导面输出）
// ============================================================================
struct FirstHitInfo
{
    float3 worldPos;        // MV/深度
    float3 normal;          // 世界空间法线（RR）
    float3 diffuseAlbedo;   // RR
    float3 specularAlbedo;  // RR
    float  roughness;       // RR
};

// ============================================================================
//  入射面 → 表面法线（0=+X 1=-X 2=+Y 3=-Y 4=+Z 5=-Z）
// ============================================================================
float3 FaceNormal(uint face)
{
    if (face == 0u) return float3( 1.0f,  0.0f,  0.0f);
    if (face == 1u) return float3(-1.0f,  0.0f,  0.0f);
    if (face == 2u) return float3( 0.0f,  1.0f,  0.0f);
    if (face == 3u) return float3( 0.0f, -1.0f,  0.0f);
    if (face == 4u) return float3( 0.0f,  0.0f,  1.0f);
    return float3( 0.0f,  0.0f, -1.0f);
}

// ============================================================================
//  主射线：grid 空间求交（替代 TraverseSVO_Stack）
//  返回材质索引；未命中返回 -1
// ============================================================================
int TraceSceneGrid(float3 roGrid, float3 rdGrid, float tMin, float tMax,
                   out float hitT, out uint hitFace)
{
    hitT = -1.0f;
    hitFace = 0u;

    // 防御：NaN/Inf 的 origin/direction 会让 DXR 硬件 BVH 遍历进入未定义状态，
    // 直接触发 DEVICE_HUNG（设备被移除、整个编辑器进程崩溃，且没有 C# 异常可捕）。
    // 上游一旦出现退化射线（零长方向向量、AABB 退化为点等），宁可当作未命中。
    if (any(isnan(roGrid)) || any(isinf(roGrid)) ||
        any(isnan(rdGrid)) || any(isinf(rdGrid))) return -1;

    RayDesc ray;
    ray.Origin    = roGrid;
    ray.Direction = rdGrid;      // 单位长度 → t 即 grid 空间距离
    ray.TMin      = tMin;
    ray.TMax      = tMax;

    VoxelPayload payload;
    payload.t        = -1.0f;
    payload.matIndex = 0xFFFFFFFFu;
    payload.face     = 0u;
    payload._pad     = 0u;

    TraceRay(g_TLAS, RAY_FLAG_NONE, 0xFFu, 0u, 0u, 0u, ray, payload);

    hitT = payload.t;
    hitFace = payload.face;
    return (payload.matIndex == 0xFFFFFFFFu) ? -1 : (int)payload.matIndex;
}

// ============================================================================
//  阴影射线：命中即"被遮挡"，未命中由 VoxelMissShadow 置 payload.t = 1
//  仅要求二值可见性 → ACCEPT_FIRST_HIT_AND_END_SEARCH + SKIP_CLOSEST_HIT_SHADER
//  （DXR 标志名为 _AND_END_SEARCH，无 _AND_STOP 变体）
// ============================================================================
bool ShadowRayVisible(float3 originGrid, float3 dirGrid, float maxDistGrid)
{
    // 同 TraceSceneGrid：异常值一律当作“被遮挡”，既不会挂设备，也不会凭空引入亮斑
    if (any(isnan(originGrid)) || any(isinf(originGrid)) ||
        any(isnan(dirGrid))    || any(isinf(dirGrid))) return false;

    RayDesc ray;
    ray.Origin    = originGrid + dirGrid * 1e-3f;
    ray.Direction = dirGrid;
    ray.TMin      = 0.0f;
    ray.TMax      = (maxDistGrid < INFINITE * 0.5f) ? maxDistGrid : INFINITE;

    VoxelPayload payload;
    payload.t        = 0.0f;   // 默认遮挡；只有 miss 才改写成 1
    payload.matIndex = 0u;
    payload.face     = 0u;
    payload._pad     = 0u;

    TraceRay(g_TLAS,
             RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER,
             0xFFu, 0u, 0u, 1u, ray, payload);

    return payload.t > 0.5f;
}

// ============================================================================
//  路径追踪主循环（等价于 SVO_PathTracing.compute L989-L1154）
// ============================================================================
float3 PathTrace(float3 roWorld, float3 rdWorld, inout uint seed, int maxDepth,
                 out bool hitSky, out FirstHitInfo fi)
{
    hitSky = false;
    // 首射线未命中几何体：RR 引导字段取中性值
    fi.worldPos       = float3(0, 0, 0);
    fi.normal         = float3(0, 0, 1);
    fi.diffuseAlbedo  = float3(0, 0, 0);
    fi.specularAlbedo = float3(0, 0, 0);
    fi.roughness      = 1.0f;

    float3 radiance   = float3(0, 0, 0);
    float3 throughput = float3(1, 1, 1);
    bool   isInside   = false;
    float  lastSamplePdf = 1e10f;   // 首射线视为 DIRAC
    float3 lastNGrid     = float3(0, 1, 0);

    float  invScale = g_ToGrid.w;
    float  scale    = g_Params2.x;
    float3 rootMin  = g_ToGrid.xyz;

    float3 roGrid = (roWorld - rootMin) * invScale;
    float3 rdGrid = rdWorld;        // 均匀缩放不改变方向；|rdGrid| = 1 → t 为 grid 距离

    const float kRayTMin = 1e-3f;   // 自相交抑制（grid 单位）

    for (int depth = 0; depth < maxDepth; depth++)
    {
        bool firstRay = (depth == 0);

        // ---- 硬件 BVH 遍历（替代 SVO DDA）----
        float hitT;
        uint  hitFace;
        int matIndex = TraceSceneGrid(roGrid, rdGrid, kRayTMin, INFINITE, hitT, hitFace);

        // ---- 未命中 → 天空（含 MIS 权重）----
        if (matIndex < 0)
        {
            float3 skyCol = SampleSky(rdWorld);

            if (firstRay)
            {
                hitSky = true;
                return skyCol;  // 首射线无 MIS
            }

            // BSDF 弹射命中天空：用 MIS 平衡 NEE 与 BSDF 采样策略
            float envPdf = (g_SkyColor.w > 0.5f)
                ? EnvironmentLightPDF(lastNGrid, rdGrid) : 0.0f;
            float misWeight = (lastSamplePdf > 1e9f) ? 1.0f
                : lastSamplePdf / (lastSamplePdf + max(envPdf, 1e-10f));
            radiance += throughput * misWeight * skyCol;
            return radiance;
        }

        // ---- 命中点（grid）与法线 ----
        float3 hitPos   = roGrid + rdGrid * hitT;
        float3 hitNGrid = FaceNormal(hitFace);

        if (firstRay)
            fi.worldPos = hitPos * scale + rootMin;

        // ---- 读取完整材质 ----
        VoxelMaterial mat = ReadMaterial(matIndex);

        // ---- 首命中 GBuffer（RR 引导输入；材质→GBuffer 映射）----
        if (firstRay && g_Flags.x > 0.5f)
        {
            fi.normal = normalize(hitNGrid);
            if (abs(mat.type - 1.0f) < 0.5f)               // Metal
            {
                fi.diffuseAlbedo  = float3(0, 0, 0);
                fi.specularAlbedo = mat.color;
                fi.roughness      = mat.rough;
            }
            else if (abs(mat.type - 2.0f) < 0.5f)          // Glass
            {
                fi.diffuseAlbedo  = float3(0, 0, 0);
                fi.specularAlbedo = float3(0.04, 0.04, 0.04);
                fi.roughness      = 0.0f;
            }
            else if (abs(mat.type - 3.0f) < 0.5f)          // Emissive
            {
                fi.diffuseAlbedo  = float3(0, 0, 0);
                fi.specularAlbedo = float3(0, 0, 0);
                fi.roughness      = 1.0f;
            }
            else                                           // Diffuse / Cloud / Blend
            {
                fi.diffuseAlbedo  = mat.color;
                fi.specularAlbedo = mat.color * mat.spec;
                fi.roughness      = mat.rough;
            }
        }

        // ---- 自发光材质 ----
        if (abs(mat.type - 3.0f) < 0.5f)
        {
            radiance += throughput * pow(2.0f, mat.flux) * mat.color;
            return radiance;
        }

        // ======== NEE 直接光照估计 ========
        float3 directContrib = float3(0, 0, 0);
        bool isDeltaBSDF = (abs(mat.type - 2.0f) < 0.5f);  // Glass = delta，跳过 NEE

        // 档位 2 跳过 NEE：diag 是 raygen 的局部变量，这里直接读 cbuffer
        if (!isDeltaBSDF && (int)g_Flags.y != 2)
        {
            float3 NGrid  = normalize(hitNGrid);
            float3 woGrid = normalize(-rdGrid);

            DirectLight directLight;
            SampleLights(hitPos, NGrid, woGrid, seed, directLight);

            float ndotl = dot(NGrid, directLight.direction);
            bool nextEventValid = (ndotl > 0.0f) && (directLight.pdf > 0.0f);

            if (nextEventValid)
            {
                float bsdfPDF;
                float3 bsdfVal = EvalBSDF(mat, NGrid, woGrid, directLight.direction, bsdfPDF);

                if (any(bsdfVal > 0.0f) && bsdfPDF > 0.0f)
                {
                    // MIS balance heuristic
                    float misWeight = (directLight.distance >= INFINITE)
                        ? directLight.pdf / (directLight.pdf + bsdfPDF)
                        : 1.0f;

                    // 阴影射线最大距离：与 compute 版 L1115 完全一致，
                    // 直接沿用 DirectLight.distance（环境光/方向光 = INFINITE）
                    bool visible = ShadowRayVisible(hitPos, directLight.direction, directLight.distance);
                    if (visible)
                    {
                        directContrib = throughput
                            * directLight.radianceOverPdf
                            * misWeight * bsdfVal;
                    }
                }
            }
        }

        radiance += directContrib;

        // ---- BSDF 采样：确定下一弹射方向 ----
        BsdfSampleData bsdfData;
        SampleBSDF(mat, hitNGrid, rdGrid, isInside, seed, bsdfData);

        float3 newDirWorld = GridDirToWorld(bsdfData.newDir);
        throughput *= bsdfData.bsdf_over_pdf;
        lastSamplePdf = bsdfData.pdf;
        lastNGrid = normalize(hitNGrid);

        if (bsdfData.eventType == 1)
            break;

        // ---- 设置下一弹射线（0.01 grid 偏移 + TMin 双保险）----
        roGrid = hitPos + bsdfData.newDir * 0.01f;
        rdGrid = bsdfData.newDir;
        rdWorld = newDirWorld;

        // ---- 俄罗斯轮盘赌 ----
        float rrPcont = min(max(throughput.x, max(throughput.y, throughput.z)) + 0.001f, 0.95f);
        if (rand(seed) >= rrPcont)
            break;
        throughput /= rrPcont;
    }

    return radiance;
}

// ============================================================================
//  像素采样（含子像素抖动）—— 等价于 SVO_PathTracing.compute L1159-L1189
// ============================================================================
float3 SamplePixel(float2 samplePos, float2 jitter, float2 imageSize, inout uint seed,
                   int maxDepth, out bool hitSky, out FirstHitInfo fi)
{
    float2 clipCoords = (samplePos + jitter) / imageSize * 2.0 - 1.0;
    float4 farWorld = mul(g_InvVP, float4(clipCoords, 1.0, 1.0));
    farWorld /= farWorld.w;

    float3 rd = normalize(farWorld.xyz - g_CameraPos.xyz);
    float3 ro = g_CameraPos.xyz;

    float3 result = PathTrace(ro, rd, seed, maxDepth, hitSky, fi);

    // 萤火虫抑制：仅对命中几何体的路径钳制，纯天空射线不钳制
    // RR 模式放宽阈值：网络按原始重尾信号训练，过强钳制偏离训练分布
    const float ffThreshold = (g_Flags.x > 0.5f) ? 40.0f : FIREFLY_THRESHOLD;
    if (!hitSky)
    {
        float lum = dot(result, float3(0.2126, 0.7152, 0.0722));
        float fireflyMask = step(ffThreshold, lum);
        float clampedLum = lum * (1.0 - fireflyMask)
                         + (ffThreshold - ffThreshold * ffThreshold / (lum + 1e-6)) * fireflyMask;
        result *= clampedLum / max(lum, 1e-6);
    }

    return result;
}

// ============================================================================
//  Ray Generation — 等价于 SVO_PathTracing.compute 的 CSMain（L1194-L1283）
// ============================================================================
[shader("raygeneration")]
void VoxelPathTrace()
{
    uint2 pixel = DispatchRaysIndex().xy;
    uint2 dims  = DispatchRaysDimensions().xy;
    if (pixel.x >= dims.x || pixel.y >= dims.y) return;

    // 诊断档位（C++ 侧由 voxrt_diag.cfg 的 level= 写入 g_Flags.y，正常渲染恒为 0）
    //   档位 1：不调任何 TraceRay，直接写恒定品红。用来区分两种成因：
    //     不崩 → DispatchRays 通路（PSO / SBT / 描述符 / 根签名）本身是好的，
    //              问题在 BVH 遍历或 IS/CH；
    //     仍崩 → 问题在 DispatchRays 的配置层面，与 shader 逻辑无关。
    const int diag = (int)g_Flags.y;

    // 档位 4：上面读 g_Flags.y 已经是唯一做的事，立即返回、不碰任何 UAV。
    //   与档位 1 构成二分：4 不崩而 1 崩 → UAV 写入问题；4 也崩 → CBV 读取问题
    if (diag == 4) return;

    if (diag == 1)
    {
        g_Color[pixel] = float4(1.0f, 0.0f, 1.0f, 1.0f);
        if (g_Params1.w > 0.5f)
        {
            g_MotionVector[pixel] = float2(0.0f, 0.0f);
            g_Depth[pixel]        = 1.0f;
            if (g_Flags.x > 0.5f)
            {
                g_DiffuseAlbedo[pixel]   = float4(0.5f, 0.5f, 0.5f, 1.0f);
                g_SpecularAlbedo[pixel]  = float4(0.0f, 0.0f, 0.0f, 1.0f);
                g_NormalRoughness[pixel] = float4(0.0f, 0.0f, 1.0f, 1.0f);
            }
        }
        return;
    }

    // 档位 5：只写颜色 UAV（u0），完全不碰引导面 u1..u5。
    //   与档位 1 二分：5 不崩而 1 崩 → 问题在引导面写入；5 也崩 → 在颜色 UAV 写入
    if (diag == 5)
    {
        g_Color[pixel] = float4(0.0f, 1.0f, 0.0f, 1.0f);
        return;
    }

    // 档位 6：只写引导面 u1/u2，不写颜色 UAV。档位 5 崩时用它反证到底是哪一侧。
    if (diag == 6)
    {
        if (g_Params1.w > 0.5f)
        {
            g_MotionVector[pixel] = float2(0.0f, 0.0f);
            g_Depth[pixel]        = 1.0f;
        }
        return;
    }

    // 档位 10+n（n = 0..5）：写颜色 + **前 n 张引导面**，逐张二分。
    //   10 = 只写颜色（等价于 5，但保留 5 原语义以便比对）
    //   11 = +MV   12 = +Depth   13 = +Diffuse   14 = +Specular   15 = +NormalRough
    //   用途：不重编就能把“写入挂死”定位到具体哪一张 UAV。
    if (diag >= 10 && diag <= 15)
    {
        g_Color[pixel] = float4(0.0f, 1.0f, 0.0f, 1.0f);
        const int n = diag - 10;
        if (g_Params1.w > 0.5f)
        {
            if (n >= 1) g_MotionVector[pixel] = float2(0.0f, 0.0f);
            if (n >= 2) g_Depth[pixel]        = 1.0f;
            if (g_Flags.x > 0.5f)
            {
                if (n >= 3) g_DiffuseAlbedo[pixel]   = float4(0.5f, 0.5f, 0.5f, 1.0f);
                if (n >= 4) g_SpecularAlbedo[pixel]  = float4(0.0f, 0.0f, 0.0f, 1.0f);
                if (n >= 5) g_NormalRoughness[pixel] = float4(0.0f, 0.0f, 1.0f, 1.0f);
            }
        }
        return;
    }

    float2 imageSize = float2(dims);
    float2 samplePos = (float2)pixel;

    uint frameIndex = (uint)g_Params1.y;
    int  maxDepth   = (int)g_Params1.x;
    bool writeGuides = g_Params1.w > 0.5f;
    bool reset       = g_Params1.z > 0.5f;

    // 档位 2：只发一条主射线（弹射止于 1），配合 PathTrace 里被跳过的 NEE，
    // 每条像素恰好 1 次 TraceRay → 单独验证硬件 BVH 遍历 + Intersection/ClosestHit
    if (diag == 2) maxDepth = 1;

    // 随机种子（基于像素位置 + 帧号）
    uint seed = (pixel.x * 1973u + pixel.y * 9277u + frameIndex * 26699u) | 1u;

    // 子像素抖动：DLSS 路径采样像素中心（抖动由 C# 侧的 jittered 投影承担）
    float2 jitter;
    if (writeGuides || frameIndex == 0u)
        jitter = float2(0.5f, 0.5f);
    else
        jitter = float2(rand(seed), rand(seed));

    bool hitSky;
    FirstHitInfo fi;
    float3 color = SamplePixel(samplePos, jitter, imageSize, seed, maxDepth, hitSky, fi);

    // ---- DLSS 引导面输出（MV / 深度 / RR 三张）----
    if (writeGuides)
    {
        if (hitSky)
        {
            // 天空像素：MV=0、深度=远平面、引导面中性值（fi 入口已初始化）
            g_MotionVector[pixel] = float2(0.0f, 0.0f);
            g_Depth[pixel]        = 1.0f;
            if (g_Flags.x > 0.5f)
            {
                g_DiffuseAlbedo[pixel]   = float4(fi.diffuseAlbedo, 1.0f);
                g_SpecularAlbedo[pixel]  = float4(fi.specularAlbedo, 1.0f);
                g_NormalRoughness[pixel] = float4(fi.normal, fi.roughness);
            }
            g_Color[pixel] = float4(color, 1.0);
            return;
        }

        float4 curClip  = mul(g_CurVP,  float4(fi.worldPos, 1.0));
        float4 prevClip = mul(g_PrevVP, float4(fi.worldPos, 1.0));

        // NDC → UV（与 SamplePixel 的 clipCoords 映射约定一致）
        float2 curUV  = curClip.xy  / max(curClip.w,  1e-6f) * 0.5 + 0.5;
        float2 prevUV = prevClip.xy / max(prevClip.w, 1e-6f) * 0.5 + 0.5;

        // DLSS 约定：MV 指向上一帧位置（像素单位）
        g_MotionVector[pixel] = (prevUV - curUV) * imageSize;

        // hardware depth：近=0 远=1（Unity D3D12 非反转深度）
        g_Depth[pixel] = saturate(curClip.z / max(curClip.w, 1e-6f));

        if (g_Flags.x > 0.5f)
        {
            g_DiffuseAlbedo[pixel]   = float4(fi.diffuseAlbedo, 1.0f);
            g_SpecularAlbedo[pixel]  = float4(fi.specularAlbedo, 1.0f);
            g_NormalRoughness[pixel] = float4(fi.normal, fi.roughness);
        }

        // DLSS 路径不做时域累积：单帧 1spp 直接输出
        g_Color[pixel] = float4(color, 1.0);
        return;
    }

    // ---- 时域累积（回退路径：DLSS 不可用时）----
    if (reset || frameIndex == 0u)
    {
        g_Color[pixel] = float4(color, 1.0);
    }
    else
    {
        float4 old = g_Color[pixel];
        float totalSamples = (float)(frameIndex + 1u);
        float3 accumulated = (old.rgb * (totalSamples - 1.0f) + color) / totalSamples;
        g_Color[pixel] = float4(accumulated, 1.0);
    }
}

// ============================================================================
//  Miss — 主射线离开场景（missShaderIndex = 0）
//  天空辐射由 raygen 自行 SampleSky 计算，这里只标记状态
// ============================================================================
[shader("miss")]
void VoxelMiss(inout VoxelPayload payload)
{
    payload.t        = -1.0f;
    payload.matIndex = 0xFFFFFFFFu;
    payload.face     = 0u;
}

// ============================================================================
//  Miss — 阴影射线未被遮挡 → 可见（missShaderIndex = 1）
//  命中时 closest-hit 被 SKIP_CLOSEST_HIT_SHADER 跳过，payload.t 保持 0 = 遮挡
// ============================================================================
[shader("miss")]
void VoxelMissShadow(inout VoxelPayload payload)
{
    payload.t = 1.0f;
    payload.matIndex = 0u;
    payload.face = 0u;
}

// ============================================================================
//  Intersection — 8³ 砖 AABB 求交 + 无栈 DDA
//
//  AABB 在 grid 空间构建（整数端点），射线也已在 grid 空间（单位方向）。
//  两层快速路径:
//    1. 实心砖（512 格全满）：一次 slab 求交直接 ReportHit，零 DDA
//    2. 非实心砖：Amanatides-Woo DDA，≤ 24 步（8³ 沿任意方向最多穿 22 格）
//  未命中时直接 return（不调用 IgnoreHit —— 语义等价且更省）
// ============================================================================
[shader("intersection")]
void VoxelIntersection()
{
    uint2 chunk = g_ChunkTable[InstanceID()];
    uint  brickIdx = chunk.x + (uint)PrimitiveIndex();
    BrickRecord b = g_Bricks[brickIdx];

    // 实例变换为 identity → ObjectRayOrigin/Direction 已是 grid 空间（raygen 传入的
    // RayDesc 就是 grid 空间），此处**不可**再套一次 toGrid 变换
    float3 ro = ObjectRayOrigin();
    float3 rd = ObjectRayDirection();   // 单位长度 → t 即 grid 距离

    float3 bmin = float3(b.ox, b.oy, b.oz);
    float3 bmax = bmin + (float)VOXRT_BRICK_SIZE;

    // ---- slab 求交 ----
    float3 inv = 1.0 / rd;
    float3 t0 = (bmin - ro) * inv;
    float3 t1 = (bmax - ro) * inv;
    float3 tsm = min(t0, t1);
    float3 tbg = max(t0, t1);

    float tEnter = max(max(tsm.x, tsm.y), tsm.z);
    float tExit  = min(min(tbg.x, tbg.y), tbg.z);
    if (tEnter > tExit || tExit < 0.0) return;
    tEnter = max(tEnter, 0.0);

    // 进入面：tEnter 由哪条轴决定，方向决定正负
    uint face;
    if (tEnter == tsm.x)      face = (rd.x > 0.0) ? 1u : 0u;
    else if (tEnter == tsm.y) face = (rd.y > 0.0) ? 3u : 2u;
    else                      face = (rd.z > 0.0) ? 5u : 4u;

    // ---- 快速路径：实心砖 ----
    if ((b.flags & VOXRT_BRICK_FLAG_SOLID) != 0u)
    {
        VoxelAttributes attr;
        // 实心 + 单材质：closest-hit 直接用 meta，根本不需要格索引
        // 实心 + 材质不唯一：必须在**这里**把精确格索引算好交给 closest-hit
        uint solidBit = 0u;
        if ((b.flags & VOXRT_BRICK_FLAG_UNIFORM) == 0u)
        {
            // 命中点正好落在格边界平面上，直接 floor() 会因浮点误差偏到相邻格（±1 格）。
            // 沿进入面法线的反方向推 1e-3 格（格边长 = 1 grid 单位，1e-3 远大于浮点误差），
            // 保证严格落在命中格内部。
            float3 pin = (ro + rd * tEnter) - FaceNormal(face) * 1e-3f;
            int ex = clamp((int)floor(pin.x) - b.ox, 0, VOXRT_BRICK_SIZE - 1);
            int ey = clamp((int)floor(pin.y) - b.oy, 0, VOXRT_BRICK_SIZE - 1);
            int ez = clamp((int)floor(pin.z) - b.oz, 0, VOXRT_BRICK_SIZE - 1);
            solidBit = (uint)((ez << 6) | (ey << 3) | ex);
        }
        attr.data = uint2(face, solidBit);
        ReportHit(tEnter, 0u, attr);
        return;
    }

    // ---- 无栈 DDA（Amanatides-Woo）----
    float3 p = ro + rd * tEnter;

    // 起步格同样不能直接 floor(p)：入口点就在格边界平面上，
    // 起步格偏 ±1 会读到错误的占据位（假命中 / 漏命中）。
    // 同样沿进入面法线反推 1e-3 格，使点严格处于命中格内部；
    // 下面计算 tnx/tny/tnz 仍用未偏移的 p（它在边界平面上，与 far* 求差正是正确步长）。
    float3 pin = p - FaceNormal(face) * 1e-3f;

    int cx = clamp((int)floor(pin.x) - b.ox, 0, VOXRT_BRICK_SIZE - 1);
    int cy = clamp((int)floor(pin.y) - b.oy, 0, VOXRT_BRICK_SIZE - 1);
    int cz = clamp((int)floor(pin.z) - b.oz, 0, VOXRT_BRICK_SIZE - 1);

    int sx = (rd.x > 0.0) ? 1 : ((rd.x < 0.0) ? -1 : 0);
    int sy = (rd.y > 0.0) ? 1 : ((rd.y < 0.0) ? -1 : 0);
    int sz = (rd.z > 0.0) ? 1 : ((rd.z < 0.0) ? -1 : 0);

    // 每跨一格所需的 t 增量
    float3 tDelta;
    tDelta.x = (rd.x != 0.0) ? abs(1.0 / rd.x) : INFINITE;
    tDelta.y = (rd.y != 0.0) ? abs(1.0 / rd.y) : INFINITE;
    tDelta.z = (rd.z != 0.0) ? abs(1.0 / rd.z) : INFINITE;

    // 到当前格各轴远端的 t
    float farX = (float)(b.ox + cx + (sx > 0 ? 1 : 0));
    float farY = (float)(b.oy + cy + (sy > 0 ? 1 : 0));
    float farZ = (float)(b.oz + cz + (sz > 0 ? 1 : 0));

    float tnx = (sx != 0) ? (tEnter + (farX - p.x) / rd.x) : INFINITE;
    float tny = (sy != 0) ? (tEnter + (farY - p.y) / rd.y) : INFINITE;
    float tnz = (sz != 0) ? (tEnter + (farZ - p.z) / rd.z) : INFINITE;

    float tCur = tEnter;

    [loop]
    for (uint i = 0u; i < VOXRT_DDA_MAX_STEPS; i++)
    {
        uint bit = (uint)((cz << 6) | (cy << 3) | cx);
        if ((b.occ[bit >> 5] & (1u << (bit & 31u))) != 0u)
        {
            VoxelAttributes attr;
            attr.data = uint2(face, bit);
            ReportHit(max(tCur, 0.0), 0u, attr);
            return;
        }

        // 选最小 tNext 的轴前进一格
        float tStep;
        if (tnx <= tny && tnx <= tnz)
        {
            tStep = tnx;
            if (tStep > tExit) return;
            cx += sx;
            if (cx < 0 || cx >= VOXRT_BRICK_SIZE) return;
            face = (sx > 0) ? 1u : 0u;
            tnx += tDelta.x;
        }
        else if (tny <= tnz)
        {
            tStep = tny;
            if (tStep > tExit) return;
            cy += sy;
            if (cy < 0 || cy >= VOXRT_BRICK_SIZE) return;
            face = (sy > 0) ? 3u : 2u;
            tny += tDelta.y;
        }
        else
        {
            tStep = tnz;
            if (tStep > tExit) return;
            cz += sz;
            if (cz < 0 || cz >= VOXRT_BRICK_SIZE) return;
            face = (sz > 0) ? 5u : 4u;
            tnz += tDelta.z;
        }

        tCur = tStep;
    }
}

// ============================================================================
//  Closest Hit — 只写 16B payload，材质查表在这里完成（一次）
// ============================================================================
[shader("closesthit")]
void VoxelClosestHit(inout VoxelPayload payload, in VoxelAttributes attr)
{
    uint2 chunk = g_ChunkTable[InstanceID()];
    uint  brickIdx = chunk.x + (uint)PrimitiveIndex();
    BrickRecord b = g_Bricks[brickIdx];

    float t = RayTCurrent();
    payload.t    = t;
    payload.face = attr.data.x;

    if ((b.flags & VOXRT_BRICK_FLAG_UNIFORM) != 0u)
    {
        payload.matIndex = b.meta;
        return;
    }

    // 非均匀砖：格索引取 intersection 阶段用整数 DDA 算出的精确 bit（attr.data.y）。
    // ⚠ 这里**不可**用 ObjectRayOrigin + ObjectRayDirection * t 用浮点反推命中格：
    //   ReportHit 的 t 是进入面的参数，命中点恰好落在整格边界平面上，
    //   floor() 在浮点误差下会偏 ±1 格（约一半命中会偏），读到邻格的材质，
    //   表现为表面成片的颜色/材质串色，且随视角闪动。
    payload.matIndex = g_VoxelCells[b.cellBase + attr.data.y];
}
