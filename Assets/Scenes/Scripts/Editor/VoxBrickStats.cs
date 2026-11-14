#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 体素砖化统计工具（阶段 0 决策依据）：
/// 用真实 .vox 数据算出「4³ / 8³ / 16³ 三种砖尺寸」的砖数、实心砖占比、单材质砖占比、
/// 每砖平均体素数，以及 AABB 输入与 BLAS 的体量估算，据此确认
/// <c>VOXRT_BRICK_SIZE</c> 选 8 是否合理。
///
/// 数据来源：<see cref="VoxDataProvider.Instance"/> 的运行时镜像，因此**必须先进 Play 模式**
/// 加载完模型再执行菜单命令（Editor 编译期没有体素数据）。
///
/// 报告写到工程根目录下的 docs/砖化统计报告_&lt;yyyyMMdd_HHmmss&gt;.md。
/// 另附能力探测：插件尚未 INIT 时会主动发一次 INIT（读完后立即 SHUTDOWN 复位），
/// 因此编辑器中不进 Play 也能确认 DXR 是否可用；DLL 不存在时只提示，不抛异常。
/// </summary>
public static class VoxBrickStats
{
    /// <summary>待评估的砖边长（体素）。8 是 VoxRTPlugin 的默认值。</summary>
    private static readonly int[] kBrickSizes = { 4, 8, 16 };

    /// <summary>表面体素统计使用的参考砖尺寸（与插件默认 VOXRT_BRICK_SIZE 一致）。</summary>
    private const int kSurfaceBrickSize = 8;

    /// <summary>砖聚合累加器（材质是否唯一 + 砖内体素数）。</summary>
    private struct BrickAccum
    {
        public int  count;      // 砖内活跃体素数
        public int  firstMat;   // 首个材质索引
        public bool mixed;      // 材质是否不唯一
    }

    private struct BrickStat
    {
        public int  brickSize;
        public long brickCount;      // 非空砖数（= DXR primitive 数）
        public long fullBricks;      // 砖内体素填满（brickSize³ 全满）
        public long uniformBricks;   // 材质唯一
        public long solidUniform;    // 全满且单材质（IS 最强快速路径）
        public long occupiedCells;   // Σ 砖内体素数
    }

    // ============================================================
    //  菜单入口
    // ============================================================

