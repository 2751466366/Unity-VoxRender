using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Built-in RP 延迟渲染管线（挂载到 Camera 上）。
///
/// Pass 1 (G-Buffer): CommandBuffer 在 CameraEvent.BeforeImageEffects 执行，
///   将所有 VoxDataProvider 管理的 chunk 用 MRT 渲染到 albedo / normal / worldPos 三张 RT。
/// Pass 2 (Shadow):   预留，等 SVO 构建完成后在 G-Buffer 和 Lighting 之间插入。
/// Pass 3 (Lighting): OnRenderImage 中全屏 Blit，读取 G-Buffer 做 Lambert + ambient 合成。
///
/// 使用方式：
///   1. 将本组件挂到 Main Camera 上
///   2. VoxModel 的 chunk3DMaterial 使用 Unlit/VoxRender_GBuffer 材质
///   3. 场景中放一个 Directional Light
/// </summary>
// [ExecuteInEditMode]
public class CameraRender : MonoBehaviour
{
    [Header("Lighting Settings")]
    [Range(0f, 1f)] public float ambient = 0.2f;

    [Header("Debug")]
    [Tooltip("在 Game 视图中显示 G-Buffer 各通道而非最终光照结果")]
    public DebugView debugView = DebugView.Final;

    [Tooltip("SVO 射线遍历计算着色器（拖入 SVO_RayCast.compute）")]
    public ComputeShader svoRayCast;

    [Tooltip("SVO 光线追踪计算着色器（拖入 SVO_RayTracing.compute）")]
    public ComputeShader svoRayTracing;

    [Tooltip("SVO 路径追踪计算着色器（拖入 SVO_PathTracing.compute）")]
    public ComputeShader svoPathTracing;

    [Header("Path Tracing")]
    [Range(1, 16)] public int maxBounceDepth = 4;

    public enum DebugView
    {
        Final,
        Albedo,
        Normal,
        WorldPos,
        SVOVisual,
        RayTracing,
        PathTracing,
    }

    private Camera cam;
    private CommandBuffer gBufferCmd;
    private Material lightingMat;
    private Material blendMat;  // SVO 混合材质

    // SVO 构建状态
    private bool svoBuilt = false;

    // SVO 可视化 RT（OnPreRender 中渲染，OnRenderImage 中 Blit）
    private RenderTexture svoVisRT;

    // SVO 光线追踪 RT
    private RenderTexture rayTracingRT;

    // SVO 路径追踪 RT
    private RenderTexture pathTracingRT;
    private int pathTracingFrameCount = 0;
    private Matrix4x4 prevViewMatrix;
    private Matrix4x4 prevProjMatrix;

    // G-Buffer RenderTextures
    private RenderTexture gBufferAlbedo;    // RT0: RGBA32，深度 24-bit
    private RenderTexture gBufferNormal;    // RT1: ARGBHalf
    private RenderTexture gBufferWorldPos;  // RT2: ARGBHalf

