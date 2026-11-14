using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// VoxRTPlugin.dll 的 P/Invoke 封装（DXR 体素路径追踪后端）。
///
/// 契约对齐:NativePlugins/VoxRTPlugin/src/VoxRTPluginAPI.h
///   - 结构体字段顺序/宽度必须与 C++ 完全一致(LayoutKind.Sequential, 默认 Pack)
///   - GPU 操作(BUILD_SCENE / UPDATE_VOXELS / RENDER)必须经
///     CommandBuffer.IssuePluginEventAndData 在 Unity 渲染线程执行；
///     查询接口(GetState / GetCapability / GetStats)主线程随时可调
///
/// 数据指针生命周期:
///   - BUILD_SCENE 的体素/材质/自发光数组由 C# 侧 pin 住，事件返回即释放
///   - RENDER 的矩阵块为持久 HGlobal（渲染线程异步消费），由 DXRPathTracer 持有
///
/// 封送辅助一律复用 DLSSInterop 的既有实现（AllocStruct / WriteStruct / AllocWideString），
/// 不在本文件重复实现。
/// </summary>
public static class DXRInterop
{
    // ---- 编译期常量(必须与 VoxRTPluginAPI.h / voxel_common.hlsli 三处一致)----
    public const int BrickSize = 8;
    public const int BrickCells = 512;
    public const int BrickOccU32 = 16;
    public const int ChunkSize = 64;
    public const int BricksPerChunkAxis = 8;
    public const int BricksPerChunk = 512;
    public const int MaxBricksForDXR = 4000000;

    // ---- RenderDesc.writeGuides 位掩码(与 VOXRT_GUIDE_* 一致)----
    /// <summary>写 MV(u1) + 深度(u2)。DLSS SR / RR 都必须置位。</summary>
    public const int GuideMotionDepth = 1 << 0;
    /// <summary>写 RR 引导面(u3/u4/u5) + 大幅放宽萤火虫钳制。仅 RR 模式置位。</summary>
    public const int GuideRR          = 1 << 1;

    // ---- 渲染事件 ID(与 VoxRTPluginAPI.h 一致)----
    public const int EventInit         = unchecked((int)0x56540001);
    public const int EventBuildScene   = unchecked((int)0x56540002);
    public const int EventUpdateVoxels = unchecked((int)0x56540003);
    public const int EventRender       = unchecked((int)0x56540004);
    public const int EventReleaseScene = unchecked((int)0x56540005);
    public const int EventShutdown     = unchecked((int)0x56540006);

    // ---- 插件状态(RT_GetState 返回值)----
    public const int StateNotInitialized = 0;
    public const int StateInitSucceeded  = 1;
    public const int StateReady          = 2;
    public const int StateInitFailed     = -1;
    public const int StateSceneFailed    = -2;
    public const int StateDeviceLost     = -3;

    // ---- 事件数据结构 ----

    /// <summary>RT_EVENT_INIT：dxilPath 为持久 UTF-16 缓冲指针（null = DLL 同目录）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InitData
    {
        public IntPtr dxilPath;
        public int    reserved;
    }

    /// <summary>RT_EVENT_BUILD_SCENE：体素/材质数组为 pin 内存，插件只读不持有。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SceneDesc
    {
        public IntPtr voxels;             // VoxelElement[16B]
        public int    voxelCount;
        public IntPtr materials;          // MaterialElement[48B]
        public int    materialCount;
        public IntPtr emissiveLights;     // float4[]
        public IntPtr emissiveAlias;      // float2[]
        public int    emissiveCount;
        public float  emissiveTotalPower;
        public int    rootMinX;
        public int    rootMinY;
        public int    rootMinZ;
        public float  voxelScale;
        public int    svoDepth;
    }

