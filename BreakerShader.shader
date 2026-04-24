Shader "Custom/BreakerShader"
{
    Properties
    {
        _MainColor ("Base Color", Color) = (0.1, 0.1, 0.1, 1)
        _ElectricColor ("Electric Glow Color", Color) = (0, 0.5, 1, 1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _NoiseTex ("Lightning Noise", 2D) = "gray" {}
        _Speed ("Electric Speed", Vector) = (0.5, 0.7, 0, 0)
        _Intensity ("Glow Intensity", Range(0, 10)) = 5.0
        _Threshold ("Lightning Threshold", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        struct Input
        {
            float2 uv_MainTex;
            float2 uv_NoiseTex;
        };

        fixed4 _MainColor;
        fixed4 _ElectricColor;
        sampler2D _MainTex;
        sampler2D _NoiseTex;
        float4 _Speed;
        float _Intensity;
        float _Threshold;

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // ベーステクスチャのサンプリング
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _MainColor;
            
            // 時間経過で2方向にノイズをスクロールさせて合成
            float2 uv1 = IN.uv_NoiseTex + _Time.y * _Speed.xy;
            float2 uv2 = IN.uv_NoiseTex - _Time.y * _Speed.yx * 0.8;
            
            fixed4 noise1 = tex2D(_NoiseTex, uv1);
            fixed4 noise2 = tex2D(_NoiseTex, uv2);
            
            // 2つのノイズを掛け合わせて鋭い電撃の筋を作る
            float lightning = noise1.r * noise2.r;
            
            // しきい値処理で電撃の形をはっきりさせる
            lightning = smoothstep(_Threshold, _Threshold + 0.05, lightning);
            
            // 明滅（狂気的なパルス）を加える
            float pulse = sin(_Time.z * 10.0) * 0.5 + 0.5;
            float finalElectric = lightning * _Intensity * (1.0 + pulse * 0.5);

            o.Albedo = c.rgb;
            // 電撃部分をEmissionとして発光させる
            o.Emission = _ElectricColor.rgb * finalElectric;
            o.Metallic = 0.8;
            o.Smoothness = 0.5;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}