    // Shader property IDs
    private static readonly int AlbedoTexID   = Shader.PropertyToID("_AlbedoTex");
    private static readonly int NormalTexID   = Shader.PropertyToID("_NormalTex");
    private static readonly int WorldPosTexID = Shader.PropertyToID("_WorldPosTex");
    private static readonly int AmbientID     = Shader.PropertyToID("_Ambient");
    private static readonly int LightDirID    = Shader.PropertyToID("_LightDir");
    private static readonly int LightColorID  = Shader.PropertyToID("_LightColor");
    private static readonly int SkyboxTexID   = Shader.PropertyToID("_SkyboxTex");
    private static readonly int InverseVPID   = Shader.PropertyToID("_InverseVP");
    private static readonly int SVOResultID   = Shader.PropertyToID("_SVOResult");

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogError("[CameraRender] 必须挂载到 Camera 上");
            return;
        }

        // 创建光照材质
        Shader lightingShader = Shader.Find("Hidden/VoxRender_Lighting");
        if (lightingShader == null)
        {
            Debug.LogError("[CameraRender] 未找到 Hidden/VoxRender_Lighting shader");
            return;
        }
        lightingMat = new Material(lightingShader);
        lightingMat.name = "DeferredLighting";

        // 创建 SVO 混合材质
        Shader blendShader = Shader.Find("Hidden/SVOBlend");
        if (blendShader != null)
        {
            blendMat = new Material(blendShader);
            blendMat.name = "SVOBlend";
        }

        // 创建 CommandBuffer（先移除旧的，防止重编译后重复添加）
        if (gBufferCmd != null)
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, gBufferCmd);

        gBufferCmd = new CommandBuffer { name = "Vox Deferred G-Buffer" };
        cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, gBufferCmd);
    }

    void OnDisable()
    {
        if (cam != null && gBufferCmd != null)
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, gBufferCmd);

        ReleaseRTs();
        SVOBuilder.Release();
        svoBuilt = false;

        if (svoVisRT != null)
        {
            svoVisRT.Release();
            if (Application.isPlaying) Destroy(svoVisRT);
            else DestroyImmediate(svoVisRT);
            svoVisRT = null;
        }

        if (rayTracingRT != null)
        {
            rayTracingRT.Release();
            if (Application.isPlaying) Destroy(rayTracingRT);
            else DestroyImmediate(rayTracingRT);
            rayTracingRT = null;
        }

        if (pathTracingRT != null)
        {
            pathTracingRT.Release();
            if (Application.isPlaying) Destroy(pathTracingRT);
            else DestroyImmediate(pathTracingRT);
            pathTracingRT = null;
        }

        if (lightingMat != null)
        {
            if (Application.isPlaying) Destroy(lightingMat);
            else DestroyImmediate(lightingMat);
            lightingMat = null;
        }

        if (blendMat != null)
        {
            if (Application.isPlaying) Destroy(blendMat);
            else DestroyImmediate(blendMat);
            blendMat = null;
        }
    }

    void OnPreRender()
    {
        EnsureRTs();
        TryBuildSVO();

        if (debugView == DebugView.RayTracing)
        {
            SetChunkRenderersEnabled(false);
            if (svoBuilt)
                DispatchSVORayTracingPre();
        }
        else if (debugView == DebugView.PathTracing)
        {
            SetChunkRenderersEnabled(false);
            if (svoBuilt)
                DispatchSVOPathTracingPre();
        }
        else
        {
            // 其他模式：恢复 chunk cube 渲染，走 GBuffer 管线
            SetChunkRenderersEnabled(true);
            RebuildCommandBuffer();

            if (debugView == DebugView.SVOVisual && svoBuilt)
                DispatchSVORayCastPre();
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

    /// <summary>确保 G-Buffer RT 与屏幕分辨率一致，变化时重建。</summary>
    void EnsureRTs()
    {
        if (cam == null) return;
        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;

        if (gBufferAlbedo != null && gBufferAlbedo.width == w && gBufferAlbedo.height == h)
            return;

        ReleaseRTs();

        // 第一张 RT 带 24-bit 深度，供所有 MRT 共享
        gBufferAlbedo = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        gBufferAlbedo.name = "GBuffer_Albedo";
        gBufferAlbedo.filterMode = FilterMode.Point;
        gBufferAlbedo.Create();

        gBufferNormal = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf);
        gBufferNormal.name = "GBuffer_Normal";
        gBufferNormal.filterMode = FilterMode.Point;
        gBufferNormal.Create();

        gBufferWorldPos = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf);
        gBufferWorldPos.name = "GBuffer_WorldPos";
        gBufferWorldPos.filterMode = FilterMode.Point;
        gBufferWorldPos.Create();
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

    /// <summary>每帧重建 CommandBuffer：设置 MRT、清除、绘制所有 chunk。</summary>
    void RebuildCommandBuffer()
    {
        if (gBufferCmd == null || gBufferAlbedo == null || gBufferNormal == null || gBufferWorldPos == null)
            return;

        gBufferCmd.Clear();

        // 设置 MRT（3 个颜色目标 + 共享深度）
        gBufferCmd.SetRenderTarget(
            new RenderTargetIdentifier[] { gBufferAlbedo, gBufferNormal, gBufferWorldPos },
            gBufferAlbedo
        );
        gBufferCmd.ClearRenderTarget(true, true, Color.clear);

        // 遍历 VoxDataProvider 中所有活跃的 chunk，用各自的材质绘制
        var provider = VoxDataProvider.Instance;
        if (provider == null) return;

        foreach (var asset in provider.Assets)
        {
            if (asset == null || !asset.isActive) continue;
            for (int i = 0; i < asset.chunks.Count; i++)
            {
                var chunk = asset.chunks[i];
                // TODO: 延迟渲染改为 SVO 输出 G-buffer 后移除 cube 依赖
                // var cube = chunk != null ? chunk.cube : null;
                var mat  = i < asset.materials.Count ? asset.materials[i] : null;
                if (mat == null) continue;
            }
        }
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

        lightingMat.SetVector(LightDirID, lightDir);
        lightingMat.SetVector(LightColorID, lightColor);
        lightingMat.SetFloat(AmbientID, ambient);
    }

    void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (lightingMat == null || gBufferAlbedo == null)
        {
            // 管线未就绪，直接输出
            Graphics.Blit(src, dest);
            return;
        }

        // G-Buffer 已由 CommandBuffer 在 BeforeImageEffects 渲染完毕

        switch (debugView)
        {
            case DebugView.Albedo:
                Graphics.Blit(gBufferAlbedo, dest);
                break;
            case DebugView.Normal:
                Graphics.Blit(gBufferNormal, dest);
                break;
            case DebugView.WorldPos:
                Graphics.Blit(gBufferWorldPos, dest);
                break;
            case DebugView.SVOVisual:
                if (svoVisRT != null && blendMat != null)
                {
                    // 1:1 混合：先渲染默认光照到临时 RT，再与 SVO 可视化混合
                    RenderTexture lightingRT = RenderTexture.GetTemporary(src.width, src.height, 0, src.format);
                    
                    // 渲染默认光照
                    SetLightParameters();
                    lightingMat.SetTexture(AlbedoTexID, gBufferAlbedo);
                    lightingMat.SetTexture(NormalTexID, gBufferNormal);
                    lightingMat.SetTexture(WorldPosTexID, gBufferWorldPos);

                    Cubemap sky2 = null;
                    var skybox2 = cam.GetComponent<Skybox>();
                    if (skybox2 != null && skybox2.material != null && skybox2.material.HasProperty("_Tex"))
                        sky2 = skybox2.material.GetTexture("_Tex") as Cubemap;
                    if (sky2 == null && RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Tex"))
                        sky2 = RenderSettings.skybox.GetTexture("_Tex") as Cubemap;
                    if (sky2 != null)
                        lightingMat.SetTexture(SkyboxTexID, sky2);

                    Matrix4x4 vp2 = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
                    lightingMat.SetMatrix(InverseVPID, vp2.inverse);

                    Graphics.Blit(null, lightingRT, lightingMat);
                    
                    // 混合 SVO 和光照
                    blendMat.SetTexture("_MainTex", svoVisRT);
                    blendMat.SetTexture("_LightingTex", lightingRT);
                    Graphics.Blit(null, dest, blendMat);
                    
                    RenderTexture.ReleaseTemporary(lightingRT);
                }
                else if (svoVisRT != null)
                    Graphics.Blit(svoVisRT, dest);
                else
                    Graphics.Blit(src, dest);
                break;
            case DebugView.RayTracing:
                if (rayTracingRT != null)
                    Graphics.Blit(rayTracingRT, dest);
                else
                    Graphics.Blit(src, dest);
                break;
            case DebugView.PathTracing:
                if (pathTracingRT != null)
                    Graphics.Blit(pathTracingRT, dest);
                else
                    Graphics.Blit(src, dest);
                break;
            default:
                // ===== Pass 3: 光照合成 =====
                SetLightParameters();
                lightingMat.SetTexture(AlbedoTexID, gBufferAlbedo);
                lightingMat.SetTexture(NormalTexID, gBufferNormal);
                lightingMat.SetTexture(WorldPosTexID, gBufferWorldPos);

                // Skybox Cubemap：优先从相机 Skybox 组件取，其次从 Lighting Settings 取
                // 只有 Skybox/Cubemap shader 有 _Tex 属性；Skybox/Procedural 等程序化 shader 没有 Cubemap 纹理，会跳过
                Cubemap sky = null;
                var skybox = cam.GetComponent<Skybox>();
                if (skybox != null && skybox.material != null && skybox.material.HasProperty("_Tex"))
                    sky = skybox.material.GetTexture("_Tex") as Cubemap;
                if (sky == null && RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Tex"))
                    sky = RenderSettings.skybox.GetTexture("_Tex") as Cubemap;
                if (sky != null)
                    lightingMat.SetTexture(SkyboxTexID, sky);

                // Inverse VP 矩阵：裁剪空间 → 世界空间，用于重建屏幕射线采样 Skybox
                Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
                lightingMat.SetMatrix(InverseVPID, vp.inverse);

                Graphics.Blit(null, dest, lightingMat);
                break;
        }
    }

    /// <summary>在 OnPreRender 中调度 SVO 射线遍历计算着色器，渲染到 svoVisRT。</summary>
    void DispatchSVORayCastPre()
    {
        if (svoRayCast == null || cam == null) return;

        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;

        int kernel = svoRayCast.FindKernel("CSMain");
        if (kernel < 0)
        {
            Debug.LogError("[SVOVis] 计算着色器找不到 CSMain kernel");
            return;
        }

        // 创建或重建 RT
        if (svoVisRT == null || svoVisRT.width != w || svoVisRT.height != h)
        {
            if (svoVisRT != null) { svoVisRT.Release(); Destroy(svoVisRT); }
            svoVisRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat);
            svoVisRT.enableRandomWrite = true;
            svoVisRT.name = "SVOVis_RT";
            svoVisRT.Create();
        }

        try
        {
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
            svoRayCast.SetMatrix("_InverseVP", vp.inverse);
            svoRayCast.SetVector("_CameraPos", cam.transform.position);
            svoRayCast.SetTexture(kernel, "_SVOResult", svoVisRT);

            uint tgX, tgY, tgZ;
            svoRayCast.GetKernelThreadGroupSizes(kernel, out tgX, out tgY, out tgZ);
            svoRayCast.Dispatch(kernel, Mathf.CeilToInt(w / (float)tgX), Mathf.CeilToInt(h / (float)tgY), 1);

            // 诊断日志（只打印一次）
            if (!_svoVisLogged)
            {
                _svoVisLogged = true;
                // 读取全局 SVO 参数
                var rootMin = Shader.GetGlobalVector("_SVORootMin");
                var scale = Shader.GetGlobalFloat("_SVOScale");
                var depth = Shader.GetGlobalInt("_SVODepth");
                Debug.Log($"[SVOVis] 诊断: _SVORootMin={rootMin}, _SVOScale={scale}, _SVODepth={depth}");
                Debug.Log($"[SVOVis] 相机: pos={cam.transform.position}, forward={cam.transform.forward}");
                Debug.Log($"[SVOVis] RT: {w}x{h}, kernel={kernel}, threadGroup=({tgX},{tgY},{tgZ})");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[SVOVis] 计算着色器调度失败: {ex.Message}");
        }
    }

    private bool _svoVisLogged = false;

    /// <summary>启用/禁用所有 chunk cube 的 MeshRenderer（防止标准管线渲染覆盖计算着色器输出）。</summary>
    void SetChunkRenderersEnabled(bool enabled)
    {
        var provider = VoxDataProvider.Instance;
        if (provider == null) return;
        foreach (var asset in provider.Assets)
        {
            if (asset == null || !asset.isActive) continue;
            foreach (var chunk in asset.chunks)
            {
                // TODO: 延迟渲染改为 SVO 输出 G-buffer 后移除
                // if (chunk?.cube == null) continue;
                // var renderer = chunk.cube.GetComponent<MeshRenderer>();
                // if (renderer != null) renderer.enabled = enabled;
            }
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

        // 创建或重建 RT
        bool resolutionChanged = false;
        if (pathTracingRT == null || pathTracingRT.width != w || pathTracingRT.height != h)
        {
            resolutionChanged = true;
            if (pathTracingRT != null) { pathTracingRT.Release(); Destroy(pathTracingRT); }
            pathTracingRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat);
            pathTracingRT.enableRandomWrite = true;
            pathTracingRT.name = "PathTracing_RT";
            pathTracingRT.Create();
        }

        // 检测相机是否移动（移动时重置累积）
        Matrix4x4 currentView = cam.worldToCameraMatrix;
        Matrix4x4 currentProj = cam.projectionMatrix;
        bool cameraMoved = (pathTracingFrameCount > 0) &&
            (currentView != prevViewMatrix || currentProj != prevProjMatrix);

        if (cameraMoved || resolutionChanged)
        {
            pathTracingFrameCount = 0;
            // 清空 RT 以移除旧帧数据
            Graphics.SetRenderTarget(pathTracingRT);
            GL.Clear(false, true, Color.black);
        }

        prevViewMatrix = currentView;
        prevProjMatrix = currentProj;

        try
        {
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
            svoPathTracing.SetMatrix("_InverseVP", vp.inverse);
            svoPathTracing.SetVector("_CameraPos", cam.transform.position);
            svoPathTracing.SetTexture(kernel, "_PathTracingResult", pathTracingRT);

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

            // 路径追踪参数
            svoPathTracing.SetInt("_MaxDepth", maxBounceDepth);
            svoPathTracing.SetInt("_FrameCount", pathTracingFrameCount);

            uint tgX, tgY, tgZ;
            svoPathTracing.GetKernelThreadGroupSizes(kernel, out tgX, out tgY, out tgZ);
            svoPathTracing.Dispatch(kernel, Mathf.CeilToInt(w / (float)tgX), Mathf.CeilToInt(h / (float)tgY), 1);

            pathTracingFrameCount++;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[PathTracing] 计算着色器调度失败: {ex.Message}");
        }
    }

    /// <summary>在 OnPreRender 中调度 SVO 光线追踪计算着色器，渲染到 rayTracingRT。</summary>
    void DispatchSVORayTracingPre()
    {
        if (svoRayTracing == null || cam == null) return;

        int w = cam.pixelWidth;
        int h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;

        int kernel = svoRayTracing.FindKernel("CSMain");
        if (kernel < 0)
        {
            Debug.LogError("[RayTracing] 计算着色器找不到 CSMain kernel");
            return;
        }

        // 创建或重建 RT
        if (rayTracingRT == null || rayTracingRT.width != w || rayTracingRT.height != h)
        {
            if (rayTracingRT != null) { rayTracingRT.Release(); Destroy(rayTracingRT); }
            rayTracingRT = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat);
            rayTracingRT.enableRandomWrite = true;
            rayTracingRT.name = "RayTracing_RT";
            rayTracingRT.Create();
        }

        try
        {
            Matrix4x4 vp = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * cam.worldToCameraMatrix;
            svoRayTracing.SetMatrix("_InverseVP", vp.inverse);
            svoRayTracing.SetVector("_CameraPos", cam.transform.position);
            svoRayTracing.SetTexture(kernel, "_RayTracingResult", rayTracingRT);

            // 光照参数
            Vector3 lightDir = new Vector3(0.5f, 0.8f, 0.3f).normalized;
            Vector3 lightColor = new Vector3(1f, 1f, 1f);
            var lights = FindObjectsOfType<Light>();
            foreach (var l in lights)
            {
                if (l.type == LightType.Directional && l.isActiveAndEnabled)
                {
                    lightDir = -l.transform.forward;
                    lightColor = (Vector4)l.color * l.intensity;
                    break;
                }
            }
            svoRayTracing.SetVector("_LightDir", lightDir);
            svoRayTracing.SetVector("_LightColor", lightColor);
            svoRayTracing.SetFloat("_Ambient", ambient);

            // 天空颜色：尝试将 Skybox Cubemap 传给计算着色器在 GPU 端采样
            Vector3 skyColor = new Vector3(0.15f, 0.25f, 0.85f); // 回退默认蓝色
            Cubemap skyCubemap = null;
            var skybox = cam.GetComponent<Skybox>();
            if (skybox != null && skybox.material != null && skybox.material.HasProperty("_Tex"))
                skyCubemap = skybox.material.GetTexture("_Tex") as Cubemap;
            if (skyCubemap == null && RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Tex"))
                skyCubemap = RenderSettings.skybox.GetTexture("_Tex") as Cubemap;
            if (skyCubemap != null)
            {
                svoRayTracing.SetTexture(kernel, "_SkyboxTex", skyCubemap);
                svoRayTracing.SetFloat("_HasSkyCube", 1.0f);
            }
            else
            {
                svoRayTracing.SetFloat("_HasSkyCube", 0.0f);
            }
            svoRayTracing.SetVector("_SkyColor", skyColor);

            uint tgX, tgY, tgZ;
            svoRayTracing.GetKernelThreadGroupSizes(kernel, out tgX, out tgY, out tgZ);
            svoRayTracing.Dispatch(kernel, Mathf.CeilToInt(w / (float)tgX), Mathf.CeilToInt(h / (float)tgY), 1);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[RayTracing] 计算着色器调度失败: {ex.Message}");
        }
    }
}
