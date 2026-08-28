using System.Collections.Generic;
using UnityEngine;
using VoxReader.Interfaces;

/// <summary>
/// 集中管理 VOX 文件的解析、3D 纹理与体素数据的创建与存储。
/// 外部脚本通过路径加载，获得一个 int 句柄（索引），再用句柄读取数据。
/// 为后续 SVO 体素路径追踪等高级渲染提供统一的数据入口。
/// </summary>
[ExecuteInEditMode]
public class VoxDataProvider : MonoBehaviour
{
    public static VoxDataProvider Instance { get; private set; }

    [Header("Defaults (可被 Load 参数覆盖)")]
    [Tooltip("体素缩放，当 Load 未指定 scale 时使用")]
    public float defaultVoxelScale = 1f;

    [Tooltip("chunk 代理材质，未指定时自动使用 Unlit/VoxRender_DDA")]
    public Material defaultChunkMaterial;


    [Header("Runtime Assets (只读)")]
    [Tooltip("所有已加载的 VOX 资产，按句柄索引存取。外部只读访问请用 GetAsset/GetChunkCubes 等方法")]
    [SerializeField, HideInInspector] private List<VoxAsset> assets = new List<VoxAsset>();

    /// <summary>只读访问资产列表。外部代码不应修改此列表。</summary>
    public IReadOnlyList<VoxAsset> Assets => assets;