    [MenuItem("Tools/VoxRender/Brick Statistics", priority = 100)]
    static void RunBrickStatistics()
    {
        var provider = VoxDataProvider.Instance;
        if (provider == null || provider.voxelElements == null || provider.voxelElements.Count == 0)
        {
            EditorUtility.DisplayDialog("砖化统计",
                "VoxDataProvider 里还没有体素数据。\n\n" +
                "请先进入 Play 模式让 VoxModel 加载 .vox（SVO 构建完成后），\n" +
                "再执行 Tools/VoxRender/Brick Statistics。", "确定");
            return;
        }

        var voxels = provider.voxelElements;

        try
        {
            EditorUtility.DisplayProgressBar("砖化统计", "统计包围盒与活跃体素…", 0.05f);

            int total = 0;
            Vector3Int min = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
            Vector3Int max = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
            for (int i = 0; i < voxels.Count; i++)
            {
                var v = voxels[i];
                if (v.materialIndex < 0) continue;    // 墓碑
                Vector3Int c = v.worldPos;
                min = Vector3Int.Min(min, c);
                max = Vector3Int.Max(max, c);
                total++;
            }

            if (total == 0)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("砖化统计", "全部体素都是墓碑（已被删除），没有可统计的数据。", "确定");
                return;
            }

            var stats = new List<BrickStat>(kBrickSizes.Length);
            for (int i = 0; i < kBrickSizes.Length; i++)
            {
                EditorUtility.DisplayProgressBar("砖化统计",
                    $"统计 {kBrickSizes[i]}³ 砖…", 0.1f + 0.25f * i);
                stats.Add(ComputeBrickStat(voxels, kBrickSizes[i]));
            }

            EditorUtility.DisplayProgressBar("砖化统计", "统计表面体素…", 0.85f);
            long surface = CountSurfaceVoxels(voxels, kSurfaceBrickSize);

            EditorUtility.DisplayProgressBar("砖化统计", "探测 DXR 能力…", 0.95f);
            string capability = ProbeCapability();

            string report = BuildReport(voxels, total, min, max, stats, surface, capability, provider);
            string path = WriteReport(report);

            EditorUtility.ClearProgressBar();
            Debug.Log($"[VoxBrickStats] 报告已写入 {path}\n" + report);
            EditorUtility.DisplayDialog("砖化统计", "已生成报告：\n" + path, "确定");
        }
        catch (Exception ex)
        {
            EditorUtility.ClearProgressBar();
            Debug.LogError("[VoxBrickStats] 统计失败: " + ex);
            EditorUtility.DisplayDialog("砖化统计", "统计失败：" + ex.Message, "确定");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    [MenuItem("Tools/VoxRender/DXR Capability Probe", priority = 101)]
    static void RunCapabilityProbe()
    {
        string result = ProbeCapability();
        Debug.Log("[VoxBrickStats] DXR 能力探测: " + result);
        EditorUtility.DisplayDialog("DXR 能力探测", result, "确定");
    }

    // ============================================================
    //  统计实现
    // ============================================================

    /// <summary>按给定砖边长聚合，返回砖数/实心砖/单材质砖等统计。</summary>
    private static BrickStat ComputeBrickStat(List<VoxelElement> voxels, int brickSize)
    {
        int shift = ShiftOf(brickSize);
        int cellsPerBrick = brickSize * brickSize * brickSize;

        var map = new Dictionary<long, BrickAccum>(Mathf.Max(16, voxels.Count / 16));
        var stat = new BrickStat { brickSize = brickSize };

        for (int i = 0; i < voxels.Count; i++)
        {
            var v = voxels[i];
            if (v.materialIndex < 0) continue;

            Vector3Int c = v.worldPos;
            long key = PackKey(c.x >> shift, c.y >> shift, c.z >> shift);

            BrickAccum acc;
            if (!map.TryGetValue(key, out acc))
            {
                acc = new BrickAccum { count = 1, firstMat = v.materialIndex, mixed = false };
            }
            else
            {
                acc.count++;
                if (!acc.mixed && acc.firstMat != v.materialIndex) acc.mixed = true;
            }
            map[key] = acc;
        }

        stat.brickCount = map.Count;
        foreach (var kv in map)
        {
            var acc = kv.Value;
            if (acc.count >= cellsPerBrick) stat.fullBricks++;
            if (!acc.mixed)
            {
                stat.uniformBricks++;
                if (acc.count >= cellsPerBrick) stat.solidUniform++;
            }
            stat.occupiedCells += acc.count;
        }
        return stat;
    }

    /// <summary>
    /// 表面体素数：6 邻域里至少缺一个体素的活跃体素。
    /// 用砖占据位掩码做邻域查询（砖内直接查位，跨砖查相邻砖），只用参考砖尺寸算一次。
    /// </summary>
    private static long CountSurfaceVoxels(List<VoxelElement> voxels, int brickSize)
    {
        int shift = ShiftOf(brickSize);
        int cellsPerBrick = brickSize * brickSize * brickSize;
        int words = (cellsPerBrick + 31) / 32;
        int b = brickSize;

        // ---- 1. 构建每砖占据掩码 ----
        var masks = new Dictionary<long, uint[]>(Mathf.Max(16, voxels.Count / 16));
        for (int i = 0; i < voxels.Count; i++)
        {
            var v = voxels[i];
            if (v.materialIndex < 0) continue;

            Vector3Int c = v.worldPos;
            long key = PackKey(c.x >> shift, c.y >> shift, c.z >> shift);

            uint[] mask;
            if (!masks.TryGetValue(key, out mask))
            {
                mask = new uint[words];
                masks[key] = mask;
            }
            int lx = c.x - ((c.x >> shift) << shift);
            int ly = c.y - ((c.y >> shift) << shift);
            int lz = c.z - ((c.z >> shift) << shift);
            int bit = (lz * b + ly) * b + lx;
            mask[bit >> 5] |= 1u << (bit & 31);
        }

        // ---- 2. 逐砖逐位检查 6 邻域 ----
        long surface = 0;
        foreach (var kv in masks)
        {
            long key = kv.Key;
            uint[] mask = kv.Value;

            int bx = KeyX(key), by = KeyY(key), bz = KeyZ(key);
            int baseX = bx << shift, baseY = by << shift, baseZ = bz << shift;

            for (int bit = 0; bit < cellsPerBrick; bit++)
            {
                if ((mask[bit >> 5] & (1u << (bit & 31))) == 0u) continue;

                int lx = bit % b;
                int ly = (bit / b) % b;
                int lz = bit / (b * b);
                int gx = baseX + lx, gy = baseY + ly, gz = baseZ + lz;

                // 任意一个方向为空 → 该体素是表面体素
                if (!IsOccupied(masks, shift, b, gx - 1, gy, gz) ||
                    !IsOccupied(masks, shift, b, gx + 1, gy, gz) ||
                    !IsOccupied(masks, shift, b, gx, gy - 1, gz) ||
                    !IsOccupied(masks, shift, b, gx, gy + 1, gz) ||
                    !IsOccupied(masks, shift, b, gx, gy, gz - 1) ||
                    !IsOccupied(masks, shift, b, gx, gy, gz + 1))
                {
                    surface++;
                }
            }
        }
        return surface;
    }

    /// <summary>grid 坐标处是否有活跃体素（查所在砖的占据掩码）。</summary>
    private static bool IsOccupied(Dictionary<long, uint[]> masks, int shift, int b,
                                   int gx, int gy, int gz)
    {
        long key = PackKey(gx >> shift, gy >> shift, gz >> shift);
        uint[] mask;
        if (!masks.TryGetValue(key, out mask)) return false;

        int lx = gx - ((gx >> shift) << shift);
        int ly = gy - ((gy >> shift) << shift);
        int lz = gz - ((gz >> shift) << shift);
        int bit = (lz * b + ly) * b + lx;
        return (mask[bit >> 5] & (1u << (bit & 31))) != 0u;
    }

    // ============================================================
    //  报告
    // ============================================================

    private static string BuildReport(List<VoxelElement> voxels, int total,
                                      Vector3Int min, Vector3Int max,
                                      List<BrickStat> stats, long surface,
                                      string capability, VoxDataProvider provider)
    {
        Vector3Int extent = max - min + Vector3Int.one;
        double bboxVolume = (double)extent.x * extent.y * extent.z;
        double fill = bboxVolume > 0 ? total / bboxVolume : 0.0;

        var sb = new StringBuilder();
        sb.AppendLine("# 体素砖化统计报告");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}  ");
        sb.AppendLine($"> 数据来源：`VoxDataProvider` 运行时镜像（voxelElements {voxels.Count} 项，含墓碑）  ");
        sb.AppendLine($"> 材质条目：{provider.materialElements.Count}");
        sb.AppendLine();

        sb.AppendLine("## 1. 场景总览");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 活跃体素总数 | {total:N0} |");
        sb.AppendLine($"| 墓碑（已删除） | {voxels.Count - total:N0} |");
        sb.AppendLine($"| 包围盒 min | ({min.x}, {min.y}, {min.z}) |");
        sb.AppendLine($"| 包围盒 max | ({max.x}, {max.y}, {max.z}) |");
        sb.AppendLine($"| 包围盒尺寸 | {extent.x} × {extent.y} × {extent.z} |");
        sb.AppendLine($"| 包围盒体素数 | {bboxVolume:N0} |");
        sb.AppendLine($"| 填充率 | {fill:P2} |");
        sb.AppendLine($"| 表面体素（{kSurfaceBrickSize}³ 砖、6 邻域） | {surface:N0}（{surface / (double)total:P1}） |");
        sb.AppendLine();

        sb.AppendLine("## 2. 砖尺寸对比");
        sb.AppendLine();
        sb.AppendLine("| 砖尺寸 | 非空砖数 | 全满砖 | 全满占比 | 单材质砖 | 单材质占比 | 全满且单材质 | 每砖平均体素 | AABB 输入 | BLAS 估算 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in stats)
        {
            double avg = s.brickCount > 0 ? s.occupiedCells / (double)s.brickCount : 0.0;
            long aabbBytes = s.brickCount * 24;                    // D3D12_RAYTRACING_AABB = 6 float
            long blasBytes = s.brickCount * 88;                    // 24B AABB + ~64B BVH 节点（经验值）
            sb.AppendLine($"| {s.brickSize}³ | {s.brickCount:N0} | {s.fullBricks:N0} | " +
                          $"{(s.brickCount > 0 ? s.fullBricks / (double)s.brickCount : 0.0):P1} | " +
                          $"{s.uniformBricks:N0} | " +
                          $"{(s.brickCount > 0 ? s.uniformBricks / (double)s.brickCount : 0.0):P1} | " +
                          $"{s.solidUniform:N0} | {avg:F2} | {ToMB(aabbBytes)} | {ToMB(blasBytes)} |");
        }
        sb.AppendLine();
        sb.AppendLine("说明：BLAS 估算 = 砖数 × 88B（24B AABB + 约 64B BVH 节点），仅用于量级判断，");
        sb.AppendLine("实测以插件 BUILD_SCENE 日志里 `GetRaytracingAccelerationStructurePrebuildInfo` 的结果为准。");
        sb.AppendLine();

        // chunk 统计（按 VOXRT_CHUNK_SIZE = 64）
        var chunkSet = new HashSet<long>(Mathf.Max(16, total / 512));
        for (int i = 0; i < voxels.Count; i++)
        {
            var v = voxels[i];
            if (v.materialIndex < 0) continue;
            Vector3Int c = v.worldPos;
            chunkSet.Add(PackKey(c.x >> 6, c.y >> 6, c.z >> 6));
        }
        sb.AppendLine("## 3. 分块（chunk 64³）");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| chunk 数 | {chunkSet.Count:N0} |");
        sb.AppendLine($"| TLAS instance 数 | {chunkSet.Count:N0} |");
        sb.AppendLine($"| TLAS instance 描述体量 | {ToMB(chunkSet.Count * 64L)}（64B/instance） |");
        sb.AppendLine();

        sb.AppendLine("## 4. DXR 能力探测");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(capability);
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("## 5. 结论提示");
        sb.AppendLine();
        sb.AppendLine("- 选砖尺寸时优先看 **全满砖占比**（IS 一次 slab 直接 ReportHit 的最快路径）与 **非空砖数**（primitive 数）；");
        sb.AppendLine("- primitive 数直接决定 BLAS 体量与构建时间，超过 `VOXRT_MAX_BRICKS_FOR_DXR`(4,000,000) 时插件会拒绝构建并回退 compute；");
        sb.AppendLine("- 改 `VOXRT_BRICK_SIZE` 时必须同步改 `voxel_common.hlsli` 的 `VOXRT_DDA_MAX_STEPS`（8 → 24，16 → 48）与 `VoxRTPluginAPI.h` / `DXRInterop.cs` 里的常量。");
        return sb.ToString();
    }

    private static string WriteReport(string content)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string dir = Path.Combine(projectRoot, "docs");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"砖化统计报告_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        File.WriteAllText(file, content, new UTF8Encoding(false));
        return file;
    }

