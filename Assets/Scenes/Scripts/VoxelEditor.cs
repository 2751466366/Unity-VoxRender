using UnityEngine;

/// <summary>
/// 体素编辑：左键点击/拖刷删除体素（SVO 标记删除，无需重建）。
///
/// 交互约定：
///   左键点击      → 删除鼠标位置射线（光标锁定时用屏幕中心准星）命中的第一个体素
///   左键按住滑动  → 拖刷模式：沿滑动路径连续删除沿途体素
///                    （帧间屏幕插值采样，快速划动不漏格；光标锁定时每帧删准星处）
///   右键按住      → 保留给 CameraController 转向，按住期间忽略左键防误删
///
/// 注意：仅 Play 模式生效（Update 不在编辑模式运行）。
/// 使用方式：挂载到 Main Camera 上（CameraRender 开启 enableVoxelEditing 时自动挂载）。
/// </summary>
[RequireComponent(typeof(Camera))]
public class VoxelEditor : MonoBehaviour
{
    [Header("Voxel Removal")]
    [Tooltip("启用左键删除体素")]
    public bool enableRemoval = true;

    [Tooltip("删除后重置路径追踪累积 / DLSS 历史（清除残影）")]
    public bool resetAccumulation = false;

    [Tooltip("删除成功时在场景视图绘制命中体素 Gizmo（运行时可见）")]
    public bool drawHitGizmo = true;

    [Tooltip("输出点击/拾取/删除全链路的诊断日志")]
    public bool verboseLogging = true;

    [Header("Drag Brush")]
    [Tooltip("按住左键滑动时连续删除沿途体素")]
    public bool enableDragBrush = true;

    [Tooltip("拖刷的屏幕采样步长（像素）：滑动路径按此间隔插值发射拾取射线，越小越连续")]
    public float dragSampleStepPixels = 4f;

    private Camera cam;
    private CameraRender cameraRender;
    private Vector3 lastHitWorld;
    private float lastHitTime = -10f;
    private bool firstUpdateLogged = false;

    // ---- 拖刷状态 ----
    private bool dragging;              // 本次按住会话中（已处理过按下）
    private Vector2 prevScreenPos;      // 上一帧的采样屏幕位置
    private Vector3Int? lastPaintedGrid; // 上一采样点已处理过的格子（帧间去重）

    void Awake()
    {
        cam = GetComponent<Camera>();
        cameraRender = GetComponent<CameraRender>();
    }

    void Start()
    {
        // 就绪状态心跳（无条件输出，用于确认组件已挂载且处于 Play 模式）
        Debug.Log($"[VoxelEditor] 组件就绪（Play 模式） — camera={(cam != null ? "ok" : "缺失")}, " +
                  $"cameraRender={(cameraRender != null ? "ok" : "缺失，删除后无法重置累积")}, " +
                  $"enableRemoval={enableRemoval}, dragBrush={enableDragBrush}, verboseLogging={verboseLogging}, " +
                  $"SVOBuilder.IsReady={SVOBuilder.IsReady}（首帧才构建 SVO，此时为 False 属正常）");
    }

    void Update()
    {
        // 心跳：首个 Update 打一条（无条件），确认 Update 循环活着
        if (!firstUpdateLogged)
        {
            firstUpdateLogged = true;
            Debug.Log("[VoxelEditor] Update 已激活，等待左键点击/拖刷（注意：必须聚焦 Game 视图操作）");
        }

        // 松开左键：结束拖刷会话（先于右键检查，确保任何路径下状态都能复位）
        if (Input.GetMouseButtonUp(0))
        {
            dragging = false;
            lastPaintedGrid = null;
        }

        if (!enableRemoval) return;

        // 右键按住（转向中）不响应左键，避免误删
        if (Input.GetMouseButton(1))
        {
            if (Input.GetMouseButtonDown(0) && verboseLogging)
                Debug.Log("[VoxelEditor] 左键被忽略：右键按住中（转向模式）");
            return;
        }

        if (cam == null) cam = GetComponent<Camera>();
        if (cam == null)
        {
            Debug.LogWarning("[VoxelEditor] Camera 组件缺失，无法拾取");
            return;
        }

        bool pressed = Input.GetMouseButtonDown(0);
        bool held = dragging && enableDragBrush && Input.GetMouseButton(0);
        if (!pressed && !held) return;

        // 光标锁定（FPS 常锁模式）时用屏幕中心准星；否则用鼠标位置
        Vector2 pos = Cursor.lockState == CursorLockMode.Locked
            ? new Vector2(cam.pixelWidth * 0.5f, cam.pixelHeight * 0.5f)
            : (Vector2)Input.mousePosition;

        if (pressed)
        {
            // ---- 按下：开启拖刷会话，处理首个采样点 ----
            dragging = true;
            prevScreenPos = pos;
            Ray ray = cam.ScreenPointToRay(pos);
            TryRemove(ray, verboseLogging);
        }
        else
        {
            // ---- 按住拖刷：沿上一帧→本帧的屏幕线段插值采样 ----
            // 一步 ≈ dragSampleStepPixels 像素，快速划动时补齐中间被跳过的体素
            float dist = Vector2.Distance(prevScreenPos, pos);
            int steps = dragSampleStepPixels > 0.5f
                ? Mathf.Max(1, Mathf.CeilToInt(dist / dragSampleStepPixels))
                : 1;
            for (int i = 1; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(prevScreenPos, pos, (float)i / steps);
                // 拖刷采样静默未命中日志，防止每秒数百条刷屏
                TryRemove(cam.ScreenPointToRay(p), false);
            }
            prevScreenPos = pos;
        }
    }

    /// <summary>
    /// 单次拾取 + 删除。logMiss 控制未命中诊断日志（拖刷高频采样传 false）。
    /// Raycast 返回的 voxelIdx 直接传给 RemoveVoxelAt 走 O(1) 墓碑快路径。
    /// 帧间去重：同一格子本会话内只处理一次（删除成功后哈希已移除，
    /// 正常不会再命中；失败场景防止每帧重复尝试刷屏）。
    /// </summary>
    bool TryRemove(Ray ray, bool logMiss)
    {
        if (!SVOBuilder.Raycast(ray.origin, ray.direction, out Vector3Int hitGrid,
                                out int _, out int voxelIdx, logMiss))
            return false;

        if (lastPaintedGrid == hitGrid) return false; // 帧间去重

        if (SVOBuilder.RemoveVoxelAt(hitGrid, voxelIdx))
        {
            lastPaintedGrid = hitGrid;

            if (resetAccumulation && cameraRender != null)
                cameraRender.ResetAccumulation();

            if (drawHitGizmo)
            {
                lastHitWorld = SVOBuilder.GridToWorld(hitGrid);
                lastHitTime = Time.time;
            }
            return true;
        }

        // 失败也标记为已处理：场景静态，重试无意义，避免每帧刷 warning
        lastPaintedGrid = hitGrid;
        Debug.LogWarning($"[VoxelEditor] RemoveVoxelAt(grid={hitGrid}) 返回 false（详见 SVOBuilder 日志）");
        return false;
    }

    void OnDrawGizmos()
    {
        if (!drawHitGizmo || Time.time - lastHitTime > 2f) return;
        float size = SVOBuilder.VoxelWorldSize;
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(lastHitWorld + Vector3.one * (size * 0.5f), Vector3.one * size);
    }
}
