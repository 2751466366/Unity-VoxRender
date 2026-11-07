using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Built-in RP 两阶段延迟渲染管线（挂载到 Camera 上）。
///
/// 仅保留两种渲染模式：
///   RayTracing  — 两阶段 SVO 延迟渲染：
///     Stage 1 (GBuffer):   SVO_GBuffer.compute 遍历 SVO，写入 Albedo/Normal/WorldPos
///     Stage 2 (Lighting):  VoxRender_Lighting.compute 读 GBuffer + SVO 阴影射线 + 光照合成
///   PathTracing — SVO 路径追踪（多弹射、NEE、MIS）
///
/// 使用方式：
///   1. 将本组件挂到 Main Camera 上
///   2. 拖入 SVO_GBuffer.compute 和 VoxRender_Lighting shader
///   3. 场景中放一个 Directional Light
/// </summary>
// [ExecuteInEditMode]
public class CameraRender : MonoBehaviour
{
    [Header("Lighting Settings")]
    [Range(0f, 1f)] public float ambient = 0.2f;

    [Header("Debug")]
    [Tooltip("渲染模式")]
    public DebugView debugView = DebugView.RayTracing;

    [Tooltip("SVO GBuffer 计算着色器（拖入 SVO_GBuffer.compute）")]
    public ComputeShader svoGBuffer;

    [Tooltip("SVO 光照计算着色器（拖入 VoxRender_Lighting.compute）")]
    public ComputeShader svoLighting;

    [Tooltip("SVO 路径追踪计算着色器（拖入 SVO_PathTracing.compute）")]
    public ComputeShader svoPathTracing;

    [Header("Path Tracing")]
    [Range(1, 16)] public int maxBounceDepth = 4;

    [Header("DLSS")]
    [Tooltip("启用 DLSS 降噪（RR/SR 均可按档位超分）；插件不可用时自动回退时域累积")]
    public bool enableDLSS = true;
        [Tooltip("Auto = 优先 RR（不可用自动降级 SR）；RayReconstruction；SuperResolution")]
        public DLSSMode dlssMode = DLSSMode.Auto;
    
        [Tooltip("超分档位：DLAA=100% 内部渲染分辨率仅降噪；Performance≈50%；" +
                 "实际渲染分辨率由 DLSS_GetOptimalSettings 查询，非固定比例；" +
                 "UltraQuality 在部分驱动/模型（含 DLSSD）不受支持，会自动降一档")]
        public DLSSQualityMode dlssQuality = DLSSQualityMode.DLAA;

        public enum DLSSMode { Auto, RayReconstruction, SuperResolution }

        /// <summary>DLSS 质量档位（对应 NVSDK_NGX_PerfQuality_Value）。</summary>
        public enum DLSSQualityMode { DLAA, UltraQuality, Quality, Balanced, Performance, UltraPerformance }

    public enum DebugView
    {
        RayTracing,
        PathTracing,
    }

    private Camera cam;

    // SVO 构建状态
    private bool svoBuilt = false;

    // SVO 路径追踪 RT
    private RenderTexture pathTracingRT;
    private int pathTracingFrameCount = 0;
    private Matrix4x4 prevViewMatrix;
    private Matrix4x4 prevProjMatrix;

    // ---- DLSS 集成状态 ----
    private bool dlssInitAttempted = false;   // INIT 仅尝试一次（失败不重试，避免拖垮帧率）
    private bool dlssCreatePending = false;   // CREATE 事件已发出
    private bool dlssNeedsReset = false;      // 下一帧 evaluate 带 reset（丢弃历史）
    private bool dlssFailedLogged = false;    // 失败日志只打一次
    private int  dlssState = DLSSInterop.StateNotInitialized;
    private int  haltonIndex = 0;
    private Matrix4x4 prevJitteredVP = Matrix4x4.identity;
    private RenderTexture motionVectorRT;     // RGFloat：像素空间 MV
    private RenderTexture depthRT;            // RFloat：hardware depth
    private RenderTexture dlssOutputRT;       // ARGBFloat：DLSS 输出
    private IntPtr dlssSearchPathPtr = IntPtr.Zero;  // 持久持有（NGX 可能保留指针）
    private IntPtr dlssAppDataPtr    = IntPtr.Zero;
    private IntPtr dlssInitDataPtr   = IntPtr.Zero;
    private IntPtr dlssCreateDataPtr = IntPtr.Zero;
    private readonly IntPtr[] dlssEvalBufs = new IntPtr[2]; // 双缓冲交替
    private int dlssEvalBufIdx = 0;
    private int  dlssActiveMode = DLSSInterop.ModeSR; // 实际模式（CREATE 前由 ResolveDLSSMode 解析）
    private int  dlssRenderWidth  = 0;                // 内部渲染分辨率（档位决定，0 = 未解析）
    private int  dlssRenderHeight = 0;
    private int  dlssCreatedQuality = int.MinValue;   // 当前 feature 创建时的档位（变化 → 重建）
    private int  dlssCreatedOutW = 0;                 // 当前 feature 创建时的输出分辨率
    private int  dlssCreatedOutH = 0;
    private bool dlssOptimalFailLogged = false;       // GetOptimalSettings 失败只提示一次
    private int  dlssResolvedQuality = int.MinValue;  // 实际生效档位（驱动不支持请求档位时降档）
    private int  dlssResolvedForRequested = int.MinValue; // 上述结果的缓存键：请求档位
    private int  dlssResolvedForOutW = 0;             // 上述结果的缓存键：输出宽
    private int  dlssResolvedForOutH = 0;             // 上述结果的缓存键：输出高
    private int  dlssDegradeLoggedQuality = int.MinValue; // 已提示过的降档结果（避免刷屏）
    private readonly IntPtr[] dlssMatrixBufs = new IntPtr[2]; // RR 矩阵双缓冲（每块 32 float：W2V+V2C）
    private int dlssMatrixBufIdx = 0;
    private RenderTexture diffuseAlbedoRT;    // RR GBuffer: ARGBHalf
    private RenderTexture specularAlbedoRT;
    private RenderTexture normalRoughnessRT;  // xyz=世界法线, w=roughness

