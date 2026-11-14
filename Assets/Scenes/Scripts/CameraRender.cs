using System;
using System.Collections.Generic;
using System.IO;
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

    [Header("Path Tracing Backend")]
    [Tooltip("DXR：8³ 砖块 AABB + Intersection Shader 无栈 DDA（需 VoxRTPlugin.dll + voxel_pathtracing.dxil）；\n" +
             "Compute：SVO_PathTracing.compute（原实现，冻结保留，用于 A/B 对拍与回退）；\n" +
             "Auto：优先 DXR，不可用自动回退 Compute")]
    public PathTracingBackend pathTracingBackend = PathTracingBackend.Auto;

    [Tooltip("DXR 渲染时关闭 Unity 自身场景渲染（cullingMask=0 + 关闭相机 Skybox），省掉无用的场景 Pass；" +
             "组件禁用或切回 Compute 时自动恢复")]
    public bool suppressUnitySceneRender = true;

    /// <summary>PathTracing 后端选择。</summary>
    public enum PathTracingBackend { Auto, DXR, Compute }

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

    // ---- DXR 后端（VoxRTPlugin.dll）----
    private DXRPathTracer dxr;
    private bool dxrWarmupDone;              // 预热已完成（或已判定无需预热）

    // 被原生插件引用过的 RT：延迟释放（原因见 RetireAndClear 上方注释）
    private readonly List<RenderTexture> retiredRTs = new List<RenderTexture>();
    private readonly List<int> retiredRTFrames = new List<int>();
    private bool sceneRenderSuppressed;      // Unity 场景渲染当前是否被抑制
    private int  camSavedCullingMask;
    private CameraClearFlags camSavedClearFlags;
    private Skybox camSkybox;

    // 方向光缓存（替代每帧 FindObjectsOfType<Light>()；60 帧或 ResetAccumulation 后失效）
    private Vector3 cachedSunDir = Vector3.up;
    private Vector3 cachedSunColor = Vector3.zero;
    private bool sunCacheValid;
    private int  sunCacheAge;
    private const int SunCacheFrames = 60;

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

        // ---- DXR 后端：新建状态机 + 订阅体素删除事件（SVOBuilder 仅追加，无订阅者时零开销）----
        dxr = new DXRPathTracer();
        SVOBuilder.VoxelRemoved += OnVoxelRemoved;

        // DXR 预热：不等用户切到 PathTracing 才开始推 INIT/BUILD_SCENE（那样头两帧必然回退 compute）。
        // 纯 Compute 后端不需要预热，跳过以省掉 BLAS/TLAS 的内存与构建时间。
        dxrWarmupDone = false;   // 新状态机 = 重新预热（每帧在 OnPreRender 里推进）
    }

    void OnDisable()
    {
        // 先恢复 Unity 侧场景渲染（否则禁用后相机依旧是空场景）
        SetUnitySceneSuppressed(false);

        SVOBuilder.VoxelRemoved -= OnVoxelRemoved;
        if (dxr != null) { dxr.Shutdown(); dxr = null; }

        ReleaseDLSS();
        ReleaseRTs();
        ReleaseRetiredRTsImmediate();   // 组件已禁用，不会再有新命令引用这些 RT
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
        FlushRetiredRTs();

        // 预热必须在渲染上下文里推进（原因见 AdvanceDXRWarmup 注释），不能放协程
        AdvanceDXRWarmup();

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

        // DXR 后端：标记场景需要重建（插件未就绪/不可用时静默忽略，不影响 svoBuilt）
        if (dxr != null) dxr.RequestSceneRebuild();
    }

    // ============================================================
    //  DXR 预热：在进入 PathTracing 模式之前把插件推到 READY
    //
    //  INIT / BUILD_SCENE 都是「发事件 → 下一帧才读到结果」的异步操作，而两者都与相机
    //  分辨率无关（RTInitData 只有 dxilPath；BUILD_SCENE 只吃体素数据）。若等用户切到
    //  PathTracing 才开始推进，前两帧必然回退 compute → 切场景瞬间画面会跳一下。
    //  这里提前把这两步推完，切过去时第 1 帧就是 DXR。
    //
    //  ⚠ 只能在 OnPreRender 里推进，不能改成协程（Update 阶段）：
    //    插件用 IUnityGraphicsD3D12::ExecuteCommandList 提交自己的命令列表，Unity 要求它
    //    挂在**当前帧的渲染上下文**上，并返回该列表的帧围栏值。在 Update 阶段提交
    //    （Graphics.ExecuteCommandBuffer 在协程里）会让围栏记账错位：插件拿到的 fenceValue
    //    无效 → 下一轮 BeginCmdList 误判槽空闲、Reset 仍在飞行的 allocator
    //    → 延迟到 present 时才爆 DXGI_ERROR_INVALID_CALL(0x887a0001)，设备被移除。
    //
    //  失败（DLL 缺失 / GPU 不支持）由 DXRPathTracer 自己置 Unavailable，此后不再推进，
    //  行为与未预热时一致：永久回退 compute。
    //
    //  ⚠ 必须避开 DLSS 的 feature 创建窗口（见下方 step 3 的守卫）：
    //    NGX 的 CreateFeature 在内部一次性创建几十 MB 共享资源并往它拿到的命令列表里录命令，
    //    实测与 DXR 的 INIT/BUILD_SCENE 命令列表撞在同一个 GPU 窗口时，会在
    //    NVSDK_NGX_CreateFeature 内部直接触发设备移除（present 887a0005 → 887a0001）。
    //    预热的作用是把这两步提前，而不是提前到 DLSS 初始化中间去。
    // ============================================================
    void AdvanceDXRWarmup()
    {
        if (dxrWarmupDone) return;
        if (pathTracingBackend == PathTracingBackend.Compute) { dxrWarmupDone = true; return; }
        if (dxr == null || dxr.IsUnavailable || cam == null) { dxrWarmupDone = true; return; }

        // 1. 等 SVO 建好：SVOBuilder.VoxelWorldSize（= sScale）在那之前不是有效值，
        //    提前 BUILD_SCENE 会用错的体素尺寸建 BLAS。
        if (!svoBuilt) return;

        // 2. 等体素数据就绪（TryBuildSVO 已保证非空，这里只兜底 provider 尚未 Awake 的情况）
        var provider = VoxDataProvider.Instance;
        if (provider == null || provider.voxelElements.Count == 0) return;

        // 3. PathTracing 模式下给 DLSS 让路：feature 未就绪（含尚未 INIT / 正在 CREATE）时不推进。
        //    PathTracing 分支里 AdvanceDLSSState 跑在 DispatchDXRPathTracing 之前，若预热在这里插队，
        //    DXR 的 INIT/BUILD_SCENE 就会与 NGX CreateFeature 挤在同一帧 → 设备移除（见上方注释）。
        //    RayTracing 模式完全不走 DLSS 分支，无需等待。
        if (debugView == DebugView.PathTracing && enableDLSS)
        {
            int dlssNow = SafeGetDLSSState();
            if (dlssNow >= 0 && dlssNow != DLSSInterop.StateFeatureReady) return;
        }

        // 4. INIT → BUILD_SCENE：每帧推进一步（状态机内部保证每步只发一次事件）
        if (!dxr.TryInitialize(cam.pixelWidth, cam.pixelHeight)) return;
        if (!dxr.TryBuildScene(provider.voxelElements, provider.materialElements,
                               SVOBuilder.VoxelWorldSize)) return;

        dxrWarmupDone = true;
        Debug.Log("[CameraRender] DXR 预热完成：进入 PathTracing 时第 1 帧即走 DXR");
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

    // ============================================================
    //  插件引用 RT 的延迟释放
    //
    //  VoxRTPlugin 的 RENDER 与 DLSSPlugin 的 EVALUATE 都把 RT 的原生指针（GetNativeTexturePtr）
    //  交给原生侧，由插件的命令列表在 GPU 上访问。Unity 的生命周期跟踪不覆盖这条路径：
    //  一旦 C# 立刻 Release()，上一帧仍在 GPU 上执行的命令列表就会引用已释放的纹理
    //  → present 报 887a0005，紧接着 Device removed reason 887a0001（DXGI_ERROR_INVALID_CALL）。
    //
    //  典型触发：DLSS 首次进入 FeatureReady 时内部渲染分辨率从全分辨率降到档位尺寸
    //  （1920x1080 → 640x360），pathTracingRT 随之重建，而 DXR 上一帧刚往旧 RT 写过。
    //
    //  插件最多同时有 4 条命令列表在飞（VoxRTPlugin.CmdSlot 四槽自旋等帧围栏），这里留 2 倍余量。
    // ============================================================
    private const int RetiredRTFrames = 8;

    /// <summary>把 RT 移入延迟释放队列并清空托管引用；当帧不做任何释放动作。</summary>
    void RetireAndClear(ref RenderTexture rt)
    {
        if (rt == null) return;
        retiredRTs.Add(rt);
        retiredRTFrames.Add(RetiredRTFrames);
        rt = null;
    }

    /// <summary>推进延迟释放队列：倒计时归零才真正 Release + Destroy。每帧调一次。</summary>
    void FlushRetiredRTs()
    {
        for (int i = retiredRTs.Count - 1; i >= 0; i--)
        {
            if (--retiredRTFrames[i] > 0) continue;

            var rt = retiredRTs[i];
            retiredRTs.RemoveAt(i);
            retiredRTFrames.RemoveAt(i);

            if (rt == null) continue;
            rt.Release();
            if (Application.isPlaying) Destroy(rt);
            else DestroyImmediate(rt);
        }
    }

    /// <summary>无视倒计时立即释放队列（仅在组件禁用/销毁时需要）。</summary>
    void ReleaseRetiredRTsImmediate()
    {
        for (int i = 0; i < retiredRTFrames.Count; i++) retiredRTFrames[i] = 0;
        FlushRetiredRTs();
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

            // 诊断开关（cfg present=1）：强制显示 PT 原始输出，绕开 DLSS。
            // 目的是把「黑屏」拆成两个互斥的成因：DXR 根本没写进 pathTracingRT，
            // 还是 DXR 写了但 NGX 的重建输出（dlssOutputRT）是空的。
            if (csPresentOverride != 0 && pathTracingRT != null)
                presentRT = pathTracingRT;

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

    // ============================================================
    //  运行期诊断开关（可在运行中切换，不必重编/重启）
    //
    //  配置：<Project>/Assets/Plugins/x86_64/voxrt_diag.cfg（与原生插件共用同一文件）
    //      present=1  强制显示 pathTracingRT（绕开 DLSS，看 1spp 原始输出）
    //      present=0  按 DLSS 状态自动选择（默认）
    //
    //  ⚠ 这里**不**读 backend：pathTracingBackend 只由 Inspector 决定。
    //    （曾经用 cfg 的 backend= 覆写它，导致 Inspector 上手工选的 Compute
    //      每次 Play 都被改回 Auto。）
    // ============================================================
    private static int csPresentOverride = 0;
    private int csCfgTick = 0;

    void ReadCsDiagCfg()
    {
        csPresentOverride = 0;
        try
        {
            string path = Path.Combine(Application.dataPath, "Plugins/x86_64/voxrt_diag.cfg");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                string t = line.Trim();
                if (!t.StartsWith("present=", StringComparison.Ordinal)) continue;
                int v;
                if (int.TryParse(t.Substring(8).Trim(), out v)) csPresentOverride = v;
            }
        }
        catch (Exception) { /* 读失败按默认处理 */ }
    }

    /// <summary>
    /// 重置时域累积与 DLSS 历史（体素删除等场景变化后调用，消除残影）。
    /// 时域回退路径：帧计数归零重新累积；DLSS 路径：下一帧 evaluate 带 reset 丢历史。
    /// </summary>
    public void ResetAccumulation()
    {
        pathTracingFrameCount = 0;
        dlssNeedsReset = true;
        sunCacheAge = SunCacheFrames;   // 下一次取光照参数时强制重查（光照可能已变化）
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

    /// <summary>
    /// 在 OnPreRender 中调度路径追踪，渲染到 pathTracingRT。
    /// 后端分派：DXR 优先（Auto/DXR），任一步失败自动回退到下方的 SVO_PathTracing.compute 实现。
    /// 共享前置（DLSS 状态机 / RT 尺寸 / jitter / 光照采集 / 累积帧计数）两后端完全一致。
    /// </summary>
    void DispatchSVOPathTracingPre()
    {
        if (cam == null) return;

        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;

        // compute 后端需要 CSMain；DXR 后端不依赖计算着色器（未拖入 svoPathTracing 也能跑）
        bool dxrEligible = pathTracingBackend != PathTracingBackend.Compute;
        int kernel = (svoPathTracing != null) ? svoPathTracing.FindKernel("CSMain") : -1;
        if (!dxrEligible)
        {
            if (svoPathTracing == null) return;
            if (kernel < 0)
            {
                Debug.LogError("[PathTracing] 计算着色器找不到 CSMain kernel");
                return;
            }
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
            // ⚠ 不能立即 Release：DXR 的 RENDER 与 NGX 的 EVALUATE 都通过原生指针跨帧引用这张 RT，
            //   上一帧仍在 GPU 上执行的命令列表会引用已释放资源 → present 887a0005 + Device removed 887a0001
            RetireAndClear(ref pathTracingRT);
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
        // ⚠ 引导面 RT 的原生指针必须非零：为 0 时 C++ 侧无法把它当 UAV 绑定，
        //   旧实现会 fallback 到颜色 RT，使 RWTexture2D<float2> 绑到 RGBA32F 上。
        //   普通 D3D12 调试层不校验 shader 级格式匹配，不报错但硬件直接 DEVICE_HUNG。
        if (useDLSS && (motionVectorRT == null || motionVectorRT.GetNativeTexturePtr() == IntPtr.Zero ||
                        depthRT == null || depthRT.GetNativeTexturePtr() == IntPtr.Zero))
        {
            Debug.LogWarning("[DXR] 引导面 RT 原生指针为空 → 本帧回退非 DLSS 路径");
            useRR = useSR = useDLSS = false;
        }
        if (useRR && (diffuseAlbedoRT == null || diffuseAlbedoRT.GetNativeTexturePtr() == IntPtr.Zero ||
                      specularAlbedoRT == null || specularAlbedoRT.GetNativeTexturePtr() == IntPtr.Zero ||
                      normalRoughnessRT == null || normalRoughnessRT.GetNativeTexturePtr() == IntPtr.Zero))
        {
            Debug.LogWarning("[DXR] RR 引导面 RT 原生指针为空 → 本帧关闭 RR 引导面写入");
            useRR = false;
        }

        // 检测方向光（缓存：每 60 帧或被 ResetAccumulation 失效后重查，避免每帧 FindObjectsOfType）
        Vector3 sunDir, sunColor;
        bool hasSun;
        GetSunParameters(out sunDir, out sunColor, out hasSun);

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

        // 每 15 帧重读一次 cfg（首帧必读）
        if (csCfgTick <= 0) ReadCsDiagCfg();
        csCfgTick = (csCfgTick + 1) % 15;

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

        // ---- DXR 后端：共享前置已全部就绪，单条 CB 内完成 [UPDATE_VOXELS]+[RENDER]+[DLSS evaluate] ----
        if (dxrEligible && DispatchDXRPathTracing(renderW, renderH, useDLSS, useRR,
                jx, jy, projMatrix, currentProj, sunDir, sunColor, hasSun))
        {
            return;
        }

        // 走到这里 = DXR 不可用（或档位为 Compute）→ 恢复 Unity 场景渲染并回退 compute 实现
        SetUnitySceneSuppressed(false);
        if (svoPathTracing == null || kernel < 0) return;

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
    //  DXR 后端（VoxRTPlugin.dll）
    //  原则：任何一步失败都返回 false → 调用方自动回退 compute，不抛异常不崩溃
    // ============================================================

    /// <summary>
    /// DXR 后端单帧提交：把 [UPDATE_VOXELS] + [RENDER] + DLSS evaluate 合并到同一条 CommandBuffer。
    /// 返回 true = 本帧已由 DXR 渲染；false = 调用方回退 compute 分支。
    /// 参数全部来自 DispatchSVOPathTracingPre 已算好的共享前置状态（保证两后端输入逐字段一致，方便对拍）。
    /// </summary>
    bool DispatchDXRPathTracing(int renderW, int renderH, bool useDLSS, bool useRR,
                                float jx, float jy, Matrix4x4 jitteredProj, Matrix4x4 plainProj,
                                Vector3 sunDir, Vector3 sunColor, bool hasSun)
    {
        if (dxr == null || dxr.IsUnavailable || pathTracingRT == null) return false;

        // ---- 初始化 / 场景构建状态机（各自只发一次事件，当帧返回 false 回退 compute）----
        if (!dxr.TryInitialize(cam.pixelWidth, cam.pixelHeight)) return false;
        var provider = VoxDataProvider.Instance;
        if (provider == null) return false;
        if (!dxr.TryBuildScene(provider.voxelElements, provider.materialElements, SVOBuilder.VoxelWorldSize))
            return false;

        // ---- 天空盒（与 compute 分支同一取法；cubemap 不可用时插件内部回退程序化天空）----
        Vector3 skyColor = new Vector3(1.5f, 2.0f, 3.0f); // HDR 回退天空色，与 compute 分支保持一致
        Cubemap skyCubemap = null;
        var skybox = cam.GetComponent<Skybox>();
        if (skybox != null && skybox.material != null && skybox.material.HasProperty("_Tex"))
            skyCubemap = skybox.material.GetTexture("_Tex") as Cubemap;
        if (skyCubemap == null && RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Tex"))
            skyCubemap = RenderSettings.skybox.GetTexture("_Tex") as Cubemap;

        Matrix4x4 viewToClip = GL.GetGPUProjectionMatrix(jitteredProj, false);
        Matrix4x4 vp = viewToClip * cam.worldToCameraMatrix;

        var p = new DXRPathTracer.RenderParams
        {
            width = renderW,
            height = renderH,
            maxDepth = maxBounceDepth,
            frameIndex = pathTracingFrameCount,
            reset = pathTracingFrameCount == 0,
            // SR/RR 都需要 MV + 深度；RR 额外写三张引导面
            guideMask = (useDLSS ? DXRInterop.GuideMotionDepth : 0) | (useRR ? DXRInterop.GuideRR : 0),
            invVP = vp.inverse,
            curVP = vp,
            prevVP = prevJitteredVP,
            cameraPos = cam.transform.position,
            sunDir = sunDir,
            sunColor = sunColor,
            hasSun = hasSun,
            skyColor = skyColor,
            skyCubemap = skyCubemap,
            colorRT = pathTracingRT,
            mvRT = useDLSS ? motionVectorRT : null,
            depthRT = useDLSS ? depthRT : null,
            diffuseAlbedoRT = useRR ? diffuseAlbedoRT : null,
            specularAlbedoRT = useRR ? specularAlbedoRT : null,
            normalRoughnessRT = useRR ? normalRoughnessRT : null,
            jitterX = jx,
            jitterY = jy,
        };

        var cb = new CommandBuffer { name = "VoxRT PathTracing" };
        bool submitted;
        try
        {
            submitted = dxr.SubmitRender(cb, p);
        }
        catch (Exception ex)
        {
            cb.Dispose();
            Debug.LogWarning("[DXRPathTracer] SubmitRender 异常: " + ex.Message);
            return false;
        }

        if (!submitted)
        {
            cb.Dispose();
            return false;
        }

        // ---- DLSS evaluate：严格追加在同一 CB 内、DXR 之后 ----
        // RR 的 W2V/V2C 必须不含抖动（抖动由 InJitterOffset 单独传入），与 compute 分支同约定
        if (useDLSS)
            IssueDLSSEvaluate(renderW, renderH, jx, jy, GL.GetGPUProjectionMatrix(plainProj, false), cb);

        Graphics.ExecuteCommandBuffer(cb);
        cb.Dispose();

        SetUnitySceneSuppressed(true);

        pathTracingFrameCount++;
        prevJitteredVP = vp;
        return true;
    }

    /// <summary>
    /// 方向光参数：指向光源的方向 + 颜色×强度（与原先 FindObjectsOfType 的结果语义一致）。
    /// 缓存 <see cref="SunCacheFrames"/> 帧；ResetAccumulation 会把缓存立刻置为过期。
    /// </summary>
    void GetSunParameters(out Vector3 dir, out Vector3 color, out bool has)
    {
        if (sunCacheValid && sunCacheAge < SunCacheFrames)
        {
            sunCacheAge++;
        }
        else
        {
            sunCacheValid = false;
            sunCacheAge = 0;
            var lights = FindObjectsOfType<Light>();
            foreach (var l in lights)
            {
                if (l.type == LightType.Directional && l.isActiveAndEnabled)
                {
                    cachedSunDir = -l.transform.forward;          // 指向光源
                    cachedSunColor = (Vector4)l.color * l.intensity;
                    sunCacheValid = true;
                    break;
                }
            }
        }
        dir = cachedSunDir;
        color = cachedSunColor;
        has = sunCacheValid;
    }

    /// <summary>
    /// DXR 渲染时抑制 Unity 自身的场景渲染（cullingMask=0 + 关闭相机 Skybox）。
    /// 幂等；suppressUnitySceneRender 关闭时永不抑制。只在 DXR 真正接管画面后才生效，
    /// 避免 compute 回退路径因 gBufferAlbedo 失效而黑屏。
    /// </summary>
    void SetUnitySceneSuppressed(bool suppress)
    {
        if (!suppressUnitySceneRender) suppress = false;
        if (sceneRenderSuppressed == suppress) return;
        sceneRenderSuppressed = suppress;

        if (cam == null) return;

        if (suppress)
        {
            if (camSkybox == null) camSkybox = cam.GetComponent<Skybox>();
            camSavedCullingMask = cam.cullingMask;
            camSavedClearFlags = cam.clearFlags;
            cam.cullingMask = 0;
            cam.clearFlags = CameraClearFlags.SolidColor;
            if (camSkybox != null) camSkybox.enabled = false;
        }
        else
        {
            cam.cullingMask = camSavedCullingMask;
            cam.clearFlags = camSavedClearFlags;
            if (camSkybox != null) camSkybox.enabled = true;
        }
    }

    /// <summary>
    /// 体素删除通知（供 VoxelEditor 等外部显式调用；SVOBuilder 内部已通过静态事件自动转发）。
    /// scenePos 为场景空间绝对整数体素坐标（= VoxelElement.worldPos）；matIndex &lt; 0 = 删除。
    /// </summary>
    public void NotifyVoxelRemoved(Vector3Int scenePos, int matIndex = -1)
    {
        if (dxr != null) dxr.NotifyVoxelRemoved(scenePos, matIndex);
    }

    /// <summary>SVOBuilder 体素删除事件转发（只累积增量，下一帧随 UPDATE_VOXELS 一次性发出）。</summary>
    void OnVoxelRemoved(Vector3Int scenePos, int matIndex)
    {
        if (dxr != null) dxr.NotifyVoxelRemoved(scenePos, matIndex);
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

            // 诊断：CreateFeature 是 NGX 里最重的一步（一次性建几十 MB 共享资源），崩溃排查需要它的时机
            Debug.Log($"[DLSS] 发起 CreateFeature：{create.inputWidth}x{create.inputHeight} → " +
                      $"{create.outputWidth}x{create.outputHeight}，模式={(dlssActiveMode == DLSSInterop.ModeRR ? "RR" : "SR")}");

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

    void IssueDLSSEvaluate(int renderW, int renderH, float jx, float jy, Matrix4x4 viewToClipNoJitter,
                           CommandBuffer externalCb = null)
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

        // externalCb != null：调用方把事件并入自己那条 CB（DXR 路径要求 DXR 与 evaluate 同 CB 严格保序）
        if (externalCb != null)
        {
            DLSSInterop.IssuePluginEvent(externalCb, DLSSInterop.EventEvaluate, dlssEvalBufs[dlssEvalBufIdx]);
            return;
        }

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

        // 这三张同样被 NGX 跨帧引用 → 延迟释放
        RetireAndClear(ref motionVectorRT);
        RetireAndClear(ref depthRT);
        RetireAndClear(ref dlssOutputRT);

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
            RetireAndClear(ref diffuseAlbedoRT);
            RetireAndClear(ref specularAlbedoRT);
            RetireAndClear(ref normalRoughnessRT);

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
