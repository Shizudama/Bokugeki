Shader "Custom/KarmaShader"
{
    Properties
    {
        _MainTex ("Base Texture (Karma)", 2D) = "white" {}
        _NoiseTex ("Distortion Noise", 2D) = "gray" {}
        _FlowColor ("Flowing Soul Color", Color) = (0.5, 0, 0, 1)
        _EmissionColor ("Madness Glow", Color) = (1, 0, 0, 1)
        _Speed ("Flow Speed", Float) = 0.5
        _PulseSpeed ("Pulse Speed", Float) = 2.0
        _PulseStrength ("Pulse Strength", Range(0, 0.2)) = 0.05
        _Distortion ("Distortion Intensity", Range(0, 1)) = 0.3
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

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
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 worldNormal : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
            };

            sampler2D _MainTex;
            sampler2D _NoiseTex;
            float4 _MainTex_ST;
            float4 _FlowColor;
            float4 _EmissionColor;
            float _Speed;
            float _PulseSpeed;
            float _PulseStrength;
            float _Distortion;

            v2f vert (appdata v)
            {
                v2f o;
                // 脈動する頂点アニメーション (筒を膨らませたり縮ませたり)
                float pulse = sin(_Time.y * _PulseSpeed + v.vertex.y * 10.0) * _PulseStrength;
                v.vertex.xyz += v.normal * pulse;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(WorldSpaceViewDir(v.vertex));
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // ノイズを利用したUVスクロール歪み
                float2 distortUV = i.uv + tex2D(_NoiseTex, i.uv + _Time.x * _Speed).rg * _Distortion;
                float2 flowUV = float2(distortUV.x, distortUV.y - _Time.y * _Speed);
                
                // ベーステクスチャのサンプリング
                fixed4 col = tex2D(_MainTex, flowUV);
                
                // 狂気的な色の混ざり（カルマの流動）
                col.rgb *= _FlowColor.rgb * (sin(_Time.y * 0.5) * 0.5 + 1.0);

                // リムライト（縁がヌルリと光る）
                half rim = 1.0 - saturate(dot(normalize(i.viewDir), normalize(i.worldNormal)));
                float rimGlow = pow(rim, 3.0);
                
                // 明滅するエミッション
                float blink = abs(sin(_Time.y * _PulseSpeed * 0.5));
                fixed3 emission = _EmissionColor.rgb * col.a * blink * 2.0;

                fixed4 finalColor;
                finalColor.rgb = col.rgb + emission + (rimGlow * _FlowColor.rgb);
                finalColor.a = 1.0;

                return finalColor;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}