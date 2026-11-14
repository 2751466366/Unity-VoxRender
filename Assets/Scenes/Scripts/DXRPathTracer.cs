using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// DXR 体素路径追踪后端（VoxRTPlugin.dll）的托管侧：状态机 + 事件调度 + 资源生命周期。
///
/// 执行模型
///   - INIT / BUILD_SCENE / SHUTDOWN 这类低频、需要立即生效的操作各自发一条 CommandBuffer
///     并 ExecuteCommandBuffer（与 CameraRender.TryInitDLSS 同风格）；
///   - RENDER 只往调用方传入的 CommandBuffer 里**追加**事件，由 CameraRender 与 DLSS evaluate
///     合并到同一条 CB，保证「DXR 先于 evaluate」的时序；
///   - 所有交给插件的事件数据都放在**持久非托管块**里：渲染线程异步消费，事件返回后不得立即释放。
///     矩阵/向量/描述符走双缓冲轮转，编辑增量块延迟若干帧释放。
///
/// 失败策略
///   任何一步失败 → Phase.Unavailable + 单次 Debug.LogWarning；此后 TryInitialize / TryBuildScene /
///   SubmitRender 全部返回 false，CameraRender 自动回退 SVO_PathTracing.compute（原实现完全保留）。
/// </summary>
public sealed class DXRPathTracer
{
    // ============================================================
    //  常量
    // ============================================================

    /// <summary>矩阵块布局：invVP / curVP / prevVP 各 16 float。</summary>
    private const int MatrixFloats = 16 * 3;
    /// <summary>向量块布局：cameraPos / sunDir / sunColor / skyColor 各占 4 float（对齐用）。</summary>
    private const int VecFloats = 4 * 4;
    /// <summary>描述符双缓冲槽数（渲染线程异步消费，与下一帧主线程覆写错开）。</summary>
    private const int RenderSlotCount = 2;
    /// <summary>编辑增量块延迟释放帧数（远大于命令列表在飞行中的窗口）。</summary>
    private const int EditRetireFrames = 4;
    /// <summary>单帧最多累积的编辑条数（拖刷一次也就几十条，超限说明异常）。</summary>
    private const int MaxPendingEdits = 1 << 14;
    /// <summary>Emissive 材质类型判别阈值（与 SVOBuilder.IsValidEmissive 一致）。</summary>
    private const float EmissiveTypeValue = 3f;

    // ============================================================
    //  状态
    // ============================================================

    /// <summary>后端状态机阶段（仅用于日志与调度；权威状态始终来自 RT_GetState）。</summary>
    public enum Phase
    {
        Idle,          // 尚未发 INIT
        InitPending,   // INIT 已发，等待 RT_GetState 转 INIT_SUCCEEDED
        ScenePending,  // Pipeline 就绪，等待 BUILD_SCENE
        Rendering,     // 场景就绪，可 SubmitRender
        Unavailable,   // 任意一步失败 → 永久回退 compute
    }

    /// <summary>单帧渲染参数（由 CameraRender 填充，避免超长参数表）。</summary>
    public struct RenderParams
    {
        public int   width;        // 内部渲染分辨率（DLSS 档位决定）
        public int   height;
        public int   maxDepth;     // 最大弹射次数
        public int   frameIndex;   // 外部帧号（仅用于日志；插件侧一律改用自有的 renderFrameCount）
        public bool  reset;        // 丢弃累积历史（与 renderFrameCount==0 取或）
        public int   guideMask;    // DXRInterop.GuideMotionDepth / GuideRR 的位或

        public Matrix4x4 invVP;    // 裁剪 → 世界（反投影主射线）
        public Matrix4x4 curVP;    // 当前帧 VP（含 jitter，用于 MV）
        public Matrix4x4 prevVP;   // 上一帧 VP（含 jitter，用于 MV）

        public Vector3 cameraPos;
        public Vector3 sunDir;     // 指向光源
        public Vector3 sunColor;   // 颜色 × 强度
        public bool    hasSun;
        public Vector3 skyColor;   // 无 Cubemap 时的回退天空色
        public Cubemap skyCubemap;

