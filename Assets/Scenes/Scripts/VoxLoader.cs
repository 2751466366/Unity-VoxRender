using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using VoxReader.Interfaces;

[ExecuteInEditMode]
public class VoxLoader : MonoBehaviour
{
    [Header("Vox File Settings")]
    [Tooltip("Path to the .vox file, relative to the project root (e.g. Assets/Scenes/Vox/castle.vox)")]
    public string voxFilePath = "Assets/Scenes/Vox/castle.vox";

    [Tooltip("Scale factor for each voxel")]
    public float voxelScale = 1f;

    [Tooltip("Material used for chunk proxy cubes. Must sample _VoxelTexture (Texture3D) and _ChunkSize (Vector3).")]
    public Material chunk3DMaterial;

    [Header("Load On Start")]
    [Tooltip("Automatically load the vox file on Start()")]
    public bool loadOnStart = true;

    void Start()
    {
        if (loadOnStart)
        {
            LoadVoxFile();
        }
    }

    [ContextMenu("Load Vox File")]
    public void LoadVoxFile()
    {
        ClearExistingVoxels();

        string path = System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath).FullName, voxFilePath);

        if (!System.IO.File.Exists(path))
        {
            Debug.LogError($"[VoxLoader] File not found: {path}");
            return;
        }

        Debug.Log($"[VoxLoader] Loading: {path}");

        IVoxFile voxFile = VoxReader.VoxReader.Read(path);

        PrintVoxStatistics(voxFile);

        CreateChunksWith3DTexture(voxFile);
    }

    /// <summary>
    /// 打印 VOX 文件中的 chunk、体素数量，以及场景节点和材质信息。
    /// </summary>
    void PrintVoxStatistics(IVoxFile voxFile)
    {
        Debug.Log($"[VoxLoader] ===== VOX 文件信息 =====");
        Debug.Log($"[VoxLoader] 版本号: {voxFile.VersionNumber}");

        int totalVoxels = 0;
        Debug.Log($"[VoxLoader] Model(Chunk) 数量: {voxFile.Models.Length}");
        for (int i = 0; i < voxFile.Models.Length; i++)
        {
            var model = voxFile.Models[i];
            totalVoxels += model.Voxels.Length;
            Debug.Log($"  [{i}] Model Name='{model.Name}'  Id={model.Id}  IsCopy={model.IsCopy}  " +
                      $"VoxelCount={model.Voxels.Length}  " +
                      $"LocalSize=({model.LocalSize.X},{model.LocalSize.Y},{model.LocalSize.Z})  " +
                      $"GlobalSize=({model.GlobalSize.X},{model.GlobalSize.Y},{model.GlobalSize.Z})");
        }
        Debug.Log($"[VoxLoader] 体素总数量: {totalVoxels}");

        if (voxFile.Chunks != null)
        {
            Debug.Log($"[VoxLoader] 底层 Chunk 总数(MAIN 子节点): {voxFile.Chunks.Length}");
            Dictionary<VoxReader.ChunkType, int> chunkCounts = new Dictionary<VoxReader.ChunkType, int>();
            foreach (var chunk in voxFile.Chunks)
            {
                if (chunk == null) continue;
                if (!chunkCounts.ContainsKey(chunk.Type))
                    chunkCounts[chunk.Type] = 0;
                chunkCounts[chunk.Type]++;
            }
            foreach (var kv in chunkCounts)
            {
                Debug.Log($"  Chunk 类型 {kv.Key}: {kv.Value} 个");
            }
        }

        int transformNodeCount = 0;
        int groupNodeCount = 0;
        int shapeNodeCount = 0;
        int layerCount = 0;
        int objectCount = 0;
        int cameraCount = 0;
        int materialOldCount = 0;
        int materialNewCount = 0;
        int packCount = 0;
        int noteCount = 0;
        int indexMapCount = 0;
        int metaCount = 0;

        if (voxFile.Chunks != null)
        {
            void CountChunks(IEnumerable<IChunk> chunks)
            {
                foreach (var c in chunks)
                {
                    if (c == null) continue;
                    switch (c.Type)
                    {
                        case VoxReader.ChunkType.TransformNode: transformNodeCount++; break;
                        case VoxReader.ChunkType.GroupNode: groupNodeCount++; break;
                        case VoxReader.ChunkType.ShapeNode: shapeNodeCount++; break;
                        case VoxReader.ChunkType.Layer: layerCount++; break;
                        case VoxReader.ChunkType.Object: objectCount++; break;
                        case VoxReader.ChunkType.Camera: cameraCount++; break;
                        case VoxReader.ChunkType.MaterialOld: materialOldCount++; break;
                        case VoxReader.ChunkType.MaterialNew: materialNewCount++; break;
                        case VoxReader.ChunkType.Pack: packCount++; break;
                        case VoxReader.ChunkType.Note: noteCount++; break;
                        case VoxReader.ChunkType.IndexMap: indexMapCount++; break;
                        case VoxReader.ChunkType.Meta: metaCount++; break;
                    }
                    if (c.Children != null && c.Children.Length > 0)
                        CountChunks(c.Children);
                }
            }
            CountChunks(voxFile.Chunks);
        }

        bool hasSceneGraph = (transformNodeCount + groupNodeCount + shapeNodeCount) > 0;
        Debug.Log($"[VoxLoader] ===== 场景/节点信息 =====");
        Debug.Log($"[VoxLoader] 是否存在场景图(Scene Graph): {(hasSceneGraph ? "是" : "否")}");
        Debug.Log($"  TransformNode(nTRN): {transformNodeCount}  " +
                  $"GroupNode(nGRP): {groupNodeCount}  " +
                  $"ShapeNode(nSHP): {shapeNodeCount}");
        Debug.Log($"  Layer: {layerCount}  Object: {objectCount}  Camera: {cameraCount}  Pack: {packCount}");

        bool hasMaterialInfo = (materialOldCount + materialNewCount + indexMapCount) > 0;
        Debug.Log($"[VoxLoader] ===== 材质(Material)信息 =====");
        Debug.Log($"[VoxLoader] 是否存在材质数据: {(hasMaterialInfo ? "是" : "否")}");
        Debug.Log($"  MaterialOld(旧MATL): {materialOldCount}  " +
                  $"MaterialNew(新材质): {materialNewCount}  " +
                  $"IndexMap: {indexMapCount}");

        if (voxFile.Palette != null)
        {
            int paletteColorCount = voxFile.Palette.Colors != null ? voxFile.Palette.Colors.Length : 0;
            int nonEmptyCount = 0;
            if (voxFile.Palette.Colors != null)
            {
                for (int i = 0; i < voxFile.Palette.Colors.Length; i++)
                {
                    var c = voxFile.Palette.Colors[i];
                    if (c.A > 0) nonEmptyCount++;
                }
            }
            Debug.Log($"[VoxLoader] 调色板(Palette): 总 {paletteColorCount} 色, 非透明 {nonEmptyCount} 色");
            Debug.Log($"  调色板 Note 数量: {(voxFile.Palette.Notes != null ? voxFile.Palette.Notes.Length : 0)}");
        }
        Debug.Log($"[VoxLoader] Note chunks: {noteCount}  Meta chunks: {metaCount}");
    }

    /// <summary>
    /// 为每个 Model(Chunk) 创建一个大立方体表示 chunk 范围，
    /// 并为每个 chunk 生成一个 3D 纹理，其中每个体素对应一个 RGBA texel。
    /// 大立方体 + 3D 纹理的组合交给着色器完成体素绘制（DDA 光线步进）。
    /// chunk 立方体作为脚本所在 GameObject 的子物体，位置使用 localPosition。
    /// </summary>
    void CreateChunksWith3DTexture(IVoxFile voxFile)
    {
        Material mat = chunk3DMaterial;
        if (mat == null)
        {
            Shader ddaShader = Shader.Find("Unlit/VoxRender_DDA");
            if (ddaShader != null)
            {
                mat = new Material(ddaShader);
                Debug.Log("[VoxLoader] 未指定 chunk3DMaterial，自动使用 Unlit/VoxRender_DDA shader。");
            }
            else
            {
                Debug.LogError("[VoxLoader] 未找到 Unlit/VoxRender_DDA shader，无法渲染。");
                return;
            }
        }

        for (int modelIdx = 0; modelIdx < voxFile.Models.Length; modelIdx++)
        {
            var model = voxFile.Models[modelIdx];

            // ---- 1. 计算 chunk 的尺寸（体素格数） ----
            int sx = Mathf.Max(1, (int)model.LocalSize.X);
            int sy = Mathf.Max(1, (int)model.LocalSize.Y);
            int sz = Mathf.Max(1, (int)model.LocalSize.Z);

            // ---- 2. 创建 3D 纹理 ----
            Texture3D voxelTex = new Texture3D(sx, sy, sz, TextureFormat.RGBA32, false);
            voxelTex.wrapMode = TextureWrapMode.Clamp;
            voxelTex.filterMode = FilterMode.Point;
            voxelTex.name = $"VoxelTex_{model.Name}_m{model.Id}";

            Color[] texPixels = new Color[sx * sy * sz];
            for (int i = 0; i < texPixels.Length; i++)
                texPixels[i] = new Color(0, 0, 0, 0);

            int filledVoxels = 0;
            foreach (var voxel in model.Voxels)
            {
                int ix = (int)voxel.LocalPosition.X;
                int iy = (int)voxel.LocalPosition.Y;
                int iz = (int)voxel.LocalPosition.Z;
                if (ix < 0 || ix >= sx || iy < 0 || iy >= sy || iz < 0 || iz >= sz)
                    continue;

                // MagicaVoxel 调色板的 alpha 通道可能为 0（未使用），体素存在即不透明
                Color col = new Color(
                    voxel.Color.R / 255f,
                    voxel.Color.G / 255f,
                    voxel.Color.B / 255f,
                    voxel.Color.A / 255f);
                int linearIdx = ix + iy * sx + iz * sx * sy;
                texPixels[linearIdx] = col;
                filledVoxels++;
            }
            voxelTex.SetPixels(texPixels);
            voxelTex.Apply(false, false);

            // ---- 3. 创建大立方体（chunk 代理），直接作为脚本子物体 ----
            GameObject chunkCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            chunkCube.name = $"Chunk_{model.Name}_m{model.Id}_{sx}x{sy}x{sz}";
            chunkCube.transform.SetParent(transform, false);

            if (!Application.isPlaying)
            {
                Undo.RegisterCreatedObjectUndo(chunkCube, "Load Vox File");
            }

            Vector3 chunkSize = new Vector3(sx, sz, sy) * voxelScale; // Vox->Unity 坐标转换: Y<->Z
            chunkCube.transform.localScale = chunkSize;


            Vector3 modelUnityPos = VoxToUnityPosition(model.GlobalPosition);
            Vector3 localPos = modelUnityPos * voxelScale;

            if (model.GlobalPosition.X == 0 && model.GlobalPosition.Y == 0 && model.GlobalPosition.Z == 0)
            {
                localPos += chunkSize * 0.5f;
            }

            chunkCube.transform.localPosition = localPos;

            // ---- 4. 绑定材质和 3D 纹理 ----
            var renderer = chunkCube.GetComponent<Renderer>();
            if (renderer != null)
            {
                Material instMat = new Material(mat);
                instMat.name = $"Mat_{chunkCube.name}";
                instMat.SetTexture("_VoxelTexture", voxelTex);
                instMat.SetVector("_ChunkSize", new Vector4(sx, sy, sz, 0));
                instMat.SetFloat("_VoxelScale", voxelScale);
                renderer.sharedMaterial = instMat;
            }

            Debug.Log($"[VoxLoader] Chunk[{modelIdx}] '{model.Name}': 3D 纹理 {sx}x{sy}x{sz} 已创建，" +
                      $"非空体素 {filledVoxels}/{model.Voxels.Length}，chunk 代理尺寸 {chunkSize}");
        }

        Debug.Log($"[VoxLoader] 已创建 {voxFile.Models.Length} 个 Chunk 代理立方体 + 3D 纹理。");
    }

    [ContextMenu("Clear Voxels")]
    public void ClearExistingVoxels()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = transform.GetChild(i).gameObject;
            if (Application.isPlaying)
            {
                Destroy(child);
            }
            else
            {
                Undo.DestroyObjectImmediate(child);
            }
        }
    }

    static Vector3 VoxToUnityPosition(VoxReader.Vector3 voxPos)
    {
        return new Vector3(voxPos.X, voxPos.Z, voxPos.Y);
    }
}