    // SVO 输入：全局扁平数据（所有模型的集合）
    [System.NonSerialized] public List<VoxelElement>    voxelElements    = new List<VoxelElement>();
    [System.NonSerialized] public List<MaterialElement> materialElements = new List<MaterialElement>();

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    void OnDestroy()
    {
        UnloadAll();
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// 加载一个 VOX 文件，返回句柄（索引）。失败返回 -1。
    /// scale/material 用默认值时取管理器配置。
    /// </summary>
    public int Load(string voxFilePath, Transform parent = null,
                    float scale = -1f, Material material = null)
    {
        if (string.IsNullOrEmpty(voxFilePath))
        {
            Debug.LogError("[VoxDataProvider] 路径为空");
            return -1;
        }

        string absPath = System.IO.Path.Combine(
            System.IO.Directory.GetParent(Application.dataPath).FullName, voxFilePath);

        if (!System.IO.File.Exists(absPath))
        {
            Debug.LogError($"[VoxDataProvider] 文件不存在: {absPath}");
            return -1;
        }

        IVoxFile voxFile = VoxReader.VoxReader.Read(absPath);
        if (voxFile == null || voxFile.Models == null || voxFile.Models.Length == 0)
        {
            Debug.LogError($"[VoxDataProvider] 解析失败或无模型: {absPath}");
            return -1;
        }

        float useScale = scale > 0f ? scale : defaultVoxelScale;
        Material useMat = material != null ? material : defaultChunkMaterial;
        Transform useParent = parent != null ? parent : transform;

        var asset = new VoxAsset
        {
            filePath = voxFilePath,
            voxFile = voxFile
        };

        if (!CreateChunks(voxFile, asset, useParent, useScale, useMat))
        {
            asset.Dispose();
            return -1;
        }

        int handle = -1;
        for (int i = 0; i < assets.Count; i++)
        {
            if (!assets[i].isActive) { handle = i; break; }
        }
        if (handle < 0)
        {
            assets.Add(asset);
            handle = assets.Count - 1;
        }
        else
        {
            assets[handle] = asset;
        }
        Debug.Log($"[VoxDataProvider] 已加载 '{voxFilePath}'，句柄={handle}，chunk 数={asset.chunks.Count}，体素={voxelElements.Count}，材质={materialElements.Count}");
        return handle;
    }

    /// <summary>按句柄获取资产数据，句柄无效或已卸载返回 null。</summary>
    public VoxAsset GetAsset(int handle)
    {
        if (handle < 0 || handle >= assets.Count) return null;
        var a = assets[handle];
        if (!a.isActive) return null;
        return a;
    }

    public IVoxFile GetVoxFile(int handle) => GetAsset(handle)?.voxFile;
    public IList<Texture3D> GetTextures(int handle) => GetAsset(handle)?.textures;
    public IList<Material> GetMaterials(int handle) => GetAsset(handle)?.materials;
    public IList<ChunkInfo> GetChunks(int handle) => GetAsset(handle)?.chunks;
    public IList<VoxelElement> GetVoxelElements(int handle) => voxelElements;
    public IList<MaterialElement> GetMaterialElements(int handle) => materialElements;

    /// <summary>已注册资产槽位总数（含已卸载的空槽）。</summary>
    public int Count => assets.Count;

    /// <summary>卸载指定句柄的资产（销毁立方体/纹理/材质，释放槽位）。
    /// 不将槽位置 null（Unity 序列化不支持 List null 元素），仅标记 isActive=false。</summary>
    public void Unload(int handle)
    {
        if (handle < 0 || handle >= assets.Count) return;
        var a = assets[handle];
        if (!a.isActive) return;
        a.Dispose();
        Debug.Log($"[VoxDataProvider] 已卸载句柄={handle}");
    }

    /// <summary>卸载全部资产。</summary>
    public void UnloadAll()
    {
        for (int i = 0; i < assets.Count; i++)
        {
            if (assets[i] != null && assets[i].isActive)
                assets[i].Dispose();
        }
        voxelElements.Clear();
        materialElements.Clear();
    }

    // ---- 内部：构建材质表（自解析 MATL chunk 原始字节） ----
    void BuildMaterialElements(IVoxFile voxFile, VoxAsset asset, int matBase)
    {
        // Phase A: 用 palette 为 256 槽填默认 Diffuse
        for (int i = 0; i < 256; i++)
        {
            var me = new MaterialElement { type = MaterialType.Diffuse };
            if (voxFile.Palette != null && voxFile.Palette.Colors != null && i < voxFile.Palette.Colors.Length)
            {
                var c = voxFile.Palette.Colors[i];
                me.color = new Color(
                    Mathf.GammaToLinearSpace(c.R / 255f),
                    Mathf.GammaToLinearSpace(c.G / 255f),
                    Mathf.GammaToLinearSpace(c.B / 255f),
                    c.A / 255f);
            }
            materialElements.Add(me);  // 添加到全局列表
        }
    
        // Phase B: 遍历递归找 MaterialNew chunk，自解析覆盖
        ParseMaterialChunks(voxFile.Chunks, asset, matBase);
    }
    
    void ParseMaterialChunks(IChunk[] chunks, VoxAsset asset, int matBase)
    {
        if (chunks == null) return;
        foreach (var chunk in chunks)
        {
            if (chunk == null) continue;
            if (chunk.Type == VoxReader.ChunkType.MaterialNew && chunk.Content != null && chunk.Content.Length >= 4)
            {
                ParseOneMaterial(chunk.Content, asset, matBase);
            }
            if (chunk.Children != null && chunk.Children.Length > 0)
                ParseMaterialChunks(chunk.Children, asset, matBase);
        }
    }
    
    /// <summary>解析单个 MATL chunk 的 Content 字节：int32 id + DICT{string,string}。</summary>
    void ParseOneMaterial(byte[] content, VoxAsset asset, int matBase)
    {
        int offset = 0;
        int id = System.BitConverter.ToInt32(content, offset); offset += 4;
        int slot = matBase + id - 1; // MATL id 1-based → 全局 0-based
        if (slot < 0 || slot >= materialElements.Count) return;
    
        int pairCount = System.BitConverter.ToInt32(content, offset); offset += 4;
        var me = materialElements[slot];
    
        for (int p = 0; p < pairCount && offset < content.Length; p++)
        {
            if (offset + 4 > content.Length) break;
            int keyLen = System.BitConverter.ToInt32(content, offset); offset += 4;
            if (offset + keyLen > content.Length) break;
            string key = System.Text.Encoding.ASCII.GetString(content, offset, keyLen); offset += keyLen;
    
            if (offset + 4 > content.Length) break;
            int valLen = System.BitConverter.ToInt32(content, offset); offset += 4;
            if (offset + valLen > content.Length) break;
            string val = System.Text.Encoding.ASCII.GetString(content, offset, valLen); offset += valLen;
    
            // 按 key 覆盖 MaterialElement 字段；未知 key 静默忽略（前向兼容）
            switch (key)
            {
                case "_type":
                    int mt; if (int.TryParse(val, out mt)) me.type = (MaterialType)mt;
                    break;
                case "_weight": float w; if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w)) me.weight = w; break;
                case "_rough":  float r; if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r)) me.rough  = r; break;
                case "_spec":   float s; if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) me.spec   = s; break;
                case "_ior":    float io; if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out io)) me.ior = io; break;
                case "_att":    float at; if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out at)) me.att = at; break;
                case "_flux":   float fl; if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out fl)) me.flux = fl; break;
                // 未知 key 静默忽略
            }
        }
        materialElements[slot] = me;
    }

    /// <summary>
    /// 四舍五入到最近的整数（round half up），避免 Unity RoundToInt 的银行家舍入问题。
    /// </summary>
    static Vector3Int RoundToInt(Vector3 v)
    {
        return new Vector3Int(
            Mathf.FloorToInt(v.x + 0.5f),
            Mathf.FloorToInt(v.y + 0.5f),
            Mathf.FloorToInt(v.z + 0.5f));
    }

    // ---- 内部：创建 3D 纹理 + voxelElements + ChunkInfo ----
    bool CreateChunks(IVoxFile voxFile, VoxAsset asset, Transform parent,
                      float scale, Material baseMat)
    {
        if (baseMat == null)
        {
            Shader sh = Shader.Find("Unlit/VoxRender_GBuffer");
            if (sh == null)
            {
                Debug.LogError("[VoxDataProvider] 未找到 Unlit/VoxRender_GBuffer shader，无法创建 chunk");
                return false;
            }
            baseMat = new Material(sh);
            baseMat.name = "VoxGBuffer_Default";
        }

        // Phase A: 构建 materialElements（chunk 循环前，一次）
        int matBase = materialElements.Count;  // 记录当前资产的材质起始索引
        BuildMaterialElements(voxFile, asset, matBase);

        // Phase B: 逐 model 创建 Texture3D + voxelElements + ChunkInfo
        for (int i = 0; i < voxFile.Models.Length; i++)
        {
            var model = voxFile.Models[i];

            int sx = Mathf.Max(1, (int)model.LocalSize.X);
            int sy = Mathf.Max(1, (int)model.LocalSize.Y);
            int sz = Mathf.Max(1, (int)model.LocalSize.Z);

            // ---- 3D 纹理 ----
            var tex = new Texture3D(sx, sy, sz, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Point;
            tex.name = $"VoxelTex_{model.Name}_m{model.Id}";

            var pixels = new Color[sx * sy * sz];
            foreach (var voxel in model.Voxels)
            {
                int ix = (int)voxel.LocalPosition.X;
                int iy = (int)voxel.LocalPosition.Y;
                int iz = (int)voxel.LocalPosition.Z;
                if (ix < 0 || ix >= sx || iy < 0 || iy >= sy || iz < 0 || iz >= sz) continue;
                pixels[ix + iy * sx + iz * sx * sy] = new Color(
                    Mathf.GammaToLinearSpace(voxel.Color.R / 255f),
                    Mathf.GammaToLinearSpace(voxel.Color.G / 255f),
                    Mathf.GammaToLinearSpace(voxel.Color.B / 255f),
                    voxel.Color.A / 255f);
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            asset.textures.Add(tex);

            // ---- Vox 变换参数 ----
            Quaternion rot = Matrix3ToQuaternion(model.GlobalRotation);
            Vector3 chunkMin = parent.position + VoxToUnityPosition(model.GlobalPosition) * scale; // chunk 最小角
            Vector3 chunkSize = new Vector3(sx, sz, sy) * scale; // Vox->Unity: Y<->Z

            // ---- ChunkInfo ----
            var chunkInfo = new ChunkInfo();
            chunkInfo.dims = new Vector3Int(sx, sy, sz);
            Vector3 rotHalf = rot * (chunkSize * 0.5f);
            chunkInfo.worldMin = chunkMin + Vector3.Min(rotHalf, -rotHalf);
            chunkInfo.worldMax = chunkMin + Vector3.Max(rotHalf, -rotHalf);

            // ---- 体素坐标计算（直接用 rot，不依赖 cube Transform） ----
            foreach (var voxel in model.Voxels)
            {
                int ix = (int)voxel.LocalPosition.X;
                int iy = (int)voxel.LocalPosition.Y;
                int iz = (int)voxel.LocalPosition.Z;
                if (ix < 0 || ix >= sx || iy < 0 || iy >= sy || iz < 0 || iz >= sz) 
                    continue;
            
                // 体素相对于 chunk 中心的局部偏移（含 YZ 交换）
                Vector3 localOffset = new Vector3(
                    (ix - sx * 0.5f) * scale,
                    (iz - sz * 0.5f) * scale,   // Vox Y → Unity Z
                    (iy - sy * 0.5f) * scale);  // Vox Z → Unity Y
                // 旋转后得到世界空间偏移，加上 chunk 最小角
                Vector3 worldMinCorner = chunkMin + rot * localOffset;
                Vector3Int sceneMinCorner = RoundToInt(worldMinCorner / scale);
                voxelElements.Add(new VoxelElement
                {
                    worldPos = sceneMinCorner,
                    materialIndex = matBase + voxel.ColorIndex
                });
            }

            // ---- 材质实例 ----
            var instMat = new Material(baseMat);
            instMat.name = $"Mat_{model.Name}_m{model.Id}";
            instMat.SetTexture("_VoxelTexture", tex);
            instMat.SetVector("_ChunkSize", new Vector4(sx, sy, sz, 0));
            instMat.SetFloat("_VoxelScale", scale);
            asset.materials.Add(instMat);
            asset.chunks.Add(chunkInfo);
        }

        Debug.Log($"[VoxDataProvider] CreateChunks 完成：{asset.chunks.Count} chunks, " +
                  $"{voxelElements.Count} voxelElements, {materialElements.Count} materialElements");
        return true;
    }



    static Vector3 VoxToUnityPosition(VoxReader.Vector3 v) => new Vector3(v.X, v.Z, v.Y);

    /// <summary>将 VoxReader.Matrix3（Vox 坐标系）转换为 Unity Quaternion。
    /// 自动完成 Vox→Unity 的 Y/Z 轴交换。</summary>
    static Quaternion Matrix3ToQuaternion(VoxReader.Matrix3 m)
    {
        float m00 = m[0, 0], m01 = m[0, 1], m02 = m[0, 2];
        float m10 = m[1, 0], m11 = m[1, 1], m12 = m[1, 2];
        float m20 = m[2, 0], m21 = m[2, 1], m22 = m[2, 2];

        // Vox→Unity: X→X, Y→Z, Z→Y
        // R_unity = SwapYZ * R_vox * SwapYZ
        // 行交换: row0→row0, row1→row2, row2→row1
        // 列交换: col0→col0, col1→col2, col2→col1
        float u00 = m00,  u01 = m02,  u02 = m01;
        float u10 = m20,  u11 = m22,  u12 = m21;
        float u20 = m10,  u21 = m12,  u22 = m11;

        Matrix4x4 mat = new Matrix4x4();
        mat[0,0] = u00; mat[0,1] = u01; mat[0,2] = u02;
        mat[1,0] = u10; mat[1,1] = u11; mat[1,2] = u12;
        mat[2,0] = u20; mat[2,1] = u21; mat[2,2] = u22;
        mat[3,3] = 1f;

        return mat.rotation;
    }
}

/// <summary>
/// 一个已加载的 VOX 资产：包含解析数据、SVO 输入（扁平 GPU-ready 数组）、DDA pass 用资源。
/// 由 VoxDataProvider 创建并管理生命周期。
/// </summary>
[System.Serializable]
public class VoxAsset
{
    public string filePath;
    [System.NonSerialized] public IVoxFile voxFile;

    // DDA pass 用：按 chunk 索引平行存储
    public List<ChunkInfo> chunks   = new List<ChunkInfo>();
    public List<Texture3D> textures = new List<Texture3D>();
    public List<Material>  materials = new List<Material>();

    /// <summary>标记资产是否有效。Dispose 后置为 false，参与序列化以跨重编译保持。</summary>
    public bool isActive = true;

    /// <summary>销毁所有关联的 Unity 资源（材质/纹理）。</summary>
    public void Dispose()
    {
        foreach (var m in materials)
            if (m != null) DestroyUnityObj(m);
        foreach (var t in textures)
            if (t != null) DestroyUnityObj(t);
        chunks.Clear();
        materials.Clear();
        textures.Clear();
        isActive = false;
    }

    static void DestroyUnityObj(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Object.Destroy(obj);
        else Object.DestroyImmediate(obj);
    }
}
