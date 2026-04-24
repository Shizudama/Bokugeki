Shader "Custom/BoneShader" {
    Properties {
        _MainTex ("Base Texture", 2D) = "white" {}
        _Color ("Base Color", Color) = (0.05, 0.0, 0.1, 1.0) // 暗い紫や黒系を推奨
        _EmissionColor ("Pulse Blood Color", Color) = (0.8, 0.0, 0.1, 1.0) // 狂気の赤
        _PulseSpeed ("Pulse Speed", Range(0.1, 10.0)) = 3.0
        _DistortAmount ("Madness Distortion", Range(0.0, 0.5)) = 0.03
        _RimColor ("Phantom Rim Color", Color) = (0.4, 0.0, 1.0, 1.0) // 幻想的な紫や青
        _RimPower ("Rim Power", Range(0.5, 8.0)) = 2.5
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.3
    }
    SubShader {
        // PCVR向けの不透明オブジェクトとして描画
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        CGPROGRAM
        // サーフェスシェーダー、頂点シェーダーを利用、影のフルサポート
        #pragma surface surf Standard vertex:vert fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;
        fixed4 _EmissionColor;
        float _PulseSpeed;
        float _DistortAmount;
        fixed4 _RimColor;
        float _RimPower;
        half _Glossiness;
        half _Metallic;

        struct Input {
            float2 uv_MainTex;
            float3 viewDir;
            float3 worldPos;
        };

        // 【狂気的要素】頂点を時間経過で不規則にうごめかせる
        void vert (inout appdata_full v) {
            // 時間と頂点位置に基づくノイズ的なサイン波で、肉体が変異するような動きを表現
            float wave1 = sin(_Time.y * _PulseSpeed * 1.5 + v.vertex.x * 15.0 + v.vertex.z * 10.0);
            float wave2 = cos(_Time.y * _PulseSpeed * 0.8 + v.vertex.y * 20.0);
            
            // 法線方向（外側）に向けて頂点を膨張・収縮させる
            v.vertex.xyz += v.normal * (wave1 * wave2 * _DistortAmount);
        }

        void surf (Input IN, inout SurfaceOutputStandard o) {
            // ベースカラーの適用
            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;

            // 【悪魔的要素】脈打つ血管のような這い回る発光
            float basePulse = (sin(_Time.y * _PulseSpeed) + 1.0) * 0.5;
            // ワールド空間のY座標（高さ）を混ぜて、下から上へ這い上がるような光の波を作る
            float creepingGlow = sin(IN.worldPos.y * 10.0 - _Time.y * _PulseSpeed * 2.0);
            // 負の値をカットし、鋭い発光にする
            float finalPulse = saturate(basePulse * creepingGlow);

            // 【幻想的要素】リムライト（視線と法線の角度から、輪郭だけをぼんやり光らせる）
            half rim = 1.0 - saturate(dot (normalize(IN.viewDir), o.Normal));
            float3 rimGlow = _RimColor.rgb * pow (rim, _RimPower);

            // 発光（脈打ち ＋ 輪郭の光）の合成
            o.Emission = (_EmissionColor.rgb * finalPulse) + rimGlow;
            
            // 質感を少し生々しくするためのメタリックとスムーズネス
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}