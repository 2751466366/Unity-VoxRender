Shader "Hidden/VoxRender_Lighting"
{
    Properties
    {
        _AlbedoTex   ("G-Buffer Albedo",   2D) = "black" {}
        _NormalTex   ("G-Buffer Normal",   2D) = "black" {}
        _WorldPosTex ("G-Buffer WorldPos", 2D) = "black" {}
        // Pass 2 预留：阴影系数贴图（白色=无阴影）
        _ShadowTex   ("Shadow Factor",     2D) = "white" {}
        _Ambient     ("Ambient Light",  Range(0,1)) = 0.2
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos  : SV_POSITION;
                float2 uv   : TEXCOORD0;
                float4 ray  : TEXCOORD1; // 世界空间射线（从相机出发，指向像素）
            };

            sampler2D _AlbedoTex;
            sampler2D _NormalTex;
            sampler2D _WorldPosTex;
            sampler2D _ShadowTex;
            samplerCUBE _SkyboxTex;   // Skybox Cubemap（CPU 从 RenderSettings.skybox 传入）
            float  _Ambient;

            // CPU 传入的光照参数
            float3 _LightDir;    // 指向光源的方向（已归一化）
            float3 _LightColor;  // 光颜色 × 强度

            // inverseVP 矩阵：从裁剪空间重建世界坐标，用于计算屏幕射线方向
            float4x4 _InverseVP;

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.texcoord;

                // UV [0,1] → 裁剪空间 [-1,1]，W=1，Z=1（远平面）
                float4 clipPos = float4(v.texcoord * 2.0 - 1.0, 1.0, 1.0);

                // 裁剪 → 世界
                float4 worldPosFar = mul(_InverseVP, clipPos);
                worldPosFar.xyz /= worldPosFar.w;

                // 世界空间射线方向（远平面点 - 相机位置）
                // 用 float4 存：xyz = 方向，w = 备用
                o.ray.xyz = worldPosFar.xyz - _WorldSpaceCameraPos;
                o.ray.w   = 0.0;

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // 读取 G-Buffer
                fixed4 albedo       = tex2D(_AlbedoTex, i.uv);
                float4 normalPacked = tex2D(_NormalTex, i.uv);

                // alpha=0 表示该像素无体素命中（分两种情况）
                if (albedo.a < 0.01)
                {
                    // 通过 normal.w 判断：
                    //   normal.w ≈ 0.0 → G-Buffer clear 值（chunk 未命中，真·背景）
                    //   normal.w ≈ 0.5 → Pass 1 标记的"相机在体素内部"
                    //   normal.w ≈ 1.0 → 正常命中体素
                    if (abs(normalPacked.w - 0.5) < 0.2)
                    {
                        // 在体素内部 → 显示纯黑
                        return fixed4(0.0, 0.0, 0.0, 1.0);
                    }

                    // 真·背景 → 采样 Skybox Cubemap
                    float3 rayDir = normalize(i.ray.xyz);
                    // Unity 的 Skybox Cubemap 采样需要 Y 朝上，方向本身就是世界空间不需要转换
                    fixed4 sky = texCUBE(_SkyboxTex, rayDir);
                    return fixed4(sky.rgb, 1.0);
                }

                // 解码世界法线（G-Buffer 中 packed 到 [0,1]）
                float3 normal = normalPacked.xyz * 2.0 - 1.0;
                normal = normalize(normal);

                // ===== Lambert 漫反射 =====
                float ndotl = max(0.0, dot(normal, _LightDir));
                float3 diffuse = albedo.rgb * _LightColor * ndotl;

                // ===== 环境光 =====
                float3 ambient = albedo.rgb * _Ambient;

                // ===== 阴影系数（Pass 2 预留，当前=1.0 无阴影）=====
                fixed shadow = tex2D(_ShadowTex, i.uv).r;

                return fixed4((diffuse * shadow + ambient), 1.0);
            }
            ENDCG
        }
    }
}
