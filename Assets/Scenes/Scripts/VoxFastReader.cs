using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 零拷贝 VOX 解析器（性能优化 P0-1 / P0-2 / P0-3）。
///
/// 与 VoxReader（com.sandrofigo.voxreader）的差别：
///   - 旧实现每解析一个 chunk 就 new byte[] 复制"从该 chunk 到 children 区末尾"的全部字节，
///     实测 castle-all.vox（88MB / 3899 chunk）累计复制 38.37GB，是加载慢的主因；
///   - 本实现只记录 (offset, length)，用 int 游标在原 byte[] 上滑动，**不产生任何切片副本**；
///   - 不构造 Voxel[] / RawVoxel[] / Content 中间对象（22M 体素原本要占 ~1.1GB），
///     直接按最终布局写入 VoxelElement 列表；
///   - 场景图父链查询改为字典索引 + 记忆化递归（O(N)），不再对每个模型线性扫描 nGRP×nTRN。
///
/// 语义与旧实现保持一致：
///   - Vox→Unity 坐标交换：Unity(x,y,z) = Vox(x,z,y)；
///   - 模型中心 = Helper.ExtractModels 的 tight bounds 中心（含 Matrix3.RotateIndex 的 +0.5-floor 语义）；
///   - 体素世界坐标 = parentOffset + chunkOffset + RoundToInt(globalRot * (voxel - center))；
///   - 材质索引 = matBase + raw palette index（IMAP 双向映射的净效果即原始索引，故省略）；
///   - 材质表 = 256 项 palette 默认 Diffuse + MATL chunk 覆盖。
///
/// 解析失败（文件损坏 / 不支持的布局）返回 null，由调用方回退到 VoxReader 路径。
/// </summary>
public static class VoxFastReader
{
    private const int PaletteSlots = 256;

    /// <summary>解析结果摘要（体素已写入调用方传入的列表）。</summary>
    public sealed class Result
    {
        public int  voxelCount;      // 本次写入的体素数
        public int  skippedVoxels;   // 越界/非法体素（被跳过）
        public int  modelCount;      // 参与输出的模型数
        public long elapsedMs;
        public MaterialElement[] materials; // 本资产的 256 项材质（未加 matBase）
    }

    // ---- VOX chunk 内常见的 4 字节 tag（小端 int，避免每个 chunk 分配字符串） ----
    private const int TagMAIN = ('M') | ('A' << 8) | ('I' << 16) | ('N' << 24);
    private const int TagSIZE = ('S') | ('I' << 8) | ('Z' << 16) | ('E' << 24);
    private const int TagXYZI = ('X') | ('Y' << 8) | ('Z' << 16) | ('I' << 24);
    private const int TagRGBA = ('R') | ('G' << 8) | ('B' << 16) | ('A' << 24);
    private const int TagMATL = ('M') | ('A' << 8) | ('T' << 16) | ('L' << 24);
    private const int TagnTRN = ('n') | ('T' << 8) | ('R' << 16) | ('N' << 24);
    private const int TagnGRP = ('n') | ('G' << 8) | ('R' << 16) | ('P' << 24);
    private const int TagnSHP = ('n') | ('S' << 8) | ('H' << 16) | ('P' << 24);
    private const int TagIMAP = ('I') | ('M' << 8) | ('A' << 16) | ('P' << 24);

    private struct ChunkRef
    {
        public int contentOffset;
        public int contentLength;
    }

    private struct TrnInfo
    {
        public int  nodeId;
        public int  childId;
        public int  rotationCode;   // frame[0]._r 编码（0x00..0xFF）；-1 = 无 _r（单位矩阵）
        public int  tx, ty, tz;     // frame[0]._t
    }

    /// <summary>
    /// 解析 .vox 并把体素追加到 <paramref name="outVoxels"/>。
    /// </summary>
    /// <param name="absPath">绝对路径</param>
    /// <param name="matBase">本资产材质在全局 materialElements 中的起始索引</param>
    /// <param name="parentOffset">父节点整数偏移（SVO 需要整数对齐）</param>
    /// <param name="outVoxels">输出列表（直接追加，容量会被预分配）</param>
    /// <returns>成功返回结果摘要；失败/不支持返回 null（调用方应回退 VoxReader）</returns>
    public static Result Read(string absPath, int matBase, Vector3Int parentOffset,
                              List<VoxelElement> outVoxels)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[] data;
        try { data = File.ReadAllBytes(absPath); }
        catch (Exception ex)
        {
            Debug.LogError($"[VoxFastReader] 读取失败: {absPath} ({ex.Message})");
            return null;
        }

