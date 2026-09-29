// MCB version comparison: a mesh the version leaves as it is, shown see-through so the eye goes to what changed while
// the avatar keeps its whole shape. A depth pass first, so the mesh never shows its own inside.
Shader "Hidden/MCB/VersionCompareUnchanged"
{
    Properties
    {
        _Color ("Color", Color) = (0.78, 0.8, 0.86, 1)
        _Alpha ("Alpha", Range(0, 1)) = 0.22
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-20" }
        Cull Back

        Pass
        {
            ZWrite On
            ColorMask 0
        }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Alpha;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 viewNormal : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.viewNormal = mul((float3x3)UNITY_MATRIX_IT_MV, v.normal);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 n = normalize(i.viewNormal);
                float light = 0.55 + 0.45 * saturate(dot(n, normalize(float3(-0.45, 0.62, 0.64))));
                float edge = pow(1.0 - saturate(n.z), 2.5);
                return fixed4(_Color.rgb * light + edge * 0.25, saturate(_Alpha + edge * 0.45));
            }
            ENDCG
        }
    }
}
