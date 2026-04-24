Shader "Custom/WashShader"
{
    Properties
    {
        [Header(Visual Settings)]
        _Color ("Base Color", Color) = (0.0, 0.1, 0.3, 0.5) // 暗めのベース色で発光を引き立てる
        [HDR] _EmissionColor ("Base Glow Color", Color) = (0.2, 0.0, 0.4, 1.0) // 幻想的なベース発光
        
        [Header(Glossiness (Shine))]
        _Metallic ("Metallic", Range(0, 1)) = 0.95 // 金属的な光沢
        _Smoothness ("Smoothness", Range(0, 1)) = 0.98 // 鏡のような滑らかさ

        [Header(Psychedelic Rim (Crazy Aura))]
        [HDR] _RimColor ("Aura Base Color", Color) = (1.0, 0.0, 1.0, 1.0) // オーラの基本色
        _RimPower ("Aura Power", Range(0.5, 8.0)) = 1.5 // 数値を小さくするとオーラが広がる
        _PsySpeed ("Psychedelic Speed", Range(0.1, 10.0)) = 5.0 // 色が変化するスピード
        _PsyIntensity ("Psychedelic Intensity", Range(0.0, 1.0)) = 0.8 // サイケデリック色の強さ

        [Header(Deformation (Crazy Waves))]
        _WaveSpeed ("Wave Speed", Range(0.1, 5.0)) = 3.0 // うねるスピード
        _WaveFreq ("Wave Frequency", Range(1.0, 15.0)) = 12.0 // 波の細かさ
        _WaveAmp ("Wave Amplitude", Range(0.0, 0.5)) = 0.12 // 波の高さ
        _ViewDeform ("View Deform Factor", Range(0.0, 1.0)) = 0.5 // 視線方向による歪み（狂気増幅）
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 200

        CGPROGRAM
        // vertex:vert を使用して複雑な頂点変形を有効化
        #pragma surface surf Standard alpha:fade vertex:vert
        #pragma target 3.0

        struct Input
        {
            float3 viewDir;
            float3 worldPos; // サイケデリック効果の計算に使用
        };

        fixed4 _Color;
        fixed4 _EmissionColor;
        float _Metallic;
        float _Smoothness;
        fixed4 _RimColor;
        float _RimPower;
        float _PsySpeed;
        float _PsyIntensity;
        float _WaveSpeed;
        float _WaveFreq;
        float _WaveAmp;
        float _ViewDeform;

        // 頂点変形（複数の波を重ね合わせ、視線方向でも歪ませる）
        void vert (inout appdata_full v)
        {
            float time = _Time.y * _WaveSpeed;
            
            // 1. 複数のサイン波・コサイン波を重ね合わせて、複雑不規則なうねりを作る
            float3 wavePos = v.vertex.xyz * _WaveFreq;
            float wave = sin(wavePos.x + time) 
                       * cos(wavePos.y + time) 
                       * sin(wavePos.z + time);
            
            // 2. さらに高周波の波を重ねて、表面をざわつかせる（狂気要素）
            wave += sin(wavePos.x * 2.3 + time * 1.7) 
                  * cos(wavePos.y * 1.9 + time * 2.1) 
                  * sin(wavePos.z * 1.4 + time * 1.3) * 0.3;

            // 3. 視線方向に基づく変形を加える（見る角度で形がぐにゃりと変わる）
            // 簡易的な視線方向（球体の中心から頂点へのベクトル）を使用
            float3 viewDir = normalize(v.vertex.xyz); 
            float viewFactor = saturate(dot(v.normal, viewDir));
            wave *= (1.0 + viewFactor * _ViewDeform);

            // 4. 頂点を法線方向に押し出す／へこませる
            v.vertex.xyz += v.normal * wave * _WaveAmp;
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // --- 1. 基本色と超光沢 ---
            o.Albedo = _Color.rgb;
            o.Metallic = _Metallic; // 高い金属感
            o.Smoothness = _Smoothness; // 高い滑らかさ（鏡面反射）

            // --- 2. リムライト（オーラ） ---
            half rim = 1.0 - saturate(dot (normalize(IN.viewDir), o.Normal));
            fixed3 rimAura = _RimColor.rgb * pow (rim, _RimPower);

            // --- 3. サイケデリック効果 (狂気的な色歪み) ---
            // 時間、ワールド座標、法線から、めまぐるしく変わる色を計算
            float3 psyPos = IN.worldPos * 0.3 + o.Normal * 0.1;
            float3 psyColorShift = 0.5 + 0.5 * cos(_Time.y * _PsySpeed + psyPos.xyx + fixed3(0,2,4));
            
            // 基本のオーラ色にサイケデリック色を混色
            fixed3 finalRim = lerp(rimAura, rimAura * psyColorShift, _PsyIntensity);

            // --- 4. 発光 (幻想的なベース + 狂気的なリム) ---
            o.Emission = _EmissionColor.rgb + finalRim;

            // --- 5. 透明度 ---
            o.Alpha = _Color.a;
        }
        ENDCG
    }
    FallBack "UI/Default"
}