        if (!IsVoxHeader(data))
        {
            Debug.LogWarning($"[VoxFastReader] 文件头非法，回退 VoxReader: {absPath}");
            return null;
        }

        var sizes  = new List<ChunkRef>();
        var xyzis  = new List<ChunkRef>();
        var trns   = new List<ChunkRef>();
        var grps   = new List<ChunkRef>();
        var shps   = new List<ChunkRef>();
        var matls  = new List<ChunkRef>();
        bool hasRgba = false;
        byte[] rgba = null;

        if (!ScanChildren(data, sizes, xyzis, trns, grps, shps, matls, ref hasRgba, ref rgba))
        {
            Debug.LogWarning($"[VoxFastReader] chunk 结构异常，回退 VoxReader: {absPath}");
            return null;
        }

        if (sizes.Count == 0 || xyzis.Count == 0)
        {
            Debug.LogWarning($"[VoxFastReader] 缺少 SIZE/XYZI，回退 VoxReader: {absPath}");
            return null;
        }

        // VoxReader 的 Palette 直接取 RGBA chunk（缺省会 NRE），故缺 RGBA 时同样回退，保证颜色逐位一致
        if (!hasRgba)
        {
            Debug.LogWarning($"[VoxFastReader] 缺少 RGBA palette，回退 VoxReader: {absPath}");
            return null;
        }

        var materials = BuildMaterials(data, matls, rgba);
        int startCount = outVoxels.Count;
        int skipped = 0;
        int modelCount;

        if (trns.Count == 0 && shps.Count == 0)
        {
            // 无场景图（MagicaVoxel "export" 而非 "save"）：每个模型使用原点 + 单位旋转
            int modelTotal = Mathf.Min(sizes.Count, xyzis.Count);
            ReserveCapacity(outVoxels, data, sizes, xyzis, modelTotal, startCount, i => i);
            for (int id = 0; id < modelTotal; id++)
            {
                EmitModel(data, sizes[id], xyzis[id], id,
                          new VoxReader.Vector3(0, 0, 0), VoxReader.Matrix3.Identity,
                          matBase, parentOffset, outVoxels, ref skipped);
            }
            modelCount = modelTotal;
        }
        else
        {
            modelCount = ReadSceneGraph(data, trns, grps, shps,
                                        sizes, xyzis, matBase, parentOffset,
                                        outVoxels, ref skipped);
        }

