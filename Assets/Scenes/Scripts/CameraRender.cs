using UnityEngine;

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

    // SVO G-Buffer RenderTextures（由 SVO_GBuffer.compute 写入）
    private RenderTexture gBufferAlbedo;    // RT0: ARGB32
    private RenderTexture gBufferNormal;    // RT1: ARGBHalf
    private RenderTexture gBufferWorldPos;  // RT2: ARGBHalf

    // SVO Lighting 输出 RT
    private RenderTexture lightingRT;

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
    }

    void OnDisable()
    {
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
            if (pathTracingRT != null)
                Graphics.Blit(pathTracingRT, dest);
            else
                Graphics.Blit(src, dest);
        }
        else
        {
            Graphics.Blit(src, dest);
        }
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

            // 方向光（太阳）
            svoPathTracing.SetVector("_SunDir", sunDir);
            svoPathTracing.SetVector("_SunColor", sunColor);
            svoPathTracing.SetFloat("_HasSun", hasSun ? 1.0f : 0.0f);

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
}