        public RenderTexture colorRT;           // HDR 颜色输出（必需）
        public RenderTexture mvRT;              // 像素空间 MV（SR/RR 需要）
        public RenderTexture depthRT;           // hardware depth（SR/RR 需要）
        public RenderTexture diffuseAlbedoRT;   // RR 引导面
        public RenderTexture specularAlbedoRT;  // RR 引导面
        public RenderTexture normalRoughnessRT; // RR 引导面

        public float jitterX;      // 渲染像素空间抖动
        public float jitterY;
    }

    private Phase phase = Phase.Idle;
    private int   lastState = DXRInterop.StateNotInitialized;
    private bool  initAttempted;
    private bool  sceneDirty = true;
    private bool  editOverflowLogged;
    private int   frameCounter;
    private int   lastReportedBrickCount = -1;
    // DXR 后端**自有的**渲染帧序号（与 CameraRender.pathTracingFrameCount 无关）。
    // 原因：插件累积缓冲随 BUILD_SCENE 重建，若沿用“整个相机生命周期”的帧号，
    //       shader 里 totalSamples = frameIndex+1 会远大于实际样本数 → 画面被稀释成黑色。
    private int   renderFrameCount;

    // ---- 持久非托管块 ----
    private IntPtr initDataPtr  = IntPtr.Zero;
    private IntPtr sceneDescPtr = IntPtr.Zero;
    private readonly IntPtr[] renderDescPtrs = new IntPtr[RenderSlotCount];
    private readonly IntPtr[] matrixPtrs     = new IntPtr[RenderSlotCount];
    private readonly IntPtr[] vecPtrs        = new IntPtr[RenderSlotCount];
    private readonly IntPtr[] voxelDeltaPtrs = new IntPtr[2];
    private int renderSlotIdx;
    private int voxelDeltaIdx;

    // ---- 托管暂存缓冲（避免每帧 GC）----
    private readonly float[] matrixScratch = new float[MatrixFloats];
    private readonly float[] vecScratch    = new float[VecFloats];
    private readonly List<int> pendingEdits = new List<int>(256);   // x,y,z,mat 连续存放
    private readonly List<KeyValuePair<int, IntPtr>> retiredEditBlocks = new List<KeyValuePair<int, IntPtr>>();
    private readonly List<int> emissiveIdx = new List<int>();

    // ---- BUILD_SCENE 的 pin / HGlobal：跨帧释放（渲染线程可能仍在读）----
    private GCHandle buildVoxelPin;
    private GCHandle buildMaterialPin;
    private IntPtr   buildEmissiveLightsPtr = IntPtr.Zero;
    private IntPtr   buildEmissiveAliasPtr  = IntPtr.Zero;

    /// <summary>当前阶段（只读，供 CameraRender 打日志/调试）。</summary>
    public Phase CurrentPhase => phase;

    /// <summary>是否已彻底不可用（此后恒回退 compute）。</summary>
    public bool IsUnavailable => phase == Phase.Unavailable;

    /// <summary>最近一次读取到的插件状态码（调试用）。</summary>
    public int LastPluginState => lastState;

    // ============================================================
    //  对外入口
    // ============================================================

    /// <summary>
    /// 推进 INIT 状态机。首次调用只发事件并返回 false（状态下一帧可见，与 DLSS 集成同一约定）。
    /// 返回 true 表示插件已过 INIT（可继续 TryBuildScene）。
    /// </summary>
    public bool TryInitialize(int outW, int outH)
    {
        if (phase == Phase.Unavailable) return false;

        if (!initAttempted)
        {
            initAttempted = true;
            SetPhase(Phase.InitPending);
            IssueInit(outW, outH);
            return false;
        }

        if (!RefreshState()) return false;

        if (lastState < 0)
        {
            Fail($"INIT 失败(state={lastState}): {DXRInterop.GetLastMessageManaged()}");
            return false;
        }

        if (lastState == DXRInterop.StateReady) return true;

        // INIT_SUCCEEDED：Pipeline 就绪，等 BUILD_SCENE
        if (lastState == DXRInterop.StateInitSucceeded && phase == Phase.InitPending)
        {
            SetPhase(Phase.ScenePending);
            LogCapability();
        }

        return lastState >= DXRInterop.StateInitSucceeded;
    }

