Shader "Custom/DefragShader"
{
    Properties
    {
        _MainTex ("Base Noise", 2D) = "white" {}
        _Speed ("Distortion Speed", Float) = 0.5
        _Intensity ("Madness Intensity", Range(0, 5)) = 1.2
        _CosmicColor ("Cosmic Tint", Color) = (0.1, 0.05, 0.2, 1)
        _GlitchScale ("Glitch Scale", Float) = 10.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        Blend One One
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 localPos : TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Speed;
            float _Intensity;
            float4 _CosmicColor;
            float _GlitchScale;

            // 擬似乱数・ノイズ関数
            float hash(float n) { return frac(sin(n) * 43758.5453123); }
            
            float noise(float3 x)
            {
                float3 p = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n = p.x + p.y * 57.0 + 113.0 * p.z;
                return lerp(lerp(lerp(hash(n + 0.0), hash(n + 1.0), f.x),
                            lerp(hash(n + 57.0), hash(n + 58.0), f.x), f.y),
                            lerp(lerp(hash(n + 113.0), hash(n + 114.0), f.x),
                            lerp(hash(n + 170.0), hash(n + 171.0), f.x), f.y), f.z);
            }

            v2f vert (appdata v)
            {
                v2f o;
                // 筒の形状を時間で微細に歪ませる（狂気的な脈動）
                float pulse = sin(_Time.y * _Speed * 2.0) * 0.05;
                v.vertex.xyz += v.vertex.xyz * pulse * noise(v.vertex.xyz * _GlitchScale + _Time.y);
                
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.localPos = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 時間軸でのUVスクロール
                float2 distortUV = i.uv;
                distortUV.y += _Time.y * _Speed * 0.1;
                
                // 宇宙的ノイズの重畳
                float n = noise(float3(distortUV * _GlitchScale, _Time.y * _Speed));
                float n2 = noise(float3(distortUV * _GlitchScale * 1.5, _Time.y * _Speed * 0.5));
                
                // 幻想的な色の計算（極光や星雲のイメージ）
                float3 col;
                col.r = sin(n * 10.0 + _Time.y) * 0.5 + 0.5;
                col.g = sin(n2 * 7.0 - _Time.y * 0.8) * 0.5 + 0.5;
                col.b = cos(n * 5.0 + n2 * 3.0) * 0.5 + 0.5;
                
                // 宇宙の深淵色とブレンド
                float3 finalColor = lerp(_CosmicColor.rgb, col, n * _Intensity);
                
                // 筒の端をフェードアウト（罪の消失を表現）
                float edgeFade = smoothstep(0.0, 0.2, i.uv.y) * smoothstep(1.0, 0.8, i.uv.y);
                
                return float4(finalColor * edgeFade, 1.0);
            }
            ENDCG
        }
    }
}