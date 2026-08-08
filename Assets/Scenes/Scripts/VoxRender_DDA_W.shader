Shader "Unlit/VoxRender_DDA_W"
{
    Properties
    {
        _VoxelTexture ("Voxel 3D Texture", 3D) = "" {}
        _ChunkSize    ("Chunk Size (x,y,z)", Vector) = (1,1,1,0)
        _VoxelScale   ("Voxel Scale", Float) = 1.0
        _WorldBoxMin  ("World AABB Min", Vector) = (-0.5,-0.5,-0.5,0)
        _WorldBoxMax  ("World AABB Max", Vector) = (0.5,0.5,0.5,0)
        _MaxSteps     ("Max Ray Steps", Int) = 256
        _Ambient      ("Ambient Light", Range(0,1)) = 0.1
        _ShadowFactor ("Shadow Darkness", Range(0,1)) = 0.7
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
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

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
            float     _Ambient;
            float     _ShadowFactor;

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

            // 自阴影 DDA：从命中体素沿光方向步进，检测是否被其他体素遮挡
            float computeShadow(float3 hitVoxel, float3 gridLightDir, float3 chunkDim)
            {
                float3 shadowStepDir = sign(gridLightDir);
                float3 shadowVoxel   = hitVoxel;

                float3 shadowAbsDir  = max(abs(gridLightDir), 1e-6);
                float3 shadowTDelta  = 1.0 / shadowAbsDir;

                float3 shadowGridOrigin = (hitVoxel + 0.5) + gridLightDir * 0.001;

                float3 shadowNextBoundary = shadowStepDir > 0 ? hitVoxel + 1.0 : hitVoxel;
                float3 shadowTMax = (shadowNextBoundary - shadowGridOrigin) / gridLightDir;
                if (abs(gridLightDir.x) < 1e-6) shadowTMax.x = 1e10;
                if (abs(gridLightDir.y) < 1e-6) shadowTMax.y = 1e10;
                if (abs(gridLightDir.z) < 1e-6) shadowTMax.z = 1e10;

                int hitStep = -1;

                [loop]
                for (int ss = 0; ss < _MaxSteps; ss++)
                {
                    if (shadowTMax.x < shadowTMax.y)
                    {
                        if (shadowTMax.x < shadowTMax.z)
                        {
                            shadowVoxel.x += shadowStepDir.x;
                            shadowTMax.x  += shadowTDelta.x;
                        }
                        else
                        {
                            shadowVoxel.z += shadowStepDir.z;
                            shadowTMax.z  += shadowTDelta.z;
                        }
                    }
                    else
                    {
                        if (shadowTMax.y < shadowTMax.z)
                        {
                            shadowVoxel.y += shadowStepDir.y;
                            shadowTMax.y  += shadowTDelta.y;
                        }
                        else
                        {
                            shadowVoxel.z += shadowStepDir.z;
                            shadowTMax.z  += shadowTDelta.z;
                        }
                    }

                    if (any(shadowVoxel < 0) || any(shadowVoxel >= chunkDim))
                        break;

                    float3 shadowUVW = (shadowVoxel + 0.5) / chunkDim;
                    fixed4 shadowCol = tex3D(_VoxelTexture, shadowUVW);

                    if (shadowCol.a > 0.01)
                    {
                        hitStep = ss;
                        break;
                    }
                }

                if (hitStep < 0)
                    return 1.0;

                float maxDist = chunkDim.x + chunkDim.y + chunkDim.z;
                float t = saturate((float)hitStep / maxDist);
                return lerp(_ShadowFactor, 0, t);
            }

            struct fragOutput
            {
                fixed4 color : SV_Target;
                float depth  : SV_Depth;
            };

            fragOutput frag (v2f i)
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
                    fragOutput o;
                    discard;
                    return o;
                }

                float tStart = max(tEnter, 0.0);
                float3 entryWorld = cameraPos + rayDir * tStart;

                // ===== 世界 → 本地 → 网格 =====
                float3 entryLocal = mul(unity_WorldToObject, float4(entryWorld, 1)).xyz;
                float3 gridOrigin = toGridSpace(entryLocal);
                gridOrigin = clamp(gridOrigin, float3(0,0,0), chunkDim - 0.001);

                // 光线方向：世界 → 本地 → 网格（Y/Z 交换 × chunkDim 缩放）
                // 不归一化：让各分量比例反映 chunk 各轴的体素数量，避免非立方体块扭曲
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
                        // 起点落在体素内部（相机在立方体内且首个体素即命中）：输出全0
                        if (s == 0 && tEnter <= 0.0)
                        {
                            fragOutput o;
                            o.color = fixed4(0, 0, 0, 0);
                            #if defined(UNITY_REVERSED_Z)
                                o.depth = 1.0;  // Reversed-Z: 1=最近
                            #else
                                o.depth = 0.0;  // 普通Z: 0=最近
                            #endif
                            return o;
                        }

                        // ===== 世界空间法线 =====
                        float3 worldNormal = gridNormalToWorld(hitNormalGrid);

                        // ===== Lambert 光照（世界空间）=====
                        float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                        float ndotl = max(0.0, dot(worldNormal, lightDir));
                        float3 diffuse = voxelColor.rgb * _LightColor0.rgb * ndotl;
                        float3 ambient = voxelColor.rgb * _Ambient;

                        // ===== 自阴影 DDA（同样不归一化，乘 chunkDim）=====
                        float3 shadowLocalDir = mul((float3x3)unity_WorldToObject, lightDir);
                        float3 shadowGridDir  = float3(shadowLocalDir.x, shadowLocalDir.z, shadowLocalDir.y) * _ChunkSize.xyz;
                        diffuse *= computeShadow(currentVoxel, shadowGridDir, chunkDim);

                        // ===== 精确命中点深度（光线进入体素的边界点）=====
                        float3 hitPointGrid = gridOrigin + gridDir * entryT;
                        float hitDepth = computeHitDepth(hitPointGrid);

                        fragOutput o;
                        o.color = fixed4(diffuse + ambient, voxelColor.a);
                        o.depth = hitDepth;
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

                fragOutput discardOutput;
                discard;
                return discardOutput;
            }
            ENDCG
        }
    }
}