    /// <summary>
    /// 需要时发起 BUILD_SCENE（纯 CPU 砖化 + BLAS/TLAS 构建，仅 SVO 全量重建后执行一次）。
    /// 返回值 = 插件是否已 READY **且**场景已构建；发起构建的这一帧返回 false，
    /// 让 CameraRender 当帧回退 compute，下一帧再切到 DXR。
    ///
    /// 说明：rootMin 由本方法按「活跃体素最小角」自算，与 SVOBuilder.Build 的 Phase 1 规则、
    /// 以及插件内部的自算结果三方完全一致，因此无需从 SVOBuilder 读私有字段。
    /// </summary>
    public bool TryBuildScene(IReadOnlyList<VoxelElement> voxels,
                              IReadOnlyList<MaterialElement> materials,
                              float voxelScale)
    {
        if (phase == Phase.Unavailable) return false;
        if (!RefreshState()) return false;

        if (lastState < 0)
        {
            Fail($"插件状态异常(state={lastState}): {DXRInterop.GetLastMessageManaged()}");
            return false;
        }

        if (lastState == DXRInterop.StateReady && !sceneDirty)
        {
            // READY 说明 BUILD_SCENE 已在渲染线程执行完毕，pin/HGlobal 可以放掉。
            // 预热路径（CameraRender.WarmupDXR）之后可能隔很久才 SubmitRender，
            // 若只在 SubmitRender 里释放，pinned 的整份体素数组会长期阻止 GC 压缩。
            ReleaseBuildPins();
            if (phase != Phase.Rendering)
            {
                SetPhase(Phase.Rendering);
                LogStats();
            }
            return true;
        }

        if (!sceneDirty) return false;                                  // INIT 尚未完成
        // INIT_SUCCEEDED = 首次构建；READY = SVO 全量重建后重新构建（插件内部会先释放旧场景）
        if (lastState != DXRInterop.StateInitSucceeded &&
            lastState != DXRInterop.StateReady) return false;

        if (voxels == null || voxels.Count == 0) return false;
        if (materials == null || materials.Count == 0) return false;

        // 本次无论成败都不再重试：失败会通过 Unavailable 兜住，避免每帧刷屏
        sceneDirty = false;
        IssueBuildScene(voxels, materials, voxelScale);
        return false;
    }

    /// <summary>
    /// 往调用方传入的 CommandBuffer 追加 [UPDATE_VOXELS]（若有累积编辑）+ [RENDER]。
    /// **不自己 Execute**：由 CameraRender 与 DLSS evaluate 合并到同一条 CB 后统一提交。
    /// 返回 false 时调用方必须走 compute 分支。
    /// </summary>
    public bool SubmitRender(CommandBuffer cb, in RenderParams p)
    {
        if (cb == null || phase == Phase.Unavailable) return false;
        if (!RefreshState()) return false;
        if (lastState != DXRInterop.StateReady) return false;

        frameCounter++;
        renderFrameCount++;

        // BUILD_SCENE 已跨帧执行完毕，pin/HGlobal 可以安全释放（22M 体素约 352MB，尽早还回去）
        ReleaseBuildPins();
        RetireEditBlocks();

        // 1. 编辑增量必须严格先于 RENDER（同一 CB 内自然有序，且不重建 BLAS）
        if (pendingEdits.Count >= 4) IssueUpdateVoxels(cb);

        // 2. 渲染
        IntPtr descPtr = WriteRenderDesc(p);
        DXRInterop.IssuePluginEvent(cb, DXRInterop.EventRender, descPtr);
        return true;
    }

    /// <summary>
    /// 累积一条体素编辑，下一帧 RENDER 前一次性随 RT_EVENT_UPDATE_VOXELS 发出。
    /// scenePos 是**场景空间绝对整数体素坐标**（= VoxelElement.worldPos，与 gridCoord + rootMinInt 等价）。
    /// matIndex &lt; 0 表示删除。
    /// </summary>
    public void NotifyVoxelRemoved(Vector3Int scenePos, int matIndex = -1)
    {
        if (phase == Phase.Unavailable) return;

        if (pendingEdits.Count / 4 >= MaxPendingEdits)
        {
            if (!editOverflowLogged)
            {
                editOverflowLogged = true;
                Debug.LogWarning($"[DXRPathTracer] 单帧编辑累积超过 {MaxPendingEdits} 条 → 之后的编辑本次丢弃" +
                                 "（修改会在下次 BUILD_SCENE 生效）");
            }
            return;
        }

        pendingEdits.Add(scenePos.x);
        pendingEdits.Add(scenePos.y);
        pendingEdits.Add(scenePos.z);
        pendingEdits.Add(matIndex);
    }

