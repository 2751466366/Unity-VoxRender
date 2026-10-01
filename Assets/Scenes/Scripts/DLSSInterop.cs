using System;
using System.Runtime.InteropServices;
using UnityEngine.Rendering;

/// <summary>
/// DLSSPlugin.dll 的 P/Invoke 封装。
///
/// 契约对齐:NativePlugins/DLSSPlugin/src/DLSSPluginAPI.h
///   - 结构体字段顺序/宽度必须与 C++ 完全一致(LayoutKind.Sequential,默认 Pack)
///   - GPU 操作(Init/CreateFeature/Evaluate)必须经 CommandBuffer.IssuePluginEventAndData
///     在 Unity 渲染线程执行;查询接口(GetState 等)主线程随时可调
///
/// 数据指针生命周期:
///   - Init 的 wchar_t* 路径缓冲:NGX 可能保留指针,必须持久持有(HGlobal,Shutdown 后释放)
///   - Create/Eval 结构体:双缓冲(HGlobal×2)交替写入,渲染线程消费与主线程覆写不重叠
/// </summary>
public static class DLSSInterop
{
    // ---- 渲染事件 ID(与 DLSSPluginAPI.h 一致)----
    public const int EventInit           = unchecked((int)0x44530001);
    public const int EventCreateFeature  = unchecked((int)0x44530002);
    public const int EventEvaluate       = unchecked((int)0x44530003);
    public const int EventReleaseFeature = unchecked((int)0x44530004);
    public const int EventShutdown       = unchecked((int)0x44530005);

    // ---- 插件状态(DLSS_GetState 返回值)----
    public const int StateNotInitialized = 0;
    public const int StateInitSucceeded  = 1;
    public const int StateFeatureReady   = 2;
    public const int StateInitFailed     = -1;
    public const int StateFeatureFailed  = -2;

    // ---- 质量模式(对应 NVSDK_NGX_PerfQuality_Value)----
    public const int QualityMaxPerf      = 0;
    public const int QualityBalanced     = 1;
    public const int QualityMaxQuality   = 2;
    public const int QualityUltraPerf    = 3;
    public const int QualityUltraQuality = 4;
    public const int QualityDLAA         = 5;   // 100% 渲染分辨率,仅降噪 + AA

    // ---- Feature 创建 flags(对应 NVSDK_NGX_DLSS_Feature_Flags)----
    public const int FlagIsHDR         = 1 << 0;
    public const int FlagMVLowRes      = 1 << 1;  // MV 为渲染分辨率(非输出分辨率)
    public const int FlagMVJittered    = 1 << 2;  // 采样位置含子像素抖动
    public const int FlagDepthInverted = 1 << 3;  // 近=1 远=0(reversed-Z);Unity D3D12 默认非反转,不设

    // ---- DLSS_EVENT_INIT:路径为持久 UTF-16 缓冲指针 ----
    [StructLayout(LayoutKind.Sequential)]
    public struct InitData
    {
        public IntPtr dllSearchPath;   // const wchar_t*;NGX 保留指针,缓冲必须持久
        public IntPtr appDataPath;     // const wchar_t*
        public ulong  appId;
    }

    // ---- DLSS_EVENT_CREATE_FEATURE ----
    [StructLayout(LayoutKind.Sequential)]
    public struct CreateData
    {
        public uint inputWidth;        // 渲染分辨率
        public uint inputHeight;
        public uint outputWidth;       // 输出分辨率
        public uint outputHeight;
        public int  perfQuality;       // Quality*
        public int  featureFlags;      // Flag* 组合
    }

    // ---- DLSS_EVENT_EVALUATE ----
    [StructLayout(LayoutKind.Sequential)]
    public struct EvalData
    {
        public IntPtr color;           // ID3D12Resource*:低分辨率 HDR 颜色
        public IntPtr motionVectors;   // ID3D12Resource*:屏幕空间 MV(像素单位)
        public IntPtr depth;           // ID3D12Resource*:hardware depth [0,1]
        public IntPtr output;          // ID3D12Resource*:DLSS 输出
        public float jitterX;          // 子像素抖动(-0.5~0.5,与 shader 相位符号相反)
        public float jitterY;
        public float mvScaleX;         // 0 = 使用默认 1.0
        public float mvScaleY;
        public uint  subrectWidth;     // 实际渲染尺寸
        public uint  subrectHeight;
        public int   reset;            // 非 0 = 丢弃历史(分辨率变化/场景切换)
    }

    // ---- C 导出接口 ----
    [DllImport("DLSSPlugin")] public static extern IntPtr DLSS_GetRenderEventFunc();
    [DllImport("DLSSPlugin")] public static extern int    DLSS_GetState();
    [DllImport("DLSSPlugin")] public static extern int    DLSS_GetOptimalSettings(
        int outputWidth, int outputHeight, int perfQuality,
        out int outOptimalWidth, out int outOptimalHeight);
    [DllImport("DLSSPlugin")] public static extern IntPtr DLSS_GetLastMessage(); // const char*

    /// <summary>读取插件最近诊断信息(ANSI)。</summary>
    public static string GetLastMessageManaged()
    {
        try
        {
            IntPtr p = DLSS_GetLastMessage();
            return (p != IntPtr.Zero) ? Marshal.PtrToStringAnsi(p) : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// 把渲染事件插入 Unity 命令流(渲染线程执行,与前后 dispatch 严格保序)。
    /// </summary>
    public static void IssuePluginEvent(CommandBuffer cb, int eventId, IntPtr data)
    {
        cb.IssuePluginEventAndData(DLSS_GetRenderEventFunc(), eventId, data);
    }

    // ---- 封送辅助 ----

    /// <summary>分配 UTF-16(null 结尾)HGlobal 缓冲,调用方负责 FreeHGlobal。</summary>
    public static IntPtr AllocWideString(string s)
    {
        if (string.IsNullOrEmpty(s)) return Marshal.AllocHGlobal(2);
        IntPtr p = Marshal.AllocHGlobal((s.Length + 1) * 2);
        Marshal.Copy(s.ToCharArray(), 0, p, s.Length);
        Marshal.WriteInt16(p, s.Length * 2, 0);
        return p;
    }

    /// <summary>分配结构体 HGlobal 并写入初值,调用方负责 FreeHGlobal。</summary>
    public static IntPtr AllocStruct<T>(ref T value) where T : struct
    {
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, p, false);
        return p;
    }

    public static void WriteStruct<T>(IntPtr p, ref T value) where T : struct
    {
        Marshal.StructureToPtr(value, p, false);
    }
}