    // SVO G-Buffer RenderTextures（由 SVO_GBuffer.compute 写入）
    private RenderTexture gBufferAlbedo;    // RT0: ARGB32
    private RenderTexture gBufferNormal;    // RT1: ARGBHalf
    private RenderTexture gBufferWorldPos;  // RT2: ARGBHalf

    // SVO Lighting 输出 RT
    private RenderTexture lightingRT;

    // DLSS keyword 变体（SVO_PathTracing.compute 的 multi_compile_local）
    private const string DlssOnKeyword = "DLSS_ON";
    private const string DlssRrKeyword = "DLSS_RR";

    // Shader property IDs
    private static readonly int AlbedoTexID   = Shader.PropertyToID("_AlbedoTex");
    private static readonly int NormalTexID   = Shader.PropertyToID("_NormalTex");
    private static readonly int WorldPosTexID = Shader.PropertyToID("_WorldPosTex");
    private static readonly int AmbientID     = Shader.PropertyToID("_Ambient");
    private static readonly int LightDirID    = Shader.PropertyToID("_LightDir");
    private static readonly int LightColorID  = Shader.PropertyToID("_LightColor");
    private static readonly int SkyboxTexID   = Shader.PropertyToID("_SkyboxTex");
    private static readonly int InverseVPID   = Shader.PropertyToID("_InverseVP");

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogError("[CameraRender] 必须挂载到 Camera 上");
            return;
        }

        // 组件重新启用时允许 DLSS 重新初始化（OnDisable 已发 Shutdown）
        dlssInitAttempted = false;
        dlssFailedLogged = false;
    }

    void OnDisable()
    {
        ReleaseDLSS();
        ReleaseRTs();
        SVOBuilder.Release();
        svoBuilt = false;

        if (lightingRT != null)
        {
            lightingRT.Release();
            if (Application.isPlaying) Destroy(lightingRT);
            else DestroyImmediate(lightingRT);
            lightingRT = null;
        }

        if (pathTracingRT != null)
        {
            pathTracingRT.Release();
            if (Application.isPlaying) Destroy(pathTracingRT);
            else DestroyImmediate(pathTracingRT);
            pathTracingRT = null;
        }
    }

    void OnPreRender()
    {
        EnsureRTs();
        TryBuildSVO();

        if (debugView == DebugView.RayTracing)
        {
            if (svoBuilt)
                DispatchSVOGBuffer();
        }
        else if (debugView == DebugView.PathTracing)
        {
            if (svoBuilt)
                DispatchSVOPathTracingPre();
        }
    }

    /// <summary>检查是否需要构建 SVO：VoxDataProvider 有已加载资产且尚未构建。</summary>
    void TryBuildSVO()
    {
        if (svoBuilt) return;
        var provider = VoxDataProvider.Instance;
        if (provider == null) return;
        if (provider.voxelElements.Count == 0) return;

        float scale = 1f;
        // 使用第一个 VoxModel 的 scale（场景中可能有多个，取首个有效值）
        var models = FindObjectsOfType<VoxModel>();
        foreach (var m in models)
        {
            if (m.Handle >= 0) { scale = m.voxelScale; break; }
        }

        SVOBuilder.Build(provider.Assets, scale);
        svoBuilt = true;
    }

    /// <summary>确保 G-Buffer RT 和 Lighting RT 与屏幕分辨率一致，变化时重建。</summary>
    void EnsureRTs()
    {
        if (cam == null) return;
        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;

        if (gBufferAlbedo != null && gBufferAlbedo.width == w && gBufferAlbedo.height == h)
            return;

        ReleaseRTs();

        // SVO GBuffer RTs（由 compute shader 写入，不需要深度缓冲）
        gBufferAlbedo = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
        gBufferAlbedo.name = "SVO_GBuffer_Albedo";
        gBufferAlbedo.filterMode = FilterMode.Point;
        gBufferAlbedo.enableRandomWrite = true;
        gBufferAlbedo.Create();

        gBufferNormal = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf);
        gBufferNormal.name = "SVO_GBuffer_Normal";
        gBufferNormal.filterMode = FilterMode.Point;
        gBufferNormal.enableRandomWrite = true;
        gBufferNormal.Create();

        gBufferWorldPos = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf);
        gBufferWorldPos.name = "SVO_GBuffer_WorldPos";
        gBufferWorldPos.filterMode = FilterMode.Point;
        gBufferWorldPos.enableRandomWrite = true;
        gBufferWorldPos.Create();

        // Lighting 输出 RT
        lightingRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat);
        lightingRT.name = "Lighting_Result";
        lightingRT.filterMode = FilterMode.Point;
        lightingRT.enableRandomWrite = true;
        lightingRT.Create();
    }

    /// <summary>释放所有 G-Buffer RT。</summary>
    void ReleaseRTs()
    {
        ReleaseRT(ref gBufferAlbedo);
        ReleaseRT(ref gBufferNormal);
        ReleaseRT(ref gBufferWorldPos);
    }

    static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        if (Application.isPlaying) Destroy(rt);
        else DestroyImmediate(rt);
        rt = null;
    }

    /// <summary>查找场景中第一个启用的 Directional Light，设置光照参数。</summary>
    void SetLightParameters()
    {
        Vector3 lightDir  = new Vector3(0.5f, 0.8f, 0.3f).normalized; // 默认方向
        Vector3 lightColor = new Vector3(1f, 1f, 1f);

        // 遍历所有 Light，找第一个 Directional
        var lights = FindObjectsOfType<Light>();
        foreach (var l in lights)
        {
            if (l.type == LightType.Directional && l.isActiveAndEnabled)
            {
                // 光照方向：指向光源（与 light.forward 相反）
                lightDir = -l.transform.forward;
                lightColor = (Vector4)l.color * l.intensity;
                break;
            }
        }

        if (svoLighting != null)
        {
            svoLighting.SetVector(LightDirID, lightDir);
            svoLighting.SetVector(LightColorID, lightColor);
            svoLighting.SetFloat(AmbientID, ambient);
        }
    }

    void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (gBufferAlbedo == null)
        {
            Graphics.Blit(src, dest);
            return;
        }

        if (debugView == DebugView.RayTracing)
        {
            // ===== Stage 2: Lighting（计算着色器） =====
            if (svoLighting == null || lightingRT == null)
            {
                Graphics.Blit(src, dest);
                return;
            }

            int kernel = svoLighting.FindKernel("CSMain");
            if (kernel < 0)
            {
                Debug.LogError("[Lighting] 计算着色器找不到 CSMain kernel");
                Graphics.Blit(src, dest);
                return;
            }

            int w = cam.pixelWidth;
            int h = cam.pixelHeight;

            // G-Buffer 纹理
            svoLighting.SetTexture(kernel, "_AlbedoTex", gBufferAlbedo);
            svoLighting.SetTexture(kernel, "_NormalTex", gBufferNormal);
            svoLighting.SetTexture(kernel, "_WorldPosTex", gBufferWorldPos);
            svoLighting.SetTexture(kernel, "_LightingResult", lightingRT);

            // 相机参数
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
            svoLighting.SetMatrix("_InverseVP", vp.inverse);
            svoLighting.SetVector("_CameraPos", cam.transform.position);

            // 光照参数
            SetLightParameters();

            // 天空盒
            Cubemap skyCubemap = null;
            var skybox = cam.GetComponent<Skybox>();
            if (skybox != null && skybox.material != null && skybox.material.HasProperty("_Tex"))
                skyCubemap = skybox.material.GetTexture("_Tex") as Cubemap;
            if (skyCubemap == null && RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Tex"))
                skyCubemap = RenderSettings.skybox.GetTexture("_Tex") as Cubemap;
            if (skyCubemap != null)
            {
                svoLighting.SetTexture(kernel, "_SkyboxTex", skyCubemap);
                svoLighting.SetFloat("_HasSkyCube", 1.0f);
            }
            else
            {
                svoLighting.SetFloat("_HasSkyCube", 0.0f);
            }
            svoLighting.SetVector("_SkyColor", new Vector3(0.15f, 0.25f, 0.85f));

            // 调度
            uint tgX, tgY, tgZ;
            svoLighting.GetKernelThreadGroupSizes(kernel, out tgX, out tgY, out tgZ);
            svoLighting.Dispatch(kernel, Mathf.CeilToInt(w / (float)tgX), Mathf.CeilToInt(h / (float)tgY), 1);

            Graphics.Blit(lightingRT, dest);
        }
        else if (debugView == DebugView.PathTracing)
        {
            // DLSS 就绪时展示重建输出；否则展示时域累积结果（回退）
            RenderTexture presentRT = (dlssState == DLSSInterop.StateFeatureReady && dlssOutputRT != null)
                ? dlssOutputRT : pathTracingRT;
            if (presentRT != null)
                Graphics.Blit(presentRT, dest);
            else
                Graphics.Blit(src, dest);
        }
        else
        {
            Graphics.Blit(src, dest);
        }
    }

    /// <summary>
    /// 重置时域累积与 DLSS 历史（体素删除等场景变化后调用，消除残影）。
    /// 时域回退路径：帧计数归零重新累积；DLSS 路径：下一帧 evaluate 带 reset 丢历史。
    /// </summary>
    public void ResetAccumulation()
    {
        pathTracingFrameCount = 0;
        dlssNeedsReset = true;
    }

    /// <summary>调度 SVO GBuffer 计算着色器（Stage 1：几何阶段）。</summary>
    void DispatchSVOGBuffer()
    {
        if (svoGBuffer == null || cam == null) return;

        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;

        int kernel = svoGBuffer.FindKernel("CSMain");
        if (kernel < 0)
        {
            Debug.LogError("[SVO_GBuffer] 计算着色器找不到 CSMain kernel");
            return;
        }

        try
        {
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
            svoGBuffer.SetMatrix("_InverseVP", vp.inverse);
            svoGBuffer.SetVector("_CameraPos", cam.transform.position);
            svoGBuffer.SetTexture(kernel, "_GBufferAlbedo", gBufferAlbedo);
            svoGBuffer.SetTexture(kernel, "_GBufferNormal", gBufferNormal);
            svoGBuffer.SetTexture(kernel, "_GBufferWorldPos", gBufferWorldPos);

            uint tgX, tgY, tgZ;
            svoGBuffer.GetKernelThreadGroupSizes(kernel, out tgX, out tgY, out tgZ);
            svoGBuffer.Dispatch(kernel, Mathf.CeilToInt(w / (float)tgX), Mathf.CeilToInt(h / (float)tgY), 1);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[SVO_GBuffer] 计算着色器调度失败: {ex.Message}");
        }
    }

    /// <summary>在 OnPreRender 中调度 SVO 路径追踪计算着色器，渲染到 pathTracingRT。</summary>
    void DispatchSVOPathTracingPre()
    {
        if (svoPathTracing == null || cam == null) return;

        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;

        int kernel = svoPathTracing.FindKernel("CSMain");
        if (kernel < 0)
        {
            Debug.LogError("[PathTracing] 计算着色器找不到 CSMain kernel");
            return;
        }

        // ---- DLSS 状态机推进（INIT → CREATE → READY，失败自动回退时域累积）----
        // 必须先推进：档位决定的内部渲染分辨率决定 PT RT 尺寸
        AdvanceDLSSState(w, h);

        bool dlssReady = enableDLSS && dlssState == DLSSInterop.StateFeatureReady;
        bool useRR = dlssReady && dlssActiveMode == DLSSInterop.ModeRR;
        bool useSR = dlssReady && dlssActiveMode == DLSSInterop.ModeSR;
        bool useDLSS = useRR || useSR;

        // DLSS 路径按档位内部渲染分辨率调度；回退路径保持全分辨率
        int renderW = (useDLSS && dlssRenderWidth  > 0) ? dlssRenderWidth  : w;
        int renderH = (useDLSS && dlssRenderHeight > 0) ? dlssRenderHeight : h;

        // 创建或重建 PT RT（尺寸 = 内部渲染分辨率）
        bool resolutionChanged = false;
        if (pathTracingRT == null || pathTracingRT.width != renderW || pathTracingRT.height != renderH)
        {
            resolutionChanged = true;
            if (pathTracingRT != null) { pathTracingRT.Release(); Destroy(pathTracingRT); }
            pathTracingRT = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.ARGBFloat);
            pathTracingRT.enableRandomWrite = true;
            pathTracingRT.name = "PathTracing_RT";
            pathTracingRT.Create();
        }

        if (useDLSS && pathTracingRT.GetNativeTexturePtr() == IntPtr.Zero)
        {
            // 极端兜底：RT 未就绪 → 本帧跳过 evaluate（渲染尺寸仍为内部尺寸）
            useRR = useSR = useDLSS = false;
        }
        if (useDLSS) EnsureDLSSRTs(renderW, renderH, w, h);

        // 检测方向光
        Vector3 sunDir = Vector3.up;
        Vector3 sunColor = Vector3.zero;
        bool hasSun = false;
        var lights = FindObjectsOfType<Light>();
        foreach (var l in lights)
        {
            if (l.type == LightType.Directional && l.isActiveAndEnabled)
            {
                sunDir = -l.transform.forward;
                sunColor = ((Vector4)l.color * l.intensity);
                hasSun = true;
                break;
            }
        }

        // 检测相机是否移动（仅回退路径需要重置累积；DLSS 由 MV 自处理）
        Matrix4x4 currentView = cam.worldToCameraMatrix;
        Matrix4x4 currentProj = cam.projectionMatrix;
        bool cameraMoved = (pathTracingFrameCount > 0) &&
            (currentView != prevViewMatrix || currentProj != prevProjMatrix);

        if (!useDLSS && (cameraMoved || resolutionChanged))
        {
            pathTracingFrameCount = 0;
            // 清空 RT 以移除旧帧数据
            Graphics.SetRenderTarget(pathTracingRT);
            GL.Clear(false, true, Color.black);
        }

        prevViewMatrix = currentView;
        prevProjMatrix = currentProj;

        // ---- Halton(2,3) 抖动 + jittered 投影矩阵（DLSS 路径；颜色/MV/深度共用同一投影）----
        float jx = 0f, jy = 0f;
        Matrix4x4 projMatrix = cam.projectionMatrix;
        if (useDLSS)
        {
            int phase = haltonIndex % 8 + 1;
            jx = Halton(phase, 2) - 0.5f;
            jy = Halton(phase, 3) - 0.5f;
            projMatrix.m02 += 2f * jx / renderW;   // 像素空间 jitter → NDC（内部渲染分辨率）
            projMatrix.m12 += 2f * jy / renderH;
        }
        haltonIndex++;

        try
        {
            Matrix4x4 viewToClip = GL.GetGPUProjectionMatrix(projMatrix, false);
            Matrix4x4 vp = viewToClip * cam.worldToCameraMatrix;
            svoPathTracing.SetMatrix("_InverseVP", vp.inverse);
            svoPathTracing.SetMatrix("_CurVP", vp);
            svoPathTracing.SetMatrix("_PrevVP", prevJitteredVP);
            svoPathTracing.SetVector("_CameraPos", cam.transform.position);
            // DLSS 变体切换：未启用时变体不含 DLSS UAV，无绑定要求（消除 not set 警告）
            if (useRR) svoPathTracing.EnableKeyword(DlssRrKeyword);
            else       svoPathTracing.DisableKeyword(DlssRrKeyword);
            if (useSR) svoPathTracing.EnableKeyword(DlssOnKeyword);
            else       svoPathTracing.DisableKeyword(DlssOnKeyword);
            svoPathTracing.SetTexture(kernel, "_PathTracingResult", pathTracingRT);
            if (useDLSS)
            {
                svoPathTracing.SetTexture(kernel, "_MotionVectorRT", motionVectorRT);
                svoPathTracing.SetTexture(kernel, "_DepthRT", depthRT);
            }
            if (useRR)
            {
                svoPathTracing.SetTexture(kernel, "_DiffuseAlbedoRT", diffuseAlbedoRT);
                svoPathTracing.SetTexture(kernel, "_SpecularAlbedoRT", specularAlbedoRT);
                svoPathTracing.SetTexture(kernel, "_NormalRoughnessRT", normalRoughnessRT);
            }

            // 天空盒
            Vector3 skyColor = new Vector3(1.5f, 2.0f, 3.0f); // HDR 回退天空色（确保无 Cubemap 时仍有足够亮度）
            Cubemap skyCubemap = null;
            var skybox = cam.GetComponent<Skybox>();
            if (skybox != null && skybox.material != null && skybox.material.HasProperty("_Tex"))
                skyCubemap = skybox.material.GetTexture("_Tex") as Cubemap;
            if (skyCubemap == null && RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Tex"))
                skyCubemap = RenderSettings.skybox.GetTexture("_Tex") as Cubemap;
            if (skyCubemap != null)
            {
                svoPathTracing.SetTexture(kernel, "_SkyboxTex", skyCubemap);
                svoPathTracing.SetFloat("_HasSkyCube", 1.0f);
            }
            else
            {
                svoPathTracing.SetFloat("_HasSkyCube", 0.0f);
            }
            svoPathTracing.SetVector("_SkyColor", skyColor);

            // 方向光（太阳）
            svoPathTracing.SetVector("_SunDir", sunDir);
            svoPathTracing.SetVector("_SunColor", sunColor);
            svoPathTracing.SetFloat("_HasSun", hasSun ? 1.0f : 0.0f);

            // 路径追踪参数
            svoPathTracing.SetInt("_MaxDepth", maxBounceDepth);
            svoPathTracing.SetInt("_FrameCount", pathTracingFrameCount);

            uint tgX, tgY, tgZ;
            svoPathTracing.GetKernelThreadGroupSizes(kernel, out tgX, out tgY, out tgZ);
            svoPathTracing.Dispatch(kernel, Mathf.CeilToInt(renderW / (float)tgX), Mathf.CeilToInt(renderH / (float)tgY), 1);

            pathTracingFrameCount++;

            // ---- DLSS evaluate（渲染线程执行，严格位于 dispatch 之后）----
            // RR 的 W2V/V2C 必须不含抖动（NGX 约定 "All matrices should NOT contain temporal AA jitter offset"）：
            // 抖动已由 InJitterOffset 单独传入，若把抖动烘进 V2C，RR 内部反投影会逐帧错位 → 静态区域抖动
            if (useDLSS) IssueDLSSEvaluate(renderW, renderH, jx, jy, GL.GetGPUProjectionMatrix(currentProj, false));

            prevJitteredVP = vp;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[PathTracing] 计算着色器调度失败: {ex.Message}");
        }
    }

    // ============================================================
    //  DLSS 集成（NGX DLSS-SR / DLAA）
    //  原则：任何一层失败都只降级不崩溃，渲染照常走时域累积回退
    // ============================================================

    static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        while (index > 0)
        {
            f /= b;
            r += f * (index % b);
            index /= b;
        }
        return r;
    }

    int SafeGetDLSSState()
    {
        try { return DLSSInterop.DLSS_GetState(); }
        catch (Exception) { return DLSSInterop.StateInitFailed; }
    }

    /// <summary>推进 DLSS 状态机（INIT → CREATE → READY）；档位/输出分辨率变化时重建 feature。</summary>
    void AdvanceDLSSState(int outW, int outH)
    {
        if (!enableDLSS) return;

        // 1. 首次：发 INIT 事件（仅一次，失败不重试）
        if (!dlssInitAttempted)
        {
            dlssInitAttempted = true;
            TryInitDLSS();
            return; // 状态下一帧可见
        }

        dlssState = SafeGetDLSSState();

        // 2. 失败 → 提示一次，之后静默回退（useDLSS 判定已排除）
        if (dlssState < 0)
        {
            if (!dlssFailedLogged)
            {
                dlssFailedLogged = true;
                Debug.LogWarning($"[DLSS] 不可用(state={dlssState}): {DLSSInterop.GetLastMessageManaged()} → 回退时域累积");
            }
            return;
        }

        // 请求档位 → 实际生效档位 + 内部渲染分辨率（仅参数变化时重查，避免每帧 P/Invoke）
        int quality = CurrentPerfQuality();
        bool stateUsable = dlssState == DLSSInterop.StateInitSucceeded
                           || dlssState == DLSSInterop.StateFeatureReady;
        if (stateUsable &&
            (quality != dlssResolvedForRequested || outW != dlssResolvedForOutW || outH != dlssResolvedForOutH))
            UpdateInternalResolution(outW, outH);

        bool paramsChanged = dlssResolvedQuality != dlssCreatedQuality
                             || outW != dlssCreatedOutW || outH != dlssCreatedOutH;

        // 3. INIT 成功 → 解析模式 + 内部渲染分辨率（档位）→ 创建/重建 feature
        if (dlssState == DLSSInterop.StateInitSucceeded)
        {
            ResolveDLSSMode();

            if (!dlssCreatePending || paramsChanged)
            {
                dlssCreatePending = true;
                IssueDLSSCreate(outW, outH);
            }
            return;
        }

        // 4. READY 但档位/输出分辨率变化 → 释放 feature，下一帧按新参数重建
        if (dlssState == DLSSInterop.StateFeatureReady && paramsChanged)
        {
            dlssCreatePending = false;
            dlssNeedsReset = true;
            IssueDLSSRelease();
            dlssState = DLSSInterop.StateInitSucceeded; // 本帧按未就绪处理，避免释放后仍 evaluate
        }
    }

    void TryInitDLSS()
    {
        try
        {
            if (DLSSInterop.DLSS_GetRenderEventFunc() == IntPtr.Zero)
            {
                dlssState = DLSSInterop.StateInitFailed;
                Debug.LogWarning("[DLSS] DLSS_GetRenderEventFunc 返回空");
                return;
            }

            // NGX 可能保留路径指针 → 持久 HGlobal（ReleaseDLSS 释放）
            dlssSearchPathPtr = DLSSInterop.AllocWideString(Application.dataPath + "/Plugins/x86_64");
            dlssAppDataPtr    = DLSSInterop.AllocWideString(Application.temporaryCachePath);
            var init = new DLSSInterop.InitData
            {
                dllSearchPath = dlssSearchPathPtr,
                appDataPath   = dlssAppDataPtr,
                appId         = 0x00564F58UL, // "VOX"
            };
            dlssInitDataPtr = DLSSInterop.AllocStruct(ref init);

            var cb = new CommandBuffer { name = "DLSS Init" };
            DLSSInterop.IssuePluginEvent(cb, DLSSInterop.EventInit, dlssInitDataPtr);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Dispose();
        }
        catch (DllNotFoundException)        { dlssState = DLSSInterop.StateInitFailed; Debug.LogWarning("[DLSS] DLSSPlugin.dll 未找到 → 回退时域累积"); }
        catch (EntryPointNotFoundException) { dlssState = DLSSInterop.StateInitFailed; Debug.LogWarning("[DLSS] DLSSPlugin 导出符号不匹配 → 回退时域累积"); }
        catch (Exception ex)                { dlssState = DLSSInterop.StateInitFailed; Debug.LogWarning("[DLSS] 初始化异常: " + ex.Message); }
    }

    void IssueDLSSCreate(int outW, int outH)
    {
        // 用"实际生效档位"：UpdateInternalResolution 已按驱动支持情况降档
        //（如 DLSSD 明确拒绝 UltraQuality，若照原样 CREATE 会失败 → 整体回退时域累积）
        int perfQuality = dlssResolvedQuality != int.MinValue ? dlssResolvedQuality : CurrentPerfQuality();
        try
        {
            var create = new DLSSInterop.CreateData
            {
                // 输入 = 档位内部渲染分辨率；输出 = 屏幕分辨率（RR/SR 均支持任意档位）
                inputWidth   = (uint)Mathf.Max(dlssRenderWidth, 1),
                inputHeight  = (uint)Mathf.Max(dlssRenderHeight, 1),
                outputWidth  = (uint)outW,
                outputHeight = (uint)outH,
                perfQuality  = perfQuality,
                featureFlags = DLSSInterop.FlagIsHDR | DLSSInterop.FlagMVJittered,
                mode          = dlssActiveMode,
                roughnessMode = DLSSInterop.RoughnessPacked, // roughness 存 normals.w
            };
            if (dlssCreateDataPtr == IntPtr.Zero) dlssCreateDataPtr = DLSSInterop.AllocStruct(ref create);
            else DLSSInterop.WriteStruct(dlssCreateDataPtr, ref create);

            var cb = new CommandBuffer { name = "DLSS CreateFeature" };
            DLSSInterop.IssuePluginEvent(cb, DLSSInterop.EventCreateFeature, dlssCreateDataPtr);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Dispose();

            dlssCreatedQuality = perfQuality;
            dlssCreatedOutW = outW;
            dlssCreatedOutH = outH;
            dlssNeedsReset = true; // 创建后首帧丢弃历史
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[DLSS] CreateFeature 异常: " + ex.Message);
            // 记录尝试参数，避免异常后每帧重试刷屏（档位/分辨率再变时会重试）
            dlssCreatedQuality = perfQuality;
            dlssCreatedOutW = outW;
            dlssCreatedOutH = outH;
        }
    }

    void IssueDLSSEvaluate(int renderW, int renderH, float jx, float jy, Matrix4x4 viewToClipNoJitter)
    {
        // RR 矩阵：双缓冲交替写入持久块（渲染线程异步消费 float*，不能指向短命内存）
        IntPtr w2vPtr = IntPtr.Zero, v2cPtr = IntPtr.Zero;
        if (dlssActiveMode == DLSSInterop.ModeRR)
        {
            int midx = 1 - dlssMatrixBufIdx;
            if (dlssMatrixBufs[midx] == IntPtr.Zero)
                dlssMatrixBufs[midx] = Marshal.AllocHGlobal(32 * sizeof(float));
            float[] m = new float[32];
            FlattenMatrix(cam.worldToCameraMatrix, m, 0);   // WorldToView
            FlattenMatrix(viewToClipNoJitter, m, 16);       // ViewToClip（不含 jitter：jitter 由 InJitterOffset 单独传入）
            Marshal.Copy(m, 0, dlssMatrixBufs[midx], 32);
            dlssMatrixBufIdx = midx;
            w2vPtr = dlssMatrixBufs[midx];
            v2cPtr = new IntPtr(w2vPtr.ToInt64() + 16 * sizeof(float));
        }

        var eval = new DLSSInterop.EvalData
        {
            color         = pathTracingRT.GetNativeTexturePtr(),
            motionVectors = motionVectorRT.GetNativeTexturePtr(),
            depth         = depthRT.GetNativeTexturePtr(),
            output        = dlssOutputRT.GetNativeTexturePtr(),
            jitterX       = -jx,
            jitterY       = -jy,
            mvScaleX      = 1f,  // shader 已输出像素单位 MV
            mvScaleY      = 1f,
            subrectWidth  = (uint)renderW,   // 内部渲染尺寸（NGX 由此推导 render resolution）
            subrectHeight = (uint)renderH,
            reset         = dlssNeedsReset ? 1 : 0,
            // ---- RR 专用（SR 模式置空，插件侧忽略）----
            diffuseAlbedo  = (dlssActiveMode == DLSSInterop.ModeRR && diffuseAlbedoRT != null)  ? diffuseAlbedoRT.GetNativeTexturePtr()  : IntPtr.Zero,
            specularAlbedo = (dlssActiveMode == DLSSInterop.ModeRR && specularAlbedoRT != null) ? specularAlbedoRT.GetNativeTexturePtr() : IntPtr.Zero,
            normals        = (dlssActiveMode == DLSSInterop.ModeRR && normalRoughnessRT != null) ? normalRoughnessRT.GetNativeTexturePtr() : IntPtr.Zero,
            worldToViewMatrix = w2vPtr,
            viewToClipMatrix  = v2cPtr,
            preExposure          = 1.0f, // 无预曝光
            frameTimeDeltaInMsec = Mathf.Max(Time.deltaTime * 1000f, 0.1f),
        };
        dlssNeedsReset = false;

        // 双缓冲交替，渲染线程读取与下一帧主线程覆写不重叠（缓冲懒分配）
        int idx = 1 - dlssEvalBufIdx;
        if (dlssEvalBufs[idx] == IntPtr.Zero)
            dlssEvalBufs[idx] = DLSSInterop.AllocStruct(ref eval);
        else
            DLSSInterop.WriteStruct(dlssEvalBufs[idx], ref eval);
        dlssEvalBufIdx = idx;

        var cb = new CommandBuffer { name = "DLSS Evaluate" };
        DLSSInterop.IssuePluginEvent(cb, DLSSInterop.EventEvaluate, dlssEvalBufs[dlssEvalBufIdx]);
        Graphics.ExecuteCommandBuffer(cb);
        cb.Dispose();
    }

    /// <summary>确保 DLSS 附加 RT（MV/深度按内部渲染分辨率，输出按屏幕分辨率）。</summary>
    void EnsureDLSSRTs(int renderW, int renderH, int outW, int outH)
    {
        bool rrRTsReady = dlssActiveMode != DLSSInterop.ModeRR ||
                          (diffuseAlbedoRT != null && diffuseAlbedoRT.width == renderW && diffuseAlbedoRT.height == renderH);
        if (motionVectorRT != null && motionVectorRT.width == renderW && motionVectorRT.height == renderH &&
            depthRT != null && depthRT.width == renderW && depthRT.height == renderH &&
            dlssOutputRT != null && dlssOutputRT.width == outW && dlssOutputRT.height == outH &&
            rrRTsReady)
            return;

        ReleaseRT(ref motionVectorRT);
        ReleaseRT(ref depthRT);
        ReleaseRT(ref dlssOutputRT);

        // RGFloat(R32G32F)与 shader 的 RWTexture2D<float2> UAV 格式精确匹配
        motionVectorRT = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RGFloat);
        motionVectorRT.name = "DLSS_MotionVectors";
        motionVectorRT.filterMode = FilterMode.Point;
        motionVectorRT.enableRandomWrite = true;
        motionVectorRT.Create();

        depthRT = new RenderTexture(renderW, renderH, 0, RenderTextureFormat.RFloat);
        depthRT.name = "DLSS_Depth";
        depthRT.filterMode = FilterMode.Point;
        depthRT.enableRandomWrite = true;
        depthRT.Create();

        dlssOutputRT = new RenderTexture(outW, outH, 0, RenderTextureFormat.ARGBFloat);
        dlssOutputRT.name = "DLSS_Output";
        dlssOutputRT.filterMode = FilterMode.Point;
        dlssOutputRT.enableRandomWrite = true;
        dlssOutputRT.Create();

        dlssNeedsReset = true;

        // RR GBuffer 三张（惰性创建，SR 模式不占显存）
        if (dlssActiveMode == DLSSInterop.ModeRR &&
            (diffuseAlbedoRT == null || diffuseAlbedoRT.width != renderW || diffuseAlbedoRT.height != renderH))
        {
            ReleaseRT(ref diffuseAlbedoRT);
            ReleaseRT(ref specularAlbedoRT);
            ReleaseRT(ref normalRoughnessRT);

            diffuseAlbedoRT   = CreateDLSSRT(renderW, renderH, "DLSS_DiffuseAlbedo",  RenderTextureFormat.ARGBHalf);
            specularAlbedoRT  = CreateDLSSRT(renderW, renderH, "DLSS_SpecularAlbedo", RenderTextureFormat.ARGBHalf);
            normalRoughnessRT = CreateDLSSRT(renderW, renderH, "DLSS_NormalRoughness", RenderTextureFormat.ARGBHalf);
        }
    }

    static RenderTexture CreateDLSSRT(int w, int h, string name, RenderTextureFormat fmt)
    {
        var rt = new RenderTexture(w, h, 0, fmt);
        rt.name = name;
        rt.filterMode = FilterMode.Point;
        rt.enableRandomWrite = true;
        rt.Create();
        return rt;
    }

    /// <summary>把 Unity Matrix4x4 展平为 16 float（按内存序，UE FMatrix 同源行主序约定）。</summary>
    static void FlattenMatrix(Matrix4x4 m, float[] dst, int offset)
    {
        dst[offset + 0]  = m.m00; dst[offset + 1]  = m.m01; dst[offset + 2]  = m.m02; dst[offset + 3]  = m.m03;
        dst[offset + 4]  = m.m10; dst[offset + 5]  = m.m11; dst[offset + 6]  = m.m12; dst[offset + 7]  = m.m13;
        dst[offset + 8]  = m.m20; dst[offset + 9]  = m.m21; dst[offset + 10] = m.m22; dst[offset + 11] = m.m23;
        dst[offset + 12] = m.m30; dst[offset + 13] = m.m31; dst[offset + 14] = m.m32; dst[offset + 15] = m.m33;
    }

    /// <summary>CREATE 前解析实际模式：请求 RR 时先查 RR 可用性，不可用则用 SR（日志一次）。</summary>
    void ResolveDLSSMode()
    {
        int want = (dlssMode == DLSSMode.SuperResolution) ? DLSSInterop.ModeSR : DLSSInterop.ModeRR;
        if (want == DLSSInterop.ModeRR)
        {
            int av = DLSSInterop.DLSS_RRAvailable();
            if (av != 1)
            {
                want = DLSSInterop.ModeSR;
                Debug.LogWarning($"[DLSS] RR 不可用(DLSS_RRAvailable={av}，驱动过旧或非 RTX)→ 使用 SR");
            }
        }
        dlssActiveMode = want;
    }

    /// <summary>UI 档位 → NVSDK_NGX_PerfQuality_Value。</summary>
    int CurrentPerfQuality()
    {
        switch (dlssQuality)
        {
            case DLSSQualityMode.DLAA:             return DLSSInterop.QualityDLAA;
            case DLSSQualityMode.UltraQuality:     return DLSSInterop.QualityUltraQuality;
            case DLSSQualityMode.Quality:          return DLSSInterop.QualityMaxQuality;
            case DLSSQualityMode.Balanced:         return DLSSInterop.QualityBalanced;
            case DLSSQualityMode.Performance:      return DLSSInterop.QualityMaxPerf;
            case DLSSQualityMode.UltraPerformance: return DLSSInterop.QualityUltraPerf;
            default:                               return DLSSInterop.QualityDLAA;
        }
    }

    /// <summary>
    /// 查询档位对应的内部渲染分辨率（NGX optimal settings）。
    /// 驱动/模型可能不支持某些档位：DLSSD 对 UltraQuality 的 optimal 返回 0x0 且 CREATE 报
    /// "unsupported by this version of DLSSD" → 必须沿降级链换成受支持档位，否则整体回退时域累积。
    /// </summary>
    void UpdateInternalResolution(int outW, int outH)
    {
        int requested = CurrentPerfQuality();
        int q = requested;
        int rw = 0, rh = 0, rc = -1;
        bool resolved = false;

        for (int i = 0; i < 8; i++)
        {
            rw = rh = 0;
            try { rc = DLSSInterop.DLSS_GetOptimalSettings(outW, outH, q, out rw, out rh); }
            catch (Exception) { rc = -1; }

            if (rc == 0 && rw > 0 && rh > 0 && rw <= outW && rh <= outH) { resolved = true; break; }
            if (rc != 0) break;                        // 能力参数不可用 → 无法判断档位，直接全分辨率

            int next = NextLowerQuality(q);
            if (next == q) break;                      // 已到 DLAA 仍无效
            q = next;
        }

        dlssResolvedQuality      = resolved ? q : requested;
        dlssResolvedForRequested = requested;
        dlssResolvedForOutW      = outW;
        dlssResolvedForOutH      = outH;

        int newW = resolved ? rw : outW;
        int newH = resolved ? rh : outH;

        if (resolved && q != requested && q != dlssDegradeLoggedQuality)
        {
            dlssDegradeLoggedQuality = q;
            Debug.LogWarning($"[DLSS] 当前驱动/模型不支持档位 {QualityName(requested)}（optimal 尺寸无效）" +
                             $" → 自动降为 {QualityName(q)}");
        }
        if (!resolved && !dlssOptimalFailLogged)
        {
            dlssOptimalFailLogged = true;
            Debug.LogWarning($"[DLSS] GetOptimalSettings 失败(rc={rc}) → 回退全分辨率渲染");
        }

        if (newW != dlssRenderWidth || newH != dlssRenderHeight)
        {
            dlssRenderWidth  = newW;
            dlssRenderHeight = newH;
            Debug.Log($"[DLSS] 档位 {QualityName(dlssResolvedQuality)}: 输出 {outW}x{outH} → 内部渲染 {newW}x{newH}");
        }
    }

    /// <summary>档位降级链：请求档位不受支持时按此顺序回退（DLAA 为终点）。</summary>
    static int NextLowerQuality(int q)
    {
        switch (q)
        {
            case DLSSInterop.QualityUltraQuality: return DLSSInterop.QualityMaxQuality;
            case DLSSInterop.QualityMaxQuality:   return DLSSInterop.QualityBalanced;
            case DLSSInterop.QualityBalanced:     return DLSSInterop.QualityMaxPerf;
            case DLSSInterop.QualityMaxPerf:      return DLSSInterop.QualityUltraPerf;
            case DLSSInterop.QualityUltraPerf:    return DLSSInterop.QualityDLAA;
            default:                              return q;
        }
    }

    static string QualityName(int q)
    {
        switch (q)
        {
            case DLSSInterop.QualityDLAA:         return "DLAA";
            case DLSSInterop.QualityUltraQuality: return "UltraQuality";
            case DLSSInterop.QualityMaxQuality:   return "Quality";
            case DLSSInterop.QualityBalanced:     return "Balanced";
            case DLSSInterop.QualityMaxPerf:      return "Performance";
            case DLSSInterop.QualityUltraPerf:    return "UltraPerformance";
            default:                              return $"Quality({q})";
        }
    }

    /// <summary>释放当前 feature（档位/分辨率变化重建用）；下一帧状态回到 INIT_SUCCEEDED。</summary>
    void IssueDLSSRelease()
    {
        try
        {
            var cb = new CommandBuffer { name = "DLSS ReleaseFeature" };
            DLSSInterop.IssuePluginEvent(cb, DLSSInterop.EventReleaseFeature, IntPtr.Zero);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Dispose();
        }
        catch (Exception ex) { Debug.LogWarning("[DLSS] ReleaseFeature 异常: " + ex.Message); }
    }

    void ReleaseDLSS()
    {
        if (dlssInitAttempted)
        {
            try
            {
                var cb = new CommandBuffer { name = "DLSS Shutdown" };
                DLSSInterop.IssuePluginEvent(cb, DLSSInterop.EventShutdown, IntPtr.Zero);
                Graphics.ExecuteCommandBuffer(cb);
                cb.Dispose();
            }
            catch (Exception) { /* 卸载阶段尽力而为 */ }
        }
        FreeHGlobalRef(ref dlssInitDataPtr);
        FreeHGlobalRef(ref dlssSearchPathPtr);
        FreeHGlobalRef(ref dlssAppDataPtr);
        FreeHGlobalRef(ref dlssCreateDataPtr);
        for (int i = 0; i < dlssEvalBufs.Length; i++) FreeHGlobalRef(ref dlssEvalBufs[i]);
        for (int i = 0; i < dlssMatrixBufs.Length; i++) FreeHGlobalRef(ref dlssMatrixBufs[i]);

        ReleaseRT(ref motionVectorRT);
        ReleaseRT(ref depthRT);
        ReleaseRT(ref dlssOutputRT);
        ReleaseRT(ref diffuseAlbedoRT);
        ReleaseRT(ref specularAlbedoRT);
        ReleaseRT(ref normalRoughnessRT);

        dlssState = DLSSInterop.StateNotInitialized;
        dlssCreatePending = false;
        dlssNeedsReset = false;
        dlssRenderWidth = 0;
        dlssRenderHeight = 0;
        dlssCreatedQuality = int.MinValue;
        dlssCreatedOutW = 0;
        dlssCreatedOutH = 0;
        dlssResolvedQuality = int.MinValue;
        dlssResolvedForRequested = int.MinValue;
        dlssResolvedForOutW = 0;
        dlssResolvedForOutH = 0;
    }

    static void FreeHGlobalRef(ref IntPtr p)
    {
        if (p != IntPtr.Zero) { Marshal.FreeHGlobal(p); p = IntPtr.Zero; }
    }
}
