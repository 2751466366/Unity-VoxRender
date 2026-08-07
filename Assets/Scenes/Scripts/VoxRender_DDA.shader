Shader "Unlit/VoxRender_DDA"
{
    Properties
    {
        _VoxelTexture ("Voxel 3D Texture", 3D) = "" {}
        _ChunkSize    ("Chunk Size (x,y,z)", Vector) = (1,1,1,0)
        _VoxelScale   ("Voxel Scale", Float) = 1.0
        _MaxSteps     ("Max Ray Steps", Int) = 256
        _Ambient      ("Ambient Light", Range(0,1)) = 0.3
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
