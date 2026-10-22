// Índice iluminado (2026-10-22): al acercar la mano a la esfera del turno, el índice se enciende para que quede claro
// con qué dedo se usa. Mismo lenguaje de puntos de luz que el botón:
//  - un halo suave a lo largo del dedo (una funda de luz);
//  - corrientes de puntos que suben en espiral por el índice (del nudillo a la punta), más claras hacia la punta;
//  - en la punta, un brillo que late y un aro nítido (el puntero);
//  - un hilo de puntos que va de la punta a la esfera (la «atracción»), solo con el índice estirado.
// El CPU pasa las articulaciones en coordenadas del mundo: _P0 nudillo, _P1 media, _P2 distal, _P3 punta, _P4 punto de
// la esfera más cercano. _Glow 0..1 (cercanía), _Tether 0..1, _Hold 0..1. Una malla fija; un draw call por mano.
Shader "XRCollab/FingerGlow"
{
    Properties
    {
        _Color ("Color", Color) = (0.11, 0.69, 0.48, 1)
        _Intensity ("Intensity", Float) = 1.5
        _T ("Time (s)", Float) = 0
        _Glow ("Glow 0..1", Float) = 0
        _Tether ("Tether 0..1", Float) = 0
        _Hold ("Hold progress 0..1", Float) = 0
        _P0 ("Knuckle (world)", Vector) = (0, 0, 0, 0)
        _P1 ("Intermediate (world)", Vector) = (0, 0, 0, 0)
        _P2 ("Distal (world)", Vector) = (0, 0, 0, 0)
        _P3 ("Tip (world)", Vector) = (0, 0, 0, 0)
        _P4 ("Orb surface (world)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+13" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
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
            float _Intensity, _T, _Glow, _Tether, _Hold;
            float4 _P0, _P1, _P2, _P3, _P4;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 corner : TEXCOORD0;   // -1..1 esquina del quad
                float4 seed : TEXCOORD1;     // x = posición 0..1, y = ángulo 0..1, z = azar, w = tipo (0 dedo, 1 punta, 2 hilo, 3 funda, 4 aro)
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 corner : TEXCOORD0;   // z = forma (0 punto nítido, 1 brillo difuso, 2 aro)
                fixed4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Punto a la fracción u (0..1) del largo de la línea nudillo → punta; devuelve también la dirección.
            float3 AlongFinger(float u, out float3 axis)
            {
                float3 a = _P1.xyz - _P0.xyz, b = _P2.xyz - _P1.xyz, c = _P3.xyz - _P2.xyz;
                float la = length(a), lb = length(b), lc = length(c);
                float L = max(la + lb + lc, 1e-4);
                float x = u * L;
                if (x < la) { axis = a / max(la, 1e-5); return _P0.xyz + axis * x; }
                x -= la;
                if (x < lb) { axis = b / max(lb, 1e-5); return _P1.xyz + axis * x; }
                x -= lb;
                axis = c / max(lc, 1e-5);
                return _P2.xyz + axis * min(x, lc);
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float rnd = v.seed.z;
                float kind = v.seed.w;
                float glow = saturate(_Glow);
                float3 world;
                float size, a, white, soft = 0;

                if (kind < 0.5)
                {
                    // Corrientes en espiral por el dedo, hacia la punta.
                    float s = frac(v.seed.x + _T * (0.45 + 0.35 * rnd) * (1 + _Hold));
                    float3 axis;
                    float3 c = AlongFinger(s, axis);
                    float3 toCam = normalize(_WorldSpaceCameraPos - c);
                    float3 n1 = normalize(cross(axis, toCam) + float3(1e-5, 0, 0));
                    float3 n2 = cross(axis, n1);
                    float th = v.seed.y * 6.2832 + s * 5.0 + _T * 1.5;
                    float rr = lerp(0.0095, 0.0060, s) * (0.9 + 0.2 * rnd);
                    world = c + (cos(th) * n1 + sin(th) * n2) * rr;
                    size = lerp(0.0014, 0.0028, s) * (0.8 + 0.4 * rnd);
                    a = glow * smoothstep(0.0, 0.12, s) * lerp(0.45, 1.0, s) * (0.8 + 0.2 * sin(_T * 6 + rnd * 30));
                    white = 0.2 + s * s * 0.6;
                }
                else if (kind < 1.5)
                {
                    // Brillo de la punta (varias capas difusas que laten).
                    world = _P3.xyz;
                    float pulse = 0.8 + 0.2 * sin(_T * 4.0 + rnd * 6.28);
                    size = lerp(0.009, 0.020, rnd) * pulse * (1 + 0.35 * _Hold);
                    a = glow * lerp(0.6, 0.18, rnd) * (1 + 0.5 * _Hold);
                    white = 0.6;
                    soft = 1;
                }
                else if (kind > 2.5)
                {
                    world = _P3.xyz;
                    if (kind < 3.5)
                    {
                        // Funda: brillos difusos fijos a lo largo del dedo.
                        float3 axis;
                        world = AlongFinger(v.seed.x, axis);
                        size = lerp(0.011, 0.008, v.seed.x);
                        a = glow * 0.22 * smoothstep(0.0, 0.25, v.seed.x);
                        white = 0.15;
                        soft = 1;
                    }
                    else
                    {
                        // Aro nítido en la punta: se cierra al mantener.
                        size = lerp(0.0085, 0.0055, saturate(_Hold)) * (1 + 0.06 * sin(_T * 5.0));
                        a = glow * 0.9;
                        white = 0.7;
                        soft = 2;
                    }
                }
                else
                {
                    // Hilo de la punta a la esfera: puntos que viajan hacia el botón, con una curva suave.
                    float s = frac(v.seed.x + _T * 0.9);
                    float3 p0 = _P3.xyz, p1 = _P4.xyz;
                    float3 mid = (p0 + p1) * 0.5;
                    float3 dir = p1 - p0;
                    float len = length(dir);
                    float3 side = normalize(cross(dir + float3(0, 1e-5, 0), float3(0, 1, 0)) + float3(1e-5, 0, 0));
                    float wob = sin(_T * 2.0 + v.seed.y * 6.2832) * 0.25 * len;
                    float3 ctrl = mid + float3(0, 0.12 * len, 0) + side * wob * 0.3;
                    world = lerp(lerp(p0, ctrl, s), lerp(ctrl, p1, s), s);
                    size = 0.0016 * (0.8 + 0.4 * rnd);
                    a = _Tether * sin(3.14159 * s);
                    white = 0.35;
                }

                float3 vc = UnityWorldToViewPos(world);
                vc.xy += v.corner * size;
                o.pos = mul(UNITY_MATRIX_P, float4(vc, 1));
                o.corner = float3(v.corner, soft);
                o.color = fixed4(lerp(saturate(_Color.rgb * 1.3), float3(1, 1, 1), saturate(white)), saturate(a));
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float q = saturate(1 - dot(i.corner.xy, i.corner.xy));
                float core = pow(q, 5);
                float shape = i.corner.z < 0.5 ? q * q * 0.55 + core * 0.9 : i.corner.z < 1.5 ? q * q * q
                            : exp(-pow((length(i.corner.xy) - 0.78) / 0.09, 2));
                float a = saturate(shape * i.color.a);
                float3 rgb = lerp(i.color.rgb, 1, core * 0.3 * (i.corner.z < 0.5 ? 1 : 0)) * a * _Intensity;
                return fixed4(rgb, a * 0.9);
            }
            ENDCG
        }
    }
    Fallback Off
}
