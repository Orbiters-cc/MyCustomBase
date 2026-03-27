Shader "MCB/ArmatureDebug"
{
    Properties
    {
        _MainColor ("Main Color", Color) = (0.5, 0.8, 1.0, 0.8)
        [Toggle] _UseLighting ("Use Lighting", Float) = 1
        [Toggle] _UseWireframe ("Use Wireframe", Float) = 0
        _WireframeColor ("Wireframe Color", Color) = (1, 1, 1, 1)
        _WireframeWidth ("Wireframe Width", Range(0.1, 10.0)) = 1.0
        [Enum(Off,0,Front,1,Back,2)] _CullMode ("Face Culling", Float) = 0
    }
    
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" }
        
        Pass
        {
            Name "Main"
            Cull [_CullMode]
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma shader_feature _USELIGHTING_ON
            #pragma shader_feature _USEWIREFRAME_ON
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            
            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };
            
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 barycentric : TEXCOORD0;
                #ifdef _USELIGHTING_ON
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                #endif
            };
            
            float4 _MainColor;
            float4 _WireframeColor;
            float _WireframeWidth;
            
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.barycentric = float3(0, 0, 0);
                
                #ifdef _USELIGHTING_ON
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                #endif
                
                return o;
            }
            
            [maxvertexcount(3)]
            void geom(triangle v2f input[3], inout TriangleStream<v2f> triStream)
            {
                v2f output;
                
                // First vertex
                output = input[0];
                output.barycentric = float3(1, 0, 0);
                triStream.Append(output);
                
                // Second vertex
                output = input[1];
                output.barycentric = float3(0, 1, 0);
                triStream.Append(output);
                
                // Third vertex
                output = input[2];
                output.barycentric = float3(0, 0, 1);
                triStream.Append(output);
                
                triStream.RestartStrip();
            }
            
            fixed4 frag(v2f i) : SV_Target
            {
                float4 baseColor;
                
                #ifdef _USELIGHTING_ON
                float3 normal = normalize(i.worldNormal);
                float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                float NdotL = max(0, dot(normal, lightDir));
                
                // Simple lighting: ambient + diffuse
                float3 ambient = ShadeSH9(float4(normal, 1.0));
                float3 diffuse = _LightColor0.rgb * NdotL;
                
                float3 finalColor = _MainColor.rgb * (ambient + diffuse);
                baseColor = float4(finalColor, _MainColor.a);
                #else
                // Plain color without lighting
                baseColor = _MainColor;
                #endif
                
                #ifdef _USEWIREFRAME_ON
                // Calculate wireframe
                float3 barys = i.barycentric;
                float3 deltas = fwidth(barys);
                float3 smoothing = deltas * _WireframeWidth;
                float3 thickness = smoothstep(float3(0, 0, 0), smoothing, barys);
                float wireframe = 1.0 - min(thickness.x, min(thickness.y, thickness.z));
                
                // Mix wireframe with base color
                return lerp(baseColor, _WireframeColor, wireframe);
                #else
                return baseColor;
                #endif
            }
            ENDCG
        }
    }
    
    Fallback "Transparent/Diffuse"
}