    /// <summary>
    /// RT_EVENT_UPDATE_VOXELS：int4 { x, y, z, matIndex(-1=删除) }。
    /// (x,y,z) 是**场景空间绝对整数体素坐标**（与 VoxelElement.worldPos 同源，
    /// 不是相对 rootMin 的 grid 坐标）；插件内部自行减 rootMin 后再定位砖。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VoxelDelta
    {
        public IntPtr edits;
        public int    editCount;
    }

    /// <summary>RT_EVENT_RENDER：矩阵指针必须指向持久 16-float 块（列主序）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RenderDesc
    {
        public IntPtr invVP;
        public IntPtr curVP;
        public IntPtr prevVP;
        public IntPtr cameraPos;
        public IntPtr sunDir;
        public IntPtr sunColor;
        public IntPtr skyColor;
        public int    hasSun;
        public int    hasSkyCube;
        public IntPtr skyCubemap;
        public IntPtr colorRT;
        public IntPtr mvRT;
        public IntPtr depthRT;
        public IntPtr diffuseAlbedoRT;
        public IntPtr specularAlbedoRT;
        public IntPtr normalRoughnessRT;
        public float  jitterX;
        public float  jitterY;
        public int    width;
        public int    height;
        public int    maxDepth;
        public int    frameIndex;
        public int    reset;
        /// <summary>引导面位掩码(GuideMotionDepth | GuideRR)，0 = 纯时域累积回退。</summary>
        public int    writeGuides;
    }

    /// <summary>能力信息（只读查询）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Capability
    {
        public int raytracingTier;
        public int hasRayQuery;
        public int maxBrickPrimitives;
        public int reserved;
    }

    /// <summary>场景统计（只读查询）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public int   brickCount;
        public int   solidBrickCount;
        public int   chunkCount;
        public int   perCellBrickCount;
        public long  blasBytes;
        public long  tlasBytes;
    }

    // ---- C 导出接口 ----
    [DllImport("VoxRTPlugin")] public static extern IntPtr RT_GetRenderEventFunc();
    [DllImport("VoxRTPlugin")] public static extern int    RT_GetState();
    [DllImport("VoxRTPlugin")] public static extern int    RT_GetCapability(out Capability outCapability);
    [DllImport("VoxRTPlugin")] public static extern IntPtr RT_GetLastMessage(); // const char*
    [DllImport("VoxRTPlugin")] public static extern void   RT_GetStats(out Stats outStats);

    /// <summary>读取插件最近诊断信息(ANSI)。</summary>
    public static string GetLastMessageManaged()
    {
        try
        {
            IntPtr p = RT_GetLastMessage();
            return (p != IntPtr.Zero) ? Marshal.PtrToStringAnsi(p) : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>把渲染事件插入 Unity 命令流(渲染线程执行，与前后 dispatch 严格保序)。</summary>
    public static void IssuePluginEvent(CommandBuffer cb, int eventId, IntPtr data)
    {
        cb.IssuePluginEventAndData(RT_GetRenderEventFunc(), eventId, data);
    }

    /// <summary>
    /// 把 Unity Matrix4x4 按**列主序**（Unity 自身内存序）写入 16 float 持久块。
    /// 插件侧 HLSL 以默认 column_major 读取，与 Shader.SetMatrix 行为一致。
    /// </summary>
    public static void FlattenMatrixColumnMajor(Matrix4x4 m, float[] dst, int offset)
    {
        dst[offset + 0]  = m.m00; dst[offset + 1]  = m.m10; dst[offset + 2]  = m.m20; dst[offset + 3]  = m.m30;
        dst[offset + 4]  = m.m01; dst[offset + 5]  = m.m11; dst[offset + 6]  = m.m21; dst[offset + 7]  = m.m31;
        dst[offset + 8]  = m.m02; dst[offset + 9]  = m.m12; dst[offset + 10] = m.m22; dst[offset + 11] = m.m32;
        dst[offset + 12] = m.m03; dst[offset + 13] = m.m13; dst[offset + 14] = m.m23; dst[offset + 15] = m.m33;
    }
}
