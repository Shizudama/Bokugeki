Shader "Custom/HypersurfaceShader"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Speed ("Speed", Float) = 2.0
        _Amount ("Distortion Amount", Float) = 0.5
        _Frequency ("Frequency", Float) = 3.0
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

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
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Speed;
            float _Amount;
            float _Frequency;
            fixed4 _Color;

            // 頂点シェーダー：ここで「超曲面的」な歪みを作る
            v2f vert (appdata v)
            {
                v2f o;
                
                // 時間と頂点座標に基づいた計算
                float time = _Time.y * _Speed;
                
                // X, Y, Zの各軸に対してサイン波を合成し、法線方向にオフセットをかける
                // これにより、ただの拡大縮小ではなく「複雑なうねり」が出る
                float distortion = sin(v.vertex.x * _Frequency + time) * cos(v.vertex.y * _Frequency + time) * sin(v.vertex.z * _Frequency + time);
                
                // 元の頂点座標に歪みを加える
                v.vertex.xyz += v.normal * distortion * _Amount;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                
                // 歪みの強さに応じて色を変化させると「高次元感」が増す
                o.color = _Color + (distortion * 0.5);
                
                return o;
            }

            // フラグメントシェーダー：色の描画
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * i.color;
                return col;
            }
            ENDCG
        }
    }
}