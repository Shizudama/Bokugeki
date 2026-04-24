Shader "Custom/PealShader"
{
    Properties
    {
        _MainColor ("Base Color", Color) = (0.9, 0.9, 1, 1)
        _InnerColor ("Inner Pulse Color", Color) = (0.5, 0, 1, 1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.95
        _Metallic ("Metallic", Range(0,1)) = 0.5
        _RimPower ("Rim Light Power", Range(0.5, 8.0)) = 3.0
        _PulseSpeed ("Pulse Speed", Range(0, 10)) = 2.0
        _Distortion ("Distortion Intensity", Range(0, 1)) = 0.3
        
        // 追加: 暗所でのベースカラー発光強度
        _BaseLuminance ("Base Luminance in Dark", Range(0, 1)) = 0.2
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
            float3 viewDir;
            float3 worldNormal;
            float4 screenPos;
        };

        half _Glossiness;
        half _Metallic;
        fixed4 _MainColor;
        fixed4 _InnerColor;
        float _RimPower;
        float _PulseSpeed;
        float _Distortion;
        float _BaseLuminance; // 追加

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // 1. 視線ベクトルと法線の計算
            float dotProduct = dot(normalize(IN.viewDir), IN.worldNormal);
            float rim = 1.0 - saturate(dotProduct);

            // 2. 虹色の干渉光 (パール感)
            float3 rainbow = float3(
                sin(rim * 10.0 + _Time.y),
                sin(rim * 10.0 + _Time.y + 2.0),
                sin(rim * 10.0 + _Time.y + 4.0)
            ) * 0.5 + 0.5;

            // 3. 狂気的な脈動 (Emission)
            float pulse = pow(rim, _RimPower) * (sin(_Time.y * _PulseSpeed) * 0.5 + 0.5);
            float3 madness = _InnerColor.rgb * pulse * 2.0;

            // 4. ベースカラーの合成
            float3 baseAlbedo = lerp(_MainColor.rgb, rainbow, 0.3);
            o.Albedo = baseAlbedo;

            // 5. 発光設定 (暗所対策としてbaseAlbedoをEmissionにも加算)
            // madness（脈動）とrainbow（リム）に加えて、ベースカラー自体を_BaseLuminanceの強度で発光させる
            o.Emission = madness + (rainbow * pow(rim, 2.0) * 0.5) + (baseAlbedo * _BaseLuminance);

            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1.0;
        }
        ENDCG
    }
    FallBack "Diffuse"
}