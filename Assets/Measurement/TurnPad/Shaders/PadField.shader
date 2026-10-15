// Botón de turno «field»: un campo de puntos sobre la mesa (patrón de girasol, ~1100 puntos) con forma de gaussiana 3D.
//  - Quieto: respira (una gaussiana baja en el centro + ondas suaves que salen del centro).
//  - Dedo acercándose: la gaussiana se mueve bajo la punta del dedo y su cima sube hacia él; se angosta, y los puntos
//    de la cima crecen, se aclaran y se vuelven blancos.
//  - Mantener: el campo se «llena» de luz del centro hacia afuera (el progreso).
//  - Cambio de turno: una onda de choque recorre el campo.
// Todo se calcula aquí (una malla fija de quads con su posición en uv1; un draw call). El CPU solo pasa _T, _Color,
// _Near, _Hold, _Hand y _Flash. Alpha premultiplicado: los puntos se ven también sobre una mesa clara.
// Espacio del objeto = el del botón: -Z es «arriba» (hacia el participante), el plano z = 0 es la mesa.
Shader "XRCollab/PadField"
{
    Properties
    {
        _Color ("Color", Color) = (0.11, 0.69, 0.48, 1)
        _Intensity ("Intensity", Float) = 1.4
        _T ("Time (s)", Float) = 0
        _Near ("Hand nearness 0..1", Float) = 0
        _Hold ("Hold progress 0..1", Float) = 0
        _Hand ("Fingertip (object space) + strength 0..1", Vector) = (0, 0, 0, 0)
        _Flash ("Toggle shockwave 1..0", Float) = 0
        _R ("Field radius (m)", Float) = 0.075
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+12" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One OneMinusSrcAlpha
        ColorMask RGBA
        ZWrite Off
        ZTest LEqual
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Intensity, _T, _Near, _Hold, _Flash, _R;
            float4 _Hand;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 corner : TEXCOORD0;   // -1..1 esquina del quad
                float4 seed : TEXCOORD1;     // xy = posición en la mesa (m), z = índice 0..1, w = azar
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 corner : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float2 xy = v.seed.xy;
                float rnd = v.seed.w;
                float rr = length(xy);
                float r = rr / _R;                       // 0 centro .. 1 borde
                float w = saturate(_Hand.w);

                // Quieto: gaussiana baja que respira + ondas que salen del centro.
                float breathe = 0.6 + 0.4 * sin(_T * 1.25);
                float idle = 0.007 * breathe * exp(-r * r / 0.10) + 0.0011 * sin(r * 17.0 - _T * 2.1) * (1 - r);

                // Dedo: la cima de la gaussiana bajo la punta, subiendo hacia ella.
                float2 c = _Hand.xy;
                float cl = length(c);
                if (cl > _R * 0.9) c *= _R * 0.9 / cl;
                float tipH = max(-_Hand.z, 0);
                float sigma = lerp(0.042, 0.021, w);
                float peak = w * clamp(tipH - 0.005, 0.004, 0.11);
                float2 dc = xy - c;
                float g = exp(-dot(dc, dc) / (2 * sigma * sigma));
                float h = lerp(idle, 0, w * 0.75) + peak * g;

                // Onda de choque al cambiar de turno.
                float rs = (1 - _Flash) * _R * 1.35;
                float shock = _Flash * exp(-pow(rr - rs, 2) / (2 * 0.005 * 0.005));
                h += 0.010 * shock;

                // Mantener: el campo se llena de luz desde el centro.
                float front = _Hold * 1.04;
                float fill = _Hold > 0.001 ? smoothstep(front, front - 0.07, r) : 0;

                float3 p = float3(xy, -h);
                float hn = saturate(h / 0.03);           // altura normalizada (brillo de la cima)
                float heat = saturate(hn * 1.2 + g * w * 0.6 + fill * 0.55 + shock * 0.8);

                float twinkle = 0.85 + 0.15 * sin(_T * (3 + 5 * rnd) + rnd * 40);
                float edge = smoothstep(1.0, 0.72, r);
                float a = edge * twinkle * saturate(0.38 + 0.25 * _Near + heat * 0.9 + fill * 0.35);

                float size = lerp(0.0017, 0.0046, heat) * (0.85 + 0.3 * rnd);
                float3 vc = UnityObjectToViewPos(float4(p, 1));
                vc.xy += v.corner * size;
                o.pos = mul(UNITY_MATRIX_P, float4(vc, 1));
                o.corner = v.corner;
                o.color = fixed4(lerp(saturate(_Color.rgb * 1.25), float3(1, 1, 1), saturate(heat * heat * 0.9)), a);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float q = saturate(1 - dot(i.corner, i.corner));
                float core = pow(q, 5);
                float a = saturate((q * q * 0.55 + core * 0.9) * i.color.a);
                // Premultiplicado: el núcleo es más claro que el halo.
                float3 rgb = lerp(i.color.rgb, 1, core * 0.25) * a * _Intensity;
                return fixed4(rgb, a * 0.9);
            }
            ENDCG
        }
    }
    Fallback Off
}
