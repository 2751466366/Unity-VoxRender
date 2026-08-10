using System.Collections.Generic;
using UnityEngine;
using VoxReader.Interfaces;

/// <summary>
/// 集中管理 VOX 文件的解析、3D 纹理与 chunk 代理立方体的创建与存储。
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
    /// parent 为 null 时立方体挂到本管理器下；scale/material 用默认值时取管理器配置。
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
        Debug.Log($"[VoxDataProvider] 已加载 '{voxFilePath}'，句柄={handle}，chunk 数={asset.chunkCubes.Count}");
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
    public IList<GameObject> GetChunkCubes(int handle) => GetAsset(handle)?.chunkCubes;
    public IList<Material> GetMaterials(int handle) => GetAsset(handle)?.materials;
    public IList<Vector3Int> GetChunkDims(int handle) => GetAsset(handle)?.chunkDims;

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
    }

    // ---- 内部：创建 chunk 立方体 + 3D 纹理 ----
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
                    voxel.Color.R / 255f, voxel.Color.G / 255f,
                    voxel.Color.B / 255f, voxel.Color.A / 255f);
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false);
            asset.textures.Add(tex);
            asset.chunkDims.Add(new Vector3Int(sx, sy, sz));

            // ---- chunk 代理立方体 ----
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = $"Chunk_{model.Name}_m{model.Id}_{sx}x{sy}x{sz}";
            cube.transform.SetParent(parent, false);

            // 立方体用 LocalSize 做缩放，旋转通过 localRotation 应用
            Vector3 chunkSize = new Vector3(sx, sz, sy) * scale; // Vox->Unity: Y<->Z
            cube.transform.localScale = chunkSize;

            // Vox 旋转矩阵 → Unity 四元数
            Quaternion rot = Matrix3ToQuaternion(model.GlobalRotation);
            cube.transform.localRotation = rot;

            // Vox 全局位置 → Unity 坐标
            Vector3 localPos = VoxToUnityPosition(model.GlobalPosition) * scale;
            if (model.GlobalPosition.X == 0 && model.GlobalPosition.Y == 0 && model.GlobalPosition.Z == 0)
                localPos += chunkSize * 0.5f;
            cube.transform.localPosition = localPos;

            // ---- 材质实例（每 chunk 一个，绑定纹理与尺寸） ----
            var instMat = new Material(baseMat);
            instMat.name = $"Mat_{cube.name}";
            instMat.SetTexture("_VoxelTexture", tex);
            instMat.SetVector("_ChunkSize", new Vector4(sx, sy, sz, 0));
            instMat.SetFloat("_VoxelScale", scale);
            cube.GetComponent<Renderer>().sharedMaterial = instMat;
            asset.materials.Add(instMat);
            asset.chunkCubes.Add(cube);
        }

        return true;
    }

    /// <summary>计算 cube 世界空间 AABB 并写入材质的 _WorldBoxMin/_WorldBoxMax。</summary>
    static void UpdateWorldAABB(GameObject cube, Material mat)
    {
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3(
                (i & 1) == 0 ? -0.5f : 0.5f,
                (i & 2) == 0 ? -0.5f : 0.5f,
                (i & 4) == 0 ? -0.5f : 0.5f
            );
            Vector3 wc = cube.transform.TransformPoint(corner);
            min = Vector3.Min(min, wc);
            max = Vector3.Max(max, wc);
        }
        mat.SetVector("_WorldBoxMin", new Vector4(min.x, min.y, min.z, 0));
        mat.SetVector("_WorldBoxMax", new Vector4(max.x, max.y, max.z, 0));
    }

    void Update()
    {
        // 动态更新所有活跃 chunk 的世界 AABB（物体移动/旋转后保持正确）
        foreach (var asset in assets)
        {
            if (asset == null || !asset.isActive) continue;
            for (int i = 0; i < asset.chunkCubes.Count; i++)
            {
                if (asset.chunkCubes[i] != null && asset.materials[i] != null)
                    UpdateWorldAABB(asset.chunkCubes[i], asset.materials[i]);
            }
        }
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
/// 一个已加载的 VOX 资产：包含解析数据、3D 纹理、chunk 立方体、材质。
/// 由 VoxDataProvider 创建并管理生命周期。
/// </summary>
[System.Serializable]
public class VoxAsset
{
    public string filePath;
    [System.NonSerialized] public IVoxFile voxFile;
    public List<Texture3D> textures = new List<Texture3D>();
    public List<GameObject> chunkCubes = new List<GameObject>();
    public List<Material> materials = new List<Material>();
    public List<Vector3Int> chunkDims = new List<Vector3Int>();

    /// <summary>标记资产是否有效。Dispose 后置为 false，参与序列化以跨重编译保持。</summary>
    public bool isActive = true;

    /// <summary>销毁所有关联的 Unity 资源（立方体/材质/纹理）。</summary>
    public void Dispose()
    {
        foreach (var c in chunkCubes)
        {
            if (c != null)
            {
                c.SetActive(false);
                UnityEngine.Object.DestroyImmediate(c);
            }
        }
        foreach (var m in materials)
            if (m != null) DestroyUnityObj(m);
        foreach (var t in textures)
            if (t != null) DestroyUnityObj(t);
        chunkCubes.Clear();
        materials.Clear();
        textures.Clear();
        chunkDims.Clear();
        isActive = false;
    }

    static void DestroyUnityObj(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Object.Destroy(obj);
        else Object.DestroyImmediate(obj);
    }
}