    // ============================================================
    //  能力探测 / 小工具
    // ============================================================

    /// <summary>
    /// 能力探测。插件尚未 INIT 时主动发一次 INIT（读完后立即 SHUTDOWN 复位），
    /// 这样在编辑器中不必进 Play 也能确认 DXR 是否可用；失败原因取自 RT_GetLastMessage。
    /// 必须复位：否则残留的 INIT_FAILED 会让 Play 首帧的 DXRPathTracer 直接判定不可用。
    /// </summary>
    private static string ProbeCapability()
    {
        try
        {
            int state = DXRInterop.RT_GetState();
            string tempError = null;

            if (state == DXRInterop.StateNotInitialized)
            {
                IssueRawEvent(DXRInterop.EventInit);
                try
                {
                    state = DXRInterop.RT_GetState();
                    if (state < 0) tempError = DXRInterop.GetLastMessageManaged();
                }
                finally
                {
                    IssueRawEvent(DXRInterop.EventShutdown);   // 释放探针创建的资源，状态回 NOT_INITIALIZED
                }
            }

            if (state < 0)
            {
                return $"初始化失败 state={StateName(state)}\n{tempError}\n\n" +
                       "常见原因：图形 API 不是 D3D12（Player Settings 里需只留 Direct3D12）、" +
                       "显卡/驱动不支持 DXR、voxel_pathtracing.dxil 缺失或未签名。";
            }

            DXRInterop.Capability cap;
            if (DXRInterop.RT_GetCapability(out cap) != 0)
                return $"能力查询失败（state={StateName(state)}）：{DXRInterop.GetLastMessageManaged()}";

            var sb = new StringBuilder();
            sb.AppendLine($"state              = {StateName(state)}（探针已自动复位）");
            sb.AppendLine($"raytracingTier     = {cap.raytracingTier}（10 = TIER_1_0，11 = TIER_1_1）");
            sb.AppendLine($"hasRayQuery        = {cap.hasRayQuery}");
            sb.AppendLine($"maxBrickPrimitives = {cap.maxBrickPrimitives:N0}");
            return sb.ToString();
        }
        catch (DllNotFoundException)
        {
            return "未安装 VoxRTPlugin.dll（Assets/Plugins/x86_64/VoxRTPlugin.dll 不存在）——跳过能力探测。";
        }
        catch (EntryPointNotFoundException)
        {
            return "VoxRTPlugin 导出符号不匹配（插件版本过旧，需重新构建）。";
        }
        catch (Exception ex)
        {
            return "能力探测异常：" + ex.Message;
        }
    }