    /// <summary>标记场景需要重建（SVO 全量重建后调用）。下次 TryBuildScene 会重发 BUILD_SCENE。</summary>
    public void RequestSceneRebuild()
    {
        sceneDirty = true;
        if (phase == Phase.Rendering) SetPhase(Phase.ScenePending);
    }

    /// <summary>释放插件资源与全部持久非托管块；可重复调用（OnEnable 会重新走 INIT）。</summary>
    public void Shutdown()
    {
        if (initAttempted && phase != Phase.Unavailable)
        {
            try
            {
                var cb = new CommandBuffer { name = "VoxRT Shutdown" };
                DXRInterop.IssuePluginEvent(cb, DXRInterop.EventShutdown, IntPtr.Zero);
                Graphics.ExecuteCommandBuffer(cb);
                cb.Dispose();
            }
            catch (Exception) { /* 卸载阶段尽力而为 */ }
        }

        ReleaseBuildPins();
        for (int i = 0; i < retiredEditBlocks.Count; i++)
            Marshal.FreeHGlobal(retiredEditBlocks[i].Value);
        retiredEditBlocks.Clear();

        FreeHGlobal(ref initDataPtr);
        FreeHGlobal(ref sceneDescPtr);
        for (int i = 0; i < RenderSlotCount; i++)
        {
            FreeHGlobal(ref renderDescPtrs[i]);
            FreeHGlobal(ref matrixPtrs[i]);
            FreeHGlobal(ref vecPtrs[i]);
        }
        for (int i = 0; i < voxelDeltaPtrs.Length; i++)
            FreeHGlobal(ref voxelDeltaPtrs[i]);

        pendingEdits.Clear();
        emissiveIdx.Clear();
        renderSlotIdx = 0;
        voxelDeltaIdx = 0;
        frameCounter = 0;
        renderFrameCount = 0;
        lastReportedBrickCount = -1;
        editOverflowLogged = false;
        initAttempted = false;
        sceneDirty = true;
        lastState = DXRInterop.StateNotInitialized;
        phase = Phase.Idle;
    }

    // ============================================================
    //  INIT / 状态
    // ============================================================

    private void IssueInit(int outW, int outH)
    {
        try
        {
            if (DXRInterop.RT_GetRenderEventFunc() == IntPtr.Zero)
            {
                lastState = DXRInterop.StateInitFailed;
                Fail("RT_GetRenderEventFunc 返回空（插件未编译或导出名不匹配）");
                return;
            }

            // dxilPath = null → 插件用 GetModuleFileName 定位 DLL 同目录下的 voxel_pathtracing.dxil
            var init = new DXRInterop.InitData { dxilPath = IntPtr.Zero, reserved = 0 };
            initDataPtr = DLSSInterop.AllocStruct(ref init);

            Debug.Log($"[DXRPathTracer] 发起 INIT（输出 {outW}x{outH}，" +
                      $"DLL 目录 {Application.dataPath}/Plugins/x86_64）");

            var cb = new CommandBuffer { name = "VoxRT Init" };
            DXRInterop.IssuePluginEvent(cb, DXRInterop.EventInit, initDataPtr);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Dispose();
        }
        catch (DllNotFoundException)
        {
            lastState = DXRInterop.StateInitFailed;
            Fail("VoxRTPlugin.dll 未找到（先构建插件并拷到 Assets/Plugins/x86_64）");
        }
        catch (EntryPointNotFoundException)
        {
            lastState = DXRInterop.StateInitFailed;
            Fail("VoxRTPlugin 导出符号不匹配（插件版本过旧）");
        }
        catch (Exception ex)
        {
            lastState = DXRInterop.StateInitFailed;
            Fail("INIT 异常: " + ex.Message);
        }
    }

    /// <summary>读取插件状态；DLL 缺失/符号不匹配时直接置 Unavailable。</summary>
    private bool RefreshState()
    {
        try
        {
            lastState = DXRInterop.RT_GetState();
            return true;
        }
        catch (DllNotFoundException)
        {
            lastState = DXRInterop.StateInitFailed;
            Fail("VoxRTPlugin.dll 未找到（先构建插件并拷到 Assets/Plugins/x86_64）");
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            lastState = DXRInterop.StateInitFailed;
            Fail("VoxRTPlugin 导出符号不匹配（插件版本过旧）");
            return false;
        }
        catch (Exception ex)
        {
            lastState = DXRInterop.StateInitFailed;
            Fail("RT_GetState 异常: " + ex.Message);
            return false;
        }
    }

