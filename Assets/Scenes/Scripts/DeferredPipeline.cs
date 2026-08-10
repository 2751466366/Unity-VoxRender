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
public class DeferredPipeline : MonoBehaviour
{
    [Header("Lighting Settings")]
    [Range(0f, 1f)] public float ambient = 0.2f;

    [Header("Debug")]
    [Tooltip("在 Game 视图中显示 G-Buffer 各通道而非最终光照结果")]
    public DebugView debugView = DebugView.Final;

    public enum DebugView
    {
        Final,
        Albedo,
        Normal,
        WorldPos,
    }

    private Camera cam;
    private CommandBuffer gBufferCmd;
    private Material lightingMat;

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

    void OnEnable()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogError("[DeferredPipeline] 必须挂载到 Camera 上");
            return;
        }

        // 创建光照材质
        Shader lightingShader = Shader.Find("Hidden/VoxRender_Lighting");
        if (lightingShader == null)
        {
            Debug.LogError("[DeferredPipeline] 未找到 Hidden/VoxRender_Lighting shader");
            return;
        }
        lightingMat = new Material(lightingShader);
        lightingMat.name = "DeferredLighting";

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

        if (lightingMat != null)
        {
            if (Application.isPlaying) Destroy(lightingMat);
            else DestroyImmediate(lightingMat);
            lightingMat = null;
        }
    }

    void OnPreRender()
    {
        EnsureRTs();
        RebuildCommandBuffer();
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
            for (int i = 0; i < asset.chunkCubes.Count; i++)
            {
                var cube = asset.chunkCubes[i];
                var mat  = asset.materials[i];
                if (cube == null || mat == null) continue;

                var mf = cube.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                // DrawMesh 不依赖 Renderer 是否启用，直接用 mesh + matrix + material 绘制
                gBufferCmd.DrawMesh(mf.sharedMesh, cube.transform.localToWorldMatrix, mat, 0, 0);
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
}
