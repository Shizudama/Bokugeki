Shader "Custom/SignageShader"
{
    Properties
    {
        [Header(Main Settings)]
        _MainTex ("Main Texture (RGB)", 2D) = "white" {}
        _Color ("Tint Color", Color) = (1, 1, 1, 1)
        
        [Header(Fluid Distortion)]
        _FluidSpeed ("Fluid Speed", Range(0.0, 5.0)) = 1.0
        _Distortion ("Distortion Amount", Range(0.0, 0.1)) = 0.02
        _WaveDensity ("Wave Density", Range(1.0, 20.0)) = 10.0

        [Header(Gloss and Reflection)]
        _Metallic ("Metallic", Range(0.0, 1.0)) = 0.8
        _Glossiness ("Smoothness (Gloss)", Range(0.0, 1.0)) = 0.9

        [Header(Digital Emission and Rim Light)]
        [HDR] _RimColor ("Rim Glow Color", Color) = (0.0, 0.8, 1.0, 1.0)
        _RimPower ("Rim Power", Range(0.1, 8.0)) = 2.0
        _EmissionStrength ("Base Emission Strength", Range(0.0, 5.0)) = 1.0
    }
    
    SubShader
    {
        // VRChatの一般的なワールド照明に馴染むOpaque（不透明）設定
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        CGPROGRAM
        // Standardライティングモデルを使用し、光沢感（PBR）を表現
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;
        float _FluidSpeed;
        float _Distortion;
        float _WaveDensity;
        half _Metallic;
        half _Glossiness;
        fixed4 _RimColor;
        float _RimPower;
        float _EmissionStrength;

        struct Input
        {
            float2 uv_MainTex;
            float3 viewDir;
            float3 worldPos;
        };

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // 1. 流動的な歪み（Fluid Distortion）の計算
            // 時間（_Time.y）とサイン波を使って、水面やデジタルノイズのようなうねりを作ります
            float2 distUV = IN.uv_MainTex;
            distUV.x += sin(IN.uv_MainTex.y * _WaveDensity + _Time.y * _FluidSpeed) * _Distortion;
            distUV.y += cos(IN.uv_MainTex.x * _WaveDensity + _Time.y * _FluidSpeed) * _Distortion;

            // 歪ませたUVでテクスチャをサンプリング
            fixed4 c = tex2D (_MainTex, distUV) * _Color;
            o.Albedo = c.rgb;

            // 2. 幻想的なリムライト（Rim Light）の計算
            // カメラの視線（viewDir）とオブジェクトの法線（Normal）から縁を光らせます
            half rim = 1.0 - saturate(dot (normalize(IN.viewDir), o.Normal));
            // 時間経過でリムライトを少し明滅（パルス）させてデジタル感を強調
            float pulse = sin(_Time.y * _FluidSpeed * 2.0) * 0.2 + 0.8;
            float3 rimGlow = _RimColor.rgb * pow(rim, _RimPower) * pulse;

            // 3. 発光（Emission）の設定
            // テクスチャ自体の色を少し発光させつつ、縁のホログラム発光を足し合わせます
            o.Emission = (c.rgb * _EmissionStrength) + rimGlow;

            // 4. 光沢感（Glossy）の設定
            // デジタルサイネージのガラスやアクリルのような質感を表現
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}