    private void Fail(string reason)
    {
        if (phase == Phase.Unavailable) return;   // 只记一次，避免拖垮帧率
        phase = Phase.Unavailable;
        Debug.LogWarning($"[DXRPathTracer] {reason} → 回退 SVO_PathTracing.compute");
    }

    private void SetPhase(Phase next)
    {
        if (phase == next) return;
        Debug.Log($"[DXRPathTracer] 阶段 {phase} → {next}");
        phase = next;
    }

    /// <summary>插件能力日志（Play 模式下不必再跑编辑器探针也能看到 DXR 是否真在跑）。</summary>
    private void LogCapability()
    {
        try
        {
            if (DXRInterop.RT_GetCapability(out var cap) != 0) return;
            Debug.Log($"[DXRPathTracer] 插件能力：RaytracingTier={cap.raytracingTier}" +
                      $"（10=1.0，11=1.1），hasRayQuery={cap.hasRayQuery}，" +
                      $"maxBrickPrimitives={cap.maxBrickPrimitives:N0}");
        }
        catch (Exception) { /* 纯诊断 */ }
    }

    private void LogStats()
    {
        try
        {
            DXRInterop.RT_GetStats(out var st);
            if (st.brickCount == lastReportedBrickCount) return;
            lastReportedBrickCount = st.brickCount;
            Debug.Log($"[DXRPathTracer] 场景就绪：砖={st.brickCount}" +
                      $"（实心 {st.solidBrickCount}，per-cell {st.perCellBrickCount}），" +
                      $"chunk={st.chunkCount}，BLAS={st.blasBytes / 1048576.0:F1}MB，" +
                      $"TLAS={st.tlasBytes / 1048576.0:F1}MB");
            // 插件侧 BUILD_SCENE 的完整诊断（含砖坐标范围自检）——
            // 排查 DISPATCHRAYS 挂死时必须看到砖坐标是否落在 grid 空间（0 起点）
            Debug.Log($"[DXRPathTracer] 插件消息：{DXRInterop.GetLastMessageManaged()}");
        }
        catch (Exception) { /* 纯诊断，失败忽略 */ }
    }

    // ============================================================
    //  BUILD_SCENE
    // ============================================================

    private void IssueBuildScene(IReadOnlyList<VoxelElement> voxels,
                                 IReadOnlyList<MaterialElement> materials,
                                 float voxelScale)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        renderFrameCount = 0;   // 新场景 = 新累积，帧号从 0 重新开始

        // ---- 1. 一次遍历：求 rootMin（活跃体素最小角）+ 收集发光体素索引 ----
        int mnx = 0, mny = 0, mnz = 0;
        bool anyActive = false;
        emissiveIdx.Clear();

        int n = voxels.Count;
        for (int i = 0; i < n; i++)
        {
            var v = voxels[i];
            if (v.materialIndex < 0) continue;    // 墓碑
            Vector3Int c = v.worldPos;
            if (!anyActive)
            {
                mnx = c.x; mny = c.y; mnz = c.z;
                anyActive = true;
            }
            else
            {
                if (c.x < mnx) mnx = c.x;
                if (c.y < mny) mny = c.y;
                if (c.z < mnz) mnz = c.z;
            }
            if (IsEmissive(materials, v.materialIndex)) emissiveIdx.Add(i);
        }

        if (!anyActive)
        {
            Fail("BUILD_SCENE: 无活跃体素（全部为墓碑）");
            return;
        }

        // ---- 2. 自发光 NEE 光源表（与 SVOBuilder.RebuildEmissiveLights 同算法、同数据格式）----
        int emissiveCount = emissiveIdx.Count;
        float emissivePower = 0f;
        double powerSum = 0.0;
        float[] lightFlat = null;
        float[] aliasFlat = null;

