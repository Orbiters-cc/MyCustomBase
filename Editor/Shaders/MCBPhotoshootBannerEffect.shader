Shader "Hidden/MCB/PhotoshootBannerEffect"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _OverlayColor ("Overlay Color", Color) = (0.1882353, 0.1882353, 0.1882353, 1)
        _MaxBlurTexels ("Max Blur Texels", Float) = 48
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _OverlayColor;
            float _MaxBlurTexels;

            void AddBlurSample(inout fixed4 color, inout float weightSum, float2 uv, float2 offset, float weight)
            {
                color += tex2D(_MainTex, saturate(uv + offset)) * weight;
                weightSum += weight;
            }

            fixed4 SampleProgressiveBlur(float2 uv, float radius)
            {
                if (radius <= 0.01)
                {
                    return tex2D(_MainTex, uv);
                }

                float2 texel = _MainTex_TexelSize.xy * radius;
                float2 inner = texel * 0.35;
                float2 middle = texel * 0.85;
                float2 outer = texel * 1.55;
                fixed4 color = 0;
                float weightSum = 0;

                AddBlurSample(color, weightSum, uv, float2(inner.x, 0), 0.075);
                AddBlurSample(color, weightSum, uv, float2(-inner.x, 0), 0.075);
                AddBlurSample(color, weightSum, uv, float2(0, inner.y), 0.075);
                AddBlurSample(color, weightSum, uv, float2(0, -inner.y), 0.075);
                AddBlurSample(color, weightSum, uv, inner, 0.065);
                AddBlurSample(color, weightSum, uv, -inner, 0.065);
                AddBlurSample(color, weightSum, uv, float2(inner.x, -inner.y), 0.065);
                AddBlurSample(color, weightSum, uv, float2(-inner.x, inner.y), 0.065);

                AddBlurSample(color, weightSum, uv, float2(middle.x, 0), 0.055);
                AddBlurSample(color, weightSum, uv, float2(-middle.x, 0), 0.055);
                AddBlurSample(color, weightSum, uv, float2(0, middle.y), 0.055);
                AddBlurSample(color, weightSum, uv, float2(0, -middle.y), 0.055);
                AddBlurSample(color, weightSum, uv, middle, 0.048);
                AddBlurSample(color, weightSum, uv, -middle, 0.048);
                AddBlurSample(color, weightSum, uv, float2(middle.x, -middle.y), 0.048);
                AddBlurSample(color, weightSum, uv, float2(-middle.x, middle.y), 0.048);

                AddBlurSample(color, weightSum, uv, float2(outer.x, 0), 0.038);
                AddBlurSample(color, weightSum, uv, float2(-outer.x, 0), 0.038);
                AddBlurSample(color, weightSum, uv, float2(0, outer.y), 0.038);
                AddBlurSample(color, weightSum, uv, float2(0, -outer.y), 0.038);
                AddBlurSample(color, weightSum, uv, outer, 0.032);
                AddBlurSample(color, weightSum, uv, -outer, 0.032);
                AddBlurSample(color, weightSum, uv, float2(outer.x, -outer.y), 0.032);
                AddBlurSample(color, weightSum, uv, float2(-outer.x, outer.y), 0.032);

                return color / max(weightSum, 0.0001);
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 source = tex2D(_MainTex, i.uv);
                float lowerThirdTop = 1.0 / 3.0;
                float rawProgress = saturate((lowerThirdTop - i.uv.y) / lowerThirdTop);
                float progress = smoothstep(0.0, 1.0, rawProgress);

                fixed4 blurred = SampleProgressiveBlur(i.uv, _MaxBlurTexels * progress);
                fixed4 color = lerp(source, blurred, smoothstep(0.0, 0.04, rawProgress));
                color.rgb = lerp(color.rgb, _OverlayColor.rgb, progress * _OverlayColor.a);
                color.a = source.a;
                return color;
            }
            ENDCG
        }
    }

    Fallback Off
}
