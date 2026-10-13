// Recorte suave de lo virtual con la mano real (passthrough). Se dibuja después de todo lo virtual y multiplica el
// color Y el alpha del buffer por (1 - a): donde está la mano, lo virtual desaparece y se ve el passthrough.
//  - a = 1 en el interior de la mano y baja a 0 en el borde de la silueta (N·V, «fresnel»): borde difuminado, así un
//    error de 2-3 mm entre la mano real y la estimada no se ve como un corte duro.
//  - _Inflate empuja la malla por sus normales (m): cubre el error de forma/latencia sin cambiar la malla.
//  - ZTest LEqual contra la profundidad de lo virtual: si algo virtual está más cerca que la mano, no se corta.
Shader "XRCollab/HandCut"
{
    Properties
    {
        _Feather ("Ancho del borde suave (N·V)", Range(0.02, 1)) = 0.35
        _Inflate ("Inflado (m)", Float) = 0.004
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+200" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend Zero OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Back

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _Feather;
            float _Inflate;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float3 toEye : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 n = UnityObjectToWorldNormal(v.normal);
                float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz + n * _Inflate;
                o.pos = mul(UNITY_MATRIX_VP, float4(wp, 1));
                o.normal = n;
                o.toEye = _WorldSpaceCameraPos.xyz - wp;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float ndv = saturate(dot(normalize(i.normal), normalize(i.toEye)));
                return fixed4(0, 0, 0, smoothstep(0, _Feather, ndv));
            }
            ENDCG
        }
    }
    Fallback Off
}
