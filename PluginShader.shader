Shader "Custom/PluginShader"
{
    Properties
    {
        _MainTex ("Texture (RGBA)", 2D) = "white" {}
        _Speed ("Wave Speed", Range(0, 10)) = 2.0
        _Frequency ("Wave Frequency", Range(0, 20)) = 5.0
        _Amplitude ("Wave Amplitude", Range(0, 1)) = 0.1
        _HueSpeed ("Color Cycle Speed", Range(0, 5)) = 1.0
        _Opacity ("Overall Opacity", Range(0, 1)) = 1.0
    }
    SubShader
    {
        // 透過設定：半透明キューに入れ、計算方法をSrcAlpha OneMinusSrcAlphaに
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 200

        // 背面を描画しない設定（必要に応じてOffにすると中身も透けます）
        Cull Back
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert alpha:fade
        #pragma target 3.0

        sampler2D _MainTex;
        float _Speed;
        float _Frequency;
        float _Amplitude;
        float _HueSpeed;
        float _Opacity;

        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
        };

        float3 hsb2rgb(float3 c)
        {
            float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
            float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
            return c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
        }

        void vert (inout appdata_full v)
        {
            float time = _Time.y * _Speed;
            v.vertex.xyz += v.normal * sin(v.vertex.y * _Frequency + time) * _Amplitude;
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // サイケデリックカラー計算
            float colorOffset = IN.worldPos.y * 0.5 + _Time.y * _HueSpeed;
            float3 psychedelicColor = hsb2rgb(float3(frac(colorOffset), 0.8, 1.0));
            
            // テクスチャ読み込み
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex);
            
            o.Albedo = c.rgb * psychedelicColor;
            o.Emission = psychedelicColor * 0.4; // 発光感
            o.Metallic = 0.2;
            o.Smoothness = 0.5;
            
            // 透過度の適用：テクスチャのAチャンネル × インスペクターのOpacity
            o.Alpha = c.a * _Opacity;
        }
        ENDCG
    }
    FallBack "Transparent/Diffuse"
}