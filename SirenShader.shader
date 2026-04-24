Shader "Custom/SirenShader"
{
    Properties
    {
        [Header(Base Texture and Color)]
        _MainTex ("Base Texture (画像テクスチャ)", 2D) = "white" {}
        _Color ("Color Tint (色合い・透明度調整)", Color) = (1, 1, 1, 1)

        [Header(Metallic Settings)]
        _Metallic ("Metallic (金属度)", Range(0,1)) = 1.0
        _Glossiness ("Smoothness (滑らかさ)", Range(0,1)) = 0.85

        [Header(Squish Settings)]
        _WarpSpeed ("Warp Speed (変形の速さ)", Float) = 3.0
        _WarpScale ("Warp Scale (変形の細かさ)", Float) = 4.0
        _WarpPower ("Warp Power (変形の大きさ)", Float) = 0.05
        
        // 追加: 暗い場所での視認性を確保するためのベース発光
        [Header(Emission Settings)]
        _BaseLuminance ("Base Luminance in Dark (暗所での発光強度)", Range(0, 1)) = 0.2
    }
    SubShader
    {
        // 描画順（Queue）とレンダリングタイプ（RenderType）をTransparent（透過）に指定
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 200

        CGPROGRAM
        // 末尾に「alpha:fade」を追加し、透過ブレンドを有効化
        #pragma surface surf Standard vertex:vert addshadow fullforwardshadows alpha:fade
        #pragma target 3.0

        struct Input
        {
            float2 uv_MainTex;
        };

        sampler2D _MainTex;
        fixed4 _Color;
        half _Metallic;
        half _Glossiness;
        
        float _WarpSpeed;
        float _WarpScale;
        float _WarpPower;
        
        float _BaseLuminance; // 追加

        // 頂点シェーダー：モデルの形状をぐにゃぐにゃに歪ませる
        void vert (inout appdata_full v)
        {
            float3 localPos = v.vertex.xyz;
            float time = _Time.y * _WarpSpeed;

            float warpX = sin(localPos.y * _WarpScale + time) * cos(localPos.z * _WarpScale * 0.8 + time * 1.2);
            float warpY = sin(localPos.z * _WarpScale * 1.1 + time * 0.9) * cos(localPos.x * _WarpScale * 0.7 + time * 0.8);
            float warpZ = sin(localPos.x * _WarpScale * 0.9 + time * 1.1) * cos(localPos.y * _WarpScale * 1.2 + time * 0.7);
            
            v.vertex.xyz += float3(warpX, warpY, warpZ) * _WarpPower;
        }

        // サーフェスシェーダー：テクスチャと金属の質感を設定
        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 texColor = tex2D(_MainTex, IN.uv_MainTex) * _Color;

            o.Albedo = texColor.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            
            // 追加：暗所対策として、テクスチャのベースカラーをEmissionに加算
            o.Emission = texColor.rgb * _BaseLuminance;

            // テクスチャのアルファ値（透過度）とColorのアルファ値を適用
            o.Alpha = texColor.a;
        }
        ENDCG
    }
    FallBack "Transparent/Diffuse"
}