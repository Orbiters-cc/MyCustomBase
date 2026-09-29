// MCB version comparison stage. _Mode 0: the backdrop, a soft spotlight behind the avatar drawn over the whole view.
// _Mode 1: the floor, a contact shadow with a faint turntable ring under the avatar.
Shader "Hidden/MCB/VersionCompareStage"
{
    Properties
    {
        _Mode ("Mode", Float) = 0
        _Top ("Top", Color) = (0.2, 0.21, 0.24, 1)
        _Bottom ("Bottom", Color) = (0.07, 0.075, 0.09, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _Mode;
            fixed4 _Top, _Bottom;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                if (_Mode < 0.5)
                {
                    // Covers the view at the far plane, whatever the camera.
                    o.pos = float4(v.uv * 2.0 - 1.0, 0.0, 1.0);
                    #if UNITY_REVERSED_Z
                    o.pos.z = 0.000001;
                    #else
                    o.pos.z = 0.999999;
                    #endif
                    // The same way up as the camera's projection, which flips when drawing into a texture.
                    o.pos.y *= _ProjectionParams.x;
                }
                else
                {
                    o.pos = UnityObjectToClipPos(v.vertex);
                }
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_Mode < 0.5)
                {
                    float2 p = i.uv - float2(0.5, 0.58);
                    float glow = saturate(1.0 - length(p * float2(1.1, 1.35)) * 1.25);
                    float3 color = lerp(_Bottom.rgb, _Top.rgb, glow * glow * (3.0 - 2.0 * glow));
                    color *= lerp(0.82, 1.0, saturate(1.0 - length(i.uv - 0.5) * 1.3));
                    return fixed4(color, 1.0);
                }

                float r = length(i.uv - 0.5) * 2.0;
                float shadow = pow(saturate(1.0 - r / 0.42), 1.6) * 0.5;
                float ring = saturate(1.0 - abs(r - 0.78) / 0.012) * 0.07 + saturate(1.0 - abs(r - 0.5) / 0.008) * 0.04;
                float fade = saturate(1.0 - r);
                float3 color = lerp(float3(0, 0, 0), float3(0.7, 0.75, 0.85), ring / max(shadow + ring, 0.0001));
                return fixed4(color, (shadow + ring) * fade);
            }
            ENDCG
        }
    }
}
