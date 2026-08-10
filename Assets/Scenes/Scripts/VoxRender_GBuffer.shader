Shader "Unlit/VoxRender_GBuffer"
{
    Properties
    {
        _VoxelTexture ("Voxel 3D Texture", 3D) = "" {}
        _ChunkSize    ("Chunk Size (x,y,z)", Vector) = (1,1,1,0)
        _VoxelScale   ("Voxel Scale", Float) = 1.0
        _WorldBoxMin  ("World AABB Min", Vector) = (-0.5,-0.5,-0.5,0)
        _WorldBoxMax  ("World AABB Max", Vector) = (0.5,0.5,0.5,0)
        _MaxSteps     ("Max Ray Steps", Int) = 256
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Off
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma target 4.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            sampler3D _VoxelTexture;
            float4    _ChunkSize;
            float     _VoxelScale;
            float4    _WorldBoxMin;
            float4    _WorldBoxMax;
            int       _MaxSteps;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos      = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // 本地空间 [-0.5,0.5] → 网格空间 [0,chunkDim]（Vox→Unity Y/Z 交换）
            float3 toGridSpace(float3 localPos)
            {
                float3 n = localPos + 0.5;
                return float3(n.x, n.z, n.y) * _ChunkSize.xyz;
            }

            // 网格空间 → 本地空间（toGridSpace 的逆）
            float3 toLocalSpace(float3 gridPos)
            {
                float3 d = _ChunkSize.xyz;
                return float3(
                    gridPos.x / d.x - 0.5,
                    gridPos.z / d.z - 0.5,
                    gridPos.y / d.y - 0.5
                );
            }

            // 网格空间法线 → 世界空间法线
            float3 gridNormalToWorld(float3 gridN)
            {
                float3 localN = float3(gridN.x, gridN.z, gridN.y);
                return normalize(mul((float3x3)unity_ObjectToWorld, localN));
            }

            // 网格空间坐标 → 世界空间位置
            float3 gridToWorld(float3 gridPos)
            {
                float3 localPos = toLocalSpace(gridPos);
                return mul(unity_ObjectToWorld, float4(localPos, 1.0)).xyz;
            }

            // 世界空间 AABB 相交测试
            bool intersectAABB(float3 origin, float3 dir,
                               float3 boxMin, float3 boxMax,
                               out float tEnter, out float tExit,
                               out float3 enterNormal)
            {
                float3 t1 = (boxMin - origin) / dir;
                float3 t2 = (boxMax - origin) / dir;
                float3 tMin3 = min(t1, t2);
                float3 tMax3 = max(t1, t2);
                tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
                tExit  = min(min(tMax3.x, tMax3.y), tMax3.z);

                if (tMin3.x >= tMin3.y && tMin3.x >= tMin3.z)
                    enterNormal = float3(-sign(dir.x), 0, 0);
                else if (tMin3.y >= tMin3.x && tMin3.y >= tMin3.z)
                    enterNormal = float3(0, -sign(dir.y), 0);
                else
                    enterNormal = float3(0, 0, -sign(dir.z));

                return tExit >= tEnter && tExit >= 0.0;
            }

            // 根据命中点网格坐标计算裁剪空间深度
            float computeHitDepth(float3 hitPointGrid)
            {
                float3 hitWorld = gridToWorld(hitPointGrid);
                float4 clipPos = mul(UNITY_MATRIX_VP, float4(hitWorld, 1.0));
                return clipPos.z / clipPos.w;
            }

            // ===== G-Buffer MRT 输出结构 =====
            // RT0: Albedo (rgb=color, a=alpha，alpha>0 表示有体素)
            // RT1: World Normal (xyz=法线，packed to [0,1])，a=1
            // RT2: World Position (xyz=世界坐标)，a=1
            // SV_Depth: 自定义深度（用于 chunk 间正确遮挡）
            struct GBufferOutput
            {
                float4 albedo   : SV_Target0;
                float4 normal   : SV_Target1;
                float4 worldPos : SV_Target2;
                float  depth    : SV_Depth;
            };

            GBufferOutput frag (v2f i)
            {
                float3 chunkDim = _ChunkSize.xyz;

                // ===== 世界空间光线 =====
                float3 cameraPos = _WorldSpaceCameraPos;
                float3 rayDir = normalize(i.worldPos - cameraPos);

                // ===== 世界空间 AABB（CPU 传入）=====
                float3 worldBoxMin = _WorldBoxMin.xyz;
                float3 worldBoxMax = _WorldBoxMax.xyz;

                // ===== 世界空间 Slab 相交 =====
                float3 worldEnterNormal;
                float tEnter, tExit;
                if (!intersectAABB(cameraPos, rayDir, worldBoxMin, worldBoxMax,
                                   tEnter, tExit, worldEnterNormal))
                {
                    discard;
                    GBufferOutput empty;
                    return empty;
                }

                float tStart = max(tEnter, 0.0);
                float3 entryWorld = cameraPos + rayDir * tStart;

                // ===== 世界 → 本地 → 网格 =====
                float3 entryLocal = mul(unity_WorldToObject, float4(entryWorld, 1)).xyz;
                float3 gridOrigin = toGridSpace(entryLocal);
                gridOrigin = clamp(gridOrigin, float3(0,0,0), chunkDim - 0.001);

                // 光线方向：世界 → 本地 → 网格（Y/Z 交换 × chunkDim 缩放，不归一化）
                float3 localDir  = mul((float3x3)unity_WorldToObject, rayDir);
                float3 gridDir   = float3(localDir.x, localDir.z, localDir.y) * _ChunkSize.xyz;

                float3 currentVoxel = floor(gridOrigin);
                float3 stepDir      = sign(gridDir);

                float3 absDir = max(abs(gridDir), 1e-6);
                float3 tDelta = 1.0 / absDir;

                float3 nextBoundary = stepDir > 0 ? currentVoxel + 1.0 : currentVoxel;
                float3 tMax = (nextBoundary - gridOrigin) / gridDir;
                if (abs(gridDir.x) < 1e-6) tMax.x = 1e10;
                if (abs(gridDir.y) < 1e-6) tMax.y = 1e10;
                if (abs(gridDir.z) < 1e-6) tMax.z = 1e10;

                // 初始面法线（网格空间）
                float3 hitNormalGrid;
                if (tEnter > 0.0)
                {
                    float3 enterLocalN = normalize(mul((float3x3)unity_WorldToObject, worldEnterNormal));
                    hitNormalGrid = float3(enterLocalN.x, enterLocalN.z, enterLocalN.y);
                }
                else
                {
                    float3 ad = abs(gridDir);
                    if (ad.x >= ad.y && ad.x >= ad.z)
                        hitNormalGrid = float3(-stepDir.x, 0, 0);
                    else if (ad.y >= ad.x && ad.y >= ad.z)
                        hitNormalGrid = float3(0, -stepDir.y, 0);
                    else
                        hitNormalGrid = float3(0, 0, -stepDir.z);
                }

                // 追踪光线进入当前体素的时间，用于计算精确命中点深度
                float entryT = 0.0;

                [loop]
                for (int s = 0; s < _MaxSteps; s++)
                {
                    if (any(currentVoxel < 0) || any(currentVoxel >= chunkDim))
                        break;

                    float3 sampleUVW = (currentVoxel + 0.5) / chunkDim;
                    fixed4 voxelColor = tex3D(_VoxelTexture, sampleUVW);

                    if (voxelColor.a > 0.01)
                    {
                        GBufferOutput o;

                        // 起点落在体素内部：输出全0，深度设为最近以遮挡后方 chunk
                        // normal.w = 0.5 作为 Pass 3 区分"在体素内"的特殊标记
                        if (s == 0 && tEnter <= 0.0)
                        {
                            o.albedo   = float4(0, 0, 0, 0);
                            o.normal   = float4(0, 0, 0, 0.5);
                            o.worldPos = float4(0, 0, 0, 0);
                            #if defined(UNITY_REVERSED_Z)
                                o.depth = 1.0;
                            #else
                                o.depth = 0.0;
                            #endif
                            return o;
                        }

                        // ===== 输出 G-Buffer 数据 =====
                        float3 worldNormal = gridNormalToWorld(hitNormalGrid);
                        float3 hitPointGrid = gridOrigin + gridDir * entryT;
                        float3 hitWorld = gridToWorld(hitPointGrid);

                        o.albedo   = float4(voxelColor.rgb, voxelColor.a);
                        o.normal   = float4(worldNormal * 0.5 + 0.5, 1.0); // pack to [0,1]
                        o.worldPos = float4(hitWorld, 1.0);
                        o.depth    = computeHitDepth(hitPointGrid);

                        return o;
                    }

                    // 步进：保存进入时间 entryT，更新网格空间法线
                    if (tMax.x < tMax.y)
                    {
                        if (tMax.x < tMax.z)
                        {
                            entryT = tMax.x;
                            currentVoxel.x += stepDir.x;
                            tMax.x += tDelta.x;
                            hitNormalGrid = float3(-stepDir.x, 0, 0);
                        }
                        else
                        {
                            entryT = tMax.z;
                            currentVoxel.z += stepDir.z;
                            tMax.z += tDelta.z;
                            hitNormalGrid = float3(0, 0, -stepDir.z);
                        }
                    }
                    else
                    {
                        if (tMax.y < tMax.z)
                        {
                            entryT = tMax.y;
                            currentVoxel.y += stepDir.y;
                            tMax.y += tDelta.y;
                            hitNormalGrid = float3(0, -stepDir.y, 0);
                        }
                        else
                        {
                            entryT = tMax.z;
                            currentVoxel.z += stepDir.z;
                            tMax.z += tDelta.z;
                            hitNormalGrid = float3(0, 0, -stepDir.z);
                        }
                    }
                }

                // 未命中任何体素
                discard;
                GBufferOutput miss;
                return miss;
            }
            ENDCG
        }
    }
}
