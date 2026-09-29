// MCB version comparison: the other version's shape as an x-ray ghost over the solid one, brighter where it is in
// front of it and faint where it is inside.
Shader "Hidden/MCB/VersionCompareGhost"
{
    Properties
    {
        _Color ("Color", Color) = (0.36, 0.78, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha One

        CGINCLUDE
        #include "UnityCG.cginc"
        fixed4 _Color;

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

        fixed4 Ghost(v2f i, float strength)
        {
            float edge = pow(1.0 - saturate(abs(normalize(i.viewNormal).z)), 2.2);
            return fixed4(_Color.rgb, (0.02 + 0.5 * edge) * strength);
        }
        ENDCG

        Pass
        {
            ZTest Greater
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag (v2f i) : SV_Target { return Ghost(i, 0.25); }
            ENDCG
        }

        Pass
        {
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag (v2f i) : SV_Target { return Ghost(i, 1.0); }
            ENDCG
        }
    }
}
