Shader "MUGONZEI/TicketShader"
{
    Properties
    {
        _MainTex ("Ticket Texture", 2D) = "white" {}
        _AnimSpeed ("Animation Speed", Float) = 0.5
        _NoiseScale ("Tear Noise Scale", Float) = 15.0
        _Distortion ("Tear Distortion", Float) = 0.3
        _EdgeColor ("Fiber Edge Color", Color) = (0.9, 0.9, 0.9, 1.0)
        _EdgeWidth ("Fiber Edge Width", Range(0.0, 0.1)) = 0.02
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        LOD 100
        
        Cull Off 

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _AnimSpeed;
            float _NoiseScale;
            float _Distortion;
            float4 _EdgeColor;
            float _EdgeWidth;

            // 疑似乱数
            float random(float2 st)
            {
                return frac(sin(dot(st.xy, float2(12.9898, 78.233))) * 43758.5453123);
            }

            // バリューノイズ
            float noise(float2 st)
            {
                float2 i = floor(st);
                float2 f = frac(st);
                float a = random(i);
                float b = random(i + float2(1.0, 0.0));
                float c = random(i + float2(0.0, 1.0));
                float d = random(i + float2(1.0, 1.0));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
            }

            // フラクタルブラウン運動 (fBm)
            float fbm(float2 st)
            {
                float value = 0.0;
                float amplitude = 0.5;
                for (int i = 0; i < 4; i++)
                {
                    value += amplitude * noise(st);
                    st *= 2.0;
                    amplitude *= 0.5;
                }
                return value;
            }

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);

                // ノイズの生成
                float n = fbm(i.uv * _NoiseScale);

                // 時間経過に基づくアニメーション進行度（1.0 から 0.0 へ向かってループ）
                float autoProgress = 1.0 - frac(_Time.y * _AnimSpeed);

                // 破断線の計算
                float tearBoundary = autoProgress + (n * _Distortion) - (_Distortion * 0.5);

                // 欠損の実行
                clip(tearBoundary - i.uv.x);

                // 断面の繊維カラー合成
                if (i.uv.x > tearBoundary - _EdgeWidth)
                {
                    float edgeFactor = smoothstep(tearBoundary - _EdgeWidth, tearBoundary, i.uv.x);
                    col.rgb = lerp(col.rgb, _EdgeColor.rgb, edgeFactor);
                }

                return col;
            }
            ENDCG
        }
    }
}