        sw.Stop();
        return new Result
        {
            voxelCount    = outVoxels.Count - startCount,
            skippedVoxels = skipped,
            modelCount    = modelCount,
            elapsedMs     = sw.ElapsedMilliseconds,
            materials     = materials,
        };
    }

    // ============================================================
    //  文件头 / chunk 扫描（零拷贝）
    // ============================================================

    private static bool IsVoxHeader(byte[] data)
    {
        return data != null && data.Length >= 20
               && data[0] == 'V' && data[1] == 'O' && data[2] == 'X' && data[3] == ' ';
    }

    /// <summary>遍历 MAIN 的 children，记录各类型 chunk 的 (offset,length)，不复制任何字节。</summary>
    private static bool ScanChildren(byte[] data,
                                     List<ChunkRef> sizes, List<ChunkRef> xyzis,
                                     List<ChunkRef> trns, List<ChunkRef> grps,
                                     List<ChunkRef> shps, List<ChunkRef> matls,
                                     ref bool hasRgba, ref byte[] rgba)
    {
        int p = 8, end = data.Length;
        while (p + 12 <= end)
        {
            int tag = ReadTag(data, p);
            int contentLength  = BitConverter.ToInt32(data, p + 4);
            int childrenLength = BitConverter.ToInt32(data, p + 8);

            if (contentLength < 0 || childrenLength < 0) return false;
            int contentOffset = p + 12;
            if (contentOffset + contentLength + childrenLength > end) return false;

            var cr = new ChunkRef { contentOffset = contentOffset, contentLength = contentLength };
            switch (tag)
            {
                case TagMAIN:
                    // MAIN 的 children 在 content 之后，单独扫描（避免递归层数限制）
                    if (!ScanChildrenRange(data, contentOffset + contentLength, childrenLength,
                                           sizes, xyzis, trns, grps, shps, matls, ref hasRgba, ref rgba))
                        return false;
                    break;
                case TagSIZE: sizes.Add(cr); break;
                case TagXYZI: xyzis.Add(cr); break;
                case TagnTRN: trns.Add(cr);  break;
                case TagnGRP: grps.Add(cr);  break;
                case TagnSHP: shps.Add(cr);  break;
                case TagMATL: matls.Add(cr); break;
                case TagIMAP: break; // IMAP 的净效果等价于原始索引，无需处理
                case TagRGBA:
                    if (contentLength >= PaletteSlots * 4)
                    {
                        rgba = new byte[PaletteSlots * 4];
                        Buffer.BlockCopy(data, contentOffset, rgba, 0, rgba.Length);
                        hasRgba = true;
                    }
                    break;
                default:
                    break; // NOTE / LAYR / rOBJ / rCAM 等忽略
            }

            p = contentOffset + contentLength + childrenLength;
        }
        return true;
    }

    private static bool ScanChildrenRange(byte[] data, int start, int length,
                                          List<ChunkRef> sizes, List<ChunkRef> xyzis,
                                          List<ChunkRef> trns, List<ChunkRef> grps,
                                          List<ChunkRef> shps, List<ChunkRef> matls,
                                          ref bool hasRgba, ref byte[] rgba)
    {
        int p = start, end = start + length;
        while (p + 12 <= end)
        {
            int tag = ReadTag(data, p);
            int contentLength  = BitConverter.ToInt32(data, p + 4);
            int childrenLength = BitConverter.ToInt32(data, p + 8);
            if (contentLength < 0 || childrenLength < 0) return false;
            int contentOffset = p + 12;
            if (contentOffset + contentLength + childrenLength > end) return false;

            var cr = new ChunkRef { contentOffset = contentOffset, contentLength = contentLength };
            switch (tag)
            {
                case TagMAIN: break;
                case TagSIZE: sizes.Add(cr); break;
                case TagXYZI: xyzis.Add(cr); break;
                case TagnTRN: trns.Add(cr);  break;
                case TagnGRP: grps.Add(cr);  break;
                case TagnSHP: shps.Add(cr);  break;
                case TagMATL: matls.Add(cr); break;
                case TagIMAP: break;
                case TagRGBA:
                    if (contentLength >= PaletteSlots * 4)
                    {
                        rgba = new byte[PaletteSlots * 4];
                        Buffer.BlockCopy(data, contentOffset, rgba, 0, rgba.Length);
                        hasRgba = true;
                    }
                    break;
                default: break;
            }
            p = contentOffset + contentLength + childrenLength;
        }
        return true;
    }

    private static int ReadTag(byte[] d, int offset)
    {
        return d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16) | (d[offset + 3] << 24);
    }

    // ============================================================
    //  材质表（palette 默认 Diffuse + MATL 覆盖）
    // ============================================================

    private static MaterialElement[] BuildMaterials(byte[] data, List<ChunkRef> matls, byte[] rgba)
    {
        var mats = new MaterialElement[PaletteSlots];

        // Phase A: 256 槽默认 Diffuse，颜色取 raw palette（与旧实现一致：GammaToLinearSpace）
        for (int i = 0; i < PaletteSlots; i++)
        {
            var me = new MaterialElement { type = 0f };
            byte r = 255, g = 255, b = 255, a = 255;
            if (rgba != null)
            {
                r = rgba[i * 4 + 0];
                g = rgba[i * 4 + 1];
                b = rgba[i * 4 + 2];
                a = rgba[i * 4 + 3];
            }
            me.color = new Color(
                Mathf.GammaToLinearSpace(r / 255f),
                Mathf.GammaToLinearSpace(g / 255f),
                Mathf.GammaToLinearSpace(b / 255f),
                a / 255f);
            mats[i] = me;
        }

        // Phase B: MATL 覆盖（id 为 1-based raw palette index）
        for (int i = 0; i < matls.Count; i++)
        {
            var cr = matls[i];
            if (cr.contentLength < 8) continue;
            ParseOneMaterial(data, cr.contentOffset, cr.contentOffset + cr.contentLength, mats);
        }

        return mats;
    }

    private static void ParseOneMaterial(byte[] content, int p, int end, MaterialElement[] mats)
    {
        if (p + 4 > end) return;
        int id = BitConverter.ToInt32(content, p); p += 4;
        int slot = id - 1;                       // 本资产内 0-based
        if (slot < 0 || slot >= PaletteSlots) return;

        var me = mats[slot];
        int pairCount = ReadI32(content, ref p, end);

        for (int i = 0; i < pairCount && p < end; i++)
        {
            string key = ReadString(content, ref p, end);
            if (key == null) break;
            string val = ReadString(content, ref p, end);
            if (val == null) break;

            switch (key)
            {
                case "_type":
                    switch (val)
                    {
                        case "_emit":    me.type = 3f; break;
                        case "_metal":   me.type = 1f; break;
                        case "_glass":   me.type = 2f; break;
                        case "_diffuse": me.type = 0f; break;
                        case "_cloud":   me.type = 4f; break;
                        case "_blend":   me.type = 5f; break;
                        default:
                            float mt;
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out mt))
                                me.type = mt;
                            break;
                    }
                    break;
                case "_emit":
                    if (val == "1") me.type = 3f;
                    break;
                case "_weight": ParseFloat(val, ref me.weight); break;
                case "_rough":  ParseFloat(val, ref me.rough);  break;
                case "_spec":   ParseFloat(val, ref me.spec);   break;
                case "_ior":    ParseFloat(val, ref me.ior);    break;
                case "_att":    ParseFloat(val, ref me.att);    break;
                case "_flux":   ParseFloat(val, ref me.flux);   break;
                default: break; // 未知 key 静默忽略（前向兼容）
            }
        }

        mats[slot] = me;
    }

    private static void ParseFloat(string s, ref float dst)
    {
        float v;
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out v))
            dst = v;
    }

    // ============================================================
    //  场景图（nTRN / nGRP / nSHP）—— O(N) 索引 + 记忆化
    // ============================================================

    private static int ReadSceneGraph(byte[] data,
                                      List<ChunkRef> trns, List<ChunkRef> grps, List<ChunkRef> shps,
                                      List<ChunkRef> sizes, List<ChunkRef> xyzis,
                                      int matBase, Vector3Int parentOffset,
                                      List<VoxelElement> outVoxels, ref int skipped)
    {
        // ---- 1. 解析 nTRN（文件顺序保留，与原实现 Dictionary 插入顺序一致）----
        var trnByNode = new Dictionary<int, TrnInfo>(trns.Count);
        var trnOrder  = new List<int>(trns.Count);
        for (int i = 0; i < trns.Count; i++)
        {
            TrnInfo info;
            if (!ParseTrn(data, trns[i], out info)) continue;
            if (trnByNode.ContainsKey(info.nodeId)) continue;
            trnByNode.Add(info.nodeId, info);
            trnOrder.Add(info.nodeId);
        }

        // ---- 2. 解析 nGRP（groupNodeId -> groupInfo）----
        var groupByNode = new Dictionary<int, GroupInfo>(grps.Count);
        for (int i = 0; i < grps.Count; i++)
        {
            GroupInfo gi;
            if (!ParseGroup(data, grps[i], out gi)) continue;
            groupByNode[gi.nodeId] = gi;
        }

        // ---- 3. 解析 nSHP（shapeNodeId -> modelIds）----
        var shapeModels = new Dictionary<int, int[]>(shps.Count);
        for (int i = 0; i < shps.Count; i++)
        {
            int nodeId;
            int[] models;
            if (!ParseShape(data, shps[i], out nodeId, out models)) continue;
            shapeModels[nodeId] = models;
        }

        // ---- 4. 建父链索引：childNodeId -> parent transform nodeId ----
        // groupNodeId -> 其父 transform nodeId
        var trnOfGroup = new Dictionary<int, int>(groupByNode.Count);
        foreach (var kv in trnByNode)
        {
            int childId = kv.Value.childId;
            if (groupByNode.ContainsKey(childId) && !trnOfGroup.ContainsKey(childId))
                trnOfGroup[childId] = kv.Key;
        }
        // childNodeId -> 所属 group 的父 transform
        var parentTrnOf = new Dictionary<int, int>(grps.Count * 4);
        foreach (var kv in groupByNode)
        {
            int parentTrn;
            if (!trnOfGroup.TryGetValue(kv.Key, out parentTrn)) continue;
            var children = kv.Value.children;
            for (int i = 0; i < children.Length; i++)
            {
                if (!parentTrnOf.ContainsKey(children[i]))
                    parentTrnOf[children[i]] = parentTrn;
            }
        }

        // ---- 5. 记忆化全局变换（复刻 GetGlobalTranslation / GetGlobalRotation 的迭代语义）----
        var rotCache = new Dictionary<int, VoxReader.Matrix3>(trnByNode.Count);
        var posCache = new Dictionary<int, VoxReader.Vector3>(trnByNode.Count);

        // ---- 6. 预分配容量（越界体素会少写，故为上限）----
        long totalVoxels = 0;
        int plannedModels = 0;
        for (int i = 0; i < trnOrder.Count; i++)
        {
            TrnInfo trn = trnByNode[trnOrder[i]];
            int[] models;
            if (!shapeModels.TryGetValue(trn.childId, out models)) continue;
            for (int k = 0; k < models.Length; k++)
            {
                int id = models[k];
                if (id < 0 || id >= xyzis.Count || id >= sizes.Count) continue;
                totalVoxels += BitConverter.ToInt32(data, xyzis[id].contentOffset);
                plannedModels++;
            }
        }
        ReserveCapacityExact(outVoxels, totalVoxels);

        // ---- 7. 输出体素 ----
        int emitted = 0;
        for (int i = 0; i < trnOrder.Count; i++)
        {
            int nodeId = trnOrder[i];
            TrnInfo trn = trnByNode[nodeId];
            int[] models;
            if (!shapeModels.TryGetValue(trn.childId, out models)) continue;

            VoxReader.Matrix3 globalRot = GetGlobalRotation(nodeId, trnByNode, parentTrnOf, rotCache);
            VoxReader.Vector3 globalPos = GetGlobalTranslation(nodeId, trnByNode, parentTrnOf, rotCache, posCache);

            for (int k = 0; k < models.Length; k++)
            {
                int id = models[k];
                if (id < 0 || id >= xyzis.Count || id >= sizes.Count) continue;
                EmitModel(data, sizes[id], xyzis[id], id, globalPos, globalRot,
                          matBase, parentOffset, outVoxels, ref skipped);
                emitted++;
            }
        }
        return emitted;
    }

    private struct GroupInfo
    {
        public int nodeId;
        public int[] children;
    }

    private static bool ParseTrn(byte[] d, ChunkRef cr, out TrnInfo info)
    {
        info = default(TrnInfo);
        int p = cr.contentOffset;
        int end = cr.contentOffset + cr.contentLength;
        if (p + 4 > end) return false;

        info.nodeId = BitConverter.ToInt32(d, p); p += 4;
        if (!SkipDict(d, ref p, end)) return false;
        if (p + 16 > end) return false;

        info.childId = BitConverter.ToInt32(d, p); p += 4;
        p += 4; // reservedId
        p += 4; // layerId
        int frameCount = BitConverter.ToInt32(d, p); p += 4;
        info.rotationCode = -1;   // 缺失 _r 的语义是 Matrix3.Identity，不是编码 0

        for (int f = 0; f < frameCount && p + 4 <= end; f++)
        {
            int pairs = BitConverter.ToInt32(d, p); p += 4;
            if (pairs < 0 || pairs > 4096) return false;
            for (int i = 0; i < pairs; i++)
            {
                string key = ReadString(d, ref p, end);
                if (key == null) return false;
                string val = ReadString(d, ref p, end);
                if (val == null) return false;

                if (f != 0) continue;
                if (key == "_r")
                {
                    int rb;
                    if (int.TryParse(val, System.Globalization.NumberStyles.Integer,
                                     System.Globalization.CultureInfo.InvariantCulture, out rb))
                    {
                        info.rotationCode = rb;
                    }
                    else if (!string.IsNullOrEmpty(val))
                    {
                        // 少数导出器把 _r 写成原始字节串（首字节即编码）；VoxReader 的 byte.Parse 会直接抛 FormatException
                        info.rotationCode = val[0];
                    }
                }
                else if (key == "_t")
                {
                    ParseTranslation(val, out info.tx, out info.ty, out info.tz);
                }
            }
        }
        return true;
    }

    private static void ParseTranslation(string s, out int x, out int y, out int z)
    {
        x = y = z = 0;
        if (string.IsNullOrEmpty(s)) return;
        int i = 0, n = s.Length;
        ReadInt(s, ref i, n, out x);
        ReadInt(s, ref i, n, out y);
        ReadInt(s, ref i, n, out z);
    }

    private static void ReadInt(string s, ref int i, int n, out int value)
    {
        value = 0;
        while (i < n && s[i] == ' ') i++;
        bool neg = false;
        if (i < n && (s[i] == '-' || s[i] == '+')) { neg = s[i] == '-'; i++; }
        int v = 0; bool any = false;
        while (i < n && s[i] >= '0' && s[i] <= '9') { v = v * 10 + (s[i] - '0'); i++; any = true; }
        value = any ? (neg ? -v : v) : 0;
    }

    private static bool ParseGroup(byte[] d, ChunkRef cr, out GroupInfo info)
    {
        info = default(GroupInfo);
        int p = cr.contentOffset;
        int end = cr.contentOffset + cr.contentLength;
        if (p + 4 > end) return false;

        info.nodeId = BitConverter.ToInt32(d, p); p += 4;
        if (!SkipDict(d, ref p, end)) return false;
        if (p + 4 > end) return false;

        int childCount = BitConverter.ToInt32(d, p); p += 4;
        if (childCount < 0 || p + childCount * 4 > end) return false;

        var children = new int[childCount];
        for (int i = 0; i < childCount; i++)
        {
            children[i] = BitConverter.ToInt32(d, p);
            p += 4;
        }
        info.children = children;
        return true;
    }

    private static bool ParseShape(byte[] d, ChunkRef cr, out int nodeId, out int[] models)
    {
        nodeId = -1;
        models = null;
        int p = cr.contentOffset;
        int end = cr.contentOffset + cr.contentLength;
        if (p + 4 > end) return false;

        nodeId = BitConverter.ToInt32(d, p); p += 4;
        if (!SkipDict(d, ref p, end)) return false;
        if (p + 4 > end) return false;

        int modelCount = BitConverter.ToInt32(d, p); p += 4;
        if (modelCount < 0 || modelCount > 4096) return false;

        var arr = new int[modelCount];
        for (int i = 0; i < modelCount; i++)
        {
            if (p + 4 > end) return false;
            arr[i] = BitConverter.ToInt32(d, p); p += 4;
            if (!SkipDict(d, ref p, end)) return false;
        }
        models = arr;
        return true;
    }

    private static bool sWarnedBadRotation;

    /// <summary>
    /// _r 编码 → Matrix3；缺失（-1）或非法时退化为单位矩阵。
    /// 注意：不能把“缺失 _r”当成编码 0 —— new Matrix3(0) 的 row0 与 row1 相同，
    /// 会命中 VoxReader._row2Lookup 的 int.MaxValue 并抛出 “Invalid rotation bytes!”。
    /// </summary>
    private static VoxReader.Matrix3 MakeRotation(int code)
    {
        if (code < 0 || code > 255) return VoxReader.Matrix3.Identity;

        int row0 = code & 0b11;
        int row1 = (code >> 2) & 0b11;
        // 合法编码：row0/row1 必须属于 {0,1,2} 且互不相同（否则 row2 不唯一，Matrix3 构造会抛异常）
        if (row0 > 2 || row1 > 2 || row0 == row1)
        {
            if (!sWarnedBadRotation)
            {
                sWarnedBadRotation = true;
                Debug.LogWarning($"[VoxFastReader] 非法 _r 编码 {code}，已按单位矩阵处理（后续同类告警已隐藏）");
            }
            return VoxReader.Matrix3.Identity;
        }
        return new VoxReader.Matrix3((byte)code);
    }

    /// <summary>复刻 Helper.GetGlobalRotation：rotation = parent.rot * rotation（自底向上）。</summary>
    private static VoxReader.Matrix3 GetGlobalRotation(int nodeId,
                                                      Dictionary<int, TrnInfo> trnByNode,
                                                      Dictionary<int, int> parentTrnOf,
                                                      Dictionary<int, VoxReader.Matrix3> cache)
    {
        VoxReader.Matrix3 cached;
        if (cache.TryGetValue(nodeId, out cached)) return cached;

        TrnInfo t = trnByNode[nodeId];
        VoxReader.Matrix3 self = MakeRotation(t.rotationCode);

        int parentId;
        VoxReader.Matrix3 result = self;
        if (parentTrnOf.TryGetValue(nodeId, out parentId) && trnByNode.ContainsKey(parentId))
            result = GetGlobalRotation(parentId, trnByNode, parentTrnOf, cache) * self;

        cache[nodeId] = result;
        return result;
    }

    /// <summary>复刻 Helper.GetGlobalTranslation：position = parent.rot * position + parent.trans（自底向上）。</summary>
    private static VoxReader.Vector3 GetGlobalTranslation(int nodeId,
                                                         Dictionary<int, TrnInfo> trnByNode,
                                                         Dictionary<int, int> parentTrnOf,
                                                         Dictionary<int, VoxReader.Matrix3> rotCache,
                                                         Dictionary<int, VoxReader.Vector3> posCache)
    {
        VoxReader.Vector3 cached;
        if (posCache.TryGetValue(nodeId, out cached)) return cached;

        TrnInfo t = trnByNode[nodeId];
        var self = new VoxReader.Vector3(t.tx, t.ty, t.tz);

        int parentId;
        VoxReader.Vector3 result = self;
        if (parentTrnOf.TryGetValue(nodeId, out parentId) && trnByNode.ContainsKey(parentId))
        {
            VoxReader.Vector3 parentPos = GetGlobalTranslation(parentId, trnByNode, parentTrnOf, rotCache, posCache);
            VoxReader.Matrix3   parentRot = GetGlobalRotation(parentId, trnByNode, parentTrnOf, rotCache);
            result = parentRot * self + parentPos;
        }

        posCache[nodeId] = result;
        return result;
    }

    // ============================================================
    //  单模型体素输出
    // ============================================================

    private static void EmitModel(byte[] data, ChunkRef sizeChunk, ChunkRef xyziChunk, int modelId,
                                  VoxReader.Vector3 globalPos, VoxReader.Matrix3 globalRot,
                                  int matBase, Vector3Int parentOffset,
                                  List<VoxelElement> outVoxels, ref int skipped)
    {
        // SIZE
        if (sizeChunk.contentLength < 12 || xyziChunk.contentLength < 4) return;
        int sp = sizeChunk.contentOffset;
        int sx = Mathf.Max(1, BitConverter.ToInt32(data, sp));
        int sy = Mathf.Max(1, BitConverter.ToInt32(data, sp + 4));
        int sz = Mathf.Max(1, BitConverter.ToInt32(data, sp + 8));

        // 模型中心：复刻 Helper.ExtractModels 的 tight bounds 中心
        var localSize = new VoxReader.Vector3(sx, sy, sz);
        var v0 = new VoxReader.Vector3(0, 0, 0);
        var v1 = localSize - new VoxReader.Vector3(1, 1, 1);
        VoxReader.Vector3 mn = ApplyTransform(v0, globalPos, globalRot, localSize);
        VoxReader.Vector3 mx = ApplyTransform(v1, globalPos, globalRot, localSize);
        var gmin = new VoxReader.Vector3(Math.Min(mx.X, mn.X), Math.Min(mx.Y, mn.Y), Math.Min(mx.Z, mn.Z));
        var gmax = new VoxReader.Vector3(Math.Max(mx.X, mn.X), Math.Max(mx.Y, mn.Y), Math.Max(mx.Z, mn.Z));
        VoxReader.Vector3 center = gmin + (gmax - gmin + new VoxReader.Vector3(1, 1, 1)) / 2;

        var chunkOffset = new Vector3Int(
            Mathf.RoundToInt(center.X),
            Mathf.RoundToInt(center.Z),   // Vox→Unity: Y↔Z
            Mathf.RoundToInt(center.Y));

        Quaternion rot = Matrix3ToQuaternion(globalRot);
        bool identity = IsIdentity(globalRot);

        // XYZI
        int cp = xyziChunk.contentOffset;
        int end = xyziChunk.contentOffset + xyziChunk.contentLength;
        int count = BitConverter.ToInt32(data, cp);
        if (count < 0) return;
        long need = (long)count * 4;
        if (cp + 4 + need > end) return;

        int voxelBase = cp + 4;
        int px = parentOffset.x + chunkOffset.x;
        int py = parentOffset.y + chunkOffset.y;
        int pz = parentOffset.z + chunkOffset.z;
        int halfX = sx >> 1, halfY = sz >> 1, halfZ = sy >> 1; // 注意 Vox→Unity 的 Y/Z 交换

        for (int i = 0; i < count; i++)
        {
            int o = voxelBase + i * 4;
            int ix = data[o];
            int iy = data[o + 1];
            int iz = data[o + 2];
            int ci = data[o + 3];

            if (ix >= sx || iy >= sy || iz >= sz) { skipped++; continue; }

            int wx, wy, wz;
            if (identity)
            {
                // 无旋转快速路径：RoundToInt(v - size*0.5) 的整数等价形式
                wx = px + (ix - halfX);
                wy = py + (iz - halfY);
                wz = pz + (iy - halfZ);
            }
            else
            {
                var localOffset = new Vector3(ix - sx * 0.5f, iz - sz * 0.5f, iy - sy * 0.5f);
                Vector3 worldOffset = rot * localOffset;
                wx = px + RoundToInt(worldOffset.x);
                wy = py + RoundToInt(worldOffset.y);
                wz = pz + RoundToInt(worldOffset.z);
            }

            int raw = ci - 1;
            if (raw < 0) raw = 0;
            else if (raw >= PaletteSlots) raw = PaletteSlots - 1;

            outVoxels.Add(new VoxelElement
            {
                worldPos = new Vector3Int(wx, wy, wz),
                materialIndex = matBase + raw,
            });
        }
    }

    private static VoxReader.Vector3 ApplyTransform(VoxReader.Vector3 voxelPos,
                                                    VoxReader.Vector3 globalPivot,
                                                    VoxReader.Matrix3 globalRot,
                                                    VoxReader.Vector3 size)
    {
        return globalPivot + globalRot.RotateIndex(voxelPos - size / 2);
    }

    private static bool IsIdentity(VoxReader.Matrix3 m)
    {
        return m[0, 0] == 1 && m[0, 1] == 0 && m[0, 2] == 0
            && m[1, 0] == 0 && m[1, 1] == 1 && m[1, 2] == 0
            && m[2, 0] == 0 && m[2, 1] == 0 && m[2, 2] == 1;
    }

    /// <summary>复刻 VoxDataProvider.Matrix3ToQuaternion：Vox→Unity 的 Y/Z 轴交换。</summary>
    private static Quaternion Matrix3ToQuaternion(VoxReader.Matrix3 m)
    {
        float m00 = m[0, 0], m01 = m[0, 1], m02 = m[0, 2];
        float m10 = m[1, 0], m11 = m[1, 1], m12 = m[1, 2];
        float m20 = m[2, 0], m21 = m[2, 1], m22 = m[2, 2];

        // Vox→Unity: X→X, Y→Z, Z→Y；R_unity = SwapYZ * R_vox * SwapYZ
        float u00 = m00, u01 = m02, u02 = m01;
        float u10 = m20, u11 = m22, u12 = m21;
        float u20 = m10, u21 = m12, u22 = m11;

        var mat = new Matrix4x4();
        mat[0, 0] = u00; mat[0, 1] = u01; mat[0, 2] = u02;
        mat[1, 0] = u10; mat[1, 1] = u11; mat[1, 2] = u12;
        mat[2, 0] = u20; mat[2, 1] = u21; mat[2, 2] = u22;
        mat[3, 3] = 1f;
        return mat.rotation;
    }

    /// <summary>四舍五入（round half up），与原 VoxDataProvider.RoundToInt 一致。</summary>
    private static int RoundToInt(float v)
    {
        return Mathf.FloorToInt(v + 0.5f);
    }

    /// <summary>容量预分配（上限估计；越界体素不写入，故实际用量 ≤ 估计）。</summary>
    private static void ReserveCapacityExact(List<VoxelElement> list, long total)
    {
        if (total <= 0) return;
        long want = list.Count + total;
        if (want > int.MaxValue - 8) want = int.MaxValue - 8;
        if (list.Capacity < (int)want) list.Capacity = (int)want;
    }

    private static void ReserveCapacity(List<VoxelElement> list, byte[] data,
                                        List<ChunkRef> sizes, List<ChunkRef> xyzis,
                                        int modelTotal, int startCount, Func<int, int> idSelector)
    {
        long total = 0;
        int n = Mathf.Min(modelTotal, Mathf.Min(sizes.Count, xyzis.Count));
        for (int i = 0; i < n; i++)
        {
            int id = idSelector(i);
            if (id < 0 || id >= xyzis.Count) continue;
            int count = BitConverter.ToInt32(data, xyzis[id].contentOffset);
            if (count > 0) total += count;
        }
        ReserveCapacityExact(list, total);
    }

    // ============================================================
    //  原始字节读取工具（全部无分配）
    // ============================================================

    private static int ReadI32(byte[] d, ref int p, int end)
    {
        if (p + 4 > end) { p = end; return 0; }
        int v = BitConverter.ToInt32(d, p);
        p += 4;
        return v;
    }

    private static string ReadString(byte[] d, ref int p, int end)
    {
        if (p + 4 > end) { p = end; return null; }
        int len = BitConverter.ToInt32(d, p); p += 4;
        if (len < 0 || p + len > end) { p = end; return null; }
        string s = len == 0 ? string.Empty : Encoding.ASCII.GetString(d, p, len);
        p += len;
        return s;
    }

    /// <summary>跳过 DICT{string,string}（不解析键值，零分配）。</summary>
    private static bool SkipDict(byte[] d, ref int p, int end)
    {
        if (p + 4 > end) return false;
        int pairs = BitConverter.ToInt32(d, p); p += 4;
        if (pairs < 0 || pairs > 65535) return false;
        for (int i = 0; i < pairs; i++)
        {
            if (p + 4 > end) return false;
            int klen = BitConverter.ToInt32(d, p); p += 4;
            if (klen < 0 || p + klen > end) return false;
            p += klen;

            if (p + 4 > end) return false;
            int vlen = BitConverter.ToInt32(d, p); p += 4;
            if (vlen < 0 || p + vlen > end) return false;
            p += vlen;
        }
        return true;
    }
}
