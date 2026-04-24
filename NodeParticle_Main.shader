Shader "Custom/NodeParticle_Main"
{
    Properties
    {
        [Header(Gradient Settings)]
        _Color1 ("Color 1", Color) = (0.1, 0.2, 0.8, 1.0)
        _Color2 ("Color 2", Color) = (0.1, 0.9, 0.9, 1.0)
        _Color3 ("Color 3", Color) = (0.8, 0.4, 1.0, 1.0)
        _Color4 ("Color 4", Color) = (1.0, 0.0, 0.7, 1.0)

        _ColorSpeed ("Color Cycle Speed", Range(0, 5.0)) = 0.5
        _ColorScale ("Color Gradient Scale", Range(0, 0.1)) = 0.01

        [Header(Texture Variation)]
        _MainTex ("Particle Texture", 2D) = "white" {}

        [Header(Geometry Pattern)]
        [KeywordEnum(Spiral, Grid, Atom, Scatter)] _GeoPattern ("Placement Pattern", Float) = 0
        _Spread ("Pattern Spread / Scale", Range(0.1, 5.0)) = 1.0

        [Header(AudioLink)]
        [Toggle] _UseAudioLink ("Use AudioLink?", Float) = 1
        // _AudioTexture はインスペクターから隠してGlobal変数を受け取る
        _AudioBassScale ("Bass Scale Power", Range(0, 2.0)) = 0.5
        _AudioTrebleEmission ("Treble Emission Power", Range(0, 5.0)) = 2.0

        // --- Interaction設定は削除しました ---

        [Header(World Simulation)]
        [Toggle] _UseWorldSpaceNoise ("Simulate in World Space?", Float) = 0

        [Header(Edge Settings)]
        [Toggle] _EdgeUseSolid ("Use Solid Edge Color?", Float) = 0
        _EdgeSolidColor ("Solid Edge Color", Color) = (1,1,1,1)

        [Header(Geometry)]
        _Size ("Node Size", Range(0.001, 1)) = 0.05
        _Radius ("Radius (Base Scale)", Float) = 0.5
        _NodeCount ("Active Node Count", Range(10, 4096)) = 50

        [Header(Animation)]
        _AnimSpeed ("Animation Speed", Range(0, 2.0)) = 0.2

        [Header(Lines)]
        _LineThickness ("Line Thickness", Range(0.0001, 0.02)) = 0.008
        _ConnectDist ("Connection Distance", Range(0, 10.0)) = 0.4 

        [Header(Masking)]
        _MaskRadius ("Mask Radius", Float) = 5.0
        _MaskSoftness ("Mask Softness", Range(0, 1)) = 0.5
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha
        
        ZTest LEqual 
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma multi_compile _GEOPATTERN_SPIRAL _GEOPATTERN_GRID _GEOPATTERN_ATOM _GEOPATTERN_SCATTER
            #include "UnityCG.cginc"

            // --- パラメータ定義 ---
            float4 _Color1; float4 _Color2; float4 _Color3; float4 _Color4;
            float _ColorSpeed; float _ColorScale;
            
            sampler2D _MainTex; float4 _MainTex_ST;
            
            float _Spread;
            float _UseAudioLink;
            sampler2D _AudioTexture;
            float _AudioBassScale;
            float _AudioTrebleEmission;

            // --- Interaction変数は削除しました ---

            float _UseWorldSpaceNoise;

            float _EdgeUseSolid; float4 _EdgeSolidColor;
            float _Size; float _Radius; float _NodeCount;
            float _AnimSpeed; float _LineThickness; float _ConnectDist;
            float _MaskRadius; float _MaskSoftness;

            struct appdata {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2g {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float id : TEXCOORD0;
                float audioBass : TEXCOORD1;
            };

            struct g2f {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float isNode : TEXCOORD1; 
                float audioTreble : TEXCOORD2;
            };

            float random(float id) {
                return frac(sin(id * 12.9898) * 43758.5453);
            }

            float4 GetGradientColor(float id)
            {
                float t = frac(id * _ColorScale + _Time.y * _ColorSpeed);
                float4 col;
                if (t < 0.25) col = lerp(_Color1, _Color2, smoothstep(0.0, 0.25, t));
                else if (t < 0.50) col = lerp(_Color2, _Color3, smoothstep(0.25, 0.50, t));
                else if (t < 0.75) col = lerp(_Color3, _Color4, smoothstep(0.50, 0.75, t));
                else col = lerp(_Color4, _Color1, smoothstep(0.75, 1.0, t));
                return col;
            }

            float GetAudioData(float band) 
            {
                if (_UseAudioLink < 0.5) return 0.0;
                return tex2Dlod(_AudioTexture, float4(band / 4.0 + 0.125, 0, 0, 0)).r;
            }

            float3 GetGeometryPos(float id, float audioBass)
            {
                float total = max(_NodeCount, 1.0);
                float t = _Time.y * _AnimSpeed;
                float3 basePos = float3(0,0,0);
                float r_scale = _Radius * _Spread;

                r_scale *= (1.0 + audioBass * _AudioBassScale);

                #if defined(_GEOPATTERN_GRID)
                    float cubeSize = ceil(pow(total, 1.0/3.0));
                    float x = fmod(id, cubeSize);
                    float y = fmod(floor(id / cubeSize), cubeSize);
                    float z = floor(id / (cubeSize * cubeSize));
                    basePos = (float3(x, y, z) - cubeSize * 0.5) * (r_scale * 0.5);
                #elif defined(_GEOPATTERN_ATOM)
                    float phi = acos(1.0 - 2.0 * (id / total));
                    float golden_angle = 2.39996323; 
                    float theta = golden_angle * id;
                    basePos.x = sin(phi) * cos(theta);
                    basePos.y = sin(phi) * sin(theta);
                    basePos.z = cos(phi);
                    basePos *= r_scale;
                #elif defined(_GEOPATTERN_SCATTER)
                    float3 randDir;
                    randDir.x = random(id) * 2.0 - 1.0;
                    randDir.y = random(id + 1.1) * 2.0 - 1.0;
                    randDir.z = random(id + 2.2) * 2.0 - 1.0;
                    basePos = normalize(randDir) * r_scale * (0.5 + 0.5 * random(id + 3.3));
                #else 
                    float k = id / total;
                    float phi_s = k * 3.14159 * 20.0;
                    float theta_s = k * 3.14159 * 100.0;
                    float r_s = r_scale * (0.2 + 0.8 * frac(k * 25.0));
                    basePos = float3(r_s * sin(phi_s) * cos(theta_s), r_s * cos(phi_s), r_s * sin(phi_s) * sin(theta_s));
                #endif

                float3 noiseSeed = basePos;
                if (_UseWorldSpaceNoise > 0.5)
                {
                    float3 worldPos = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
                    noiseSeed += worldPos;
                }

                float3 noise = float3(
                    sin(t * 1.1 + id * 0.13 + noiseSeed.x),
                    sin(t * 0.9 + id * 0.21 + noiseSeed.y),
                    cos(t * 1.3 + id * 0.17 + noiseSeed.z)
                );
                
                float noiseStrength = 0.4 * _Radius;
                #if defined(_GEOPATTERN_GRID)
                    noiseStrength *= 0.3;
                #endif

                float3 finalPos = basePos + noise * noiseStrength;

                // --- Interactionロジックは削除しました ---
                
                return finalPos;
            }

            v2g vert (appdata v) {
                v2g o;
                float width = 64.0; float height = 64.0;
                float id = floor(v.color.r * width + 0.5) + floor(v.color.g * height + 0.5) * width;
                o.id = id;
                o.pos = v.vertex; 
                o.color = v.color;
                o.audioBass = GetAudioData(0.0);
                return o;
            }

            [maxvertexcount(8)] 
            void geom(point v2g input[1], inout TriangleStream<g2f> stream)
            {
                float id = input[0].id;
                if (id >= _NodeCount) return;

                float audioBass = input[0].audioBass;
                float audioTreble = GetAudioData(3.0);

                // ローカル座標を取得した後、ワールド座標へ変換する
                float3 currentPosLocal = GetGeometryPos(id, audioBass);
                float3 nextPosLocal = GetGeometryPos(id + 1.0, audioBass);

                float3 currentPosWorld = mul(unity_ObjectToWorld, float4(currentPosLocal, 1.0)).xyz;
                float3 nextPosWorld = mul(unity_ObjectToWorld, float4(nextPosLocal, 1.0)).xyz;
                
                float4 nodeColor = GetGradientColor(id);
                float4 nextNodeColor = GetGradientColor(id + 1.0);

                // マスク判定は「ローカル座標（アバター中心からの距離）」で行う
                float distFromCenter = length(currentPosLocal);
                float maskAlpha = smoothstep(_MaskRadius, _MaskRadius * (1.0 - _MaskSoftness), distFromCenter);
                if (maskAlpha <= 0.001) return;

                // ビルボード用のカメラベクトル（ワールド空間）
                float3 camRight = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 camUp    = UNITY_MATRIX_I_V._m01_m11_m21;
                
                // ビュー方向もワールド座標同士で正しく計算
                float3 viewDir = normalize(_WorldSpaceCameraPos - currentPosWorld);

                // --- 接続ライン ---
                float distNodes = distance(currentPosWorld, nextPosWorld); // ワールド距離で判定
                float connectBoost = audioTreble * 0.2;
                float connectFluctuation = sin(_Time.y * 2.0 + id * 13.0);
                float dynamicThreshold = (_ConnectDist + connectBoost) * (1.0 + 0.5 * connectFluctuation);

                #if defined(_GEOPATTERN_GRID)
                   dynamicThreshold *= 1.5;
                #endif

                if (distNodes < dynamicThreshold)
                {
                    float3 lineDir = normalize(nextPosWorld - currentPosWorld);
                    float3 lineNormal = cross(lineDir, viewDir); 
                    lineNormal = normalize(lineNormal) * _LineThickness * 0.5;

                    g2f lIn;
                    lIn.isNode = 0.0;
                    lIn.audioTreble = audioTreble;

                    float4 cStart = (_EdgeUseSolid > 0.5) ? _EdgeSolidColor : nodeColor;
                    float4 cEnd   = (_EdgeUseSolid > 0.5) ? _EdgeSolidColor : nextNodeColor;
                    cStart.a *= maskAlpha;
                    cEnd.a *= maskAlpha;
                    
                    float emission = 1.0 + audioTreble * _AudioTrebleEmission;
                    cStart.rgb *= emission;
                    cEnd.rgb *= emission;

                    lIn.uv = float2(0,0); lIn.color = cStart;
                    // UnityWorldToClipPos を使用してワールド座標から変換
                    lIn.pos = UnityWorldToClipPos(currentPosWorld - lineNormal); stream.Append(lIn);
                    lIn.pos = UnityWorldToClipPos(currentPosWorld + lineNormal); stream.Append(lIn);
                    lIn.uv = float2(1,0); lIn.color = cEnd;
                    lIn.pos = UnityWorldToClipPos(nextPosWorld - lineNormal); stream.Append(lIn);
                    lIn.pos = UnityWorldToClipPos(nextPosWorld + lineNormal); stream.Append(lIn);
                    stream.RestartStrip();
                }

                // --- ノード描画 ---
                g2f pIn;
                pIn.color = nodeColor;
                pIn.color.a *= maskAlpha;
                pIn.isNode = 1.0;
                pIn.audioTreble = audioTreble;
                
                float s = _Size * (1.0 + audioBass * _AudioBassScale);
                
                // ワールド座標に対してカメラベクトル（ワールド）を適用
                pIn.pos = UnityWorldToClipPos(currentPosWorld - camRight * s - camUp * s); pIn.uv = float2(0, 0); stream.Append(pIn);
                pIn.pos = UnityWorldToClipPos(currentPosWorld + camRight * s - camUp * s); pIn.uv = float2(1, 0); stream.Append(pIn);
                pIn.pos = UnityWorldToClipPos(currentPosWorld - camRight * s + camUp * s); pIn.uv = float2(0, 1); stream.Append(pIn);
                pIn.pos = UnityWorldToClipPos(currentPosWorld + camRight * s + camUp * s); pIn.uv = float2(1, 1); stream.Append(pIn);
                stream.RestartStrip();
            }

            fixed4 frag (g2f i) : SV_Target
            {
                if (i.isNode > 0.5)
                {
                    fixed4 texColor = tex2D(_MainTex, i.uv);
                    
                    float blink = 0.7 + 0.3 * sin(_Time.y * 3.0 + i.pos.x * 0.1);
                    float emission = 1.0 + i.audioTreble * _AudioTrebleEmission;
                    
                    return float4(i.color.rgb * texColor.rgb * blink * emission, i.color.a * texColor.a);
                }
                else
                {
                    return i.color;
                }
            }
            ENDCG
        }
    }
}