    /// <summary>
    /// 直接向渲染线程提交一条插件事件。data 传 null = 插件默认参数
    /// （HandleInit 已做 null 容错：null 表示 DXIL 取 DLL 同目录）。
    /// </summary>
    private static void IssueRawEvent(int eventId)
    {
        var cb = new CommandBuffer { name = "VoxRT Probe" };
        DXRInterop.IssuePluginEvent(cb, eventId, IntPtr.Zero);
        Graphics.ExecuteCommandBuffer(cb);
        cb.Dispose();
    }

    private static string StateName(int state)
    {
        switch (state)
        {
            case DXRInterop.StateNotInitialized: return "NOT_INITIALIZED";
            case DXRInterop.StateInitSucceeded:  return "INIT_SUCCEEDED";
            case DXRInterop.StateReady:          return "READY";
            case DXRInterop.StateInitFailed:     return "INIT_FAILED";
            case DXRInterop.StateSceneFailed:    return "SCENE_FAILED";
            case DXRInterop.StateDeviceLost:     return "DEVICE_LOST";
            default:                             return $"UNKNOWN({state})";
        }
    }

    /// <summary>砖边长 → 移位位数（4→2, 8→3, 16→4）。</summary>
    private static int ShiftOf(int brickSize)
    {
        int shift = 0;
        while ((1 << shift) < brickSize) shift++;
        return shift;
    }

    /// <summary>三轴各 21 位打包砖坐标（与 VoxRTPlugin 的 PackKey3 同构）。</summary>
    private static long PackKey(int x, int y, int z)
    {
        const int mask = 0x1FFFFF;
        return ((long)(x & mask) << 42) | ((long)(y & mask) << 21) | (long)(z & mask);
    }

    // 解包时必须做 21 位符号扩展：砖坐标可为负（场景原点不一定在包围盒最小角）
    private static int KeyX(long key) => SignExtend21((int)((key >> 42) & 0x1FFFFF));
    private static int KeyY(long key) => SignExtend21((int)((key >> 21) & 0x1FFFFF));
    private static int KeyZ(long key) => SignExtend21((int)(key & 0x1FFFFF));

    private static int SignExtend21(int v) => ((v & 0x100000) != 0) ? v - 0x200000 : v;

    private static string ToMB(long bytes) => $"{bytes / 1048576.0:F1} MB";
}
#endif
