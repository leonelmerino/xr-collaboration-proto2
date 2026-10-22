// Botón de turno «orb» (2026-10-22): una esfera de luz hecha de puntos, el mismo lenguaje del campo («field»).
//  - Quieta: ~1000 puntos en una cáscara (reparto de Fibonacci) y dos cáscaras interiores más tenues que giran en
//    sentidos opuestos; respira, y los puntos de la cara de atrás son más tenues (se lee el volumen).
//  - Índice acercándose: los puntos que miran a la punta del dedo se estiran hacia ella (la esfera «lo busca»).
//  - Índice adentro: alrededor de la punta los puntos se abren (un hueco blanco) y se encienden.
//  - Mantener: la luz se llena desde donde entró el dedo hasta el otro lado (el progreso).
//  - Cambio de turno: una onda recorre la esfera desde ese punto.
//  - _Dim (recién movida, hasta que la mano salga): tenue y desaturada.
// Una malla fija de quads (uv1 = dirección unitaria + azar; dirección 0 = brillo del núcleo); un draw call.
// Espacio del objeto: el centro de la esfera es el origen. Alpha premultiplicado.
Shader "XRCollab/PadOrb"
{
    Properties
    {
        _Color ("Color", Color) = (0.11, 0.69, 0.48, 1)
        _Intensity ("Intensity", Float) = 1.4
        _T ("Time (s)", Float) = 0
        _Near ("Hand nearness 0..1", Float) = 0
        _Hold ("Hold progress 0..1", Float) = 0
        _Hand ("Fingertip (object space) + strength 0..1", Vector) = (0, 0, -1, 0)
        _HoldDir ("Entry direction (object space)", Vector) = (0, 0, -1, 0)
        _Flash ("Toggle shockwave 1..0", Float) = 0
        _Dim ("Disabled 0..1", Float) = 0
        _R ("Radius (m)", Float) = 0.035
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
            float _Intensity, _T, _Near, _Hold, _Flash, _Dim, _R;
            float4 _Hand, _HoldDir;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 corner : TEXCOORD0;   // -1..1 esquina del quad
                float4 seed : TEXCOORD1;     // xyz = dirección unitaria (0 = núcleo), w = azar
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 corner : TEXCOORD0;   // z = suavidad (0 = punto nítido, 1 = brillo difuso)
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float3 RotY(float3 p, float a)
            {
                float s = sin(a), c = cos(a);
                return float3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float rnd = v.seed.w;
                float w = saturate(_Hand.w);
                float3 col = lerp(saturate(_Color.rgb * 1.25), dot(_Color.rgb, float3(0.3, 0.59, 0.11)).xxx * 0.8, _Dim * 0.7);
                float dimA = lerp(1, 0.22, _Dim);

                if (dot(v.seed.xyz, v.seed.xyz) < 0.25)
                {
                    // Núcleo: brillo difuso que late suave y crece al mantener.
                    float pulse = 0.85 + 0.15 * sin(_T * 1.6 + rnd * 6.28);
                    float size = _R * (0.95 + 0.25 * _Hold + 0.15 * _Flash) * (0.8 + 0.4 * rnd);
                    float3 vc = UnityObjectToViewPos(float4(0, 0, 0, 1));
                    vc.xy += v.corner * size;
                    o.pos = mul(UNITY_MATRIX_P, float4(vc, 1));
                    o.corner = float3(v.corner, 1);
                    o.color = fixed4(lerp(col, 1, 0.25 + 0.2 * _Hold), saturate((0.20 + 0.12 * _Near + 0.25 * _Hold + 0.3 * _Flash) * pulse) * dimA);
                    return o;
                }

                // Cáscara: 70 % afuera, el resto en dos cáscaras interiores que giran al revés.
                float shell = rnd < 0.70 ? 1.0 : (rnd < 0.88 ? 0.74 : 0.46);
                float spin = rnd < 0.70 ? 0.22 : (rnd < 0.88 ? -0.35 : 0.5);
                float3 d = RotY(normalize(v.seed.xyz), _T * spin);
                float breathe = 1 + 0.028 * sin(_T * 1.3 + d.y * 4.0 + rnd * 2.0);
                float rad = _R * shell * breathe;

                // Dedo: dirección y distancia de la punta respecto del centro.
                float3 tip = _Hand.xyz;
                float tl = max(length(tip), 1e-4);
                float3 td = tip / tl;
                float facing = dot(d, td);
                float outside = saturate((tl - _R) / 0.06);   // 1 = la punta está a ≥ 6 cm de la superficie
                float inside = saturate((_R - tl) / 0.012 + 0.5) * w;
                // Afuera: los puntos de la cara hacia el dedo se estiran hacia él (más cuanto más cerca).
                float reach = w * smoothstep(0.55, 1.0, facing) * shell * shell;
                rad += reach * min(tl - _R, 0.035) * (1 - outside * 0.6) * 0.55;
                float3 p = d * rad;
                // Adentro: los puntos cerca de la punta se apartan (un hueco alrededor del dedo).
                float3 away = p - tip;
                float hole = exp(-dot(away, away) / (2 * 0.011 * 0.011)) * inside;
                p += normalize(away + 1e-5) * hole * 0.012;

                // Mantener: la luz se llena desde la entrada del dedo hasta el otro lado.
                float3 hd = normalize(_HoldDir.xyz + float3(0, 0, 1e-4));
                float ang = acos(clamp(dot(d, hd), -1, 1)) / 3.14159;   // 0 entrada .. 1 lado opuesto
                float front = _Hold * 1.06;
                float fill = _Hold > 0.001 ? smoothstep(front, front - 0.09, ang) : 0;
                // Onda del cambio de turno, desde la entrada.
                float shock = _Flash * exp(-pow(ang - (1 - _Flash) * 1.1, 2) / (2 * 0.05 * 0.05));
                p += d * shock * 0.006;

                // Volumen: la cara de atrás más tenue.
                float3 viewP = UnityObjectToViewPos(float4(p, 1));
                float3 viewC = UnityObjectToViewPos(float4(0, 0, 0, 1));
                float front01 = saturate(0.5 - 0.5 * dot(normalize(viewP - viewC), normalize(viewC)));   // 1 = mira al ojo

                // Borde: los puntos de la silueta brillan más y más blancos (la esfera se lee nítida sobre cualquier fondo).
                float rim = (shell < 1 ? 0 : 1) * pow(saturate(1 - abs(2 * front01 - 1)), 2);
                // Volumen: luz desde arriba y un poco de frente (el botón mira a los ojos: -Z = hacia el participante).
                // Arriba más claro, abajo más oscuro, y un brillo arriba-adelante: se lee como esfera, no como círculo.
                float3 L = normalize(float3(0, 0.85, -0.45));
                float lit = shell < 1 ? 1 : 0.25 + 0.75 * saturate(dot(d, L));
                float spot = shell < 1 ? 0 : pow(saturate(dot(d, normalize(L + float3(0, 0, -1)))), 28);
                float heat = saturate(reach * (1 - outside) * 0.8 + hole * 1.3 + fill * 0.7 + shock * 0.9 + spot * 0.7);
                float twinkle = 0.82 + 0.18 * sin(_T * (2.5 + 5 * rnd) + rnd * 40);
                float a = twinkle * lerp(0.2, 1.0, front01) * lit * (shell < 1 ? 0.5 : 1.0) *
                          saturate(0.5 + 0.25 * _Near + heat * 0.9 + fill * 0.3 + rim * 0.35);
                float size = lerp(0.0019, 0.0046, heat) * (0.85 + 0.3 * rnd) * (shell < 1 ? 0.8 : 1.0) * (1 + 0.25 * rim);
                float3 vc = viewP;
                vc.xy += v.corner * size;
                o.pos = mul(UNITY_MATRIX_P, float4(vc, 1));
                o.corner = float3(v.corner, 0);
                o.color = fixed4(lerp(col, float3(1, 1, 1), saturate(heat * heat * 0.9 + rim * 0.35 + 0.12)), a * dimA);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float q = saturate(1 - dot(i.corner.xy, i.corner.xy));
                float core = pow(q, 5);
                float dotA = q * q * 0.55 + core * 0.9;
                float softA = q * q * q;
                float a = saturate(lerp(dotA, softA, i.corner.z) * i.color.a);
                float3 rgb = lerp(i.color.rgb, 1, core * 0.25 * (1 - i.corner.z)) * a * _Intensity;
                return fixed4(rgb, a * 0.9);
            }
            ENDCG
        }
    }
    Fallback Off
}
