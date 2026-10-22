// Cáscara de vidrio de la esfera del turno (2026-10-22): sin ella, la esfera de puntos se leía como un círculo plano.
// Una esfera de malla, transparente, con lo que el ojo usa para leer volumen:
//  - borde brillante (fresnel): el contorno se ve nítido y redondo desde cualquier lado;
//  - sombreado con luz desde arriba y un poco de frente (más clara arriba, más oscura abajo);
//  - un brillo especular chico arriba (el «reflejo» de una esfera).
// Color del turno, _Dim (recién movida) la apaga, _Hold y _Flash la encienden. Alpha premultiplicado.
Shader "XRCollab/PadOrbShell"
{
    Properties
    {
        _Color ("Color", Color) = (0.11, 0.69, 0.48, 1)
        _Near ("Hand nearness 0..1", Float) = 0
        _Hold ("Hold progress 0..1", Float) = 0
        _Flash ("Toggle 1..0", Float) = 0
        _Dim ("Disabled 0..1", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+11" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Back
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Near, _Hold, _Flash, _Dim;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 n : TEXCOORD0;
                float3 v : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata i)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(i.vertex);
                o.n = UnityObjectToWorldNormal(i.normal);
                o.v = WorldSpaceViewDir(i.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.n), v = normalize(i.v);
                float3 l = normalize(float3(0, 1, 0) * 0.85 + v * 0.45);   // luz de arriba y un poco de frente
                float ndv = saturate(dot(n, v));
                float rim = pow(1 - ndv, 2.6);
                float lambert = saturate(dot(n, l));
                float spec = pow(saturate(dot(n, normalize(l + v))), 60);
                float energy = 1 + 0.6 * _Near + 1.2 * _Hold + 1.5 * _Flash;
                float a = saturate((rim * 0.62 + lambert * lambert * 0.07 + spec * 0.55) * energy) * lerp(1, 0.25, _Dim);
                float3 c = lerp(saturate(_Color.rgb * 1.3), float3(1, 1, 1), saturate(rim * 0.35 + spec * 0.9 + _Flash * 0.4));
                c = lerp(c, dot(c, float3(0.3, 0.59, 0.11)).xxx, _Dim * 0.7);
                return fixed4(c * a, a);
            }
            ENDCG
        }
    }
    Fallback Off
}
