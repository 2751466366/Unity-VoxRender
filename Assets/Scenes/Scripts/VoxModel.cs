using UnityEngine;

/// <summary>
/// 场景中挂载的 VOX 模型实例。仅作为 VoxDataProvider 的门面：
/// 加载时传入路径和父节点，卸载时转发给 provider。
/// 所有资源（立方体/纹理/材质）的创建与销毁由 VoxDataProvider/VoxAsset 统一管理。
/// </summary>
// [ExecuteInEditMode]
public class VoxModel : MonoBehaviour
{
    [Header("Vox File Settings")]
    [Tooltip("相对工程根目录的路径，如 Assets/Scenes/Vox/castle.vox")]
    public string voxFilePath = "Assets/Scenes/Vox/castle.vox";

    [Tooltip("体素缩放")]
    public float voxelScale = 1f;

    [Tooltip("chunk 代理材质，留空则使用 VoxDataProvider 的默认材质或自动创建")]
    public Material chunk3DMaterial;

    [Header("Load On Start")]
    [Tooltip("Start 时自动加载")]
    public bool loadOnStart = true;

    [Header("Runtime (只读)")]
    [Tooltip("当前加载句柄，-1 表示未加载。序列化保存，重编译后不丢失")]
    [SerializeField, HideInInspector] private int handle = -1;

    /// <summary>当前加载句柄，-1 表示未加载。外部脚本可据此访问 VoxDataProvider 的数据。</summary>
    public int Handle => handle;

    void Start()
    {
        if (loadOnStart) Load();
    }

    void OnDestroy()
    {
        Unload();
    }

    private VoxDataProvider GetProvider()
    {
        var p = VoxDataProvider.Instance;
        if (p == null) p = FindObjectOfType<VoxDataProvider>();
        return p;
    }

    [ContextMenu("Load Vox File")]
    public void Load()
    {
        // 先卸载旧的
        if (handle >= 0)
        {
            return;
        }

        var provider = GetProvider();
        if (provider == null)
        {
            Debug.LogError("[VoxModel] 场景中未找到 VoxDataProvider，请先在场景挂载该组件。");
            return;
        }

        handle = provider.Load(voxFilePath, transform, voxelScale, chunk3DMaterial);
    }

    [ContextMenu("Clear Voxels")]
    public void Unload()
    {
        if (handle < 0) return;

        var provider = GetProvider();
        if (provider != null)
            provider.Unload(handle);

        handle = -1;
    }
}