        if (emissiveCount > 0)
        {
            var probs = new float[emissiveCount];
            for (int k = 0; k < emissiveCount; k++)
            {
                var v = voxels[emissiveIdx[k]];
                var m = materials[v.materialIndex];
                double lum = 0.2126 * m.color.r + 0.7152 * m.color.g + 0.0722 * m.color.b;
                double p = Math.Pow(2.0, m.flux) * lum;
                probs[k] = (float)p;
                powerSum += p;
            }

            if (powerSum <= 0.0)
            {
                emissiveCount = 0;   // 全部零功率 → 视为无光源（避免除零）
            }
            else
            {
                var aliases = new int[emissiveCount];
                for (int k = 0; k < emissiveCount; k++) probs[k] = (float)(probs[k] / powerSum);
                BuildAliasTable(probs, aliases);

                emissivePower = (float)powerSum;
                lightFlat = new float[emissiveCount * 4];
                aliasFlat = new float[emissiveCount * 2];
                for (int k = 0; k < emissiveCount; k++)
                {
                    Vector3Int c = voxels[emissiveIdx[k]].worldPos;
                    lightFlat[k * 4 + 0] = c.x - mnx;   // 与 shader 的 grid 空间一致
                    lightFlat[k * 4 + 1] = c.y - mny;
                    lightFlat[k * 4 + 2] = c.z - mnz;
                    // ⚠ 与 SVOBuilder 一致：w 写 (float)materialIndex，
                    //   shader 侧必须用 (int)(w+0.5) 取索引，不能用 asint 按位重解释
                    lightFlat[k * 4 + 3] = voxels[emissiveIdx[k]].materialIndex;
                    aliasFlat[k * 2 + 0] = probs[k];
                    aliasFlat[k * 2 + 1] = aliases[k];
                }
            }
        }

        if (lightFlat != null)
        {
            buildEmissiveLightsPtr = Marshal.AllocHGlobal(lightFlat.Length * sizeof(float));
            Marshal.Copy(lightFlat, 0, buildEmissiveLightsPtr, lightFlat.Length);
            buildEmissiveAliasPtr = Marshal.AllocHGlobal(aliasFlat.Length * sizeof(float));
            Marshal.Copy(aliasFlat, 0, buildEmissiveAliasPtr, aliasFlat.Length);
        }

        // ---- 3. pin 体素/材质数组（插件只读不持有；本方法返回后跨帧释放）----
        VoxelElement[] voxArray = voxels.ToArray();
        MaterialElement[] matArray = materials.ToArray();
        buildVoxelPin = GCHandle.Alloc(voxArray, GCHandleType.Pinned);
        buildMaterialPin = GCHandle.Alloc(matArray, GCHandleType.Pinned);

        // ---- 4. 发事件 ----
        try
        {
            if (sceneDescPtr == IntPtr.Zero)
                sceneDescPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DXRInterop.SceneDesc>());

            var desc = new DXRInterop.SceneDesc
            {
                voxels = buildVoxelPin.AddrOfPinnedObject(),
                voxelCount = voxArray.Length,
                materials = buildMaterialPin.AddrOfPinnedObject(),
                materialCount = matArray.Length,
                emissiveLights = buildEmissiveLightsPtr,
                emissiveAlias = buildEmissiveAliasPtr,
                emissiveCount = emissiveCount,
                emissiveTotalPower = emissivePower,
                rootMinX = mnx,
                rootMinY = mny,
                rootMinZ = mnz,
                voxelScale = (voxelScale > 0f) ? voxelScale : 1f,
                svoDepth = 0,
            };
            DLSSInterop.WriteStruct(sceneDescPtr, ref desc);

            Debug.Log($"[DXRPathTracer] BUILD_SCENE：体素={voxArray.Length}（含墓碑），材质={matArray.Length}，" +
                      $"自发光={emissiveCount}，rootMin=({mnx},{mny},{mnz})，scale={desc.voxelScale}，" +
                      $"pin 内存≈{(voxArray.Length * 16L + matArray.Length * 48L) / 1048576.0:F1}MB，" +
                      $"遍历耗时={sw.ElapsedMilliseconds}ms");

