// MCB version comparison: soft studio clay, with the surface a version moves glowing from amber (a little) to magenta
// (the most). TEXCOORD7: x how much it moved (0 to 1), y what it is (0 unchanged, 1 reshaped, 2 added, 3 removed).
Shader "Hidden/MCB/VersionCompare"
{
    Properties
    {
        _Clay ("Clay", Color) = (0.8, 0.78, 0.76, 1)
        _Heat ("Heat", Range(0, 1)) = 1
        _Pulse ("Pulse", Range(0, 1)) = 0
        _Dim ("Dim", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            fixed4 _Clay;
            float _Heat, _Pulse, _Dim;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 heat : TEXCOORD7;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 viewNormal : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float4 heat : TEXCOORD2;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.viewNormal = mul((float3x3)UNITY_MATRIX_IT_MV, v.normal);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.heat = v.heat;
                return o;
            }

            float3 Ramp(float t)
            {
                float3 amber = float3(1.0, 0.8, 0.28);
                float3 orange = float3(1.0, 0.46, 0.2);
                float3 magenta = float3(0.96, 0.18, 0.5);
                return t < 0.5 ? lerp(amber, orange, t * 2.0) : lerp(orange, magenta, t * 2.0 - 1.0);
            }

            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.viewNormal) * (facing > 0 ? 1.0 : -1.0);
                float3 up = normalize(i.worldNormal) * (facing > 0 ? 1.0 : -1.0);
                float3 key = normalize(float3(-0.45, 0.62, 0.64));
                float3 fill = normalize(float3(0.7, 0.05, 0.5));
                float wrapKey = saturate(dot(n, key) * 0.6 + 0.4);
                float light = 0.18 + 0.72 * wrapKey * wrapKey + 0.16 * saturate(dot(n, fill));
                float3 ambient = lerp(float3(0.16, 0.15, 0.15), float3(0.3, 0.32, 0.36), up.y * 0.5 + 0.5);
                float rim = pow(1.0 - saturate(n.z), 3.0) * 0.32;
                float spec = pow(saturate(dot(n, normalize(key + float3(0, 0, 1)))), 40.0) * 0.18;

                float kind = i.heat.y;
                float3 hot = kind > 2.5 ? float3(1.0, 0.36, 0.38) : kind > 1.5 ? float3(0.2, 0.9, 0.6) : Ramp(saturate(i.heat.x));
                // Fades in from the smallest change, so the edge of a changed area is soft rather than a hard patch.
                float mask = kind > 1.5 ? _Heat : kind > 0.5 ? saturate(i.heat.x * 3.0) * lerp(0.55, 1.0, saturate(i.heat.x)) * _Heat : 0.0;
                float3 albedo = lerp(_Clay.rgb, hot, mask);
                float3 color = albedo * (light + ambient) + rim * lerp(float3(0.75, 0.82, 1.0), hot, mask) + spec;
                color += hot * mask * (0.1 + 0.32 * _Pulse);

                float grey = dot(color, float3(0.3, 0.59, 0.11));
                color = lerp(color, grey * 0.32 + 0.03, _Dim);
                return fixed4(color, 1.0);
            }
            ENDCG
        }
    }
}
