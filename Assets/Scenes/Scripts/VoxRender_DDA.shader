Shader "Unlit/VoxRender_DDA"
{
    Properties
    {
        _VoxelTexture ("Voxel 3D Texture", 3D) = "" {}
        _ChunkSize    ("Chunk Size (x,y,z)", Vector) = (1,1,1,0)
        _VoxelScale   ("Voxel Scale", Float) = 1.0
        _MaxSteps     ("Max Ray Steps", Int) = 256
    }
    SubShader
    {
        Tags { "Queue"="2000" "RenderType"="Opaque" }
        // Cull Front：只渲染背面（远端面），减少片元数量
        // 光线进入点由 Slab 测试计算，不依赖渲染哪个面
        Cull Front
        LOD 100

        Pass
        {
            CGPROGRAM
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
                float3 localPos : TEXCOORD0;  // 片元在立方体本地坐标 [-0.5, 0.5]
            };

            sampler3D _VoxelTexture;
            float4    _ChunkSize;    // (sx, sy, sz, 0) — Vox 原始维度
            float     _VoxelScale;
            int       _MaxSteps;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos      = UnityObjectToClipPos(v.vertex);
                o.localPos = v.vertex.xyz;
                return o;
            }

            // 将 Unity 本地坐标 [-0.5, 0.5] 转换为体素网格坐标 [0, chunkDim]
            // Y/Z 交换：Vox(x,y,z) 中 y=上下, z=前后；Unity(x,y,z) 中 y=前后, z=上下
            float3 toGridSpace(float3 localPos)
            {
                float3 n = localPos + 0.5;
                return float3(n.x, n.z, n.y) * _ChunkSize.xyz;
            }

            // AABB Slab 相交测试
            // 给定光线起点 origin 和方向 dir，以及 AABB [boxMin, boxMax]
            // 返回光线进入和退出 AABB 的参数 t
            bool intersectAABB(float3 origin, float3 dir,
                               float3 boxMin, float3 boxMax,
                               out float tEnter, out float tExit)
            {
                // 每个轴上求光线与两个平行平面的交点 t
                float3 t1 = (boxMin - origin) / dir;
                float3 t2 = (boxMax - origin) / dir;

                // tMin = 每个轴上较小的 t（进入平面），tMax = 较大的 t（退出平面）
                float3 tMin3 = min(t1, t2);
                float3 tMax3 = max(t1, t2);

                // 进入点 = 三轴 tMin 的最大值（光线真正进入盒子的时刻）
                tEnter = max(max(tMin3.x, tMin3.y), tMin3.z);
                // 退出点 = 三轴 tMax 的最小值（光线离开盒子的时刻）
                tExit  = min(min(tMax3.x, tMax3.y), tMax3.z);

                // 相交条件：tExit >= tEnter 且 tExit >= 0（盒子在光线前方）
                return tExit >= tEnter && tExit >= 0.0;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 chunkDim = _ChunkSize.xyz;

                // ===== 1. 光线起点：相机位置（本地空间） =====
                float3 localCameraPos = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;

                // ===== 2. 光线方向：从相机指向片元 =====
                float3 rayDir = normalize(i.localPos - localCameraPos);

                // ===== 3. AABB Slab 相交测试 =====
                // 立方体在本地空间的 AABB：[-0.5, 0.5]
                float3 boxMin = float3(-0.5, -0.5, -0.5);
                float3 boxMax = float3( 0.5,  0.5,  0.5);

                float tEnter, tExit;
                if (!intersectAABB(localCameraPos, rayDir, boxMin, boxMax, tEnter, tExit))
                    discard;

                // 光线进入立方体的实际起点
                // 如果相机在立方体外部：tEnter > 0，从交点开始
                // 如果相机在立方体内部：tEnter < 0，从相机位置开始（t=0）
                float tStart = max(tEnter, 0.0);
                float3 entryPoint = localCameraPos + rayDir * tStart;

                // ===== 4. 转换到体素网格空间 =====
                float3 gridOrigin = toGridSpace(entryPoint);
                gridOrigin = clamp(gridOrigin, float3(0,0,0), chunkDim - 0.001);

                // 光线方向也做 Y/Z 交换，保持与网格空间一致
                float3 gridDir = normalize(float3(rayDir.x, rayDir.z, rayDir.y));

                // ===== 5. DDA 参数初始化 =====
                float3 currentVoxel = floor(gridOrigin);
                float3 stepDir      = sign(gridDir);

                float3 absDir = max(abs(gridDir), 1e-6);
                float3 tDelta = 1.0 / absDir;

                float3 nextBoundary = stepDir > 0 ? currentVoxel + 1.0 : currentVoxel;
                float3 tMax = (nextBoundary - gridOrigin) / gridDir;
                if (abs(gridDir.x) < 1e-6) tMax.x = 1e10;
                if (abs(gridDir.y) < 1e-6) tMax.y = 1e10;
                if (abs(gridDir.z) < 1e-6) tMax.z = 1e10;

                // ===== 6. DDA 步进循环 =====
                [loop]
                for (int s = 0; s < _MaxSteps; s++)
                {
                    if (any(currentVoxel < 0) || any(currentVoxel >= chunkDim))
                        break;

                    float3 sampleUVW = (currentVoxel + 0.5) / chunkDim;
                    fixed4 voxelColor = tex3D(_VoxelTexture, sampleUVW);

                    if (voxelColor.a > 0.01)
                        return voxelColor;

                    if (tMax.x < tMax.y)
                    {
                        if (tMax.x < tMax.z)
                        {
                            currentVoxel.x += stepDir.x;
                            tMax.x += tDelta.x;
                        }
                        else
                        {
                            currentVoxel.z += stepDir.z;
                            tMax.z += tDelta.z;
                        }
                    }
                    else
                    {
                        if (tMax.y < tMax.z)
                        {
                            currentVoxel.y += stepDir.y;
                            tMax.y += tDelta.y;
                        }
                        else
                        {
                            currentVoxel.z += stepDir.z;
                            tMax.z += tDelta.z;
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