            var cb = new CommandBuffer { name = "VoxRT BuildScene" };
            DXRInterop.IssuePluginEvent(cb, DXRInterop.EventBuildScene, sceneDescPtr);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Dispose();
        }
        catch (Exception ex)
        {
            ReleaseBuildPins();
            Fail("BUILD_SCENE 提交异常: " + ex.Message);
        }
    }

    /// <summary>释放 BUILD_SCENE 的 pin / 自发光 HGlobal（跨帧调用，渲染线程此时已消费完）。</summary>
    private void ReleaseBuildPins()
    {
        if (buildVoxelPin.IsAllocated) buildVoxelPin.Free();
        if (buildMaterialPin.IsAllocated) buildMaterialPin.Free();
        FreeHGlobal(ref buildEmissiveLightsPtr);
        FreeHGlobal(ref buildEmissiveAliasPtr);
    }

    /// <summary>索引是否为有效发光材质（非墓碑、材质 Emissive）。</summary>
    private static bool IsEmissive(IReadOnlyList<MaterialElement> materials, int matIndex)
    {
        if (matIndex < 0 || matIndex >= materials.Count) return false;
        return Mathf.Abs(materials[matIndex].type - EmissiveTypeValue) < 0.5f;
    }

    /// <summary>
    /// 构建 alias method 采样表（Vose's alias method）。
    /// 与 SVOBuilder.BuildAliasTable 逐行等价，保证两后端 NEE 选点分布一致（便于对拍）。
    /// </summary>
    private static void BuildAliasTable(float[] probs, int[] aliases)
    {
        int n = probs.Length;
        if (n <= 0) return;

        var scaled = new float[n];
        for (int i = 0; i < n; i++) scaled[i] = probs[i] * n;

        var small = new Stack<int>();
        var large = new Stack<int>();
        for (int i = 0; i < n; i++)
        {
            if (scaled[i] < 1.0f) small.Push(i);
            else large.Push(i);
        }

        while (small.Count > 0 && large.Count > 0)
        {
            int s = small.Pop();
            int l = large.Pop();
            probs[s] = scaled[s];
            aliases[s] = l;
            scaled[l] = (scaled[l] + scaled[s]) - 1.0f;
            if (scaled[l] < 1.0f) small.Push(l);
            else large.Push(l);
        }

        while (large.Count > 0) { int l = large.Pop(); probs[l] = 1.0f; }
        while (small.Count > 0) { int s = small.Pop(); probs[s] = 1.0f; }
    }

    // ============================================================
    //  UPDATE_VOXELS / RENDER
    // ============================================================

    private void IssueUpdateVoxels(CommandBuffer cb)
    {
        int count = pendingEdits.Count / 4;
        if (count <= 0) { pendingEdits.Clear(); return; }

        int[] flat = pendingEdits.ToArray();
        pendingEdits.Clear();

        IntPtr block = Marshal.AllocHGlobal(flat.Length * sizeof(int));
        Marshal.Copy(flat, 0, block, flat.Length);
        // 渲染线程异步消费 → 不能立即释放；记录发起帧号，延迟 EditRetireFrames 帧回收
        retiredEditBlocks.Add(new KeyValuePair<int, IntPtr>(frameCounter, block));

        int slot = 1 - voxelDeltaIdx;
        voxelDeltaIdx = slot;
        if (voxelDeltaPtrs[slot] == IntPtr.Zero)
            voxelDeltaPtrs[slot] = Marshal.AllocHGlobal(Marshal.SizeOf<DXRInterop.VoxelDelta>());

        var delta = new DXRInterop.VoxelDelta { edits = block, editCount = count };
        DLSSInterop.WriteStruct(voxelDeltaPtrs[slot], ref delta);
        DXRInterop.IssuePluginEvent(cb, DXRInterop.EventUpdateVoxels, voxelDeltaPtrs[slot]);
    }

    private void RetireEditBlocks()
    {
        for (int i = retiredEditBlocks.Count - 1; i >= 0; i--)
        {
            if (frameCounter - retiredEditBlocks[i].Key < EditRetireFrames) continue;
            Marshal.FreeHGlobal(retiredEditBlocks[i].Value);
            retiredEditBlocks.RemoveAt(i);
        }
    }

    /// <summary>把参数写进持久块（双缓冲轮转）并返回 RTRenderDesc 指针。</summary>
    private IntPtr WriteRenderDesc(in RenderParams p)
    {
        int slot = 1 - renderSlotIdx;
        renderSlotIdx = slot;

        if (matrixPtrs[slot] == IntPtr.Zero)
            matrixPtrs[slot] = Marshal.AllocHGlobal(MatrixFloats * sizeof(float));
        if (vecPtrs[slot] == IntPtr.Zero)
            vecPtrs[slot] = Marshal.AllocHGlobal(VecFloats * sizeof(float));
        if (renderDescPtrs[slot] == IntPtr.Zero)
            renderDescPtrs[slot] = Marshal.AllocHGlobal(Marshal.SizeOf<DXRInterop.RenderDesc>());

        // 矩阵：列主序（Unity 内存序），插件侧 memcpy 进 cbuffer，HLSL 按 column_major 读
        DXRInterop.FlattenMatrixColumnMajor(p.invVP, matrixScratch, 0);
        DXRInterop.FlattenMatrixColumnMajor(p.curVP, matrixScratch, 16);
        DXRInterop.FlattenMatrixColumnMajor(p.prevVP, matrixScratch, 32);
        Marshal.Copy(matrixScratch, 0, matrixPtrs[slot], MatrixFloats);

        // 向量：每个占 4 float（插件只读前 3 个分量）
        vecScratch[0] = p.cameraPos.x; vecScratch[1] = p.cameraPos.y; vecScratch[2] = p.cameraPos.z; vecScratch[3] = 0f;
        vecScratch[4] = p.sunDir.x;    vecScratch[5] = p.sunDir.y;    vecScratch[6] = p.sunDir.z;    vecScratch[7] = 0f;
        vecScratch[8] = p.sunColor.x;  vecScratch[9] = p.sunColor.y;  vecScratch[10] = p.sunColor.z; vecScratch[11] = 0f;
        vecScratch[12] = p.skyColor.x; vecScratch[13] = p.skyColor.y; vecScratch[14] = p.skyColor.z; vecScratch[15] = 0f;
        Marshal.Copy(vecScratch, 0, vecPtrs[slot], VecFloats);

        IntPtr matBase = matrixPtrs[slot];
        IntPtr vecBase = vecPtrs[slot];
        IntPtr skyPtr = (p.skyCubemap != null) ? p.skyCubemap.GetNativeTexturePtr() : IntPtr.Zero;

        var desc = new DXRInterop.RenderDesc
        {
            invVP = matBase,
            curVP = OffsetFloats(matBase, 16),
            prevVP = OffsetFloats(matBase, 32),
            cameraPos = vecBase,
            sunDir = OffsetFloats(vecBase, 4),
            sunColor = OffsetFloats(vecBase, 8),
            skyColor = OffsetFloats(vecBase, 12),
            hasSun = p.hasSun ? 1 : 0,
            hasSkyCube = (skyPtr != IntPtr.Zero) ? 1 : 0,
            skyCubemap = skyPtr,
            colorRT = TexPtr(p.colorRT),
            mvRT = TexPtr(p.mvRT),
            depthRT = TexPtr(p.depthRT),
            diffuseAlbedoRT = TexPtr(p.diffuseAlbedoRT),
            specularAlbedoRT = TexPtr(p.specularAlbedoRT),
            normalRoughnessRT = TexPtr(p.normalRoughnessRT),
            jitterX = p.jitterX,
            jitterY = p.jitterY,
            width = Mathf.Max(p.width, 1),
            height = Mathf.Max(p.height, 1),
            maxDepth = Mathf.Clamp(p.maxDepth, 1, 16),
            // frameIndex/reset 一律用 DXR 自有序号：接管的那一帧必然 reset，
            // 之后逐帧递增，保证 shader 的累积分母与实际样本数一致
            frameIndex = Mathf.Max(renderFrameCount, 0),
            reset = (p.reset || renderFrameCount == 0) ? 1 : 0,
            writeGuides = p.guideMask,
        };
        DLSSInterop.WriteStruct(renderDescPtrs[slot], ref desc);
        return renderDescPtrs[slot];
    }

    // ============================================================
    //  小工具
    // ============================================================

    private static IntPtr TexPtr(RenderTexture rt) => (rt != null) ? rt.GetNativeTexturePtr() : IntPtr.Zero;

    private static IntPtr OffsetFloats(IntPtr basePtr, int floatCount) =>
        new IntPtr(basePtr.ToInt64() + floatCount * sizeof(float));

    private static void FreeHGlobal(ref IntPtr p)
    {
        if (p != IntPtr.Zero) { Marshal.FreeHGlobal(p); p = IntPtr.Zero; }
    }
}
