Shader "Unlit/VoxRender_DDA"
{
    Properties
    {
        _VoxelTexture ("Voxel 3D Texture", 3D) = "" {}
        _ChunkSize    ("Chunk Size (x,y,z)", Vector) = (1,1,1,0)
        _VoxelScale   ("Voxel Scale", Float) = 1.0
        _MaxSteps     ("Max Ray Steps", Int) = 256
        _Ambient      ("Ambient Light", Range(0,1)) = 0.1
        _ShadowFactor ("Shadow Darkness", Range(0,1)) = 0.7
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Front
        LOD 100

        Pass
        {
            CGPROGRAM
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
                float3 localPos : TEXCOORD0;
            };

            sampler3D _VoxelTexture;
            float4    _ChunkSize;
            float     _VoxelScale;
            int       _MaxSteps;
            float     _Ambient;
            float     _ShadowFactor;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos      = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz;
                return o;
            }

            float3 toGridSpace(float3 localPos)
            {
                float3 n = localPos + 0.5;
                return float3(n.x, n.z, n.y) * _ChunkSize.xyz;
            }

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

                // 进入面法线（指向光线来源方向，即面朝外）
                if (tMin3.x >= tMin3.y && tMin3.x >= tMin3.z)
                    enterNormal = float3(-sign(dir.x), 0, 0);
                else if (tMin3.y >= tMin3.x && tMin3.y >= tMin3.z)
                    enterNormal = float3(0, -sign(dir.y), 0);
                else
                    enterNormal = float3(0, 0, -sign(dir.z));

                return tExit >= tEnter && tExit >= 0.0;
            }

            // 自阴影 DDA：从命中体素沿光方向步进，检测是否被其他体素遮挡
            // 返回阴影系数（1.0 = 无阴影，越小越暗），同时按命中距离修正幅度：步进越小越暗
            float computeShadow(float3 hitVoxel, float3 gridLightDir, float3 chunkDim)
            {
                // 和主 DDA 一致：从命中体素开始，用 hitVoxel 计算初始边界
                // 第一次步进后 shadowVoxel 自动变成 hitVoxel + shadowStepDir（相邻体素）
                float3 shadowStepDir = sign(gridLightDir);
                float3 shadowVoxel   = hitVoxel;

                float3 shadowAbsDir  = max(abs(gridLightDir), 1e-6);
                float3 shadowTDelta  = 1.0 / shadowAbsDir;

                // 精确起点：命中体素中心沿光方向微小偏移
                float3 shadowGridOrigin = (hitVoxel + 0.5) + gridLightDir * 0.001;

                float3 shadowNextBoundary = shadowStepDir > 0 ? hitVoxel + 1.0 : hitVoxel;
                float3 shadowTMax = (shadowNextBoundary - shadowGridOrigin) / gridLightDir;
                if (abs(gridLightDir.x) < 1e-6) shadowTMax.x = 1e10;
                if (abs(gridLightDir.y) < 1e-6) shadowTMax.y = 1e10;
                if (abs(gridLightDir.z) < 1e-6) shadowTMax.z = 1e10;

                int  hitStep  = -1;     // 命中遮挡物时的步进次数，-1 表示未命中

                [loop]
                for (int ss = 0; ss < _MaxSteps; ss++)
                {
                    // 先步进：跨越一个体素边界，shadowVoxel 变为相邻体素
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

                    // 步进后再检查边界
                    if (any(shadowVoxel < 0) || any(shadowVoxel >= chunkDim))
                        break;

                    // 再采样：此时 shadowVoxel 已经是相邻体素
                    float3 shadowUVW = (shadowVoxel + 0.5) / chunkDim;
                    fixed4 shadowCol = tex3D(_VoxelTexture, shadowUVW);

                    if (shadowCol.a > 0.01)
                    {
                        hitStep = ss;
                        break;
                    }
                }

                if (hitStep < 0)
                    return 1.0; // 未被遮挡

                // 步进越小 → 遮挡越近 → 阴影越暗
                // 用 chunk 对角线方向的最大步进数做归一化
                float maxDist = chunkDim.x + chunkDim.y + chunkDim.z;
                float t = saturate((float)hitStep / maxDist);
                return lerp(_ShadowFactor, 0, t);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 chunkDim = _ChunkSize.xyz;

                float3 localCameraPos = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                float3 rayDir = normalize(i.localPos - localCameraPos);

                float3 boxMin = float3(-0.5, -0.5, -0.5);
                float3 boxMax = float3( 0.5,  0.5,  0.5);

                float3 enterNormalLocal;
                float tEnter, tExit;
                if (!intersectAABB(localCameraPos, rayDir, boxMin, boxMax, tEnter, tExit, enterNormalLocal))
                    discard;

                float tStart = max(tEnter, 0.0);
                float3 entryPoint = localCameraPos + rayDir * tStart;

                float3 gridOrigin = toGridSpace(entryPoint);
                gridOrigin = clamp(gridOrigin, float3(0,0,0), chunkDim - 0.001);

                float3 gridDir = normalize(float3(rayDir.x, rayDir.z, rayDir.y));

                float3 currentVoxel = floor(gridOrigin);
                float3 stepDir      = sign(gridDir);

                float3 absDir = max(abs(gridDir), 1e-6);
                float3 tDelta = 1.0 / absDir;

                float3 nextBoundary = stepDir > 0 ? currentVoxel + 1.0 : currentVoxel;
                float3 tMax = (nextBoundary - gridOrigin) / gridDir;
                if (abs(gridDir.x) < 1e-6) tMax.x = 1e10;
                if (abs(gridDir.y) < 1e-6) tMax.y = 1e10;
                if (abs(gridDir.z) < 1e-6) tMax.z = 1e10;

                // 初始面法线：光线进入 AABB 时穿过的面
                float3 hitNormal;
                if (tEnter > 0.0)
                {
                    // 相机在外部：Slab 进入面（本地空间 → 网格空间 Y/Z 交换）
                    hitNormal = float3(enterNormalLocal.x, enterNormalLocal.z, enterNormalLocal.y);
                }
                else
                {
                    // 相机在内部：取 gridDir 最大分量方向作为入口面
                    float3 ad = abs(gridDir);
                    if (ad.x >= ad.y && ad.x >= ad.z)
                        hitNormal = float3(-stepDir.x, 0, 0);
                    else if (ad.y >= ad.x && ad.y >= ad.z)
                        hitNormal = float3(0, -stepDir.y, 0);
                    else
                        hitNormal = float3(0, 0, -stepDir.z);
                }

                [loop]
                for (int s = 0; s < _MaxSteps; s++)
                {
                    if (any(currentVoxel < 0) || any(currentVoxel >= chunkDim))
                        break;

                    float3 sampleUVW = (currentVoxel + 0.5) / chunkDim;
                    fixed4 voxelColor = tex3D(_VoxelTexture, sampleUVW);

                    if (voxelColor.a > 0.01)
                    {
                        // ===== 面法线光照 =====
                        // hitNormal 是网格空间 (Vox: x=右, y=前后, z=上下)
                        // 转换到 Unity 本地空间: (x, z, y) → (x, y_up, z_forward)
                        float3 localNormal = normalize(float3(hitNormal.x, hitNormal.z, hitNormal.y));
                        // 转换到世界空间
                        float3 worldNormal = normalize(mul((float3x3)unity_ObjectToWorld, localNormal));

                        // 场景平行光方向（_WorldSpaceLightPos0.xyz 是从表面指向光源的方向，Built-in 标准）
                        float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                        // Lambert 漫反射
                        float ndotl = max(0.0, dot(worldNormal, lightDir));
                        float3 diffuse = voxelColor.rgb * _LightColor0.rgb * ndotl;
                        // 环境光
                        float3 ambient = voxelColor.rgb * _Ambient;

                        // ===== 自阴影 DDA =====
                        // 世界空间光方向 → 本地空间 → 网格空间（y/z 交换）
                        float3 shadowLocalDir = normalize(mul((float3x3)unity_WorldToObject, lightDir));
                        float3 shadowGridDir  = normalize(float3(shadowLocalDir.x, shadowLocalDir.z, shadowLocalDir.y));
                        // 阴影只影响漫反射（环境光不受阴影影响）
                        diffuse *= computeShadow(currentVoxel, shadowGridDir, chunkDim);
                        // 合成最终颜色
                        return fixed4(diffuse + ambient, voxelColor.a);
                    }

                    // 步进：记录进入下一个体素时穿过的面
                    if (tMax.x < tMax.y)
                    {
                        if (tMax.x < tMax.z)
                        {
                            currentVoxel.x += stepDir.x;
                            tMax.x += tDelta.x;
                            hitNormal = float3(-stepDir.x, 0, 0);
                        }
                        else
                        {
                            currentVoxel.z += stepDir.z;
                            tMax.z += tDelta.z;
                            hitNormal = float3(0, 0, -stepDir.z);
                        }
                    }
                    else
                    {
                        if (tMax.y < tMax.z)
                        {
                            currentVoxel.y += stepDir.y;
                            tMax.y += tDelta.y;
                            hitNormal = float3(0, -stepDir.y, 0);
                        }
                        else
                        {
                            currentVoxel.z += stepDir.z;
                            tMax.z += tDelta.z;
                            hitNormal = float3(0, 0, -stepDir.z);
                        }
                    }
                }

                discard;
                return fixed4(0, 0, 0, 0);
            }
            ENDCG
        }
    }
}
