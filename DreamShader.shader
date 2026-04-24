Shader "Custom/DreamShader"
{
    Properties
    {
        _MainTex ("宇宙の核 (Base Texture)", 2D) = "white" {}
        _NoiseTex ("狂気のノイズ (Distortion Noise)", 2D) = "bump" {}
        _Color ("深淵の色 (Base Color)", Color) = (0.1, 0, 0.2, 1)
        _EmissionColor ("発光色 (Glow Color)", Color) = (0.5, 0, 1, 1)
        _Speed ("流転速度 (Flow Speed)", Float) = 0.5
        _Distortion ("狂気度 (Distortion Intensity)", Range(0, 1)) = 0.3
        _FresnelPower ("存在の境界 (Fresnel Power)", Range(0.5, 5.0)) = 2.0
        _Parallax ("深淵の奥行き (Inside Depth)", Range(0, 0.1)) = 0.05
        
        // 追加: 暗所でのベーステクスチャ・カラーの自己発光強度
        _BaseLuminance ("暗域の自己発光 (Base Luminance)", Range(0, 1)) = 0.2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf Standard fullforwardshadows

        sampler2D _MainTex;
        sampler2D _NoiseTex;
        float4 _Color;
        float4 _EmissionColor;
        float _Speed;
        float _Distortion;
        float _FresnelPower;
        float _Parallax;
        
        float _BaseLuminance; // 追加

        struct Input
        {
            float2 uv_MainTex;
            float3 viewDir;      // 視点方向（奥行き演出用）
            float3 worldNormal;  // 法線（フレンネル用）
        };

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            float time = _Time.y * _Speed;

            // --- 1. 狂気的な空間の歪み ---
            // ノイズテクスチャを2層重ねてスクロールさせ、歪みを作る
            float2 noiseUV = IN.uv_MainTex + float2(sin(time), cos(time)) * 0.1;
            float3 noise = tex2D(_NoiseTex, noiseUV).rgb;
            
            // --- 2. 深淵の奥行き（視差効果） ---
            // 視点方向に応じてUVをずらすことで、球体の中に空間があるように見せる
            float2 offset = IN.viewDir.xy * _Parallax * noise.r;
            float2 distortedUV = IN.uv_MainTex + offset + (noise.xy - 0.5) * _Distortion;

            // --- 3. メインテクスチャの描画 ---
            fixed4 c = tex2D(_MainTex, distortedUV) * _Color;

            // --- 4. フレンネル反射（縁が光る） ---
            // 視線と法線の角度から、外側に向かって発光を強める
            half fresnel = 1.0 - saturate(dot(normalize(IN.viewDir), IN.worldNormal));
            fresnel = pow(fresnel, _FresnelPower);

            // --- 最終出力 ---
            o.Albedo = c.rgb;
            
            // --- 5. 発光設定 (暗所対策としてベースカラーをEmissionに加算) ---
            // 脈動発光 + フレンネル発光 + ベースカラーの自己発光
            float3 pulseGlow = c.rgb * _EmissionColor.rgb * (sin(time * 2.0) * 0.5 + 0.5);
            float3 fresnelGlow = fresnel * _EmissionColor.rgb;
            float3 baseGlow = c.rgb * _BaseLuminance;
            
            o.Emission = pulseGlow + fresnelGlow + baseGlow;
            
            o.Metallic = 0.5;
            o.Smoothness = 0.8;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}