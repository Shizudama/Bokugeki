Shader "Custom/Persona"
{
    Properties {
        [Header(Base Settings)]
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        
        // 明るさ調整（MToonっぽく影を飛ばす用）
        _Brightness ("MToon Brightness", Range(0.0, 1.0)) = 0.5

        [Header(Motion Settings)]
        // 0:被っている状態, 1:完全に脱いだ状態
        _PeelProgress ("Progress (0 to 1)", Range(0, 1)) = 0.0
        
        // 回転の中心になる「ワールド空間の高さ」（首の位置）
        // PersonaSetup が実行時にアバターの Neck ボーン Y 座標を自動で書き込む。
        _PivotWorldY ("Pivot World Y (Neck)", Float) = 1.4
        
        // 回転の支点の奥行き（ワールド空間Z）
        _PivotWorldZ ("Pivot World Z", Float) = 0.0
        
        // 回転の勢い
        _RotationStrength ("Rotation Strength", Range(0.1, 3.0)) = 0.5

        [Header(Clip Settings)]
        // ★このワールド空間の高さ(Y)より下は描画しない
        //   Neck ボーン Y - 0.05 を PersonaSetup が自動設定する
        _ClipWorldY ("Clip World Y (Neck Cut)", Float) = 1.35

        [Header(Fade Settings)]
        // フェードアウト開始タイミング
        _FadeStart ("Fade Start Point", Range(0.0, 0.9)) = 0.0
        
        [Header(Backside Settings)]
        _BackColor ("Backside Color", Color) = (0.3, 0.2, 0.2, 1)
    }
    SubShader {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off 

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert addshadow alpha:fade
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;
        fixed4 _BackColor;
        half _Brightness;

        float _PeelProgress;
        float _PivotWorldY;
        float _PivotWorldZ;
        float _RotationStrength;
        float _FadeStart;
        float _ClipWorldY;

        // ★変更: 独自フィールド clipMask で vert → surf にクリップマスクを明示的に運ぶ
        struct Input {
            float2 uv_MainTex;
            float facing : VFACE;
            float clipMask;
        };

        float3x3 RotationX(float angle) {
            float s, c;
            sincos(angle, s, c);
            return float3x3(
                1, 0, 0,
                0, c, -s,
                0, s, c
            );
        }

        void vert (inout appdata_full v, out Input o) {
            UNITY_INITIALIZE_OUTPUT(Input, o);

            // ★変更: オブジェクト空間ではなくワールド空間で判定する。
            //   アバターのスケール・bind pose・メッシュ原点配置に関わらず、
            //   「ワールドでこの高さより下は描画しない」という絶対基準になる。
            //   メッシュ頂点をワールド変換してから Y を比較。
            float3 worldPos = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
            o.clipMask = step(_ClipWorldY, worldPos.y);

            if (_PeelProgress > 0.001) {
                // ワールド空間で回転処理を行う
                //   1. 頂点をワールド座標へ
                //   2. ピボットを中心に回転
                //   3. オブジェクト空間に戻す
                float3 pivotW = float3(0, _PivotWorldY, _PivotWorldZ);
                // X はアバター正面中心と仮定(アバターはワールド原点付近に置かれる想定)

                float3 relW = worldPos - pivotW;
                float angle = -1.0 * smoothstep(0.0, 1.0, _PeelProgress) * 3.14 * _RotationStrength;
                float3x3 rotMatrix = RotationX(angle);
                float3 rotatedW = mul(rotMatrix, relW);
                float3 newWorld = pivotW + rotatedW;
                // 前方へ少し押し出し（ワールドZ）
                newWorld.z += _PeelProgress * 0.15;

                // ワールド→オブジェクトに戻す
                v.vertex = mul(unity_WorldToObject, float4(newWorld, 1.0));
            }
        }

        void surf (Input IN, inout SurfaceOutput o) {
            // ★首より下のフラグメントを完全破棄（＝頭部のみ peel）
            clip(IN.clipMask - 0.5);

            fixed4 c = tex2D (_MainTex, IN.uv_MainTex) * _Color;
            
            if (IN.facing < 0) {
                 c.rgb = c.rgb * 0.3 + _BackColor.rgb * 0.7;
            }
            
            o.Albedo = c.rgb;
            o.Emission = c.rgb * _Brightness;

            float fadeAlpha = 1.0 - smoothstep(_FadeStart, 1.0, _PeelProgress);
            o.Alpha = c.a * fadeAlpha;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
